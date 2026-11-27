using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Playnite.Core;
using Playnite.Core.Models;
using Playnite.Core.Plugins;
using Playnite.Platform.Linux;
using Xunit;

namespace Playnite.Core.Tests.Plugins;

public sealed class PluginHostTests
{
    [Fact]
    public async Task Built_in_plugin_registers_capabilities_commands_settings_and_events()
    {
        var token = TestContext.Current.CancellationToken;
        var fixedTime = new FixedTimeProvider(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        var settingsStore = new InMemoryPluginSettingsStore();
        using var host = new PluginHost(PluginSurface.Core | PluginSurface.Desktop, fixedTime, settingsStore);

        var plugin = new FakeBuiltInPlugin();
        Assert.True(host.RegisterBuiltIn(plugin));

        Assert.True(host.TryGetSource(FakeBuiltInPlugin.SourceGuid, out var source));
        Assert.Equal("Fake Store", source.DisplayName);
        Assert.Single(host.GetSources());

        Assert.True(host.TryGetLaunchProvider(FakeBuiltInPlugin.SourceGuid, out var launcher));
        var game = new Game(Guid.NewGuid(), "fake-1", "Fake Quest") { PluginId = FakeBuiltInPlugin.SourceGuid };
        Assert.Equal("/usr/bin/fake-runner", launcher.ResolveLaunch(game).Executable);

        var toolbarCommands = host.GetCommands(PluginCommandPlacement.LibraryToolbar);
        var command = Assert.Single(toolbarCommands);
        Assert.Equal("fake.sync", command.Id);
        var result = await host.ExecuteCommandAsync("fake.sync", new PluginCommandContext(game.Id), token);
        Assert.True(result.Succeeded);
        Assert.Equal("Synced", result.Message);

        var field = Assert.Single(host.GetSettingsSchema(FakeBuiltInPlugin.Id));
        Assert.Equal("apiKey", field.Key);
        Assert.Equal("configured", settingsStore.Get(FakeBuiltInPlugin.Id, "initialized"));

        await host.PublishEventAsync(new PluginEvent(PluginEvents.GameStarting, fixedTime.GetUtcNow(), game.Id), token);
        Assert.Equal([PluginEvents.GameStarting], plugin.ReceivedEvents);

        // Unsubscribed event takes synchronous fast-path without invoking the plugin.
        var unsubscribedTask = host.PublishEventAsync(new PluginEvent("unsubscribed.event", fixedTime.GetUtcNow()), token);
        Assert.True(unsubscribedTask.IsCompletedSuccessfully);
        await unsubscribedTask;
    }

    [Fact]
    public void Lazy_plugin_is_not_materialized_until_first_capability_lookup()
    {
        var lazySourceId = Guid.Parse("a1111111-1111-1111-1111-111111111111");
        var descriptor = new PluginDescriptor(
            "test.lazy",
            lazySourceId,
            "Lazy Plugin",
            new Version(1, 0, 0),
            PluginLoadKind.ExternalAssembly,
            PluginSurface.Core,
            PluginCapabilityKind.LibrarySource | PluginCapabilityKind.Launch,
            new PluginEntryPoints(Core: "lazy.dll"));

        using var host = new PluginHost(PluginSurface.Core);
        var factoryCalls = 0;
        host.RegisterLazy(descriptor, () =>
        {
            factoryCalls++;
            return new FakeBuiltInPlugin("test.lazy", lazySourceId, "Lazy Source");
        });

        var recordBefore = Assert.Single(host.GetRecords());
        Assert.Equal(PluginLifecycleState.Registered, recordBefore.State);
        Assert.False(recordBefore.IsMaterialized);
        Assert.Equal(0, factoryCalls);

        Assert.True(host.TryGetSource(lazySourceId, out var source));
        Assert.Equal("Lazy Source", source.DisplayName);
        Assert.Equal(1, factoryCalls);

        Assert.True(host.TryGetLaunchProvider(lazySourceId, out _));
        Assert.Equal(1, factoryCalls);

        var recordAfter = Assert.Single(host.GetRecords());
        Assert.Equal(PluginLifecycleState.Active, recordAfter.State);
        Assert.True(recordAfter.IsMaterialized);
    }

    [Fact]
    public async Task Lazy_commands_settings_and_event_hooks_activate_on_demand()
    {
        var token = TestContext.Current.CancellationToken;
        var descriptor = new PluginDescriptor(
            "test.lazy-ops",
            Guid.Empty,
            "Lazy Ops",
            new Version(1, 0, 0),
            PluginLoadKind.ExternalAssembly,
            PluginSurface.Core,
            PluginCapabilityKind.Commands | PluginCapabilityKind.EventHooks | PluginCapabilityKind.Settings,
            new PluginEntryPoints(Core: "lazy-ops.dll"));

        using var host = new PluginHost(PluginSurface.Core);
        var created = new List<FakeBuiltInPlugin>();
        host.RegisterLazy(descriptor, () =>
        {
            var instance = new FakeBuiltInPlugin("test.lazy-ops", Guid.Parse("a2222222-2222-2222-2222-222222222222"), "Lazy Ops");
            created.Add(instance);
            return instance;
        });

        Assert.Empty(created);
        Assert.Single(host.GetSettingsSchema("test.lazy-ops"));
        Assert.Single(created);

        var commands = host.GetCommands();
        Assert.Single(commands);
        await host.PublishEventAsync(new PluginEvent(PluginEvents.AppStarted, DateTimeOffset.UtcNow), token);
        Assert.Equal([PluginEvents.AppStarted], created[0].ReceivedEvents);
    }

    [Fact]
    public void External_discovery_filters_by_surface_lazy_loads_and_unloads_collectible_context()
    {
        var tempDir = Directory.CreateTempSubdirectory("playnite-plugin-host-");
        try
        {
            var desktopOnlyDir = tempDir.CreateSubdirectory("desktop-only");
            File.WriteAllText(
                Path.Combine(desktopOnlyDir.FullName, "desktop.dll"),
                "stub");
            File.WriteAllText(
                Path.Combine(desktopOnlyDir.FullName, "plugin.json"),
                """
                {
                  "id": "desktop.only",
                  "name": "Desktop Only",
                  "version": "1.0.0",
                  "surfaces": ["desktop"],
                  "capabilities": ["commands"],
                  "entryPoints": { "desktop": "desktop.dll" }
                }
                """);

            var coreDir = tempDir.CreateSubdirectory("external-sample");
            var sourceAssemblyPath = typeof(TestExternalPlugin).Assembly.Location;
            var copiedAssemblyName = "TestExternalSample.dll";
            File.Copy(sourceAssemblyPath, Path.Combine(coreDir.FullName, copiedAssemblyName));
            File.WriteAllText(
                Path.Combine(coreDir.FullName, "plugin.json"),
                $$"""
                {
                  "id": "test.external",
                  "sourceId": "{{TestExternalPlugin.ExternalSourceId:D}}",
                  "name": "External Sample",
                  "version": "1.2.3",
                  "surfaces": ["core"],
                  "capabilities": ["librarySource", "launch"],
                  "entryPoints": {
                    "core": "{{copiedAssemblyName}}",
                    "entryType": "{{typeof(TestExternalPlugin).FullName}}"
                  }
                }
                """);

            using var host = new PluginHost(PluginSurface.Core);
            var weakAlc = RunExternalDiscoveryAndLoad(host, tempDir.FullName);

            host.Dispose();
            for (var i = 0; i < 10 && weakAlc.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            // Even while 'host' local is still in scope, Dispose cleared all strong references so ALC unloads.
            Assert.False(weakAlc.IsAlive);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunExternalDiscoveryAndLoad(PluginHost host, string rootPath)
    {
        var discovery = host.DiscoverExternal(rootPath, new LinuxRegularFileProbe());

        Assert.Empty(discovery.Diagnostics);
        var discovered = Assert.Single(discovery.Discovered);
        Assert.Equal("test.external", discovered.Id);

        var recordBefore = Assert.Single(host.GetRecords());
        Assert.False(recordBefore.IsMaterialized);
        Assert.Equal(PluginLifecycleState.Registered, recordBefore.State);

        Assert.True(host.TryGetSource(TestExternalPlugin.ExternalSourceId, out var source));
        Assert.Equal("External Sample Source", source.DisplayName);

        Assert.True(host.TryGetLaunchProvider(TestExternalPlugin.ExternalSourceId, out var launcher));
        var game = new Game(Guid.NewGuid(), "ext-1", "External Game") { PluginId = TestExternalPlugin.ExternalSourceId };
        Assert.Equal("/usr/bin/external-runner", launcher.ResolveLaunch(game).Executable);

        var alc = AssemblyLoadContext.GetLoadContext(launcher.GetType().Assembly);
        Assert.NotNull(alc);
        Assert.NotSame(AssemblyLoadContext.Default, alc);

        var recordAfter = Assert.Single(host.GetRecords());
        Assert.True(recordAfter.IsMaterialized);
        Assert.Equal(PluginLifecycleState.Active, recordAfter.State);

        return new WeakReference(alc, trackResurrection: true);
    }

    [Fact]
    public void Manifest_parser_validates_v2_and_legacy_yaml_and_rejects_bad_inputs()
    {
        var v2 = PluginManifestParser.ParseV2Manifest(
            "\uFEFF" + """
            {
              "id": "steamgriddb.plugin",
              "name": "SteamGridDB",
              "version": "2.1.0",
              "surfaces": ["core", "desktop"],
              "capabilities": ["metadata", "settings"],
              "entryPoints": { "core": "SteamGridDb.Core.dll", "desktop": "SteamGridDb.Desktop.dll" }
            }
            """,
            "/tmp/plugins/steamgriddb");
        Assert.NotNull(v2.Descriptor);
        Assert.Empty(v2.Diagnostics);
        Assert.Equal(PluginSurface.Core | PluginSurface.Desktop, v2.Descriptor.Surfaces);
        Assert.Equal(PluginCapabilityKind.Metadata | PluginCapabilityKind.Settings, v2.Descriptor.Capabilities);

        var duplicateKeys = PluginManifestParser.ParseV2Manifest(
            """
            {
              "id": "dup.plugin",
              "id": "dup.override",
              "name": "Dup",
              "version": "1.0.0",
              "surfaces": ["core"],
              "entryPoints": { "core": "dup.dll" }
            }
            """,
            "/tmp/plugins/dup");
        Assert.Null(duplicateKeys.Descriptor);
        Assert.Contains(duplicateKeys.Diagnostics, d => d.Message.Contains("duplicate property", StringComparison.Ordinal));

        var missingSourceId = PluginManifestParser.ParseV2Manifest(
            """
            {
              "id": "lib.no-source",
              "name": "Lib No Source",
              "version": "1.0.0",
              "surfaces": ["core"],
              "capabilities": ["librarySource"],
              "entryPoints": { "core": "lib.dll" }
            }
            """,
            "/tmp/plugins/lib");
        Assert.Null(missingSourceId.Descriptor);
        Assert.Contains(missingSourceId.Diagnostics, d => d.Message.Contains("$.sourceId", StringComparison.Ordinal));

        var traversal = PluginManifestParser.ParseV2Manifest(
            """
            {
              "id": "bad.plugin",
              "name": "Bad",
              "version": "1.0.0",
              "surfaces": ["core"],
              "entryPoints": { "core": "../escape.dll" }
            }
            """,
            "/tmp/plugins/bad");
        Assert.Null(traversal.Descriptor);
        Assert.NotEmpty(traversal.Diagnostics);

        var legacy = PluginManifestParser.ParseLegacyExtensionManifest(
            """
            Id: sample.legacy
            Name: Legacy Sample
            Author: Tester
            Version: 1.0.0
            Module: LegacySample.dll
            Type: GameLibrary
            """,
            "/tmp/plugins/legacy");
        Assert.NotNull(legacy.Descriptor);
        Assert.Empty(legacy.Diagnostics);
        Assert.True(legacy.Descriptor.Capabilities.HasFlag(PluginCapabilityKind.LibrarySource));
        Assert.Equal("LegacySample.dll", legacy.Descriptor.EntryPoints.Core);

        var legacyScript = PluginManifestParser.ParseLegacyExtensionManifest(
            """
            Id: sample.script
            Name: Legacy Script
            Author: Tester
            Version: 1.0.0
            Module: script.ps1
            Type: Script
            """,
            "/tmp/plugins/script");
        Assert.Null(legacyScript.Descriptor);
        Assert.Contains(legacyScript.Diagnostics, d => d.Code == "unsupported-extension-type");
    }

    [Fact]
    public void Discovery_rejects_non_regular_manifest_and_intermediate_or_leaf_symlinks()
    {
        var tempDir = Directory.CreateTempSubdirectory("playnite-plugin-sec-");
        try
        {
            var fifoPlugin = tempDir.CreateSubdirectory("fifo-plugin");
            var manifestPath = Path.Combine(fifoPlugin.FullName, "plugin.json");
            File.WriteAllText(
                manifestPath,
                """
                {
                  "id": "fifo.plugin",
                  "name": "Fifo Plugin",
                  "version": "1.0.0",
                  "surfaces": ["core"],
                  "entryPoints": { "core": "fifo.dll" }
                }
                """);

            using var host = new PluginHost(PluginSurface.Core);
            var result = host.DiscoverExternal(tempDir.FullName, new RejectPathProbe(manifestPath));
            Assert.Empty(result.Discovered);
            Assert.Contains(result.Diagnostics, d => d.Code == "unsafe-manifest-file");

            // Leaf symlink rejected
            var symlinkDir = tempDir.CreateSubdirectory("symlink-plugin");
            var outsideDir = tempDir.CreateSubdirectory("outside");
            var realTarget = Path.Combine(outsideDir.FullName, "outside.dll");
            File.WriteAllText(realTarget, "not-an-assembly");
            var linkPath = Path.Combine(symlinkDir.FullName, "linked.dll");
            File.CreateSymbolicLink(linkPath, realTarget);
            File.WriteAllText(
                Path.Combine(symlinkDir.FullName, "plugin.json"),
                """
                {
                  "id": "symlink.plugin",
                  "name": "Symlink Plugin",
                  "version": "1.0.0",
                  "surfaces": ["core"],
                  "entryPoints": { "core": "linked.dll" }
                }
                """);

            // Intermediate directory symlink escape ("sub/outside.dll" where "sub" -> outsideDir) rejected
            var nestedEscapeDir = tempDir.CreateSubdirectory("nested-escape");
            Directory.CreateSymbolicLink(Path.Combine(nestedEscapeDir.FullName, "sub"), outsideDir.FullName);
            File.WriteAllText(
                Path.Combine(nestedEscapeDir.FullName, "plugin.json"),
                """
                {
                  "id": "nested.escape",
                  "name": "Nested Escape",
                  "version": "1.0.0",
                  "surfaces": ["core"],
                  "entryPoints": { "core": "sub/outside.dll" }
                }
                """);

            var probe = new LinuxRegularFileProbe();
            var symlinkResult = host.DiscoverExternal(tempDir.FullName, probe);
            Assert.Contains(symlinkResult.Diagnostics, d => d.PluginId == "symlink.plugin" && d.Code == "missing-entry-point");
            Assert.Contains(symlinkResult.Diagnostics, d => d.PluginId == "nested.escape" && d.Code == "missing-entry-point");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Disable_enable_fault_recovery_and_source_conflict_guards_keep_host_consistent()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = new PluginHost(PluginSurface.Core);
        var healthy = new FakeBuiltInPlugin();
        var faulty = new FlakyPlugin();

        Assert.True(host.RegisterBuiltIn(healthy));
        Assert.True(host.RegisterBuiltIn(faulty));

        // Duplicate SourceId is rejected cleanly.
        var duplicateSource = new FakeBuiltInPlugin("test.duplicate-source", FakeBuiltInPlugin.SourceGuid, "Dup");
        Assert.Throws<InvalidOperationException>(() => host.RegisterBuiltIn(duplicateSource));

        Assert.True(host.Disable(FakeBuiltInPlugin.Id));
        Assert.False(host.TryGetSource(FakeBuiltInPlugin.SourceGuid, out _));
        Assert.False(host.TryGetLaunchProvider(FakeBuiltInPlugin.SourceGuid, out _));
        Assert.DoesNotContain(host.GetCommands(), c => c.Id == "fake.sync");

        Assert.True(host.Enable(FakeBuiltInPlugin.Id));
        Assert.True(host.TryGetSource(FakeBuiltInPlugin.SourceGuid, out _));

        // Faulty plugin fails command & event hook, transitions to Faulted without breaking healthy plugin.
        var failedCommand = await host.ExecuteCommandAsync("flaky.cmd", new PluginCommandContext(null), token);
        Assert.False(failedCommand.Succeeded);
        var flakyRecord = Assert.Single(host.GetRecords(), r => r.Descriptor.Id == FlakyPlugin.PluginId);
        Assert.Equal(PluginLifecycleState.Faulted, flakyRecord.State);

        await host.PublishEventAsync(new PluginEvent(PluginEvents.AppStarted, DateTimeOffset.UtcNow), token);
        Assert.Equal([PluginEvents.AppStarted], healthy.ReceivedEvents);

        // Recovering a faulted plugin re-invokes Setup().
        faulty.ShouldThrowOnCommand = false;
        Assert.True(host.Enable(FlakyPlugin.PluginId));
        var recovered = await host.ExecuteCommandAsync("flaky.cmd", new PluginCommandContext(null), token);
        Assert.True(recovered.Succeeded);
        Assert.Equal(2, faulty.SetupCount);

        host.Dispose();
        Assert.True(healthy.Disposed);
    }

    [Fact]
    public async Task Sync_providers_activate_lazily_publish_library_synced_and_isolate_faults_and_conflicts()
    {
        var token = TestContext.Current.CancellationToken;
        var v2SyncManifest = PluginManifestParser.ParseV2Manifest(
            """
            {
              "id": "sync.sample",
              "sourceId": "a3333333-3333-3333-3333-333333333333",
              "name": "Sync Sample",
              "version": "1.0.0",
              "surfaces": ["core"],
              "capabilities": ["librarySource", "librarySync"],
              "entryPoints": { "core": "sync.dll" }
            }
            """,
            "/tmp/plugins/sync-sample");
        Assert.NotNull(v2SyncManifest.Descriptor);
        Assert.True(v2SyncManifest.Descriptor.Capabilities.HasFlag(PluginCapabilityKind.LibrarySync));

        using var host = new PluginHost(PluginSurface.Core);
        var listener = new FakeBuiltInPlugin("test.listener", Guid.Parse("b3333333-3333-3333-3333-333333333333"), "Listener");
        Assert.True(host.RegisterBuiltIn(listener));

        var lazySyncFactoryCalls = 0;
        var syncDescriptor = new PluginDescriptor(
            "test.lazy-sync",
            Guid.Parse("b4444444-4444-4444-4444-444444444444"),
            "Lazy Sync",
            new Version(1, 0, 0),
            PluginLoadKind.ExternalAssembly,
            PluginSurface.Core,
            PluginCapabilityKind.LibrarySource | PluginCapabilityKind.LibrarySync,
            new PluginEntryPoints(Core: "lazy-sync.dll"));
        host.RegisterLazy(syncDescriptor, () =>
        {
            lazySyncFactoryCalls++;
            return new FakeSyncPlugin("test.lazy-sync", syncDescriptor.SourceId);
        });

        Assert.Equal(0, lazySyncFactoryCalls);
        var syncResult = await host.RunSyncAsync("test.lazy-sync", "/tmp/library.sqlite", token);
        Assert.Equal(1, lazySyncFactoryCalls);
        Assert.True(syncResult.Succeeded);
        Assert.Equal(2, syncResult.Added);
        Assert.Equal([PluginEvents.LibrarySynced], listener.ReceivedEvents);
        var syncedEvent = Assert.Single(listener.ReceivedEventRecords);
        Assert.NotNull(syncedEvent.Payload);
        Assert.Equal("test.lazy-sync", syncedEvent.Payload["pluginId"]);
        Assert.Equal("true", syncedEvent.Payload["succeeded"]);
        Assert.Equal("2", syncedEvent.Payload["added"]);
        Assert.Equal("0", syncedEvent.Payload["updated"]);
        Assert.Equal("0", syncedEvent.Payload["removed"]);
        Assert.Equal("true", syncedEvent.Payload["reconciled"]);
        Assert.Single(host.GetSyncProviders());

        // Non-succeeded sync result does not fault and does not publish LibrarySynced.
        var nonSucceededSync = new FakeSyncPlugin(
            "test.unsucceeded-sync",
            Guid.Parse("b7777777-7777-7777-7777-777777777777"),
            succeeded: false);
        Assert.True(host.RegisterBuiltIn(nonSucceededSync));
        var nonSucceededResult = await host.RunSyncAsync("test.unsucceeded-sync", "/tmp/library.sqlite", token);
        Assert.False(nonSucceededResult.Succeeded);
        Assert.Single(listener.ReceivedEvents);
        Assert.Contains(host.GetRecords(), r => r.Descriptor.Id == "test.unsucceeded-sync" && r.State == PluginLifecycleState.Active);

        // Duplicate sync provider id conflicts cleanly and transitions only the conflicting plugin to Faulted.
        var conflictingSync = new FakeSyncPlugin("test.conflict-sync", Guid.Parse("b5555555-5555-5555-5555-555555555555"), overrideSyncId: "test.lazy-sync");
        Assert.False(host.RegisterBuiltIn(conflictingSync));
        Assert.Contains(host.GetRecords(), r => r.Descriptor.Id == "test.conflict-sync" && r.State == PluginLifecycleState.Faulted);

        // Disabled sync provider returns unavailable result without running.
        Assert.True(host.Disable("test.lazy-sync"));
        var disabledResult = await host.RunSyncAsync("test.lazy-sync", "/tmp/library.sqlite", token);
        Assert.False(disabledResult.Succeeded);
        Assert.Contains("sync-provider-unavailable", disabledResult.Issues);

        // Throwing sync provider faults only that plugin and returns a failed summary.
        var throwingSync = new FakeSyncPlugin("test.throwing-sync", Guid.Parse("b6666666-6666-6666-6666-666666666666"), shouldThrow: true);
        Assert.True(host.RegisterBuiltIn(throwingSync));
        var failedResult = await host.RunSyncAsync("test.throwing-sync", "/tmp/library.sqlite", token);
        Assert.False(failedResult.Succeeded);
        Assert.Contains(host.GetRecords(), r => r.Descriptor.Id == "test.throwing-sync" && r.State == PluginLifecycleState.Faulted);

        // Null return from SyncAsync faults cleanly without NRE.
        var nullSync = new FakeSyncPlugin("test.null-sync", Guid.Parse("b8888888-8888-8888-8888-888888888888"), returnNull: true);
        Assert.True(host.RegisterBuiltIn(nullSync));
        var nullResult = await host.RunSyncAsync("test.null-sync", "/tmp/library.sqlite", token);
        Assert.False(nullResult.Succeeded);
        Assert.Contains(host.GetRecords(), r => r.Descriptor.Id == "test.null-sync" && r.State == PluginLifecycleState.Faulted);

        // Caller cancellation during SyncAsync propagates without faulting the sync plugin.
        using var cancelCts = new CancellationTokenSource();
        var cancelSync = new FakeSyncPlugin("test.cancel-sync", Guid.Parse("b9999999-9999-9999-9999-999999999999"), cancelOnSync: cancelCts);
        Assert.True(host.RegisterBuiltIn(cancelSync));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RunSyncAsync("test.cancel-sync", "/tmp/library.sqlite", cancelCts.Token));
        Assert.Contains(host.GetRecords(), r => r.Descriptor.Id == "test.cancel-sync" && r.State == PluginLifecycleState.Active);
    }

    [Fact]
    public void GetDeclaredSources_is_non_materializing_and_excludes_empty_source_ids()
    {
        using var host = new PluginHost(PluginSurface.Core);
        var lazySourceId = Guid.Parse("c4444444-4444-4444-4444-444444444444");
        var lazyFactoryCalls = 0;

        host.RegisterLazy(
            new PluginDescriptor(
                "test.lazy-declared",
                lazySourceId,
                "Lazy Declared",
                new Version(1, 0, 0),
                PluginLoadKind.ExternalAssembly,
                PluginSurface.Core,
                PluginCapabilityKind.LibrarySource | PluginCapabilityKind.LibrarySync,
                new PluginEntryPoints(Core: "lazy-declared.dll")),
            () =>
            {
                lazyFactoryCalls++;
                return new FakeSourceOnlyPlugin("test.lazy-declared", lazySourceId, "Lazy Materialized");
            });

        host.RegisterLazy(
            new PluginDescriptor(
                "test.lazy-empty-source",
                Guid.Empty,
                "Lazy Empty",
                new Version(1, 0, 0),
                PluginLoadKind.ExternalAssembly,
                PluginSurface.Core,
                PluginCapabilityKind.LibrarySource,
                new PluginEntryPoints(Core: "lazy-empty.dll")),
            () => new FakeSourceOnlyPlugin("test.lazy-empty-source", Guid.Empty, "Lazy Empty"));

        Assert.True(host.RegisterBuiltIn(new FakeSourceOnlyPlugin("test.active-empty-source", Guid.Empty, "Active Empty")));

        var declared = host.GetDeclaredSources();
        Assert.Equal(0, lazyFactoryCalls);
        var declaredSource = Assert.Single(declared);
        Assert.Equal(lazySourceId, declaredSource.SourceId);
        Assert.Equal("Lazy Declared", declaredSource.DisplayName);
        Assert.True(declaredSource.SupportsSync);
        Assert.Contains(host.GetRecords(), r => r.Descriptor.Id == "test.lazy-declared" && !r.IsMaterialized);

        var materialized = host.GetSources();
        Assert.Equal(1, lazyFactoryCalls);
        var materializedSource = Assert.Single(materialized);
        Assert.Equal(lazySourceId, materializedSource.SourceId);
        Assert.Equal("Lazy Materialized", materializedSource.DisplayName);
        Assert.Contains(host.GetRecords(), r => r.Descriptor.Id == "test.lazy-declared" && r.IsMaterialized);
    }

    [Fact]
    public void DiscoverExternal_rejects_depth_zero_symlinked_plugins_root()
    {
        var tempDir = Directory.CreateTempSubdirectory("playnite-symlink-root-");
        try
        {
            var realRoot = Path.Combine(tempDir.FullName, "real-plugins");
            var pluginDir = Path.Combine(realRoot, "sample");
            Directory.CreateDirectory(pluginDir);
            File.WriteAllText(
                Path.Combine(pluginDir, "plugin.json"),
                """
                {
                  "id": "symlink.sample",
                  "name": "Symlink Sample",
                  "version": "1.0.0",
                  "surfaces": ["core"],
                  "capabilities": ["commands"],
                  "entryPoints": { "core": "sample.dll" }
                }
                """);

            var symlinkRoot = Path.Combine(tempDir.FullName, "plugins-link");
            Directory.CreateSymbolicLink(symlinkRoot, realRoot);

            using var host = new PluginHost(PluginSurface.Core);
            var discovery = host.DiscoverExternal(symlinkRoot, new AlwaysRegularFileProbe());
            Assert.Empty(discovery.Discovered);
            Assert.Contains(discovery.Diagnostics, d => d.Code == "unsafe-plugins-root");
            Assert.Empty(host.GetRecords());
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void FilePluginSettingsStore_persists_atomically_enforces_limits_and_recovers_from_corruption()
    {
        var tempDir = Directory.CreateTempSubdirectory("playnite-plugin-settings-");
        try
        {
            var settingsRoot = Path.Combine(tempDir.FullName, "plugin-settings");
            var errors = new List<(string PluginId, string Message)>();
            var store1 = new FilePluginSettingsStore(settingsRoot, (id, msg) => errors.Add((id, msg)));

            store1.Set("demo.plugin", "apiKey", "secret-123");
            store1.Set("demo.plugin", "enabled", "true");
            store1.Set("demo.plugin", "enabled", "false");
            Assert.Equal("false", store1.Get("demo.plugin", "enabled"));

            var store2 = new FilePluginSettingsStore(settingsRoot, (id, msg) => errors.Add((id, msg)));
            Assert.Equal("secret-123", store2.Get("demo.plugin", "apiKey"));
            Assert.Equal("false", store2.Get("demo.plugin", "enabled"));
            var all = store2.GetAll("demo.plugin");
            Assert.Equal(2, all.Count);

            store2.Set("demo.plugin", "apiKey", null);
            var store3 = new FilePluginSettingsStore(settingsRoot);
            Assert.Null(store3.Get("demo.plugin", "apiKey"));
            Assert.Equal("false", store3.Get("demo.plugin", "enabled"));

            Assert.Throws<ArgumentException>(() => store3.Set("../escape", "k", "v"));
            Assert.Throws<ArgumentException>(() => new FilePluginSettingsStore("relative/path"));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                store3.Set("demo.plugin", "huge", new string('a', FilePluginSettingsStore.MaxValueLength + 1)));

            // 256-key cap permits updating existing keys and rejects adding key #257.
            for (var i = 0; i < FilePluginSettingsStore.MaxKeysPerPlugin; i++)
            {
                store3.Set("capped.plugin", $"k{i}", "v");
            }

            store3.Set("capped.plugin", "k0", "updated");
            Assert.Throws<InvalidOperationException>(() => store3.Set("capped.plugin", "k256", "overflow"));

            // Duplicate JSON keys, oversized files (> MaxFileBytes), and non-regular files fail safely cleanly.
            File.WriteAllText(Path.Combine(settingsRoot, "dup.plugin.json"), """{"a":"1","a":"2"}""");
            var probeRejectPath = Path.Combine(settingsRoot, "fifo.plugin.json");
            File.WriteAllText(probeRejectPath, """{"a":"1"}""");
            File.WriteAllText(Path.Combine(settingsRoot, "oversized.plugin.json"), new string('x', FilePluginSettingsStore.MaxFileBytes + 32));
            File.WriteAllText(Path.Combine(settingsRoot, "corrupt.plugin.json"), "{ not-json");

            // Locking is reentrancy-safe when onError calls back into Get/GetAll.
            var reentrantErrors = new List<string>();
            FilePluginSettingsStore? store4 = null;
            store4 = new FilePluginSettingsStore(
                settingsRoot,
                (id, msg) =>
                {
                    reentrantErrors.Add($"{id}:{msg}");
                    _ = store4!.GetAll(id);
                },
                new RejectPathProbe(probeRejectPath));
            Assert.Null(store4.Get("corrupt.plugin", "anything"));
            Assert.Null(store4.Get("dup.plugin", "a"));
            Assert.Null(store4.Get("fifo.plugin", "a"));
            Assert.Null(store4.Get("oversized.plugin", "a"));
            Assert.Contains(reentrantErrors, e => e.StartsWith("corrupt.plugin:", StringComparison.Ordinal));
            Assert.Contains(reentrantErrors, e => e.StartsWith("dup.plugin:", StringComparison.Ordinal));
            Assert.Contains(reentrantErrors, e => e.StartsWith("fifo.plugin:", StringComparison.Ordinal));
            Assert.Contains(reentrantErrors, e => e.StartsWith("oversized.plugin:", StringComparison.Ordinal));

            // Transient read failure does NOT overwrite existing corrupted/protected disk file on Set.
            Assert.Throws<IOException>(() => store4.Set("corrupt.plugin", "new", "val"));
            Assert.Equal("{ not-json", File.ReadAllText(Path.Combine(settingsRoot, "corrupt.plugin.json")));

            // Dangling symlink leaf refuses both Get and Set without touching outside path.
            var danglingLink = Path.Combine(settingsRoot, "dangling.plugin.json");
            File.CreateSymbolicLink(danglingLink, Path.Combine(tempDir.FullName, "does-not-exist.json"));
            Assert.Null(store4.Get("dangling.plugin", "k"));
            Assert.Throws<IOException>(() => store4.Set("dangling.plugin", "k", "v"));

            // Symlinked root directory refuses reads/writes safely.
            var realTarget = Path.Combine(tempDir.FullName, "real-target");
            Directory.CreateDirectory(realTarget);
            var symlinkRoot = Path.Combine(tempDir.FullName, "symlink-settings");
            Directory.CreateSymbolicLink(symlinkRoot, realTarget);
            var symlinkStore = new FilePluginSettingsStore(symlinkRoot, (id, msg) => errors.Add((id, msg)));
            Assert.Null(symlinkStore.Get("demo.plugin", "enabled"));
            Assert.Throws<IOException>(() => symlinkStore.Set("demo.plugin", "k", "v"));

            // Live symlinked plugin JSON file refuses reads/writes safely.
            var dummyTarget = Path.Combine(tempDir.FullName, "outside.json");
            File.WriteAllText(dummyTarget, "{\"secret\":\"leak\"}");
            File.CreateSymbolicLink(Path.Combine(settingsRoot, "symlink.plugin.json"), dummyTarget);
            var leafCheckStore = new FilePluginSettingsStore(settingsRoot, (id, msg) => errors.Add((id, msg)));
            Assert.Null(leafCheckStore.Get("symlink.plugin", "secret"));
            Assert.Throws<IOException>(() => leafCheckStore.Set("symlink.plugin", "k", "v"));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UiSlots_and_metadata_activate_lazily_sanitize_urls_cap_items_and_isolate_faults()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = new PluginHost(PluginSurface.Core | PluginSurface.Desktop);
        var lazyFactoryCalls = 0;

        var lazyDescriptor = new PluginDescriptor(
            "test.lazy-slots",
            Guid.Empty,
            "Lazy Slots",
            new Version(1, 0, 0),
            PluginLoadKind.ExternalAssembly,
            PluginSurface.Desktop,
            PluginCapabilityKind.UiSlots | PluginCapabilityKind.Metadata,
            new PluginEntryPoints(Desktop: "lazy-slots.dll"));
        host.RegisterLazy(lazyDescriptor, () =>
        {
            lazyFactoryCalls++;
            return new FakeSlotAndMetadataPlugin("test.lazy-slots", "meta.lazy");
        });

        Assert.True(host.HasUiSlotProviders(PluginUiSlots.CardBadge));
        Assert.Equal(0, lazyFactoryCalls);
        Assert.Contains(host.GetRecords(), r => r.Descriptor.Id == "test.lazy-slots" && !r.IsMaterialized);

        Assert.True(host.RegisterBuiltIn(new ThrowingSlotAndMetadataPlugin("test.broken-slots")));

        var snapshot = new PluginGameSnapshot(Guid.NewGuid(), "Half-Life 2", "220", Guid.Parse("cb91dfc9-b977-43bf-8e70-55f46e410fab"), true);
        var cardContributions = await host.ResolveUiSlotAsync(PluginUiSlots.CardBadge, snapshot, token);
        Assert.Equal(1, lazyFactoryCalls);

        var contribution = Assert.Single(cardContributions);
        Assert.Equal("test.lazy-slots", contribution.PluginId);
        Assert.NotNull(contribution.Badges);
        Assert.Equal(4, contribution.Badges.Count); // Capped to 4 per plugin, blank label filtered out.
        Assert.Equal("https://protondb.com/app/220", contribution.Badges[0].LinkUrl);
        Assert.Null(contribution.Badges[1].LinkUrl); // javascript: stripped
        Assert.Null(contribution.Badges[2].LinkUrl); // http: stripped

        // Broken plugin transitioned to Faulted without breaking healthy slot resolution or metadata.
        Assert.Contains(host.GetRecords(), r => r.Descriptor.Id == "test.broken-slots" && r.State == PluginLifecycleState.Faulted);

        var metadata = await host.FetchMetadataAsync(snapshot, token);
        var metaItem = Assert.Single(metadata);
        Assert.Equal("test.lazy-slots", metaItem.PluginId);
        Assert.Equal(100, metaItem.CommunityScore); // Clamped to [0..100]
        Assert.Equal(32, metaItem.Attributes!.Count); // Capped to 32 & duplicate trimmed key handled inside try.
        Assert.True(host.TryGetMetadataProvider("meta.lazy", out _));
        Assert.Single(host.GetMetadataProviders());

        // DetailsSection sanitization caps: max 2 sections, 12 fields/section, URL UserInfo rejected & LinkLabel stripped.
        var sectionResults = await host.ResolveUiSlotAsync(PluginUiSlots.DetailsSection, snapshot, token);
        var sectionContrib = Assert.Single(sectionResults);
        Assert.Equal(2, sectionContrib.Sections!.Count);
        Assert.Equal(12, sectionContrib.Sections[0].Fields!.Count);
        Assert.Null(sectionContrib.Sections[0].LinkUrl);
        Assert.Null(sectionContrib.Sections[0].LinkLabel);
        Assert.Equal("https://protondb.com/app/220", sectionContrib.Sections[1].LinkUrl);
        Assert.Equal("Open", sectionContrib.Sections[1].LinkLabel);

        // Disable/Enable removes & restores trimmed slot subscriptions and updates HasUiSlotProviders accurately.
        Assert.True(host.Disable("test.lazy-slots"));
        Assert.False(host.HasUiSlotProviders(PluginUiSlots.CardBadge));
        Assert.False(host.HasUiSlotProviders(PluginUiSlots.DetailsSection));
        Assert.True(host.Enable("test.lazy-slots"));
        Assert.True(host.HasUiSlotProviders(PluginUiSlots.CardBadge));

        // Conflicting ProviderId is rejected without taking over active provider.
        Assert.False(host.RegisterBuiltIn(new FakeSlotAndMetadataPlugin("test.conflict-meta", "meta.lazy")));
        Assert.Contains(host.GetRecords(), r => r.Descriptor.Id == "test.conflict-meta" && r.State == PluginLifecycleState.Faulted);

        // Caller cancellation propagates without faulting active UI slot plugin.
        using var cancelCts = new CancellationTokenSource();
        var cancelPlugin = new CancelingSlotPlugin("test.cancel-slot", cancelCts);
        Assert.True(host.RegisterBuiltIn(cancelPlugin));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.ResolveUiSlotAsync(PluginUiSlots.DetailsBadges, snapshot, cancelCts.Token));
        Assert.Contains(host.GetRecords(), r => r.Descriptor.Id == "test.cancel-slot" && r.State == PluginLifecycleState.Active);
    }

    private sealed class AlwaysRegularFileProbe : IRegularFileProbe
    {
        public bool IsRegularFile(string path) => true;
    }

    private sealed class RejectPathProbe(string rejectedPath) : IRegularFileProbe
    {
        public bool IsRegularFile(string path) => !string.Equals(path, rejectedPath, StringComparison.Ordinal);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FakeBuiltInPlugin :
        IPlaynitePlugin,
        ILibrarySourceCapability,
        ILaunchCapability,
        ICommandCapability,
        IEventHookCapability,
        IPluginSettingsCapability
    {
        public const string Id = "test.builtin";
        public static readonly Guid SourceGuid = Guid.Parse("b2222222-2222-2222-2222-222222222222");

        private readonly string pluginId;
        private readonly Guid sourceId;
        private readonly string displayName;

        public FakeBuiltInPlugin(
            string pluginId = Id,
            Guid? sourceId = null,
            string displayName = "Fake Store")
        {
            this.pluginId = pluginId;
            this.sourceId = sourceId ?? SourceGuid;
            this.displayName = displayName;
            Descriptor = new PluginDescriptor(
                pluginId,
                this.sourceId,
                displayName,
                new Version(1, 0, 0),
                PluginLoadKind.BuiltIn,
                PluginSurface.Core | PluginSurface.Desktop,
                PluginCapabilityKind.LibrarySource | PluginCapabilityKind.Launch | PluginCapabilityKind.Commands | PluginCapabilityKind.EventHooks | PluginCapabilityKind.Settings,
                new PluginEntryPoints());
        }

        public List<string> ReceivedEvents { get; } = [];
        public List<PluginEvent> ReceivedEventRecords { get; } = [];
        public bool Disposed { get; private set; }

        public PluginDescriptor Descriptor { get; }
        public LibrarySourceInfo Source => new(sourceId, displayName, displayName);
        public Guid SourceId => sourceId;
        public IReadOnlyList<PluginCommandDescriptor> Commands =>
            [new PluginCommandDescriptor("fake.sync", pluginId, "Sync Fake", PluginCommandPlacement.LibraryToolbar, sourceId)];
        public IReadOnlyList<string> SubscribedEvents => [PluginEvents.AppStarted, PluginEvents.GameStarting, PluginEvents.LibrarySynced];
        public IReadOnlyList<PluginSettingField> Fields =>
            [new PluginSettingField("apiKey", "API Key", PluginSettingType.String)];

        public PluginRegistration Setup(PluginContext context)
        {
            context.SetSetting("initialized", "configured");
            return new PluginRegistration
            {
                LibrarySources = [this],
                LaunchProviders = [this],
                CommandProviders = [this],
                EventHooks = [this],
                Settings = this,
                OnDispose = () => Disposed = true,
            };
        }

        public PluginLaunchSpec DescribeLaunch(Game game) =>
            new(PluginLaunchKind.TrackedProcess, "/usr/bin/fake-runner");

        public PluginLaunchSpec ResolveLaunch(Game game, Func<string, string?>? toolResolver = null) =>
            new(PluginLaunchKind.TrackedProcess, "/usr/bin/fake-runner", "/usr/bin/fake-runner", ["--play"]);

        public Task<PluginCommandResult> ExecuteAsync(string commandId, PluginCommandContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new PluginCommandResult(true, "Synced"));

        public Task OnEventAsync(PluginEvent pluginEvent, CancellationToken cancellationToken)
        {
            ReceivedEvents.Add(pluginEvent.Name);
            ReceivedEventRecords.Add(pluginEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class FlakyPlugin : IPlaynitePlugin, ICommandCapability, IEventHookCapability
    {
        public const string PluginId = "test.flaky";

        public bool ShouldThrowOnCommand { get; set; } = true;
        public int SetupCount { get; private set; }

        public PluginDescriptor Descriptor { get; } = new(
            PluginId,
            Guid.Empty,
            "Flaky Plugin",
            new Version(1, 0, 0),
            PluginLoadKind.BuiltIn,
            PluginSurface.Core,
            PluginCapabilityKind.Commands | PluginCapabilityKind.EventHooks,
            new PluginEntryPoints());

        public IReadOnlyList<PluginCommandDescriptor> Commands =>
            [new PluginCommandDescriptor("flaky.cmd", PluginId, "Flaky", PluginCommandPlacement.MainMenu)];

        public IReadOnlyList<string> SubscribedEvents => [PluginEvents.AppStarted];

        public PluginRegistration Setup(PluginContext context)
        {
            SetupCount++;
            return new()
            {
                CommandProviders = [this],
                EventHooks = [this],
            };
        }

        public Task<PluginCommandResult> ExecuteAsync(string commandId, PluginCommandContext context, CancellationToken cancellationToken)
        {
            if (ShouldThrowOnCommand)
            {
                throw new InvalidOperationException("Command boom");
            }

            return Task.FromResult(new PluginCommandResult(true, "Recovered"));
        }

        public Task OnEventAsync(PluginEvent pluginEvent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Hook boom");
    }

    private sealed class FakeSyncPlugin(
        string pluginId,
        Guid sourceId,
        string? overrideSyncId = null,
        bool shouldThrow = false,
        bool succeeded = true,
        bool returnNull = false,
        CancellationTokenSource? cancelOnSync = null)
        : IPlaynitePlugin, ILibrarySourceCapability, ILibrarySyncCapability
    {
        public PluginDescriptor Descriptor { get; } = new(
            pluginId,
            sourceId,
            pluginId,
            new Version(1, 0, 0),
            PluginLoadKind.BuiltIn,
            PluginSurface.Core,
            PluginCapabilityKind.LibrarySource | PluginCapabilityKind.LibrarySync,
            new PluginEntryPoints());

        public LibrarySourceInfo Source { get; } = new(sourceId, pluginId, pluginId, SupportsSync: true);
        string ILibrarySyncCapability.PluginId => overrideSyncId ?? pluginId;
        public string DisplayName => pluginId;

        public PluginRegistration Setup(PluginContext context) => new()
        {
            LibrarySources = [this],
            SyncProviders = [this],
        };

        public async Task<PluginSyncResult> SyncAsync(string databasePath, CancellationToken cancellationToken)
        {
            if (cancelOnSync is not null)
            {
                await cancelOnSync.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (shouldThrow)
            {
                throw new InvalidOperationException("Sync boom");
            }

            if (returnNull)
            {
                return null!;
            }

            return new PluginSyncResult(
                pluginId,
                Succeeded: succeeded,
                Reconciled: succeeded,
                Added: succeeded ? 2 : 0,
                Updated: 0,
                Removed: 0,
                Unchanged: 1,
                Skipped: 0,
                Summary: succeeded ? "Synced 2 games." : "Not synced.",
                Issues: succeeded ? [] : ["sync-declined"]);
        }
    }

    private sealed class FakeSourceOnlyPlugin(string pluginId, Guid sourceId, string displayName)
        : IPlaynitePlugin, ILibrarySourceCapability
    {
        public PluginDescriptor Descriptor { get; } = new(
            pluginId,
            sourceId,
            displayName,
            new Version(1, 0, 0),
            PluginLoadKind.BuiltIn,
            PluginSurface.Core,
            PluginCapabilityKind.LibrarySource,
            new PluginEntryPoints());

        public LibrarySourceInfo Source { get; } = new(sourceId, displayName, displayName);

        public PluginRegistration Setup(PluginContext context) => new()
        {
            LibrarySources = [this],
        };
    }

    private sealed class FakeSlotAndMetadataPlugin(string pluginId, string providerId)
        : IPlaynitePlugin, IUiSlotCapability, IMetadataCapability
    {
        public PluginDescriptor Descriptor { get; } = new(
            pluginId,
            Guid.Empty,
            pluginId,
            new Version(1, 0, 0),
            PluginLoadKind.BuiltIn,
            PluginSurface.Core | PluginSurface.Desktop,
            PluginCapabilityKind.UiSlots | PluginCapabilityKind.Metadata,
            new PluginEntryPoints());

        public IReadOnlyList<string> SubscribedSlots => ["  card.badge  ", PluginUiSlots.DetailsSection];
        public string ProviderId => providerId;
        public string DisplayName => pluginId;

        public PluginRegistration Setup(PluginContext context) => new()
        {
            UiSlotProviders = [this],
            MetadataProviders = [this],
        };

        public Task<PluginUiSlotContribution?> ResolveSlotAsync(
            string slotId,
            PluginGameSnapshot game,
            CancellationToken cancellationToken)
        {
            if (slotId == PluginUiSlots.DetailsSection)
            {
                var tooManyFields = Enumerable.Range(0, 15)
                    .Select(i => new PluginUiSectionField($"Field{i}", $"Val{i}"))
                    .ToArray();
                return Task.FromResult<PluginUiSlotContribution?>(new PluginUiSlotContribution(
                    "forged-id",
                    slotId,
                    Sections:
                    [
                        new PluginUiSection("s0", "  ", "ignored"),
                        new PluginUiSection("s1", "First\nSection", "Summary\r\nText", tooManyFields, "Blocked", "https://user:pass@protondb.com/app/220"),
                        new PluginUiSection("s2", "Second Section", "Ok", [new PluginUiSectionField("Tier", "Gold")], "Open", "https://protondb.com/app/220"),
                        new PluginUiSection("s3", "Third Truncated"),
                    ]));
            }

            return Task.FromResult<PluginUiSlotContribution?>(new PluginUiSlotContribution(
                "forged-id",
                slotId,
                Badges:
                [
                    new PluginUiBadge("b0", "   ", PluginBadgeTone.Neutral),
                    new PluginUiBadge("b1", "Platinum", PluginBadgeTone.Accent, LinkUrl: "https://protondb.com/app/220"),
                    new PluginUiBadge("b2", "Xss", PluginBadgeTone.Danger, LinkUrl: "javascript:alert(1)"),
                    new PluginUiBadge("b3", "Insecure", PluginBadgeTone.Warning, LinkUrl: "http://example.com"),
                    new PluginUiBadge("b4", "Fourth", PluginBadgeTone.Positive),
                    new PluginUiBadge("b5", "FifthTruncated", PluginBadgeTone.Muted),
                ]));
        }

        public Task<PluginGameMetadata?> FetchMetadataAsync(PluginGameSnapshot game, CancellationToken cancellationToken)
        {
            var attrs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tier"] = "platinum",
                [" tier "] = "trimmed-duplicate",
            };
            for (var i = 0; i < 40; i++)
            {
                attrs[$"extra{i}"] = $"v{i}";
            }

            return Task.FromResult<PluginGameMetadata?>(new PluginGameMetadata(
                "forged-meta",
                Summary: "  Rich metadata  ",
                CommunityScore: 150,
                Attributes: attrs));
        }
    }

    private sealed class ThrowingSlotAndMetadataPlugin(string pluginId)
        : IPlaynitePlugin, IUiSlotCapability, IMetadataCapability
    {
        public PluginDescriptor Descriptor { get; } = new(
            pluginId,
            Guid.Empty,
            pluginId,
            new Version(1, 0, 0),
            PluginLoadKind.BuiltIn,
            PluginSurface.Core | PluginSurface.Desktop,
            PluginCapabilityKind.UiSlots | PluginCapabilityKind.Metadata,
            new PluginEntryPoints());

        public IReadOnlyList<string> SubscribedSlots => [PluginUiSlots.CardBadge];
        public string ProviderId => pluginId;
        public string DisplayName => pluginId;

        public PluginRegistration Setup(PluginContext context) => new()
        {
            UiSlotProviders = [this],
            MetadataProviders = [this],
        };

        public Task<PluginUiSlotContribution?> ResolveSlotAsync(string slotId, PluginGameSnapshot game, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Slot boom");

        public Task<PluginGameMetadata?> FetchMetadataAsync(PluginGameSnapshot game, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Meta boom");
    }

    private sealed class CancelingSlotPlugin(string pluginId, CancellationTokenSource cts)
        : IPlaynitePlugin, IUiSlotCapability
    {
        public PluginDescriptor Descriptor { get; } = new(
            pluginId,
            Guid.Empty,
            pluginId,
            new Version(1, 0, 0),
            PluginLoadKind.BuiltIn,
            PluginSurface.Desktop,
            PluginCapabilityKind.UiSlots,
            new PluginEntryPoints());

        public IReadOnlyList<string> SubscribedSlots => [PluginUiSlots.DetailsBadges];

        public PluginRegistration Setup(PluginContext context) => new()
        {
            UiSlotProviders = [this],
        };

        public async Task<PluginUiSlotContribution?> ResolveSlotAsync(
            string slotId,
            PluginGameSnapshot game,
            CancellationToken cancellationToken)
        {
            await cts.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }
}

/// <summary>Public plugin entry loaded into a collectible <see cref="AssemblyLoadContext"/> by test.</summary>
public sealed class TestExternalPlugin : IPlaynitePlugin, ILibrarySourceCapability, ILaunchCapability
{
    public static readonly Guid ExternalSourceId = Guid.Parse("c3333333-3333-3333-3333-333333333333");

    public PluginDescriptor Descriptor { get; } = new(
        "test.external",
        ExternalSourceId,
        "External Sample",
        new Version(1, 2, 3),
        PluginLoadKind.ExternalAssembly,
        PluginSurface.Core,
        PluginCapabilityKind.LibrarySource | PluginCapabilityKind.Launch,
        new PluginEntryPoints(Core: "TestExternalSample.dll", EntryType: typeof(TestExternalPlugin).FullName));

    public LibrarySourceInfo Source => new(ExternalSourceId, "External Sample Source", "External");

    public Guid SourceId => ExternalSourceId;

    public PluginRegistration Setup(PluginContext context) => new()
    {
        LibrarySources = [this],
        LaunchProviders = [this],
    };

    public PluginLaunchSpec DescribeLaunch(Game game) =>
        new(PluginLaunchKind.TrackedProcess, "External Runner");

    public PluginLaunchSpec ResolveLaunch(Game game, Func<string, string?>? toolResolver = null) =>
        new(PluginLaunchKind.TrackedProcess, "External Runner", "/usr/bin/external-runner", [game.GameId ?? string.Empty]);
}

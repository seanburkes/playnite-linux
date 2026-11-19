using Playnite.Platform.Linux;
using Playnite.Steam;
using Xunit;

namespace Playnite.Core.Tests.Steam;

/// <summary>
/// Portal-import tests. Every tree lives under one temporary directory and every portal answer is
/// scripted, so the tests need no desktop session, no network and no real Steam install.
/// </summary>
public sealed class SteamPortalTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("playnite-steam-portal-").FullName;
    private readonly SteamFileSystem fileSystem = new(new LinuxBoundedFileReader());

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task First_import_selects_each_declared_library_and_reads_its_app_set()
    {
        var primary = SteamApps("steam", (100, "Primary Game"));
        var second = SteamApps("second", (200, "Second Game"));
        WriteLibraryFile(primary, ("/host/steam", [100]), ("/host/second", [200]));
        var portal = new ScriptedPortal(
            Selected(primary, "/host/steam/steamapps", "doc-primary"),
            Selected(second, "/host/second/steamapps", "doc-second"));
        var store = new JsonSteamPortalGrantStore(GrantsPath);

        var result = await new PortalSteamLibraryImporter(portal, store, fileSystem).ImportAsync();

        Assert.True(result.Status == SteamPortalImportStatus.Complete, result.Detail);
        Assert.Equal(2, portal.Calls);
        Assert.Equal(2, result.Libraries.Count);
        Assert.Equal(new uint[] { 100 }, result.Libraries[0].Apps.Select(app => app.AppId));
        Assert.Equal(new uint[] { 200 }, result.Libraries[1].Apps.Select(app => app.AppId));
        Assert.Equal("Second Game", result.Libraries[1].Apps.Single().Name);
        Assert.Equal(2, store.Load().Count);
    }

    [Fact]
    public async Task A_second_import_reuses_the_persisted_grants_without_prompting()
    {
        var primary = SteamApps("steam", (100, "Primary Game"));
        var second = SteamApps("second", (200, "Second Game"));
        WriteLibraryFile(primary, ("/host/steam", [100]), ("/host/second", [200]));
        var store = new JsonSteamPortalGrantStore(GrantsPath);
        await new PortalSteamLibraryImporter(
            new ScriptedPortal(
                Selected(primary, "/host/steam/steamapps", "doc-primary"),
                Selected(second, "/host/second/steamapps", "doc-second")),
            store, fileSystem).ImportAsync();

        var offline = new ScriptedPortal();
        var result = await new PortalSteamLibraryImporter(offline, store, fileSystem).ImportAsync();

        Assert.True(result.Status == SteamPortalImportStatus.Complete, result.Detail);
        Assert.Equal(0, offline.Calls);
        Assert.Equal(2, result.Libraries.Count);
    }

    [Fact]
    public async Task A_selection_for_the_wrong_library_is_rejected_and_nothing_is_saved()
    {
        var primary = SteamApps("steam", (100, "Primary Game"));
        var other = SteamApps("other", (300, "Other Game"));
        WriteLibraryFile(primary, ("/host/steam", [100]), ("/host/second", [200]));
        var store = new JsonSteamPortalGrantStore(GrantsPath);

        var result = await new PortalSteamLibraryImporter(
            new ScriptedPortal(
                Selected(primary, "/host/steam/steamapps", "doc-primary"),
                Selected(other, "/host/other/steamapps", "doc-other")),
            store, fileSystem).ImportAsync();

        Assert.Equal(SteamPortalImportStatus.Rejected, result.Status);
        Assert.Contains("/host/second/steamapps", result.Detail, StringComparison.Ordinal);
        Assert.Empty(store.Load());
    }

    [Fact]
    public async Task A_primary_selection_without_libraryfolders_is_rejected()
    {
        var empty = Directory.CreateDirectory(Path.Combine(root, "empty", "steamapps")).FullName;

        var result = await new PortalSteamLibraryImporter(
            new ScriptedPortal(Selected(empty, "/host/empty/steamapps", "doc-empty")),
            new JsonSteamPortalGrantStore(GrantsPath), fileSystem).ImportAsync();

        Assert.Equal(SteamPortalImportStatus.Rejected, result.Status);
        Assert.Contains("libraryfolders.vdf", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_selection_without_a_host_path_or_document_id_is_rejected()
    {
        var primary = SteamApps("steam", (100, "Primary Game"));
        WriteLibraryFile(primary, ("/host/steam", [100]));

        var result = await new PortalSteamLibraryImporter(
            new ScriptedPortal(SteamPortalSelection.Selected(primary, null, null)),
            new JsonSteamPortalGrantStore(GrantsPath), fileSystem).ImportAsync();

        Assert.Equal(SteamPortalImportStatus.Rejected, result.Status);
        Assert.Contains("host path", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_revoked_grant_is_dropped_and_selected_again()
    {
        var primary = SteamApps("steam", (100, "Primary Game"));
        var second = SteamApps("second", (200, "Second Game"));
        WriteLibraryFile(primary, ("/host/steam", [100]), ("/host/second", [200]));
        var store = new JsonSteamPortalGrantStore(GrantsPath);
        await new PortalSteamLibraryImporter(
            new ScriptedPortal(
                Selected(primary, "/host/steam/steamapps", "doc-primary"),
                Selected(second, "/host/second/steamapps", "doc-second")),
            store, fileSystem).ImportAsync();
        var moved = Path.Combine(root, "moved", "steamapps");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(second, moved);

        var portal = new ScriptedPortal(Selected(moved, "/host/second/steamapps", "doc-second"));
        var result = await new PortalSteamLibraryImporter(portal, store, fileSystem).ImportAsync();

        Assert.True(result.Status == SteamPortalImportStatus.Complete, result.Detail);
        Assert.Equal(1, portal.Calls);
        Assert.Contains("second", portal.Titles[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_cancelled_selection_is_reported_as_cancelled()
    {
        var result = await new PortalSteamLibraryImporter(
            new ScriptedPortal(SteamPortalSelection.Cancelled()),
            new JsonSteamPortalGrantStore(GrantsPath), fileSystem).ImportAsync();

        Assert.Equal(SteamPortalImportStatus.Cancelled, result.Status);
        Assert.False(File.Exists(GrantsPath));
    }

    [Fact]
    public async Task An_unavailable_portal_is_reported_and_reads_nothing()
    {
        var result = await new PortalSteamLibraryImporter(
            new ScriptedPortal(SteamPortalSelection.Unavailable("no portal")),
            new JsonSteamPortalGrantStore(GrantsPath), fileSystem).ImportAsync();

        Assert.Equal(SteamPortalImportStatus.Unavailable, result.Status);
        Assert.Equal("no portal", result.Detail);
        Assert.Empty(result.Libraries);
    }

    [Fact]
    public void The_grant_store_round_trips_and_writes_a_private_file()
    {
        var store = new JsonSteamPortalGrantStore(GrantsPath);
        var grant = new SteamPortalGrant("/host/steam", "/host/steam/steamapps", "doc-1", "/run/user/1000/doc/1");

        store.Save([grant]);

        Assert.Equal([grant], store.Load());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(GrantsPath));
        }
    }

    [Fact]
    public void The_grant_store_drops_malformed_and_inconsistent_entries()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GrantsPath)!);
        File.WriteAllText(GrantsPath, """
            {
              "version": 1,
              "grants": [
                { "hostLibraryPath": "/host/steam", "steamAppsHostPath": "/host/steam/steamapps", "documentId": "doc-1", "portalPath": "/run/user/1000/doc/1" },
                { "hostLibraryPath": "relative", "steamAppsHostPath": "relative", "documentId": "", "portalPath": "relative" }
              ]
            }
            """);

        var grants = new JsonSteamPortalGrantStore(GrantsPath).Load();

        Assert.Single(grants);
        Assert.Equal("/host/steam/steamapps", grants[0].SteamAppsHostPath);
    }

    [Fact]
    public void Manifest_set_reads_only_appmanifest_files_in_app_id_order()
    {
        var steamApps = SteamApps("steam", (100, "Primary Game"), (50, "Early Game"));
        File.WriteAllText(Path.Combine(steamApps, "notes.txt"), "ignored");
        File.WriteAllText(Path.Combine(steamApps, "appmanifest_notanumber.acf"), "ignored");

        var manifests = SteamManifestSet.Read(fileSystem, steamApps);

        Assert.Equal(new uint[] { 50, 100 }, manifests.Apps.Select(app => app.AppId));
        Assert.Empty(manifests.Issues);
    }

    private string GrantsPath => Path.Combine(root, "config", "playnite", "steam-portal-grants.json");

    private string SteamApps(string name, params (uint Id, string Name)[] apps)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, name, "steamapps")).FullName;
        foreach (var app in apps)
        {
            File.WriteAllText(Path.Combine(directory, $"appmanifest_{app.Id}.acf"), Manifest(app.Id, app.Name));
        }

        return directory;
    }

    private static void WriteLibraryFile(string steamApps,
        params (string Path, uint[] Apps)[] libraries)
    {
        var text = new System.Text.StringBuilder("\"libraryfolders\"\n{\n");
        for (var index = 0; index < libraries.Length; index++)
        {
            text.Append("    \"").Append(index).Append("\"\n    {\n");
            text.Append("        \"path\" \"").Append(libraries[index].Path).Append("\"\n");
            text.Append("        \"apps\"\n        {\n");
            foreach (var app in libraries[index].Apps)
            {
                text.Append("            \"").Append(app).Append("\" \"1\"\n");
            }

            text.Append("        }\n    }\n");
        }

        text.Append("}\n");
        File.WriteAllText(System.IO.Path.Combine(steamApps, "libraryfolders.vdf"), text.ToString());
    }

    private static SteamPortalSelection Selected(string portalPath, string hostPath, string documentId) =>
        SteamPortalSelection.Selected(portalPath, hostPath, documentId);

    private static string Manifest(uint appId, string name) => $$"""
        "AppState"
        {
            "appid" "{{appId}}"
            "name" "{{name}}"
            "StateFlags" "4"
            "installdir" "Synthetic"
        }
        """;

    private sealed class ScriptedPortal(params SteamPortalSelection[] selections) : ISteamLibraryPortal
    {
        private readonly Queue<SteamPortalSelection> pending = new(selections);

        public int Calls { get; private set; }

        public List<string> Titles { get; } = [];

        public Task<SteamPortalSelection> SelectSteamAppsDirectoryAsync(string title,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Titles.Add(title);
            if (pending.Count == 0)
            {
                throw new InvalidOperationException("Unexpected portal prompt.");
            }

            return Task.FromResult(pending.Dequeue());
        }
    }
}

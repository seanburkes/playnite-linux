using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Playnite.Core.Database;
using Playnite.Settings;
using Xunit;

namespace Playnite.Smoke.Tests;

public sealed class FirstTimeSetupSmokeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "playnite first-run " + Path.GetRandomFileName());
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Config => Path.Combine(root, "config", "playnite", "settings.json");
    private string Database => Path.Combine(root, "data", "playnite", "library.sqlite");
    private string SettingsArchive => Database + ".import-settings.json";

    [Fact]
    public async Task A_published_import_survives_closing_the_wizard_and_finishes_on_relaunch()
    {
        var source = CopyLibrary();
        await File.WriteAllTextAsync(Path.Combine(source, "config.json"),
            """
            {"FirstTimeWizardComplete":true,"ShowSidebar":false,"DatabasePath":"D:\\Playnite\\library",
             "AutoBackupDir":"Q:\\Unknown","FutureSetting":{"enabled":true}}
            """, Token);
        var before = Snapshot(source);

        var imported = await RunAsync("leave", source, @"D:\Playnite");
        Assert.Contains("choices import=True; steam=True; keep=False; finish=False", imported);
        Assert.Contains("dark=True; import card=True; panel styled=True", imported);
        Assert.Contains("progress=True; finish=False", imported);
        Assert.Contains("Imported 3 Steam games", imported);
        Assert.Contains("Unmapped settings paths kept as legacy data: AutoBackupDir.", imported);
        Assert.Contains("finish=True; progress=False", imported);
        Assert.DoesNotContain("shell visible", imported);
        Assert.False(File.Exists(Config));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(Config)!, "welcome-acknowledged")));
        Assert.Equal(before, Snapshot(source));
        Assert.Equal(["220", "400", "570"], await GameIdsAsync());

        var backup = Path.Combine(Database + ".import", "source.json");
        var report = Database + ".import-report.json";
        Assert.Contains(backup, imported);
        Assert.Contains(report, imported);
        Assert.Equal("playnite-v10-library", (await ReadJsonAsync(backup)).GetProperty("format").GetString());
        Assert.Equal(3, (await ReadJsonAsync(report)).GetProperty("ImportedSteam").GetInt32());
        Assert.True(File.Exists(SettingsArchive));

        // The mounted source need not remain available for the handoff.
        Directory.Move(source, source + "-unmounted");
        var finished = await RunAsync("keep");
        Assert.Contains("choices import=False; steam=True; keep=True; finish=False", finished);
        Assert.Contains("dark=True; import card=False; panel styled=True", finished);
        Assert.Contains("existing Linux library will be kept", finished);
        Assert.Contains("shell visible=True", finished);
        using var settings = new PlayniteSettingsService(Config);
        var native = await settings.LoadAsync(Token);
        Assert.True(native.Get(PlayniteSettingsKeys.FirstTimeWizardComplete));
        Assert.False(native.Get(PlayniteSettingsKeys.ShowSidebar));
        Assert.Equal(Path.Combine(source, "library"), native.Get(PlayniteSettingsKeys.DatabasePath));
        Assert.Equal(@"Q:\Unknown", (string?)native.Legacy["AutoBackupDir"]);
        Assert.NotNull(native.Legacy["FutureSetting"]);
        Assert.Contains("already complete", await RunAsync("observe"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:relative")]
    public async Task Existing_native_preferences_win_over_a_legacy_config_but_the_archive_is_retained(string windowsRoot)
    {
        var source = CopyLibrary();
        await File.WriteAllTextAsync(Path.Combine(source, "config.json"),
            """{"Language":"de","DatabasePath":"C:\\Unmapped\\library","ShowSidebar":true}""", Token);
        using var settings = new PlayniteSettingsService(Config);
        await settings.UpdateAsync([new(PlayniteSettingsKeys.ShowSidebar, false)], Token);
        var output = await RunAsync("import", source, windowsRoot);

        Assert.Contains("shell visible=True", output);
        Assert.Equal(windowsRoot.Length != 0, output.Contains("was not a valid mapping", StringComparison.Ordinal));
        var native = await settings.LoadAsync(Token);
        Assert.True(native.Get(PlayniteSettingsKeys.FirstTimeWizardComplete));
        Assert.False(native.Get(PlayniteSettingsKeys.ShowSidebar));
        Assert.Equal("english", native.Get(PlayniteSettingsKeys.Language));
        var archived = await ReadJsonAsync(SettingsArchive);
        Assert.Equal(@"C:\Unmapped\library", archived.GetProperty("legacy").GetProperty("DatabasePath").GetString());
        Assert.Equal("DatabasePath", archived.GetProperty("report").GetProperty("unmappedPaths")[0]
            .GetProperty("property").GetString());
    }

    [Fact]
    public async Task Malformed_legacy_settings_do_not_undo_the_published_library_or_block_finishing()
    {
        var source = CopyLibrary();
        await File.WriteAllTextAsync(Path.Combine(source, "config.json"), "{", Token);
        var before = Snapshot(source);

        var output = await RunAsync("import", source);
        Assert.Contains("Imported 3 Steam games", output);
        Assert.Contains("Legacy settings were not copied:", output);
        Assert.Contains("shell visible=True", output);
        Assert.False(File.Exists(SettingsArchive));
        Assert.Equal(["220", "400", "570"], await GameIdsAsync());
        Assert.Equal(before, Snapshot(source));
    }

    [Fact]
    public async Task Reader_failure_is_visible_and_does_not_create_a_library_or_complete_setup()
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "not a v10 library")).FullName;
        var output = await RunAsync("fail", source);

        Assert.Contains("Setup failed", output);
        Assert.Contains("legacy reader exited", output);
        Assert.Contains("database.json", output);
        Assert.Contains("finish=False; progress=False", output);
        Assert.DoesNotContain("shell visible", output);
        Assert.False(File.Exists(Database));
        Assert.False(File.Exists(Config));
        Assert.Empty(Directory.EnumerateFileSystemEntries(source));
    }

    [Fact]
    public async Task A_symlink_alias_to_the_profile_is_refused_before_creating_destinations()
    {
        var profile = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
        var source = Path.Combine(root, "linked source");
        Directory.CreateSymbolicLink(source, profile);

        var output = await RunAsync("fail", source);
        Assert.Contains("Import paths must not traverse symbolic links", output);
        Assert.Empty(Directory.EnumerateFileSystemEntries(profile));
        Assert.False(File.Exists(Config));
    }

    [Fact]
    public async Task Cancelling_the_reader_leaves_setup_unfinished_and_a_relaunch_can_import()
    {
        var source = CopyLibrary();
        var before = Snapshot(source);
        var output = await RunAsync("cancel", Path.Combine(source, "library"));

        Assert.Contains("Setup was cancelled", output);
        Assert.Contains("finish=False; progress=False", output);
        Assert.False(File.Exists(Database));
        Assert.False(File.Exists(Config));
        Assert.Equal(before, Snapshot(source));
        Assert.Contains("shell visible=True", await RunAsync("import", Path.Combine(source, "library")));
        Assert.Equal(["220", "400", "570"], await GameIdsAsync());
    }

    [Fact]
    public async Task Fresh_setup_discovers_local_steam_games_without_a_legacy_source()
    {
        var steam = Path.Combine(root, "home", ".steam", "steam");
        var steamApps = Path.Combine(steam, "steamapps");
        Directory.CreateDirectory(Path.Combine(steamApps, "common", "Half-Life 2"));
        var libraryFile = Path.Combine(steamApps, "libraryfolders.vdf");
        await File.WriteAllTextAsync(libraryFile,
            $$""" "libraryfolders" { "0" { "path" "{{steam}}" "apps" { "220" "123456789" } } } """, Token);
        File.Copy(Path.Combine(RepositoryRoot(), "tests", "fixtures", "steam", "basic", "steamapps", "appmanifest_220.acf"),
            Path.Combine(steamApps, "appmanifest_220.acf"));
        var before = Snapshot(steam);

        var output = await RunAsync("steam");
        Assert.Contains("Steam setup found 1 library entries", output);
        Assert.Contains("shell visible=True", output);
        Assert.Equal(["220"], await GameIdsAsync());
        Assert.Equal(before, Snapshot(steam));
        Assert.False(File.Exists(Database + ".import-report.json"));
        using var settings = new PlayniteSettingsService(Config);
        Assert.True((await settings.LoadAsync(Token)).Get(PlayniteSettingsKeys.FirstTimeWizardComplete));
    }

    [Fact]
    public async Task Fresh_setup_can_finish_with_an_empty_library_when_steam_is_absent()
    {
        var output = await RunAsync("steam");
        Assert.Contains("No local Steam installation was found", output);
        Assert.Contains("shell visible=True", output);
        Assert.Empty(await GameIdsAsync());
    }

    [Fact]
    public async Task LiteDB_exists_only_in_the_sidecar_dependency_manifest()
    {
        var directory = Path.GetDirectoryName(DesktopHostPath())!;
        var desktop = (await ReadJsonAsync(Path.Combine(directory, "Playnite.Desktop.deps.json"))).GetProperty("libraries");
        var reader = (await ReadJsonAsync(Path.Combine(directory, "legacy-import", "Playnite.LegacyImport.deps.json")))
            .GetProperty("libraries");
        Assert.DoesNotContain(desktop.EnumerateObject(), entry => entry.Name.StartsWith("LiteDB/", StringComparison.Ordinal)
            || entry.Name.StartsWith("Playnite.LegacyImport/", StringComparison.Ordinal));
        Assert.Contains(reader.EnumerateObject(), entry => entry.Name.StartsWith("LiteDB/", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(directory, "LiteDB.dll")));
    }

    private async Task<string[]> GameIdsAsync()
    {
        Assert.True(File.Exists(Database));
        var library = await LibraryDatabase.OpenAsync(Database, Token);
        var games = await library.ReadAsync(session => session.Games.GetAllAsync(), Token);
        return games.Select(item => item.Value.GameId).Order(StringComparer.Ordinal).ToArray();
    }

    private string CopyLibrary()
    {
        var source = Path.Combine(root, "mounted Windows copy");
        var fixture = Path.Combine(RepositoryRoot(), "tests", "fixtures", "v10", "library");
        foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(source, "library", Path.GetRelativePath(fixture, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return source;
    }

    private static string[] Snapshot(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(file => Path.GetRelativePath(directory, file) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))))
        .ToArray();

    private async Task<JsonElement> ReadJsonAsync(string file)
    {
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(file, Token));
        return document.RootElement.Clone();
    }

    private async Task<string> RunAsync(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { DesktopHostPath(), "--headless", "--first-run-smoke" }.Concat(args))
        {
            start.ArgumentList.Add(argument);
        }
        foreach (var (key, directory) in new (string, string)[]
        {
            ("XDG_CONFIG_HOME", "config"), ("XDG_STATE_HOME", "state"), ("XDG_DATA_HOME", "data"),
            ("XDG_CACHE_HOME", "cache"), ("XDG_RUNTIME_DIR", "runtime"), ("HOME", "home"),
        })
        {
            var path = Directory.CreateDirectory(Path.Combine(root, directory)).FullName;
            start.Environment[key] = path;
        }
        start.Environment.Remove("STEAM_ROOT");
        start.Environment.Remove("DISPLAY");
        start.Environment.Remove("WAYLAND_DISPLAY");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Token);
        var error = process.StandardError.ReadToEndAsync(Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync();
            Token.ThrowIfCancellationRequested();
            throw new TimeoutException($"First-run smoke timed out. stdout: {await output}; stderr: {await error}");
        }
        var stdout = await output;
        var stderr = await error;
        Assert.True(process.ExitCode == 0, $"Exit {process.ExitCode}\n{stdout}\n{stderr}");
        Assert.Empty(stderr);
        return stdout;
    }

    private static string DesktopHostPath() => Path.Combine(RepositoryRoot(), "src", "Playnite.Desktop", "bin",
        typeof(FirstTimeSetupSmokeTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration,
        "net11.0", "Playnite.Desktop.dll");

    private static string RepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "Playnite.slnx")))
            {
                return current.FullName;
            }
        }
        throw new InvalidOperationException("Cannot locate Playnite.slnx.");
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

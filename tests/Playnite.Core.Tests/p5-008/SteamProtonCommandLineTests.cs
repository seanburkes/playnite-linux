using Playnite.Platform.Sessions;
using Playnite.Steam;
using Xunit;

namespace Playnite.Core.Tests.Steam;

/// <summary>
/// Proton/Windows identity: the host executable is the runtime preloader, so the only game
/// evidence is the Windows argv path resolved through this app's own prefix drive map.
/// </summary>
public sealed class SteamProtonCommandLineTests
{
    private const string Preloader =
        "/steam/steamapps/common/Proton - Experimental/files/lib/wine/i386-unix/wine64-preloader";
    private const string WindowsGame = "S:\\steamapps\\common\\Hades\\x64\\Hades.exe";

    private static readonly ExternalProcessEntry Client = Entry(10, 1, 10, "/steam/steam");
    private static readonly ExternalProcessSnapshot Before = new([Client], true);

    [Fact]
    public void Windows_command_line_resolves_through_the_app_prefix_to_the_real_game()
    {
        using var library = new ProtonLibrary();
        var decision = SteamProcessAttribution.Evaluate(library.Target, Before,
            new([Client, Row(22, ["S:\\steamapps\\common\\Hades\\x64\\Hades.exe", "-foo"])], true));

        Assert.Null(decision.Rejection);
        var candidate = Assert.Single(decision.Candidates);
        Assert.Equal(library.Game, candidate.GamePath);
        Assert.Equal(WindowsGame, candidate.WindowsArgument);
        Assert.Equal(Preloader, candidate.Executable);
        Assert.Equal(22, decision.Identity!.Value.ProcessId);
    }

    [Theory]
    [InlineData("s:", "s:/steamapps/common/Hades/x64/Hades.exe")]
    [InlineData("S:", "S:\\steamapps\\common\\Hades\\x64\\Hades.exe")]
    [InlineData("t:", "T:\\steamapps\\common\\Hades\\x64\\Hades.exe")]
    [InlineData("t:", "t:/steamapps/common/Hades/x64/Hades.exe")]
    public void Drive_letter_and_separator_variants_map_to_the_same_game(string drive, string argument)
    {
        using var library = new ProtonLibrary(drive);
        var decision = SteamProcessAttribution.Evaluate(library.Target, Before,
            new([Client, Row(22, [argument])], true));

        Assert.Null(decision.Rejection);
        Assert.Equal(library.Game, Assert.Single(decision.Candidates).GamePath);
        Assert.Equal(argument, Assert.Single(decision.Candidates).WindowsArgument);
    }

    [Fact]
    public void Windows_path_outside_the_installdir_or_prefix_is_rejected()
    {
        using var library = new ProtonLibrary();
        library.CreateForeignFile("Other", "Other.exe");
        library.CreatePrefixFile("drive_c/windows/system32/other.exe");

        string[] outside =
        [
            "S:\\steamapps\\common\\Other\\Other.exe",                     // sibling installdir
            "Z:\\etc\\hostname",                                            // drive mapped outside the library
            "Q:\\steamapps\\common\\Hades\\x64\\Hades.exe",                 // no dosdevices link
            "S:\\..\\..\\..\\..\\etc\\hostname",                            // traversal out of the drive
            "steamapps\\common\\Hades\\x64\\Hades.exe",                     // relative, not drive-absolute
            "\\\\server\\share\\Hades.exe",                                 // UNC is not resolved
        ];

        foreach (var argument in outside)
        {
            var decision = SteamProcessAttribution.Evaluate(library.Target, Before,
                new([Client, Row(22, [argument])], true));
            Assert.Empty(decision.Candidates);
            Assert.Null(decision.Identity);
        }
    }

    [Fact]
    public void Symlink_inside_the_game_directory_that_escapes_the_roots_is_rejected()
    {
        using var library = new ProtonLibrary();
        var escape = Path.Combine(library.Install, "x64", "escape.exe");
        File.CreateSymbolicLink(escape, "/etc/hostname");

        var decision = SteamProcessAttribution.Evaluate(library.Target, Before,
            new([Client, Row(22, ["S:\\steamapps\\common\\Hades\\x64\\escape.exe"])], true));

        Assert.Empty(decision.Candidates);
        Assert.Null(decision.Identity);
    }

    [Theory]
    [InlineData("/steam/steam.exe")]
    [InlineData("/usr/bin/python3")]
    [InlineData("/steam/pv-adverb")]
    [InlineData("/steam/wineserver")]
    [InlineData("/steam/winedevice.exe")]
    [InlineData("/steam/explorer.exe")]
    [InlineData("/steam/services.exe")]
    public void Proton_wrapper_process_is_never_the_game_even_when_its_argv_names_the_game(string wrapper)
    {
        using var library = new ProtonLibrary();
        var decision = SteamProcessAttribution.Evaluate(library.Target, Before,
            new([Client, Row(22, ["S:\\steamapps\\common\\Hades\\x64\\Hades.exe"], executable: wrapper)], true));

        Assert.Empty(decision.Candidates);
        Assert.Null(decision.Identity);
        Assert.True(SteamProcessAttribution.IsProtonWrapper(wrapper));
    }

    [Theory]
    [InlineData("C:\\windows\\system32\\services.exe")]
    [InlineData("C:\\windows\\system32\\winedevice.exe")]
    [InlineData("C:\\windows\\explorer.exe")]
    [InlineData("C:\\windows\\system32\\python.exe")]
    [InlineData("C:\\windows\\system32\\steam.exe")]
    public void Windows_wrapper_binary_inside_the_prefix_is_never_the_game(string argument)
    {
        using var library = new ProtonLibrary();
        var name = argument.Replace('\\', '/');
        library.CreatePrefixFile("drive_c/" + name[(name.IndexOf(":/", StringComparison.Ordinal) + 2)..]);

        var decision = SteamProcessAttribution.Evaluate(library.Target, Before,
            new([Client, Row(22, [argument])], true));

        Assert.Empty(decision.Candidates);
        Assert.Null(decision.Identity);
    }

    [Fact]
    public void Native_host_executable_stays_primary_over_a_windows_argument()
    {
        using var library = new ProtonLibrary();
        library.CreateForeignFile("Other", "Other.exe");
        // A real native binary under the installdir already matches; the extra argv is not needed.
        var decision = SteamProcessAttribution.Evaluate(library.Target, Before,
            new([Client, Entry(22, 2, 20, library.Game, ["S:\\steamapps\\common\\Other\\Other.exe"])], true));

        Assert.Null(decision.Rejection);
        var candidate = Assert.Single(decision.Candidates);
        Assert.Equal(library.Game, candidate.GamePath);
        Assert.Null(candidate.WindowsArgument);
    }

    [Fact]
    public void Proton_launch_with_only_wrapper_processes_has_no_candidate_and_is_unknown()
    {
        using var library = new ProtonLibrary();
        ExternalProcessEntry[] sample =
        [
            Client,
            Row(20, ["S:\\steamapps\\common\\Hades\\x64\\Hades.exe"], "/steam/pv-adverb"),
            Row(21, ["S:\\steamapps\\common\\Hades\\x64\\Hades.exe"], "/steam/wineserver"),
            Row(23, ["S:\\steamapps\\common\\Hades\\x64\\Hades.exe"], "/usr/bin/python3"),
            Row(24, ["S:\\steamapps\\common\\Hades\\x64\\Hades.exe"], "/steam/steam.exe"),
        ];

        var decision = SteamProcessAttribution.Evaluate(library.Target, Before, new(sample, true));
        Assert.Empty(decision.Candidates);
        Assert.Contains("no fresh installdir/prefix game process", decision.Rejection);
        Assert.Null(SteamProcessAttribution.Find(library.Target, Before, new(sample, true)));
    }

    [Fact]
    public void Game_path_in_a_later_argument_does_not_identify_a_wrapper_process()
    {
        using var library = new ProtonLibrary();
        library.CreatePrefixFile("drive_c/windows/system32/services.exe");
        var row = new ExternalProcessEntry(
            new ProcessEntry(new ProcessIdentity(22, 2, 20), 1, true), Preloader,
            [
                "C:\\windows\\system32\\services.exe",
                "S:\\steamapps\\common\\Hades\\x64\\Hades.exe",
            ]);
        var decision = SteamProcessAttribution.Evaluate(library.Target, Before, new([Client, row], true));
        Assert.Empty(decision.Candidates);
        Assert.Null(decision.Identity);
    }

    [Fact]
    public void Unreadable_command_line_is_not_guessed()
    {
        using var library = new ProtonLibrary();
        var decision = SteamProcessAttribution.Evaluate(library.Target, Before,
            new([Client, Entry(22, 2, 20, Preloader)], true));

        Assert.Empty(decision.Candidates);
        Assert.Null(decision.Identity);
    }

    [Fact]
    public async Task Proton_session_signals_only_the_resolved_windows_identity()
    {
        using var library = new ProtonLibrary();
        var host = new ProtonHost();
        await using var launcher = new SteamProcessLauncher(host, library.Target, new()
        {
            SteamExecutable = "/usr/bin/steam", ProtocolOpener = "/usr/bin/xdg-open",
            PollInterval = TimeSpan.FromMilliseconds(1), AttributionWindow = TimeSpan.FromSeconds(1),
        });

        var game = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
        Assert.Equal(22, game.Identity.ProcessId);
        Assert.Equal(library.Game, launcher.AttributedGames.Single().GamePath);
        Assert.Equal(WindowsGame, launcher.AttributedGames.Single().WindowsArgument);

        await launcher.StopAsync(game, CancellationToken.None);
        Assert.Equal([22], host.Signalled.Select(identity => identity.ProcessId));
        Assert.Equal([WindowsGame], host.SignalArguments);
        Assert.True(launcher.StopReport!.IsDegraded); // pv-adverb survives unidentified-unreadable
    }

    private static ExternalProcessEntry Row(int pid, string[] argv,
        string executable = Preloader) => new(new(new(pid, 2, 20), 1, true), executable, argv);

    private static ExternalProcessEntry Entry(int pid, long ticks, int group, string executable,
        IReadOnlyList<string>? commandLine = null) =>
        new(new(new(pid, ticks, group), 1, true), executable, commandLine);

    private sealed class ProtonHost : IExternalProcessHost
    {
        public List<ProcessIdentity> Signalled { get; } = [];
        public List<string?> SignalArguments { get; } = [];
        public List<ExternalProcessEntry> Rows { get; } = [Client];

        public Task<ExternalProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ExternalProcessSnapshot(Rows.ToArray(), true));

        public Task<int> RunAsync(LaunchRequest request, CancellationToken cancellationToken)
        {
            if (!Rows.Any(row => row.Process.Identity.ProcessId == 22))
            {
                Rows.Add(new(new(new(20, 2, 20), 1, true), "/steam/pv-adverb", [WindowsGame]));
                Rows.Add(new(new(new(22, 2, 20), 1, true), Preloader,
                    [WindowsGame, "-foo"]));
            }

            return Task.FromResult(0);
        }

        public Task SignalAsync(ProcessIdentity identity, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unscoped stop forbidden.");

        public Task SignalAsync(ProcessIdentity identity, string expectedExecutable,
            CancellationToken cancellationToken) => SignalAsync(identity, expectedExecutable, null, cancellationToken);

        public Task SignalAsync(ProcessIdentity identity, string expectedExecutable,
            string? expectedCommandLineArgument, CancellationToken cancellationToken)
        {
            Assert.Equal(Preloader, expectedExecutable);
            Signalled.Add(identity);
            SignalArguments.Add(expectedCommandLineArgument);
            Rows.RemoveAll(row => row.Process.Identity == identity);
            return Task.CompletedTask;
        }
    }

    /// <summary>A real library, installdir, prefix and dosdevices map, so mapping is proven end to end.</summary>
    private sealed class ProtonLibrary : IDisposable
    {
        public ProtonLibrary(string drive = "s")
        {
            Root = Directory.CreateTempSubdirectory("playnite-p5-008d-").FullName;
            SteamRoot = Path.Combine(Root, "SteamLibrary");
            Install = Path.Combine(SteamRoot, "steamapps", "common", "Hades");
            Directory.CreateDirectory(Path.Combine(Install, "x64"));
            Game = Path.Combine(Install, "x64", "Hades.exe");
            File.WriteAllText(Game, "game");
            Prefix = Path.Combine(SteamRoot, "steamapps", "compatdata", "1145360", "pfx");
            var dosdevices = Path.Combine(Prefix, "dosdevices");
            Directory.CreateDirectory(dosdevices);
            var letter = char.ToLowerInvariant(drive.TrimEnd(':')[0]);
            Directory.CreateSymbolicLink(Path.Combine(dosdevices, $"{letter}:"), SteamRoot);
            if (letter != 's')
            {
                Directory.CreateSymbolicLink(Path.Combine(dosdevices, "s:"), SteamRoot);
            }

            if (letter != 't')
            {
                Directory.CreateSymbolicLink(Path.Combine(dosdevices, "t:"), SteamRoot);
            }

            Directory.CreateSymbolicLink(Path.Combine(dosdevices, "z:"), HostRoot);
            Directory.CreateSymbolicLink(Path.Combine(dosdevices, "c:"), Path.Combine("..", "drive_c"));
            Directory.CreateDirectory(Path.Combine(Prefix, "drive_c"));
            Directory.CreateDirectory(Path.Combine(HostRoot, "etc"));
            File.WriteAllText(Path.Combine(HostRoot, "etc", "hostname"), "host");
        }

        public string Root { get; }
        public string SteamRoot { get; }
        public string HostRoot => Path.Combine(Root, "hostroot");
        public string Install { get; }
        public string Game { get; }
        public string Prefix { get; }

        public SteamLaunchTarget Target => new(new SteamApp(1145360, "Hades", "Hades", SteamRoot,
            SteamAppState.Installed, false, null, null, 4), Prefix);

        public void CreateForeignFile(string directory, string name)
        {
            var path = Path.Combine(SteamRoot, "steamapps", "common", directory);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, name), "other");
        }

        public void CreatePrefixFile(string relative)
        {
            var path = Path.Combine(Prefix, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "wrapper");
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}

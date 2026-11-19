using System.Diagnostics;
using Playnite.Platform.Linux;
using Playnite.Platform.Sessions;
using Playnite.Steam;
using Xunit;

namespace Playnite.Core.Tests.Steam;

public sealed class SteamAcceptanceSurvivorTests
{
    private static readonly SteamFileSystem Files = new(new LinuxBoundedFileReader());

    [Theory]
    [InlineData("wineserver", null, null)]
    [InlineData(null, "/proton/files/bin/wineserver64", null)]
    [InlineData(null, "/proton/wine64-preloader", "C:\\windows\\system32\\services.exe")]
    [InlineData("rundll32.exe", null, null)]
    public void Fresh_daemonized_wine_process_is_a_survivor_without_group_or_ancestry(
        string? name, string? executable, string? argv0)
    {
        var original = Entry(new(41, 10, 4), name, executable, argv0);
        var movedOriginal = original with { Process = new(new(41, 10, 41), 1, true) };
        var daemon = Entry(new(42, 31, 42), name, executable, argv0);
        var deadDaemon = daemon with { Process = new(new(43, 32, 43), 1, false) };
        var unrelated = Entry(new(44, 33, 44), "other", "/usr/bin/other", null);
        var game = new ProcessIdentity(40, 23, 40);

        var survivors = SteamAcceptanceSurvivorScan.Find(Target("/steam"), Files, new([original], true),
            new([movedOriginal, daemon, deadDaemon, unrelated], true), game.ProcessGroupId, [game]);

        Assert.Same(daemon, Assert.Single(survivors).Entry);
    }

    [Fact]
    public void Reused_pid_with_new_start_ticks_is_fresh()
    {
        var old = Entry(new(41, 10, 41), "wineserver", null, null);
        var replacement = Entry(new(41, 33, 41), "wineserver", null, null);

        var survivors = SteamAcceptanceSurvivorScan.Find(Target("/steam"), Files, new([old], true),
            new([replacement], true), 40, []);

        Assert.Same(replacement, Assert.Single(survivors).Entry);
    }

    [Fact]
    public async Task Detached_worker_with_relative_argv_under_its_install_directory_is_a_survivor()
    {
        var library = Directory.CreateTempSubdirectory();
        try
        {
            var target = Target(library.FullName);
            Directory.CreateDirectory(target.App.InstallPath);
            await File.WriteAllTextAsync(Path.Combine(target.App.InstallPath, "worker.py"), """
                import os, select, sys
                os.setsid()
                print("ready", flush=True)
                select.select([sys.stdin], [], [], 30)
                """, TestContext.Current.CancellationToken);
            var start = new ProcessStartInfo("/usr/bin/python3")
            {
                UseShellExecute = false,
                WorkingDirectory = target.App.InstallPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                ArgumentList = { "./worker.py" },
            };
            using var worker = Assert.IsType<Process>(Process.Start(start));
            try
            {
                Assert.Equal("ready", await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
                var host = new LinuxExternalProcessHost();
                var after = await host.SnapshotAsync(captureWorkingDirectories: true, TestContext.Current.CancellationToken);
                var row = Assert.Single(after.Processes, entry => entry.Process.Identity.ProcessId == worker.Id);
                Assert.Equal(worker.Id, row.Process.Identity.ProcessGroupId);
                Assert.Equal(["/usr/bin/python3", "./worker.py"], row.CommandLine);

                var survivors = SteamAcceptanceSurvivorScan.Find(target, Files, new([], true),
                    after with { Processes = [row] }, Environment.ProcessId, []);

                Assert.Same(row, Assert.Single(survivors).Entry);
                Assert.False(worker.HasExited);
            }
            finally
            {
                worker.StandardInput.Close();
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            library.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Detached_worker_whose_relative_argv_target_was_unlinked_is_a_survivor()
    {
        var library = Directory.CreateTempSubdirectory();
        try
        {
            var target = Target(library.FullName);
            Directory.CreateDirectory(target.App.InstallPath);
            var script = Path.Combine(target.App.InstallPath, "worker.py");
            await File.WriteAllTextAsync(script, """
                import os, select, sys
                os.setsid()
                print("ready", flush=True)
                select.select([sys.stdin], [], [], 30)
                """, TestContext.Current.CancellationToken);
            var start = new ProcessStartInfo("/usr/bin/python3")
            {
                UseShellExecute = false,
                WorkingDirectory = target.App.InstallPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                ArgumentList = { "./worker.py" },
            };
            using var worker = Assert.IsType<Process>(Process.Start(start));
            try
            {
                Assert.Equal("ready", await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
                File.Delete(script);
                Assert.False(File.Exists(script));

                var host = new LinuxExternalProcessHost();
                var after = await host.SnapshotAsync(captureWorkingDirectories: true, TestContext.Current.CancellationToken);
                var row = Assert.Single(after.Processes, entry => entry.Process.Identity.ProcessId == worker.Id);
                Assert.Equal(["/usr/bin/python3", "./worker.py"], row.CommandLine);
                Assert.Equal(Path.Combine(target.App.InstallPath, "worker.py"), Path.GetFullPath(Path.Combine(row.WorkingDirectory!, "./worker.py")));

                // The pre-fix scan combined the relative argument with the worker's own cwd
                // and then required realpath to resolve it. With the script unlinked, realpath
                // fails and that scan returns no app-path evidence, so the worker survives
                // unreported. The fixed scan judges the lexical cwd-relative path instead.
                Assert.False(PreFixScanFindsAppArg(target, Files, row));

                var survivors = SteamAcceptanceSurvivorScan.Find(target, Files, new([], true),
                    after with { Processes = [row] }, Environment.ProcessId, []);

                Assert.Same(row, Assert.Single(survivors).Entry);
                Assert.False(worker.HasExited);
            }
            finally
            {
                worker.StandardInput.Close();
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            library.Delete(recursive: true);
        }
    }

    [Fact]
    public void Executable_and_argv_paths_under_the_app_roots_count_even_for_wrappers()
    {
        var library = Directory.CreateTempSubdirectory();
        try
        {
            var target = Target(library.FullName);
            var prefix = target.ProtonPrefix!;
            var system = Path.Combine(prefix, "drive_c", "windows", "system32");
            Directory.CreateDirectory(system);
            var drives = Directory.CreateDirectory(Path.Combine(prefix, "dosdevices"));
            Directory.CreateSymbolicLink(Path.Combine(drives.FullName, "c:"), "../drive_c");
            File.WriteAllText(Path.Combine(system, "services.exe"), "");
            var install = Directory.CreateDirectory(target.App.InstallPath);
            var executable = Path.Combine(system, "arbitrary.exe");
            File.WriteAllText(executable, "");
            var installAlias = Path.Combine(library.FullName, "install-alias");
            Directory.CreateSymbolicLink(installAlias, install.FullName);

            var prefixExecutable = Entry(new(41, 12, 41), "tool", executable, null);
            var windowsArg = Entry(new(42, 13, 42), "wrapper", "/usr/bin/python3", null) with
            {
                CommandLine = ["wrapper", "C:\\windows\\system32\\services.exe"],
            };
            var hostArg = Entry(new(43, 14, 43), "wrapper", "/usr/bin/python3", $"--path={installAlias}");
            var siblingArg = Entry(new(44, 15, 44), "wrapper", "/usr/bin/python3", $"{install.FullName}-unrelated");

            var survivors = SteamAcceptanceSurvivorScan.Find(target, Files, new([], true),
                new([prefixExecutable, windowsArg, hostArg, siblingArg], true), 40, []);

            Assert.Equal(new[] { prefixExecutable, windowsArg, hostArg }, survivors.Select(row => row.Entry));
            Assert.All(survivors, row => Assert.Contains("app install or prefix path", row.Reason));
        }
        finally
        {
            library.Delete(recursive: true);
        }
    }

    private static SteamLaunchTarget Target(string library) =>
        new(new(1145360, "Hades", "Hades", library, SteamAppState.Installed, true, null, null, 0),
            Path.Combine(library, "steamapps", "compatdata", "1145360", "pfx"));

    private static ExternalProcessEntry Entry(ProcessIdentity identity, string? name, string? executable, string? argv0) =>
        new(new(identity, 1, true), executable, argv0 is null ? null : [argv0]) { ProcessName = name };

    // The pre-fix argv predicate, kept only for the unlinked-target regression: a relative
    // argument was combined with the process cwd and then had to survive realpath, so a
    // missing target produced null and no survivor.
    private static bool PreFixScanFindsAppArg(SteamLaunchTarget target, ISteamFileSystem files, ExternalProcessEntry row)
    {
        foreach (var argument in row.CommandLine ?? [])
        {
            var separator = argument.IndexOf('=');
            var path = separator > 2 && argument.StartsWith("--", StringComparison.Ordinal)
                ? argument[(separator + 1)..]
                : argument;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            var absolute = Path.IsPathFullyQualified(path);
            if (!absolute)
            {
                if (ProtonCommandLine.ResolveAppPath(target, path) is not null)
                {
                    return true;
                }

                if (row.WorkingDirectory is null)
                {
                    continue;
                }

                path = Path.Combine(row.WorkingDirectory, path);
            }

            var resolved = files.GetRealPath(path);
            if (resolved is not null && (resolved == target.App.InstallPath || resolved == target.ProtonPrefix
                || SteamProcessAttribution.Under(resolved, target.App.InstallPath)
                || SteamProcessAttribution.Under(resolved, target.ProtonPrefix)))
            {
                return true;
            }
        }

        return false;
    }
}

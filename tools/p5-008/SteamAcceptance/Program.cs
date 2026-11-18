using Playnite.Platform.Linux;
using Playnite.Platform.Sessions;
using Playnite.Steam;

// Owner-only acceptance. Not a test project and never invoked by CI.
if (args.Length < 4)
{
    Console.Error.WriteLine("Default: read-only inspection; no launch or signal. Save data before explicitly consenting to launch and graceful stop.");
    Console.Error.WriteLine("Usage: steam-acceptance.sh NATIVE_LIBRARY NATIVE_APPID PROTON_LIBRARY PROTON_APPID [--allow-launch --allow-stop] [--uri] [--native-exe ABS_PATH] [--proton-exe ABS_PATH]");
    return 2;
}

try
{
    ValidateOptions();
    var allowLaunch = args.Contains("--allow-launch", StringComparer.Ordinal);
    var allowStop = args.Contains("--allow-stop", StringComparer.Ordinal);
    if (allowLaunch != allowStop)
    {
        throw new ArgumentException("Live acceptance requires both --allow-launch and --allow-stop: consent to four launches and graceful stop/cancel cleanup for these two selected apps only.");
    }

    Console.WriteLine(allowLaunch
        ? "CONSENT: four selected-app launches and graceful SIGTERM stop/cancel/disposal cleanup permitted. Never SIGKILL; uncooperative games require manual Steam Stop."
        : "DRY RUN: read-only inspection. No Steam command, launch or signal will be issued.");
    var uri = args.Contains("--uri", StringComparer.Ordinal);
    var steam = uri ? null : FindExecutable("steam");
    var opener = FindExecutable("xdg-open") ?? "/usr/bin/xdg-open";
    if (!uri && steam is null)
    {
        Console.WriteLine("Steam executable unavailable; using protocol-handler fallback.");
    }

    var host = new ConsentProcessHost(new ReportingHost(), allowLaunch, allowStop);
    var initial = await host.SnapshotAsync(CancellationToken.None);
    var clients = initial.Processes.Where(row => row.Process.IsRunning && SteamProcessAttribution.IsSteamClient(row.Executable))
        .Select(row => row.Process.Identity).ToArray();
    if (allowLaunch && (!initial.IsComplete || clients.Length == 0))
    {
        throw new InvalidOperationException("Start Steam and log in first; a complete baseline and existing client are required for this acceptance run.");
    }

    foreach (var (label, library, id, executable) in new[]
    {
        ("native", args[0], args[1], Optional("--native-exe")),
        ("Proton", args[2], args[3], Optional("--proton-exe")),
    })
    {
        var files = new SteamFileSystem(new LinuxBoundedFileReader());
        var canonicalLibrary = files.GetRealPath(library) ?? throw new IOException("Library does not exist.");
        if (!uint.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var appId))
        {
            throw new ArgumentException("App id must be uint decimal.");
        }

        var manifestFile = Path.Combine(canonicalLibrary, "steamapps", $"appmanifest_{appId}.acf");
        var bytes = files.ReadFile(manifestFile, KeyValues.DefaultMaxBytes).Bytes ?? throw new IOException("Manifest could not be read.");
        var manifest = SteamManifestParser.Parse(bytes, manifestFile).Value ?? throw new IOException("Manifest rejected.");
        var app = manifest.ToApp(canonicalLibrary, true, Directory.Exists(Path.Combine(canonicalLibrary, "steamapps", "common", manifest.InstallDir)), false);
        // Resolve the full install directory too, including symlinks below the library root.
        var canonicalInstall = files.GetRealPath(app.InstallPath) ?? throw new IOException("Install directory unavailable.");
        if (canonicalInstall != app.InstallPath)
        {
            throw new InvalidOperationException("Install directory has a symlink alias; supply its canonical library or inspect manually before acceptance.");
        }

        var prefixRoot = Path.Combine(canonicalLibrary, "steamapps", "compatdata", appId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var prefix = files.GetRealPath(Path.Combine(prefixRoot, "pfx"));
        Console.WriteLine($"{label} app={appId}; selection evidence={SteamExecutionSelection.Evaluate(manifest, null, prefix is not null, File.Exists(Path.Combine(prefixRoot, "config_info"))).Kind}; Steam owns actual selection.");
        var canonicalOverride = executable is null ? null : files.GetRealPath(executable) ?? throw new IOException("Override executable unavailable.");
        if (canonicalOverride is not null)
        {
            Console.WriteLine("Explicit executable override enabled: attribution is an owner-selected heuristic.");
        }

        var target = new SteamLaunchTarget(app, prefix, canonicalOverride);
        if (canonicalOverride is not null && !SteamProcessAttribution.Matches(target, canonicalOverride))
        {
            throw new ArgumentException("Executable selector must remain under this game's install directory/prefix and must not be a shell, system binary or helper.");
        }

        if (!allowLaunch)
        {
            foreach (var row in initial.Processes.Where(row => row.Process.IsRunning && SteamProcessAttribution.Matches(target, row.Executable)))
            {
                Console.WriteLine($"READ ONLY existing candidate pid={row.Process.Identity.ProcessId} start_ticks={row.Process.Identity.StartTimeTicks} pgid={row.Process.Identity.ProcessGroupId}; not fresh attribution");
            }

            continue;
        }

        foreach (var cancel in new[] { false, true })
        {
            Console.WriteLine($"Launching {label} app={appId} for {(cancel ? "cancel" : "stop")} acceptance.");
            await using var launcher = new SteamProcessLauncher(host, target, new()
            {
                SteamExecutable = steam, ProtocolOpener = opener,
                Progress = new InlineProgress<SteamAttributionProgress>(progress => Console.WriteLine(
                    $"ATTRIBUTION elapsed={progress.Elapsed.TotalSeconds:F1}s window={progress.Window.TotalSeconds:F0}s {progress.Decision}")),
            });
            var process = await launcher.StartAsync(launcher.CreateRequest(), CancellationToken.None);
            Console.WriteLine($"ATTRIBUTED pid={process.Identity.ProcessId} start_ticks={process.Identity.StartTimeTicks} pgid={process.Identity.ProcessGroupId}");
            await using var tracker = new GameSessionTracker(launcher, launcher);
            tracker.StateChanged += (_, change) => Console.WriteLine($"TRACK {change.State} degraded={change.IsDegraded} reason={change.Reason}");
            using var cancellation = new CancellationTokenSource();
            var tracking = tracker.TrackAsync(process, new SessionOptions(), cancellation.Token);
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (tracker.State != GameSessionState.Running || tracker.IsDegraded)
            {
                throw new InvalidOperationException("Tracking did not establish a nondegraded Running state; inspect Steam manually.");
            }

            if (cancel)
            {
                cancellation.Cancel();
                try
                {
                    await tracking;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    // Expected only after the tracker attempted group cleanup.
                }
            }
            else
            {
                await tracker.StopAsync(CancellationToken.None);
                await tracking;
            }

            var expected = cancel ? GameSessionState.Cancelled : GameSessionState.Stopped;
            if (tracker.State != expected)
            {
                throw new InvalidOperationException($"Expected {expected}, got {tracker.State}.");
            }

            await tracker.DisposeAsync();
            await launcher.DisposeAsync();
            var after = await host.SnapshotAsync(CancellationToken.None);
            if (!after.IsComplete || after.Processes.Any(row => row.Process.IsRunning
                && (SteamProcessAttribution.Matches(target, row.Executable)
                    || launcher.AttributedIdentities.Concat(launcher.ObservedDescendants)
                        .Any(identity => SteamProcessAttribution.SameProcess(identity, row.Process.Identity)))))
            {
                throw new InvalidOperationException("No-orphan assertion failed: game identity remains live or unverifiable.");
            }

            if (clients.Any(client => !after.Processes.Any(row => row.Process.IsRunning && row.Process.Identity == client)))
            {
                throw new InvalidOperationException("An original Steam client/helper identity disappeared; inspect before proceeding.");
            }

            var report = launcher.StopReport ?? throw new InvalidOperationException("Missing game-exit proof.");
            Console.WriteLine($"STOP REPORT degraded={report.IsDegraded} reason={report.Reason}");
            foreach (var helper in report.HelperSurvivors)
            {
                Console.WriteLine($"HELPER SURVIVOR pid={helper.ProcessId} start_ticks={helper.StartTimeTicks} pgid={helper.ProcessGroupId}; unsignalled");
            }
            Console.WriteLine($"PASS {label} {expected}: attributed game identities gone, original Steam client identities retained; no group/client/helper signal.");
        }
    }

    Console.WriteLine(allowLaunch
        ? "Owner acceptance completed. This does not verify Flatpak, launch options, offline mode, or Phase 5 exit."
        : "DRY RUN completed: no launches or signals; no acceptance evidence collected.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"NOT ACCEPTED: {exception.Message}");
    Console.Error.WriteLine("Inspect Steam now and use Steam's Stop for any still-running selected game. No unidentified group is killed by this tool.");
    return 1;
}

string? Optional(string option)
{
    var index = Array.FindIndex(args, 4, value => value == option);
    return index < 0 ? null : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing option value.");
}

void ValidateOptions()
{
    var seen = new HashSet<string>(StringComparer.Ordinal);
    for (var index = 4; index < args.Length; index++)
    {
        var option = args[index];
        if (!seen.Add(option))
        {
            throw new ArgumentException("Duplicate option.");
        }

        if (option is "--native-exe" or "--proton-exe")
        {
            if (++index >= args.Length || !Path.IsPathFullyQualified(args[index]))
            {
                throw new ArgumentException("Executable selectors require an absolute path.");
            }
        }
        else if (option is not ("--allow-launch" or "--allow-stop" or "--uri"))
        {
            throw new ArgumentException("Unknown option; nothing was launched.");
        }
    }
}

static string? FindExecutable(string name) => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
    .Split(Path.PathSeparator).Where(Path.IsPathFullyQualified).Select(directory => Path.Combine(directory, name))
    .FirstOrDefault(File.Exists);

sealed class ReportingHost : IExternalProcessHost
{
    private readonly LinuxExternalProcessHost host = new();
    public Task<ExternalProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken) => host.SnapshotAsync(cancellationToken);
    public Task<int> RunAsync(LaunchRequest request, CancellationToken cancellationToken)
    {
        Console.WriteLine($"REQUEST {request.Executable}: [{string.Join(", ", request.Arguments)}]");
        return host.RunAsync(request, cancellationToken);
    }

    public Task SignalAsync(ProcessIdentity identity, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Unscoped signal forbidden in Steam acceptance.");

    public Task SignalAsync(ProcessIdentity identity, string expectedExecutable, CancellationToken cancellationToken)
    {
        Console.WriteLine($"SIGTERM pidfd pid={identity.ProcessId} start_ticks={identity.StartTimeTicks} pgid={identity.ProcessGroupId} exe={expectedExecutable}");
        return host.SignalAsync(identity, expectedExecutable, cancellationToken);
    }
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

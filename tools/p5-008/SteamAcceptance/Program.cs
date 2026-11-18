using Playnite.Platform.Linux;
using Playnite.Platform.Sessions;
using Playnite.Steam;

// Owner-only acceptance. Not a test project and never invoked by CI.
if (args.Length < 5 || args[0] != "--confirm-no-unsaved-data")
{
    Console.Error.WriteLine("WARNING: force-stops the selected games, including on cancellation. Save all game data first.");
    Console.Error.WriteLine("Usage: steam-acceptance.sh --confirm-no-unsaved-data NATIVE_LIBRARY NATIVE_APPID PROTON_LIBRARY PROTON_APPID [--uri] [--native-exe ABS_PATH] [--proton-exe ABS_PATH]");
    return 2;
}

try
{
    var uri = args.Contains("--uri", StringComparer.Ordinal);
    var steam = uri ? null : FindExecutable("steam");
    var opener = FindExecutable("xdg-open") ?? "/usr/bin/xdg-open";
    if (!uri && steam is null)
    {
        Console.WriteLine("Steam executable unavailable; using protocol-handler fallback.");
    }

    var host = new ReportingHost();
    var initial = await host.SnapshotAsync(CancellationToken.None);
    var clients = initial.Processes.Where(row => row.Process.IsRunning && SteamProcessAttribution.IsSteamClient(row.Executable))
        .Select(row => row.Process.Identity).ToArray();
    if (!initial.IsComplete || clients.Length == 0)
    {
        throw new InvalidOperationException("Start Steam and log in first; a complete baseline and existing client are required for this acceptance run.");
    }

    foreach (var (label, library, id, executable) in new[]
    {
        ("native", args[1], args[2], Optional("--native-exe")),
        ("Proton", args[3], args[4], Optional("--proton-exe")),
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

        foreach (var cancel in new[] { false, true })
        {
            Console.WriteLine($"Launching {label} app={appId} for {(cancel ? "cancel" : "stop")} acceptance.");
            await using var launcher = new SteamProcessLauncher(host, new(app, prefix, canonicalOverride), new()
            {
                SteamExecutable = steam, ProtocolOpener = opener, AttributionWindow = TimeSpan.FromSeconds(60),
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

            var after = await host.SnapshotAsync(CancellationToken.None);
            if (!after.IsComplete || after.Processes.Any(row => row.Process.IsRunning && row.Process.Identity.ProcessGroupId == process.Identity.ProcessGroupId))
            {
                throw new InvalidOperationException("No-orphan assertion failed: group remains live or unverifiable.");
            }

            if (clients.Any(client => !after.Processes.Any(row => row.Process.IsRunning && row.Process.Identity == client)))
            {
                throw new InvalidOperationException("An original Steam client/helper identity disappeared; inspect before proceeding.");
            }

            Console.WriteLine($"PASS {label} {expected}: attributed group empty, original Steam client identities retained.");
        }
    }

    Console.WriteLine("Owner acceptance completed. This does not verify Flatpak, launch options, offline mode, or Phase 5 exit.");
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
    var index = Array.IndexOf(args, option);
    return index < 0 ? null : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing option value.");
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
    {
        Console.WriteLine($"STOP pid={identity.ProcessId} start_ticks={identity.StartTimeTicks} pgid={identity.ProcessGroupId}");
        return host.SignalAsync(identity, cancellationToken);
    }
}

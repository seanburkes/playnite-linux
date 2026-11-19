using System.Globalization;
using Playnite.Core.Tests.Serialization;
using Playnite.Core.Tests.Sessions;
using Playnite.Platform.Sessions;
using Xunit;

namespace Playnite.Core.Tests.Refinement;

public sealed class GameSessionRefinementTests
{
    private static readonly string CorpusPath =
        RepositoryPaths.Combine("tests/Playnite.Core.Tests/p5-005/GameSession.traces.tsv");
    private static readonly string[] Corpus = File.ReadAllLines(CorpusPath);

    [Fact]
    public void Tlc_corpus_matches_the_frozen_model_and_configuration()
    {
        Assert.Equal(new[] { "GameSession.tla", "GameSession.cfg" }.Select(
            file => RepositoryPaths.Sha256(RepositoryPaths.Combine("docs/formal", file))),
            Corpus[0].Split('\t'));
        Assert.Equal("cfe899ea978e50dcd92d24a00c3b68955cc13c2c069f44b5fedae5c52a6bf5da",
            RepositoryPaths.Sha256(CorpusPath));
        Assert.Equal(6, Corpus.Length - 1);
    }

    [Theory]
    [InlineData(Stimulus.NaturalExit, "Start LaunchOk Stop StopOk", 1)]
    [InlineData(Stimulus.ManualStop, "Start LaunchOk Stop StopOk", 1)]
    [InlineData(Stimulus.ObserverFailure, "Start LaunchOk Stop Crash", 0)]
    public async Task Tracker_replays_the_TLC_action_path(Stimulus input, string actions, int secondsDuringReap)
    {
        var expected = StatesFor(actions);
        // R = live root, I = incomplete sample. Exhaust the binary input words up
        // to length two before natural exit or fault. Manual fallback never polls.
        string[] prefixes = input == Stimulus.ManualStop ? [""] : ["", "R", "I", "RR", "RI", "IR", "II"];
        foreach (var prefix in prefixes)
        {
            var actual = await Observe(input, secondsDuringReap, prefix);
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData(Stimulus.PreCancelled, "Starting/TRUE/0", "Stopping/TRUE/0")]
    [InlineData(Stimulus.CancelRunning, "Stopping/TRUE/0", "Cancelled/FALSE/0")]
    [InlineData(Stimulus.Unreaped, "Stopping/TRUE/0", "Running/TRUE/0")]
    [InlineData(Stimulus.NaturalExit, "Stopping/TRUE/0", "Stopped/FALSE/0")]
    public async Task Concrete_trace_exhibits_a_reported_model_gap(Stimulus input, string from, string to)
    {
        var actual = await Observe(input, secondsDuringReap: 0);
        var legal = Corpus.Skip(1).SelectMany(row => Edges(States(row))).ToHashSet();
        var violations = Edges(actual).Where(edge => !legal.Contains(edge)).ToArray();
        if (input == Stimulus.PreCancelled)
        {
            Assert.Equal(new[] { (from, to), (to, "Cancelled/FALSE/0") }, violations);
        }
        else
        {
            Assert.Equal(new[] { (from, to) }, violations);
        }
    }

    private static string[] StatesFor(string actions)
    {
        var row = Assert.Single(Corpus.Skip(1), row =>
            string.Join(' ', row.Split('\t').Where((_, i) => i % 2 == 1)) == actions);
        return States(row);
    }

    private static string[] States(string row) =>
        row.Split('\t').Where((_, i) => i % 2 == 0).ToArray();

    private static IEnumerable<(string, string)> Edges(string[] states) =>
        states.Zip(states.Skip(1));

    public enum Stimulus { NaturalExit, ManualStop, ObserverFailure, PreCancelled, CancelRunning, Unreaped }

    private sealed class Clock : TimeProvider
    {
        public long Seconds { get; set; } = 1;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }

    private static string Sample(GameSessionState state, bool held, TimeSpan playtime) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{state}/{(held ? "TRUE" : "FALSE")}/{playtime.TotalSeconds:R}");

    private static async Task<string[]> Observe(Stimulus input, int secondsDuringReap, string prefix = "")
    {
        using var cancellation = new CancellationTokenSource();
        var process = new FakeGameProcess();
        await using var launcher = new FakeProcessLauncher(process) { FailAllStops = input == Stimulus.Unreaped };
        var observer = new ScriptedProcessObserver(call =>
        {
            if (call <= prefix.Length)
            {
                return prefix[call - 1] == 'R' ? ProcessSnapshots.Running(process.Identity) : ProcessSnapshots.Incomplete();
            }
            var step = call - prefix.Length;
            if (input == Stimulus.ObserverFailure || step > 3)
            {
                throw new InvalidOperationException("scripted observation failure");
            }
            if (input == Stimulus.CancelRunning)
            {
                cancellation.Cancel();
            }
            return step == 1 ? ProcessSnapshots.Running(process.Identity) : ProcessSnapshots.Stopped();
        });
        var clock = new Clock();
        await using var tracker = new GameSessionTracker(launcher, observer, new GatedSessionDelay(), clock);

        // Before handoff the fake process belongs to the driver. After handoff only a
        // successful launcher reap clears Running, independently of the tracker state.
        var samples = new List<string> { Sample(tracker.State, false, tracker.Playtime) };
        var last = tracker.State;
        var consistent = true;
        tracker.StateChanged += (_, change) =>
        {
            // The tracker isolates subscriber exceptions. Assert outside the callback.
            consistent &= change.PreviousState == last && change.State == tracker.State
                && change.Playtime == tracker.Playtime && change.Identity == process.Identity;
            last = change.State;
            samples.Add(Sample(change.State, process.Running, change.Playtime));
            if (change.State == GameSessionState.Stopping)
            {
                clock.Seconds += secondsDuringReap;
            }
        };

        var manual = input is Stimulus.ManualStop or Stimulus.Unreaped;
        if (input == Stimulus.PreCancelled)
        {
            cancellation.Cancel();
        }
        var tracking = tracker.TrackAsync(process, new SessionOptions { ExitObservationSupported = !manual },
            cancellation.Token);
        try
        {
            if (input == Stimulus.Unreaped)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => tracker.StopAsync(CancellationToken.None));
            }
            else if (manual)
            {
                Assert.True(await tracker.StopAsync(CancellationToken.None));
            }

            if (cancellation.IsCancellationRequested)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tracking);
            }
            else
            {
                await tracking.WaitAsync(TimeSpan.FromSeconds(10));
            }
            int suffixCalls = input switch
            {
                Stimulus.NaturalExit => 3, // root, empty, empty
                Stimulus.ObserverFailure or Stimulus.CancelRunning => 1,
                _ => 0,
            };
            Assert.Equal(prefix.Length + suffixCalls, observer.Calls);
            Assert.True(consistent, "Event and public state, playtime, identity or previous state disagree");
            Assert.Equal(samples[^1], Sample(tracker.State, process.Running, tracker.Playtime));
            // [Next]_vars admits stuttering only if the entire observed tuple is unchanged.
            return samples.Where((sample, i) => i == 0 || sample != samples[i - 1]).ToArray();
        }
        finally
        {
            launcher.FailAllStops = false;
        }
    }
}

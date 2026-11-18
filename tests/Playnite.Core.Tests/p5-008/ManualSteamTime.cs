namespace Playnite.Core.Tests.Steam;

internal sealed class ManualSteamTime : TimeProvider
{
    private readonly object gate = new();
    private readonly List<Timer> timers = [];
    private long ticks;
    private TaskCompletionSource pollScheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task PollScheduled { get { lock (gate) { return pollScheduled.Task; } } }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (gate) { return ticks; } }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (gate)
        {
            var timer = new Timer(this, callback, state);
            timer.Change(dueTime, period);
            timers.Add(timer);
            if (dueTime <= TimeSpan.FromSeconds(1)) { pollScheduled.TrySetResult(); }
            return timer;
        }
    }

    public void Advance(TimeSpan amount)
    {
        Timer[] ready;
        lock (gate)
        {
            ticks += amount.Ticks;
            pollScheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ready = timers.Where(timer => timer.Due <= ticks).ToArray();
            foreach (var timer in ready) { timer.Due = long.MaxValue; }
        }
        foreach (var timer in ready) { timer.Fire(); }
    }

    private sealed class Timer(ManualSteamTime clock, TimerCallback callback, object? state) : ITimer
    {
        public long Due { get; set; } = long.MaxValue;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) { throw new ArgumentException("Only one-shot timers are used by this seam."); }
            lock (clock.gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks;
                return true;
            }
        }
        public void Fire() => callback(state);
        public void Dispose() { lock (clock.gate) { Due = long.MaxValue; } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

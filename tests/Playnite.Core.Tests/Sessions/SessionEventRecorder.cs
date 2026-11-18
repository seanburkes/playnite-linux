using Playnite.Platform.Sessions;

namespace Playnite.Core.Tests.Sessions;

internal sealed class SessionEventRecorder : IDisposable
{
    private readonly GameSessionTracker tracker;
    private readonly List<SessionStateChanged> events = [];
    private readonly List<(Func<SessionStateChanged, bool> Predicate, TaskCompletionSource<SessionStateChanged> Completion)> waiters = [];

    public SessionEventRecorder(GameSessionTracker tracker)
    {
        this.tracker = tracker;
        tracker.StateChanged += OnStateChanged;
    }

    public IReadOnlyList<SessionStateChanged> Events
    {
        get
        {
            lock (events)
            {
                return events.ToArray();
            }
        }
    }

    public Task<SessionStateChanged> WaitForAsync(Func<SessionStateChanged, bool> predicate, TimeSpan? timeout = null)
    {
        lock (events)
        {
            var existing = events.FirstOrDefault(predicate);
            if (existing is not null)
            {
                return Task.FromResult(existing);
            }

            var completion = new TaskCompletionSource<SessionStateChanged>(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((predicate, completion));
            return completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5));
        }
    }

    public void Dispose() => tracker.StateChanged -= OnStateChanged;

    private void OnStateChanged(object? sender, SessionStateChanged change)
    {
        List<TaskCompletionSource<SessionStateChanged>> completed;
        lock (events)
        {
            events.Add(change);
            completed = waiters.Where(waiter => waiter.Predicate(change))
                .Select(waiter => waiter.Completion).ToList();
            waiters.RemoveAll(waiter => waiter.Predicate(change));
        }

        foreach (var waiter in completed)
        {
            waiter.TrySetResult(change);
        }
    }
}

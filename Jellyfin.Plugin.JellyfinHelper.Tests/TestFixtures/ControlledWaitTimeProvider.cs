namespace Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;

/// <summary>Timer deadlines advance only when the test explicitly fires them; cancellation stays independent.</summary>
internal sealed class ControlledWaitTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ControlledTimer> _timers = [];
    private readonly SemaphoreSlim _created = new(0);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ControlledTimer(callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        _created.Release();
        return timer;
    }

    internal Task WaitForDeadlineAsync(CancellationToken cancellationToken)
        => _created.WaitAsync(cancellationToken);

    internal void ExpireDeadlines()
    {
        ControlledTimer[] timers;
        lock (_gate)
        {
            timers = _timers.ToArray();
        }

        foreach (var timer in timers) timer.Fire();
    }

    /// <summary>Captures a callback already dispatched before disposal, independent of the timer's later disposed state.</summary>
    internal Action CaptureLatestCallback()
    {
        lock (_gate)
        {
            return _timers[^1].CaptureCallback();
        }
    }

    private sealed class ControlledTimer(TimerCallback callback, object? state) : ITimer
    {
        private int _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _disposed) == 0;

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        internal void Fire()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) callback(state);
        }

        internal Action CaptureCallback() => () => callback(state);
    }
}

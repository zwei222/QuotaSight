using QuotaSight.Infrastructure;

namespace QuotaSight.Tests;

public sealed class SchedulingTests
{
    [Fact]
    public async Task Refresh_scheduler_re_evaluates_interval_supplier_before_each_delay()
    {
        var interval = TimeSpan.FromMinutes(5);
        using var timeProvider = new ManualTimeProvider();
        var constructor = typeof(RefreshScheduler).GetConstructor([typeof(Func<TimeSpan>), typeof(TimeProvider)]);
        var scheduler = Assert.IsType<RefreshScheduler>(constructor?.Invoke([new Func<TimeSpan>(() => interval), timeProvider]));
        using var cancellation = new CancellationTokenSource();

        var running = scheduler.RunAsync(_ => ValueTask.CompletedTask, cancellation.Token).AsTask();
        var firstTimer = await timeProvider.WaitForTimerAsync();
        Assert.Equal(TimeSpan.FromMinutes(5), firstTimer.DueTime);

        interval = TimeSpan.FromMinutes(15);
        firstTimer.Fire();

        var secondTimer = await timeProvider.WaitForTimerAsync();
        Assert.Equal(TimeSpan.FromMinutes(15), secondTimer.DueTime);

        cancellation.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => running);
    }

    private sealed class ManualTimeProvider : TimeProvider, IDisposable
    {
        private readonly Queue<ManualTimer> timers = [];
        private readonly SemaphoreSlim timerCreated = new(0);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            lock (timers) timers.Enqueue(timer);
            timerCreated.Release();
            return timer;
        }

        public async Task<ManualTimer> WaitForTimerAsync()
        {
            await timerCreated.WaitAsync();
            lock (timers) return timers.Dequeue();
        }

        public void Dispose() => timerCreated.Dispose();
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        public TimeSpan DueTime { get; } = dueTime;
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Fire() => callback(state);
    }
}

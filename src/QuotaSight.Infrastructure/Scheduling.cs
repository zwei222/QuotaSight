using QuotaSight.Application;

namespace QuotaSight.Infrastructure;

public sealed class ExponentialBackoff
{
    private readonly TimeProvider timeProvider;
    private readonly Random random;
    private readonly TimeSpan cap;

    public ExponentialBackoff(TimeProvider timeProvider, Random? random = null, TimeSpan? cap = null)
    {
        this.timeProvider = timeProvider;
        this.random = random ?? new Random(0);
        this.cap = cap ?? TimeSpan.FromSeconds(30);
    }

    public TimeSpan Delay(int attempt, TimeSpan? retryAfter = null)
    {
        if (retryAfter is { } serverDelay)
        {
            return serverDelay > cap ? cap : serverDelay;
        }

        var exponential = Math.Min(cap.TotalMilliseconds, 250d * Math.Pow(2, attempt));
        var jitter = random.NextDouble() * Math.Min(250d, cap.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Min(cap.TotalMilliseconds, exponential + jitter));
    }

    public async ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> operation, Func<T, bool> retryable, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await operation(cancellationToken);
            if (!retryable(result) || attempt >= 5) return result;
            await Task.Delay(Delay(attempt), timeProvider, cancellationToken);
        }
    }
}

public sealed class AccountRefreshCoordinator
{
    private readonly Dictionary<string, Task> active = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public Task RunAsync(string account, Func<Task> operation)
    {
        lock (gate)
        {
            if (active.TryGetValue(account, out var existing)) return existing;
            var task = RunAndRelease(account, operation);
            active[account] = task;
            return task;
        }
    }

    private async Task RunAndRelease(string account, Func<Task> operation)
    {
        try { await operation(); }
        finally { lock (gate) active.Remove(account); }
    }
}

public sealed class RefreshScheduler : IRefreshScheduler, IDisposable
{
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan interval;

    public RefreshScheduler(TimeSpan interval, TimeProvider? timeProvider = null)
    {
        if (interval < TimeSpan.FromMinutes(5) || interval > TimeSpan.FromMinutes(15)) throw new ArgumentOutOfRangeException(nameof(interval));
        this.interval = interval;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask RunAsync(Func<CancellationToken, ValueTask> refresh, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await refresh(cancellationToken);
            await Task.Delay(interval, timeProvider, cancellationToken);
        }
    }
    public void Dispose() { }
}

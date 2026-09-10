namespace QuotaSight.UI;

public sealed class AppStartupGuard
{
    private readonly object gate = new();
    private Task? startupTask;

    public int StartCount { get; private set; }

    public Task StartOnceAsync(Func<CancellationToken, Task> startup, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (startupTask is not null) return startupTask;
            if (cancellationToken.IsCancellationRequested) return Task.CompletedTask;
            StartCount++;
            startupTask = startup(cancellationToken);
            return startupTask;
        }
    }
}

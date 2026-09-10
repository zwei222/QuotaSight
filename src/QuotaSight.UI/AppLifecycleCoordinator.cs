using Avalonia.Threading;

namespace QuotaSight.UI;

public static class ResidentModePolicy
{
    public static bool ShouldHideOnClose(bool effectiveResidentMode, bool trayCapability, bool exiting) =>
        effectiveResidentMode && trayCapability && !exiting;
}

public static class ShutdownPolicy
{
    public static bool ShouldCancelExternalRequest(bool coordinatedExitStarted, bool cleanupCompleted = false) => !cleanupCompleted;
}

public sealed class AppLifecycleCoordinator
{
    private readonly object gate = new();
    private readonly List<Task> tracked = [];
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<CancellationToken, Task> cleanup;
    private readonly bool marshalCleanup;
    private readonly CancellationTokenSource lifetime = new();
    private int exiting;
    private int cleanupStarted;

    public AppLifecycleCoordinator(Func<ValueTask> cleanup)
    {
        this.cleanup = _ => cleanup().AsTask();
        marshalCleanup = false;
    }
    public AppLifecycleCoordinator(Func<CancellationToken, Task> cleanup)
    {
        this.cleanup = cleanup;
        marshalCleanup = true;
    }
    public bool IsExiting => Volatile.Read(ref exiting) != 0;
    public bool IsCleanupCompleted => completion.Task.IsCompleted;
    public CancellationToken CancellationToken => lifetime.Token;

    public bool Track(Task? operation)
    {
        if (operation is null) return false;
        lock (gate)
        {
            if (IsExiting) return false;
            tracked.Add(operation);
            return true;
        }
    }

    public Task Run(Func<CancellationToken, Task> operation)
    {
        lock (gate)
        {
            if (IsExiting) return Task.CompletedTask;
            var task = operation(lifetime.Token);
            tracked.Add(task);
            return task;
        }
    }

    public Task Run(Func<Task> operation) => Run(_ => operation());

    public static CancellationTokenSource? DetachLifetime(ref CancellationTokenSource? lifetime)
    {
        return Interlocked.Exchange(ref lifetime, null);
    }

    public static CancellationTokenSource CreateLinkedLifetime(CancellationToken lifetime) =>
        CancellationTokenSource.CreateLinkedTokenSource(lifetime);

    public void MarkExitWithoutCleanup()
    {
        Interlocked.Exchange(ref exiting, 1);
        lifetime.Cancel();
    }

    public Task BeginExitAsync()
    {
        if (Interlocked.Exchange(ref exiting, 1) == 0)
        {
            lifetime.Cancel();
            _ = DrainAndCleanupAsync();
        }
        return completion.Task;
    }

    private async Task DrainAndCleanupAsync()
    {
        Task[] operations;
        lock (gate) operations = tracked.ToArray();
        try { await Task.WhenAll(operations).ConfigureAwait(false); }
        catch { }
        if (Interlocked.Exchange(ref cleanupStarted, 1) == 0)
        {
            try
            {
                if (marshalCleanup)
                {
                    var dispatched = Dispatcher.UIThread.InvokeAsync(async () => await cleanup(lifetime.Token));
                    await dispatched;
                }
                else
                    await cleanup(lifetime.Token).ConfigureAwait(false);
            }
            catch { }
        }
        completion.TrySetResult(true);
    }
}

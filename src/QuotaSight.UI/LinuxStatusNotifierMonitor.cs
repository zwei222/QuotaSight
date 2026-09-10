namespace QuotaSight.UI;

public sealed class LinuxStatusNotifierMonitor(Func<Task<bool>> probe, Action<bool> availabilityChanged, TimeSpan? pollInterval = null)
{
    private readonly TimeSpan interval = pollInterval ?? TimeSpan.FromSeconds(5);
    private bool? last;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool available;
            try { available = await probe().WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { available = false; }
            if (last != available) { last = available; availabilityChanged(available); }
            try { await Task.Delay(interval, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }
}

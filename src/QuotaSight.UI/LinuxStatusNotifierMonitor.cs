namespace QuotaSight.UI;

public sealed class LinuxStatusNotifierMonitor(Func<Task<bool>> probe, Action<bool> availabilityChanged, TimeSpan? pollInterval = null)
{
    private readonly TimeSpan interval = pollInterval ?? TimeSpan.FromSeconds(5);
    private bool? announced;
    private int consecutiveFailures;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool available;
            try { available = await probe().WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { available = false; }
            if (available)
            {
                consecutiveFailures = 0;
                if (announced != true) { announced = true; availabilityChanged(true); }
            }
            else if (++consecutiveFailures >= 3 && announced != false)
            {
                announced = false;
                availabilityChanged(false);
            }
            try { await Task.Delay(interval, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }
}

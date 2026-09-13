using QuotaSight.Application;
using QuotaSight.Core;
namespace QuotaSight.Infrastructure;

public sealed class QuotaApplication(IEnumerable<IQuotaAdapter> adapters, IQuotaHistory history, TimeProvider timeProvider) : IQuotaApplication, IDisposable
{
    private readonly TimeProvider clock = timeProvider;
    public async ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await history.PruneAsync(DateOnly.FromDateTime(now.UtcDateTime).AddDays(-30), cancellationToken);
        var all = new List<QuotaSnapshot>();
        var failures = new List<ProviderFailure>();
        foreach (var adapter in adapters)
        {
            try
            {
                var result = await adapter.FetchAsync(adapter.Provider.ToString(), cancellationToken);
                if (result.IsSuccess && result.Value is not null) all.AddRange(result.Value);
                else if (!result.IsSuccess) failures.Add(new(adapter.Provider, result.Status, result.RetryAfter));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add(new(adapter.Provider, FetchStatus.TransientFailure));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add(new(adapter.Provider, FetchStatus.TransientFailure));
            }
        }
        if (all.Count > 0) await history.AppendAsync(all, cancellationToken);
        return new(all, failures);
    }

    public void Dispose() => (history as IDisposable)?.Dispose();
}

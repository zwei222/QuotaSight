using QuotaSight.Application;
using QuotaSight.Core;
namespace QuotaSight.Infrastructure;

public sealed class QuotaApplication(IEnumerable<IQuotaAdapter> adapters, IQuotaHistory history, TimeProvider timeProvider) : IQuotaApplication, IDisposable
{
    private readonly TimeProvider clock = timeProvider;
    public async ValueTask<IReadOnlyList<QuotaSnapshot>> RefreshAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await history.PruneAsync(DateOnly.FromDateTime(now.UtcDateTime).AddDays(-30), cancellationToken);
        var all = new List<QuotaSnapshot>(); foreach (var adapter in adapters) { var result = await adapter.FetchAsync(adapter.Provider.ToString(), cancellationToken); if (result.IsSuccess && result.Value is not null) all.AddRange(result.Value); }
        if (all.Count > 0) await history.AppendAsync(all, cancellationToken); return all;
    }

    public void Dispose() => (history as IDisposable)?.Dispose();
}

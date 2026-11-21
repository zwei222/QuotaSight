namespace QuotaSight.UI;

/// <summary>Owner-neutral daily totals returned by GET /v1/usage/daily.</summary>
public sealed record UsageDay(DateOnly Date, string CollectorId, long TotalTokens);
public sealed record DailyUsage(DateOnly From, DateOnly To, IReadOnlyList<UsageDay> Days, long TotalTokens);

/// <summary>Read-only seam for later HTTP integration; no runtime transport is configured in v1 UI.</summary>
public interface IUsageReadPort
{
    Task<DailyUsage> ReadDailyAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken);
}

public sealed class UnconfiguredUsageReadPort : IUsageReadPort
{
    public Task<DailyUsage> ReadDailyAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken) =>
        Task.FromException<DailyUsage>(new InvalidOperationException("Usage API is not configured."));
}

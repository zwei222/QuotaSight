namespace QuotaSight.UI;

/// <summary>Owner-neutral daily totals returned by GET /v1/usage/daily.</summary>
public sealed record UsageDay(DateOnly Date, string CollectorId, long TotalTokens);

/// <summary>
/// Reception state of one collector in the read response: the collector's active
/// generation and the reception time of the newest accepted snapshot of that
/// generation. <see cref="LastReceivedAt"/> is <c>null</c> when the active
/// generation has no accepted receipt yet, which is a normal state, not an error.
/// </summary>
public sealed record UsageCollectorReceipt(string CollectorId, long Generation, DateTimeOffset? LastReceivedAt);

/// <summary>
/// One verified daily read. <see cref="Collectors"/> is the freshness surface: it
/// carries, per covered collector, when that collector last landed a snapshot.
/// </summary>
public sealed record DailyUsage
{
    /// <summary>
    /// Creates a read result. <paramref name="collectors"/> defaults to an empty
    /// list so a caller can build a result with no freshness information; the HTTP
    /// port always supplies the parsed list.
    /// </summary>
    public DailyUsage(
        DateOnly from,
        DateOnly to,
        IReadOnlyList<UsageDay> days,
        long totalTokens,
        IReadOnlyList<UsageCollectorReceipt>? collectors = null)
    {
        From = from;
        To = to;
        Days = days;
        TotalTokens = totalTokens;
        Collectors = collectors ?? [];
    }

    /// <summary>Start of the echoed range.</summary>
    public DateOnly From { get; }

    /// <summary>End of the echoed range.</summary>
    public DateOnly To { get; }

    /// <summary>Verified daily rows, ascending by date then collector id.</summary>
    public IReadOnlyList<UsageDay> Days { get; }

    /// <summary>Sum of <see cref="Days"/>, recomputed and verified locally.</summary>
    public long TotalTokens { get; }

    /// <summary>Per-collector reception state, ascending by collector id.</summary>
    public IReadOnlyList<UsageCollectorReceipt> Collectors { get; }
}

/// <summary>
/// Stable, screen-safe classification of a failed usage read.
///
/// It is what the presentation layer may show; the underlying
/// <see cref="UsageReadFailure"/> is finer grained and transport specific. The
/// values are fixed, so a caller can map them to copy without inspecting an
/// exception message, a status code or a response body.
/// </summary>
public enum UsageFailureKind
{
    /// <summary>No failure has been observed for the current values.</summary>
    None,

    /// <summary>The service rejected the credential (401).</summary>
    Unauthorized,

    /// <summary>The credential is not allowed to read usage (403).</summary>
    Forbidden,

    /// <summary>The service reports that the requested resource does not exist (404).</summary>
    NotFound,

    /// <summary>
    /// The breakdown endpoint reported <c>not_found</c>: the server serves the daily
    /// totals but not the breakdown yet. A distinct state, not a generic error.
    /// </summary>
    BreakdownUnsupported,

    /// <summary>
    /// The breakdown endpoint reported <c>collector_not_found</c>: the named collector
    /// does not exist, so the request needs the user's attention.
    /// </summary>
    CollectorNotFound,

    /// <summary>The response violated the read contract, or the request was refused locally.</summary>
    InvalidResponse,

    /// <summary>A temporary condition: timeout, connection failure, 429 or 5xx.</summary>
    Transient,

    /// <summary>The service answered with a redirect, or a response for a different URI.</summary>
    Redirected,

    /// <summary>No credential is stored, or the stored one belongs to another identity.</summary>
    CredentialMissing,
}

/// <summary>
/// One (client, provider, model) slice of attributed token usage, aggregated over the
/// whole requested range. An empty key component means "unknown"; it is data, not a
/// defect, and is shown as such instead of being merged away.
/// </summary>
public sealed record UsagePart(
    string Client,
    string Provider,
    string Model,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long Reasoning,
    long Total);

/// <summary>
/// One collector-day pair of the range: the day's received total and the part of it the
/// server could attribute to a (client, provider, model) key. <c>TotalTokens</c> may
/// exceed <see cref="AttributedTokens"/> (unclassified remainder) or, for a faulty
/// upstream, fall below it (over-attribution); neither is corrected here.
/// </summary>
public sealed record UsageCoverage(DateOnly Date, string CollectorId, long TotalTokens, long AttributedTokens);

/// <summary>
/// One verified breakdown read: the attributed (client, provider, model) parts of the
/// range, the per-day attribution coverage that bounds them, and the freshness surface
/// of the collectors that reported. The parts must sum to the attributed coverage; the
/// read port verifies that locally instead of trusting it.
/// </summary>
public sealed record UsageBreakdown(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<UsagePart> Parts,
    IReadOnlyList<UsageCoverage> Coverage,
    IReadOnlyList<UsageCollectorReceipt> Collectors);

/// <summary>
/// The daily read seam the usage page calls. The application wires
/// <see cref="ReconfigurableUsageReadPort"/>, which installs an
/// <see cref="HttpUsageReadPort"/> once an endpoint and a credential pair exist and
/// answers "not configured" otherwise.
/// </summary>
public interface IUsageReadPort
{
    Task<DailyUsage> ReadDailyAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the attributed usage breakdown for a range. It is a distinct, optional
    /// capability: a server that serves the daily totals may not serve the breakdown yet,
    /// and the failure says which of the two the caller hit.
    /// </summary>
    Task<UsageBreakdown> ReadBreakdownAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken);
}

public sealed class UnconfiguredUsageReadPort : IUsageReadPort
{
    public Task<DailyUsage> ReadDailyAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken) =>
        Task.FromException<DailyUsage>(new InvalidOperationException("Usage API is not configured."));

    public Task<UsageBreakdown> ReadBreakdownAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken) =>
        Task.FromException<UsageBreakdown>(new InvalidOperationException("Usage API is not configured."));
}

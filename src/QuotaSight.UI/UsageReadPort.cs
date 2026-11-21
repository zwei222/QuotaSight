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
/// The daily read seam the usage page calls. The application wires
/// <see cref="ReconfigurableUsageReadPort"/>, which installs an
/// <see cref="HttpUsageReadPort"/> once an endpoint and a credential pair exist and
/// answers "not configured" otherwise.
/// </summary>
public interface IUsageReadPort
{
    Task<DailyUsage> ReadDailyAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken);
}

public sealed class UnconfiguredUsageReadPort : IUsageReadPort
{
    public Task<DailyUsage> ReadDailyAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken) =>
        Task.FromException<DailyUsage>(new InvalidOperationException("Usage API is not configured."));
}

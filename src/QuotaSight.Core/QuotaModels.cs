namespace QuotaSight.Core;

public enum ProviderKind { ChatGpt, Claude, OpenCode, Copilot, Manual }
public enum QuotaWindowKind { Rolling, Daily, Weekly, Monthly, Custom }
public enum QuotaSource { Official, Delayed, Manual, Experimental }
public enum QuotaConfidence { Official, Reported, Estimated, Manual, High, Low }
public enum StaleReason { None, FreshUntilExceeded, WindowEnded }

public sealed record QuotaWindow(
    QuotaWindowKind Kind,
    DateTimeOffset Start,
    DateTimeOffset End,
    TimeSpan? Duration = null,
    DateTimeOffset? ResetAt = null)
{
    public TimeSpan EffectiveDuration => Duration ?? End - Start;
    public bool Contains(DateTimeOffset instant) => instant >= Start && instant < End;
}

public sealed record QuotaSnapshot(
    ProviderKind Provider,
    string Account,
    string Metric,
    QuotaWindow Window,
    decimal? Used,
    decimal? Limit,
    decimal? ReportedPercent,
    string Unit,
    DateTimeOffset Observed,
    DateTimeOffset Fetched,
    QuotaSource Source,
    QuotaConfidence Confidence,
    DateTimeOffset? FreshUntil,
    string DisplayName = "")
{
    public decimal? UsedPercent => Limit is > 0 && Used is { } used ? used / Limit.Value * 100m : null;
    public decimal? EffectivePercent => UsedPercent ?? ReportedPercent;
    public decimal? RemainingPercent => Limit is > 0 && UsedPercent is { } percent ? Math.Max(0m, 100m - percent) : null;
    public decimal? OveragePercent => Limit is > 0 && UsedPercent is { } percent ? Math.Max(0m, percent - 100m) : null;
    public bool IsStale(DateTimeOffset now) => GetStaleReason(now) != StaleReason.None;
    public StaleReason GetStaleReason(DateTimeOffset now)
    {
        if (Window.End <= now)
        {
            return StaleReason.WindowEnded;
        }

        return FreshUntil is { } expiry && now > expiry
            ? StaleReason.FreshUntilExceeded
            : StaleReason.None;
    }

    public static QuotaSnapshot? MostConstrained(IEnumerable<QuotaSnapshot> snapshots) => snapshots
        .OrderByDescending(s => s.EffectivePercent.HasValue)
        .ThenByDescending(s => s.EffectivePercent ?? decimal.MinValue)
        .ThenBy(s => s.Window.End)
        .FirstOrDefault();
}

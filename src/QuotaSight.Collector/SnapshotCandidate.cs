namespace QuotaSight.Collector;

/// <summary>
/// One normalized (client, provider, model) triple.
///
/// The three values are already in the API's normalized form: lowercase, bounded,
/// and <c>""</c> when upstream supplied something that is not an identifier (an
/// unparsable value, or a model id that is really a file path). Ordering is
/// ordinal on the triple, which is exactly the order the wire needs.
/// </summary>
/// <param name="Client">Normalized client identifier, or <c>""</c>.</param>
/// <param name="Provider">Normalized provider identifier, or <c>""</c>.</param>
/// <param name="Model">Normalized model identifier, or <c>""</c>.</param>
public sealed record SnapshotPartKey(string Client, string Provider, string Model)
    : IComparable<SnapshotPartKey>
{
    /// <summary>Ordinal comparison of client, then provider, then model.</summary>
    /// <param name="other">Key to compare with.</param>
    public int CompareTo(SnapshotPartKey? other)
    {
        if (other is null)
        {
            return 1;
        }

        var client = string.CompareOrdinal(Client, other.Client);
        if (client != 0)
        {
            return client;
        }

        var provider = string.CompareOrdinal(Provider, other.Provider);
        return provider != 0 ? provider : string.CompareOrdinal(Model, other.Model);
    }
}

/// <summary>
/// One day's tokens attributed to one key: the five components the API stores and
/// reports. <see cref="Total"/> is derived, because the API requires the row total
/// to be exactly the sum and a second, independently carried number could drift.
/// </summary>
/// <param name="Key">Normalized key this row belongs to.</param>
/// <param name="Input">Input (prompt) tokens.</param>
/// <param name="Output">Output (completion) tokens.</param>
/// <param name="CacheRead">Cache-read tokens.</param>
/// <param name="CacheWrite">Cache-write tokens.</param>
/// <param name="Reasoning">Reasoning tokens.</param>
public sealed record SnapshotPart(
    SnapshotPartKey Key,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long Reasoning)
{
    /// <summary>Sum of the five components; never stored separately upstream.</summary>
    public long Total => Input + Output + CacheRead + CacheWrite + Reasoning;
}

/// <summary>One observed calendar day with its observed token total and breakdown.</summary>
/// <param name="Date">Strict <c>YYYY-MM-DD</c> calendar date.</param>
/// <param name="TotalTokens">Observed non-negative token total for that day.</param>
/// <param name="Parts">
/// Breakdown rows in ascending key order, or <c>null</c> when this day sends no
/// breakdown. The API requires <c>sum(parts) == TotalTokens</c>, so a day whose
/// client rows do not add up to its total is sent without parts rather than with a
/// partial or adjusted breakdown.
/// </param>
public sealed record SnapshotDay(string Date, long TotalTokens, IReadOnlyList<SnapshotPart>? Parts = null);

/// <summary>
/// A candidate API snapshot built only from days the upstream document actually
/// dated. It is a candidate, never an authority: nothing here claims that the
/// upstream scan was complete.
/// </summary>
/// <param name="SchemaVersion">Always <see cref="TokscaleGraphContract.SchemaVersion"/>.</param>
/// <param name="AdapterVersion">Always <see cref="TokscaleGraphContract.PinnedAdapterVersion"/>.</param>
/// <param name="CollectedAt">Canonical UTC instant (<c>YYYY-MM-DDTHH:MM:SS.mmmZ</c>).</param>
/// <param name="BucketTimeZone">Operator-asserted bucket time zone.</param>
/// <param name="CoveredFrom">First dated day in <paramref name="Days"/>.</param>
/// <param name="CoveredTo">Last dated day in <paramref name="Days"/>.</param>
/// <param name="Days">Ascending, distinct dated rows.</param>
/// <param name="OmittedPartDays">
/// Days whose breakdown was withheld during normalization (client rows that did not
/// add up to the day total, or more keys than one day may carry). The count is
/// reported to the operator; the days themselves are still sent with their totals.
/// </param>
public sealed record SnapshotCandidate(
    int SchemaVersion,
    string AdapterVersion,
    string CollectedAt,
    string BucketTimeZone,
    string CoveredFrom,
    string CoveredTo,
    IReadOnlyList<SnapshotDay> Days,
    int OmittedPartDays = 0);

/// <summary>Outcome classification of a normalization attempt.</summary>
public enum NormalizationStatus
{
    /// <summary>At least one dated row was observed; a candidate exists.</summary>
    Candidate,

    /// <summary>The document is well formed for the pinned version but has no dated rows.</summary>
    NoObservedDays,

    /// <summary>The document violates the pinned contract; nothing may be emitted.</summary>
    Rejected,
}

/// <summary>
/// Result of normalizing an upstream graph document. Rejections carry a stable
/// field/reason pair and never carry upstream payload text.
/// </summary>
public sealed record NormalizationResult
{
    private NormalizationResult(
        NormalizationStatus status,
        SnapshotCandidate? candidate,
        string? field,
        string? reason,
        string? message)
    {
        Status = status;
        Candidate = candidate;
        Field = field;
        Reason = reason;
        Message = message;
    }

    /// <summary>Classification of this attempt.</summary>
    public NormalizationStatus Status { get; }

    /// <summary>Candidate snapshot, present only for <see cref="NormalizationStatus.Candidate"/>.</summary>
    public SnapshotCandidate? Candidate { get; }

    /// <summary>Stable dotted field path for a rejection.</summary>
    public string? Field { get; }

    /// <summary>Stable machine-readable reason code.</summary>
    public string? Reason { get; }

    /// <summary>Short human-readable explanation without upstream values.</summary>
    public string? Message { get; }

    internal static NormalizationResult FromCandidate(SnapshotCandidate candidate) =>
        new(NormalizationStatus.Candidate, candidate, null, null, null);

    internal static NormalizationResult NoObservedDays(string reason, string message) =>
        new(NormalizationStatus.NoObservedDays, null, null, reason, message);

    internal static NormalizationResult Rejected(string field, string reason, string message) =>
        new(NormalizationStatus.Rejected, null, field, reason, message);
}

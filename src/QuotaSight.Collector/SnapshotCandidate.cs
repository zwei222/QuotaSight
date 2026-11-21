namespace QuotaSight.Collector;

/// <summary>One observed calendar day with its observed token total.</summary>
/// <param name="Date">Strict <c>YYYY-MM-DD</c> calendar date.</param>
/// <param name="TotalTokens">Observed non-negative token total for that day.</param>
public sealed record SnapshotDay(string Date, long TotalTokens);

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
public sealed record SnapshotCandidate(
    int SchemaVersion,
    string AdapterVersion,
    string CollectedAt,
    string BucketTimeZone,
    string CoveredFrom,
    string CoveredTo,
    IReadOnlyList<SnapshotDay> Days);

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

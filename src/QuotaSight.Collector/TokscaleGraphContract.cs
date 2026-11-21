namespace QuotaSight.Collector;

/// <summary>
/// The pinned upstream contract and the bounds of the local API wire v1.
///
/// The upstream version is pinned exactly on purpose: any other version is an
/// unknown shape and is rejected instead of being guessed at. Every bound here
/// mirrors <c>src/QuotaSight.Usage.Api/src/schema.ts</c>; the collector must
/// never emit something the API will reject.
/// </summary>
public static class TokscaleGraphContract
{
    /// <summary>Exact upstream graph version this normalizer was written against.</summary>
    public const string PinnedUpstreamVersion = "4.23.0";

    /// <summary>Adapter version reported to the API (lowercase ASCII slug).</summary>
    public const string PinnedAdapterVersion = "tokscale-4.23.0";

    /// <summary>API snapshot schema version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// The only bucket time zone API v1 accepts. The upstream graph JSON does not
    /// carry the zone it scanned in, so this value is an operator-provided fact and
    /// is never inferred from the dates in the document.
    /// </summary>
    public const string SupportedBucketTimeZone = "Asia/Tokyo";

    /// <summary>Hard ceiling on the upstream document read from stdin.</summary>
    public const int MaxInputBytes = 64 * 1024;

    /// <summary>API body ceiling for the emitted snapshot.</summary>
    public const int MaxOutputBytes = 32 * 1024;

    /// <summary>Maximum number of day rows in a snapshot.</summary>
    public const int MaxDays = 62;

    /// <summary>Largest token value expressible as a JavaScript safe integer.</summary>
    public const long MaxSafeInteger = 9_007_199_254_740_991;

    /// <summary>Smallest accepted calendar year in a day row.</summary>
    public const int MinYear = 2000;

    /// <summary>Largest accepted calendar year in a day row.</summary>
    public const int MaxYear = 2100;
}

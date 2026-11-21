namespace QuotaSight.Collector;

/// <summary>
/// The pinned upstream contract and the bounds of the local API wire.
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

    /// <summary>API snapshot schema version emitted by default (the daily-total wire).</summary>
    public const int SchemaVersion = 1;

    /// <summary>API snapshot schema version that carries the per-key breakdown.</summary>
    public const int SchemaVersionParts = 2;

    /// <summary>
    /// The only bucket time zone the API accepts. The upstream graph JSON does not
    /// carry the zone it scanned in, so this value is an operator-provided fact and
    /// is never inferred from the dates in the document.
    /// </summary>
    public const string SupportedBucketTimeZone = "Asia/Tokyo";

    /// <summary>
    /// Hard ceiling on the upstream document read from stdin.
    ///
    /// This is a local constraint, not an API one: the emitted body is bound by
    /// <see cref="MaxOutputBytes"/> whatever the input size. The range was raised
    /// from 64 KiB when the collector started reading <c>contributions[].clients</c>
    /// as well as the daily totals — a real document covering 17 days is already
    /// about 34 KiB, and a 30-day one is larger than the old ceiling, which would
    /// have turned a normal month of data into a rejection.
    /// </summary>
    public const int MaxInputBytes = 512 * 1024;

    /// <summary>API body ceiling for the emitted snapshot, in either wire version.</summary>
    public const int MaxOutputBytes = 32 * 1024;

    /// <summary>
    /// Body budget the collector holds itself to when it decides how much of the
    /// breakdown to send. The difference from <see cref="MaxOutputBytes"/> is
    /// deliberate headroom: the API counts bytes, this counts bytes, and a
    /// rejection at the hard bound would throw away the whole snapshot instead of
    /// the breakdown of its oldest days.
    /// </summary>
    public const int MaxPartsBodyBytes = 30 * 1024;

    /// <summary>Maximum number of day rows in a snapshot.</summary>
    public const int MaxDays = 62;

    /// <summary>Maximum number of distinct breakdown keys in one snapshot.</summary>
    public const int MaxKeys = 128;

    /// <summary>
    /// Maximum number of breakdown rows in one day. A day above it sends its total
    /// only, because a partial breakdown would be a lie about the missing keys.
    /// </summary>
    public const int MaxPartsPerDay = 32;

    /// <summary>Maximum number of breakdown rows in one snapshot.</summary>
    public const int MaxPartsPerSnapshot = 1024;

    /// <summary>Longest accepted client identifier (pattern allows 1..32).</summary>
    public const int MaxClientLength = 32;

    /// <summary>Longest accepted provider identifier, commas included.</summary>
    public const int MaxProviderLength = 64;

    /// <summary>Longest accepted model identifier (pattern allows 1..96).</summary>
    public const int MaxModelLength = 96;

    /// <summary>Greatest number of providers joined into one provider identifier.</summary>
    public const int MaxProviderElements = 4;

    /// <summary>An identifier that upstream did not supply or that failed normalization.</summary>
    public const string UnknownIdentifier = "";

    /// <summary>Largest token value expressible as a JavaScript safe integer.</summary>
    public const long MaxSafeInteger = 9_007_199_254_740_991;

    /// <summary>Smallest accepted calendar year in a day row.</summary>
    public const int MinYear = 2000;

    /// <summary>Largest accepted calendar year in a day row.</summary>
    public const int MaxYear = 2100;
}

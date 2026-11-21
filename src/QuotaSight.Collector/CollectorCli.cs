using System.Globalization;

namespace QuotaSight.Collector;

/// <summary>
/// Exit codes are the only way this tool can be trusted by a caller. Only
/// <see cref="Candidate"/> is a clean run, and even then the output is a
/// candidate snapshot, never a claim that the upstream scan was complete.
/// </summary>
public static class CollectorExitCodes
{
    /// <summary>A candidate snapshot was written; upstream reported no partial/pricing warning.</summary>
    public const int Candidate = 0;

    /// <summary>The upstream document had no dated rows; no snapshot was produced.</summary>
    public const int NoObservedDays = 2;

    /// <summary>A candidate was written but upstream declared partial/unpriced evidence; do not treat it as complete.</summary>
    public const int CandidateUnverified = 3;

    /// <summary>The upstream document violates the pinned contract or the API bounds; nothing was written.</summary>
    public const int Rejected = 4;

    /// <summary>The invocation is unusable (missing mode, generation, bucket time zone, or unknown argument).</summary>
    public const int Usage = 5;

    /// <summary>Send refused: authentication failed (401). Not retried.</summary>
    public const int SendUnauthorized = 11;

    /// <summary>Send refused: authorization failed (403). Not retried.</summary>
    public const int SendForbidden = 12;

    /// <summary>Send refused: redirect, not followed.</summary>
    public const int SendRedirect = 13;

    /// <summary>Send refused: revision or generation conflict (409). Not retried.</summary>
    public const int SendConflict = 14;

    /// <summary>Send refused: precondition failed (412). Not retried.</summary>
    public const int SendPrecondition = 15;

    /// <summary>Send refused: snapshot not processable (422). Not retried.</summary>
    public const int SendUnprocessable = 16;

    /// <summary>Send refused: other client error (4xx). Not retried.</summary>
    public const int SendRejected = 17;

    /// <summary>Send failed: server unavailable after the attempt cap.</summary>
    public const int SendUnavailable = 18;

    /// <summary>Send failed: timed out after the attempt cap.</summary>
    public const int SendTimeout = 19;

    /// <summary>Send failed: transport failed after the attempt cap.</summary>
    public const int SendTransport = 20;

    /// <summary>Send refused: credentials unavailable or unsupported.</summary>
    public const int CredentialUnavailable = 21;

    /// <summary>Send refused: the response body exceeded the read bound.</summary>
    public const int SendResponseTooLarge = 22;
}

/// <summary>
/// Local collector CLI. The default is a dry run: read one bounded upstream
/// document from an explicit stdin, normalize it, and write the API wire
/// candidate to stdout in <c>--preview</c> mode only.
///
/// A socket is opened only when <c>--send</c> is explicit and <c>--preview</c>
/// is absent. Payload text is never written to stderr.
/// </summary>
public static class CollectorCli
{
    /// <summary>Explicit preview mode flag; without it nothing is written to stdout.</summary>
    public const string PreviewFlag = "--preview";

    /// <summary>Explicit send mode flag. Does nothing unless endpoint, collector id and revision are also set.</summary>
    public const string SendFlag = "--send";

    /// <summary>Declares that upstream reported partial, unpriced or stale-priced usage.</summary>
    public const string UpstreamPartialFlag = "--upstream-partial";

    private const string GenerationOption = "--generation";
    private const string BucketTimeZoneOption = "--bucket-time-zone";
    private const string EndpointOption = "--endpoint";
    private const string CollectorIdOption = "--collector-id";
    private const string RevisionOption = "--revision";

    private const string UsageText =
        "usage: QuotaSight.Collector --preview --generation <positive integer> "
        + "--bucket-time-zone <zone> [--upstream-partial] < graph.json\n"
        + "       QuotaSight.Collector --send --endpoint <https-base-url> --collector-id <id> "
        + "--revision <positive integer> --generation <positive integer> --bucket-time-zone <zone> < graph.json";

    /// <summary>Runs the collector against explicit streams.</summary>
    /// <param name="args">Command line arguments.</param>
    /// <param name="stdin">Upstream document stream (the operator's explicit stdin).</param>
    /// <param name="stdout">Preview sink; receives only the API wire candidate.</param>
    /// <param name="stderr">One-line status sink; never receives payload text.</param>
    /// <returns>Process exit code from <see cref="CollectorExitCodes"/>.</returns>
    public static int Run(string[] args, Stream stdin, Stream stdout, TextWriter stderr) =>
        Run(args, stdin, stdout, stderr, send: null);

    /// <summary>Runs the collector against explicit streams and an optional send seam.</summary>
    /// <param name="args">Command line arguments.</param>
    /// <param name="stdin">Upstream document stream (the operator's explicit stdin).</param>
    /// <param name="stdout">Preview sink; receives only the API wire candidate.</param>
    /// <param name="stderr">One-line status sink; never receives payload text.</param>
    /// <param name="send">Injected transport and credentials. Production passes null.</param>
    /// <returns>Process exit code from <see cref="CollectorExitCodes"/>.</returns>
    public static int Run(string[] args, Stream stdin, Stream stdout, TextWriter stderr, CollectorSendContext? send) =>
        RunCore(args, stdin, stdout, stderr, send);

    private static int RunCore(string[] args, Stream stdin, Stream stdout, TextWriter stderr, CollectorSendContext? send)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var preview = false;
        var sendRequested = false;
        var upstreamPartial = false;
        long? generation = null;
        string? bucketTimeZone = null;
        string? endpoint = null;
        string? collectorId = null;
        string? revisionRaw = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case PreviewFlag:
                    preview = true;
                    break;
                case SendFlag:
                    sendRequested = true;
                    break;
                case UpstreamPartialFlag:
                    upstreamPartial = true;
                    break;
                case EndpointOption:
                    if (!TryTakeValue(args, ref index, out var endpointValue, stderr, EndpointOption))
                    {
                        return CollectorExitCodes.Usage;
                    }

                    endpoint = endpointValue;
                    break;
                case CollectorIdOption:
                    if (!TryTakeValue(args, ref index, out var collectorValue, stderr, CollectorIdOption))
                    {
                        return CollectorExitCodes.Usage;
                    }

                    collectorId = collectorValue;
                    break;
                case RevisionOption:
                    if (!TryTakeValue(args, ref index, out var revisionValue, stderr, RevisionOption))
                    {
                        return CollectorExitCodes.Usage;
                    }

                    revisionRaw = revisionValue;
                    break;
                case GenerationOption:
                    if (!TryTakeValue(args, ref index, out var generationValue, stderr, GenerationOption))
                    {
                        return CollectorExitCodes.Usage;
                    }

                    if (!TryParseGeneration(generationValue, out var parsed))
                    {
                        return Usage(stderr, "generation must be a positive integer no greater than 2^53-1.");
                    }

                    generation = parsed;
                    break;
                case BucketTimeZoneOption:
                    if (!TryTakeValue(args, ref index, out var zoneValue, stderr, BucketTimeZoneOption))
                    {
                        return CollectorExitCodes.Usage;
                    }

                    bucketTimeZone = zoneValue;
                    break;
                default:
                    if (TrySplitOption(argument, EndpointOption, out var inlineEndpoint))
                    {
                        endpoint = inlineEndpoint;
                        break;
                    }

                    if (TrySplitOption(argument, CollectorIdOption, out var inlineCollector))
                    {
                        collectorId = inlineCollector;
                        break;
                    }

                    if (TrySplitOption(argument, RevisionOption, out var inlineRevision))
                    {
                        revisionRaw = inlineRevision;
                        break;
                    }

                    if (TrySplitOption(argument, GenerationOption, out var inlineGeneration))
                    {
                        if (!TryParseGeneration(inlineGeneration, out var parsedInline))
                        {
                            return Usage(stderr, "generation must be a positive integer no greater than 2^53-1.");
                        }

                        generation = parsedInline;
                        break;
                    }

                    if (TrySplitOption(argument, BucketTimeZoneOption, out var inlineZone))
                    {
                        bucketTimeZone = inlineZone;
                        break;
                    }

                    return Usage(stderr, "unknown argument.");
            }
        }

        if (sendRequested && preview)
        {
            return Usage(stderr, "send_usage: --send and --preview cannot be combined.");
        }

        if (!sendRequested && (endpoint is not null || collectorId is not null || revisionRaw is not null))
        {
            return Usage(stderr, "send_usage: --endpoint, --collector-id and --revision are only valid with --send.");
        }

        if (!sendRequested && !preview)
        {
            return Usage(stderr, $"{PreviewFlag} is required to preview locally; nothing is sent unless --send is explicit.");
        }

        if (sendRequested && upstreamPartial)
        {
            return Usage(stderr, "send_usage: --send cannot be combined with --upstream-partial.");
        }

        Uri? sendEndpoint = null;
        long revision = 0;
        if (sendRequested)
        {
            if (endpoint is null)
            {
                return Usage(stderr, "send_usage: --send requires --endpoint.");
            }

            if (collectorId is null)
            {
                return Usage(stderr, "send_usage: --send requires --collector-id.");
            }

            if (revisionRaw is null)
            {
                return Usage(stderr, "send_usage: --send requires --revision.");
            }

            if (!TryParseEndpoint(endpoint, out sendEndpoint))
            {
                return Usage(stderr, "send_usage: --endpoint must be https with an empty path and no user info, query or fragment.");
            }

            if (!IsCollectorId(collectorId))
            {
                return Usage(stderr, "send_usage: --collector-id is not a lowercase slug of 3-64 characters.");
            }

            if (!TryParseRevision(revisionRaw, out revision))
            {
                return Usage(stderr, "send_usage: --revision must be a positive integer no greater than 2^53-1.");
            }
        }

        if (generation is null)
        {
            return Usage(stderr, $"{GenerationOption} is required; the collector generation is not derivable from upstream data.");
        }

        if (string.IsNullOrEmpty(bucketTimeZone))
        {
            return Usage(stderr, $"{BucketTimeZoneOption} is required; the scan bucket zone is an operator fact, never inferred from dates.");
        }

        if (!string.Equals(bucketTimeZone, TokscaleGraphContract.SupportedBucketTimeZone, StringComparison.Ordinal))
        {
            return Rejected(stderr, "bucketTimeZone", "unsupported_time_zone", "API v1 only accepts the Asia/Tokyo bucket time zone.");
        }

        var (tooLarge, document) = ReadBounded(stdin);
        if (tooLarge)
        {
            return Rejected(
                stderr,
                "input",
                "too_large",
                $"The upstream document must not exceed {TokscaleGraphContract.MaxInputBytes} bytes.");
        }

        var result = TokscaleGraphNormalizer.Normalize(document);
        switch (result.Status)
        {
            case NormalizationStatus.Rejected:
                return Rejected(stderr, result.Field!, result.Reason!, result.Message!);
            case NormalizationStatus.NoObservedDays:
                stderr.WriteLine($"no_snapshot: {result.Reason} (no dated row was observed; nothing was emitted)");
                return CollectorExitCodes.NoObservedDays;
            default:
                break;
        }

        var wire = SnapshotWire.Serialize(result.Candidate!, generation.Value);
        if (!sendRequested)
        {
            stdout.Write(wire, 0, wire.Length);
            stdout.Flush();

            if (upstreamPartial)
            {
                stderr.WriteLine("candidate_unverified: upstream reported partial, unpriced or stale-priced usage; the candidate is not complete evidence");
                return CollectorExitCodes.CandidateUnverified;
            }

            return CollectorExitCodes.Candidate;
        }

        return Send(send, sendEndpoint!, collectorId!, revision, wire, stderr);
    }

    private static int Send(
        CollectorSendContext? send,
        Uri endpoint,
        string collectorId,
        long revision,
        byte[] wire,
        TextWriter stderr)
    {
        HttpClient? owned = null;
        var client = send?.HttpClient;
        if (client is null)
        {
            owned = CollectorSnapshotTransport.CreateClient();
            client = owned;
        }

        try
        {
            if (CollectorSnapshotTransport.HasForbiddenDefaultHeader(client))
            {
                return Usage(stderr, "send_usage: the injected client carries a forbidden default header.");
            }

            var credentials = send?.Credentials ?? new SystemdLoadCredentialSource();
            var read = credentials.Read();
            if (!read.Succeeded || read.Credential is null)
            {
                stderr.WriteLine($"credential_unavailable: {read.Failure}");
                return CollectorExitCodes.CredentialUnavailable;
            }

            var delay = send?.Delay ?? DefaultDelay;
            var jitter = send?.NextJitterUnit ?? DefaultJitter;
            return CollectorSnapshotTransport.Transmit(
                client,
                endpoint,
                collectorId,
                revision,
                wire,
                read.Credential,
                delay,
                jitter,
                stderr);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static void DefaultDelay(TimeSpan delay)
    {
        if (delay > TimeSpan.Zero)
        {
            Thread.Sleep(delay);
        }
    }

    private static double DefaultJitter() => Random.Shared.NextDouble();

    private static bool TryParseEndpoint(string raw, out Uri endpoint)
    {
        endpoint = null!;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        if (uri.AbsolutePath is not ("/" or ""))
        {
            return false;
        }

        if (string.IsNullOrEmpty(uri.IdnHost))
        {
            return false;
        }

        endpoint = uri;
        return true;
    }

    private static bool IsCollectorId(string value)
    {
        if (value.Length is < 3 or > 64)
        {
            return false;
        }

        if (!IsLowerAlphaNumeric(value[0]))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!IsLowerAlphaNumeric(character) && character is not ('_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLowerAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool TryParseRevision(string raw, out long revision)
    {
        revision = 0;
        if (raw.Length == 0 || raw[0] == '0')
        {
            return false;
        }

        foreach (var character in raw)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out revision)
            && revision >= 1
            && revision <= TokscaleGraphContract.MaxSafeInteger;
    }

    private static (bool TooLarge, byte[] Document) ReadBounded(Stream stdin)
    {
        var buffer = new byte[TokscaleGraphContract.MaxInputBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stdin.Read(buffer, total, buffer.Length - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        if (total > TokscaleGraphContract.MaxInputBytes)
        {
            return (true, []);
        }

        return (false, buffer.AsSpan(0, total).ToArray());
    }

    private static bool TryTakeValue(string[] args, ref int index, out string value, TextWriter stderr, string option)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            stderr.WriteLine($"{option} requires a value.");
            return false;
        }

        index++;
        value = args[index];
        return true;
    }

    private static bool TrySplitOption(string argument, string option, out string value)
    {
        var prefix = option + "=";
        if (argument.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = argument[prefix.Length..];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryParseGeneration(string raw, out long generation) =>
        long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out generation)
        && generation >= 1
        && generation <= TokscaleGraphContract.MaxSafeInteger;

    private static int Usage(TextWriter stderr, string message)
    {
        stderr.WriteLine(message);
        stderr.WriteLine(UsageText);
        return CollectorExitCodes.Usage;
    }

    private static int Rejected(TextWriter stderr, string field, string reason, string message)
    {
        stderr.WriteLine($"rejected: {field} {reason} — {message}");
        return CollectorExitCodes.Rejected;
    }
}

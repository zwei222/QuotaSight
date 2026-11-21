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

    /// <summary>The invocation is unusable (missing preview mode, generation or bucket time zone, unknown argument).</summary>
    public const int Usage = 5;
}

/// <summary>
/// Local dry-run CLI: reads one bounded upstream document from an explicit
/// stdin, normalizes it, and writes the API wire candidate to stdout in
/// <c>--preview</c> mode only.
///
/// It never opens a socket, never launches another process, never writes files
/// and never logs payload text.
/// </summary>
public static class CollectorCli
{
    /// <summary>Explicit preview mode flag; without it nothing is written to stdout.</summary>
    public const string PreviewFlag = "--preview";

    /// <summary>Declares that upstream reported partial, unpriced or stale-priced usage.</summary>
    public const string UpstreamPartialFlag = "--upstream-partial";

    private const string GenerationOption = "--generation";
    private const string BucketTimeZoneOption = "--bucket-time-zone";

    private const string UsageText =
        "usage: QuotaSight.Collector --preview --generation <positive integer> "
        + "--bucket-time-zone <zone> [--upstream-partial] < graph.json";

    /// <summary>Runs the collector against explicit streams.</summary>
    /// <param name="args">Command line arguments.</param>
    /// <param name="stdin">Upstream document stream (the operator's explicit stdin).</param>
    /// <param name="stdout">Preview sink; receives only the API wire candidate.</param>
    /// <param name="stderr">One-line status sink; never receives payload text.</param>
    /// <returns>Process exit code from <see cref="CollectorExitCodes"/>.</returns>
    public static int Run(string[] args, Stream stdin, Stream stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var preview = false;
        var upstreamPartial = false;
        long? generation = null;
        string? bucketTimeZone = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case PreviewFlag:
                    preview = true;
                    break;
                case UpstreamPartialFlag:
                    upstreamPartial = true;
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

                    return Usage(stderr, $"unknown argument '{argument}'.");
            }
        }

        if (!preview)
        {
            return Usage(stderr, $"{PreviewFlag} is required; this collector only previews locally and never sends.");
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
        stdout.Write(wire, 0, wire.Length);
        stdout.Flush();

        if (upstreamPartial)
        {
            stderr.WriteLine("candidate_unverified: upstream reported partial, unpriced or stale-priced usage; the candidate is not complete evidence");
            return CollectorExitCodes.CandidateUnverified;
        }

        return CollectorExitCodes.Candidate;
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

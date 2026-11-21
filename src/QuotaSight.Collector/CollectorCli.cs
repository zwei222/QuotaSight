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

    /// <summary>The state directory or the state document is unusable; nothing was sent.</summary>
    public const int StateUnavailable = 23;

    /// <summary>An unresolved 409 is waiting for <c>--state-advance</c>; nothing was sent.</summary>
    public const int StateBlocked = 24;

    /// <summary>The state belongs to another collector or generation, or a floor would go backwards.</summary>
    public const int StateMismatch = 25;

    /// <summary>No revision is left to allocate for this (collector, generation).</summary>
    public const int RevisionExhausted = 26;

    /// <summary>2xx, but the body was not the receipt the API documents; the outcome is unverified.</summary>
    public const int SendReceiptUnverified = 27;

    /// <summary>The send happened but its result could not be recorded in the state file.</summary>
    public const int StateUnrecorded = 28;
}

/// <summary>
/// Local collector CLI. The default is a dry run: read one bounded upstream
/// document from an explicit stdin, normalize it, and write the API wire
/// candidate to stdout in <c>--preview</c> mode only.
///
/// A socket is opened only when <c>--send</c> is explicit and <c>--preview</c>
/// is absent. The revision is never an argument: it is allocated from the
/// validated state directory before the request leaves the process. Payload text
/// is never written to stderr.
/// </summary>
public static class CollectorCli
{
    /// <summary>Explicit preview mode flag; without it nothing is written to stdout.</summary>
    public const string PreviewFlag = "--preview";

    /// <summary>Explicit send mode flag. Does nothing unless endpoint, collector id and generation are also set.</summary>
    public const string SendFlag = "--send";

    /// <summary>Declares that upstream reported partial, unpriced or stale-priced usage.</summary>
    public const string UpstreamPartialFlag = "--upstream-partial";

    /// <summary>
    /// Selects the API wire contract: <c>1</c> (default, daily totals only) or
    /// <c>2</c> (daily totals plus the client/provider/model breakdown). Opt-in on
    /// purpose: an operator upgrades the API before the collector starts sending a
    /// shape it may not accept yet.
    /// </summary>
    public const string WireSchemaOption = "--wire-schema";

    /// <summary>Initialises the state document with an explicit revision floor.</summary>
    public const string StateInitFlag = "--state-init";

    /// <summary>Clears a 409 block after the operator has resolved it.</summary>
    public const string StateAdvanceFlag = "--state-advance";

    /// <summary>Prints the state document and changes nothing.</summary>
    public const string StateShowFlag = "--state-show";

    private const string GenerationOption = "--generation";
    private const string BucketTimeZoneOption = "--bucket-time-zone";
    private const string EndpointOption = "--endpoint";
    private const string CollectorIdOption = "--collector-id";
    private const string FirstRevisionOption = "--first-revision";
    private const string RemovedRevisionOption = "--revision";

    private const string UsageText =
        "usage: QuotaSight.Collector --preview --generation <positive integer> "
        + "--bucket-time-zone <zone> [--upstream-partial] < graph.json\n"
        + "       QuotaSight.Collector --send --endpoint <https-base-url> --collector-id <id> "
        + "--generation <positive integer> --bucket-time-zone <zone> < graph.json\n"
        + "       QuotaSight.Collector --state-init --collector-id <id> --generation <positive integer> "
        + "--first-revision <positive integer>\n"
        + "       QuotaSight.Collector --state-show\n"
        + "       QuotaSight.Collector --state-advance";

    private const string RemovedRevisionMessage =
        "--revision is not accepted: the collector allocates each revision from STATE_DIRECTORY. "
        + "Set the floor explicitly with --state-init --first-revision.";

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
    /// <param name="send">Injected transport, credentials and state directory. Production passes null.</param>
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
        var stateInit = false;
        var stateAdvance = false;
        var stateShow = false;
        var wireSchema = WireSchema.V1;
        long? generation = null;
        long? firstRevision = null;
        string? bucketTimeZone = null;
        string? endpoint = null;
        string? collectorId = null;

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
                case StateInitFlag:
                    stateInit = true;
                    break;
                case StateAdvanceFlag:
                    stateAdvance = true;
                    break;
                case StateShowFlag:
                    stateShow = true;
                    break;
                case RemovedRevisionOption:
                    return Usage(stderr, RemovedRevisionMessage);
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
                case FirstRevisionOption:
                    if (!TryTakeValue(args, ref index, out var firstValue, stderr, FirstRevisionOption))
                    {
                        return CollectorExitCodes.Usage;
                    }

                    if (!TryParsePositiveInteger(firstValue, out var parsedFirst))
                    {
                        return Usage(stderr, "--first-revision must be a positive integer no greater than 2^53-1.");
                    }

                    firstRevision = parsedFirst;
                    break;
                case GenerationOption:
                    if (!TryTakeValue(args, ref index, out var generationValue, stderr, GenerationOption))
                    {
                        return CollectorExitCodes.Usage;
                    }

                    if (!TryParsePositiveInteger(generationValue, out var parsed))
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
                case WireSchemaOption:
                    if (!TryTakeValue(args, ref index, out var schemaValue, stderr, WireSchemaOption))
                    {
                        return CollectorExitCodes.Usage;
                    }

                    if (!TryParseWireSchema(schemaValue, out wireSchema))
                    {
                        return Usage(stderr, "--wire-schema must be 1 (daily totals) or 2 (with the client/provider/model breakdown).");
                    }

                    break;
                default:
                    if (TrySplitOption(argument, RemovedRevisionOption, out _))
                    {
                        return Usage(stderr, RemovedRevisionMessage);
                    }

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

                    if (TrySplitOption(argument, FirstRevisionOption, out var inlineFirst))
                    {
                        if (!TryParsePositiveInteger(inlineFirst, out var parsedInlineFirst))
                        {
                            return Usage(stderr, "--first-revision must be a positive integer no greater than 2^53-1.");
                        }

                        firstRevision = parsedInlineFirst;
                        break;
                    }

                    if (TrySplitOption(argument, GenerationOption, out var inlineGeneration))
                    {
                        if (!TryParsePositiveInteger(inlineGeneration, out var parsedInline))
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

                    if (TrySplitOption(argument, WireSchemaOption, out var inlineSchema))
                    {
                        if (!TryParseWireSchema(inlineSchema, out wireSchema))
                        {
                            return Usage(stderr, "--wire-schema must be 1 (daily totals) or 2 (with the client/provider/model breakdown).");
                        }

                        break;
                    }

                    return Usage(stderr, "unknown argument.");
            }
        }

        if (sendRequested && preview)
        {
            return Usage(stderr, "send_usage: --send and --preview cannot be combined.");
        }

        var modes = (preview ? 1 : 0) + (sendRequested ? 1 : 0) + (stateInit ? 1 : 0)
            + (stateAdvance ? 1 : 0) + (stateShow ? 1 : 0);
        if (modes > 1)
        {
            return Usage(
                stderr,
                "send_usage: only one of --preview, --send, --state-init, --state-advance and --state-show may be given.");
        }

        if (firstRevision is not null && !stateInit)
        {
            return Usage(stderr, "state_usage: --first-revision is only valid with --state-init.");
        }

        if (stateShow || stateAdvance)
        {
            if (endpoint is not null || collectorId is not null || generation is not null
                || bucketTimeZone is not null || firstRevision is not null || upstreamPartial
                || wireSchema != WireSchema.V1)
            {
                return Usage(stderr, "state_usage: --state-show and --state-advance take no other option.");
            }

            return stateShow
                ? ShowState(send, stdout, stderr)
                : AdvanceState(send, stderr);
        }

        if (stateInit)
        {
            if (endpoint is not null || bucketTimeZone is not null || upstreamPartial
                || wireSchema != WireSchema.V1)
            {
                return Usage(stderr, "state_usage: --state-init takes --collector-id, --generation and --first-revision only.");
            }

            if (collectorId is null)
            {
                return Usage(stderr, "state_usage: --state-init requires --collector-id.");
            }

            if (!CollectorSendState.IsCollectorId(collectorId))
            {
                return Usage(stderr, "send_usage: --collector-id is not a lowercase slug of 3-64 characters.");
            }

            if (generation is null)
            {
                return Usage(stderr, "state_usage: --state-init requires --generation.");
            }

            if (firstRevision is null)
            {
                return Usage(stderr, "state_usage: --state-init requires --first-revision.");
            }

            return InitState(send, collectorId, generation.Value, firstRevision.Value, stderr);
        }

        if (!sendRequested && (endpoint is not null || collectorId is not null))
        {
            return Usage(stderr, "send_usage: --endpoint and --collector-id are only valid with --send.");
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

            if (!TryParseEndpoint(endpoint, out sendEndpoint))
            {
                return Usage(stderr, "send_usage: --endpoint must be https with an empty path and no user info, query or fragment.");
            }

            if (!CollectorSendState.IsCollectorId(collectorId))
            {
                return Usage(stderr, "send_usage: --collector-id is not a lowercase slug of 3-64 characters.");
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

        if (sendRequested)
        {
            return Send(send, sendEndpoint!, collectorId!, generation.Value, wireSchema, stdin, stderr);
        }

        return Preview(generation.Value, upstreamPartial, wireSchema, stdin, stdout, stderr);
    }

    private static int Preview(
        long generation,
        bool upstreamPartial,
        WireSchema wireSchema,
        Stream stdin,
        Stream stdout,
        TextWriter stderr)
    {
        if (!TryNormalize(stdin, stderr, out var candidate, out var exitCode))
        {
            return exitCode;
        }

        var wire = SnapshotWire.Serialize(candidate!, generation, wireSchema);
        ReportOmittedPartDays(stderr, wire.OmittedPartDays);
        stdout.Write(wire.Bytes, 0, wire.Bytes.Length);
        stdout.Flush();

        if (upstreamPartial)
        {
            stderr.WriteLine("candidate_unverified: upstream reported partial, unpriced or stale-priced usage; the candidate is not complete evidence");
            return CollectorExitCodes.CandidateUnverified;
        }

        return CollectorExitCodes.Candidate;
    }

    /// <summary>
    /// Send, in the documented order: lock, state validation, normalization,
    /// credentials, allocation (fsynced before the request), transmit, record.
    /// </summary>
    private static int Send(
        CollectorSendContext? send,
        Uri endpoint,
        string collectorId,
        long generation,
        WireSchema wireSchema,
        Stream stdin,
        TextWriter stderr)
    {
        if (!OperatingSystem.IsLinux() || !SystemdStateDirectory.IsSupportedPlatform)
        {
            return StateFailure(stderr, CollectorStateFailure.UnsupportedPlatform);
        }

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

            if (!CollectorStateStore.TryOpen(Directory(send), out var store, out var openFailure))
            {
                return StateFailure(stderr, openFailure);
            }

            using (store)
            {
                if (!store!.TryLock(out var lease))
                {
                    return StateFailure(stderr, CollectorStateFailure.StateLocked);
                }

                using (lease)
                {
                    var state = store.Read(out var readFailure);
                    if (state is null)
                    {
                        return StateFailure(stderr, readFailure);
                    }

                    if (!state.Matches(collectorId, generation))
                    {
                        stderr.WriteLine(
                            "state_mismatch: the state belongs to a different collector or generation; "
                            + "re-initialise it with --state-init --first-revision");
                        return CollectorExitCodes.StateMismatch;
                    }

                    if (state.IsBlocked)
                    {
                        stderr.WriteLine(
                            $"state_blocked: {state.BlockedReason}; resolve the conflict, then run --state-advance");
                        return CollectorExitCodes.StateBlocked;
                    }

                    if (!TryNormalize(stdin, stderr, out var candidate, out var normalizeExit))
                    {
                        return normalizeExit;
                    }

                    var wire = SnapshotWire.Serialize(candidate!, generation, wireSchema);
                    ReportOmittedPartDays(stderr, wire.OmittedPartDays);

                    var credentials = send?.Credentials ?? new SystemdLoadCredentialSource();
                    var read = credentials.Read();
                    if (!read.Succeeded || read.Credential is null)
                    {
                        stderr.WriteLine($"credential_unavailable: {read.Failure}");
                        return CollectorExitCodes.CredentialUnavailable;
                    }

                    if (state.LastOutcome is CollectorSendOutcome.None or CollectorSendOutcome.Unknown)
                    {
                        stderr.WriteLine(
                            "previous_send_unresolved: the previous revision has no confirmed result; "
                            + "sending continues with the next revision");
                    }

                    if (!state.TryAllocate(out var allocated))
                    {
                        stderr.WriteLine("state_revision_exhausted: no revision is left to allocate");
                        return CollectorExitCodes.RevisionExhausted;
                    }

                    if (!store.Write(allocated!, out var allocateFailure))
                    {
                        return StateFailure(stderr, allocateFailure);
                    }

                    var outcome = CollectorSnapshotTransport.Transmit(
                        client,
                        endpoint,
                        collectorId,
                        generation,
                        allocated!.LastRevision,
                        wire.Bytes,
                        read.Credential,
                        send?.Delay ?? DefaultDelay,
                        send?.NextJitterUnit ?? DefaultJitter,
                        partsReceipt: wireSchema == WireSchema.V2);

                    if (!store.Write(allocated.Record(outcome.Outcome, outcome.ConflictReason), out var recordFailure))
                    {
                        // The request left the process, so the result cannot be replayed
                        // and the next run must not assume anything about it.
                        stderr.WriteLine($"state_unrecorded: {CollectorStateFailureText.Describe(recordFailure)}");
                        ReportOutcome(stderr, outcome);
                        return CollectorExitCodes.StateUnrecorded;
                    }

                    ReportOutcome(stderr, outcome);
                    return outcome.ExitCode;
                }
            }
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// <summary>
    /// Writes the fixed outcome line, plus the ignored-reduction count when the
    /// API accepted a snapshot but kept a higher accepted total for some days.
    /// The count is a fixed token and an integer, never a response excerpt.
    /// </summary>
    private static void ReportOutcome(TextWriter stderr, SendOutcome outcome)
    {
        stderr.WriteLine(outcome.Message);
        if (outcome.IgnoredReductions > 0)
        {
            stderr.WriteLine(
                "reductions_ignored: " + outcome.IgnoredReductions.ToString(CultureInfo.InvariantCulture));
        }

        if (outcome.IgnoredPartReductions > 0)
        {
            stderr.WriteLine(
                "part_reductions_ignored: " + outcome.IgnoredPartReductions.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Reports how many days were sent with their total only, as a count.
    ///
    /// Which days lost their breakdown is deliberately not printed: the operator
    /// needs to know the breakdown is incomplete, and the days themselves are day
    /// partitions of their own usage, not diagnostics.
    /// </summary>
    private static void ReportOmittedPartDays(TextWriter stderr, int omittedPartDays)
    {
        if (omittedPartDays > 0)
        {
            stderr.WriteLine(
                "parts_omitted: " + omittedPartDays.ToString(CultureInfo.InvariantCulture)
                + " days were sent without a breakdown");
        }
    }

    private static int ShowState(CollectorSendContext? send, Stream stdout, TextWriter stderr)
    {
        if (!OperatingSystem.IsLinux() || !SystemdStateDirectory.IsSupportedPlatform)
        {
            return StateFailure(stderr, CollectorStateFailure.UnsupportedPlatform);
        }

        if (!CollectorStateStore.TryOpen(Directory(send), out var store, out var openFailure))
        {
            return StateFailure(stderr, openFailure);
        }

        using (store)
        {
            var state = store!.Read(out var readFailure);
            if (state is null)
            {
                return StateFailure(stderr, readFailure);
            }

            var document = state.ToCanonicalJson();
            stdout.Write(document, 0, document.Length);
            stdout.Flush();
            return CollectorExitCodes.Candidate;
        }
    }

    private static int InitState(
        CollectorSendContext? send,
        string collectorId,
        long generation,
        long firstRevision,
        TextWriter stderr)
    {
        if (!OperatingSystem.IsLinux() || !SystemdStateDirectory.IsSupportedPlatform)
        {
            return StateFailure(stderr, CollectorStateFailure.UnsupportedPlatform);
        }

        if (!CollectorStateStore.TryOpen(Directory(send), out var store, out var openFailure))
        {
            return StateFailure(stderr, openFailure);
        }

        using (store)
        {
            if (!store!.TryLock(out var lease))
            {
                return StateFailure(stderr, CollectorStateFailure.StateLocked);
            }

            using (lease)
            {
                var existing = store.Read(out var readFailure);
                if (existing is not null)
                {
                    // An existing state may only move forward for the same collector:
                    // another collector's state is never overwritten, the generation
                    // never goes backwards, and the floor only moves up.
                    if (!string.Equals(existing.CollectorId, collectorId, StringComparison.Ordinal))
                    {
                        stderr.WriteLine(
                            "state_mismatch: the state belongs to another collector; nothing was changed");
                        return CollectorExitCodes.StateMismatch;
                    }

                    if (generation < existing.Generation)
                    {
                        stderr.WriteLine(
                            "state_mismatch: the generation must not go backwards; nothing was changed");
                        return CollectorExitCodes.StateMismatch;
                    }

                    if (generation == existing.Generation && firstRevision <= existing.LastRevision)
                    {
                        stderr.WriteLine(
                            "state_mismatch: --first-revision must be above the last allocated revision; nothing was changed");
                        return CollectorExitCodes.StateMismatch;
                    }
                }
                else if (!IsReplaceable(readFailure))
                {
                    // A missing, empty, malformed or non-canonical document is replaced:
                    // recovering from a lost or damaged state is the operator's explicit
                    // call. A document that exists but is unreadable (wrong owner, wrong
                    // mode, a symbolic link) is not overwritten.
                    return StateFailure(stderr, readFailure);
                }

                var state = CollectorSendState.ForFirstRevision(collectorId, generation, firstRevision);
                if (!store.Write(state, out var writeFailure))
                {
                    return StateFailure(stderr, writeFailure);
                }

                stderr.WriteLine(
                    $"state_initialized: next revision {state.NextRevision.ToString(CultureInfo.InvariantCulture)}");
                return CollectorExitCodes.Candidate;
            }
        }
    }

    private static int AdvanceState(CollectorSendContext? send, TextWriter stderr)
    {
        if (!OperatingSystem.IsLinux() || !SystemdStateDirectory.IsSupportedPlatform)
        {
            return StateFailure(stderr, CollectorStateFailure.UnsupportedPlatform);
        }

        if (!CollectorStateStore.TryOpen(Directory(send), out var store, out var openFailure))
        {
            return StateFailure(stderr, openFailure);
        }

        using (store)
        {
            if (!store!.TryLock(out var lease))
            {
                return StateFailure(stderr, CollectorStateFailure.StateLocked);
            }

            using (lease)
            {
                var state = store.Read(out var readFailure);
                if (state is null)
                {
                    return StateFailure(stderr, readFailure);
                }

                var wasBlocked = state.IsBlocked;
                if (!store.Write(state.ClearBlock(), out var writeFailure))
                {
                    return StateFailure(stderr, writeFailure);
                }

                stderr.WriteLine(wasBlocked
                    ? "state_advanced: block cleared"
                    : "state_advanced: no block was set");
                return CollectorExitCodes.Candidate;
            }
        }
    }

    private static bool IsReplaceable(CollectorStateFailure failure) =>
        failure is CollectorStateFailure.StateMissing
            or CollectorStateFailure.Empty
            or CollectorStateFailure.Malformed
            or CollectorStateFailure.StateNotCanonical
            or CollectorStateFailure.StateUnsupportedVersion
            or CollectorStateFailure.StateInvalidField;

    private static SystemdStateDirectory Directory(CollectorSendContext? send) =>
        new(
            static () => SystemdStateDirectory.IsSupportedPlatform,
            send?.StateDirectory ?? SystemdStateDirectory.FromEnvironment);

    private static int StateFailure(TextWriter stderr, CollectorStateFailure failure)
    {
        stderr.WriteLine($"state_unavailable: {CollectorStateFailureText.Describe(failure)}");
        if (failure == CollectorStateFailure.StateMissing)
        {
            stderr.WriteLine("state_missing: set a revision floor with --state-init --first-revision before sending");
        }

        return CollectorExitCodes.StateUnavailable;
    }

    /// <summary>Reads one bounded document from stdin and normalizes it. Nothing is emitted here.</summary>
    private static bool TryNormalize(Stream stdin, TextWriter stderr, out SnapshotCandidate? candidate, out int exitCode)
    {
        candidate = null;
        exitCode = CollectorExitCodes.Candidate;

        var (tooLarge, document) = ReadBounded(stdin);
        if (tooLarge)
        {
            exitCode = Rejected(
                stderr,
                "input",
                "too_large",
                $"The upstream document must not exceed {TokscaleGraphContract.MaxInputBytes} bytes.");
            return false;
        }

        var result = TokscaleGraphNormalizer.Normalize(document);
        switch (result.Status)
        {
            case NormalizationStatus.Rejected:
                exitCode = Rejected(stderr, result.Field!, result.Reason!, result.Message!);
                return false;
            case NormalizationStatus.NoObservedDays:
                stderr.WriteLine($"no_snapshot: {result.Reason} (no dated row was observed; nothing was emitted)");
                exitCode = CollectorExitCodes.NoObservedDays;
                return false;
            default:
                candidate = result.Candidate;
                return true;
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

    private static bool TryParseWireSchema(string raw, out WireSchema schema)
    {
        switch (raw)
        {
            case "1":
                schema = WireSchema.V1;
                return true;
            case "2":
                schema = WireSchema.V2;
                return true;
            default:
                schema = WireSchema.V1;
                return false;
        }
    }

    private static bool TryParsePositiveInteger(string raw, out long value)
    {
        value = 0;
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

        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value >= 1
            && value <= TokscaleGraphContract.MaxSafeInteger;
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

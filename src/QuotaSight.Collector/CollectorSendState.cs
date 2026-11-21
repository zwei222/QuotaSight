using System.Globalization;
using System.Text;
using System.Text.Json;

namespace QuotaSight.Collector;

/// <summary>
/// What the API answered for one allocated revision.
/// <see cref="None"/> means the revision was allocated but no result was recorded.
/// </summary>
public enum CollectorSendOutcome
{
    /// <summary>Allocated, no result recorded yet.</summary>
    None = 0,

    /// <summary>The API confirmed the receipt for this revision.</summary>
    Accepted,

    /// <summary>The outcome is unknown (5xx, timeout or transport failure).</summary>
    Unknown,

    /// <summary>The API rejected the request as a contract violation.</summary>
    Rejected,

    /// <summary>The API answered 409; the revision is unusable.</summary>
    Conflict,

    /// <summary>2xx, but the body was not the expected receipt.</summary>
    UnverifiedReceipt,
}

/// <summary>Why the state directory or the state document could not be used.</summary>
public enum CollectorStateFailure
{
    /// <summary>No failure.</summary>
    None = 0,

    /// <summary>The state directory is Linux-only.</summary>
    UnsupportedPlatform,

    /// <summary><c>STATE_DIRECTORY</c> is unset or blank.</summary>
    DirectoryUnset,

    /// <summary><c>STATE_DIRECTORY</c> is not a fully qualified path.</summary>
    DirectoryNotAbsolute,

    /// <summary><c>STATE_DIRECTORY</c> is not its own normalized form (<c>..</c>, <c>//</c>, <c>/.</c>).</summary>
    DirectoryNotNormalized,

    /// <summary><c>STATE_DIRECTORY</c> names more than one directory.</summary>
    DirectoryMultiple,

    /// <summary>The state directory does not exist.</summary>
    DirectoryMissing,

    /// <summary>The state directory is a symbolic link and is not followed.</summary>
    DirectorySymlink,

    /// <summary>The state directory path is not a directory.</summary>
    DirectoryNotDirectory,

    /// <summary>The state directory is not owned by the effective user.</summary>
    DirectoryWrongOwner,

    /// <summary>The state directory is accessible by anybody but its owner, or is not 0700.</summary>
    DirectoryWrongMode,

    /// <summary>There is no state file. Set a floor with <c>--state-init --first-revision</c>.</summary>
    StateMissing,

    /// <summary>The state file is empty.</summary>
    Empty,

    /// <summary>The state file is not a single JSON object.</summary>
    Malformed,

    /// <summary>The state file exceeds the byte bound.</summary>
    StateTooLarge,

    /// <summary>The state file is not the exact document this tool writes.</summary>
    StateNotCanonical,

    /// <summary>The state file declares an unknown state version.</summary>
    StateUnsupportedVersion,

    /// <summary>The state file carries a value outside the contract.</summary>
    StateInvalidField,

    /// <summary>The state file could not be read, or is not a private regular file.</summary>
    StateUnreadable,

    /// <summary>Another collector process holds the state lock.</summary>
    StateLocked,

    /// <summary>The state file could not be written.</summary>
    StateWriteFailed,
}

/// <summary>Fixed stderr tokens for <see cref="CollectorStateFailure"/>. A path is never part of one.</summary>
public static class CollectorStateFailureText
{
    /// <summary>Returns the fixed word for a failure. Never carries a path or a value.</summary>
    public static string Describe(CollectorStateFailure failure) => failure.ToString();
}

/// <summary>
/// The durable local fact that makes revisions safe to allocate offline: the
/// highest revision ever allocated for one (collector, generation) pair, the
/// result of that attempt, and whether a conflict is waiting for an operator.
///
/// The document is machine-written. It is parsed strictly and must be byte
/// identical to <see cref="ToCanonicalJson"/>, so a hand edit or a truncated
/// write is refused instead of being interpreted.
/// </summary>
public sealed record CollectorSendState
{
    /// <summary>Document version written and accepted by this build.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The largest revision the API accepts.</summary>
    public const long MaxRevision = TokscaleGraphContract.MaxSafeInteger;

    /// <summary>Recorded when a 409 body does not name a known reason.</summary>
    public const string UnknownConflictReason = "conflict_unknown";

    /// <summary>Recorded when a 2xx body could not be verified as the API's receipt.</summary>
    public const string ResponseUnverifiedReason = "response_unverified";

    private static readonly string[] ConflictReasons =
    [
        "revision_conflict",
        "stale_revision",
        "reduction_unverified",
        "generation_inactive",
        UnknownConflictReason,
        ResponseUnverifiedReason,
    ];

    private CollectorSendState(
        int stateVersion,
        string collectorId,
        long generation,
        long lastRevision,
        CollectorSendOutcome lastOutcome,
        string? blockedReason)
    {
        StateVersion = stateVersion;
        CollectorId = collectorId;
        Generation = generation;
        LastRevision = lastRevision;
        LastOutcome = lastOutcome;
        BlockedReason = blockedReason;
    }

    /// <summary>Document version. Only <see cref="CurrentVersion"/> is written.</summary>
    public int StateVersion { get; }

    /// <summary>The collector this state belongs to. Never a secret.</summary>
    public string CollectorId { get; }

    /// <summary>The generation this state belongs to.</summary>
    public long Generation { get; }

    /// <summary>Highest revision ever allocated; <c>0</c> when none was allocated.</summary>
    public long LastRevision { get; }

    /// <summary>Result recorded for <see cref="LastRevision"/>.</summary>
    public CollectorSendOutcome LastOutcome { get; }

    /// <summary>Unresolved conflict reason, or <c>null</c> when sending may continue.</summary>
    public string? BlockedReason { get; }

    /// <summary>True when a conflict is waiting for an operator decision.</summary>
    public bool IsBlocked => BlockedReason is not null;

    /// <summary>The revision the next send would take.</summary>
    public long NextRevision => LastRevision + 1;

    /// <summary>True when <paramref name="collectorId"/> and <paramref name="generation"/> are this state's pair.</summary>
    public bool Matches(string collectorId, long generation) =>
        string.Equals(CollectorId, collectorId, StringComparison.Ordinal) && Generation == generation;

    /// <summary>True for a lowercase collector slug of 3-64 characters (<c>[a-z0-9_-]</c>).</summary>
    public static bool IsCollectorId(string? value)
    {
        if (value is null || value.Length is < 3 or > 64)
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

    /// <summary>Builds a validated state. Throws instead of storing an unusable value.</summary>
    public static CollectorSendState Create(
        string collectorId,
        long generation,
        long lastRevision,
        CollectorSendOutcome lastOutcome = CollectorSendOutcome.None,
        string? blockedReason = null)
    {
        if (!IsCollectorId(collectorId))
        {
            throw new ArgumentException("A collector id is a lowercase slug of 3-64 characters.", nameof(collectorId));
        }

        if (generation is < 1 or > MaxRevision)
        {
            throw new ArgumentOutOfRangeException(nameof(generation), generation, "generation must be a positive safe integer.");
        }

        if (lastRevision is < 0 or > MaxRevision)
        {
            throw new ArgumentOutOfRangeException(nameof(lastRevision), lastRevision, "lastRevision must be a safe integer of at least zero.");
        }

        if (!Enum.IsDefined(lastOutcome))
        {
            throw new ArgumentOutOfRangeException(nameof(lastOutcome));
        }

        if (blockedReason is not null && !IsConflictReason(blockedReason))
        {
            throw new ArgumentException("A blocked reason must be an allowlisted word.", nameof(blockedReason));
        }

        return new CollectorSendState(CurrentVersion, collectorId, generation, lastRevision, lastOutcome, blockedReason);
    }

    /// <summary>State whose next allocated revision is <paramref name="firstRevision"/>.</summary>
    public static CollectorSendState ForFirstRevision(string collectorId, long generation, long firstRevision)
    {
        if (firstRevision is < 1 or > MaxRevision)
        {
            throw new ArgumentOutOfRangeException(nameof(firstRevision), firstRevision, "firstRevision must be a positive safe integer.");
        }

        return Create(collectorId, generation, firstRevision - 1);
    }

    /// <summary>Reserves the next revision. Fails when no revision is left.</summary>
    public bool TryAllocate(out CollectorSendState? allocated)
    {
        if (LastRevision >= MaxRevision)
        {
            allocated = null;
            return false;
        }

        allocated = Create(CollectorId, Generation, LastRevision + 1);
        return true;
    }

    /// <summary>Reserves the next revision. Throws when no revision is left.</summary>
    public CollectorSendState Allocate() =>
        TryAllocate(out var allocated)
            ? allocated!
            : throw new InvalidOperationException("No revision is left to allocate.");

    /// <summary>
    /// Records the result of the allocated revision. A 409 blocks with a reason
    /// from the allowlist (an unknown reason becomes
    /// <see cref="UnknownConflictReason"/>), and a 2xx whose body could not be
    /// verified blocks with <see cref="ResponseUnverifiedReason"/>: in both cases
    /// the local record and the server disagree, and only an operator can decide.
    /// Any other result never blocks and never clears a block.
    /// </summary>
    public CollectorSendState Record(CollectorSendOutcome outcome, string? conflictReason)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        var blocked = outcome switch
        {
            CollectorSendOutcome.Conflict => NormalizeConflictReason(conflictReason),
            CollectorSendOutcome.UnverifiedReceipt => ResponseUnverifiedReason,
            _ => BlockedReason,
        };

        return Create(CollectorId, Generation, LastRevision, outcome, blocked);
    }

    /// <summary>Clears an operator-acknowledged block. Only an explicit command does this.</summary>
    public CollectorSendState ClearBlock() =>
        Create(CollectorId, Generation, LastRevision, LastOutcome);

    /// <summary>Maps any reason to the allowlist.</summary>
    public static string NormalizeConflictReason(string? reason) =>
        reason is not null && IsConflictReason(reason) ? reason : UnknownConflictReason;

    /// <summary>True when <paramref name="reason"/> is one of the fixed 409 reasons.</summary>
    public static bool IsConflictReason(string reason) =>
        Array.Exists(ConflictReasons, candidate => string.Equals(candidate, reason, StringComparison.Ordinal));

    /// <summary>The exact bytes of the state document, with one trailing line feed.</summary>
    public byte[] ToCanonicalJson()
    {
        var builder = new StringBuilder(192);
        builder.Append("{\"stateVersion\":").Append(StateVersion.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"collectorId\":\"").Append(CollectorId).Append('"');
        builder.Append(",\"generation\":").Append(Generation.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"lastRevision\":").Append(LastRevision.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"lastOutcome\":\"").Append(OutcomeToken(LastOutcome)).Append('"');
        builder.Append(",\"blockedReason\":");
        if (BlockedReason is null)
        {
            builder.Append("null");
        }
        else
        {
            builder.Append('"').Append(BlockedReason).Append('"');
        }

        builder.Append("}\n");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>
    /// Parses the document strictly: known version, exactly the six fields, typed
    /// values, allowlisted words, and bytes identical to
    /// <see cref="ToCanonicalJson"/>.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> document, out CollectorSendState? state, out CollectorStateFailure failure)
    {
        state = null;
        if (document.IsEmpty)
        {
            failure = CollectorStateFailure.Empty;
            return false;
        }

        if (document.Length > CollectorStateStore.MaxStateBytes)
        {
            failure = CollectorStateFailure.StateTooLarge;
            return false;
        }

        if (document.StartsWith(Utf8Bom))
        {
            failure = CollectorStateFailure.StateNotCanonical;
            return false;
        }

        var fields = new Fields();
        try
        {
            var reader = new Utf8JsonReader(document, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 2,
            });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                failure = CollectorStateFailure.Malformed;
                return false;
            }

            var ended = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    ended = true;
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    failure = CollectorStateFailure.Malformed;
                    return false;
                }

                var name = reader.GetString()!;
                if (!reader.Read())
                {
                    failure = CollectorStateFailure.Malformed;
                    return false;
                }

                var read = fields.TryRead(name, ref reader);
                if (read != FieldRead.Ok)
                {
                    failure = read == FieldRead.Duplicate
                        ? CollectorStateFailure.StateNotCanonical
                        : CollectorStateFailure.StateInvalidField;
                    return false;
                }
            }

            if (!ended)
            {
                failure = CollectorStateFailure.Malformed;
                return false;
            }

            bool trailing;
            try
            {
                trailing = reader.Read();
            }
            catch (JsonException)
            {
                // A second value after the object: trailing content, not a broken value.
                trailing = true;
            }

            if (trailing)
            {
                failure = CollectorStateFailure.StateNotCanonical;
                return false;
            }
        }
        catch (JsonException)
        {
            failure = CollectorStateFailure.Malformed;
            return false;
        }
        catch (InvalidOperationException)
        {
            failure = CollectorStateFailure.Malformed;
            return false;
        }

        if (!fields.Complete)
        {
            // A document without every field is not one this tool writes.
            failure = CollectorStateFailure.StateNotCanonical;
            return false;
        }

        if (fields.Version != CurrentVersion)
        {
            failure = CollectorStateFailure.StateUnsupportedVersion;
            return false;
        }

        if (!IsCollectorId(fields.CollectorId)
            || fields.Generation is not (>= 1 and <= MaxRevision)
            || fields.LastRevision is not (>= 0 and <= MaxRevision)
            || !TryParseOutcome(fields.LastOutcome!, out var outcome)
            || (fields.BlockedReason is not null && !IsConflictReason(fields.BlockedReason)))
        {
            failure = CollectorStateFailure.StateInvalidField;
            return false;
        }

        var parsed = new CollectorSendState(
            fields.Version,
            fields.CollectorId!,
            fields.Generation!.Value,
            fields.LastRevision!.Value,
            outcome,
            fields.BlockedReason);

        if (!parsed.ToCanonicalJson().AsSpan().SequenceEqual(document))
        {
            failure = CollectorStateFailure.StateNotCanonical;
            return false;
        }

        state = parsed;
        failure = CollectorStateFailure.None;
        return true;
    }

    /// <summary>Omits the collector id and the revision.</summary>
    public override string ToString() => nameof(CollectorSendState);

    private static string OutcomeToken(CollectorSendOutcome outcome) => outcome switch
    {
        CollectorSendOutcome.None => "none",
        CollectorSendOutcome.Accepted => "accepted",
        CollectorSendOutcome.Unknown => "unknown",
        CollectorSendOutcome.Rejected => "rejected",
        CollectorSendOutcome.Conflict => "conflict",
        CollectorSendOutcome.UnverifiedReceipt => "unverified_receipt",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static bool TryParseOutcome(string token, out CollectorSendOutcome outcome)
    {
        foreach (var candidate in Enum.GetValues<CollectorSendOutcome>())
        {
            if (string.Equals(OutcomeToken(candidate), token, StringComparison.Ordinal))
            {
                outcome = candidate;
                return true;
            }
        }

        outcome = CollectorSendOutcome.None;
        return false;
    }

    private static bool IsLowerAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private enum FieldRead
    {
        Ok,
        InvalidField,
        Duplicate,
    }

    private sealed class Fields
    {
        public int Version { get; private set; } = -1;

        public string? CollectorId { get; private set; }

        public long? Generation { get; private set; }

        public long? LastRevision { get; private set; }

        public string? LastOutcome { get; private set; }

        public string? BlockedReason { get; private set; }

        private bool BlockedSeen { get; set; }

        public bool Complete =>
            Version >= 0 && CollectorId is not null && Generation is not null
            && LastRevision is not null && LastOutcome is not null && BlockedSeen;

        public FieldRead TryRead(string name, ref Utf8JsonReader reader)
        {
            switch (name)
            {
                case "stateVersion":
                    if (Version >= 0)
                    {
                        return FieldRead.Duplicate;
                    }

                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var version))
                    {
                        return FieldRead.InvalidField;
                    }

                    Version = version;
                    return FieldRead.Ok;
                case "collectorId":
                    if (CollectorId is not null)
                    {
                        return FieldRead.Duplicate;
                    }

                    if (reader.TokenType != JsonTokenType.String)
                    {
                        return FieldRead.InvalidField;
                    }

                    CollectorId = reader.GetString();
                    return FieldRead.Ok;
                case "generation":
                    if (Generation is not null)
                    {
                        return FieldRead.Duplicate;
                    }

                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var generation))
                    {
                        return FieldRead.InvalidField;
                    }

                    Generation = generation;
                    return FieldRead.Ok;
                case "lastRevision":
                    if (LastRevision is not null)
                    {
                        return FieldRead.Duplicate;
                    }

                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var lastRevision))
                    {
                        return FieldRead.InvalidField;
                    }

                    LastRevision = lastRevision;
                    return FieldRead.Ok;
                case "lastOutcome":
                    if (LastOutcome is not null)
                    {
                        return FieldRead.Duplicate;
                    }

                    if (reader.TokenType != JsonTokenType.String)
                    {
                        return FieldRead.InvalidField;
                    }

                    LastOutcome = reader.GetString();
                    return FieldRead.Ok;
                case "blockedReason":
                    if (BlockedSeen)
                    {
                        return FieldRead.Duplicate;
                    }

                    BlockedSeen = true;
                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        BlockedReason = null;
                        return FieldRead.Ok;
                    }

                    if (reader.TokenType != JsonTokenType.String)
                    {
                        return FieldRead.InvalidField;
                    }

                    BlockedReason = reader.GetString();
                    return FieldRead.Ok;
                default:
                    // A field this tool would never write: the document is not ours.
                    return FieldRead.Duplicate;
            }
        }
    }
}

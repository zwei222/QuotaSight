using System.Runtime.Versioning;
using System.Text;

namespace QuotaSight.Collector.Tests;

/// <summary>
/// The state document is machine-written: it is parsed strictly, it must be
/// byte-identical to what the collector writes, and every transition is a pure
/// function. No test here touches a real state directory.
/// </summary>
public sealed class CollectorSendStateTests
{
    private const string CollectorId = "col-state";
    private const long Generation = 4;

    private static CollectorSendState Sample() =>
        CollectorSendState.Create(CollectorId, Generation, lastRevision: 3, CollectorSendOutcome.Accepted);

    private static string Document(
        string version = "1",
        string collectorId = CollectorId,
        string generation = "4",
        string lastRevision = "3",
        string lastOutcome = "\"accepted\"",
        string blockedReason = "null") =>
        "{\"stateVersion\":" + version
        + ",\"collectorId\":\"" + collectorId
        + "\",\"generation\":" + generation
        + ",\"lastRevision\":" + lastRevision
        + ",\"lastOutcome\":" + lastOutcome
        + ",\"blockedReason\":" + blockedReason + "}\n";

    private static CollectorStateFailure Parse(string document) =>
        Parse(Encoding.UTF8.GetBytes(document));

    private static CollectorStateFailure Parse(byte[] document)
    {
        var parsed = CollectorSendState.TryParse(document, out var state, out var failure);
        Assert.Equal(parsed, failure == CollectorStateFailure.None);
        Assert.Equal(parsed, state is not null);
        return failure;
    }

    [Fact]
    public void CanonicalDocumentIsCompactWithOneTrailingNewline()
    {
        var text = Encoding.UTF8.GetString(Sample().ToCanonicalJson());

        Assert.Equal(Document(), text);
    }

    [Fact]
    public void ParseAcceptsTheCanonicalDocument()
    {
        Assert.True(CollectorSendState.TryParse(Encoding.UTF8.GetBytes(Document()), out var state, out var failure));

        Assert.Equal(CollectorStateFailure.None, failure);
        Assert.NotNull(state);
        Assert.Equal(CollectorSendState.CurrentVersion, state!.StateVersion);
        Assert.Equal(CollectorId, state.CollectorId);
        Assert.Equal(Generation, state.Generation);
        Assert.Equal(3, state.LastRevision);
        Assert.Equal(CollectorSendOutcome.Accepted, state.LastOutcome);
        Assert.Null(state.BlockedReason);
        Assert.False(state.IsBlocked);
    }

    [Fact]
    public void RoundTripKeepsEveryField()
    {
        var original = CollectorSendState.Create(
            CollectorId,
            Generation,
            lastRevision: 9,
            CollectorSendOutcome.Conflict,
            "stale_revision");

        Assert.True(CollectorSendState.TryParse(original.ToCanonicalJson(), out var parsed, out var failure));

        Assert.Equal(CollectorStateFailure.None, failure);
        Assert.Equal(original, parsed);
    }

    [Theory]
    [InlineData("no-trailing-newline", "{\"stateVersion\":1,\"collectorId\":\"col-state\",\"generation\":4,\"lastRevision\":3,\"lastOutcome\":\"accepted\",\"blockedReason\":null}")]
    [InlineData("space-after-colon", "{\"stateVersion\": 1,\"collectorId\":\"col-state\",\"generation\":4,\"lastRevision\":3,\"lastOutcome\":\"accepted\",\"blockedReason\":null}\n")]
    [InlineData("reordered-keys", "{\"lastRevision\":3,\"stateVersion\":1,\"collectorId\":\"col-state\",\"generation\":4,\"lastOutcome\":\"accepted\",\"blockedReason\":null}\n")]
    [InlineData("trailing-object", "{\"stateVersion\":1,\"collectorId\":\"col-state\",\"generation\":4,\"lastRevision\":3,\"lastOutcome\":\"accepted\",\"blockedReason\":null}\n{}")]
    [InlineData("unknown-field", "{\"stateVersion\":1,\"collectorId\":\"col-state\",\"generation\":4,\"lastRevision\":3,\"lastOutcome\":\"accepted\",\"blockedReason\":null,\"note\":1}\n")]
    [InlineData("duplicate-field", "{\"stateVersion\":1,\"stateVersion\":1,\"collectorId\":\"col-state\",\"generation\":4,\"lastRevision\":3,\"lastOutcome\":\"accepted\",\"blockedReason\":null}\n")]
    [InlineData("byte-order-mark", "\uFEFF{\"stateVersion\":1,\"collectorId\":\"col-state\",\"generation\":4,\"lastRevision\":3,\"lastOutcome\":\"accepted\",\"blockedReason\":null}\n")]
    [InlineData("blank-line", "{\"stateVersion\":1,\"collectorId\":\"col-state\",\"generation\":4,\"lastRevision\":3,\"lastOutcome\":\"accepted\",\"blockedReason\":null}\n\n")]
    public void ParseRejectsBytesItWouldNotWriteItself(string label, string document)
    {
        var failure = Parse(document);

        Assert.True(failure == CollectorStateFailure.StateNotCanonical, $"{label} was parsed as {failure}");
    }

    [Fact]
    public void EmptyDocumentIsTyped()
    {
        Assert.Equal(CollectorStateFailure.Empty, Parse([]));
        Assert.Equal(CollectorStateFailure.Empty, Parse(""u8.ToArray()));
    }

    [Fact]
    public void OversizedDocumentIsTyped()
    {
        var oversized = Encoding.UTF8.GetBytes(Document() + new string(' ', CollectorStateStore.MaxStateBytes));

        Assert.Equal(CollectorStateFailure.StateTooLarge, Parse(oversized));
    }

    [Fact]
    public void UnsupportedVersionIsTyped()
    {
        Assert.Equal(CollectorStateFailure.StateUnsupportedVersion, Parse(Document(version: "2")));
        Assert.Equal(CollectorStateFailure.StateUnsupportedVersion, Parse(Document(version: "0")));
    }

    private static CollectorStateFailure ParseCollectorId(string raw) =>
        Parse("{\"stateVersion\":1,\"collectorId\":" + raw + ",\"generation\":4,\"lastRevision\":3,\"lastOutcome\":\"accepted\",\"blockedReason\":null}\n");

    [Theory]
    [InlineData("\"COL-STATE\"")]
    [InlineData("\"\"")]
    [InlineData("\"col state\"")]
    [InlineData("null")]
    [InlineData("1")]
    public void CollectorIdMustBeALowercaseSlug(string raw)
    {
        Assert.Equal(CollectorStateFailure.StateInvalidField, ParseCollectorId(raw));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("4.0")]
    [InlineData("\"4\"")]
    [InlineData("9007199254740992")]
    [InlineData("null")]
    public void GenerationMustBeAPositiveSafeInteger(string raw)
    {
        Assert.Equal(CollectorStateFailure.StateInvalidField, Parse(Document(generation: raw)));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"3\"")]
    [InlineData("9007199254740992")]
    [InlineData("null")]
    public void LastRevisionMustBeAZeroBasedSafeInteger(string raw)
    {
        Assert.Equal(CollectorStateFailure.StateInvalidField, Parse(Document(lastRevision: raw)));
    }

    [Theory]
    [InlineData("\"sent\"")]
    [InlineData("\"Accepted\"")]
    [InlineData("null")]
    [InlineData("0")]
    public void LastOutcomeMustBeAWordOfThisContract(string raw)
    {
        Assert.Equal(CollectorStateFailure.StateInvalidField, Parse(Document(lastOutcome: raw)));
    }

    [Theory]
    [InlineData("\"something_else\"")]
    [InlineData("\"\"")]
    [InlineData("0")]
    [InlineData("\"stale_revision \"")]
    public void BlockedReasonMustBeAllowlistedOrNull(string raw)
    {
        Assert.Equal(CollectorStateFailure.StateInvalidField, Parse(Document(blockedReason: raw)));
    }

    [Theory]
    [InlineData("revision_conflict")]
    [InlineData("stale_revision")]
    [InlineData("generation_inactive")]
    [InlineData("conflict_unknown")]
    [InlineData("response_unverified")]
    public void AllowlistedBlockedReasonsParse(string reason)
    {
        Assert.True(CollectorSendState.TryParse(
            Encoding.UTF8.GetBytes(Document(blockedReason: "\"" + reason + "\"")),
            out var state,
            out var failure));

        Assert.Equal(CollectorStateFailure.None, failure);
        Assert.Equal(reason, state!.BlockedReason);
        Assert.True(state.IsBlocked);
    }

    [Fact]
    public void AStateBlockedByThePreviousContractStillParses()
    {
        // `reduction_unverified` was a 409 reason while a decreased day refused
        // the whole snapshot. A state file written then must still open, so the
        // operator can clear it with `--state-advance`.
        Assert.True(CollectorSendState.TryParse(
            Encoding.UTF8.GetBytes(Document(blockedReason: "\"reduction_unverified\"")),
            out var state,
            out var failure));

        Assert.Equal(CollectorStateFailure.None, failure);
        Assert.Equal("reduction_unverified", state!.BlockedReason);
        Assert.True(state.IsBlocked);
    }

    [Fact]
    public void ThePreviousReductionReasonIsNoLongerRecorded()
    {
        var recorded = Sample().Record(CollectorSendOutcome.Conflict, "reduction_unverified");

        Assert.False(CollectorSendState.IsConflictReason("reduction_unverified"));
        Assert.Equal(CollectorSendState.UnknownConflictReason, recorded.BlockedReason);
    }

    [Fact]
    public void NonObjectDocumentIsTyped()
    {
        Assert.Equal(CollectorStateFailure.Malformed, Parse("[]\n"));
        Assert.Equal(CollectorStateFailure.Malformed, Parse("{"));
    }

    [Fact]
    public void AllocateUsesTheNextRevisionAndClearsThePreviousResult()
    {
        var allocated = Sample().Allocate();

        Assert.Equal(4, allocated.LastRevision);
        Assert.Equal(CollectorSendOutcome.None, allocated.LastOutcome);
        Assert.Null(allocated.BlockedReason);
        Assert.Equal(CollectorId, allocated.CollectorId);
        Assert.Equal(Generation, allocated.Generation);
    }

    [Fact]
    public void AllocateRefusesToExceedTheLargestSafeInteger()
    {
        var exhausted = CollectorSendState.Create(CollectorId, Generation, CollectorSendState.MaxRevision);

        Assert.False(exhausted.TryAllocate(out var allocated));
        Assert.Null(allocated);
        Assert.Throws<InvalidOperationException>(exhausted.Allocate);
    }

    [Fact]
    public void NextRevisionIsOneAboveTheRecordedRevision()
    {
        Assert.Equal(4, Sample().NextRevision);
        Assert.Equal(1, CollectorSendState.Create(CollectorId, Generation, lastRevision: 0).NextRevision);
    }

    [Theory]
    [InlineData(CollectorSendOutcome.Accepted)]
    [InlineData(CollectorSendOutcome.Unknown)]
    [InlineData(CollectorSendOutcome.Rejected)]
    public void NonBlockingResultsNeverBlock(CollectorSendOutcome outcome)
    {
        var recorded = Sample().Record(outcome, "stale_revision");

        Assert.Equal(outcome, recorded.LastOutcome);
        Assert.Null(recorded.BlockedReason);
        Assert.False(recorded.IsBlocked);
        Assert.Equal(3, recorded.LastRevision);
    }

    [Theory]
    [InlineData("revision_conflict")]
    [InlineData("stale_revision")]
    [InlineData("generation_inactive")]
    public void AllowlistedConflictReasonsBlock(string reason)
    {
        var recorded = Sample().Record(CollectorSendOutcome.Conflict, reason);

        Assert.Equal(CollectorSendOutcome.Conflict, recorded.LastOutcome);
        Assert.Equal(reason, recorded.BlockedReason);
        Assert.True(recorded.IsBlocked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("internal_error")]
    [InlineData("Stale_Revision")]
    [InlineData("stale revision")]
    public void UnknownConflictReasonsBecomeConflictUnknown(string? reason)
    {
        var recorded = Sample().Record(CollectorSendOutcome.Conflict, reason);

        Assert.Equal(CollectorSendState.UnknownConflictReason, recorded.BlockedReason);
        Assert.True(recorded.IsBlocked);
    }

    [Fact]
    public void AnUnverifiedReceiptBlocksWithItsOwnReason()
    {
        var recorded = Sample().Record(CollectorSendOutcome.UnverifiedReceipt, null);

        Assert.Equal(CollectorSendOutcome.UnverifiedReceipt, recorded.LastOutcome);
        Assert.Equal(CollectorSendState.ResponseUnverifiedReason, recorded.BlockedReason);
        Assert.True(recorded.IsBlocked);
        Assert.Equal(3, recorded.LastRevision);
        Assert.Equal(CollectorSendState.ResponseUnverifiedReason, "response_unverified");
    }

    [Fact]
    public void AResponseUnverifiedBlockIsClearedOnlyByClearBlock()
    {
        var blocked = Sample().Record(CollectorSendOutcome.UnverifiedReceipt, null);

        var recorded = blocked.Record(CollectorSendOutcome.Accepted, null);

        Assert.Equal(CollectorSendState.ResponseUnverifiedReason, recorded.BlockedReason);

        var cleared = blocked.ClearBlock();

        Assert.Null(cleared.BlockedReason);
        Assert.Equal(CollectorSendOutcome.UnverifiedReceipt, cleared.LastOutcome);
    }

    [Fact]
    public void ARelatedBlockSurvivesAnUnrelatedResult()
    {
        var blocked = Sample().Record(CollectorSendOutcome.Conflict, "stale_revision");

        var recorded = blocked.Record(CollectorSendOutcome.Rejected, null);

        Assert.Equal("stale_revision", recorded.BlockedReason);
        Assert.Equal(CollectorSendOutcome.Rejected, recorded.LastOutcome);
    }

    [Fact]
    public void ClearBlockKeepsTheRevisionAndTheResult()
    {
        var blocked = Sample().Record(CollectorSendOutcome.Conflict, "stale_revision");

        var cleared = blocked.ClearBlock();

        Assert.Null(cleared.BlockedReason);
        Assert.False(cleared.IsBlocked);
        Assert.Equal(3, cleared.LastRevision);
        Assert.Equal(CollectorSendOutcome.Conflict, cleared.LastOutcome);
    }

    [Fact]
    public void ForFirstRevisionSetsTheFloorSoTheNextSendUsesIt()
    {
        var state = CollectorSendState.ForFirstRevision(CollectorId, Generation, firstRevision: 5);

        Assert.Equal(4, state.LastRevision);
        Assert.Equal(5, state.NextRevision);
        Assert.Equal(CollectorSendOutcome.None, state.LastOutcome);
        Assert.Null(state.BlockedReason);
        Assert.Equal(CollectorId, state.CollectorId);
        Assert.Equal(Generation, state.Generation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FirstRevisionMustBePositive(long firstRevision)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CollectorSendState.ForFirstRevision(CollectorId, Generation, firstRevision));
    }

    [Fact]
    public void MatchesOnlyTheSameCollectorAndGeneration()
    {
        var state = Sample();

        Assert.True(state.Matches(CollectorId, Generation));
        Assert.False(state.Matches("col-other", Generation));
        Assert.False(state.Matches(CollectorId, Generation + 1));
    }

    [Fact]
    public void CreateRejectsAnUnusableCollectorIdOrGeneration()
    {
        Assert.Throws<ArgumentException>(() => CollectorSendState.Create("COL", Generation, 0));
        Assert.Throws<ArgumentException>(() => CollectorSendState.Create(string.Empty, Generation, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CollectorSendState.Create(CollectorId, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CollectorSendState.Create(CollectorId, Generation, -1));
        Assert.Throws<ArgumentException>(() => CollectorSendState.Create(CollectorId, Generation, 0, CollectorSendOutcome.Accepted, "not_allowlisted"));
    }

    [Fact]
    public void TheDocumentCarriesNoPathOrUrlOrCredentialName()
    {
        var text = Encoding.UTF8.GetString(Sample().ToCanonicalJson());

        Assert.DoesNotContain("/", text, StringComparison.Ordinal);
        Assert.DoesNotContain("http", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("STATE_DIRECTORY", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ToStringOmitsTheCollectorId()
    {
        Assert.DoesNotContain(CollectorId, Sample().ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CollectorStateFailure.UnsupportedPlatform)]
    [InlineData(CollectorStateFailure.DirectoryUnset)]
    [InlineData(CollectorStateFailure.DirectoryNotAbsolute)]
    [InlineData(CollectorStateFailure.DirectoryNotNormalized)]
    [InlineData(CollectorStateFailure.DirectoryMultiple)]
    [InlineData(CollectorStateFailure.DirectoryMissing)]
    [InlineData(CollectorStateFailure.DirectorySymlink)]
    [InlineData(CollectorStateFailure.DirectoryNotDirectory)]
    [InlineData(CollectorStateFailure.DirectoryWrongOwner)]
    [InlineData(CollectorStateFailure.DirectoryWrongMode)]
    [InlineData(CollectorStateFailure.StateMissing)]
    [InlineData(CollectorStateFailure.StateTooLarge)]
    [InlineData(CollectorStateFailure.StateNotCanonical)]
    [InlineData(CollectorStateFailure.StateUnsupportedVersion)]
    [InlineData(CollectorStateFailure.StateInvalidField)]
    [InlineData(CollectorStateFailure.StateUnreadable)]
    [InlineData(CollectorStateFailure.StateLocked)]
    [InlineData(CollectorStateFailure.StateWriteFailed)]
    public void EveryFailureTokenIsAFixedWord(CollectorStateFailure failure)
    {
        var token = CollectorStateFailureText.Describe(failure);

        Assert.NotEmpty(token);
        Assert.DoesNotContain("/", token, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", token, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", token, StringComparison.Ordinal);
        Assert.Matches("^[A-Za-z][A-Za-z0-9]*$", token);
    }
}

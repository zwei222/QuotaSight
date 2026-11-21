using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Text;

namespace QuotaSight.Collector.Tests;

/// <summary>
/// Revision allocation is a local, durable fact: the number is written and
/// fsynced before the request leaves the process, a number is never reused, and
/// only a conflict that needs a human blocks the next send. Every test uses a
/// private temporary state directory and a fake handler.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class CollectorSendFlowTests
{
    private const string StateCollectorId = "col-test";

    private static string[] InitArgs(
        long firstRevision,
        string collectorId = StateCollectorId,
        long generation = SendHarness.Generation) =>
    [
        "--state-init",
        "--first-revision",
        firstRevision.ToString(CultureInfo.InvariantCulture),
        "--collector-id",
        collectorId,
        "--generation",
        generation.ToString(CultureInfo.InvariantCulture),
    ];

    private static string SnapshotPath(long revision) =>
        "/v1/collectors/" + SendHarness.CollectorId + "/snapshots/" + revision.ToString(CultureInfo.InvariantCulture);

    [LinuxFact]
    public void FirstSendAllocatesTheRevisionAboveTheFloor()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.Equal(SnapshotPath(3), Assert.Single(harness.Handler.Captured).Path);
        var state = harness.ReadState()!;
        Assert.Equal(3, state.LastRevision);
        Assert.Equal(CollectorSendOutcome.Accepted, state.LastOutcome);
        Assert.False(state.IsBlocked);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void ConsecutiveSendsUseFreshRevisions()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);

        Assert.Equal(CollectorExitCodes.Candidate, harness.RunSend().ExitCode);
        Assert.Equal(CollectorExitCodes.Candidate, harness.RunSend().ExitCode);

        Assert.Equal([SnapshotPath(3), SnapshotPath(4)], harness.Handler.Captured.Select(request => request.Path).ToArray());
        Assert.Equal(4, harness.ReadState()!.LastRevision);
    }

    [LinuxFact]
    public void AFailedSendConsumesTheRevisionAndTheNextSendMovesOn()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(HttpStatusCode.ServiceUnavailable, "synthetic-secret-value");
        harness.Handler.Enqueue(HttpStatusCode.ServiceUnavailable, "synthetic-secret-value");
        harness.Handler.Enqueue(HttpStatusCode.ServiceUnavailable, "synthetic-secret-value");

        var failed = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendUnavailable, failed.ExitCode);
        Assert.Equal(CollectorSendOutcome.Unknown, harness.ReadState()!.LastOutcome);
        Assert.Equal(3, harness.ReadState()!.LastRevision);

        var next = harness.RunSend();

        Assert.Equal(CollectorExitCodes.Candidate, next.ExitCode);
        Assert.Equal(4, harness.ReadState()!.LastRevision);
        Assert.Equal(SnapshotPath(4), harness.Handler.Captured[^1].Path);
        SendHarness.AssertNoLeak(failed, next);
    }

    [LinuxTheory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    [InlineData(HttpStatusCode.NotFound)]
    public void ContractViolationsAreDroppedWithoutBlocking(HttpStatusCode status)
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(status, "synthetic-secret-value");

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendRejected, run.ExitCode);
        var state = harness.ReadState()!;
        Assert.Equal(CollectorSendOutcome.Rejected, state.LastOutcome);
        Assert.False(state.IsBlocked);
        Assert.Equal(3, state.LastRevision);

        Assert.Equal(CollectorExitCodes.Candidate, harness.RunSend().ExitCode);
        Assert.Equal(SnapshotPath(4), harness.Handler.Captured[^1].Path);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxTheory]
    [InlineData("revision_conflict")]
    [InlineData("stale_revision")]
    [InlineData("reduction_unverified")]
    [InlineData("generation_inactive")]
    public void AConflictBlocksUntilTheOperatorAdvances(string reason)
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(HttpStatusCode.Conflict, "{\"error\":{\"code\":\"" + reason + "\",\"message\":\"synthetic-secret-value\"}}");

        var conflicted = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendConflict, conflicted.ExitCode);
        Assert.Contains("send_conflict: " + reason, conflicted.Stderr, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Captured);
        var state = harness.ReadState()!;
        Assert.Equal(reason, state.BlockedReason);
        Assert.Equal(3, state.LastRevision);

        var readsAfterConflict = harness.Credentials.Reads;
        var refused = harness.RunSend();

        Assert.Equal(CollectorExitCodes.StateBlocked, refused.ExitCode);
        Assert.Contains("state_blocked: " + reason, refused.Stderr, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Captured);
        Assert.Equal(readsAfterConflict, harness.Credentials.Reads);

        var advanced = harness.Run(AdvanceArgs());

        Assert.Equal(CollectorExitCodes.Candidate, advanced.ExitCode);
        Assert.Contains("state_advanced: block cleared", advanced.Stderr, StringComparison.Ordinal);
        Assert.False(harness.ReadState()!.IsBlocked);

        Assert.Equal(CollectorExitCodes.Candidate, harness.RunSend().ExitCode);
        Assert.Equal(SnapshotPath(4), harness.Handler.Captured[^1].Path);
        SendHarness.AssertNoLeak(conflicted, refused, advanced);
    }

    [LinuxTheory]
    [InlineData("{\"error\":{\"code\":\"internal_error\"}}")]
    [InlineData("{\"error\":{\"message\":\"no code\"}}")]
    [InlineData("{\"code\":\"stale_revision\"}")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void AnUnrecognisedConflictBodyBlocksAsConflictUnknown(string body)
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(HttpStatusCode.Conflict, body);

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendConflict, run.ExitCode);
        Assert.Contains("send_conflict: " + CollectorSendState.UnknownConflictReason, run.Stderr, StringComparison.Ordinal);
        var state = harness.ReadState()!;
        Assert.Equal(CollectorSendState.UnknownConflictReason, state.BlockedReason);
        Assert.Equal(CollectorExitCodes.StateBlocked, harness.RunSend().ExitCode);
        SendHarness.AssertNoLeak(run, "internal_error", "stale_revision\",\"details");
    }

    [LinuxFact]
    public void AnOversizedConflictBodyBlocksAsConflictUnknown()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(
            HttpStatusCode.Conflict,
            "{\"error\":{\"code\":\"stale_revision\"}}"u8.ToArray(),
            contentLength: CollectorSnapshotTransport.MaxResponseBytes + 1);

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendConflict, run.ExitCode);
        Assert.Equal(CollectorSendState.UnknownConflictReason, harness.ReadState()!.BlockedReason);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void MissingStateRefusesToSendWithoutReadingCredentials()
    {
        using var harness = SendHarness.Create();

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.StateUnavailable, run.ExitCode);
        Assert.Contains("state_unavailable: StateMissing", run.Stderr, StringComparison.Ordinal);
        Assert.Contains("state-init", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Captured);
        Assert.Equal(0, harness.Credentials.Reads);
        Assert.False(harness.StateFileExists);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void AnUnsetStateDirectoryRefusesToSend()
    {
        using var harness = SendHarness.CreateWithoutStateDirectory();

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.StateUnavailable, run.ExitCode);
        Assert.Contains("state_unavailable: DirectoryUnset", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Captured);
        Assert.Equal(0, harness.Credentials.Reads);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void AnUnsafeStateDirectoryRefusesToSend()
    {
        using var harness = SendHarness.CreateAt(Path.Combine(Path.GetTempPath(), "qs-state-missing-" + Guid.NewGuid().ToString("N")[..8]));

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.StateUnavailable, run.ExitCode);
        Assert.Contains("state_unavailable: DirectoryMissing", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Captured);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void ADifferentCollectorIdInTheStateRefusesToSend()
    {
        using var harness = SendHarness.Create();
        harness.WriteStateBytes(CollectorSendState.Create("col-other", SendHarness.Generation, 2).ToCanonicalJson());
        SendHarness.SetMode(harness.StateFilePath, SendHarness.PrivateFileMode);

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.StateMismatch, run.ExitCode);
        Assert.Contains("state_mismatch", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Captured);
        Assert.Equal(0, harness.Credentials.Reads);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void ADifferentGenerationInTheStateRefusesToSend()
    {
        using var harness = SendHarness.Create();
        harness.WriteStateBytes(CollectorSendState.Create(SendHarness.CollectorId, SendHarness.Generation + 1, 2).ToCanonicalJson());
        SendHarness.SetMode(harness.StateFilePath, SendHarness.PrivateFileMode);

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.StateMismatch, run.ExitCode);
        Assert.Empty(harness.Handler.Captured);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void ACredentialFailureDoesNotConsumeARevision()
    {
        using var harness = SendHarness.Create(CollectorCredentialResult.Failed(CollectorCredentialFailure.DirectoryUnset));
        harness.WriteState(2);
        var before = harness.ReadStateBytes();

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.CredentialUnavailable, run.ExitCode);
        Assert.Empty(harness.Handler.Captured);
        Assert.Equal(before, harness.ReadStateBytes());
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void RejectedInputDoesNotConsumeARevision()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        var before = harness.ReadStateBytes();

        var run = harness.RunSend(FixtureFiles.Bytes(FixtureFiles.VersionUnknown));

        Assert.Equal(CollectorExitCodes.Rejected, run.ExitCode);
        Assert.Empty(harness.Handler.Captured);
        Assert.Equal(0, harness.Credentials.Reads);
        Assert.Equal(before, harness.ReadStateBytes());
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void PreviewNeverTouchesTheState()
    {
        using var harness = SendHarness.Create();

        var run = harness.Run(SendHarness.PreviewArgs());

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.False(harness.StateFileExists);
        Assert.Empty(harness.Handler.Captured);
        Assert.Equal(0, harness.Credentials.Reads);
        Assert.False(File.Exists(Path.Combine(harness.StateDirectory!, CollectorStateStore.LockFileName)));
        SendHarness.AssertNoLeak(run);
    }

    [LinuxTheory]
    [InlineData("--revision")]
    [InlineData("--revision=3")]
    public void TheRevisionFlagIsGone(string flag)
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        var before = harness.ReadStateBytes();

        var run = harness.Run([.. SendHarness.SendArgs(), flag, "3"]);

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.Contains("--revision", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Captured);
        Assert.Equal(before, harness.ReadStateBytes());
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void StateInitWritesTheFloor()
    {
        using var harness = SendHarness.Create();

        var run = harness.Run(InitArgs(5));

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.Contains("state_initialized", run.Stderr, StringComparison.Ordinal);
        var state = harness.ReadState()!;
        Assert.Equal(4, state.LastRevision);
        Assert.Equal(CollectorSendOutcome.None, state.LastOutcome);
        Assert.Null(state.BlockedReason);
        Assert.Equal(CollectorExitCodes.Candidate, harness.RunSend().ExitCode);
        Assert.Equal(SnapshotPath(5), Assert.Single(harness.Handler.Captured).Path);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void StateInitRequiresAFirstRevision()
    {
        using var harness = SendHarness.Create();

        var run = harness.Run(["--state-init", "--collector-id", SendHarness.CollectorId, "--generation", "7"]);

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.Contains("--first-revision", run.Stderr, StringComparison.Ordinal);
        Assert.False(harness.StateFileExists);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void FirstRevisionRequiresStateInit()
    {
        using var harness = SendHarness.Create();

        var run = harness.Run(["--first-revision", "5", "--collector-id", SendHarness.CollectorId, "--generation", "7"]);

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.False(harness.StateFileExists);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void StateInitRefusesToMoveTheFloorBackwardsForTheSameCollector()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(9);
        var before = harness.ReadStateBytes();

        var run = harness.Run(InitArgs(5));

        Assert.Equal(CollectorExitCodes.StateMismatch, run.ExitCode);
        Assert.Contains("state_mismatch", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, harness.ReadStateBytes());
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void StateInitRefusesToOverwriteAnotherCollector()
    {
        using var harness = SendHarness.Create();
        harness.WriteStateBytes(CollectorSendState.Create("col-other", SendHarness.Generation, 2).ToCanonicalJson());
        SendHarness.SetMode(harness.StateFilePath, SendHarness.PrivateFileMode);
        var before = harness.ReadStateBytes();

        var run = harness.Run(InitArgs(5));

        Assert.Equal(CollectorExitCodes.StateMismatch, run.ExitCode);
        Assert.Contains("state_mismatch", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, harness.ReadStateBytes());
        SendHarness.AssertNoLeak(run, "col-other");
    }

    [LinuxFact]
    public void StateInitAcceptsAHigherGeneration()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(9);

        var run = harness.Run(InitArgs(1, StateCollectorId, SendHarness.Generation + 1));

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        var state = harness.ReadState()!;
        Assert.Equal(SendHarness.Generation + 1, state.Generation);
        Assert.Equal(0, state.LastRevision);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void StateInitRefusesToLowerTheGeneration()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(3);
        var before = harness.ReadStateBytes();

        var run = harness.Run(InitArgs(5, StateCollectorId, SendHarness.Generation - 1));

        Assert.Equal(CollectorExitCodes.StateMismatch, run.ExitCode);
        Assert.Contains("state_mismatch", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, harness.ReadStateBytes());
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void StateInitAcceptsTheNextFloorForTheSameGeneration()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(9);

        var run = harness.Run(InitArgs(10));

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.Equal(9, harness.ReadState()!.LastRevision);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void StateInitOverwritesAnUnreadableStateFile()
    {
        using var harness = SendHarness.Create();
        harness.WriteStateBytes("not a state document"u8.ToArray());
        SendHarness.SetMode(harness.StateFilePath, SendHarness.PrivateFileMode);

        var refused = harness.RunSend();
        var initialized = harness.Run(InitArgs(1));

        Assert.Equal(CollectorExitCodes.StateUnavailable, refused.ExitCode);
        Assert.Equal(CollectorExitCodes.Candidate, initialized.ExitCode);
        Assert.Equal(0, harness.ReadState()!.LastRevision);
        SendHarness.AssertNoLeak(refused, initialized);
    }

    [LinuxFact]
    public void StateShowPrintsTheCanonicalDocument()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(4, CollectorSendOutcome.Accepted);

        var run = harness.Run(ShowArgs());

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.Equal(Encoding.UTF8.GetString(harness.ReadStateBytes()), run.Stdout);
        Assert.Empty(run.Stderr);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void StateShowWithoutStateIsATypedFailure()
    {
        using var harness = SendHarness.Create();

        var run = harness.Run(ShowArgs());

        Assert.Equal(CollectorExitCodes.StateUnavailable, run.ExitCode);
        Assert.Contains("state_unavailable: StateMissing", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(run.Stdout);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void StateCommandsNeedTheStateDirectory()
    {
        using var harness = SendHarness.CreateWithoutStateDirectory();

        var run = harness.Run(ShowArgs());

        Assert.Equal(CollectorExitCodes.StateUnavailable, run.ExitCode);
        Assert.Contains("state_unavailable: DirectoryUnset", run.Stderr, StringComparison.Ordinal);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void StateCommandsRejectSendArguments()
    {
        using var harness = SendHarness.Create();

        var run = harness.Run([.. ShowArgs(), "--endpoint", SendHarness.Endpoint]);

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.False(harness.StateFileExists);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxTheory]
    [InlineData("{\"status\":\"applied\",\"collectorId\":\"col-test\",\"generation\":7,\"revision\":2,\"receivedAt\":\"2026-11-21T00:00:00.000Z\",\"changedDays\":[]}")]
    [InlineData("{\"status\":\"applied\",\"collectorId\":\"col-test\",\"generation\":8,\"revision\":3,\"receivedAt\":\"2026-11-21T00:00:00.000Z\",\"changedDays\":[]}")]
    [InlineData("{\"status\":\"applied\",\"collectorId\":\"col-other\",\"generation\":7,\"revision\":3,\"receivedAt\":\"2026-11-21T00:00:00.000Z\",\"changedDays\":[]}")]
    [InlineData("{\"status\":\"queued\",\"collectorId\":\"col-test\",\"generation\":7,\"revision\":3,\"receivedAt\":\"2026-11-21T00:00:00.000Z\",\"changedDays\":[]}")]
    [InlineData("{\"status\":\"applied\",\"collectorId\":\"col-test\",\"generation\":7,\"revision\":3,\"receivedAt\":\"2026-11-21T00:00:00Z\",\"changedDays\":[]}")]
    [InlineData("{\"status\":\"applied\",\"collectorId\":\"col-test\",\"generation\":7,\"revision\":3,\"receivedAt\":\"2026-11-21T00:00:00.000Z\",\"changedDays\":[\"2026-11-18\",\"2026-11-17\"]}")]
    [InlineData("{\"status\":\"applied\",\"collectorId\":\"col-test\",\"generation\":7,\"revision\":3,\"receivedAt\":\"2026-11-21T00:00:00.000Z\",\"changedDays\":[\"2026-11-17\",\"2026-11-17\"]}")]
    [InlineData("{\"status\":\"applied\",\"collectorId\":\"col-test\",\"generation\":7,\"revision\":3,\"receivedAt\":\"2026-11-21T00:00:00.000Z\",\"changedDays\":[],\"note\":1}")]
    [InlineData("{\"status\":\"applied\",\"collectorId\":\"col-test\",\"generation\":7,\"revision\":3,\"receivedAt\":\"2026-11-21T00:00:00.000Z\"}")]
    [InlineData("{\"status\":\"applied\",\"collectorId\":\"col-test\",\"generation\":7,\"revision\":\"3\",\"receivedAt\":\"2026-11-21T00:00:00.000Z\",\"changedDays\":[]}")]
    [InlineData("{}")]
    [InlineData("")]
    public void AnUnverifiedReceiptIsNotSuccess(string body)
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(HttpStatusCode.OK, body);

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendReceiptUnverified, run.ExitCode);
        Assert.Contains("send_receipt_unverified", run.Stderr, StringComparison.Ordinal);
        var state = harness.ReadState()!;
        Assert.Equal(CollectorSendOutcome.UnverifiedReceipt, state.LastOutcome);
        Assert.True(state.IsBlocked);
        Assert.Equal(CollectorSendState.ResponseUnverifiedReason, state.BlockedReason);
        Assert.Equal(3, state.LastRevision);

        var readsBeforeRefusal = harness.Credentials.Reads;
        var refused = harness.RunSend();

        Assert.Equal(CollectorExitCodes.StateBlocked, refused.ExitCode);
        Assert.Single(harness.Handler.Captured);
        Assert.Equal(readsBeforeRefusal, harness.Credentials.Reads);
        SendHarness.AssertNoLeak(run, "2026-11-21T00:00:00.000Z", "col-other");
        SendHarness.AssertNoLeak(refused);
    }

    [LinuxTheory]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.NoContent)]
    public void ASuccessWithoutAReceiptIsNotSuccess(HttpStatusCode status)
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(status, string.Empty);

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendReceiptUnverified, run.ExitCode);
        var state = harness.ReadState()!;
        Assert.Equal(CollectorSendOutcome.UnverifiedReceipt, state.LastOutcome);
        Assert.Equal(CollectorSendState.ResponseUnverifiedReason, state.BlockedReason);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxTheory]
    [InlineData("applied", "[\"2026-11-17\"]")]
    [InlineData("replayed", "[]")]
    public void AVerifiedReceiptIsSuccess(string status, string changedDays)
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(HttpStatusCode.OK, SendHarness.Receipt(3, status, changedDays: changedDays));

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.Contains("sent: accepted", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(CollectorSendOutcome.Accepted, harness.ReadState()!.LastOutcome);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void AnOversizedReceiptIsRefusedWithoutRetry()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(
            HttpStatusCode.OK,
            Encoding.UTF8.GetBytes(SendHarness.Receipt(3)),
            contentLength: CollectorSnapshotTransport.MaxResponseBytes + 1);

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendResponseTooLarge, run.ExitCode);
        Assert.Single(harness.Handler.Captured);
        var state = harness.ReadState()!;
        Assert.Equal(CollectorSendOutcome.UnverifiedReceipt, state.LastOutcome);
        Assert.Equal(CollectorSendState.ResponseUnverifiedReason, state.BlockedReason);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void TheRetriedRequestReusesTheSameRevisionAndBody()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(HttpStatusCode.BadGateway, string.Empty);
        harness.Handler.Enqueue(HttpStatusCode.BadGateway, string.Empty);

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.Equal(3, harness.Handler.Captured.Count);
        Assert.All(harness.Handler.Captured, request => Assert.Equal(SnapshotPath(3), request.Path));
        Assert.All(harness.Handler.Captured, request => Assert.Equal(SendHarness.ExpectedWire(), request.Body));
        Assert.Equal(3, harness.ReadState()!.LastRevision);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void StateOutputCarriesNoPathAndNoEndpoint()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);

        var sent = harness.RunSend();
        var shown = harness.Run(ShowArgs());

        Assert.DoesNotContain(harness.StateDirectory!, sent.Stderr + shown.Stdout + shown.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(SendHarness.EndpointHost, shown.Stdout, StringComparison.Ordinal);
        SendHarness.AssertNoLeak(sent, shown);
    }

    [LinuxFact]
    public void StateAdvanceClearsAResponseUnverifiedBlock()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(HttpStatusCode.OK, "{}");

        var unverified = harness.RunSend();
        var advanced = harness.Run(AdvanceArgs());
        var next = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendReceiptUnverified, unverified.ExitCode);
        Assert.Contains("state_advanced: block cleared", advanced.Stderr, StringComparison.Ordinal);
        Assert.Equal(CollectorExitCodes.Candidate, next.ExitCode);
        Assert.Equal(SnapshotPath(4), harness.Handler.Captured[^1].Path);
        SendHarness.AssertNoLeak(unverified, advanced, next);
    }

    [LinuxFact]
    public void TheRevisionIsDurableBeforeTheRequestArrives()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        byte[]? inFlight = null;
        harness.Handler.Observed = _ => inFlight = File.ReadAllBytes(harness.StateFilePath);

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.NotNull(inFlight);
        Assert.True(
            CollectorSendState.TryParse(inFlight!, out var observed, out var failure),
            $"the state was not readable while the request was in flight: {failure}");
        Assert.Equal(3, observed!.LastRevision);
        Assert.Equal(CollectorSendOutcome.None, observed.LastOutcome);
        Assert.Equal(3, harness.ReadState()!.LastRevision);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void AStateWriteFailureStopsBeforeAnyRequest()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        Directory.CreateDirectory(harness.StateTempPath);

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.StateUnavailable, run.ExitCode);
        Assert.Contains("state_unavailable: StateWriteFailed", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Captured);
        Assert.Equal(2, harness.ReadState()!.LastRevision);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void AFailedResultRecordIsReportedWithItsOwnCode()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Observed = _ => Directory.CreateDirectory(harness.StateTempPath);

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.StateUnrecorded, run.ExitCode);
        Assert.Contains("state_unrecorded", run.Stderr, StringComparison.Ordinal);
        Assert.Contains("sent: accepted", run.Stderr, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Captured);
        var state = harness.ReadState()!;
        Assert.Equal(3, state.LastRevision);
        Assert.Equal(CollectorSendOutcome.None, state.LastOutcome);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void AFailedRecordAfterAConflictUsesTheDedicatedCode()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(HttpStatusCode.Conflict, "{\"error\":{\"code\":\"stale_revision\"}}");
        harness.Handler.Observed = _ => Directory.CreateDirectory(harness.StateTempPath);

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.StateUnrecorded, run.ExitCode);
        Assert.Contains("state_unrecorded", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(CollectorSendOutcome.None, harness.ReadState()!.LastOutcome);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxTheory]
    [InlineData(CollectorSendOutcome.None)]
    [InlineData(CollectorSendOutcome.Unknown)]
    public void AnUnresolvedPreviousSendIsReportedAndSendingContinues(CollectorSendOutcome previous)
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2, previous);

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.Contains("previous_send_unresolved", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(SnapshotPath(3), Assert.Single(harness.Handler.Captured).Path);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxTheory]
    [InlineData(CollectorSendOutcome.Accepted)]
    [InlineData(CollectorSendOutcome.Rejected)]
    public void AResolvedPreviousSendIsNotReported(CollectorSendOutcome previous)
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2, previous);

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.DoesNotContain("previous_send_unresolved", run.Stderr, StringComparison.Ordinal);
        SendHarness.AssertNoLeak(run);
    }

    private static string[] AdvanceArgs() => ["--state-advance"];

    private static string[] ShowArgs() => ["--state-show"];
}

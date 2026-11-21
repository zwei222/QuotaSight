using System.Net;
using System.Text;

namespace QuotaSight.Collector.Tests;

/// <summary>
/// Wire v2 through the CLI: opting in, what reaches stdout and the socket, and what
/// the collector makes of a v2 receipt.
///
/// v2 is opt-in, so the first thing asserted is that nothing changes for a caller
/// who does not ask for it.
/// </summary>
public sealed class CollectorWireV2Tests
{
    private static readonly string[] PreviewV2Args =
    [
        "--preview",
        "--generation",
        "1",
        "--bucket-time-zone",
        "Asia/Tokyo",
        "--wire-schema",
        "2",
    ];

    private sealed record CliRun(int ExitCode, string Stdout, string Stderr);

    private static CliRun Run(string[] args, byte[] stdin)
    {
        using var input = new MemoryStream(stdin, writable: false);
        using var output = new MemoryStream();
        using var stderr = new StringWriter();
        var exitCode = CollectorCli.Run(args, input, output, stderr);
        return new CliRun(exitCode, Encoding.UTF8.GetString(output.ToArray()), stderr.ToString());
    }

    [Fact]
    public void PreviewWithoutTheFlagStillEmitsWireV1()
    {
        var run = Run(SendHarness.PreviewArgs(), FixtureFiles.Bytes(FixtureFiles.Parts));

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.StartsWith("{\"schemaVersion\":1,", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\"keys\"", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\"parts\"", run.Stdout, StringComparison.Ordinal);
        // Nothing was withheld, so there is nothing to report.
        Assert.Empty(run.Stderr);
    }

    [Fact]
    public void PreviewWithTheFlagEmitsTheSharedWireV2Bytes()
    {
        var run = Run(PreviewV2Args, FixtureFiles.Bytes(FixtureFiles.Parts));

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.Equal(Encoding.UTF8.GetString(FixtureFiles.SharedWireV2Golden()), run.Stdout);
        Assert.Empty(run.Stderr);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("two")]
    [InlineData("")]
    public void AnUnsupportedWireSchemaIsAUsageError(string value)
    {
        var args = new List<string>(PreviewV2Args);
        args[args.IndexOf("--wire-schema") + 1] = value;

        var run = Run([.. args], FixtureFiles.Bytes(FixtureFiles.Parts));

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.Empty(run.Stdout);
        Assert.Contains("--wire-schema", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFlagIsAcceptedInline()
    {
        var args = new List<string>(PreviewV2Args);
        args.RemoveRange(args.IndexOf("--wire-schema"), 2);
        args.Add("--wire-schema=2");

        var run = Run([.. args], FixtureFiles.Bytes(FixtureFiles.Parts));

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.StartsWith("{\"schemaVersion\":2,", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void WithheldBreakdownsAreReportedAsACountOnly()
    {
        // The positive fixture has a day whose client rows do not add up to its
        // total, so wire v2 sends that day without a breakdown.
        var run = Run(PreviewV2Args, FixtureFiles.Bytes(FixtureFiles.Positive));

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.Contains("parts_omitted: 1 days were sent without a breakdown", run.Stderr, StringComparison.Ordinal);
        // The withheld day still appears, with its total and with no parts array.
        Assert.Contains("{\"date\":\"2026-11-19\",\"totalTokens\":1440}", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFlagIsRejectedForStateModesThatSendNothing()
    {
        var run = Run(["--state-show", "--wire-schema", "2"], []);

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.Contains("--state-show", run.Stderr, StringComparison.Ordinal);
    }

    [LinuxFact]
    public void SendWithTheFlagTransmitsWireV2AndAcceptsAV2Receipt()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(HttpStatusCode.OK, SendHarness.ReceiptV2(3));

        var run = harness.RunSend(FixtureFiles.Bytes(FixtureFiles.Parts), "--wire-schema", "2");

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.Contains("sent: accepted", run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("part_reductions_ignored", run.Stderr, StringComparison.Ordinal);
        var captured = Assert.Single(harness.Handler.Captured);
        Assert.Equal(SendHarness.ExpectedWireV2(SendHarness.Generation), captured.Body);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void AV2SendAnsweredWithAV1ReceiptIsUnverified()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        // A v2 write answers with nine fields; a seven-field receipt means the
        // deployment does not know v2, and the collector must not call that a success.
        harness.Handler.Enqueue(HttpStatusCode.OK, SendHarness.Receipt(3));

        var run = harness.RunSend(FixtureFiles.Bytes(FixtureFiles.Parts), "--wire-schema", "2");

        Assert.Equal(CollectorExitCodes.SendReceiptUnverified, run.ExitCode);
        Assert.Contains("send_receipt_unverified", run.Stderr, StringComparison.Ordinal);
    }

    [LinuxFact]
    public void AV1SendAnsweredWithAV2ReceiptIsUnverified()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(HttpStatusCode.OK, SendHarness.ReceiptV2(3));

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendReceiptUnverified, run.ExitCode);
    }

    [LinuxFact]
    public void IgnoredBreakdownReductionsAreReportedAsACount()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(
            HttpStatusCode.OK,
            SendHarness.ReceiptV2(3, changedParts: "0", ignoredPartReductions: "2"));

        var run = harness.RunSend(FixtureFiles.Bytes(FixtureFiles.Parts), "--wire-schema", "2");

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.Contains("part_reductions_ignored: 2", run.Stderr, StringComparison.Ordinal);
    }

    [LinuxFact]
    public void AV2ReceiptWithAnOutOfBoundPartCountIsUnverified()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(
            HttpStatusCode.OK,
            SendHarness.ReceiptV2(3, changedParts: (TokscaleGraphContract.MaxPartsPerSnapshot + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var run = harness.RunSend(FixtureFiles.Bytes(FixtureFiles.Parts), "--wire-schema", "2");

        Assert.Equal(CollectorExitCodes.SendReceiptUnverified, run.ExitCode);
    }
}

using System.Text;

namespace QuotaSight.Collector.Tests;

public sealed class CollectorCliTests
{
    private static readonly string[] PreviewArgs =
    {
        "--preview",
        "--generation",
        "7",
        "--bucket-time-zone",
        "Asia/Tokyo",
    };

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
    public void PreviewEmitsCandidateForThePositiveFixture()
    {
        var run = Run(PreviewArgs, FixtureFiles.Bytes(FixtureFiles.Positive));

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.Equal(0, run.ExitCode);
        Assert.StartsWith("{\"schemaVersion\":1,", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"generation\":7", run.Stdout, StringComparison.Ordinal);
        Assert.Empty(run.Stderr);
    }

    [Fact]
    public void PreviewStdoutMatchesTheSerializedWire()
    {
        var run = Run(PreviewArgs, FixtureFiles.Bytes(FixtureFiles.Positive));

        var candidate = TokscaleGraphNormalizer.Normalize(FixtureFiles.Bytes(FixtureFiles.Positive)).Candidate!;
        Assert.Equal(SnapshotWire.Text(SnapshotWire.Serialize(candidate, generation: 7)), run.Stdout);
    }

    [Theory]
    [InlineData(FixtureFiles.Empty)]
    [InlineData(FixtureFiles.Malformed)]
    public void NoDatedRowsEmitsNoSnapshotAndDoesNotClaimSuccess(string fixture)
    {
        var run = Run(PreviewArgs, FixtureFiles.Bytes(fixture));

        Assert.Equal(CollectorExitCodes.NoObservedDays, run.ExitCode);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Empty(run.Stdout);
        Assert.Contains("no_dated_rows", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void UpstreamPartialEvidenceEmitsCandidateButNeverExitsZero()
    {
        var args = PreviewArgs.Append("--upstream-partial").ToArray();

        var run = Run(args, FixtureFiles.Bytes(FixtureFiles.Partial));

        Assert.Equal(CollectorExitCodes.CandidateUnverified, run.ExitCode);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("\"days\":[{\"date\":\"2026-11-17\",\"totalTokens\":400}", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("candidate_unverified", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialFlagIsTheOnlyWayToReportPartialEvidence()
    {
        var run = Run(PreviewArgs, FixtureFiles.Bytes(FixtureFiles.Partial));

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.NotEmpty(run.Stdout);
    }

    [Fact]
    public void RejectedInputEmitsNothingOnStdout()
    {
        var run = Run(PreviewArgs, FixtureFiles.Bytes(FixtureFiles.VersionUnknown));

        Assert.Equal(CollectorExitCodes.Rejected, run.ExitCode);
        Assert.Empty(run.Stdout);
        Assert.Contains("unsupported_version", run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("4.24.0", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutPreviewNothingIsWrittenToStdout()
    {
        var args = PreviewArgs.Where(arg => arg != "--preview").ToArray();

        var run = Run(args, FixtureFiles.Bytes(FixtureFiles.Positive));

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.Empty(run.Stdout);
    }

    [Fact]
    public void BucketTimeZoneMustBeProvidedByTheOperator()
    {
        var args = new[] { "--preview", "--generation", "1" };

        var run = Run(args, FixtureFiles.Bytes(FixtureFiles.Positive));

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.Empty(run.Stdout);
        Assert.Contains("bucket-time-zone", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedBucketTimeZoneIsRejected()
    {
        var args = new[] { "--preview", "--generation", "1", "--bucket-time-zone", "UTC" };

        var run = Run(args, FixtureFiles.Bytes(FixtureFiles.Positive));

        Assert.Equal(CollectorExitCodes.Rejected, run.ExitCode);
        Assert.Empty(run.Stdout);
        Assert.Contains("unsupported_time_zone", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerationMustBeProvidedExplicitly()
    {
        var args = new[] { "--preview", "--bucket-time-zone", "Asia/Tokyo" };

        var run = Run(args, FixtureFiles.Bytes(FixtureFiles.Positive));

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.Empty(run.Stdout);
        Assert.Contains("generation", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownArgumentIsAUsageError()
    {
        var args = PreviewArgs.Append("--send").ToArray();

        var run = Run(args, FixtureFiles.Bytes(FixtureFiles.Positive));

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.Empty(run.Stdout);
    }

    [Fact]
    public void OversizedStdinIsRejectedBeforeParsing()
    {
        var oversized = new byte[(TokscaleGraphContract.MaxInputBytes) + 1];
        Array.Fill(oversized, (byte)' ');

        var run = Run(PreviewArgs, oversized);

        Assert.Equal(CollectorExitCodes.Rejected, run.ExitCode);
        Assert.Empty(run.Stdout);
        Assert.Contains("too_large", run.Stderr, StringComparison.Ordinal);
    }
}

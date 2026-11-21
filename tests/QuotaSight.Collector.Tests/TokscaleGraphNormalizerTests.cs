using System.Globalization;

namespace QuotaSight.Collector.Tests;

public sealed class TokscaleGraphNormalizerTests
{
    [Fact]
    public void PinnedPositiveFixtureYieldsCandidateWithPinnedAdapterVersion()
    {
        var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Bytes(FixtureFiles.Positive));

        Assert.Equal(NormalizationStatus.Candidate, result.Status);
        var candidate = Assert.IsType<SnapshotCandidate>(result.Candidate);
        Assert.Equal(1, candidate.SchemaVersion);
        Assert.Equal("tokscale-4.23.0", candidate.AdapterVersion);
        Assert.Equal(TokscaleGraphContract.PinnedAdapterVersion, candidate.AdapterVersion);
        Assert.Equal("2026-11-17", candidate.CoveredFrom);
        Assert.Equal("2026-11-19", candidate.CoveredTo);
        Assert.Equal(new[]
        {
            new SnapshotDay("2026-11-17", 400),
            new SnapshotDay("2026-11-18", 1200),
            new SnapshotDay("2026-11-19", 1440),
        }, candidate.Days);
    }

    [Fact]
    public void AdapterVersionIsAsciiWithoutZeroWidthCharacters()
    {
        Assert.Equal("tokscale-4.23.0", TokscaleGraphContract.PinnedAdapterVersion);
        Assert.All(TokscaleGraphContract.PinnedAdapterVersion, ch => Assert.InRange(ch, (char)0x21, (char)0x7e));
    }

    [Theory]
    [InlineData(FixtureFiles.Empty)]
    [InlineData(FixtureFiles.Malformed)]
    public void NoDatedRowsYieldsNoSnapshot(string fixture)
    {
        var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Bytes(fixture));

        Assert.Equal(NormalizationStatus.NoObservedDays, result.Status);
        Assert.Null(result.Candidate);
        Assert.Equal("no_dated_rows", result.Reason);
    }

    [Fact]
    public void OffsetInstantIsCanonicalizedToUtcMilliseconds()
    {
        var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Bytes(FixtureFiles.OffsetInstant));

        var candidate = Assert.IsType<SnapshotCandidate>(result.Candidate);
        Assert.Equal("2026-11-19T18:49:08.875Z", candidate.CollectedAt);
    }

    [Fact]
    public void UnsortedDaysAreCanonicalizedIntoAscendingOrder()
    {
        var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Bytes(FixtureFiles.Unsorted));

        var candidate = Assert.IsType<SnapshotCandidate>(result.Candidate);
        Assert.Equal(new[] { "2026-11-17", "2026-11-18", "2026-11-19" }, candidate.Days.Select(day => day.Date));
    }

    [Fact]
    public void ObservedZeroIsKeptForTheDatedRowAndAbsentDayHasNoRow()
    {
        var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Bytes(FixtureFiles.ZeroObserved));

        var candidate = Assert.IsType<SnapshotCandidate>(result.Candidate);
        Assert.Equal(new[]
        {
            new SnapshotDay("2026-11-17", 0),
            new SnapshotDay("2026-11-19", 500),
        }, candidate.Days);
        Assert.DoesNotContain(candidate.Days, day => day.Date == "2026-11-18");
        Assert.Equal("2026-11-17", candidate.CoveredFrom);
        Assert.Equal("2026-11-19", candidate.CoveredTo);
    }

    [Theory]
    [InlineData(FixtureFiles.VersionUnknown, "meta.version", "unsupported_version")]
    [InlineData(FixtureFiles.DuplicateDate, "contributions", "duplicate_date")]
    [InlineData(FixtureFiles.DuplicateKey, "contributions[0].totals.tokens", "duplicate_key")]
    [InlineData(FixtureFiles.FractionalTokens, "contributions[1].totals.tokens", "invalid_integer")]
    [InlineData(FixtureFiles.OverflowTokens, "contributions[1].totals.tokens", "out_of_range")]
    [InlineData(FixtureFiles.NegativeTokens, "contributions[0].totals.tokens", "out_of_range")]
    [InlineData(FixtureFiles.MissingTokens, "contributions[1].totals.tokens", "missing_field")]
    [InlineData(FixtureFiles.TokenString, "contributions[2].totals.tokens", "invalid_type")]
    [InlineData(FixtureFiles.BadDate, "contributions[1].date", "invalid_date")]
    [InlineData(FixtureFiles.UnknownRootKey, "extra", "unexpected_field")]
    [InlineData(FixtureFiles.UnknownMetaKey, "meta.extra", "unexpected_field")]
    [InlineData(FixtureFiles.TrailingGarbage, "$", "trailing_content")]
    public void MalformedUpstreamShapesAreRejected(string fixture, string field, string reason)
    {
        var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Bytes(fixture));

        Assert.Equal(NormalizationStatus.Rejected, result.Status);
        Assert.Null(result.Candidate);
        Assert.Equal(field, result.Field);
        Assert.Equal(reason, result.Reason);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public void RangeLongerThanTheApiBoundIsRejected()
    {
        var start = new DateOnly(2026, 1, 1);
        var rows = Enumerable.Range(0, TokscaleGraphContract.MaxDays + 1)
            .Select(offset => (start.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 10L))
            .ToList();

        var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Utf8(FixtureFiles.Graph(rows)));

        Assert.Equal(NormalizationStatus.Rejected, result.Status);
        Assert.Equal("days", result.Field);
        Assert.Equal("too_many_days", result.Reason);
    }

    [Fact]
    public void ExactlyTheApiDayBoundIsAccepted()
    {
        var start = new DateOnly(2026, 1, 1);
        var rows = Enumerable.Range(0, TokscaleGraphContract.MaxDays)
            .Select(offset => (start.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 10L))
            .ToList();

        var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Utf8(FixtureFiles.Graph(rows)));

        Assert.Equal(NormalizationStatus.Candidate, result.Status);
        Assert.Equal(TokscaleGraphContract.MaxDays, result.Candidate!.Days.Count);
    }

    [Fact]
    public void SafeIntegerUpperBoundIsAccepted()
    {
        var result = TokscaleGraphNormalizer.Normalize(
            FixtureFiles.Utf8(FixtureFiles.Graph(new[] { ("2026-11-17", TokscaleGraphContract.MaxSafeInteger) })));

        Assert.Equal(NormalizationStatus.Candidate, result.Status);
        Assert.Equal(TokscaleGraphContract.MaxSafeInteger, result.Candidate!.Days[0].TotalTokens);
    }

    [Fact]
    public void InputLongerThanTheBoundIsRejectedWithoutBufferingIt()
    {
        var oversized = new byte[(TokscaleGraphContract.MaxInputBytes) + 1];
        Array.Fill(oversized, (byte)' ');

        var result = TokscaleGraphNormalizer.Normalize(oversized);

        Assert.Equal(NormalizationStatus.Rejected, result.Status);
        Assert.Equal("input", result.Field);
        Assert.Equal("too_large", result.Reason);
    }

    [Fact]
    public void EmptyInputIsRejected()
    {
        var result = TokscaleGraphNormalizer.Normalize(ReadOnlySpan<byte>.Empty);

        Assert.Equal(NormalizationStatus.Rejected, result.Status);
        Assert.Equal("invalid_json", result.Reason);
    }

    [Fact]
    public void RejectionsCarryNoUpstreamPayloadText()
    {
        var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Bytes(FixtureFiles.VersionUnknown));

        Assert.Equal(NormalizationStatus.Rejected, result.Status);
        Assert.DoesNotContain("4.24.0", result.Message);
        Assert.DoesNotContain("gpt-5.1", result.Message);
    }
}

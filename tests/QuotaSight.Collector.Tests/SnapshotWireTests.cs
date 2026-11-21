using System.Text;
using System.Text.Json;

namespace QuotaSight.Collector.Tests;

public sealed class SnapshotWireTests
{
    private const string ExpectedPositiveWire =
        "{\"schemaVersion\":1,\"adapterVersion\":\"tokscale-4.23.0\",\"generation\":7," +
        "\"collectedAt\":\"2026-11-19T18:49:08.875Z\",\"bucketTimeZone\":\"Asia/Tokyo\"," +
        "\"coveredRange\":{\"from\":\"2026-11-17\",\"to\":\"2026-11-19\"}," +
        "\"days\":[{\"date\":\"2026-11-17\",\"totalTokens\":400}," +
        "{\"date\":\"2026-11-18\",\"totalTokens\":1200}," +
        "{\"date\":\"2026-11-19\",\"totalTokens\":1440}]}\n";

    private static SnapshotCandidate Candidate()
    {
        var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Bytes(FixtureFiles.Positive));
        return Assert.IsType<SnapshotCandidate>(result.Candidate);
    }

    [Fact]
    public void WireIsApiCompatibleAndKeyOrderIsCanonical()
    {
        var wire = SnapshotWire.Serialize(Candidate(), generation: 7);
        Assert.Equal(ExpectedPositiveWire, SnapshotWire.Text(wire));
    }

    [Fact]
    public void WireExposesOnlyAllowlistedApiFields()
    {
        var text = SnapshotWire.Text(SnapshotWire.Serialize(Candidate(), generation: 1));

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.Equal(
            new[] { "schemaVersion", "adapterVersion", "generation", "collectedAt", "bucketTimeZone", "coveredRange", "days" },
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            new[] { "from", "to" },
            root.GetProperty("coveredRange").EnumerateObject().Select(property => property.Name));
        foreach (var day in root.GetProperty("days").EnumerateArray())
        {
            Assert.Equal(new[] { "date", "totalTokens" }, day.EnumerateObject().Select(property => property.Name));
        }

        foreach (var forbidden in new[] { "cost", "clients", "summary", "meta", "intensity", "tokenBreakdown", "totalCost", "years", "timeMetrics" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WireIsWithinTheApiBodyBoundAndHasNoByteOrderMark()
    {
        var wire = SnapshotWire.Serialize(Candidate(), generation: 1);

        Assert.InRange(wire.Length, 1, 32 * 1024);
        Assert.Equal((byte)'{', wire[0]);
        Assert.Equal((byte)'\n', wire[^1]);
    }

    [Fact]
    public void WireTextDecodesAsUtf8WithoutEscapingTheAsciiFields()
    {
        var text = SnapshotWire.Text(SnapshotWire.Serialize(Candidate(), generation: 3));

        Assert.Contains("\"generation\":3,", text, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), SnapshotWire.Serialize(Candidate(), generation: 3).Length);
    }
}

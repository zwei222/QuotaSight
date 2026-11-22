using System.Globalization;
using System.Text;

namespace QuotaSight.Collector.Tests;

/// <summary>
/// The optional fields the pinned 4.23.0 graph carries, and the rule that makes
/// them safe to allow: each one is read for its type and bounds, then discarded.
///
/// <c>contributions[].activeTimeMs</c> is present on some days only, and
/// <c>contributions[].clients[].costProvenance</c> is present on every client row.
/// Both were missing from the pinned allow-lists, so a real document was rejected
/// (the day-level field) or lost its breakdown (the client-level one). Neither is
/// ever mapped to the wire: an optional field is allowed, never echoed.
/// </summary>
public sealed class TokscaleGraphOptionalFieldsTests
{
    private const string ClientTokens = "\"tokens\": {\"input\": 250, \"output\": 40, \"cacheRead\": 10, \"cacheWrite\": 0, \"reasoning\": 0}";

    [Fact]
    public void AnActiveTimeOnSomeDaysOnlyIsAcceptedByBothWireVersions()
    {
        var v1 = Assert.IsType<SnapshotCandidate>(
            TokscaleGraphNormalizer.Normalize(FixtureFiles.Bytes(FixtureFiles.ActiveTime), WireSchema.V1).Candidate);
        Assert.Equal(new[]
        {
            ("2026-11-17", 400L),
            ("2026-11-18", 1200L),
            ("2026-11-19", 1440L),
            ("2026-11-20", 100L),
        }, v1.Days.Select(day => (day.Date, day.TotalTokens)));

        var v2 = Assert.IsType<SnapshotCandidate>(
            TokscaleGraphNormalizer.Normalize(FixtureFiles.Bytes(FixtureFiles.ActiveTime), WireSchema.V2).Candidate);

        // The days that carry the optional field keep their breakdown exactly like
        // the days that do not; nothing about the field withholds a row.
        Assert.Equal(0, v2.OmittedPartDays);
        Assert.All(v2.Days, day => Assert.NotNull(day.Parts));
        foreach (var day in v2.Days)
        {
            Assert.Equal(day.TotalTokens, day.Parts!.Sum(part => part.Total));
        }
    }

    /// <summary>
    /// Wire v1 has to stay byte-identical: an optional field that is not mapped
    /// cannot change the body a caller already ships.
    /// </summary>
    [Fact]
    public void AnOptionalActiveTimeDoesNotChangeTheWireV1Bytes()
    {
        const string Expected =
            "{\"schemaVersion\":1,\"adapterVersion\":\"tokscale-4.23.0\",\"generation\":5,"
            + "\"collectedAt\":\"2026-11-19T18:49:08.875Z\",\"bucketTimeZone\":\"Asia/Tokyo\","
            + "\"coveredRange\":{\"from\":\"2026-11-17\",\"to\":\"2026-11-18\"},"
            + "\"days\":[{\"date\":\"2026-11-17\",\"totalTokens\":400},{\"date\":\"2026-11-18\",\"totalTokens\":1200}]}\n";

        var withActiveTime = Normalize(
            Graph(("2026-11-17", 400, "900000"), ("2026-11-18", 1200, "0")),
            WireSchema.V1);
        var withoutActiveTime = Normalize(
            Graph(("2026-11-17", 400, null), ("2026-11-18", 1200, null)),
            WireSchema.V1);

        var withBytes = SnapshotWire.Text(SnapshotWire.Serialize(withActiveTime, generation: 5));
        var withoutBytes = SnapshotWire.Text(SnapshotWire.Serialize(withoutActiveTime, generation: 5));

        Assert.Equal(Expected, withoutBytes);
        Assert.Equal(withoutBytes, withBytes);
    }

    [Theory]
    [InlineData(FixtureFiles.ActiveTimeNegative, "out_of_range")]
    [InlineData(FixtureFiles.ActiveTimeFractional, "invalid_integer")]
    [InlineData(FixtureFiles.ActiveTimeString, "invalid_type")]
    [InlineData(FixtureFiles.ActiveTimeOverflow, "out_of_range")]
    public void AnActiveTimeThatIsNotANonNegativeIntegerIsRejected(string fixture, string reason)
    {
        var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Bytes(fixture));

        Assert.Equal(NormalizationStatus.Rejected, result.Status);
        Assert.Null(result.Candidate);
        // The bad value sits on the second row, so a rejection here also proves the
        // first row's optional field was read without ending the walk.
        Assert.Equal("contributions[1].activeTimeMs", result.Field);
        Assert.Equal(reason, result.Reason);
        Assert.DoesNotContain("1000", result.Message);
    }

    [Fact]
    public void AnActiveTimeThatIsNotANonNegativeIntegerIsRejectedOnTheWireV2PathToo()
    {
        var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Bytes(FixtureFiles.ActiveTimeString), WireSchema.V2);

        Assert.Equal(NormalizationStatus.Rejected, result.Status);
        Assert.Equal("contributions[1].activeTimeMs", result.Field);
        Assert.Equal("invalid_type", result.Reason);
    }

    [Fact]
    public void ARepeatedActiveTimeIsRejected()
    {
        var document = """
            {
              "meta": { "generatedAt": "2026-11-19T18:49:08.875485588+00:00", "version": "4.23.0" },
              "contributions": [
                { "date": "2026-11-17", "totals": { "tokens": 10 }, "activeTimeMs": 1, "activeTimeMs": 2 }
              ]
            }
            """;

        var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Utf8(document));

        Assert.Equal(NormalizationStatus.Rejected, result.Status);
        Assert.Equal("contributions[0].activeTimeMs", result.Field);
        Assert.Equal("duplicate_key", result.Reason);
    }

    [Fact]
    public void AnActiveTimeThatIsNotANumberButValidJsonIsRejected()
    {
        foreach (var literal in new[] { "null", "true", "{}", "[]" })
        {
            var document = """
                {
                  "meta": { "generatedAt": "2026-11-19T18:49:08.875485588+00:00", "version": "4.23.0" },
                  "contributions": [ { "date": "2026-11-17", "totals": { "tokens": 10 }, "activeTimeMs": __VALUE__ } ]
                }
                """.Replace("__VALUE__", literal, StringComparison.Ordinal);

            var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Utf8(document));

            Assert.Equal(NormalizationStatus.Rejected, result.Status);
            Assert.Equal("contributions[0].activeTimeMs", result.Field);
            Assert.Equal("invalid_type", result.Reason);
        }
    }

    [Fact]
    public void TheOptionalActiveTimeIsNeverEchoed()
    {
        var candidateV1 = Normalize(FixtureFiles.Bytes(FixtureFiles.ActiveTime), WireSchema.V1);
        var candidateV2 = Normalize(FixtureFiles.Bytes(FixtureFiles.ActiveTime), WireSchema.V2);

        var v1Text = SnapshotWire.Text(SnapshotWire.Serialize(candidateV1, generation: 1));
        var v2Text = SnapshotWire.Text(SnapshotWire.Serialize(candidateV2, generation: 1, WireSchema.V2).Bytes);

        Assert.DoesNotContain("activeTimeMs", v1Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("activeTimeMs", v2Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("3612345", v2Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every client row in the real document carries <c>costProvenance</c>. It is
    /// allowed at that level and never mapped, so it must not cost the day its
    /// breakdown the way an unknown key does.
    /// </summary>
    [Fact]
    public void AClientRowWithACostProvenanceKeepsItsDayBreakdown()
    {
        var document = Graph(
            ("2026-11-17", 300, "1500",
            [
                "{\"client\": \"codex\", \"modelId\": \"gpt-5.1\", \"providerId\": \"openai\", "
                + ClientTokens + ", \"cost\": 0.0, \"messages\": 1, "
                + "\"costProvenance\": {\"kind\": \"estimated\", \"estimateSource\": \"list-price\"}}",
            ]));

        var candidate = Normalize(document, WireSchema.V2);

        Assert.Equal(0, candidate.OmittedPartDays);
        var part = Assert.Single(candidate.Days[0].Parts!);
        Assert.Equal(new SnapshotPartKey("codex", "openai", "gpt-5.1"), part.Key);
        Assert.Equal(300, part.Total);
    }

    /// <summary>
    /// The same document with and without the optional fields produces the same
    /// v2 body, which is the byte-level statement of "allowed, never echoed".
    /// </summary>
    [Fact]
    public void TheOptionalFieldsDoNotChangeTheWireV2Bytes()
    {
        var withFields = Graph(
            ("2026-11-17", 300, "1500",
            [
                "{\"client\": \"codex\", \"modelId\": \"gpt-5.1\", \"providerId\": \"openai\", "
                + ClientTokens + ", \"costProvenance\": {\"kind\": \"unknown\"}}",
            ]));
        var withoutFields = Graph(
            ("2026-11-17", 300, null,
            [
                "{\"client\": \"codex\", \"modelId\": \"gpt-5.1\", \"providerId\": \"openai\", "
                + ClientTokens + "}",
            ]));

        var withBytes = SnapshotWire.Text(SnapshotWire.Serialize(Normalize(withFields, WireSchema.V2), 3, WireSchema.V2).Bytes);
        var withoutBytes = SnapshotWire.Text(SnapshotWire.Serialize(Normalize(withoutFields, WireSchema.V2), 3, WireSchema.V2).Bytes);

        Assert.Equal(withoutBytes, withBytes);
        Assert.DoesNotContain("costProvenance", withBytes, StringComparison.Ordinal);
    }

    /// <summary>Builds a pinned-shape document from dated rows that carry no client rows.</summary>
    private static string Graph(params (string Date, long Tokens, string? ActiveTime)[] days) =>
        Graph([.. days.Select(day => (day.Date, day.Tokens, day.ActiveTime, Array.Empty<string>()))]);

    /// <summary>Builds a pinned-shape document from dated rows, each with client rows.</summary>
    private static string Graph(params (string Date, long Tokens, string? ActiveTime, string[] Clients)[] days)
    {
        var builder = new StringBuilder();
        builder.Append("{\n  \"meta\": {\"generatedAt\": \"2026-11-19T18:49:08.875485588+00:00\", \"version\": \"4.23.0\"},\n");
        builder.Append("  \"contributions\": [");
        for (var index = 0; index < days.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            var (date, tokens, activeTime, clients) = days[index];
            builder.Append("\n    {\"date\": \"").Append(date).Append("\", \"totals\": {\"tokens\": ")
                .Append(tokens.ToString(CultureInfo.InvariantCulture)).Append('}');
            if (activeTime is not null)
            {
                builder.Append(", \"activeTimeMs\": ").Append(activeTime);
            }

            builder.Append(", \"clients\": [").Append(string.Join(',', clients)).Append("]}");
        }

        builder.Append("\n  ]\n}\n");
        return builder.ToString();
    }

    private static SnapshotCandidate Normalize(byte[] document, WireSchema schema)
    {
        var result = TokscaleGraphNormalizer.Normalize(document, schema);
        Assert.Equal(NormalizationStatus.Candidate, result.Status);
        return Assert.IsType<SnapshotCandidate>(result.Candidate);
    }

    private static SnapshotCandidate Normalize(string document, WireSchema schema) =>
        Normalize(FixtureFiles.Utf8(document), schema);
}

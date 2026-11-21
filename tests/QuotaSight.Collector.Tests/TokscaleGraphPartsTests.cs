using System.Globalization;
using System.Text;

namespace QuotaSight.Collector.Tests;

/// <summary>
/// How <c>contributions[].clients</c> becomes a day's breakdown, and how little the
/// wire v1 path cares about it.
///
/// The rule under test throughout: a day's breakdown either explains that day's
/// total exactly or is not sent at all, and withheld days are counted so the
/// operator can tell "no breakdown upstream" from "a breakdown this collector
/// refused to send". A day whose client rows cannot be read at all is withheld,
/// never a reason to reject the snapshot: the daily totals are still true.
/// </summary>
public sealed class TokscaleGraphPartsTests
{
    private const string ClientHeader = "\"client\": \"codex\", \"modelId\": \"gpt-5.1\"";
    private const string ZeroComponents = "\"output\": 0, \"cacheRead\": 0, \"cacheWrite\": 0, \"reasoning\": 0";

    [Fact]
    public void PinnedPartsFixtureYieldsAscendingKeysAndExactSums()
    {
        var candidate = Assert.IsType<SnapshotCandidate>(
            TokscaleGraphNormalizer.Normalize(FixtureFiles.Bytes(FixtureFiles.Parts), WireSchema.V2).Candidate);

        Assert.Equal(0, candidate.OmittedPartDays);
        Assert.Equal(new[] { "2026-11-17", "2026-11-18" }, candidate.Days.Select(day => day.Date));

        var first = candidate.Days[0].Parts;
        Assert.NotNull(first);
        // The mixed-case client is folded to lower case; the path-shaped model
        // stays unknown, so the key is one the API accepts.
        Assert.Equal(
            new[]
            {
                (new SnapshotPartKey("codex", "", ""), 190L, 10L, 5L, 0L, 0L),
                (new SnapshotPartKey("conduit", "anthropic,openai", "claude-sonnet-5-5[1m]"), 100L, 20L, 10L, 0L, 5L),
            },
            first!.Select(part => (part.Key, part.Input, part.Output, part.CacheRead, part.CacheWrite, part.Reasoning)));
        Assert.Equal(340, first.Sum(part => part.Total));

        var second = candidate.Days[1].Parts;
        Assert.NotNull(second);
        Assert.Equal(new SnapshotPartKey("codex", "openai", "gpt-5.1"), Assert.Single(second!).Key);
        Assert.Equal(100, second!.Sum(part => part.Total));
    }

    [Fact]
    public void RowsThatNormalizeToTheSameKeyAreSummedComponentByComponent()
    {
        var document = Graph(
            ("2026-11-17", 120,
            [
                """{"client": "codex", "modelId": "GPT-5.1", "providerId": "openai", "tokens": {"input": 40, "output": 5, "cacheRead": 0, "cacheWrite": 0, "reasoning": 0}}""",
                """{"client": "CODEX", "modelId": "gpt-5.1", "providerId": "OpenAI", "tokens": {"input": 60, "output": 10, "cacheRead": 5, "cacheWrite": 0, "reasoning": 0}}""",
            ]));

        var candidate = Normalize(document);

        var parts = candidate.Days[0].Parts;
        Assert.NotNull(parts);
        var part = Assert.Single(parts!);
        // Both client spellings fold to the same identifier, so the rows add up.
        Assert.Equal(new SnapshotPartKey("codex", "openai", "gpt-5.1"), part.Key);
        Assert.Equal((100L, 15L, 5L, 0L, 0L), (part.Input, part.Output, part.CacheRead, part.CacheWrite, part.Reasoning));
        Assert.Equal(120, part.Total);
    }

    [Fact]
    public void AClientRowSumThatDoesNotExplainTheDayWithholdsThatBreakdownOnly()
    {
        var document = Graph(
            ("2026-11-17", 340,
            [
                """{"client": "codex", "modelId": "gpt-5.1", "tokens": {"input": 10, "output": 0, "cacheRead": 0, "cacheWrite": 0, "reasoning": 0}}""",
            ]),
            ("2026-11-18", 100,
            [
                """{"client": "codex", "modelId": "gpt-5.1", "tokens": {"input": 100, "output": 0, "cacheRead": 0, "cacheWrite": 0, "reasoning": 0}}""",
            ]));

        var candidate = Normalize(document);

        Assert.Null(candidate.Days[0].Parts);
        Assert.Equal(340, candidate.Days[0].TotalTokens);
        Assert.NotNull(candidate.Days[1].Parts);
        Assert.Equal(1, candidate.OmittedPartDays);
    }

    [Fact]
    public void ADayWithMoreKeysThanTheWireAllowsWithholdsThatBreakdownAndCountsIt()
    {
        var rows = new List<string>();
        for (var index = 0; index <= TokscaleGraphContract.MaxPartsPerDay; index++)
        {
            rows.Add(
                "{\"client\": \"codex\", \"modelId\": \"m"
                + index.ToString("000", CultureInfo.InvariantCulture)
                + "\", \"tokens\": {\"input\": 1, " + ZeroComponents + "}}");
        }

        var candidate = Normalize(Graph(("2026-11-17", rows.Count, [.. rows])));

        Assert.Null(candidate.Days[0].Parts);
        Assert.Equal(rows.Count, candidate.Days[0].TotalTokens);
        Assert.Equal(1, candidate.OmittedPartDays);
    }

    [Fact]
    public void ADayWithoutClientRowsCarriesNoBreakdownAndIsNotCountedAsWithheld()
    {
        var candidate = Normalize(Graph(("2026-11-17", 5, [])));

        Assert.Null(Assert.Single(candidate.Days).Parts);
        Assert.Equal(0, candidate.OmittedPartDays);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("costProvenance")]
    [InlineData("sessionCount")]
    public void AnUnknownClientFieldWithholdsThatDayInsteadOfRejectingTheSnapshot(string field)
    {
        var document = Graph(
            ("2026-11-17", 10,
            [
                "{\"client\": \"codex\", \"modelId\": \"gpt-5.1\", \"tokens\": {\"input\": 10, "
                + ZeroComponents + "}, \"" + field + "\": {}}",
            ]),
            ("2026-11-18", 20,
            [
                """{"client": "codex", "modelId": "gpt-5.1", "tokens": {"input": 20, "output": 0, "cacheRead": 0, "cacheWrite": 0, "reasoning": 0}}""",
            ]));

        var candidate = Normalize(document);

        // The unreadable day keeps its total and loses its breakdown; the next day
        // is read normally, so one bad day cannot cost the whole snapshot.
        Assert.Null(candidate.Days[0].Parts);
        Assert.Equal(10, candidate.Days[0].TotalTokens);
        Assert.NotNull(candidate.Days[1].Parts);
        Assert.Equal(1, candidate.OmittedPartDays);
    }

    [Fact]
    public void AClientRowWithoutTokensWithholdsThatDay()
    {
        var document = Graph(("2026-11-17", 10, ["""{"client": "codex", "modelId": "gpt-5.1"}"""]));

        var candidate = Normalize(document);

        Assert.Null(candidate.Days[0].Parts);
        Assert.Equal(10, candidate.Days[0].TotalTokens);
        Assert.Equal(1, candidate.OmittedPartDays);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("cacheRead")]
    [InlineData("reasoning")]
    public void AClientTokenComponentThatIsMissingWithholdsThatDayRatherThanZeroing(string missing)
    {
        var all = new Dictionary<string, string>
        {
            ["input"] = "10",
            ["output"] = "0",
            ["cacheRead"] = "0",
            ["cacheWrite"] = "0",
            ["reasoning"] = "0",
        };
        all.Remove(missing);
        var tokens = string.Join(',', all.Select(pair => "\"" + pair.Key + "\": " + pair.Value));

        var document = Graph(
            ("2026-11-17", 10,
            [
                "{" + ClientHeader + ", \"tokens\": {" + tokens + "}}",
            ]));

        var candidate = Normalize(document);

        Assert.Null(candidate.Days[0].Parts);
        Assert.Equal(1, candidate.OmittedPartDays);
    }

    [Fact]
    public void AClientTokenComponentThatIsNotAnIntegerWithholdsThatDay()
    {
        var document = Graph(
            ("2026-11-17", 10,
            [
                "{" + ClientHeader + ", \"tokens\": {\"input\": 10.5, " + ZeroComponents + "}}",
            ]));

        var candidate = Normalize(document);

        Assert.Null(candidate.Days[0].Parts);
        Assert.Equal(1, candidate.OmittedPartDays);
    }

    [Fact]
    public void ClientsThatAreNotAnArrayWithholdThatDay()
    {
        var document = "{\n  \"meta\": {\"generatedAt\": \"2026-11-19T18:49:08.875485588+00:00\", \"version\": \"4.23.0\"},\n"
            + "  \"contributions\": [{\"date\": \"2026-11-17\", \"totals\": {\"tokens\": 10}, \"clients\": \"none\"}]\n}\n";

        var candidate = Normalize(document);

        Assert.Null(candidate.Days[0].Parts);
        Assert.Equal(10, candidate.Days[0].TotalTokens);
        Assert.Equal(1, candidate.OmittedPartDays);
    }

    /// <summary>
    /// Wire v1 never reads <c>clients[]</c>, so nothing about the breakdown can
    /// break it: unknown client fields, a null provider, an over-long key list and
    /// a very large array all produce the bytes the collector produced before the
    /// breakdown existed. The expected text is pinned literally for that reason.
    /// </summary>
    [Fact]
    public void WireV1IgnoresEverythingAboutTheBreakdown()
    {
        var rows = new List<string>();
        for (var index = 0; index < 600; index++)
        {
            rows.Add(
                "{\"client\": \"codex\", \"modelId\": \"m"
                + index.ToString("000", CultureInfo.InvariantCulture)
                + "\", \"providerId\": null, \"tokens\": {\"input\": 1, " + ZeroComponents
                + "}, \"sessionCount\": " + index.ToString(CultureInfo.InvariantCulture) + "}");
        }

        var document = Graph(("2026-11-17", 400, [.. rows]), ("2026-11-18", 1200, [.. rows]));

        var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Utf8(document), WireSchema.V1);

        var candidate = Assert.IsType<SnapshotCandidate>(result.Candidate);
        Assert.Equal(0, candidate.OmittedPartDays);
        Assert.All(candidate.Days, day => Assert.Null(day.Parts));
        Assert.Equal(
            "{\"schemaVersion\":1,\"adapterVersion\":\"tokscale-4.23.0\",\"generation\":5,"
            + "\"collectedAt\":\"2026-11-19T18:49:08.875Z\",\"bucketTimeZone\":\"Asia/Tokyo\","
            + "\"coveredRange\":{\"from\":\"2026-11-17\",\"to\":\"2026-11-18\"},"
            + "\"days\":[{\"date\":\"2026-11-17\",\"totalTokens\":400},{\"date\":\"2026-11-18\",\"totalTokens\":1200}]}\n",
            SnapshotWire.Text(SnapshotWire.Serialize(candidate, generation: 5)));
    }

    /// <summary>Builds a pinned-shape document from dated rows and their client rows.</summary>
    private static string Graph(params (string Date, long Tokens, string[] Clients)[] days)
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

            var (date, tokens, clients) = days[index];
            builder.Append("\n    {\"date\": \"").Append(date).Append("\", \"totals\": {\"tokens\": ")
                .Append(tokens.ToString(CultureInfo.InvariantCulture))
                .Append("}, \"clients\": [").Append(string.Join(',', clients)).Append("]}");
        }

        builder.Append("\n  ]\n}\n");
        return builder.ToString();
    }

    private static SnapshotCandidate Normalize(string document)
    {
        var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Utf8(document), WireSchema.V2);
        Assert.Equal(NormalizationStatus.Candidate, result.Status);
        return Assert.IsType<SnapshotCandidate>(result.Candidate);
    }
}

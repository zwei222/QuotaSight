using System.Globalization;
using System.Text;

namespace QuotaSight.Collector.Tests;

/// <summary>
/// How <c>contributions[].clients</c> becomes a day's breakdown.
///
/// The rule under test throughout: a day's breakdown either explains that day's
/// total exactly or is not sent at all, and withheld days are counted so the
/// operator can tell "no breakdown upstream" from "a breakdown this collector
/// refused to send".
/// </summary>
public sealed class TokscaleGraphPartsTests
{
    private const string ClientHeader = "\"client\": \"codex\", \"modelId\": \"gpt-5.1\"";
    private const string ZeroComponents = "\"output\": 0, \"cacheRead\": 0, \"cacheWrite\": 0, \"reasoning\": 0";

    [Fact]
    public void PinnedPartsFixtureYieldsAscendingKeysAndExactSums()
    {
        var candidate = Assert.IsType<SnapshotCandidate>(
            TokscaleGraphNormalizer.Normalize(FixtureFiles.Bytes(FixtureFiles.Parts)).Candidate);

        Assert.Equal(0, candidate.OmittedPartDays);
        Assert.Equal(new[] { "2026-11-17", "2026-11-18" }, candidate.Days.Select(day => day.Date));

        var first = candidate.Days[0].Parts;
        Assert.NotNull(first);
        Assert.Equal(
            new[]
            {
                (new SnapshotPartKey("", "anthropic,openai", "claude-sonnet-5-5[1m]"), 100L, 20L, 10L, 0L, 5L),
                (new SnapshotPartKey("codex", "", ""), 190L, 10L, 5L, 0L, 0L),
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
                """{"client": "codex", "modelId": "gpt-5.1", "providerId": "OpenAI", "tokens": {"input": 60, "output": 10, "cacheRead": 5, "cacheWrite": 0, "reasoning": 0}}""",
            ]));

        var candidate = Normalize(document);

        var parts = candidate.Days[0].Parts;
        Assert.NotNull(parts);
        var part = Assert.Single(parts!);
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
    public void AnUnknownClientFieldIsRejected(string field)
    {
        var document = Graph(
            ("2026-11-17", 10,
            [
                "{\"" + "client\": \"codex\", \"modelId\": \"gpt-5.1\", \"tokens\": {\"input\": 10, "
                + ZeroComponents + "}, \"" + field + "\": {}}",
            ]));

        var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Utf8(document));

        Assert.Equal(NormalizationStatus.Rejected, result.Status);
        Assert.Equal("contributions[0].clients[0]." + field, result.Field);
        Assert.Equal("unexpected_field", result.Reason);
    }

    [Fact]
    public void AClientRowWithoutTokensIsRejected()
    {
        var document = Graph(("2026-11-17", 10, ["""{"client": "codex", "modelId": "gpt-5.1"}"""]));

        var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Utf8(document));

        Assert.Equal(NormalizationStatus.Rejected, result.Status);
        Assert.Equal("contributions[0].clients[0].tokens", result.Field);
        Assert.Equal("missing_field", result.Reason);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("cacheRead")]
    [InlineData("reasoning")]
    public void AClientTokenComponentThatIsMissingIsRejectedRatherThanZeroed(string missing)
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

        var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Utf8(document));

        Assert.Equal(NormalizationStatus.Rejected, result.Status);
        Assert.Equal("contributions[0].clients[0].tokens." + missing, result.Field);
        Assert.Equal("missing_field", result.Reason);
    }

    [Fact]
    public void AClientTokenComponentThatIsNotAnIntegerIsRejected()
    {
        var document = Graph(
            ("2026-11-17", 10,
            [
                "{" + ClientHeader + ", \"tokens\": {\"input\": 10.5, " + ZeroComponents + "}}",
            ]));

        var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Utf8(document));

        Assert.Equal(NormalizationStatus.Rejected, result.Status);
        Assert.Equal("contributions[0].clients[0].tokens.input", result.Field);
        Assert.Equal("invalid_integer", result.Reason);
    }

    [Fact]
    public void ClientsThatAreNotAnArrayAreRejected()
    {
        var document = "{\n  \"meta\": {\"generatedAt\": \"2026-11-19T18:49:08.875485588+00:00\", \"version\": \"4.23.0\"},\n"
            + "  \"contributions\": [{\"date\": \"2026-11-17\", \"totals\": {\"tokens\": 10}, \"clients\": \"none\"}]\n}\n";

        var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Utf8(document));

        Assert.Equal(NormalizationStatus.Rejected, result.Status);
        Assert.Equal("contributions[0].clients", result.Field);
        Assert.Equal("invalid_type", result.Reason);
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
        var result = TokscaleGraphNormalizer.Normalize(FixtureFiles.Utf8(document));
        Assert.Equal(NormalizationStatus.Candidate, result.Status);
        return Assert.IsType<SnapshotCandidate>(result.Candidate);
    }
}

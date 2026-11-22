using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// The comparison bars on the TokScale cards: an absolute, non-gauge visualization of the
/// observed amounts. Every card carries the same common top — the largest observed amount
/// of either period across the cards on screen — so bars are comparable across cards, and
/// the scale is always stated as a token count. There is no limit, no percentage and no
/// gauge anywhere: a bar is one period's observed tokens against that common top.
/// </summary>
public sealed class TokScaleComparisonBarTests
{
    private static readonly DateOnly Today = new(2026, 11, 22);
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 3, 0, 0, TimeSpan.Zero);

    // ---------- the common top across both periods ----------

    [Fact]
    public void EveryCardCarriesTheLargestObservedAmountOfEitherPeriodAsItsCommonTop()
    {
        var cards = Cards(
            TodayParts(Part("openai", 200), Part("anthropic", 4_000), Part("nous", 0)),
            WeekParts(Part("openai", 6_000), Part("anthropic", 8_000)),
            ["openai", "anthropic", "nous", "xai_oauth"]);

        // The largest observed amount on the board is Anthropic's 8,000 for the week.
        var top = cards.Select(card => card.ComparisonTop).Distinct().ToList();
        Assert.Equal([8_000L], top);
        Assert.Equal(8_000, cards.Max(card => Math.Max(card.TodayTokens, card.WeekTokens)));
        // Nothing invented: the top is a token amount, not a percentage of one.
        Assert.Equal("8,000", cards[0].ComparisonTop.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void BarRatiosKeepThePositiveSizeOrderingOnTheSharedScale()
    {
        var cards = Cards(
            TodayParts(Part("openai", 200), Part("anthropic", 4_000)),
            WeekParts(Part("openai", 6_000), Part("anthropic", 8_000)),
            ["openai", "anthropic"]);

        var anthropic = cards.Single(card => card.ProviderKey == "anthropic");
        var openai = cards.Single(card => card.ProviderKey == "openai");

        // The largest observed amount fills the scale exactly once, others proportionally.
        Assert.Equal(1.0, anthropic.WeekBarRatio, 6);
        Assert.Equal(0.5, anthropic.TodayBarRatio, 6);
        Assert.Equal(0.75, openai.WeekBarRatio, 6);
        // A bigger observed amount is always the wider bar; a smaller one never wins.
        Assert.True(openai.WeekBarRatio > openai.TodayBarRatio);
        Assert.True(anthropic.WeekBarRatio > openai.WeekBarRatio);
        Assert.All(cards.SelectMany(card => new[] { card.TodayBarRatio, card.WeekBarRatio }),
            ratio => Assert.InRange(ratio, 0.0, 1.0));
    }

    // ---------- unobserved is never drawn like a zero observation ----------

    [Fact]
    public void AnUnobservedCardIsNeverDrawnLikeAZeroObservation()
    {
        var cards = Cards(
            TodayParts(Part("nous", 0)),
            WeekParts(Part("nous", 0)),
            ["nous", "xai_oauth"]);

        var zeroObservation = cards.Single(card => card.ProviderKey == "nous");
        var notObserved = cards.Single(card => card.ProviderKey == "xai_oauth");

        // A zero-token part is a real observation: the card keeps its observed state, its
        // numbers and its bar rows (at zero width), and it does not stretch the top.
        Assert.True(zeroObservation.Observed);
        Assert.Equal(0, zeroObservation.ComparisonTop);
        Assert.False(zeroObservation.HasComparisonBars);
        Assert.Equal(0, zeroObservation.TodayBarRatio);
        Assert.Equal(0, zeroObservation.WeekBarRatio);

        // A provider the breakdown has not attributed anything to keeps its own state:
        // no bars, never a zero-shaped rendering of "nothing was measured".
        Assert.False(notObserved.Observed);
        Assert.False(notObserved.HasComparisonBars);
        Assert.Equal(0, notObserved.ComparisonTop);
        Assert.Contains("Not observed yet", notObserved.SummaryText, StringComparison.Ordinal);
        Assert.Equal("0", zeroObservation.TodayText);
        Assert.NotEqual(zeroObservation.SummaryText, notObserved.SummaryText);
    }

    [Fact]
    public void AnAllZeroBoardShowsNeitherBarsNorAnInventedTop()
    {
        var cards = Cards(
            TodayParts(Part("openai", 0), Part("anthropic", 0)),
            WeekParts(Part("openai", 0), Part("anthropic", 0)),
            ["openai", "anthropic", "xai_oauth"]);

        Assert.All(cards, card =>
        {
            Assert.Equal(0, card.ComparisonTop);
            Assert.False(card.HasComparisonBars);
            Assert.Equal(0, card.TodayBarRatio);
            Assert.Equal(0, card.WeekBarRatio);
        });
        // No card invents a scale and no card is decorated like a positive one.
        Assert.All(cards.Where(card => card.Observed), card =>
            Assert.Contains("0", card.SummaryText, StringComparison.Ordinal));
    }

    [Fact]
    public void UnobservedCardsNeverStretchTheSharedScale()
    {
        var cards = Cards(
            TodayParts(Part("openai", 1_500)),
            WeekParts(Part("openai", 1_500)),
            ["openai", "xai_oauth", "nous"]);

        var top = cards.Select(card => card.ComparisonTop).Distinct().Single();
        Assert.Equal(1_500, top);
        Assert.All(cards.Where(card => !card.Observed), unobserved =>
        {
            Assert.Equal((0L, 0L), (unobserved.TodayTokens, unobserved.WeekTokens));
            Assert.False(unobserved.HasComparisonBars);
        });
    }

    // ---------- the values stay honest in odd shapes of the data ----------

    [Fact]
    public void ADayWeekInversionKeepsTheBarsOnTheSameSharedScale()
    {
        // Real logs can produce a day above its week (late data, corrections): the bars
        // never assume the week is the larger period.
        var cards = Cards(
            TodayParts(Part("openai", 9_000)),
            WeekParts(Part("openai", 5_000)),
            ["openai"]);

        var card = Assert.Single(cards);
        Assert.Equal(9_000, card.ComparisonTop);
        Assert.Equal(1.0, card.TodayBarRatio, 6);
        Assert.Equal(5_000 / 9_000d, card.WeekBarRatio, 6);
        Assert.Equal("9,000", card.TodayText);
        Assert.Equal("5,000", card.WeekText);
    }

    [Fact]
    public void TheExistingCardContractIsUntouchedByTheBars()
    {
        var cards = Cards(
            TodayParts(Part("codex", "deepseek,opencode_go", "model-a", 800), Part("claude", "nous", "", 600)),
            WeekParts(Part("codex", "deepseek,opencode_go", "model-a", 2_400), Part("claude", "nous", "", 12_345)),
            ["deepseek", "opencode_go", "nous"]);

        // The documented order (week descending, ties by key) and the shared-amount
        // notice survive the bar fields: the cards mean the same thing they meant before.
        Assert.Equal(["nous", "deepseek", "opencode_go"], cards.Select(card => card.ProviderKey).ToArray());
        Assert.Equal([12_345L, 2_400L, 2_400L], cards.Select(card => card.WeekTokens).ToArray());
        var shared = cards.Where(card => card.IsMultiProvider).Select(card => card.ProviderKey).ToHashSet();
        Assert.Equal(new HashSet<string> { "deepseek", "opencode_go" }, shared);
        Assert.All(cards.Where(card => card.IsMultiProvider),
            card => Assert.False(string.IsNullOrWhiteSpace(card.MultiProviderText)));
        var top = cards.Select(card => card.ComparisonTop).Distinct().Single();
        Assert.Equal(12_345, top);
        Assert.All(cards, card => Assert.True(card.HasComparisonBars, $"{card.ProviderKey} lost its bars."));
    }

    // ---------- the scale is stated as a token count ----------

    [Fact]
    public void TheCommonScaleIsStatedAsATokenCountInBothLanguages()
    {
        var japanese = new UiCopy(UiLanguage.Japanese).TokScaleScaleTop(8_000);
        var english = new UiCopy(UiLanguage.English).TokScaleScaleTop(8_000);

        Assert.Contains("8,000", japanese, StringComparison.Ordinal);
        Assert.Contains("8,000", english, StringComparison.Ordinal);
        Assert.Contains("トークン", japanese, StringComparison.Ordinal);
        Assert.Contains("token", english, StringComparison.OrdinalIgnoreCase);
        // The label describes the longest bar, never a quota/limit or maximum.
        Assert.Contains("最長", japanese, StringComparison.Ordinal);
        Assert.Contains("Longest bar", english, StringComparison.Ordinal);
        Assert.DoesNotContain("最大", japanese, StringComparison.Ordinal);
        Assert.DoesNotContain("up to", english, StringComparison.OrdinalIgnoreCase);
        foreach (var text in new[] { japanese, english })
        {
            Assert.DoesNotContain("%", text, StringComparison.Ordinal);
            Assert.DoesNotContain("上限", text, StringComparison.Ordinal);
            Assert.DoesNotContain("limit", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gauge", text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.NotEqual(japanese, english);
    }

    [Fact]
    public void TheBarRowsAreNamedByTheirObservedTokenAmountsForBothLanguages()
    {
        var japanese = new UiCopy(UiLanguage.Japanese).TokScaleBarsName(200, 6_000);
        var english = new UiCopy(UiLanguage.English).TokScaleBarsName(200, 6_000);

        Assert.Contains("200", japanese, StringComparison.Ordinal);
        Assert.Contains("6,000", japanese, StringComparison.Ordinal);
        Assert.Contains("200", english, StringComparison.Ordinal);
        Assert.Contains("6,000", english, StringComparison.Ordinal);
        Assert.NotEqual(japanese, english);
    }

    // ---------- helpers ----------

    private static IReadOnlyList<TokScaleUsageCard> Cards(
        UsageBreakdown today, UsageBreakdown week, string[] tracked) =>
        UsageBreakdownAggregation.TokScaleCards(today, week, tracked);

    private static UsageBreakdown TodayParts(params UsagePart[] parts) =>
        Breakdown(parts);
    private static UsageBreakdown WeekParts(params UsagePart[] parts) =>
        Breakdown(parts);

    private static UsageBreakdown Breakdown(params UsagePart[] parts) =>
        new(Today, Today, parts,
            [new UsageCoverage(Today, "col-a", parts.Sum(part => part.Total), parts.Sum(part => part.Total))],
            [new UsageCollectorReceipt("col-a", 1, Now.AddHours(-1))]);

    private static UsagePart Part(string provider, long total) => Part("claude", provider, "", total);

    private static UsagePart Part(string client, string provider, string model, long total) =>
        new(client, provider, model, total, 0, 0, 0, 0, total);
}

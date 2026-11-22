using System.Globalization;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// The dashboard's observed-usage cards, keyed by provider: tokens one provider served
/// are summed across clients and models, a row that several providers served together
/// is one combination card (its tokens go to no single provider's card), and providers
/// nobody has added yet still get their card once they are observed.
/// </summary>
public sealed class TokScaleProviderCardsTests
{
    private static readonly UiLanguage[] BothLanguages = [UiLanguage.English, UiLanguage.Japanese];

    [Fact]
    public void TokensOnOneProviderSumAcrossClientsAndModels()
    {
        // The same provider served usage logged under different clients and models:
        // the card is the provider's total, not a per-client split.
        var today = Breakdown(
            Part("claude", "openai", "gpt-5", 10),
            Part("codex", "openai", "gpt-5", 4),
            Part("codex", "openai", "gpt-5-mini", 1));
        var week = Breakdown(
            Part("claude", "openai", "gpt-5", 100),
            Part("codex", "openai", "gpt-5", 20),
            Part("codex", "openai", "gpt-5-mini", 5));

        var card = Assert.Single(UsageBreakdownAggregation.TokScaleCards(today, week, [], UiLanguage.English));

        Assert.Equal("openai", card.ProviderKey);
        Assert.Equal("OpenAI", card.DisplayName);
        Assert.Equal("15", card.TodayText);
        Assert.Equal("125", card.WeekText);
        Assert.True(card.Observed);
    }

    [Fact]
    public void MultipleProvidersInOneRowAreOneCombinationCard()
    {
        // One part may name several providers (the collector's normalized, sorted,
        // de-duplicated list, joined with commas). Splitting it would credit both
        // providers' cards with tokens they share, so the combination itself is one card.
        var today = Breakdown(Part("claude", "deepseek,opencode_go", "model-a", 12));
        var week = Breakdown(Part("claude", "deepseek,opencode_go", "model-a", 34));

        var card = Assert.Single(UsageBreakdownAggregation.TokScaleCards(today, week, [], UiLanguage.English));

        Assert.Equal("deepseek,opencode_go", card.ProviderKey);
        Assert.Equal("DeepSeek + OpenCode Go", card.DisplayName);
        Assert.True(card.IsMultiProvider);
        Assert.False(Assert.Single(UsageBreakdownAggregation.TokScaleCards(
            Breakdown(Part("claude", "deepseek", "model-a", 5)), Breakdown(), [], UiLanguage.English)).IsMultiProvider);
        Assert.Equal("12", card.TodayText);
        Assert.Equal("34", card.WeekText);
    }

    [Fact]
    public void TheCardTotalsNeverDoubleCount()
    {
        // Whatever the key shape, every part lands on exactly one card: the cards'
        // totals must add up to the parts' totals.
        var today = Breakdown(
            Part("claude", "openai", "gpt-5", 10),
            Part("codex", "deepseek,opencode_go", "model-a", 20),
            Part("codex", "anthropic", "model-b", 30),
            Part("codex", "", "model-c", 40));
        var week = Breakdown(
            Part("claude", "openai", "gpt-5", 100),
            Part("codex", "nous", "model-d", 200),
            Part("codex", "", "model-c", 400));

        foreach (var language in BothLanguages)
        {
            var cards = UsageBreakdownAggregation.TokScaleCards(today, week, [], language);
            Assert.Equal(100, cards.Sum(card => card.TodayTokens));
            Assert.Equal(700, cards.Sum(card => card.WeekTokens));
            Assert.Equal(100, today.Parts.Sum(part => part.Total));
            Assert.Equal(700, week.Parts.Sum(part => part.Total));
        }
    }

    [Fact]
    public void AnEmptyProviderIsItsOwnUnknownCard()
    {
        var today = Breakdown(Part("claude", "", "model-a", 7));
        var week = Breakdown(Part("claude", "", "model-a", 70));

        foreach (var language in BothLanguages)
        {
            var card = Assert.Single(UsageBreakdownAggregation.TokScaleCards(today, week, [], language));
            Assert.Equal(string.Empty, card.ProviderKey);
            Assert.False(card.IsMultiProvider);
            Assert.Equal(new UiCopy(language).UsageBreakdownUnknown, card.DisplayName);
            Assert.Equal("7", card.TodayText);
            Assert.Equal("70", card.WeekText);
        }
    }

    [Fact]
    public void ACombinationCardMixesKnownAndUnknownIdsWithoutInventingNames()
    {
        var today = Breakdown(Part("claude", "nous,not-in-catalog", "model-a", 5));
        var week = Breakdown();

        var card = Assert.Single(UsageBreakdownAggregation.TokScaleCards(today, week, [], UiLanguage.English));
        Assert.Equal("Nous + not-in-catalog", card.DisplayName);
        Assert.True(card.IsMultiProvider);
    }

    [Fact]
    public void CardsAreSortedByTheWeekTotalDescendingWithTiesByKey()
    {
        var today = Breakdown(Part("claude", "openai", "m", 1));
        var week = Breakdown(
            Part("claude", "openai", "m", 50),
            Part("claude", "anthropic", "m", 50),
            Part("claude", "deepseek", "m", 10));

        var cards = UsageBreakdownAggregation.TokScaleCards(today, week, [], UiLanguage.English);

        Assert.Equal(3, cards.Count);
        Assert.Equal("anthropic", cards[0].ProviderKey);
        Assert.Equal("openai", cards[1].ProviderKey);
        Assert.Equal("deepseek", cards[2].ProviderKey);
    }

    [Fact]
    public void CardsFollowTheLanguageWithoutChangingNumbers()
    {
        var today = Breakdown(Part("claude", "openai", "m", 10));
        var week = Breakdown(Part("claude", "openai", "m", 80));

        var english = Assert.Single(UsageBreakdownAggregation.TokScaleCards(today, week, [], UiLanguage.English));
        var japanese = Assert.Single(UsageBreakdownAggregation.TokScaleCards(today, week, [], UiLanguage.Japanese));

        Assert.Equal("OpenAI", english.DisplayName);
        Assert.Equal("OpenAI", japanese.DisplayName);
        Assert.Contains("Today 10", english.SummaryText, StringComparison.Ordinal);
        Assert.Contains("今日 10", japanese.SummaryText, StringComparison.Ordinal);
        Assert.Contains("7 days 80", english.SummaryText, StringComparison.Ordinal);
        Assert.Contains("7日 80", japanese.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void ATrackedProviderWithoutAttributionGetsItsNotObservedCard()
    {
        var today = Breakdown();
        var week = Breakdown();

        var cards = UsageBreakdownAggregation.TokScaleCards(today, week, ["grok-not-a-catalog-id", "openai"], UiLanguage.English);

        // A catalog provider the user added is "not observed yet"; an id outside the
        // catalog never resurrects as a card.
        var card = Assert.Single(cards);
        Assert.Equal("openai", card.ProviderKey);
        Assert.False(card.Observed);
        Assert.Contains("Not observed yet", card.SummaryText, StringComparison.Ordinal);
        Assert.Equal("0", card.TodayText);
        Assert.Equal("0", card.WeekText);
    }

    // ---------- helpers ----------

    private static UsageBreakdown Breakdown(params UsagePart[] parts) =>
        new(new DateOnly(2026, 11, 22), new DateOnly(2026, 11, 22), parts, [], []);

    private static UsagePart Part(string client, string provider, string model, long total) =>
        new(client, provider, model, total, 0, 0, 0, 0, total);
}

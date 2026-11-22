using System.Globalization;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// The dashboard's observed-usage cards under the user-selected provider model: the
/// section shows only the providers the user added ("add provider"), observed providers
/// are never auto-carded, a row several providers served together is not a card of its
/// own but feeds every provider it contains, and a card always means "usage through
/// that provider" — so a card that shares a row with another provider says so.
/// </summary>
public sealed class TokScaleProviderCardsTests
{
    private static readonly UiLanguage[] BothLanguages = [UiLanguage.English, UiLanguage.Japanese];

    // ---------- only user-selected providers get cards ----------

    [Fact]
    public void UntrackedObservedProvidersNeverBecomeCards()
    {
        var today = Breakdown(
            Part("claude", "openai", "gpt-5", 10),
            Part("codex", "deepseek", "model-a", 4));
        var week = Breakdown(
            Part("claude", "openai", "gpt-5", 100),
            Part("codex", "deepseek", "model-a", 40));

        // Only openai was added; deepseek is observed but must not be auto-carded.
        var cards = UsageBreakdownAggregation.TokScaleCards(today, week, ["openai"], UiLanguage.English);

        var card = Assert.Single(cards);
        Assert.Equal("openai", card.ProviderKey);
        Assert.Equal("10", card.TodayText);
        Assert.Equal("100", card.WeekText);
    }

    [Fact]
    public void NothingSelectedMeansNoCardsEvenWhenDataWasObserved()
    {
        var today = Breakdown(Part("claude", "anthropic", "", 10));
        var week = Breakdown(Part("claude", "anthropic", "", 80));

        Assert.Empty(UsageBreakdownAggregation.TokScaleCards(today, week, [], UiLanguage.English));
        // The observation still happened: the section can say so in its empty state.
        Assert.Equal(1, UsageBreakdownAggregation.ObservedProviderCount(today, week));
    }

    [Fact]
    public void AnUnknownTrackedIdNeverResurrectsAsACard()
    {
        var today = Breakdown(Part("claude", "openai", "m", 5));
        var week = Breakdown(Part("claude", "openai", "m", 50));

        var cards = UsageBreakdownAggregation.TokScaleCards(today, week, ["grok-not-a-catalog-id"], UiLanguage.English);

        Assert.Empty(cards);
    }

    // ---------- combination rows feed every provider they contain ----------

    [Fact]
    public void ACombinationRowFeedsEveryProviderItContains()
    {
        // The collector's normalized combination "deepseek,opencode_go" is not a card of
        // its own: both DeepSeek and OpenCode Go carry the row's tokens.
        var today = Breakdown(Part("claude", "deepseek,opencode_go", "model-a", 12));
        var week = Breakdown(Part("claude", "deepseek,opencode_go", "model-a", 34));

        var cards = UsageBreakdownAggregation.TokScaleCards(today, week, ["deepseek", "opencode_go"], UiLanguage.English);

        Assert.Equal(2, cards.Count);
        Assert.All(cards, card => Assert.DoesNotContain(",", card.ProviderKey, StringComparison.Ordinal));
        Assert.All(cards, card =>
        {
            Assert.Equal("12", card.TodayText);
            Assert.Equal("34", card.WeekText);
            Assert.True(card.Observed);
        });
    }

    [Fact]
    public void MixedOwnAndSharedRowsSumIntoEachCard()
    {
        // A card means "usage through that provider": own rows plus the combination rows
        // the provider is part of. Cards may therefore overlap by the shared amount.
        var today = Breakdown(
            Part("claude", "deepseek", "model-a", 100),
            Part("codex", "deepseek,opencode_go", "model-a", 50),
            Part("codex", "opencode_go", "model-b", 70));
        var week = Breakdown(Part("claude", "deepseek", "model-a", 200));

        var cards = UsageBreakdownAggregation.TokScaleCards(today, week, ["deepseek", "opencode_go"], UiLanguage.English);

        var deepseek = cards.Single(card => card.ProviderKey == "deepseek");
        var opencode = cards.Single(card => card.ProviderKey == "opencode_go");
        Assert.Equal("150", deepseek.TodayText);
        Assert.Equal("200", deepseek.WeekText);
        Assert.Equal("120", opencode.TodayText);
        Assert.Equal("0", opencode.WeekText);
    }

    [Fact]
    public void ACardIncludingSharedRowsCarriesTheSharedNotice()
    {
        var today = Breakdown(Part("claude", "deepseek,opencode_go", "model-a", 12));
        var week = Breakdown(Part("claude", "deepseek", "model-a", 34));

        foreach (var language in BothLanguages)
        {
            var cards = UsageBreakdownAggregation.TokScaleCards(today, week, ["deepseek", "opencode_go"], language);
            var copy = new UiCopy(language);
            var shared = cards.Where(card => card.IsMultiProvider).ToList();
            Assert.Equal(2, shared.Count);
            Assert.All(shared, card => Assert.Equal(copy.TokScaleSharedNotice, card.MultiProviderText));
            // A card fed only by its own rows carries no notice.
            var ownOnly = UsageBreakdownAggregation
                .TokScaleCards(Breakdown(), Breakdown(Part("claude", "deepseek", "m", 5)), ["deepseek"], language)
                .Single();
            Assert.False(ownOnly.IsMultiProvider);
            Assert.Equal(string.Empty, ownOnly.MultiProviderText);
        }
    }

    // ---------- consistency with the usage page ----------

    [Fact]
    public void ACardFedOnlyByItsOwnRowsMatchesTheUsagePageProviderGroup()
    {
        // For providers whose rows are all single-provider rows, the card's total must
        // equal the usage page's provider-dimension group for the same provider.
        var today = Breakdown(
            Part("claude", "openai", "gpt-5", 10),
            Part("codex", "openai", "gpt-5", 4),
            Part("codex", "openai", "gpt-5-mini", 1),
            Part("claude", "anthropic", "m", 30));
        var week = Breakdown(
            Part("claude", "openai", "gpt-5", 100),
            Part("codex", "openai", "gpt-5", 25),
            Part("claude", "anthropic", "m", 300));

        var cards = UsageBreakdownAggregation.TokScaleCards(today, week, ["openai", "anthropic"], UiLanguage.English);
        var groups = UsageBreakdownAggregation.GroupParts(
            [.. today.Parts, .. week.Parts], UsageBreakdownDimension.Provider, UiLanguage.English);

        foreach (var card in cards)
        {
            var group = groups.Single(group => group.Key == card.ProviderKey);
            Assert.Equal(group.Total, card.TodayTokens + card.WeekTokens);
        }
    }

    // ---------- unknown providers ----------

    [Fact]
    public void AnEmptyProviderIsObservedButNeverACard()
    {
        var today = Breakdown(Part("claude", "", "model-a", 7));
        var week = Breakdown(Part("claude", "", "model-a", 70));

        // The empty provider is never a card and never a selectable choice; it may only
        // surface as "something was observed" in the empty state's wording.
        Assert.Empty(UsageBreakdownAggregation.TokScaleCards(today, week, [], UiLanguage.English));
        Assert.Equal(0, UsageBreakdownAggregation.ObservedProviderCount(today, week));
    }

    // ---------- kept contracts ----------

    [Fact]
    public void TokensOnOneProviderSumAcrossClientsAndModels()
    {
        var today = Breakdown(
            Part("claude", "openai", "gpt-5", 10),
            Part("codex", "openai", "gpt-5", 4),
            Part("codex", "openai", "gpt-5-mini", 1));
        var week = Breakdown(
            Part("claude", "openai", "gpt-5", 100),
            Part("codex", "openai", "gpt-5", 20),
            Part("codex", "openai", "gpt-5-mini", 5));

        var card = Assert.Single(UsageBreakdownAggregation.TokScaleCards(today, week, ["openai"], UiLanguage.English));

        Assert.Equal("openai", card.ProviderKey);
        Assert.Equal("OpenAI", card.DisplayName);
        Assert.Equal("15", card.TodayText);
        Assert.Equal("125", card.WeekText);
        Assert.True(card.Observed);
    }

    [Fact]
    public void ATrackedProviderWithoutAttributionGetsItsNotObservedCard()
    {
        var today = Breakdown();
        var week = Breakdown();

        var cards = UsageBreakdownAggregation.TokScaleCards(today, week, ["openai"], UiLanguage.English);

        var card = Assert.Single(cards);
        Assert.Equal("openai", card.ProviderKey);
        Assert.False(card.Observed);
        Assert.Contains("Not observed yet", card.SummaryText, StringComparison.Ordinal);
        Assert.Equal("0", card.TodayText);
        Assert.Equal("0", card.WeekText);
    }

    [Fact]
    public void CardsAreSortedByTheWeekTotalDescendingWithTiesByKey()
    {
        var today = Breakdown(Part("claude", "openai", "m", 1));
        var week = Breakdown(
            Part("claude", "openai", "m", 50),
            Part("claude", "anthropic", "m", 50),
            Part("claude", "deepseek", "m", 10));

        var cards = UsageBreakdownAggregation.TokScaleCards(today, week, ["openai", "anthropic", "deepseek"], UiLanguage.English);

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

        var english = Assert.Single(UsageBreakdownAggregation.TokScaleCards(today, week, ["openai"], UiLanguage.English));
        var japanese = Assert.Single(UsageBreakdownAggregation.TokScaleCards(today, week, ["openai"], UiLanguage.Japanese));

        Assert.Equal("OpenAI", english.DisplayName);
        Assert.Equal("OpenAI", japanese.DisplayName);
        Assert.Contains("Today 10", english.SummaryText, StringComparison.Ordinal);
        Assert.Contains("今日 10", japanese.SummaryText, StringComparison.Ordinal);
        Assert.Contains("7 days 80", english.SummaryText, StringComparison.Ordinal);
        Assert.Contains("7日 80", japanese.SummaryText, StringComparison.Ordinal);
    }

    // ---------- helpers ----------

    private static UsageBreakdown Breakdown(params UsagePart[] parts) =>
        new(new DateOnly(2026, 11, 22), new DateOnly(2026, 11, 22), parts, [], []);

    private static UsagePart Part(string client, string provider, string model, long total) =>
        new(client, provider, model, total, 0, 0, 0, 0, total);
}

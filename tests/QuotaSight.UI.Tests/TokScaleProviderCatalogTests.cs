using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// The static provider catalog that backs the add-provider picker and the observed
/// cards' display names. Providers are the model-serving sources (OpenAI, xAI,
/// Anthropic, …) the breakdown attributes tokens to, aggregated across clients and
/// models. The count is pinned so a TokScale release that renames or drops a provider
/// id cannot silently drift through.
/// </summary>
public sealed class TokScaleProviderCatalogTests
{
    [Fact]
    public void TheCatalogIsPinnedToATokScaleVersion()
    {
        Assert.Equal("4.23.0", TokScaleProviderCatalog.TokScaleVersion);
    }

    [Fact]
    public void TheCatalogHoldsTwentyFourUniqueLowercaseProvidersWithDisplayNames()
    {
        var providers = TokScaleProviderCatalog.Providers;

        Assert.Equal(24, providers.Count);
        Assert.All(providers, provider => Assert.False(string.IsNullOrWhiteSpace(provider.Id)));
        Assert.All(providers, provider => Assert.False(string.IsNullOrWhiteSpace(provider.DisplayName)));
        Assert.Equal(
            providers.Select(provider => provider.Id).ToHashSet(StringComparer.Ordinal).Count,
            providers.Count);
        // The ids are the lowercase ids the usage breakdown attributes.
        Assert.All(providers, provider => Assert.Equal(provider.Id.ToLowerInvariant(), provider.Id));
        Assert.Equal(
            providers.Select(provider => provider.DisplayName).ToHashSet(StringComparer.Ordinal).Count,
            providers.Count);
        Assert.All(providers, provider => Assert.NotNull(TokScaleProviderCatalog.Find(provider.Id)));
    }

    [Fact]
    public void TheObservedProviderIdsMapToTheirProductNames()
    {
        // The ids below are the attribution ids the breakdown carries: single ids the
        // parsers emit, exactly as observed (lowercase, underscored).
        Assert.Equal("OpenAI", TokScaleProviderCatalog.DisplayNameOf("openai"));
        Assert.Equal("Anthropic", TokScaleProviderCatalog.DisplayNameOf("anthropic"));
        Assert.Equal("Claude (subscription SDK)", TokScaleProviderCatalog.DisplayNameOf("claude_subscription_directsdk_experimental"));
        Assert.Equal("Google", TokScaleProviderCatalog.DisplayNameOf("google"));
        Assert.Equal("xAI", TokScaleProviderCatalog.DisplayNameOf("xai"));
        Assert.Equal("xAI (OAuth)", TokScaleProviderCatalog.DisplayNameOf("xai_oauth"));
        Assert.Equal("DeepSeek", TokScaleProviderCatalog.DisplayNameOf("deepseek"));
        Assert.Equal("OpenCode Go", TokScaleProviderCatalog.DisplayNameOf("opencode_go"));
        Assert.Equal("GitHub Copilot", TokScaleProviderCatalog.DisplayNameOf("copilot"));
        Assert.Equal("Z.AI", TokScaleProviderCatalog.DisplayNameOf("zai"));
        Assert.Equal("MiniMax", TokScaleProviderCatalog.DisplayNameOf("minimax"));
        Assert.Equal("Nous", TokScaleProviderCatalog.DisplayNameOf("nous"));
    }

    [Fact]
    public void AnUnknownIdIsNotFoundAndFallsBackToTheRawId()
    {
        Assert.Null(TokScaleProviderCatalog.Find("no-such-provider"));
        Assert.Equal("no-such-provider", TokScaleProviderCatalog.DisplayNameOf("no-such-provider"));
    }

    [Fact]
    public void TheAddProviderPickerListsTheExistingFiveFirstThenTheProviderCatalogByDisplayName()
    {
        var choices = UiSettings.ProviderChoices(UiLanguage.English);

        // The five existing providers keep their slots at the top, in enum order.
        var existing = choices.Take(5).ToList();
        Assert.Equal(
            (ProviderConnectionChoice[])Enum.GetValues<ProviderConnectionChoice>()[..5],
            [.. existing.Select(choice => choice.Choice)]);
        Assert.All(existing, choice => Assert.Equal(string.Empty, choice.TokScaleProviderId));

        var catalog = choices.Skip(5).ToList();
        Assert.Equal(TokScaleProviderCatalog.Providers.Count, catalog.Count);
        Assert.All(catalog, choice => Assert.Equal(ProviderConnectionChoice.TokScale, choice.Choice));
        Assert.All(catalog, choice => Assert.False(string.IsNullOrWhiteSpace(choice.TokScaleProviderId)));
        // Every catalog provider is offered; the five existing connection kinds are kept
        // alongside, and no TokScale choice is filtered away to avoid them.
        Assert.Equal(
            TokScaleProviderCatalog.Providers.Select(provider => provider.Id).ToHashSet(StringComparer.Ordinal),
            catalog.Select(choice => choice.TokScaleProviderId).ToHashSet(StringComparer.Ordinal));
        Assert.Contains(catalog, choice => choice.TokScaleProviderId == "copilot");
        Assert.Contains(catalog, choice => choice.TokScaleProviderId == "opencode_go");

        // The tail is readable: display name order, case-insensitively.
        var names = catalog.Select(choice => choice.DisplayName).ToList();
        var sorted = names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(sorted, names);
    }

    [Fact]
    public void ThePickerNeverNamesTheSameProviderTwice()
    {
        var choices = UiSettings.ProviderChoices(UiLanguage.English);

        var identifiers = choices
            .Select(choice => choice.Choice == ProviderConnectionChoice.TokScale ? choice.TokScaleProviderId : choice.Choice.ToString())
            .ToList();
        Assert.Equal(identifiers.ToHashSet(StringComparer.Ordinal).Count, identifiers.Count);
    }

    [Fact]
    public void TheProviderCardAndFlowCopyExistsInBothLanguages()
    {
        foreach (var language in new[] { UiLanguage.Japanese, UiLanguage.English })
        {
            var copy = new UiCopy(language);

            Assert.False(string.IsNullOrWhiteSpace(copy.TokScaleNotObserved));
            Assert.False(string.IsNullOrWhiteSpace(copy.TokScaleMultiProvider));
            Assert.False(string.IsNullOrWhiteSpace(copy.TokScalePanelDescription));
            Assert.False(string.IsNullOrWhiteSpace(copy.TokScalePanelAdd));
            Assert.False(string.IsNullOrWhiteSpace(copy.TokScalePanelRemove));
            Assert.False(string.IsNullOrWhiteSpace(copy.TokScalePanelTracked));
            Assert.False(string.IsNullOrWhiteSpace(copy.TokScalePanelNotTracked));
        }
    }

    [Fact]
    public void TheMultipleProviderLabelDiffersBetweenLanguages()
    {
        var japanese = new UiCopy(UiLanguage.Japanese);
        var english = new UiCopy(UiLanguage.English);

        Assert.NotEqual(japanese.TokScaleMultiProvider, english.TokScaleMultiProvider, StringComparer.Ordinal);
        Assert.Contains("プロバイダー", japanese.TokScaleMultiProvider, StringComparison.Ordinal);
        Assert.Contains("provider", english.TokScaleMultiProvider, StringComparison.OrdinalIgnoreCase);
    }
}

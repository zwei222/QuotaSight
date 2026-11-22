using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// The static TokScale client catalog that backs the add-provider picker and the
/// dashboard's usage-only cards. The count is pinned so a TokScale release that adds
/// or removes clients cannot silently drift through.
/// </summary>
public sealed class TokScaleCatalogTests
{
    [Fact]
    public void TheCatalogIsPinnedToATokScaleVersion()
    {
        Assert.Equal("4.23.0", TokScaleClientCatalog.TokScaleVersion);
    }

    [Fact]
    public void TheCatalogHoldsSixtyOneUniqueLowercaseClientsWithDisplayNames()
    {
        var clients = TokScaleClientCatalog.Clients;

        Assert.Equal(61, clients.Count);
        Assert.All(clients, client => Assert.False(string.IsNullOrWhiteSpace(client.Id)));
        Assert.All(clients, client => Assert.False(string.IsNullOrWhiteSpace(client.DisplayName)));
        Assert.Equal(
            clients.Select(client => client.Id).ToHashSet(StringComparer.Ordinal).Count,
            clients.Count);
        // The ids are the lowercase ids the usage breakdown attributes.
        Assert.All(clients, client => Assert.Equal(client.Id.ToLowerInvariant(), client.Id));
        Assert.Equal(
            clients.Select(client => client.DisplayName).ToHashSet(StringComparer.Ordinal).Count,
            clients.Count);
        Assert.All(clients, client => Assert.NotNull(TokScaleClientCatalog.Find(client.Id)));
    }

    [Fact]
    public void AnUnknownIdIsNotFoundAndFallsBackToTheRawId()
    {
        Assert.Null(TokScaleClientCatalog.Find("no-such-client"));
        Assert.Equal("no-such-client", TokScaleClientCatalog.DisplayNameOf("no-such-client"));
    }

    [Fact]
    public void TheAddProviderPickerListsTheExistingFiveFirstThenTheProviderCatalog()
    {
        var choices = UiSettings.ProviderChoices(UiLanguage.English);

        // The five existing providers keep their slots at the top, in enum order; the
        // provider catalog follows (see TokScaleProviderCatalogTests for its contract).
        var existing = choices.Take(5).ToList();
        Assert.Equal(
            (ProviderConnectionChoice[])Enum.GetValues<ProviderConnectionChoice>()[..5],
            [.. existing.Select(choice => choice.Choice)]);
        Assert.All(existing, choice => Assert.Equal(string.Empty, choice.TokScaleProviderId));

        var catalog = choices.Skip(5).ToList();
        Assert.Equal(TokScaleProviderCatalog.Providers.Count, catalog.Count);
        Assert.Equal(
            TokScaleProviderCatalog.Providers.Select(provider => provider.Id).ToHashSet(StringComparer.Ordinal),
            catalog.Select(choice => choice.TokScaleProviderId).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void ThePickerNeverNamesTheSameChoiceTwice()
    {
        var choices = UiSettings.ProviderChoices(UiLanguage.English);

        var identifiers = choices
            .Select(choice => choice.Choice == ProviderConnectionChoice.TokScale ? choice.TokScaleProviderId : choice.Choice.ToString())
            .ToList();
        Assert.Equal(identifiers.ToHashSet(StringComparer.Ordinal).Count, identifiers.Count);
    }

    [Fact]
    public void ClientDisplayNamesComeFromTheCatalogBeforeTheLegacyMap()
    {
        var english = new UiCopy(UiLanguage.English);

        // The catalog is the versioned source of truth for product names.
        Assert.Equal("Cursor IDE", english.TokScaleClientName("cursor"));
        Assert.Equal("Gemini CLI", english.TokScaleClientName("gemini"));
        // Ids outside the catalog keep their known names, then the raw id.
        Assert.Equal("Aider", english.TokScaleClientName("aider"));
        Assert.Equal("some-new-client", english.TokScaleClientName("some-new-client"));
    }

    [Fact]
    public void TheUsageOnlyCardAndFlowCopyExistsInBothLanguages()
    {
        foreach (var language in new[] { UiLanguage.Japanese, UiLanguage.English })
        {
            var copy = new UiCopy(language);

            Assert.False(string.IsNullOrWhiteSpace(copy.TokScaleNotObserved));
            Assert.False(string.IsNullOrWhiteSpace(copy.TokScalePanelDescription));
            Assert.False(string.IsNullOrWhiteSpace(copy.TokScalePanelAdd));
            Assert.False(string.IsNullOrWhiteSpace(copy.TokScalePanelRemove));
            Assert.False(string.IsNullOrWhiteSpace(copy.TokScalePanelTracked));
            Assert.False(string.IsNullOrWhiteSpace(copy.TokScalePanelNotTracked));
            Assert.False(string.IsNullOrWhiteSpace(copy.TokScalePanelTitle));
        }
    }

    [Fact]
    public void TheObservationBadgeSaysThereIsNoLimit()
    {
        Assert.Contains("上限なし", new UiCopy(UiLanguage.Japanese).TokScaleBadge, StringComparison.Ordinal);
        Assert.Contains("no limit", new UiCopy(UiLanguage.English).TokScaleBadge, StringComparison.Ordinal);
        // The observation wording the section already carried is kept.
        Assert.Contains("他端末", new UiCopy(UiLanguage.Japanese).TokScaleBadge, StringComparison.Ordinal);
        Assert.Contains("other devices", new UiCopy(UiLanguage.English).TokScaleBadge, StringComparison.Ordinal);
    }
}

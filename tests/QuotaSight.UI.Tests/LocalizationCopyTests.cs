using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class LocalizationCopyTests
{
    [Fact]
    public void Navigation_copy_comes_from_the_single_copy_table_in_both_languages()
    {
        var japanese = new UiCopy(UiLanguage.Japanese);
        var english = new UiCopy(UiLanguage.English);

        // The usage navigation label is copy like every other nav item: it must not be
        // built from a literal inside the view model, or a language switch relocalizes
        // the other items and leaves this one behind.
        Assert.Equal("利用量", japanese.NavUsage);
        Assert.Equal("Token usage", english.NavUsage);

        using var viewModel = new MainViewModel();
        Assert.Equal(english.NavUsage, viewModel.NavUsageText);

        viewModel.NavigateToUsage();
        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, args) => raised.Add(args.PropertyName);
        viewModel.Language = UiLanguage.Japanese;
        Assert.Equal(japanese.NavUsage, viewModel.NavUsageText);
        Assert.Equal(japanese.NavUsage, viewModel.HeaderTitle);
        Assert.Contains(nameof(MainViewModel.NavUsageText), raised);
    }

    [Fact]
    public void Privacy_footer_says_no_telemetry_and_explains_provider_authentication_in_both_languages()
    {
        var japanese = new UiCopy(UiLanguage.Japanese);
        var english = new UiCopy(UiLanguage.English);

        Assert.Equal("テレメトリーは送信しません。認証情報は連携先への認証に使用します。", japanese.NoSecretsLeave);
        Assert.Equal("No telemetry is sent. Credentials are used to authenticate with providers.", english.NoSecretsLeave);
        Assert.DoesNotContain("leave this app", english.NoSecretsLeave, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exception_notifications_have_localized_safe_copy()
    {
        var japanese = new UiCopy(UiLanguage.Japanese);
        var english = new UiCopy(UiLanguage.English);

        Assert.Equal("履歴", japanese.HistoryNotificationTitle);
        Assert.Equal("履歴データが破損しています。読み込める項目を表示しています。", japanese.HistoryCorrupt);
        Assert.Equal("トレイを利用できません", japanese.TrayUnavailableTitle);
        Assert.Equal("トレイを利用できません。アプリ内で操作してください。", japanese.TrayUnavailable);
        Assert.Equal("History", english.HistoryNotificationTitle);
        Assert.Equal("History data is damaged; showing available entries.", english.HistoryCorrupt);
        Assert.Equal("Tray unavailable", english.TrayUnavailableTitle);
        Assert.Equal("Tray unavailable. Use the app window instead.", english.TrayUnavailable);
    }

    [Theory]
    [InlineData("Loading")]
    [InlineData("Ready")]
    [InlineData("Empty")]
    [InlineData("Unsupported")]
    [InlineData("Stale")]
    [InlineData("Error")]
    [InlineData("Unconfigured")]
    public void Usage_breakdown_state_sentences_exist_in_both_languages(string state)
    {
        var japanese = new UiCopy(UiLanguage.Japanese).UsageBreakdownState(state);
        var english = new UiCopy(UiLanguage.English).UsageBreakdownState(state);

        Assert.False(string.IsNullOrWhiteSpace(japanese));
        Assert.False(string.IsNullOrWhiteSpace(english));
        Assert.NotEqual(japanese, english);
    }

    [Fact]
    public void Usage_breakdown_notes_and_labels_exist_in_both_languages()
    {
        var japanese = new UiCopy(UiLanguage.Japanese);
        var english = new UiCopy(UiLanguage.English);

        foreach (var copy in new[] { japanese, english })
        {
            Assert.Contains("TokScale", copy.UsageBreakdownNote, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(copy.UsageBreakdownTitle));
            Assert.False(string.IsNullOrWhiteSpace(copy.UsageBreakdownUnknown));
            Assert.False(string.IsNullOrWhiteSpace(copy.UsageBreakdownComponentInput));
            Assert.False(string.IsNullOrWhiteSpace(copy.UsageBreakdownComponentOutput));
            Assert.False(string.IsNullOrWhiteSpace(copy.UsageBreakdownComponentCacheRead));
            Assert.False(string.IsNullOrWhiteSpace(copy.UsageBreakdownComponentCacheWrite));
            Assert.False(string.IsNullOrWhiteSpace(copy.UsageBreakdownComponentReasoning));
            Assert.False(string.IsNullOrWhiteSpace(copy.UsageBreakdownPartial));
            Assert.False(string.IsNullOrWhiteSpace(copy.UsageBreakdownUnclassified(1)));
            Assert.False(string.IsNullOrWhiteSpace(copy.UsageBreakdownTokens(1)));
            Assert.False(string.IsNullOrWhiteSpace(copy.UsageBreakdownShare(50m)));
            Assert.False(string.IsNullOrWhiteSpace(copy.UsageBreakdownOverAttributionRow("2026-11-01", 5, 4)));
            Assert.False(string.IsNullOrWhiteSpace(copy.UsageFailure(UsageFailureKind.BreakdownUnsupported)));
            Assert.False(string.IsNullOrWhiteSpace(copy.UsageFailure(UsageFailureKind.CollectorNotFound)));
            foreach (UsageBreakdownDimension dimension in Enum.GetValues<UsageBreakdownDimension>())
            {
                Assert.False(string.IsNullOrWhiteSpace(copy.UsageBreakdownDimensionName(dimension)));
            }
        }

        // The observation note carries the log-deletion and cache-client caveats verbatim.
        Assert.Contains("既定30日", japanese.UsageBreakdownNote, StringComparison.Ordinal);
        Assert.Contains("30 days by default", english.UsageBreakdownNote, StringComparison.Ordinal);
        Assert.Contains("クライアント", japanese.UsageBreakdownDimensionName(UsageBreakdownDimension.Client), StringComparison.Ordinal);
        Assert.Equal("Client", english.UsageBreakdownDimensionName(UsageBreakdownDimension.Client));
    }

    [Fact]
    public void TokScale_section_copy_exists_in_both_languages_and_known_clients_have_names()
    {
        var japanese = new UiCopy(UiLanguage.Japanese);
        var english = new UiCopy(UiLanguage.English);

        foreach (var copy in new[] { japanese, english })
        {
            Assert.False(string.IsNullOrWhiteSpace(copy.TokScaleSectionTitle));
            Assert.False(string.IsNullOrWhiteSpace(copy.TokScaleBadge));
            Assert.False(string.IsNullOrWhiteSpace(copy.TokScaleToday));
            Assert.False(string.IsNullOrWhiteSpace(copy.TokScaleWeek));
            foreach (var state in new[] { "Unconfigured", "Loading", "Ready", "Empty", "Stale", "Error" })
            {
                Assert.False(string.IsNullOrWhiteSpace(copy.TokScaleState(state)));
            }
        }

        Assert.Contains("他端末", japanese.TokScaleBadge, StringComparison.Ordinal);
        Assert.Contains("other devices", english.TokScaleBadge, StringComparison.Ordinal);
        Assert.Equal("Claude Code", japanese.TokScaleClientName("claude"));
        Assert.Equal("Claude Code", english.TokScaleClientName("claude"));
        Assert.Equal("Codex CLI", english.TokScaleClientName("codex"));
        // An unknown id is displayed as-is, never invented.
        Assert.Equal("some-new-client", english.TokScaleClientName("some-new-client"));
        Assert.Equal("some-new-client", japanese.TokScaleClientName("some-new-client"));
    }

    [Fact]
    public void Breakdown_and_TokScale_copy_is_present_in_both_languages_and_Japanese_is_never_English_passthrough()
    {
        var japanese = new UiCopy(UiLanguage.Japanese);
        var english = new UiCopy(UiLanguage.English);

        var pairs = new List<(string Japanese, string English, string Name)>
        {
            (japanese.UsageBreakdownNote, english.UsageBreakdownNote, nameof(UiCopy.UsageBreakdownNote)),
            (japanese.UsageBreakdownTitle, english.UsageBreakdownTitle, nameof(UiCopy.UsageBreakdownTitle)),
            (japanese.UsageBreakdownDimensionSelector, english.UsageBreakdownDimensionSelector, nameof(UiCopy.UsageBreakdownDimensionSelector)),
            (japanese.UsageBreakdownUnknown, english.UsageBreakdownUnknown, nameof(UiCopy.UsageBreakdownUnknown)),
            (japanese.UsageBreakdownTotal, english.UsageBreakdownTotal, nameof(UiCopy.UsageBreakdownTotal)),
            (japanese.UsageBreakdownPartial, english.UsageBreakdownPartial, nameof(UiCopy.UsageBreakdownPartial)),
            (japanese.TokScaleSectionTitle, english.TokScaleSectionTitle, nameof(UiCopy.TokScaleSectionTitle)),
            (japanese.TokScaleBadge, english.TokScaleBadge, nameof(UiCopy.TokScaleBadge)),
            (japanese.TokScaleToday, english.TokScaleToday, nameof(UiCopy.TokScaleToday)),
            (japanese.TokScaleWeek, english.TokScaleWeek, nameof(UiCopy.TokScaleWeek)),
        };

        foreach (UsageBreakdownDimension dimension in Enum.GetValues<UsageBreakdownDimension>())
        {
            pairs.Add((japanese.UsageBreakdownDimensionName(dimension), english.UsageBreakdownDimensionName(dimension), $"dimension {dimension}"));
        }

        foreach (var state in new[] { "Loading", "Ready", "Empty", "Stale", "Error" })
        {
            pairs.Add((japanese.UsageBreakdownState(state), english.UsageBreakdownState(state), $"breakdown state {state}"));
        }

        // "Unconfigured" is a TokScale state now: without a connection the section stays
        // visible with an empty state instead of hiding itself.
        foreach (var state in new[] { "Unconfigured", "Loading", "Ready", "Empty", "Stale", "Error" })
        {
            pairs.Add((japanese.TokScaleState(state), english.TokScaleState(state), $"TokScale state {state}"));
        }

        pairs.Add((japanese.TokScaleEmptyTitle, english.TokScaleEmptyTitle, nameof(UiCopy.TokScaleEmptyTitle)));
        pairs.Add((japanese.TokScaleEmptyBodyUnconfigured, english.TokScaleEmptyBodyUnconfigured, nameof(UiCopy.TokScaleEmptyBodyUnconfigured)));
        pairs.Add((japanese.TokScaleEmptyBody, english.TokScaleEmptyBody, nameof(UiCopy.TokScaleEmptyBody)));
        pairs.Add((japanese.TokScaleOpenSettings, english.TokScaleOpenSettings, nameof(UiCopy.TokScaleOpenSettings)));

        foreach (var component in new (Func<string> Japanese, Func<string> English, string Name)[]
        {
            (() => japanese.UsageBreakdownComponentInput, () => english.UsageBreakdownComponentInput, "Input"),
            (() => japanese.UsageBreakdownComponentOutput, () => english.UsageBreakdownComponentOutput, "Output"),
            (() => japanese.UsageBreakdownComponentCacheRead, () => english.UsageBreakdownComponentCacheRead, "CacheRead"),
            (() => japanese.UsageBreakdownComponentCacheWrite, () => english.UsageBreakdownComponentCacheWrite, "CacheWrite"),
            (() => japanese.UsageBreakdownComponentReasoning, () => english.UsageBreakdownComponentReasoning, "Reasoning"),
        })
        {
            pairs.Add((component.Japanese(), component.English(), component.Name));
        }

        foreach (var (japaneseText, englishText, name) in pairs)
        {
            Assert.False(string.IsNullOrWhiteSpace(japaneseText), $"The Japanese copy for {name} is missing.");
            Assert.False(string.IsNullOrWhiteSpace(englishText), $"The English copy for {name} is missing.");
            Assert.NotEqual(englishText, japaneseText);
            // The Japanese sentence must be written in Japanese: identical wording with
            // different punctuation is still an untranslated passthrough.
            Assert.True(
                HasJapaneseGlyph(japaneseText),
                $"The Japanese copy for {name} is an English passthrough: '{japaneseText}'.");
        }

        // The dimension switcher must carry its own accessibility name, not reuse the card title.
        Assert.Equal("内訳の集計単位", japanese.UsageBreakdownDimensionSelector);
        Assert.Equal("Breakdown grouping", english.UsageBreakdownDimensionSelector);
    }

    private static bool HasJapaneseGlyph(string text) =>
        text.Any(character => character is >= '\u3040' and <= '\u30FF' or >= '\u4E00' and <= '\u9FFF');
}

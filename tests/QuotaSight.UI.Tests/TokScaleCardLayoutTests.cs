using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using QuotaSight.Infrastructure;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// The dashboard's observed-usage cards as laid-out pixels (headless Skia): a provider
/// name wider than the card wraps onto two lines and stays fully readable — no
/// ellipsis cut — and every card in one WrapPanel row is the same height, whether or
/// not it carries the shared-amount notice.
/// </summary>
public sealed class TokScaleCardLayoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 22);
    private const double Tolerance = 0.5;

    /// <summary>Nine selected providers, three of them sharing combination rows.</summary>
    private static readonly string[] NineProviders =
    [
        "openai",
        "anthropic",
        "claude_subscription_directsdk_experimental",
        "deepseek",
        "opencode_go",
        "zai",
        "xai_oauth",
        "google",
        "nous",
    ];

    // ---------- the long name wraps instead of being cut ----------

    [AvaloniaFact]
    public async Task ANameLongerThanTheCardWrapsToTwoLinesAndStaysFullyReadable()
    {
        var window = await Section(420, wideFont: true);

        var (card, border) = Cards(window).Single(entry =>
            entry.Card.ProviderKey == "claude_subscription_directsdk_experimental");

        // Squeeze the card below the name's natural width, the exact real-machine report:
        // the name ("Claude (subscription SDK)") is wider than the card. The width comes
        // from the measured name itself, so the scenario holds on any font metrics.
        var name = CardName(border);
        var natural = SqueezeBelowLabelWidth(border, name);
        window.UpdateLayout();

        // The template contract itself: wrap, at most two lines, nothing trimmed off.
        Assert.Equal(TextWrapping.Wrap, name.TextWrapping);
        Assert.Equal(TextTrimming.None, name.TextTrimming);
        Assert.True(name.MaxLines == 2, $"The name allows {name.MaxLines} lines; it must be capped at two.");

        // The name really is too wide for the card here, so it must have wrapped.
        Assert.True(natural > name.Bounds.Width,
            $"The scenario no longer exercises the bug: '{name.Text}' measures {natural:0.#}px but the element is {name.Bounds.Width:0.#}px wide.");
        Assert.True(name.TextLayout.TextLines.Count == 2,
            $"'{name.Text}' laid out on {name.TextLayout.TextLines.Count} line(s) at {name.Bounds.Width:0.#}px; a too-wide name must wrap onto two lines, not be cut.");

        // Every card's name stays inside its own element in both languages.
        AssertEveryNameLaysOutInsideItsElement(window);
        window.ViewModel.Language = UiLanguage.Japanese;
        window.UpdateLayout();
        AssertEveryNameLaysOutInsideItsElement(window);
        window.Close();
    }

    // ---------- one row, one card height ----------

    [AvaloniaFact]
    public async Task CardsSharingARowAreTheSameHeightWhetherOrNotTheyCarryTheSharedNotice()
    {
        var window = await Section(1100);

        var entries = Cards(window);
        // A long weekly total makes one card's summary wrap to two lines, the way real
        // observation data does; its row must not become taller than the others. The card
        // is squeezed from the summary's measured width, not a fixed pixel count.
        var squeezed = entries.Single(entry => entry.Card.ProviderKey == "claude_subscription_directsdk_experimental");
        SqueezeBelowLabelWidth(squeezed.Border, CardSummary(squeezed.Border));
        window.UpdateLayout();
        var panel = window.FindControl<ItemsControl>("TokScaleCards")!.ItemsPanelRoot!;
        var rows = GroupRowsByY(entries, panel);
        Assert.True(rows.Count >= 2, $"Expected several WrapPanel rows, got {rows.Count} (panel {panel.Bounds.Width:0.#}x{panel.Bounds.Height:0.#}).");

        // The scenario must actually mix the two kinds in one row, or the assertion below
        // proves nothing.
        var mixedRow = rows.FirstOrDefault(row =>
            row.Any(entry => entry.Card.IsMultiProvider) && row.Any(entry => !entry.Card.IsMultiProvider));
        Assert.True(mixedRow is not null,
            "No row mixes a shared-notice card with a plain card; the height comparison is vacuous.");

        // Every card is the same height — within its row and across all rows — whatever
        // the name, summary or shared notice lays out to.
        var heights = entries.Select(entry => entry.Border.Bounds.Height).Distinct().ToList();
        Assert.True(heights.Count == 1,
            $"Cards differ in height: {string.Join(", ", entries.Select(entry => $"{entry.Card.ProviderKey}={entry.Border.Bounds.Height:0.#}"))}.");

        // The shared notice never changes a card's height either, in Japanese too.
        window.ViewModel.Language = UiLanguage.Japanese;
        window.UpdateLayout();
        var japaneseRows = GroupRowsByY(Cards(window), panel);
        var japaneseHeights = japaneseRows.SelectMany(row => row).Select(entry => entry.Border.Bounds.Height).Distinct().ToList();
        Assert.True(japaneseHeights.Count == 1,
            $"Cards differ in height (Japanese): {japaneseHeights.Select(h => h.ToString("0.#")).ToList()}.");
        window.Close();
    }

    // ---------- the shared notice wraps instead of being cut ----------

    [AvaloniaFact]
    public async Task TheSharedNoticeWrapsInsideTheCardInsteadOfBeingCutOff()
    {
        var window = await Section(420, wideFont: true);

        var (card, border) = Cards(window).Single(entry =>
            entry.Card.ProviderKey == "anthropic");
        Assert.True(card.IsMultiProvider, "The Anthropic card must carry the shared notice for this test to mean anything.");

        // Squeeze the card the way the real-machine report does; the Japanese notice is
        // then wider than the card's content area and would be cut at the right edge. The
        // width comes from the measured notice, so any font still overflows it.
        var notice = SharedNotice(border);
        var natural = SqueezeBelowLabelWidth(border, notice);
        window.UpdateLayout();
        // The scenario must actually exercise the bug: the notice is wider than its element.
        Assert.True(natural > notice.Bounds.Width,
            $"The scenario no longer exercises the bug: '{notice.Text}' measures {natural:0.#}px but the element is {notice.Bounds.Width:0.#}px wide.");
        AssertNoticeStaysReadable(window);
        window.ViewModel.Language = UiLanguage.Japanese;
        window.UpdateLayout();
        // A language change rebuilds the cards, so the squeezed border above is detached
        // now: resolve the card again and squeeze from the Japanese notice's own measured
        // width, or this half of the scenario silently stops exercising the cut.
        var japaneseBorder = Cards(window).Single(entry => entry.Card.ProviderKey == "anthropic").Border;
        var japaneseNotice = SharedNotice(japaneseBorder);
        var japaneseNatural = SqueezeBelowLabelWidth(japaneseBorder, japaneseNotice);
        window.UpdateLayout();
        Assert.True(japaneseNatural > japaneseNotice.Bounds.Width,
            $"The Japanese scenario no longer exercises the bug: '{japaneseNotice.Text}' measures {japaneseNatural:0.#}px but the element is {japaneseNotice.Bounds.Width:0.#}px wide.");
        AssertNoticeStaysReadable(window);
        window.Close();
    }

    // ---------- the shared notice on real, unsqueezed cards ----------

    /// <summary>Real WrapPanel cards at the widths the window actually uses — no artificial
    /// MaxWidth squeeze — in both languages: every shared notice's drawn lines stay inside
    /// the card's interior, and a one-line notice keeps visible clearance from the card's
    /// inner right edge instead of ending flush against it.</summary>
    [AvaloniaFact]
    public async Task OnRealCardsTheSharedNoticeStaysInsideTheCardInteriorWithClearance()
    {
        foreach (var width in new[] { 1100, 420 })
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.Japanese })
            {
                var window = await Section(width);
                window.ViewModel.Language = language;
                window.UpdateLayout();
                AssertNoticeFitsInsideRealCards(window, width, language);
                window.Close();
            }
        }
    }

    private static void AssertNoticeFitsInsideRealCards(MainWindow window, int width, UiLanguage language)
    {
        foreach (var entry in Cards(window).Where(entry => entry.Card.IsMultiProvider))
        {
            var border = entry.Border;
            var notice = SharedNotice(border);
            Assert.Equal(TextWrapping.Wrap, notice.TextWrapping);
            Assert.Equal(TextTrimming.None, notice.TextTrimming);

            var lines = notice.TextLayout.TextLines;
            Assert.True(lines.Count > 0, $"'{notice.Text}' has no measured text lines.");
            var widest = lines.Max(line => line.Width);

            var thickness = border.BorderThickness;
            var innerRight = border.Bounds.Width - border.Padding.Right - thickness.Right;
            var interior = border.Bounds.Width
                - border.Padding.Left - border.Padding.Right
                - thickness.Left - thickness.Right;
            var textRightInBorder = (notice.TranslatePoint(default, border)?.X ?? 0) + widest;
            var label = $"{language}, {width}px window, card '{entry.Card.ProviderKey}'";

            // Nothing is drawn past its own element or the card's interior: no right-edge cut.
            Assert.True(widest <= notice.Bounds.Width + 1,
                $"{label}: the widest notice line measures {widest:0.#}px but its element is {notice.Bounds.Width:0.#}px wide.");
            Assert.True(textRightInBorder <= innerRight + 0.5,
                $"{label}: the notice's drawn text reaches x={textRightInBorder:0.#} inside the card but the interior ends at x={innerRight:0.#}.");

            if (lines.Count == 1)
            {
                // A one-line notice must not end flush against the card's inner edge — a
                // zero-gap line is exactly what reads as "cut off" on a real screen.
                Assert.True(innerRight - textRightInBorder >= 4,
                    $"{label}: the one-line notice ends {innerRight - textRightInBorder:0.#}px from the card's inner right edge; it needs visible clearance.");
            }
            else
            {
                Assert.True(lines.Count <= 2,
                    $"{label}: the notice lays out on {lines.Count} lines; the shared-notice area allows at most two.");
            }
        }
    }

    // ---------- helpers ----------

    /// <summary>
    /// Builds the dashboard with the nine providers and the three combination rows. The
    /// narrow 420px window and the deliberately wide font stand in for a real screen where
    /// a catalog name is longer than its card; the wide font is not what makes the name
    /// overflow — every squeeze in this file is derived from measured text.
    /// </summary>
    private static async Task<MainWindow> Section(int width, bool wideFont = false)
    {
        var port = new ScriptedDualReadPort();
        port.Breakdown.Enqueue(() => Breakdown(
            Part("claude", "anthropic,claude_subscription_directsdk_experimental", "", 4_000),
            Part("codex", "deepseek,opencode_go", "model-a", 800),
            Part("cli", "opencode_go,zai", "model-b", 300),
            Part("codex", "openai", "gpt-5", 2_000),
            Part("claude", "xai_oauth", "", 1_500),
            Part("claude", "google", "", 900),
            Part("claude", "nous", "", 600)));
        port.Breakdown.Enqueue(() => Breakdown(
            Part("claude", "anthropic,claude_subscription_directsdk_experimental", "", 123_456_789),
            Part("codex", "deepseek,opencode_go", "model-a", 2_400),
            Part("cli", "opencode_go,zai", "model-b", 900),
            Part("codex", "openai", "gpt-5", 6_000),
            Part("claude", "xai_oauth", "", 4_500),
            Part("claude", "google", "", 2_700),
            Part("claude", "nous", "", 12_345_678)));

        var viewModel = Create(port, TemporarySettingsStore());
        await viewModel.InitializeAsync();
        await viewModel.TokScaleLoadTask;
        foreach (var id in NineProviders)
        {
            await viewModel.AddTokScaleProviderAsync(id);
        }

        Assert.Equal(9, viewModel.TokScaleCards.Count);

        // A narrow window plus a deliberately wide font keep the widest catalog name
        // ("Claude (subscription SDK)") wider than its card, which is the real-machine
        // report; the wide font stands in for any future longer display name.
        var window = new MainWindow(viewModel) { Width = width, Height = 740 };
        window.Show();
        if (wideFont)
        {
            window.FindControl<Border>("TokScaleSection")!
                .SetValue(TextElement.FontFamilyProperty, new FontFamily("DejaVu Serif"));
        }
        window.UpdateLayout();
        return window;
    }

    private static IReadOnlyList<(TokScaleUsageCard Card, Border Border)> Cards(MainWindow window)
    {
        var items = window.FindControl<ItemsControl>("TokScaleCards")!;
        var borders = items.ItemsPanelRoot!.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("tokscale"))
            .Where(border => border.IsEffectivelyVisible)
            .ToList();
        Assert.Equal(window.ViewModel.TokScaleCards.Count, borders.Count);

        return [.. window.ViewModel.TokScaleCards.Select(card =>
        {
            var border = borders.Single(candidate =>
                CardName(candidate).Text == card.DisplayName);
            return (card, border);
        })];
    }

    /// <summary>Clusters the cards into WrapPanel rows by their absolute Y in the panel
    /// (rows differ by the line spacing; Bounds alone are relative to each presenter).</summary>
    private static List<List<(TokScaleUsageCard Card, Border Border)>> GroupRowsByY(
        IReadOnlyList<(TokScaleUsageCard Card, Border Border)> entries, Panel panel)
    {
        var rows = new List<List<(TokScaleUsageCard Card, Border Border)>>();
        foreach (var entry in entries.OrderBy(entry => YIn(entry.Border, panel)))
        {
            var y = YIn(entry.Border, panel);
            var row = rows.FirstOrDefault(candidate => Math.Abs(YIn(candidate[0].Border, panel) - y) <= Tolerance);
            if (row is null)
            {
                row = [];
                rows.Add(row);
            }

            row.Add(entry);
        }

        return rows;
    }

    private static double YIn(Visual visual, Visual panel) =>
        visual.TranslatePoint(default, panel)?.Y
            ?? throw new InvalidOperationException("The card is not attached to the items panel.");

    private static TextBlock CardName(Border card) =>
        card.GetVisualDescendants().OfType<TextBlock>().First(text => text.FontWeight == FontWeight.SemiBold);

    /// <summary>The card's usage summary: the only 12px text inside the card.</summary>
    private static TextBlock CardSummary(Border card) =>
        card.GetVisualDescendants().OfType<TextBlock>().First(text => Math.Abs(text.FontSize - 12) < 0.5);

    /// <summary>
    /// Narrows a card below the natural width of one of its labels, so that label must wrap.
    /// The width is measured from the label itself rather than fixed in pixels: font metrics
    /// differ per machine, and a fixed MaxWidth only squeezes a font wide enough to overflow
    /// it — on a narrower font (a Windows CI run measured the name at 152.9px, not 194.9px)
    /// the label slid back onto one line and the scenario stopped exercising the bug. The
    /// content area is set to 80% of the label's natural width: below the label, still above
    /// its widest word, so a wide label wraps onto exactly two lines and nothing is trimmed.
    /// The template's 170px MinWidth floor is released too, or it would refuse the
    /// artificial squeeze once the measured width falls below it. Returns the natural width.
    /// </summary>
    private static double SqueezeBelowLabelWidth(Border card, TextBlock label)
    {
        var natural = NaturalLineWidth(label);
        var chrome = card.Padding.Left + card.Padding.Right
            + card.BorderThickness.Left + card.BorderThickness.Right;
        card.MinWidth = 0;
        card.MaxWidth = (natural * 0.8) + chrome;
        return natural;
    }

    /// <summary>The shared-notice label: the only 10px text inside the card.</summary>
    private static TextBlock SharedNotice(Border card) =>
        card.GetVisualDescendants().OfType<TextBlock>().First(text => Math.Abs(text.FontSize - 10) < 0.5);

    /// <summary>The shared notice is capped at two wrapped lines and every line stays
    /// inside the card — nothing is trimmed off at the right edge, in either language.</summary>
    private static void AssertNoticeStaysReadable(MainWindow window)
    {
        window.UpdateLayout();
        foreach (var entry in Cards(window).Where(entry => entry.Card.IsMultiProvider))
        {
            var notice = SharedNotice(entry.Border);
            Assert.Equal(TextWrapping.Wrap, notice.TextWrapping);
            Assert.Equal(TextTrimming.None, notice.TextTrimming);

            var lines = notice.TextLayout.TextLines;
            Assert.True(lines.Count > 0, $"'{notice.Text}' has no measured text lines.");
            var widest = lines.Max(line => line.Width);
            Assert.True(widest <= notice.Bounds.Width + 1,
                $"The widest line of '{notice.Text}' measures {widest:0.#}px but its element is {notice.Bounds.Width:0.#}px wide; the shared notice would clip at the right edge.");
            Assert.True(lines.Count <= 2,
                $"'{notice.Text}' lays out on {lines.Count} lines at {notice.Bounds.Width:0.#}px; the shared-notice area allows at most two.");
        }
    }

    private static void AssertEveryNameLaysOutInsideItsElement(MainWindow window)
    {
        window.UpdateLayout();
        foreach (var entry in Cards(window))
        {
            var name = CardName(entry.Border);
            var lines = name.TextLayout.TextLines;
            Assert.True(lines.Count > 0, $"'{name.Text}' has no measured text lines.");
            var widest = lines.Max(line => line.Width);
            Assert.True(widest <= name.Bounds.Width + 1,
                $"The widest line of '{name.Text}' measures {widest:0.#}px but its element is {name.Bounds.Width:0.#}px wide; the name would clip.");
            Assert.True(name.TextLayout.TextLines.Count <= 2,
                $"'{name.Text}' lays out on {name.TextLayout.TextLines.Count} lines; the card allows at most two.");
        }
    }

    private static double NaturalLineWidth(TextBlock text)
    {
        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        var layout = new TextLayout(text.Text!, typeface, text.FontSize, Brushes.Black, maxWidth: double.PositiveInfinity);
        return layout.WidthIncludingTrailingWhitespace;
    }

    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    private static MainViewModel Create(ScriptedDualReadPort port, AppSettingsStore settingsStore) =>
        new(
            timeProvider: port.Clock,
            uiDispatcher: new ImmediateUiDispatcher(),
            settingsStore: settingsStore,
            usageReadPort: port,
            localTimeZone: Tokyo);

    /// <summary>A settings store rooted in a throwaway directory; no user setting is touched.</summary>
    private static AppSettingsStore TemporarySettingsStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "quotasight-tests", Guid.NewGuid().ToString("N"));
        return new AppSettingsStore(root);
    }

    private static UsageBreakdown Breakdown(params UsagePart[] parts) =>
        new(Today, Today, parts, [new UsageCoverage(Today, "col-a", parts.Sum(part => part.Total), parts.Sum(part => part.Total))],
            [new UsageCollectorReceipt("col-a", 1, Now.AddHours(-1))]);

    private static UsagePart Part(string client, string provider, string model, long total) =>
        new(client, provider, model, total, 0, 0, 0, 0, total);

    private sealed class ScriptedDualReadPort : IUsageReadPort
    {
        public MutableClock Clock { get; } = new(Now);
        public Queue<Func<UsageBreakdown>> Breakdown { get; } = new();
        public int BreakdownCalls;

        public Task<DailyUsage> ReadDailyAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken) =>
            Task.FromResult(new DailyUsage(from, to, [], 0));

        public Task<UsageBreakdown> ReadBreakdownAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref BreakdownCalls);
            if (!Breakdown.TryDequeue(out var next))
            {
                return Task.FromResult(new UsageBreakdown(from, to, [], [], []));
            }

            try
            {
                return Task.FromResult(next());
            }
            catch (Exception failure)
            {
                return Task.FromException<UsageBreakdown>(failure);
            }
        }
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan amount) => current += amount;
    }
}

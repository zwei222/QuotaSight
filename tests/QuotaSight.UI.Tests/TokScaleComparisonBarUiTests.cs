using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.VisualTree;
using QuotaSight.Infrastructure;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// The comparison bars as laid-out pixels (headless Skia): each observed card draws two
/// rows on a scale shared by every card on screen — today above the seven-day row, the
/// row length proportional to its observed tokens — while an unobserved card is never
/// drawn like a zero observation and an all-zero board shows neither bars nor a scale.
/// The scale is stated once, as a token count.
/// </summary>
public sealed class TokScaleComparisonBarUiTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 22);
    private const double Tolerance = 0.5;

    // ---------- the dashboard cards ----------

    [AvaloniaFact]
    public async Task DashboardCardsDrawTodayAboveWeekOnTheSharedTokenScale()
    {
        foreach (var width in new[] { 1100, 420 })
        {
            var window = await MainWindow(Board(), width);
            try
            {
                var entries = Cards(window.FindControl<ItemsControl>("TokScaleCards")!, window.ViewModel.TokScaleCards);
                Assert.True(entries.Count >= 5, $"Expected the tracked board, got {entries.Count} cards at {width}px.");

                foreach (var (card, border) in entries.Where(entry => entry.Card.HasComparisonBars))
                {
                    var (todayFill, todayTrack, weekFill, weekTrack) = BarRows(border, card);
                    // Position identifies the period: today is the upper row, the week below.
                    Assert.True(todayFill.TranslatePoint(default, border)!.Value.Y < weekFill.TranslatePoint(default, border)!.Value.Y,
                        $"'{card.ProviderKey}' ({width}px): the today row must sit above the week row.");

                    // Every bar is measured on the same shared scale: the row's drawn
                    // length is its tokens' share of the common top.
                    AssertBarShare(card, card.TodayBarRatio, todayFill, todayTrack, width, "today");
                    AssertBarShare(card, card.WeekBarRatio, weekFill, weekTrack, width, "week");
                }

                // Cross-card comparison on the shared scale: the board's largest amount
                // (Anthropic's 8,000-week) fills the row; OpenAI's 6,000-week is 0.75 of it.
                var anthropic = entries.Single(entry => entry.Card.ProviderKey == "anthropic");
                var openai = entries.Single(entry => entry.Card.ProviderKey == "openai");
                var (_, _, anthropicWeek, anthropicWeekTrack) = BarRows(anthropic.Border, anthropic.Card);
                var (_, _, openaiWeek, openaiWeekTrack) = BarRows(openai.Border, openai.Card);
                var openaiShare = openaiWeek.Bounds.Width / openaiWeekTrack.Bounds.Width;
                var anthropicShare = anthropicWeek.Bounds.Width / anthropicWeekTrack.Bounds.Width;
                // Pixel rounding keeps the shares within a hair of the exact ratio.
                Assert.InRange(anthropicShare, 0.98, 1.0);
                Assert.InRange(openaiShare, 0.73, 0.77);
                Assert.True(anthropicShare > openaiShare);
                Assert.True(openaiShare > 0);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaFact]
    public async Task EqualTokensHaveEqualPhysicalWidthAndOriginAcrossCardsAndPeriods()
    {
        foreach (var language in new[] { UiLanguage.Japanese, UiLanguage.English })
        {
            var window = await MainWindow(CrossScaleBoard());
            try
            {
                window.ViewModel.Language = language;
                window.UpdateLayout();
                var entries = Cards(window.FindControl<ItemsControl>("TokScaleCards")!, window.ViewModel.TokScaleCards);
                var anthropic = entries.Single(entry => entry.Card.ProviderKey == "anthropic");
                var openai = entries.Single(entry => entry.Card.ProviderKey == "openai");
                var (anthropicToday, anthropicTodayTrack, _, _) = BarRows(anthropic.Border, anthropic.Card);
                var (_, _, openaiWeek, openaiWeekTrack) = BarRows(openai.Border, openai.Card);

                Assert.Equal(8_000, anthropic.Card.TodayTokens);
                Assert.Equal(8_000, openai.Card.WeekTokens);
                Assert.Equal(anthropicToday.Bounds.Width, openaiWeek.Bounds.Width, Tolerance);
                Assert.Equal(anthropicToday.TranslatePoint(default, anthropic.Border)!.Value.X,
                    openaiWeek.TranslatePoint(default, openai.Border)!.Value.X, Tolerance);
                Assert.Equal(anthropicTodayTrack.Bounds.Width, openaiWeekTrack.Bounds.Width, Tolerance);
            }
            finally { window.Close(); }
        }
    }

    // ---------- unobserved is never drawn like a zero observation ----------

    [AvaloniaFact]
    public async Task AnUnobservedCardShowsNoBarRowsWhileAZeroObservationKeepsItsLabeledRows()
    {
        var window = await MainWindow(Board());
        try
        {
            var entries = Cards(window.FindControl<ItemsControl>("TokScaleCards")!, window.ViewModel.TokScaleCards);

            // A zero-token observation keeps its labeled rows: the period names are on
            // screen, both fills are at zero length, and the rows are named by the
            // observed amounts themselves.
            var zero = entries.Single(entry => entry.Card.ProviderKey == "nous");
            Assert.True(zero.Card.Observed);
            // The board has positive cards, so the common top exists: the zero
            // observation keeps its labeled rows on the shared scale, at zero length.
            Assert.True(zero.Card.HasComparisonBars);
            var (todayFill, _, weekFill, _) = BarRows(zero.Border, zero.Card);
            Assert.Equal(0, todayFill.Bounds.Width, Tolerance);
            Assert.Equal(0, weekFill.Bounds.Width, Tolerance);
            AssertPeriodLabelsPresent(zero.Border);

            // A provider nothing was attributed to yet shows no bar rows at all — never
            // the same shape as the zero observation above.
            var unobserved = entries.Single(entry => entry.Card.ProviderKey == "xai_oauth");
            Assert.False(unobserved.Card.Observed);
            Assert.False(unobserved.Card.HasComparisonBars);
            Assert.Null(FindBarsArea(unobserved.Border));
            Assert.NotEqual(zero.Card.SummaryText, unobserved.Card.SummaryText);

            // The bar slot is a fixed part of every card: mixing observed, zero and
            // unobserved cards in one WrapPanel row keeps them the same height.
            var heights = entries.Select(entry => entry.Border.Bounds.Height).Distinct().ToList();
            Assert.True(heights.Count == 1,
                $"Cards differ in height: {string.Join(", ", entries.Select(entry => $"{entry.Card.ProviderKey}={entry.Border.Bounds.Height:0.#}"))}.");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task JapaneseDashboardHeaderShowsItsFullTitleAndBadgeAt420Pixels()
    {
        var window = await MainWindow(Board());
        try
        {
            window.Width = 420;
            window.ViewModel.Language = UiLanguage.Japanese;
            window.UpdateLayout();
            var title = window.FindControl<TextBlock>("TokScaleTitle")!;
            var badge = window.FindControl<TextBlock>("TokScaleBadge")!;
            var section = window.FindControl<Border>("TokScaleSection")!;

            Assert.Contains("観測使用量", title.Text, StringComparison.Ordinal);
            Assert.True(title.Bounds.Width > 100, $"Japanese title width: {title.Bounds.Width:0.#}.");
            Assert.True(badge.Bounds.Width > 120, $"Japanese badge width: {badge.Bounds.Width:0.#}.");
            Assert.True(badge.Bounds.Height > 24, $"Japanese badge height: {badge.Bounds.Height:0.#}.");
            Assert.True(title.Bounds.Right <= section.Bounds.Right);
            Assert.True(badge.Bounds.Right <= section.Bounds.Right);
            var titleBounds = new Rect(title.TranslatePoint(default, section)!.Value, title.Bounds.Size);
            var badgeBounds = new Rect(badge.TranslatePoint(default, section)!.Value, badge.Bounds.Size);
            Assert.True(titleBounds.Intersect(badgeBounds).Width <= 0);
        }
        finally { window.Close(); }
    }

    // ---------- the common scale is stated as a token count ----------

    [AvaloniaFact]
    public async Task TheCommonScaleIsShownOnceAboveTheCardsAsATokenCount()
    {
        var window = await MainWindow(Board());
        try
        {
            var scale = window.FindControl<TextBlock>("TokScaleScaleLabel");
            Assert.NotNull(scale);
            Assert.True(scale!.IsVisible, "The common scale must be on screen when bars are drawn.");
            Assert.Contains("8,000", scale.Text, StringComparison.Ordinal);
            Assert.Contains("token", scale.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("%", scale.Text, StringComparison.Ordinal);

            // The rows are labelled too, so no period is identified by position alone.
            var entries = Cards(window.FindControl<ItemsControl>("TokScaleCards")!, window.ViewModel.TokScaleCards);
            var anthropic = entries.Single(entry => entry.Card.ProviderKey == "anthropic");
            AssertPeriodLabelsPresent(anthropic.Border);

            // The language switch relabels the scale and the rows, and the bars survive.
            window.ViewModel.Language = UiLanguage.Japanese;
            window.UpdateLayout();
            scale = window.FindControl<TextBlock>("TokScaleScaleLabel");
            Assert.True(scale!.IsVisible);
            Assert.Contains("8,000", scale.Text, StringComparison.Ordinal);
            Assert.Contains("トークン", scale.Text, StringComparison.Ordinal);
            var japaneseCards = Cards(window.FindControl<ItemsControl>("TokScaleCards")!, window.ViewModel.TokScaleCards);
            AssertPeriodLabelsPresent(japaneseCards.Single(entry => entry.Card.ProviderKey == "anthropic").Border, japanese: true);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task AnAllZeroBoardShowsNeitherBarsNorAScaleLabel()
    {
        var window = await MainWindow(ZeroBoard());
        try
        {
            var scale = window.FindControl<TextBlock>("TokScaleScaleLabel");
            Assert.True(scale is null || !scale.IsVisible,
                "An all-zero board must not show an invented scale.");

            var entries = Cards(window.FindControl<ItemsControl>("TokScaleCards")!, window.ViewModel.TokScaleCards);
            Assert.All(entries.Where(entry => entry.Card.Observed), entry =>
            {
                Assert.Equal(0, entry.Card.ComparisonTop);
                Assert.False(entry.Card.HasComparisonBars);
                Assert.Null(FindBarsArea(entry.Border));
                Assert.Contains("0", entry.Card.SummaryText, StringComparison.Ordinal);
            });
            Assert.All(entries.Where(entry => !entry.Card.Observed), entry =>
                Assert.False(entry.Card.HasComparisonBars));
        }
        finally { window.Close(); }
    }

    // ---------- the compact window ----------

    [AvaloniaFact]
    public async Task TheCompactWindowDrawsTheSameComparisonBarsOnTheSharedScale()
    {
        var viewModel = await ViewModel(Board());
        var window = new CompactQuotaWindow(viewModel);
        try
        {
            window.Show();
            window.UpdateLayout();

            var scale = window.FindControl<TextBlock>("CompactTokScaleScaleLabel");
            Assert.NotNull(scale);
            Assert.True(scale!.IsVisible);
            Assert.Contains("8,000", scale.Text, StringComparison.Ordinal);

            var entries = Cards(window.FindControl<ItemsControl>("CompactTokScaleCards")!, viewModel.TokScaleCards);
            Assert.True(entries.Count >= 5, $"Expected the tracked board, got {entries.Count} compact cards.");
            var anthropic = entries.Single(entry => entry.Card.ProviderKey == "anthropic");
            var (todayFill, todayTrack, weekFill, weekTrack) = BarRows(anthropic.Border, anthropic.Card);
            Assert.True(todayFill.TranslatePoint(default, anthropic.Border)!.Value.Y < weekFill.TranslatePoint(default, anthropic.Border)!.Value.Y);
            AssertBarShare(anthropic.Card, anthropic.Card.TodayBarRatio, todayFill, todayTrack, 410, "today");
            AssertBarShare(anthropic.Card, anthropic.Card.WeekBarRatio, weekFill, weekTrack, 410, "week");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheCompactWindowKeepsEqualAmountsAtEqualPhysicalWidthsAcrossCardsAndPeriods()
    {
        foreach (var language in new[] { UiLanguage.Japanese, UiLanguage.English })
        {
            var viewModel = await ViewModel(CrossScaleBoard());
            var window = new CompactQuotaWindow(viewModel);
            try
            {
                window.Show();
                window.UpdateLayout();
                viewModel.Language = language;
                window.UpdateLayout();
                var entries = Cards(window.FindControl<ItemsControl>("CompactTokScaleCards")!, viewModel.TokScaleCards);
                var anthropic = entries.Single(entry => entry.Card.ProviderKey == "anthropic");
                var openai = entries.Single(entry => entry.Card.ProviderKey == "openai");
                var (todayFill, todayTrack, _, _) = BarRows(anthropic.Border, anthropic.Card);
                var (_, _, weekFill, weekTrack) = BarRows(openai.Border, openai.Card);
                Assert.Equal(8_000, anthropic.Card.TodayTokens);
                Assert.Equal(8_000, openai.Card.WeekTokens);
                Assert.Equal(todayFill.Bounds.Width, weekFill.Bounds.Width, Tolerance);
                Assert.Equal(todayFill.TranslatePoint(default, anthropic.Border)!.Value.X,
                    weekFill.TranslatePoint(default, openai.Border)!.Value.X, Tolerance);
                Assert.Equal(todayTrack.Bounds.Width, weekTrack.Bounds.Width, Tolerance);
            }
            finally { window.Close(); }
        }
    }

    // ---------- rendered dashboard specimens ----------

    /// <summary>
    /// Captures six dashboard specimens from the production MainWindow XAML template and
    /// the real TokScale aggregation path: Japanese/Dark and English/Light at 1100px and
    /// 420px, plus an all-zero board and a 7-day/today inversion. These are Skia-headless
    /// layout captures, not OS compositor screenshots: their frame and text metrics are
    /// production Avalonia templates and fonts, but the host is a test session.
    /// </summary>
    [AvaloniaFact]
    public async Task RenderTheDashboardBarScenariosToPng()
    {
        var output = Environment.GetEnvironmentVariable("QUOTASIGHT_UI_PNG_DIR");
        if (string.IsNullOrWhiteSpace(output)) return;
        if (!Path.IsPathFullyQualified(output)) throw new ArgumentException("QUOTASIGHT_UI_PNG_DIR must be an absolute scratch path.", nameof(output));

        Directory.CreateDirectory(output);
        var scenarios = new (string Name, Func<ScriptedDualReadPort> Port, UiLanguage Language, int Width)[]
        {
            ("dashboard-ja-dark-1100", Board, UiLanguage.Japanese, 1100),
            ("dashboard-en-light-420", Board, UiLanguage.English, 420),
            ("dashboard-ja-dark-zero-420", ZeroBoard, UiLanguage.Japanese, 420),
            ("dashboard-en-light-inversion-1100", InvertedBoard, UiLanguage.English, 1100),
            ("dashboard-ja-dark-420", Board, UiLanguage.Japanese, 420),
            ("dashboard-en-light-1100", Board, UiLanguage.English, 1100),
        };

        foreach (var scenario in scenarios)
        {
            var window = await MainWindow(scenario.Port());
            try
            {
                window.ViewModel.Language = scenario.Language;
                // The UI has no user setting dependency in this rendered fixture; themes
                // are selected directly on the existing production window for the capture.
                window.RequestedThemeVariant = scenario.Name.Contains("ja-dark", StringComparison.Ordinal)
                    ? Avalonia.Styling.ThemeVariant.Dark
                    : Avalonia.Styling.ThemeVariant.Light;
                window.Width = scenario.Width;
                window.UpdateLayout();

                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                var path = Path.Combine(output, scenario.Name + ".png");
                using var stream = File.Create(path);
                frame!.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                Assert.True(new FileInfo(path).Length > 0, $"The rendered frame at '{path}' is empty.");
            }
            finally { window.Close(); }
        }

        foreach (var (language, dark, width) in new[]
        {
            (UiLanguage.Japanese, true, 420),
            (UiLanguage.Japanese, true, 1100),
            (UiLanguage.English, false, 420),
            (UiLanguage.English, false, 1100),
        })
        {
            var viewModel = await ViewModel(Board());
            var window = new CompactQuotaWindow(viewModel);
            try
            {
                window.Show();
                viewModel.Language = language;
                window.RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
                window.MaxWidth = 1100;
                window.Width = width;
                window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                var name = $"compact-{(language == UiLanguage.Japanese ? "ja-dark" : "en-light")}-{width}.png";
                using var stream = File.Create(Path.Combine(output, name));
                frame!.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            finally { window.Close(); }
        }
    }

    // ---------- helpers ----------

    private static void AssertBarShare(
        TokScaleUsageCard card, double expectedRatio, Border fill, Panel track, int width, string period)
    {
        var trackWidth = track.Bounds.Width;
        Assert.True(trackWidth > 20, $"'{card.ProviderKey}' {period} row has no measured track at {width}px.");
        var share = fill.Bounds.Width / trackWidth;
        Assert.True(Math.Abs(share - expectedRatio) <= 0.02,
            $"'{card.ProviderKey}' {period} bar fills {share:0.###} of its track at {width}px, but {expectedRatio:0.###} is the observed share of the common top.");
    }

    private static void AssertPeriodLabelsPresent(Border border, bool japanese = false)
    {
        var bars = FindBarsArea(border)!;
        var labels = bars.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.FontSize <= 11)
            .Select(text => text.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();
        Assert.Contains(japanese ? "今日" : "Today", labels);
        Assert.Contains(japanese ? "7日" : "7 days", labels);
        // Two labels only: the bars are identified by position and label, never by color alone.
        Assert.Equal(2, labels.Count);
    }

    private static Panel? FindBarsArea(Border card) =>
        card.GetVisualDescendants().OfType<Panel>()
            .FirstOrDefault(panel => panel.IsEffectivelyVisible
                && AutomationProperties.GetName(panel) is { Length: > 0 } name
                && (name.Contains("tokens", StringComparison.OrdinalIgnoreCase) || name.Contains("トークン", StringComparison.Ordinal)));

    private static (Border Today, Panel TodayTrack, Border Week, Panel WeekTrack) BarRows(Border card, TokScaleUsageCard cardModel)
    {
        var bars = FindBarsArea(card)
            ?? throw new InvalidOperationException($"'{cardModel.ProviderKey}' has no visible bar rows.");
        var fills = bars.GetVisualDescendants().OfType<Panel>()
            .Where(panel => panel.Classes.Contains("tokscale-bar"))
            .OrderBy(panel => panel.TranslatePoint(default, card)!.Value.Y)
            .ToList();
        Assert.True(fills.Count == 2, $"'{cardModel.ProviderKey}' draws {fills.Count} bar rows; expected today and week.");

        // The drawn amount is the fill's own child, not the measuring panel.
        Border FillOf(Panel row) => row.GetVisualDescendants().OfType<Border>().Single();

        return (FillOf(fills[0]), fills[0], FillOf(fills[1]), fills[1]);
    }

    private static IReadOnlyList<(TokScaleUsageCard Card, Border Border)> Cards(ItemsControl control, IReadOnlyList<TokScaleUsageCard> cards)
    {
        var borders = control.ItemsPanelRoot!.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("tokscale"))
            .Where(border => border.IsEffectivelyVisible)
            .ToList();
        Assert.Equal(cards.Count, borders.Count);
        return [.. cards.Select(card =>
        {
            var border = borders.Single(candidate =>
                candidate.GetVisualDescendants().OfType<TextBlock>()
                    .Any(text => text.FontWeight == Avalonia.Media.FontWeight.SemiBold && text.Text == card.DisplayName));
            return (card, border);
        })];
    }

    /// <summary>The mixed board: positive cards, a combination row, a zero observation and an unobserved provider.</summary>
    private static ScriptedDualReadPort Board()
    {
        var port = new ScriptedDualReadPort();
        port.Breakdown.Enqueue(() => Breakdown(
            Part("openai", 200),
            Part("anthropic", 4_000),
            Part("nous", 0),
            Part("deepseek,opencode_go", 800)));
        port.Breakdown.Enqueue(() => Breakdown(
            Part("openai", 6_000),
            Part("anthropic", 8_000),
            Part("deepseek,opencode_go", 2_400)));
        return port;
    }

    private static ScriptedDualReadPort CrossScaleBoard()
    {
        var port = new ScriptedDualReadPort();
        port.Breakdown.Enqueue(() => Breakdown(Part("openai", 8_000), Part("anthropic", 8_000), Part("nous", 0)));
        port.Breakdown.Enqueue(() => Breakdown(Part("openai", 8_000), Part("anthropic", 4_000)));
        return port;
    }

    private static ScriptedDualReadPort ZeroBoard()
    {
        var port = new ScriptedDualReadPort();
        port.Breakdown.Enqueue(() => Breakdown(Part("openai", 0), Part("nous", 0)));
        port.Breakdown.Enqueue(() => Breakdown(Part("openai", 0)));
        return port;
    }

    private static ScriptedDualReadPort InvertedBoard()
    {
        var port = new ScriptedDualReadPort();
        port.Breakdown.Enqueue(() => Breakdown(Part("openai", 9_000), Part("anthropic", 2_000)));
        port.Breakdown.Enqueue(() => Breakdown(Part("openai", 5_000), Part("anthropic", 4_000)));
        return port;
    }

    private static async Task<MainWindow> MainWindow(ScriptedDualReadPort port, double width = 1100)
    {
        var viewModel = await ViewModel(port);
        var window = new QuotaSight.UI.MainWindow(viewModel) { Width = width, Height = 740 };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static async Task<MainViewModel> ViewModel(ScriptedDualReadPort port)
    {
        var viewModel = new MainViewModel(
            timeProvider: port.Clock,
            uiDispatcher: new ImmediateUiDispatcher(),
            settingsStore: TemporarySettingsStore(),
            usageReadPort: port,
            localTimeZone: TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"));
        await viewModel.InitializeAsync();
        await viewModel.TokScaleLoadTask;
        foreach (var id in new[] { "openai", "anthropic", "nous", "xai_oauth", "deepseek", "opencode_go" })
        {
            await viewModel.AddTokScaleProviderAsync(id);
        }

        return viewModel;
    }

    private static AppSettingsStore TemporarySettingsStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "quotasight-tests", Guid.NewGuid().ToString("N"));
        return new AppSettingsStore(root);
    }

    private static UsageBreakdown Breakdown(params UsagePart[] parts) =>
        new(Today, Today, parts,
            [new UsageCoverage(Today, "col-a", parts.Sum(part => part.Total), parts.Sum(part => part.Total))],
            [new UsageCollectorReceipt("col-a", 1, Now.AddHours(-1))]);

    private static UsagePart Part(string provider, long total) =>
        new("claude", provider, "", total, 0, 0, 0, 0, total);

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

            return Task.FromResult(next());
        }
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan amount) => current += amount;
    }
}

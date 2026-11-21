using System.Globalization;
using Avalonia.Layout;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using QuotaSight.Application;
using QuotaSight.Core;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class QuotaCardLayoutTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-20T12:00:00+09:00");

    private static QuotaRowViewModel Row(ProviderKind provider, string account, string metric, QuotaWindowKind kind, TimeSpan duration, decimal used) =>
        QuotaPresentationFormatter.Format(new QuotaSnapshot(provider, account, metric,
            new(kind, Now.AddHours(-1), Now.Add(duration), duration, Now.AddHours(1)), used, 100, null, "%",
            Now, Now, QuotaSource.Official, QuotaConfidence.Reported, Now.AddHours(2)), Now);

    [AvaloniaFact]
    public void Provider_header_keeps_long_name_and_account_primary_and_bounds_state_separately()
    {
        var longName = new ProviderCardViewModel(ProviderKind.Copilot, "GitHub Copilot Business", "Acme Engineering",
            "#73D6B1", "Connected · organization-wide reference data is delayed pending refresh", false,
            [Row(ProviderKind.Copilot, "Acme Engineering", "AI credits", QuotaWindowKind.Monthly, TimeSpan.FromDays(30), 38)]);
        var twoWindows = new ProviderCardViewModel(ProviderKind.Claude, "Claude Max", "alice", "#73D6B1", "Connected", false,
            [Row(ProviderKind.Claude, "alice", "5h", QuotaWindowKind.Rolling, TimeSpan.FromHours(5), 38),
             Row(ProviderKind.Claude, "alice", "Messages", QuotaWindowKind.Weekly, TimeSpan.FromDays(7), 71)]);
        var fourWindows = new ProviderCardViewModel(ProviderKind.OpenCode, "OpenCode Go", "dev", "#73D6B1", "Connected", false,
            [Row(ProviderKind.OpenCode, "dev", "5h", QuotaWindowKind.Rolling, TimeSpan.FromHours(5), 12),
             Row(ProviderKind.OpenCode, "dev", "Requests", QuotaWindowKind.Daily, TimeSpan.FromDays(1), 28),
             Row(ProviderKind.OpenCode, "dev", "Requests", QuotaWindowKind.Weekly, TimeSpan.FromDays(7), 60),
             Row(ProviderKind.OpenCode, "dev", "Requests", QuotaWindowKind.Monthly, TimeSpan.FromDays(30), 88)]);
        using var viewModel = new MainViewModel(new FixedCardSource([longName, twoWindows, fourWindows]));
        var window = new MainWindow(viewModel) { Width = 1100, Height = 740 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var text = window.GetVisualDescendants().OfType<TextBlock>().Where(item => item.IsEffectivelyVisible && item.Bounds.Width > 0).ToArray();
            var title = Assert.Single(text, item => item.Text == longName.Name && item.DataContext is ProviderCardViewModel card && card.Name == longName.Name);
            var account = Assert.Single(text, item => item.Text == longName.Account && item.DataContext is ProviderCardViewModel card && card.Name == longName.Name);
            // Mock plan A: provider name in the top row, account on the card's bottom-right foot,
            // and the long connection state is not rendered as tile text (it lives in tooltips/a11y).
            // Mock plan A: the freshness label was shortened so the name no longer needs to wrap;
            // CharacterEllipsis stays only as an overflow guard.
            Assert.True(title.Bounds.Height < title.FontSize * 2, $"Provider title wrapped: {title.Bounds.Height:0.#}px tall.");
            Assert.True(title.Bounds.Width >= 120, $"Provider title was squeezed to {title.Bounds.Width:0.#}px.");
            Assert.True(account.Bounds.Width >= 60, $"Provider account was squeezed to {account.Bounds.Width:0.#}px.");
            Assert.True(title.Bounds.Height < 60, $"Provider title stacked one character per line: {title.Bounds.Height:0.#}px tall.");
            Assert.DoesNotContain(text, item => item.Text == longName.StateText && item.DataContext is ProviderCardViewModel);

            var providerCards = window.GetVisualDescendants().OfType<Border>()
                .Where(border => border.DataContext is ProviderCardViewModel && border.Classes.Contains("tile") && border.IsEffectivelyVisible)
                .ToArray();
            Assert.Equal(3, providerCards.Length);
            var twoWindowCard = Assert.Single(providerCards, border => ((ProviderCardViewModel)border.DataContext!).Name == twoWindows.Name);
            var fourWindowCard = Assert.Single(providerCards, border => ((ProviderCardViewModel)border.DataContext!).Name == fourWindows.Name);
            // Mock plan A stretches every card to the shared 160px tile height, so rows stay even.
            Assert.Equal(160, twoWindowCard.Bounds.Height);
            Assert.Equal(160, fourWindowCard.Bounds.Height);
            var twoWindowsRowTexts = twoWindowCard.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.DataContext is QuotaRowViewModel && text.IsEffectivelyVisible && text.Bounds.Width > 0).ToArray();
            Assert.DoesNotContain(twoWindowsRowTexts, text => text.IsVisible && text.Text == ((QuotaRowViewModel)text.DataContext!).StatusText);
            Assert.DoesNotContain(twoWindowsRowTexts, text => text.IsVisible && text.Text == ((QuotaRowViewModel)text.DataContext!).PercentText);
            // Mock plan A shows a small relative reset under the value; the long absolute reset
            // sentence (ResetText) stays out of the tile (ProgressLabel/tooltip keeps it).
            Assert.DoesNotContain(twoWindowsRowTexts, text => text.IsVisible && text.Text == ((QuotaRowViewModel)text.DataContext!).ResetText);
        }
        finally
        {
            window.Close();
        }
    }
    [AvaloniaFact]
    public void Dashboard_tile_line_fits_worst_case_texts_without_ellipsis()
    {
        var monthlyJp = QuotaPresentationFormatter.Format(Snap(ProviderKind.OpenCode, QuotaWindowKind.Monthly, TimeSpan.FromDays(30), Now.AddDays(10), 64), Now, UiLanguage.Japanese);
        var rollingJp = QuotaPresentationFormatter.Format(Snap(ProviderKind.OpenCode, QuotaWindowKind.Rolling, TimeSpan.FromHours(5), Now.AddHours(1).AddMinutes(12), 38), Now, UiLanguage.Japanese);
        var unknownResetEn = QuotaPresentationFormatter.Format(Snap(ProviderKind.Claude, QuotaWindowKind.Monthly, TimeSpan.FromDays(30), null, 64), Now);
        var staleEn = QuotaPresentationFormatter.Format(Snap(ProviderKind.Claude, QuotaWindowKind.Rolling, TimeSpan.FromHours(5), Now.AddHours(1).AddMinutes(12), 38) with { Fetched = Now.AddHours(-5), FreshUntil = Now.AddHours(-4) }, Now);
        var resetPendingEn = QuotaPresentationFormatter.Format(Snap(ProviderKind.Claude, QuotaWindowKind.Monthly, TimeSpan.FromDays(30), Now.AddMinutes(-30), 64) with { Fetched = Now.AddHours(-1) }, Now);
        var exhaustedEn = QuotaPresentationFormatter.Format(Snap(ProviderKind.Claude, QuotaWindowKind.Monthly, TimeSpan.FromDays(30), Now.AddDays(10), 100), Now);
        var unknownEn = QuotaPresentationFormatter.Format(Snap(ProviderKind.Claude, QuotaWindowKind.Monthly, TimeSpan.FromDays(30), Now.AddDays(10), null), Now);
        var twoWindowEn = QuotaPresentationFormatter.Format(Snap(ProviderKind.Claude, QuotaWindowKind.Rolling, TimeSpan.FromHours(5), Now.AddHours(1).AddMinutes(12), 38), Now);
        var worstCase = new ProviderCardViewModel(ProviderKind.OpenCode, "OpenCode Go", "dev", "#73D6B1", "Connected", false,
            [monthlyJp, rollingJp, unknownResetEn, staleEn, resetPendingEn, exhaustedEn, unknownEn]);
        var twoWindow = new ProviderCardViewModel(ProviderKind.Claude, "Claude Pro", "personal", "#73D6B1", "Connected", false, [twoWindowEn, rollingJp]);
        using var viewModel = new MainViewModel(new FixedCardSource([worstCase, twoWindow]));
        var window = new MainWindow(viewModel) { Width = 1100, Height = 740 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var tiles = window.GetVisualDescendants().OfType<Border>()
                .Where(border => border.DataContext is ProviderCardViewModel && border.Classes.Contains("tile")).ToArray();
            Assert.Equal(2, tiles.Length);
            // Mock plan A: every card in the grid shares the mock's fixed 160px tile height.
            Assert.All(tiles, tile => Assert.Equal(160, tile.Bounds.Height));
            // Mock plan A ring size floor: 86px on the dashboard.
            var rings = window.GetVisualDescendants().OfType<QuotaRingControl>().Where(ring => ring.IsEffectivelyVisible).ToArray();
            Assert.Equal(2, rings.Length);
            Assert.All(rings, ring => Assert.True(ring.Bounds.Width >= 86, $"Ring is {ring.Bounds.Width:0.#}px; mock plan A needs at least 86px."));
            var factBlocks = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(block => block.IsEffectivelyVisible && block.Bounds.Width > 0 && block.DataContext is QuotaRowViewModel row && (
                    block.Text == row.WindowName ||
                    (row.FactValueText.Length > 0 && block.Text == row.FactValueText) ||
                    (row.FactResetText.Length > 0 && block.Text == row.FactResetText) ||
                    (row.PillText.Length > 0 && block.Text == row.PillText)))
                .ToArray();
            Assert.True(factBlocks.Length >= (worstCase.Windows.Count + 2) * 2,
                $"Expected window label plus value/reset/pill text for every slot, found only {factBlocks.Length}.");
            Assert.DoesNotContain(factBlocks, block => block.TextTrimming != TextTrimming.None);
            foreach (var block in factBlocks)
            {
                AssertFits(block, "dashboard tile fact");
                AssertInsideCard(block, "dashboard tile fact");
            }
            var stackedValues = factBlocks.Where(block => block.FontSize >= 16).ToArray();
            var inlineValues = factBlocks.Where(block => block.FontSize == 12 && block.FontWeight == FontWeight.Bold).ToArray();
            Assert.NotEmpty(stackedValues);
            Assert.NotEmpty(inlineValues);
            Assert.All(stackedValues, block => Assert.True(block.Bounds.Height < block.FontSize * 2.2,
                $"Value '{block.Text}' wrapped onto multiple lines; one line per slot is required."));
            Assert.All(inlineValues, block => Assert.True(block.Bounds.Height < block.FontSize * 2.2,
                $"Value '{block.Text}' wrapped onto multiple lines; one line per slot is required."));
            var inlineGroups = inlineValues.GroupBy(block => block.DataContext).ToArray();
            Assert.Equal(worstCase.Windows.Count, inlineGroups.Length);
        }
        finally { window.Close(); }
    }


    private static QuotaSnapshot Snap(ProviderKind provider, QuotaWindowKind kind, TimeSpan duration, DateTimeOffset? resetAt, decimal? used) =>
        new(provider, "dev", kind.ToString(), new(kind, Now.AddHours(-1), Now.Add(duration), duration, resetAt), used, 100, null, "%", Now, Now, QuotaSource.Official, QuotaConfidence.Reported, Now.AddHours(2));

    private static void AssertInsideCard(TextBlock block, string context)
    {
        var card = block.GetVisualAncestors().OfType<Border>().First(border => border.DataContext is ProviderCardViewModel && border.Classes.Contains("tile"));
        var right = block.TranslatePoint(new Point(block.Bounds.Width, 0), card)!.Value.X;
        var innerRight = card.Bounds.Width - card.Padding.Right - card.BorderThickness.Right;
        Assert.True(right <= innerRight - 8,
            $"{context}: '{block.Text}' ends {right:0.#}px into the card but the card's inner edge is at {innerRight:0.#}px; the badge/text can clip at the card boundary in real rendering.");
    }

    private static void AssertFits(TextBlock block, string context, double metricMargin = 1.0)
    {
        var typeface = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight);
        var formatted = new FormattedText(block.Text ?? string.Empty, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, block.FontSize, Brushes.Black);
        var textWidth = formatted.WidthIncludingTrailingWhitespace + block.Padding.Left + block.Padding.Right;
        // metricMargin > 1 adds headroom for desktop font metrics that run wider than the
        // headless font (verified against Xvfb screenshots).
        Assert.True(block.Bounds.Width >= textWidth * metricMargin - 0.5,
            $"{context}: '{block.Text}' renders {textWidth:0.#}px wide (+{metricMargin - 1:0.#} margin) but was arranged in {block.Bounds.Width:0.#}px; the line would be cut with an ellipsis.");
    }

    [Fact]
    public void Compact_quota_row_summarizes_remaining_and_relative_reset_while_retaining_full_details()
    {
        var snapshot = new QuotaSnapshot(ProviderKind.Claude, "alice", "5h",
            new(QuotaWindowKind.Rolling, Now.AddHours(-1), Now.AddHours(4), TimeSpan.FromHours(5), Now.AddHours(1).AddMinutes(12)),
            38, 100, null, "%", Now, Now, QuotaSource.Official, QuotaConfidence.Reported, Now.AddHours(2));
        var row = QuotaPresentationFormatter.Format(snapshot, Now);

        Assert.Equal("62%", row.CompactValueText);
        Assert.Equal("Rolling 62%", row.CompactPrimaryText);
        Assert.Equal("Rolling 62% · in 1h 12m", row.CompactTileText);
        Assert.Equal(string.Empty, row.CompactStateBadge);
        Assert.Contains("38% used", row.PercentText);
        Assert.Contains("Normal", row.StatusText, StringComparison.Ordinal);
        Assert.Contains("Resets", row.ProgressLabel);
        Assert.Contains("Official", row.ProgressLabel);
        Assert.Contains("Updated just now", row.ProgressLabel);
    }

    [Fact]
    public void Compact_state_badges_use_single_short_words_for_exceptions_only()
    {
        var snapshot = new QuotaSnapshot(ProviderKind.Claude, "alice", "5h",
            new(QuotaWindowKind.Rolling, Now.AddHours(-1), Now.AddHours(4), TimeSpan.FromHours(5), Now.AddHours(1).AddMinutes(12)),
            38, 100, null, "%", Now, Now, QuotaSource.Official, QuotaConfidence.Reported, Now.AddHours(2));

        var stale = QuotaPresentationFormatter.Format(snapshot with { Fetched = Now.AddHours(-5), FreshUntil = Now.AddHours(-4) }, Now);
        Assert.Equal("Stale", stale.CompactStateBadge);
        Assert.False(stale.IsCompactBadgeWarning);
        Assert.Contains("Last observed", stale.PercentText);
        Assert.Contains("Normal", stale.StatusText, StringComparison.Ordinal);

        var unknown = QuotaPresentationFormatter.Format(snapshot with { Used = null, Limit = null }, Now);
        Assert.Equal("Unknown", unknown.CompactStateBadge);
        Assert.Equal("—", unknown.CompactValueText);
        Assert.False(unknown.IsCompactBadgeWarning);

        var over = QuotaPresentationFormatter.Format(snapshot with { Used = 112 }, Now);
        Assert.Equal("Over", over.CompactStateBadge);
        Assert.True(over.IsCompactBadgeWarning);

        var exhausted = QuotaPresentationFormatter.Format(snapshot with { Used = 100 }, Now);
        Assert.Equal("Exhausted", exhausted.CompactStateBadge);
        Assert.True(exhausted.IsCompactBadgeWarning);

        var resetPending = QuotaPresentationFormatter.Format(snapshot with
        {
            Window = snapshot.Window with { ResetAt = Now.AddMinutes(-30) },
            Fetched = Now.AddHours(-1)
        }, Now);
        Assert.Equal("Pending", resetPending.CompactStateBadge);
        Assert.True(resetPending.IsCompactBadgeWarning);

        var japanese = QuotaPresentationFormatter.Format(snapshot, Now, UiLanguage.Japanese);
        Assert.Equal("ローリング 62% · あと1時間", japanese.CompactTileText);
        var staleJapanese = QuotaPresentationFormatter.Format(snapshot with { Fetched = Now.AddHours(-5), FreshUntil = Now.AddHours(-4) }, Now, UiLanguage.Japanese);
        Assert.Equal("古い", staleJapanese.CompactStateBadge);
        Assert.Equal("不明", (unknown with { CurrentLanguage = UiLanguage.Japanese }).CompactStateBadge);
        Assert.Equal("未確認", (resetPending with { CurrentLanguage = UiLanguage.Japanese }).CompactStateBadge);
        Assert.Equal("上限超過", (over with { CurrentLanguage = UiLanguage.Japanese }).CompactStateBadge);
        Assert.Equal("上限到達", (exhausted with { CurrentLanguage = UiLanguage.Japanese }).CompactStateBadge);
    }

    [AvaloniaFact]
    public void Over_limit_ring_uses_patterned_flat_cap_over_limit_track_without_remaining_arc_and_distinct_marks()
    {
        var trackBrush = new SolidColorBrush(Color.Parse("#34443D"));
        var overLimitBrush = new SolidColorBrush(Color.Parse("#D0A4F2"));
        var staleBrush = new SolidColorBrush(Color.Parse("#88908B"));

        var over = new QuotaRingSegmentViewModel("Daily", QuotaWindowKind.Daily, 0m, UsageBand.OverLimit, false);
        var overPen = Assert.IsType<Pen>(QuotaRingControl.CreateTrackPen(over, trackBrush, overLimitBrush, staleBrush, 5d));
        Assert.Equal(overLimitBrush, overPen.Brush);
        Assert.Equal(PenLineCap.Flat, overPen.LineCap);
        Assert.NotNull(overPen.DashStyle);
        Assert.Equal(new[] { 2d, 2d }, overPen.DashStyle!.Dashes);
        Assert.Null(QuotaRingControl.GetDrawableRemaining(over));
        Assert.Equal(QuotaRingMark.OverLimit, QuotaRingControl.GetMark(over));

        var exhausted = new QuotaRingSegmentViewModel("Daily", QuotaWindowKind.Daily, 0m, UsageBand.Normal, false);
        var exhaustedPen = Assert.IsType<Pen>(QuotaRingControl.CreateTrackPen(exhausted, trackBrush, overLimitBrush, staleBrush, 5d));
        Assert.Equal(trackBrush, exhaustedPen.Brush);
        Assert.Null(exhaustedPen.DashStyle);
        Assert.Equal(PenLineCap.Round, exhaustedPen.LineCap);
        Assert.Null(QuotaRingControl.GetDrawableRemaining(exhausted));

        var resetPending = new QuotaRingSegmentViewModel("Monthly", QuotaWindowKind.Monthly, null, UsageBand.Normal, false);
        var staleOver = new QuotaRingSegmentViewModel("Weekly", QuotaWindowKind.Weekly, 0m, UsageBand.OverLimit, true);
        var unknownPen = Assert.IsType<Pen>(QuotaRingControl.CreateTrackPen(resetPending, trackBrush, overLimitBrush, staleBrush, 5d));
        var staleOverPen = Assert.IsType<Pen>(QuotaRingControl.CreateTrackPen(staleOver, trackBrush, overLimitBrush, staleBrush, 5d));
        Assert.Equal(trackBrush, unknownPen.Brush);
        Assert.Equal(staleBrush, staleOverPen.Brush);
        Assert.NotEqual(unknownPen.Brush, staleOverPen.Brush);
        Assert.Equal(PenLineCap.Round, unknownPen.LineCap);
        Assert.Equal(PenLineCap.Flat, staleOverPen.LineCap);
        Assert.NotNull(unknownPen.DashStyle);
        Assert.NotNull(staleOverPen.DashStyle);
    }

    [AvaloniaFact]
    public void Ring_render_paints_exhausted_neutral_track_patterned_over_limit_track_and_distinguishable_exceptions()
    {
        var frames = RenderRings(
            new QuotaRingSegmentViewModel("Daily", QuotaWindowKind.Daily, 0m, UsageBand.Normal, false),
            new QuotaRingSegmentViewModel("Daily", QuotaWindowKind.Daily, 0m, UsageBand.OverLimit, false),
            new QuotaRingSegmentViewModel("Monthly", QuotaWindowKind.Monthly, null, UsageBand.Normal, false),
            new QuotaRingSegmentViewModel("Weekly", QuotaWindowKind.Weekly, 0m, UsageBand.OverLimit, true));
        var exhausted = frames[0];
        var over = frames[1];
        var resetPending = frames[2];
        var staleOver = frames[3];

        Assert.True(Near(exhausted, TrackColor) > 100, "Exhausted ring must paint a full neutral continuous track.");
        Assert.Equal(0, Near(exhausted, NormalColor));
        Assert.Equal(0, Near(exhausted, OverColor));

        Assert.True(Near(over, OverColor) > 40, "Over-limit ring must paint the patterned OverLimit track.");
        Assert.Equal(0, Near(over, NormalColor));
        Assert.Equal(0, Near(over, TrackColor));

        Assert.True(Near(resetPending, TrackColor) > 20, "Reset-pending ring must paint a patterned neutral track.");
        Assert.Equal(0, Near(resetPending, NormalColor));
        Assert.Equal(0, Near(resetPending, OverColor));
        var exhaustedTrackPixels = Near(exhausted, TrackColor);
        var resetPendingTrackPixels = Near(resetPending, TrackColor);
        Assert.True(exhaustedTrackPixels > resetPendingTrackPixels * 1.2,
            $"Exhausted continuous track covered {exhaustedTrackPixels} pixels; reset-pending dash covered {resetPendingTrackPixels}.");

        Assert.True(Near(staleOver, StaleColor) > 20, "Stale-over ring must paint the stale brush.");
        Assert.Equal(0, Near(staleOver, OverColor));
        Assert.Equal(0, Near(staleOver, NormalColor));
        Assert.Equal(0, Near(resetPending, StaleColor));
    }

    [AvaloniaFact]
    public async Task Compact_widget_fits_three_complete_two_window_cards_and_keeps_copilot_disclaimer_without_ring_gutter()
    {
        var twoA = new ProviderCardViewModel(ProviderKind.Claude, "Claude Max", "alice", "#73D6B1", "Connected", false,
            [Row(ProviderKind.Claude, "alice", "5h", QuotaWindowKind.Rolling, TimeSpan.FromHours(5), 38),
             Row(ProviderKind.Claude, "alice", "Messages", QuotaWindowKind.Weekly, TimeSpan.FromDays(7), 71)]);
        var twoB = new ProviderCardViewModel(ProviderKind.ChatGpt, "ChatGPT Plus", "work", "#73D6B1", "Connected", false,
            [Row(ProviderKind.ChatGpt, "work", "5h", QuotaWindowKind.Rolling, TimeSpan.FromHours(5), 22),
             Row(ProviderKind.ChatGpt, "work", "Messages", QuotaWindowKind.Weekly, TimeSpan.FromDays(7), 45)]);
        var twoC = new ProviderCardViewModel(ProviderKind.OpenCode, "OpenCode Go", "dev", "#73D6B1", "Connected", false,
            [Row(ProviderKind.OpenCode, "dev", "5h", QuotaWindowKind.Rolling, TimeSpan.FromHours(5), 88),
             Row(ProviderKind.OpenCode, "dev", "Requests", QuotaWindowKind.Daily, TimeSpan.FromDays(1), 72)]);
        var threeWindow = new ProviderCardViewModel(ProviderKind.OpenCode, "OpenCode Go", "dev", "#73D6B1", "Connected", false,
            [Row(ProviderKind.OpenCode, "dev", "5h", QuotaWindowKind.Rolling, TimeSpan.FromHours(5), 88),
             Row(ProviderKind.OpenCode, "dev", "Requests", QuotaWindowKind.Daily, TimeSpan.FromDays(1), 72),
             Row(ProviderKind.OpenCode, "dev", "Requests", QuotaWindowKind.Weekly, TimeSpan.FromDays(7), 40)]);
        var copilotSnapshot = new QuotaSnapshot(ProviderKind.Copilot, "corp", "AI credits",
            new QuotaWindow(QuotaWindowKind.Monthly, Now.AddDays(-10), Now.AddDays(20), TimeSpan.FromDays(30), Now.AddDays(20)),
            null, null, 83, "credits", Now, Now, QuotaSource.Delayed, QuotaConfidence.Reported,
            Now.AddHours(2), "GitHub Copilot Business", new CopilotUsageBreakdown(1240, 900, 340));
        var copilot = new ProviderCardViewModel(ProviderKind.Copilot, "GitHub Copilot Business", "corp",
            "#73D6B1", "Connected", false, [QuotaPresentationFormatter.Format(copilotSnapshot, Now)])
        {
            CurrentLanguage = UiLanguage.English
        };
        using var viewModel = new MainViewModel(new FixedCardSource([twoA, twoB, twoC, threeWindow, copilot]));
        await viewModel.SetCopilotReferenceAsync("1500");
        var compact = new CompactQuotaWindow(viewModel) { Width = 410, Height = 620 };
        try
        {
            compact.Show();
            compact.UpdateLayout();
            var scroll = Assert.Single(compact.GetVisualDescendants().OfType<ScrollViewer>());
            var cards = compact.GetVisualDescendants().OfType<Border>()
                .Where(border => border.DataContext is ProviderCardViewModel && border.Classes.Contains("compact-tile") && border.IsEffectivelyVisible)
                .ToArray();
            Assert.Equal(5, cards.Length);
            var twoWindowCards = cards.Where(border => border.DataContext is ProviderCardViewModel { Windows.Count: 2 }).ToArray();
            Assert.Equal(3, twoWindowCards.Length);
            // Mock plan A widget tiles share one small fixed height.
            // Fixed 112px was dropped: cards fit content with a 112px floor so no fact is clipped.
            Assert.All(twoWindowCards, border => Assert.InRange(border.Bounds.Height, 112, 140));
            var scrollTop = scroll.TranslatePoint(new Point(0, 0), compact)!.Value.Y;
            var scrollBottom = scrollTop + scroll.Bounds.Height;
            var fullyVisible = twoWindowCards.Count(border =>
            {
                var top = border.TranslatePoint(new Point(0, 0), compact)!.Value.Y;
                return top >= scrollTop && top + border.Bounds.Height <= scrollBottom;
            });
            Assert.True(fullyVisible >= 3, $"Expected at least three complete two-window cards in the 410×620 widget, found {fullyVisible}.");

            var widgetRings = compact.GetVisualDescendants().OfType<QuotaRingControl>()
                .Where(ring => ring.IsEffectivelyVisible && ring.Bounds.Width > 0).ToArray();
            // Mock plan A widget ring floor: 72px, drawn for every card (Copilot is dashed).
            Assert.Equal(cards.Length, widgetRings.Length);
            Assert.All(widgetRings, ring =>
            {
                Assert.True(ring.Bounds.Width >= 72, $"Widget ring is {ring.Bounds.Width:0.#}px; mock plan A needs at least 72px.");
                var tile = ring.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains("compact-tile"));
                Assert.True(ring.Bounds.Height <= tile.Bounds.Height, $"Widget ring {ring.Bounds.Height:0.#}px overflows the {tile.Bounds.Height:0.#}px card.");
            });
            foreach (var border in cards)
            {
                var card = (ProviderCardViewModel)border.DataContext!;
                var name = Assert.Single(border.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.DataContext is ProviderCardViewModel && text.Text == card.Name);
                // The name may trim with an ellipsis only as an overflow guard; short names must not be cut.
                Assert.True(name.Bounds.Width >= 60 && name.Bounds.Height < name.FontSize * 2,
                    $"{name.Text} wrapped or was cut to {name.Bounds.Width:0.#}x{name.Bounds.Height:0.#} in the widget.");
            }

            var copilotCard = Assert.Single(cards, border => ((ProviderCardViewModel)border.DataContext!).Provider == ProviderKind.Copilot);
            var disclaimer = Assert.Single(copilotCard.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == new UiCopy(UiLanguage.English).CopilotReferenceDisclaimer);
            Assert.True(disclaimer.IsVisible);
            Assert.Equal(TextWrapping.Wrap, disclaimer.TextWrapping);
            Assert.True(disclaimer.TextTrimming == TextTrimming.None,
                "The Copilot reference disclaimer must wrap, never trim to an ellipsis.");
            var disclaimerBottom = disclaimer.TranslatePoint(new Point(0, disclaimer.Bounds.Height), compact)!.Value.Y;
            var cardBottom = copilotCard.TranslatePoint(new Point(0, copilotCard.Bounds.Height), compact)!.Value.Y;
            Assert.True(disclaimerBottom <= cardBottom - 1,
                $"Copilot reference disclaimer bottom {disclaimerBottom:0.#} is clipped by its card bottom {cardBottom:0.#}.");
            var widgetFacts = compact.GetVisualDescendants().OfType<TextBlock>()
                .Where(block => block.IsEffectivelyVisible && block.Bounds.Width > 0 && block.GetVisualAncestors().OfType<Border>().Any(border => border.Classes.Contains("compact-tile") && border.Bounds.Width >= 300) && block.DataContext is QuotaRowViewModel row && (
                    block.Text == row.WindowName ||
                    (row.FactValueText.Length > 0 && block.Text == row.FactValueText) ||
                    (row.FactResetText.Length > 0 && block.Text == row.FactResetText) ||
                    (row.PillText.Length > 0 && block.Text == row.PillText)))
                .ToArray();
            Assert.NotEmpty(widgetFacts);
            Assert.DoesNotContain(widgetFacts, block => block.TextTrimming != TextTrimming.None);
            foreach (var block in widgetFacts)
            {
                // Star columns give the fact its panel width; text wider than that would be cut.
                var factPanel = Assert.IsAssignableFrom<Panel>(block.GetVisualAncestors().OfType<StackPanel>()
                    .Last(panel => panel.DataContext is QuotaRowViewModel));
                if (factPanel.Bounds.Width < 150) continue; // stale intermediate measure copy, not a visible state
                var typeface = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight);
                var formatted = new FormattedText(block.Text ?? string.Empty, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, block.FontSize, Brushes.Black);
                Assert.True(formatted.WidthIncludingTrailingWhitespace * 1.1 <= factPanel.Bounds.Width,
                    $"widget tile fact: '{block.Text}' needs {formatted.WidthIncludingTrailingWhitespace * 1.1:0.#}px (desktop font margin) but its fact column is only {factPanel.Bounds.Width:0.#}px wide.");
            }
            foreach (var tile in cards.Where(border => border.Bounds.Width >= 300))
            {
                var tileBottom = tile.TranslatePoint(new Point(0, tile.Bounds.Height), compact)!.Value.Y;
                foreach (var block in tile.GetVisualDescendants().OfType<TextBlock>()
                    .Where(text => text.IsEffectivelyVisible && text.Bounds.Width > 0 && text.DataContext is QuotaRowViewModel))
                {
                    var blockBottom = block.TranslatePoint(new Point(0, block.Bounds.Height), compact)!.Value.Y;
                    Assert.True(blockBottom <= tileBottom + 0.5,
                        $"Fact '{block.Text}' bottom {blockBottom:0.#} is cut by the card bottom {tileBottom:0.#}.");
                }
            }
            var copilotName = Assert.Single(copilotCard.GetVisualDescendants().OfType<TextBlock>(),
                text => text.DataContext is ProviderCardViewModel && text.Text == copilot.Name);
            Assert.True(copilotName.Bounds.Width > copilotCard.Bounds.Width / 4,
                $"Copilot card name only reached {copilotName.Bounds.Width:0.#}px of the {copilotCard.Bounds.Width:0.#}px card.");
        }
        finally
        {
            compact.Close();
        }
    }

    private static readonly Color TrackColor = Color.Parse("#394A41");
    private static readonly Color NormalColor = Color.Parse("#73D6B1");
    private static readonly Color OverColor = Color.Parse("#D0A4F2");
    private static readonly Color StaleColor = Color.Parse("#6E8BB8");

    private static List<List<Color>> RenderRings(params QuotaRingSegmentViewModel[] segments)
    {
        var panel = new StackPanel();
        foreach (var segment in segments)
        {
            panel.Children.Add(new QuotaRingControl
            {
                Width = 80,
                Height = 80,
                Segments = [segment],
                TrackBrush = new SolidColorBrush(TrackColor),
                NormalBrush = new SolidColorBrush(NormalColor),
                AttentionBrush = new SolidColorBrush(Color.Parse("#F0C66A")),
                DangerBrush = new SolidColorBrush(Color.Parse("#FF918A")),
                OverLimitBrush = new SolidColorBrush(OverColor),
                StaleBrush = new SolidColorBrush(StaleColor),
            });
        }
        var window = new Window { Width = 80, Height = 80 * segments.Length, Background = Brushes.Transparent, Content = panel };
        try
        {
            window.Show();
            window.UpdateLayout();
            WriteableBitmap? frame = null;
            for (var attempt = 0; attempt < 5 && frame is null; attempt++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            }
            Assert.NotNull(frame);
            var frames = new List<List<Color>>();
            using (var locked = frame.Lock())
            {
                var buffer = new byte[locked.RowBytes * locked.Size.Height];
                Marshal.Copy(locked.Address, buffer, 0, buffer.Length);
                var rgba = locked.Format == PixelFormat.Rgba8888;
                for (var band = 0; band < segments.Length; band++)
                {
                    var pixels = new List<Color>();
                    for (var y = band * 80; y < (band + 1) * 80; y++)
                    {
                        for (var x = 0; x < 80; x++)
                        {
                            var dx = x - 40 + 0.5;
                            var dy = y - band * 80 - 40 + 0.5;
                            var distance = Math.Sqrt(dx * dx + dy * dy);
                            if (distance < 26 || distance > 46) continue;
                            var offset = y * locked.RowBytes + x * 4;
                            var a = buffer[offset + 3];
                            var r = rgba ? buffer[offset] : buffer[offset + 2];
                            var g = buffer[offset + 1];
                            var b = rgba ? buffer[offset + 2] : buffer[offset];
                            pixels.Add(Color.FromArgb(a, r, g, b));
                        }
                    }
                    frames.Add(pixels);
                }
            }
            return frames;
        }
        finally
        {
            window.Close();
        }
    }

    private static int Near(List<Color> pixels, Color target) => pixels.Count(pixel =>
        pixel.A > 200 &&
        Math.Abs(pixel.R - target.R) + Math.Abs(pixel.G - target.G) + Math.Abs(pixel.B - target.B) < 60);

    private sealed class FixedCardSource(IReadOnlyList<ProviderCardViewModel> cards) : IDashboardSource
    {
        public bool IsDemo => false;
        public IReadOnlyList<ProviderCardViewModel> Load() => cards;
    }

    [AvaloniaFact]
    public void Ring_center_text_fits_inside_the_innermost_ring_or_is_hidden()
    {
        var main = new MainWindow(new MainViewModel(new DemoDashboardSource())) { Width = 1100, Height = 740 };
        var compact = new CompactQuotaWindow(new MainViewModel(new DemoDashboardSource()));
        try
        {
            main.Show(); compact.Show(); main.UpdateLayout(); compact.UpdateLayout();
            foreach (var window in new Window[] { main, compact })
            {
                var rings = window.GetVisualDescendants().OfType<QuotaRingControl>().Where(item => item.IsEffectivelyVisible).ToArray();
                Assert.NotEmpty(rings);
                foreach (var ring in rings)
                {
                    var count = Math.Min(3, ring.Segments.Count);
                    if (count == 0) continue;
                    var card = Assert.IsAssignableFrom<ProviderCardViewModel>(ring.GetVisualAncestors().OfType<Border>()
                        .First(border => border.DataContext is ProviderCardViewModel).DataContext);
                    var ringGrid = Assert.IsAssignableFrom<Panel>(ring.Parent);
                    Assert.True(ringGrid.IsVisible, "Mock plan A renders a ring for every card, dashed for the Copilot reference.");
                    var centerBlocks = ringGrid.GetVisualDescendants().OfType<TextBlock>()
                        .Where(block => block.IsEffectivelyVisible && block.Bounds.Width > 0 && (block.Text == card.RemainingCenterText || block.Text == card.RemainingCenterPeriodText))
                        .ToArray();
                    var hole = RingHole(ring.Bounds.Width, count);
                    if (hole < 24 || !card.HasKnownRemainingCenter)
                    {
                        // Not enough room, or no known remaining to show: the center must stay hidden.
                        Assert.Empty(centerBlocks);
                        continue;
                    }
                    Assert.NotEmpty(centerBlocks);
                    foreach (var block in centerBlocks)
                    {
                        var typeface = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight);
                        var formatted = new FormattedText(block.Text ?? string.Empty, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, block.FontSize, Brushes.Black);
                        var textWidth = formatted.WidthIncludingTrailingWhitespace;
                        Assert.True(textWidth <= hole - 3,
                            $"Ring center '{block.Text}' ({block.FontSize:0.#}px) renders {textWidth:0.#}px wide but the innermost ring hole is only {hole:0.#}px; the text overlaps the ring line.");
                    }
                }
            }
        }
        finally { main.Close(); compact.Close(); }
    }


    private static double RingHole(double side, int count)
    {
        var outerRadius = side / 2 - 3;
        var thickness = count == 1 ? Math.Clamp(side * 0.085, 4, 7) : Math.Clamp(side * 0.065, 3, 5);
        var gap = Math.Max(2, side * 0.035);
        var innerRadius = outerRadius - (count - 1) * (thickness + gap) - thickness;
        return innerRadius * 2;
    }
}

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using QuotaSight.UI.Tests.Support;
using QuotaSight.Core;
using QuotaSight.Infrastructure;
using QuotaSight.UI;
using Xunit;

public sealed class MockParityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-20T12:00:00+09:00");

    private static QuotaRowViewModel Sample(ProviderKind provider, string account, string metric,
        QuotaWindowKind kind, TimeSpan duration, decimal? remaining, DateTimeOffset fetched,
        DateTimeOffset resetAt, DateTimeOffset? freshUntil = null, string? displayName = null)
    {
        var used = remaining is { } value ? 100m - value : (decimal?)null;
        var snapshot = new QuotaSnapshot(provider, account, metric,
            new QuotaWindow(kind, fetched.AddDays(-1), fetched.Add(duration), duration, resetAt),
            used, remaining is null ? null : 100m, null, "requests", fetched, fetched,
            QuotaSource.Official, QuotaConfidence.Reported, freshUntil ?? fetched.AddHours(4), displayName ?? "");
        return QuotaPresentationFormatter.Format(snapshot, Now, UiLanguage.English);
    }

    private static IReadOnlyList<ProviderCardViewModel> FixtureLikeCards() =>
    [
        new(ProviderKind.Claude, "Claude Max", "alice", "#73D6B1", "Connected", false,
        [
            Sample(ProviderKind.Claude, "alice", "5h", QuotaWindowKind.Rolling, TimeSpan.FromHours(5), 62, Now, Now.AddHours(1).AddMinutes(12), displayName: "Claude"),
            Sample(ProviderKind.Claude, "alice", "Messages", QuotaWindowKind.Weekly, TimeSpan.FromDays(7), 29, Now, Now.AddDays(3))
        ]),
        new(ProviderKind.ChatGpt, "ChatGPT Plus", "work", "#73D6B1", "Connected", false,
        [
            Sample(ProviderKind.ChatGpt, "work", "5h", QuotaWindowKind.Rolling, TimeSpan.FromHours(5), 0, Now, Now.AddMinutes(22)),
            Sample(ProviderKind.ChatGpt, "work", "Messages", QuotaWindowKind.Weekly, TimeSpan.FromDays(7), 55, Now, Now.AddDays(2))
        ]),
        new(ProviderKind.OpenCode, "OpenCode Go", "dev", "#73D6B1", "Connected", false,
        [
            Sample(ProviderKind.OpenCode, "dev", "5h", QuotaWindowKind.Rolling, TimeSpan.FromHours(5), 88, Now, Now.AddHours(2)),
            Sample(ProviderKind.OpenCode, "dev", "Requests", QuotaWindowKind.Daily, TimeSpan.FromDays(1), 72, Now, Now.AddHours(9)),
            Sample(ProviderKind.OpenCode, "dev", "Requests", QuotaWindowKind.Weekly, TimeSpan.FromDays(7), 40, Now, Now.AddDays(2)),
            Sample(ProviderKind.OpenCode, "dev", "Requests", QuotaWindowKind.Monthly, TimeSpan.FromDays(30), 12, Now, Now.AddDays(10))
        ]),
        new(ProviderKind.Claude, "Claude Pro", "personal", "#73D6B1", "Stale · refresh failed", false,
        [
            Sample(ProviderKind.Claude, "personal", "5h", QuotaWindowKind.Rolling, TimeSpan.FromHours(5), 75, Now.AddHours(-3), Now.AddHours(1), Now.AddHours(-2)),
            Sample(ProviderKind.Claude, "personal", "Messages", QuotaWindowKind.Weekly, TimeSpan.FromDays(7), null, Now.AddHours(-3), Now.AddDays(3), Now.AddHours(-2)),
            Sample(ProviderKind.Claude, "personal", "Messages", QuotaWindowKind.Monthly, TimeSpan.FromDays(30), 64, Now.AddHours(-1), Now.AddMinutes(-30), Now.AddHours(2))
        ]),
        CopilotCard()
    ];

    private static ProviderCardViewModel CopilotCard()
    {
        var snapshot = new QuotaSnapshot(ProviderKind.Copilot, "corp", "AI credits",
            new QuotaWindow(QuotaWindowKind.Monthly, Now.AddDays(-10), Now.AddDays(20), TimeSpan.FromDays(30), Now.AddDays(20)),
            null, null, 83, "credits", Now, Now, QuotaSource.Delayed, QuotaConfidence.Reported,
            Now.AddHours(2), "GitHub Copilot Business", new CopilotUsageBreakdown(1240, 900, 340));
        return new ProviderCardViewModel(ProviderKind.Copilot, "GitHub Copilot Business", "corp",
            "#73D6B1", "Connected · reference values", false, [QuotaPresentationFormatter.Format(snapshot, Now)]);
    }

    private sealed class CardSource(IReadOnlyList<ProviderCardViewModel> cards) : IDashboardSource
    {
        public bool IsDemo => false;
        public IReadOnlyList<ProviderCardViewModel> Load() => cards;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [AvaloniaFact]
    public void AutoColumnsPanel_places_every_child_in_equal_cells_inside_the_panel()
    {
        var panel = new AutoColumnsPanel { MinTileWidth = 300, Gap = 12 };
        for (var index = 0; index < 5; index++) panel.Children.Add(new Border { Height = 160, Background = Brushes.Gray });
        var window = new Window { Width = 920, Height = 740, Content = panel }; // 920px panel ≈ the dashboard content width (1100 − sidebar − margins) → two 300px+ columns
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.Equal(2, panel.ComputeColumns(panel.Bounds.Width));
            Assert.True(panel.DesiredSize.Height >= 3 * 160 + 2 * 12, $"Panel height {panel.DesiredSize.Height:0.#} only fits one row.");
            foreach (var child in panel.Children)
            {
                Assert.True(child.IsEffectivelyVisible);
                Assert.True(child.Bounds.Width >= 300, $"Child was squeezed to {child.Bounds.Width:0.#}px.");
                Assert.True(child.Bounds.Height > 0, "A card was given no row and did not render.");
                var bottom = child.TranslatePoint(new Point(0, child.Bounds.Height), panel)!.Value.Y;
                Assert.True(bottom <= panel.Bounds.Height + 0.5,
                    $"Child bottom {bottom:0.#} is outside the panel height {panel.Bounds.Height:0.#}; the card would disappear.");
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Dashboard_renders_one_tile_per_card_and_matches_the_summary_chip_count()
    {
        var cards = FixtureLikeCards();
        using var viewModel = new MainViewModel(new CardSource(cards));
        var window = new MainWindow(viewModel) { Width = 1100, Height = 740 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var board = Assert.Single(window.GetVisualDescendants().OfType<Border>(), border => border.Classes.Contains("board"));
            var tiles = window.GetVisualDescendants().OfType<Border>()
                .Where(border => border.DataContext is ProviderCardViewModel && border.Classes.Contains("tile") && border.IsEffectivelyVisible && border.Bounds.Width > 0)
                .ToArray();
            Assert.Equal(cards.Count, tiles.Length);
            // 集計チップの件数 == 描画されたカード数
            var chipText = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible && text.Bounds.Width > 0)
                .Select(text => text.Text ?? string.Empty)
                .FirstOrDefault(text => text.StartsWith($"{cards.Count}") || text == new UiCopy(UiLanguage.English).SummaryAccounts(cards.Count));
            Assert.NotNull(chipText);
            foreach (var tile in tiles)
            {
                var top = tile.TranslatePoint(new Point(0, 0), board)!.Value.Y;
                var bottom = tile.TranslatePoint(new Point(0, tile.Bounds.Height), board)!.Value.Y;
                Assert.True(top >= 0 && bottom <= board.Bounds.Height - 1,
                    $"Card '{((ProviderCardViewModel)tile.DataContext!).Name}' sits outside the board (top {top:0.#}, bottom {bottom:0.#}, board height {board.Bounds.Height:0.#}).");
                Assert.All(tile.GetVisualDescendants().OfType<QuotaRingControl>().Where(ring => ring.Bounds.Width > 0),
                    ring => Assert.True(ring.Bounds.Width >= 86, $"Ring is {ring.Bounds.Width:0.#}px; mock plan A needs at least 86px."));
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Fixture_launch_with_dark_argument_renders_a_dark_dashboard()
    {
        // Same boot path as the real `--main --dark` fixture launch (QuotaRingsFixture.Create).
        var (window, viewModel) = QuotaRingsHost.Create(["--main", "--dark"]);
        try
        {
            window.Show();
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            Assert.NotNull(frame);
            var background = PixelAt(frame!, 30, 400);
            var backgroundLuminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
            Assert.True(backgroundLuminance < 0.35,
                $"--dark requested but the dashboard background sampled {background} is too bright.");
            var tiles = window.GetVisualDescendants().OfType<Border>()
                .Where(border => border.DataContext is ProviderCardViewModel && border.Classes.Contains("tile") && border.IsEffectivelyVisible && border.Bounds.Width > 0)
                .ToArray();
            Assert.Equal(5, tiles.Length);
            var cardPixel = PixelAt(frame!, 120, 400);
            var cardLuminance = (0.299 * cardPixel.R + 0.587 * cardPixel.G + 0.114 * cardPixel.B) / 255.0;
            Assert.True(cardLuminance < 0.5, $"--dark requested but the card surface sampled {cardPixel} is too bright.");
        }
        finally { window.Close(); viewModel.Dispose(); }
    }

    [AvaloniaFact]
    public void Dashboard_content_scrolls_to_reveal_the_last_card()
    {
        var cards = FixtureLikeCards();
        using var viewModel = new MainViewModel(new CardSource(cards));
        var window = new MainWindow(viewModel) { Width = 1100, Height = 740 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var scroll = Assert.IsAssignableFrom<ScrollViewer>(window.GetVisualDescendants().FirstOrDefault(control => control.Name == "ContentScroll"));
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height + 50,
                $"Content height {scroll.Extent.Height:0.#} fits the viewport {scroll.Viewport.Height:0.#}; the Copilot card would be clipped with no way to scroll.");
            scroll.ScrollToEnd();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var copilot = Assert.Single(window.GetVisualDescendants().OfType<Border>(),
                border => border.DataContext is ProviderCardViewModel model && model.Provider == ProviderKind.Copilot && border.Classes.Contains("tile") && border.IsEffectivelyVisible && border.Bounds.Width > 0);
            var bottom = copilot.TranslatePoint(new Point(0, copilot.Bounds.Height), scroll)!.Value.Y;
            Assert.True(bottom <= scroll.Viewport.Height + 1,
                $"After scrolling to the end the Copilot card bottom {bottom:0.#} is still below the viewport {scroll.Viewport.Height:0.#}.");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Language_switch_with_the_fixture_clock_keeps_the_same_state_distribution_as_english()
    {
        var cards = FixtureLikeCards();
        using var viewModel = new MainViewModel(new CardSource(cards), timeProvider: new FixedClock(Now));
        var englishPills = viewModel.Cards.SelectMany(card => card.Windows)
            .Select(row => row.PillText).ToArray();
        viewModel.Language = UiLanguage.Japanese;
        var japaneseRows = viewModel.Cards.SelectMany(card => card.Windows).ToArray();
        for (var index = 0; index < englishPills.Length; index++)
        {
            Assert.False(japaneseRows[index].IsStale && !IsStalePill(englishPills[index]),
                $"Japanese re-localization made row {index} stale ('{japaneseRows[index].PillText}') while English shows '{englishPills[index]}'.");
        }
        Assert.Equal(englishPills.Length, japaneseRows.Length);
        Assert.Contains("更新", japaneseRows[0].HeaderFreshnessText);
        Assert.DoesNotContain("データが古い", japaneseRows[0].HeaderFreshnessText);
    }

    private static bool IsStalePill(string pill) => pill is "Stale" or "Unknown";

    [AvaloniaFact]
    public void Japanese_ui_shows_no_fixed_english_copy_on_fixture_cards()
    {
        foreach (var args in new[] { new[] { "--main", "--dark", "--ja" }, new[] { "--widget", "--light", "--ja" } })
        {
            var (window, viewModel) = QuotaRingsHost.Create(args);
            try
            {
                window.Show();
                window.UpdateLayout();
                var leaks = window.GetVisualDescendants().OfType<TextBlock>()
                    .Where(text => text.IsEffectivelyVisible && text.Bounds.Width > 0 && text.DataContext is ProviderCardViewModel or QuotaRowViewModel)
                    .Select(text => text.Text ?? string.Empty)
                    .Where(text => text.Contains("Reference only", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("not remaining", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("Updated", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                Assert.Empty(leaks);
            }
            finally { window.Close(); viewModel.Dispose(); }
        }
    }

    [AvaloniaFact]
    public void Copilot_reference_notes_fit_inside_their_cards()
    {
        foreach (var args in new[] { new[] { "--main", "--dark" }, new[] { "--widget", "--light" }, new[] { "--main", "--dark", "--ja" }, new[] { "--widget", "--light", "--ja" } })
        {
            var (window, viewModel) = QuotaRingsHost.Create(args);
            try
            {
                window.Show();
                window.UpdateLayout();
                var copilot = Assert.Single(window.GetVisualDescendants().OfType<Border>(),
                    border => border.DataContext is ProviderCardViewModel model && model.Provider == ProviderKind.Copilot
                        && (border.Classes.Contains("tile") || border.Classes.Contains("compact-tile")) && border.IsEffectivelyVisible && border.Bounds.Width > 100);
                var tileBottom = copilot.TranslatePoint(new Point(0, copilot.Bounds.Height), window)!.Value.Y;
                foreach (var block in copilot.GetVisualDescendants().OfType<TextBlock>()
                    .Where(text => text.IsEffectivelyVisible && text.Bounds.Width > 0))
                {
                    var blockBottom = block.TranslatePoint(new Point(0, block.Bounds.Height), window)!.Value.Y;
                    Assert.True(blockBottom <= tileBottom + 0.5,
                        $"Copilot card note '{block.Text}' bottom {blockBottom:0.#} is cut by the card bottom {tileBottom:0.#}.");
                }
            }
            finally { window.Close(); viewModel.Dispose(); }
        }
    }

    [AvaloniaFact]
    public void Japanese_fixture_render_has_no_untranslated_alphabet_copy()
    {
        // Proper nouns and units are intentionally left in Latin script; everything else must be Japanese.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Claude Max", "Claude Pro", "ChatGPT Plus", "OpenCode Go", "GitHub Copilot Business",
            "5h", "⌘", "G", "C", "O", "M"
        };
        foreach (var args in new[] { new[] { "--main", "--dark", "--ja" }, new[] { "--widget", "--light", "--ja" } })
        {
            var (window, viewModel) = QuotaRingsHost.Create(args);
            foreach (var card in viewModel.Cards)
            {
                allowed.Add(card.Name);
                allowed.Add(card.Account);
            }
            try
            {
                window.Show();
                window.UpdateLayout();
                var leaks = window.GetVisualDescendants().OfType<TextBlock>()
                    .Where(text => text.IsEffectivelyVisible && text.Bounds.Width > 0 && text.DataContext is ProviderCardViewModel or QuotaRowViewModel)
                    .Select(text => text.Text ?? string.Empty)
                    .Where(text => text.Length > 2 && !allowed.Contains(text))
                    .Where(text =>
                    {
                        var letters = text.Count(char.IsLetter);
                        var latin = text.Count(char.IsAsciiLetter);
                        return letters > 0 && latin >= 3 && latin >= letters * 0.8;
                    })
                    .ToArray();
                Assert.Empty(leaks);
            }
            finally { window.Close(); viewModel.Dispose(); }
        }
    }


    [AvaloniaFact]
    public void Widget_facts_wrap_into_two_columns_for_three_window_cards()
    {
        var cards = FixtureLikeCards();
        using var viewModel = new MainViewModel(new CardSource(cards));
        var compact = new CompactQuotaWindow(viewModel) { Width = 410, Height = 620 };
        try
        {
            compact.Show();
            compact.UpdateLayout();
            var card = Assert.Single(compact.GetVisualDescendants().OfType<Border>(),
                border => border.DataContext is ProviderCardViewModel model && model.Name == "OpenCode Go" && model.Windows.Count == 4 && border.Classes.Contains("compact-tile"));
            var valueBlocks = card.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible && text.Bounds.Width > 0 && text.DataContext is QuotaRowViewModel row && text.Text == row.FactValueText)
                .ToArray();
            Assert.True(valueBlocks.Length >= 3, $"Expected facts for every window, found {valueBlocks.Length}.");
            var columns = valueBlocks.Select(block => block.TranslatePoint(new Point(0, 0), card)!.Value.X).Distinct().Count();
            Assert.True(columns >= 2, $"Window facts stacked vertically ({columns} column); the mock puts them in two columns.");
            var ring = Assert.Single(card.GetVisualDescendants().OfType<QuotaRingControl>(),
                item => item.IsEffectivelyVisible && item.Bounds.Width > 0);
            Assert.True(ring.Bounds.Width >= 72, $"Widget ring is {ring.Bounds.Width:0.#}px.");
            var cardBottom = card.TranslatePoint(new Point(0, card.Bounds.Height), compact)!.Value.Y;
            var ringBottom = ring.TranslatePoint(new Point(0, ring.Bounds.Height), compact)!.Value.Y;
            Assert.True(ringBottom <= cardBottom + 0.5, $"Widget ring bottom {ringBottom:0.#} exceeds card bottom {cardBottom:0.#} (card height {card.Bounds.Height:0.#}).");
            // No fact text may be cut by the card bottom (4-window cards included).
            var tileBottom = card.TranslatePoint(new Point(0, card.Bounds.Height), compact)!.Value.Y;
            foreach (var block in card.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible && text.Bounds.Width > 0 && text.DataContext is QuotaRowViewModel))
            {
                var blockBottom = block.TranslatePoint(new Point(0, block.Bounds.Height), compact)!.Value.Y;
                Assert.True(blockBottom <= tileBottom + 0.5,
                    $"Fact '{block.Text}' bottom {blockBottom:0.#} is cut by the card bottom {tileBottom:0.#}.");
            }
        }
        finally { compact.Close(); }
    }

    private static Color PixelAt(WriteableBitmap frame, int x, int y)
    {
        using var locked = frame.Lock();
        var row = y * locked.RowBytes;
        var offset = row + x * 4;
        unsafe
        {
            var pointer = (byte*)locked.Address;
            var b = pointer[offset];
            var g = pointer[offset + 1];
            var r = pointer[offset + 2];
            return Color.FromRgb(r, g, b);
        }
    }
}

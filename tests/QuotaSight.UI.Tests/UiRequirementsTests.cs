using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Headless.XUnit;
using Avalonia.Media;

using Avalonia.VisualTree;

using QuotaSight.Core;
using QuotaSight.Application;
using QuotaSight.Infrastructure;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class UiRequirementsTests
{
    [Fact]
    public void Ring_projection_keeps_unknown_distinct_from_zero_and_zero_from_over_limit()
    {
        var now = DateTimeOffset.Parse("2026-11-20T12:00:00+09:00");
        QuotaRowViewModel Row(QuotaWindowKind kind, TimeSpan duration, decimal? used) =>
            QuotaPresentationFormatter.Format(new QuotaSnapshot(ProviderKind.ChatGpt, "work", kind.ToString(), new(kind, now, now.Add(duration), duration), null, null, used, "%", now, now, QuotaSource.Experimental, QuotaConfidence.Reported, now.AddHours(1)), now, UiLanguage.Japanese);
        var card = new ProviderCardViewModel(ProviderKind.ChatGpt, "ChatGPT", "work", "#73D6B1", "Ready", false,
        [Row(QuotaWindowKind.Custom, TimeSpan.FromDays(10), 112), Row(QuotaWindowKind.Daily, TimeSpan.FromDays(1), 100), Row(QuotaWindowKind.Rolling, TimeSpan.FromHours(5), null)]);

        Assert.Null(card.RingSegments.Single(segment => segment.WindowKind == QuotaWindowKind.Rolling).RemainingPercent);
        Assert.Equal(0m, card.RingSegments.Single(segment => segment.WindowKind == QuotaWindowKind.Daily).RemainingPercent);
        Assert.Contains("上限に到達", card.Windows.Single(row => row.WindowKind == QuotaWindowKind.Daily).StatusText);
        var over = Assert.Single(card.RingSegments, segment => segment.WindowKind == QuotaWindowKind.Custom);
        Assert.Equal(0m, over.RemainingPercent);
        Assert.Equal(UsageBand.OverLimit, over.Band);
        Assert.Equal("0%", card.RemainingCenterText);
        Assert.Equal("カスタム", card.RemainingCenterPeriodText);

        var unknownOnly = card with { Windows = [Row(QuotaWindowKind.Rolling, TimeSpan.FromHours(5), null)] };
        Assert.Equal("—", unknownOnly.RemainingCenterText);
        Assert.Empty(unknownOnly.RemainingCenterPeriodText);
    }

    [Fact]
    public void Ring_marking_keeps_zero_unknown_stale_and_over_limit_visually_distinct()
    {
        QuotaRingSegmentViewModel Segment(decimal? remaining, UsageBand band, bool stale = false) => new("week", QuotaWindowKind.Weekly, remaining, band, stale);
        Assert.Equal(QuotaRingMark.Zero, QuotaRingControl.GetMark(Segment(0, UsageBand.Normal)));
        Assert.Equal(QuotaRingMark.Unknown, QuotaRingControl.GetMark(Segment(null, UsageBand.Attention)));
        Assert.Equal(QuotaRingMark.Stale, QuotaRingControl.GetMark(Segment(20, UsageBand.Normal, stale: true)));
        Assert.Equal(QuotaRingMark.OverLimit, QuotaRingControl.GetMark(Segment(0, UsageBand.OverLimit)));
        Assert.Equal(QuotaRingMark.StaleOverLimit, QuotaRingControl.GetMark(Segment(0, UsageBand.OverLimit, stale: true)));
        Assert.Equal(QuotaRingMark.Remaining, QuotaRingControl.GetMark(Segment(75, UsageBand.Normal)));
    }

    [Fact]
    public void Stale_and_reset_pending_windows_never_supply_the_current_ring_center()
    {
        var now = DateTimeOffset.Parse("2026-11-20T12:00:00+09:00");
        var current = QuotaPresentationFormatter.Format(new QuotaSnapshot(ProviderKind.Claude, "alice", "5h", new(QuotaWindowKind.Rolling, now.AddHours(-1), now.AddHours(4), TimeSpan.FromHours(5)), 40, 100, null, "%", now, now, QuotaSource.Official, QuotaConfidence.Reported, now.AddHours(2)), now);
        var stale = QuotaPresentationFormatter.Format(new QuotaSnapshot(ProviderKind.Claude, "alice", "Messages", new(QuotaWindowKind.Weekly, now.AddDays(-2), now.AddDays(5), TimeSpan.FromDays(7)), 90, 100, null, "%", now.AddHours(-3), now.AddHours(-3), QuotaSource.Official, QuotaConfidence.Reported, now.AddHours(-2)), now);
        var resetAt = now.AddMinutes(-30);
        var pending = QuotaPresentationFormatter.Format(new QuotaSnapshot(ProviderKind.Claude, "alice", "Messages", new(QuotaWindowKind.Monthly, now.AddDays(-2), now.AddDays(28), TimeSpan.FromDays(30), resetAt), 64, 100, null, "%", now.AddHours(-1), now.AddHours(-1), QuotaSource.Manual, QuotaConfidence.Manual, now.AddHours(2)), now);
        var card = new ProviderCardViewModel(ProviderKind.Claude, "Claude Pro", "alice", "#73D6B1", "Ready", false, [stale, pending, current]);

        Assert.True(stale.IsStale);
        Assert.Contains("Last observed", stale.PercentText);
        Assert.True(stale.IsStalePercentVisible);
        Assert.False(stale.IsCurrentPercentVisible);
        Assert.True(pending.IsResetPending);
        Assert.Contains("Last observed before reset", pending.PercentText);
        Assert.Contains("36% remaining", pending.PercentText);
        Assert.Equal(10m, card.RingSegments.Single(segment => segment.WindowKind == QuotaWindowKind.Weekly).RemainingPercent);
        Assert.True(card.RingSegments.Single(segment => segment.WindowKind == QuotaWindowKind.Weekly).IsStale);
        Assert.Null(card.RingSegments.Single(segment => segment.WindowKind == QuotaWindowKind.Monthly).RemainingPercent);
        Assert.Equal("60%", card.RemainingCenterText);
        Assert.Equal("Rolling", card.RemainingCenterPeriodText);

        var noCurrentRing = card with { Windows = [stale, pending] };
        Assert.False(noCurrentRing.HasKnownRemainingCenter);
        Assert.Equal("—", noCurrentRing.RemainingCenterText);
        Assert.Empty(noCurrentRing.RemainingCenterPeriodText);
    }

    [Fact]
    public void Over_limit_wins_zero_remaining_ties_for_the_constrained_middle_ring()
    {
        var now = DateTimeOffset.Parse("2026-11-20T12:00:00+09:00");
        QuotaRowViewModel Row(QuotaWindowKind kind, TimeSpan duration, decimal used) =>
            QuotaPresentationFormatter.Format(new QuotaSnapshot(ProviderKind.ChatGpt, "work", kind.ToString(), new(kind, now, now.Add(duration), duration), used, 100, null, "%", now, now, QuotaSource.Official, QuotaConfidence.Reported, now.AddHours(1)), now);
        var card = new ProviderCardViewModel(ProviderKind.ChatGpt, "ChatGPT", "work", "#73D6B1", "Ready", false,
        [
            Row(QuotaWindowKind.Monthly, TimeSpan.FromDays(30), 60),
            Row(QuotaWindowKind.Daily, TimeSpan.FromDays(1), 100),
            Row(QuotaWindowKind.Weekly, TimeSpan.FromDays(7), 112),
            Row(QuotaWindowKind.Custom, TimeSpan.FromDays(10), 80),
            Row(QuotaWindowKind.Rolling, TimeSpan.FromHours(5), 40)
        ]);

        Assert.Equal([QuotaWindowKind.Rolling, QuotaWindowKind.Weekly, QuotaWindowKind.Monthly], card.RingSegments.Select(segment => segment.WindowKind));
        Assert.Equal(2, card.AdditionalRingWindowCount);
        Assert.Equal(UsageBand.OverLimit, card.RingSegments[1].Band);
        Assert.Equal("0%", card.RemainingCenterText);
        Assert.Equal("Weekly", card.RemainingCenterPeriodText);
    }

    [AvaloniaFact]
    public void Exposed_quota_text_has_a_per_window_accessible_name()
    {
        var window = new MainWindow(new MainViewModel(new DemoDashboardSource()));
        try
        {
            window.Show();
            window.UpdateLayout();
            var row = window.ViewModel.Cards.SelectMany(card => card.Windows).First(row => row.IsPercentVisible);
            var value = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && text.Bounds.Width > 0),
                text => ReferenceEquals(text.DataContext, row) && text.Text == row.FactValueText);
            Assert.Equal(row.ProgressLabel, AutomationProperties.GetName(value));
        }
        finally { window.Close(); window.ViewModel.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Stale_values_are_muted_and_readable_in_both_dashboard_and_compact_rows()
    {
        var now = DateTimeOffset.UtcNow;
        using var vm = new MainViewModel(new EmptyDashboardSource());
        var snapshot = new QuotaSnapshot(ProviderKind.Claude, "alice", "5h",
            new(QuotaWindowKind.Rolling, now.AddHours(-3), now.AddHours(2), TimeSpan.FromHours(5)),
            38, 100, null, "%", now.AddHours(-3), now.AddHours(-3), QuotaSource.Official,
            QuotaConfidence.Reported, now.AddHours(-2));
        await vm.ApplyProviderSnapshotsAsync([snapshot]);
        var row = Assert.Single(Assert.Single(vm.Cards).Windows);
        Assert.True(row.IsStale);
        Assert.Contains("Last observed", row.PercentText);

        var main = new MainWindow(vm);
        var compact = new CompactQuotaWindow(vm);
        try
        {
            main.Show(); compact.Show(); main.UpdateLayout(); compact.UpdateLayout();
            var app = Assert.IsType<App>(Avalonia.Application.Current);
            var key = main.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? "Dark" : "Light";
            var theme = (ResourceDictionary)app.Resources.ThemeDictionaries.First(pair => pair.Key.ToString() == key).Value!;
            var expectedMuted = ((ISolidColorBrush)theme["TextMutedBrush"]!).Color;
            foreach (var window in new Window[] { main, compact })
            {
                // Mock plan A: the stale state is carried by the muted pill, the value stays primary-colored.
                var value = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && text.Bounds.Width > 0), text =>
                    ReferenceEquals(text.DataContext, row) && text.Text == row.FactValueText);
                Assert.Equal(row.ProgressLabel, AutomationProperties.GetName(value));
                var pill = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && text.Bounds.Width > 0), text =>
                    ReferenceEquals(text.DataContext, row) && text.Text == row.PillText);
                Assert.False(string.IsNullOrWhiteSpace(pill.Text));
                Assert.Equal(expectedMuted, Assert.IsAssignableFrom<ISolidColorBrush>(pill.Foreground).Color);
                Assert.Equal(row.PillText, AutomationProperties.GetName(pill));
                Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), text =>
                    text.IsVisible && ReferenceEquals(text.DataContext, row) && text.Text == row.StatusText && row.IsPercentVisible);
            }
        }
        finally { main.Close(); compact.Close(); }
    }

    [Fact]
    public void Past_reset_keeps_the_observed_balance_and_exposes_pending_reset_in_accessible_copy()
    {
        var now = DateTimeOffset.Parse("2026-11-20T12:00:00+09:00");
        var resetAt = now.AddMinutes(-30);
        var snapshot = new QuotaSnapshot(ProviderKind.Claude, "alice", "weekly", new(QuotaWindowKind.Weekly, now.AddDays(-2), now.AddDays(5), TimeSpan.FromDays(7), resetAt), 64, 100, null, "%", now.AddHours(-1), now.AddHours(-1), QuotaSource.Manual, QuotaConfidence.Manual, now.AddHours(1));
        var row = QuotaPresentationFormatter.Format(snapshot, now, UiLanguage.Japanese);

        Assert.True(row.IsResetPending);
        Assert.Contains("未確認", row.ResetPendingText);
        Assert.Contains("残り 36%", row.PercentText);
        Assert.Contains("リセット後の最新値は未確認", row.ProgressLabel);
    }

    [AvaloniaFact]
    public void Dashboard_and_compact_cards_render_rings_at_their_responsive_sizes()
    {
        var dashboard = new MainWindow(new MainViewModel(new DemoDashboardSource()));
        var narrowDashboard = new MainWindow(new MainViewModel(new DemoDashboardSource())) { Width = 760 };
        var compact = new CompactQuotaWindow(new MainViewModel(new DemoDashboardSource()));
        try
        {
            dashboard.Show();
            narrowDashboard.Show();
            compact.Show();
            dashboard.UpdateLayout();
            narrowDashboard.UpdateLayout();
            compact.UpdateLayout();

            var dashboardRings = dashboard.GetVisualDescendants().OfType<QuotaRingControl>().Where(ring => ring.IsEffectivelyVisible).ToArray();
            var compactRings = compact.GetVisualDescendants().OfType<QuotaRingControl>().Where(ring => ring.IsEffectivelyVisible).ToArray();
            // Mock plan A restores the large ring (about 86px dashboard / 72px widget); the demo
            // Copilot card has no manual reference configured, so its ring stays hidden there.
            Assert.Equal(3, dashboardRings.Length);
            Assert.Equal(3, compactRings.Length);
            Assert.All(dashboardRings, ring => Assert.Equal(86, ring.Bounds.Width));
            Assert.All(compactRings, ring => Assert.Equal(72, ring.Bounds.Width));
            Assert.All(dashboardRings.Where(ring => ring.DataContext is not ProviderCardViewModel { Provider: ProviderKind.Copilot }), ring => Assert.NotEmpty(ring.Segments));
            Assert.All(dashboardRings, ring => Assert.Equal(86, ring.Bounds.Width));

            var tileBorders = dashboard.GetVisualDescendants().OfType<Border>()
                .Where(border => border.DataContext is ProviderCardViewModel && border.Classes.Contains("tile") && border.IsEffectivelyVisible)
                .ToArray();
            var columns = tileBorders.Select(border => border.TranslatePoint(new Point(0, 0), dashboard)!.Value.X).Distinct().Count();
            Assert.Equal(4, tileBorders.Length);
            Assert.All(tileBorders, border => Assert.Equal(160, border.Bounds.Height));
            // Cards are at least 300px wide (mock plan A); with the unchanged 220px sidebar that
            // yields two equal columns at 1100px, three columns from about 1250px.
            Assert.True(columns >= 2, $"Expected at least two dashboard columns at 1100px; rendered {columns}.");
            Assert.All(tileBorders, border => Assert.True(border.Bounds.Width >= 300, $"Card width {border.Bounds.Width:0.#} is below the 300px mock minimum."));

            Assert.Equal(760, narrowDashboard.Bounds.Width);
            var narrowTileBorders = narrowDashboard.GetVisualDescendants().OfType<Border>()
                .Where(border => border.DataContext is ProviderCardViewModel && border.Classes.Contains("tile") && border.IsEffectivelyVisible)
                .ToArray();
            var narrowColumns = narrowTileBorders.Select(border => border.TranslatePoint(new Point(0, 0), narrowDashboard)!.Value.X).Distinct().Count();
            Assert.InRange(narrowColumns, 1, 2);
            Assert.All(narrowTileBorders, border => Assert.True(border.TranslatePoint(new Point(border.Bounds.Width, 0), narrowDashboard)!.Value.X <= narrowDashboard.Bounds.Width));

            var compactCards = compact.GetVisualDescendants().OfType<Border>()
                .Where(border => border.DataContext is ProviderCardViewModel && border.Classes.Contains("compact-tile") && border.IsEffectivelyVisible)
                .ToArray();
            Assert.Equal(4, compactCards.Length);
            Assert.All(compactCards.Where(border => border.DataContext is ProviderCardViewModel { IsCopilotReferenceVisible: false }), border => Assert.InRange(border.Bounds.Height, 112, 140));
            Assert.All(compactCards, border => Assert.True(border.TranslatePoint(new Point(border.Bounds.Width, 0), compact)!.Value.X <= compact.Bounds.Width));
        }
        finally
        {
            dashboard.Close();
            dashboard.ViewModel.Dispose();
            narrowDashboard.Close();
            narrowDashboard.ViewModel.Dispose();
            compact.Close();
            compact.ViewModel.Dispose();
        }
    }

    [Fact]
    public void Copilot_credit_and_reference_percentages_are_not_rendered_as_remaining_quota_rings()
    {
        var now = DateTimeOffset.Parse("2026-11-20T12:00:00+09:00");
        var snapshot = new QuotaSnapshot(ProviderKind.Copilot, "Acme", "AI credits", new(QuotaWindowKind.Monthly, now, now.AddDays(30), TimeSpan.FromDays(30), now.AddDays(30)), null, null, 83, "credits", now, now, QuotaSource.Experimental, QuotaConfidence.Estimated, now.AddHours(1));
        var row = QuotaPresentationFormatter.Format(snapshot, now, UiLanguage.Japanese);
        var card = new ProviderCardViewModel(ProviderKind.Copilot, "Copilot Business", "Acme", "#78A9FF", "接続済み", false, [row]);

        Assert.Empty(card.RingSegments);
        Assert.Contains("使用率 83%", card.Windows[0].PercentText);
        Assert.Contains("残量ではありません", card.Windows[0].PercentText);
    }

    [Fact]
    public void Quota_ring_summary_orders_short_windows_outside_and_centers_the_lowest_known_remaining_value()
    {
        var now = DateTimeOffset.Parse("2026-11-20T12:00:00+09:00");
        QuotaRowViewModel Row(QuotaWindowKind kind, TimeSpan duration, decimal used) =>
            QuotaPresentationFormatter.Format(new QuotaSnapshot(ProviderKind.Claude, "alice", kind.ToString(), new(kind, now, now.Add(duration), duration, now.Add(duration)), used, 100, null, "%", now, now, QuotaSource.Manual, QuotaConfidence.Manual, now.AddHours(1)), now, UiLanguage.Japanese);
        var card = new ProviderCardViewModel(ProviderKind.Claude, "Claude Max", "alice", "#73D6B1", "通常", false,
        [
            Row(QuotaWindowKind.Monthly, TimeSpan.FromDays(30), 88),
            Row(QuotaWindowKind.Rolling, TimeSpan.FromHours(5), 38),
            Row(QuotaWindowKind.Weekly, TimeSpan.FromDays(7), 71),
            Row(QuotaWindowKind.Daily, TimeSpan.FromDays(1), 20)
        ]);

        Assert.Equal([QuotaWindowKind.Rolling, QuotaWindowKind.Weekly, QuotaWindowKind.Monthly], card.RingSegments.Select(segment => segment.WindowKind));
        Assert.Equal(1, card.AdditionalRingWindowCount);
        Assert.Equal("+1 more windows outside rings", card.AdditionalRingWindowText);
        Assert.Equal("+1件はリング外に表示", (card with { CurrentLanguage = UiLanguage.Japanese }).AdditionalRingWindowText);
        Assert.Equal("12%", card.RemainingCenterText);
        Assert.Equal("月次", card.RemainingCenterPeriodText);
    }

    [AvaloniaFact]
    public async Task Codex_code_copy_uses_injected_clipboard_service()
    {
        var clipboard = new RecordingClipboard();
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource()), new UiProviderFacade(), clipboard);
        window.ViewModel.SetCodexResult(new(true, CodexAuthorizationState.AwaitingAuthorization, "waiting", new("DEVICE-123", new Uri("https://example.test"), DateTimeOffset.UtcNow.AddMinutes(5))));

        await window.CopyCodexCodeAsync();

        Assert.Equal("DEVICE-123", clipboard.LastText);
    }

    [AvaloniaFact]
    public async Task Codex_copy_button_click_copies_only_the_code_once_and_empty_or_missing_clipboard_is_safe()
    {
        var clipboard = new RecordingClipboard();
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource()), new UiProviderFacade(), clipboard);
        window.ViewModel.SetCodexResult(new(true, CodexAuthorizationState.AwaitingAuthorization, "waiting", new("DEVICE-456", new Uri("https://example.test"), DateTimeOffset.UtcNow.AddMinutes(5))));
        window.FindControl<Button>("CodexCopyButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(10);
        Assert.Equal(1, clipboard.CopyCount);
        Assert.Equal("DEVICE-456", clipboard.LastText);
        window.ViewModel.SetCodexResult(new(false, CodexAuthorizationState.Disconnected, "disconnected"));
        window.FindControl<Button>("CodexCopyButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(10);
        Assert.Equal(1, clipboard.CopyCount);
        window.Close();

        var noClipboardWindow = new MainWindow(new MainViewModel(new EmptyDashboardSource()), new UiProviderFacade());
        noClipboardWindow.ViewModel.SetCodexResult(new(false, CodexAuthorizationState.Disconnected, "disconnected"));
        var exception = Record.Exception(() => noClipboardWindow.FindControl<Button>("CodexCopyButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        Assert.Null(exception);
        noClipboardWindow.Close();
    }

    [AvaloniaFact]
    public void Application_theme_uses_quiet_mineral_surfaces_and_a_single_green_action_accent()
    {
        var window = new MainWindow(new MainViewModel(new DemoDashboardSource()));
        try
        {
            window.Show();
            UiSettings.ApplyTheme(ThemeMode.Light);
            window.UpdateLayout();
            AssertBrushColor(window.Background, "#F4F6F3");
            AssertBrushColor(window.FindControl<Border>("Sidebar")!.Background, "#EEF1ED");
            AssertBrushColor(window.FindControl<Border>("LocalFirstCard")!.Background, "#FFFFFF");
            Assert.Equal(14, window.FindControl<Border>("LocalFirstCard")!.CornerRadius.TopLeft);
            UiSettings.ApplyTheme(ThemeMode.Dark);
            window.UpdateLayout();
            AssertBrushColor(window.Background, "#101715");
            AssertBrushColor(window.FindControl<Border>("Sidebar")!.Background, "#151D1A");
        }
        finally { window.Close(); window.ViewModel.Dispose(); }
    }

    [AvaloniaFact]
    public void Page_header_is_single_and_sidebar_note_keeps_intrinsic_layout()
    {
        var window = new MainWindow(new MainViewModel(new DemoDashboardSource()));
        try
        {
            window.Show();
            window.UpdateLayout();
            var navigation = window.FindControl<DockPanel>("MainNavigation")!;
            var note = window.FindControl<Border>("LocalFirstCard")!;
            var sidebar = window.FindControl<Border>("Sidebar")!;
            Assert.False(navigation.LastChildFill);
            Assert.True(note.Bounds.Height < sidebar.Bounds.Height / 2);

            window.ViewModel.Navigate(AppPage.Settings);
            window.UpdateLayout();
            var headings = window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsVisible && text.Classes.Contains("title") && text.Text == window.ViewModel.CopyText.Settings).ToList();
            Assert.Single(headings);
            var header = window.FindControl<Grid>("DashboardHeader")!;
            Assert.Equal(window.ViewModel.CopyText.Settings, header.GetVisualDescendants().OfType<TextBlock>().First(text => text.Classes.Contains("title")).Text);
        }
        finally
        {
            window.Close();
            window.ViewModel.Dispose();
        }
    }

    [AvaloniaFact]
    public void Primary_action_buttons_use_matching_vector_icons_localized_tooltips_and_automation_names()
    {
        var main = new MainWindow(new MainViewModel(new EmptyDashboardSource()));
        var compact = new CompactQuotaWindow(new MainViewModel(new EmptyDashboardSource()));
        try
        {
            main.Show();
            compact.Show();
            main.UpdateLayout();
            compact.UpdateLayout();

            var mainRefresh = main.FindControl<Button>("RefreshButton")!;
            var compactRefresh = compact.FindControl<Button>("CompactRefreshButton")!;
            var addProvider = main.FindControl<Button>("AddProviderButton")!;

            Assert.IsType<PathIcon>(mainRefresh.Content);
            Assert.IsType<PathIcon>(compactRefresh.Content);
            Assert.IsType<PathIcon>(addProvider.Content);
            Assert.Equal(((PathIcon)mainRefresh.Content).Data?.ToString(), ((PathIcon)compactRefresh.Content).Data?.ToString());
            Assert.Equal(main.ViewModel.CopyText.Refresh, ToolTip.GetTip(mainRefresh));
            Assert.Equal(main.ViewModel.CopyText.Refresh, AutomationProperties.GetName(mainRefresh));
            Assert.Equal(compact.ViewModel.CopyText.Refresh, ToolTip.GetTip(compactRefresh));
            Assert.Equal(compact.ViewModel.CopyText.Refresh, AutomationProperties.GetName(compactRefresh));
            Assert.Equal(main.ViewModel.CopyText.AddProvider, ToolTip.GetTip(addProvider));
            Assert.Equal(main.ViewModel.CopyText.AddProvider, AutomationProperties.GetName(addProvider));

            main.ViewModel.Language = UiLanguage.Japanese;
            compact.ViewModel.Language = UiLanguage.Japanese;
            main.UpdateLayout();
            compact.UpdateLayout();

            Assert.Equal("更新", ToolTip.GetTip(mainRefresh));
            Assert.Equal("更新", AutomationProperties.GetName(mainRefresh));
            Assert.Equal("更新", ToolTip.GetTip(compactRefresh));
            Assert.Equal("更新", AutomationProperties.GetName(compactRefresh));
            Assert.Equal(main.ViewModel.CopyText.AddProvider, ToolTip.GetTip(addProvider));
            Assert.Equal(main.ViewModel.CopyText.AddProvider, AutomationProperties.GetName(addProvider));
        }
        finally
        {
            compact.Close();
            compact.ViewModel.Dispose();
            main.Close();
            main.ViewModel.Dispose();
        }
    }

    [AvaloniaFact]
    public void Icon_actions_keep_the_path_icon_readable_across_theme_and_interaction_states()
    {
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource())) { Width = 420 };
        window.ViewModel.Navigate(AppPage.Settings);
        try
        {
            window.Show();
            var refresh = window.FindControl<Button>("RefreshButton")!;
            var icon = Assert.IsType<PathIcon>(refresh.Content);

            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                UiSettings.ApplyTheme(theme);
                GetPseudoClasses(refresh).Remove(":pressed");
                window.UpdateLayout();
                AssertBrushColor(refresh.Background, "#00FFFFFF");
                AssertBrushColor(icon.Foreground, theme == ThemeMode.Light ? "#17211D" : "#F1F5F2");

                GetPseudoClasses(refresh).Add(":pointerover");
                window.UpdateLayout();
                var presenter = Assert.Single(refresh.GetVisualDescendants().OfType<ContentPresenter>(), part => part.Name == "PART_ContentPresenter");
                AssertBrushColor(presenter.Background, theme == ThemeMode.Light ? "#E5F1EC" : "#202F29");
                AssertBrushColor(presenter.BorderBrush, theme == ThemeMode.Light ? "#167C68" : "#73D6B1");
                AssertBrushColor(icon.Foreground, theme == ThemeMode.Light ? "#17211D" : "#F1F5F2");
                GetPseudoClasses(refresh).Remove(":pointerover");

                var navigation = window.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Dashboard");
                GetPseudoClasses(navigation).Add(":pointerover");
                window.UpdateLayout();
                var navPresenter = Assert.Single(navigation.GetVisualDescendants().OfType<ContentPresenter>(), part => part.Name == "PART_ContentPresenter");
                AssertBrushColor(navPresenter.Background, theme == ThemeMode.Light ? "#E5F1EC" : "#202F29");
                GetPseudoClasses(navigation).Remove(":pointerover");

                var input = window.FindControl<TextBox>("GithubClientIdBox")!;
                GetPseudoClasses(input).Add(":pointerover");
                window.UpdateLayout();
                var inputBorder = Assert.Single(input.GetVisualDescendants().OfType<Border>(), part => part.Name == "PART_BorderElement");
                Assert.NotEqual(Color.Parse(theme == ThemeMode.Light ? "#DCE3DE" : "#34443C"), ((ISolidColorBrush)inputBorder.BorderBrush!).Color);
                GetPseudoClasses(input).Remove(":pointerover");

                var combo = window.FindControl<ComboBox>("ThemeBox")!;
                GetPseudoClasses(combo).Add(":pointerover");
                window.UpdateLayout();
                var comboBorder = Assert.Single(combo.GetVisualDescendants().OfType<Border>(), part => part.Name == "Background");
                Assert.NotEqual(Color.Parse(theme == ThemeMode.Light ? "#DCE3DE" : "#34443C"), ((ISolidColorBrush)comboBorder.BorderBrush!).Color);
                GetPseudoClasses(combo).Remove(":pointerover");


                GetPseudoClasses(refresh).Add(":focus-visible");
                window.UpdateLayout();
                var focusedPresenter = Assert.Single(refresh.GetVisualDescendants().OfType<ContentPresenter>(), part => part.Name == "PART_ContentPresenter");
                AssertBrushColor(focusedPresenter.BorderBrush, theme == ThemeMode.Light ? "#167C68" : "#73D6B1");
                Assert.Equal(2, focusedPresenter.BorderThickness.Top);
                GetPseudoClasses(refresh).Remove(":focus-visible");

                GetPseudoClasses(refresh).Add(":pressed");
                window.UpdateLayout();
                var pressedPresenter = Assert.Single(refresh.GetVisualDescendants().OfType<ContentPresenter>(), part => part.Name == "PART_ContentPresenter");
                Assert.NotEqual(Color.Parse("#E5F1EC"), ((ISolidColorBrush)pressedPresenter.Background!).Color);
                AssertBrushColor(icon.Foreground, theme == ThemeMode.Light ? "#F4F6F3" : "#101715");
                GetPseudoClasses(refresh).Remove(":pressed");

                refresh.IsEnabled = false;
                window.UpdateLayout();
                Assert.Equal(0.5, refresh.Opacity);
                AssertBrushColor(icon.Foreground, theme == ThemeMode.Light ? "#17211D" : "#F1F5F2");
                refresh.IsEnabled = true;
            }
        }
        finally
        {
            window.Close();
            window.ViewModel.Dispose();
        }
    }

    [AvaloniaFact]
    public void Compact_width_keeps_header_and_add_provider_actions_from_overlapping_copy()
    {
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource())) { Width = 420 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var headerCopy = window.FindControl<StackPanel>("DashboardHeaderCopy")!;
            var refresh = window.FindControl<Button>("RefreshButton")!;
            var addProvider = window.FindControl<Button>("AddProviderButton")!;
            var usageHeading = window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == window.ViewModel.CopyText.UsageOverview);

            Assert.False(headerCopy.Bounds.Intersects(refresh.Bounds));
            Assert.False(usageHeading.Bounds.Intersects(addProvider.Bounds));
        }
        finally
        {
            window.Close();
            window.ViewModel.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Add_provider_flow_shows_selection_then_only_selected_provider_panel_and_can_close()
    {
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource()), new UiProviderFacade());
        window.Show();
        await window.InitializeAsync();
        var add = window.FindControl<Button>("AddProviderButton")!;
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(window.FindControl<ComboBox>("ProviderPicker")!.IsVisible);
        Assert.False(window.FindControl<Border>("ManualProviderPanel")!.IsVisible);

        var picker = window.FindControl<ComboBox>("ProviderPicker")!;
        var codex = window.ViewModel.ProviderChoices.Single(item => item.Choice == ProviderConnectionChoice.Codex);
        picker.SelectedItem = codex;
        picker.RaiseEvent(new SelectionChangedEventArgs(ComboBox.SelectionChangedEvent, new List<object?>(), new List<object?> { codex }));
        Assert.True(window.FindControl<Border>("CodexCard")!.IsVisible);
        Assert.False(window.FindControl<Border>("OpenCodePanel")!.IsVisible);
        Assert.True(window.FindControl<Button>("CloseProviderButton")!.IsVisible);

        window.ViewModel.CloseProviderFlow();
        Assert.False(window.FindControl<ComboBox>("ProviderPicker")!.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Reopening_provider_flow_shows_picker_without_previous_panel()
    {
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource()), new UiProviderFacade());
        window.Show();
        await window.InitializeAsync();
        var add = window.FindControl<Button>("AddProviderButton")!;
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var picker = window.FindControl<ComboBox>("ProviderPicker")!;
        var codex = window.ViewModel.ProviderChoices.Single(item => item.Choice == ProviderConnectionChoice.Codex);
        picker.SelectedItem = codex;
        picker.RaiseEvent(new SelectionChangedEventArgs(ComboBox.SelectionChangedEvent, new List<object?>(), new List<object?> { codex }));
        window.ViewModel.CloseProviderFlow();

        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(picker.IsVisible);
        Assert.False(window.FindControl<Border>("CodexCard")!.IsVisible);
        Assert.False(window.FindControl<Border>("ManualProviderPanel")!.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Real_provider_picker_selection_opens_chatgpt_and_same_provider_without_manual_raise_event()
    {
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource()), new UiProviderFacade());
        window.Show();
        await window.InitializeAsync();
        var add = window.FindControl<Button>("AddProviderButton")!;
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var picker = window.FindControl<ComboBox>("ProviderPicker")!;
        var chatGpt = window.ViewModel.ProviderChoices.Single(item => item.Choice == ProviderConnectionChoice.ChatGpt);

        picker.SelectedItem = chatGpt;
        Assert.True(window.FindControl<Border>("ManualProviderPanel")!.IsVisible);
        var manualProvider = Assert.IsType<LocalizedChoice<ProviderKind>>(window.FindControl<ComboBox>("ManualProviderBox")!.SelectedItem);
        Assert.Equal(ProviderKind.ChatGpt, manualProvider.Value);
        Assert.Equal("ChatGPT", manualProvider.DisplayName);
        picker.SelectedItem = null;
        picker.SelectedItem = chatGpt;
        Assert.True(window.FindControl<Border>("ManualProviderPanel")!.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Language_switch_keeps_closed_provider_flow_closed()
    {
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource()), new UiProviderFacade());
        window.Show();
        await window.InitializeAsync();

        var language = window.FindControl<ComboBox>("LanguageBox")!;
        language.SelectedItem = "日本語";
        await Task.Delay(50);

        Assert.False(window.ViewModel.IsProviderFlowOpen);
        Assert.False(window.FindControl<ComboBox>("ProviderPicker")!.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Quota_refresh_status_is_visible_disables_refresh_and_is_announced_until_done()
    {
        var application = new BlockingRefreshApplication();
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource(), quotaApplication: application), new UiProviderFacade());
        window.Show();

        var refresh = window.ViewModel.RefreshAsync();
        await application.Started.Task;

        var status = window.FindControl<Border>("QuotaRefreshStatus")!;
        Assert.True(status.IsVisible);
        Assert.True(window.FindControl<ProgressBar>("QuotaRefreshProgressBar")!.IsIndeterminate);
        Assert.False(window.FindControl<Button>("RefreshButton")!.IsEnabled);
        Assert.Equal(window.ViewModel.QuotaStatusText, AutomationProperties.GetName(status));

        application.Release.TrySetResult(true);
        await refresh;
        Assert.False(status.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Language_switch_preserves_manual_controls_and_saves_the_selected_claude_monthly_identity()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        MainWindow? window = null;
        MainViewModel? viewModel = null;
        JsonlQuotaHistory? history = null;
        try
        {
            var store = new AppSettingsStore(root);
            const string githubClientId = "github-client-before-language-switch";
            store.Save(new AppSettingsDto(Language: "English", GithubOAuthClientId: githubClientId));
            history = new JsonlQuotaHistory(root);
            viewModel = new MainViewModel(new EmptyDashboardSource(), new ManualQuotaService(history), history, settingsStore: store);
            window = new MainWindow(viewModel, new UiProviderFacade());
            window.Show();
            await window.InitializeAsync();
            var githubClientIdBox = window.FindControl<TextBox>("GithubClientIdBox")!;
            Assert.Equal(githubClientId, githubClientIdBox.Text);
            Assert.Equal(githubClientId, viewModel.Settings.GithubOAuthClientId);

            window.FindControl<Button>("AddProviderButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.FindControl<ComboBox>("ProviderPicker")!.SelectedItem = window.ViewModel.ProviderChoices.Single(item => item.Choice == ProviderConnectionChoice.Claude);
            var provider = window.FindControl<ComboBox>("ManualProviderBox")!;
            var quotaWindow = window.FindControl<ComboBox>("ManualWindowBox")!;
            var account = window.FindControl<TextBox>("AccountNameBox")!;
            var percent = window.FindControl<TextBox>("UsedPercentBox")!;
            var reset = window.FindControl<TextBox>("ResetAtBox")!;
            provider.SelectedItem = UiSettings.ManualProviderChoices(UiLanguage.English).Single(item => item.Value == ProviderKind.Claude);
            quotaWindow.SelectedItem = UiSettings.ManualWindowChoices(UiLanguage.English).Single(item => item.Value == QuotaWindowKind.Monthly);
            account.Text = "Claude account";
            percent.Text = "37";
            reset.Text = string.Empty;

            window.FindControl<ComboBox>("LanguageBox")!.SelectedItem = "日本語";
            await WaitForAsync(async () => (await store.LoadAsync()).Language == "Japanese");
            var savedSettings = await store.LoadAsync();

            Assert.Equal(UiLanguage.Japanese, viewModel.Language);
            Assert.Equal(githubClientId, githubClientIdBox.Text);
            Assert.Equal(githubClientId, viewModel.Settings.GithubOAuthClientId);
            Assert.Equal(githubClientId, savedSettings.GithubOAuthClientId);
            Assert.Equal("Japanese", savedSettings.Language);
            Assert.Equal(ProviderKind.Claude, Assert.IsType<LocalizedChoice<ProviderKind>>(provider.SelectedItem).Value);
            Assert.Equal<QuotaWindowKind>(QuotaWindowKind.Monthly, ((LocalizedChoice<QuotaWindowKind>)quotaWindow.SelectedItem!).Value);
            Assert.Equal("Claude account", account.Text);
            Assert.Equal("37", percent.Text);
            Assert.Equal(string.Empty, reset.Text);

            var addAccount = window.GetVisualDescendants().OfType<Button>().Single(button => button.Content?.ToString() == window.ViewModel.CopyText.AddAccount);
            addAccount.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => Task.FromResult(window.ViewModel.History.Entries.Any(entry => entry.Account == "Claude account")));
            var saved = Assert.Single(window.ViewModel.History.Entries, entry => entry.Account == "Claude account");
            Assert.Equal("Claude", saved.Provider);
            Assert.Equal("Monthly", saved.Window);
        }
        finally
        {
            window?.Close();
            if (viewModel is not null) viewModel.Dispose();
            else history?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task Github_client_id_same_value_text_change_does_not_mutate_but_changed_input_persists()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        MainWindow? window = null;
        MainViewModel? viewModel = null;
        try
        {
            var store = new AppSettingsStore(root);
            const string originalClientId = "github-client-original";
            store.Save(new AppSettingsDto(GithubOAuthClientId: originalClientId));
            var viewModelFactory = new RecordingGithubClientFactory();
            var facadeFactory = new RecordingGithubClientFactory();
            viewModel = new MainViewModel(new EmptyDashboardSource(), settingsStore: store, githubFactory: viewModelFactory);
            window = new MainWindow(viewModel, new UiProviderFacade(githubFactory: facadeFactory));
            window.Show();
            await window.InitializeAsync();

            var clientIdBox = window.FindControl<TextBox>("GithubClientIdBox")!;
            Assert.Equal(1, viewModelFactory.CreateCount);
            Assert.Equal(0, facadeFactory.CreateCount);

            clientIdBox.Text = originalClientId;
            await Task.Delay(50);

            Assert.Equal(originalClientId, viewModel.Settings.GithubOAuthClientId);
            Assert.Equal(1, viewModelFactory.CreateCount);
            Assert.Equal(0, facadeFactory.CreateCount);

            const string changedClientId = "github-client-changed";
            clientIdBox.Text = $"  {changedClientId}  ";
            await WaitForAsync(async () =>
                (await store.LoadAsync()).GithubOAuthClientId == changedClientId
                && facadeFactory.CreateCount == 1);

            Assert.Equal(changedClientId, viewModel.Settings.GithubOAuthClientId);
            Assert.Equal(2, viewModelFactory.CreateCount);
            Assert.Equal(1, facadeFactory.CreateCount);
        }
        finally
        {
            window?.Close();
            viewModel?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Japanese_language_switch_relocalizes_existing_usage_rows()
    {
        var vm = new MainViewModel(new DemoDashboardSource());
        var english = vm.Cards[0].Windows[0];
        vm.Language = UiLanguage.Japanese;
        var japanese = vm.Cards[0].Windows[0];

        Assert.Contains("使用", japanese.PercentText);
        Assert.Contains("残り", japanese.PercentText);
        Assert.Contains("更新", japanese.FreshnessText);
        Assert.NotEqual(english.PercentText, japanese.PercentText);
    }

    [Fact]
    public void Japanese_usage_copy_uses_natural_status_and_metric_labels()
    {
        var now = new DateTimeOffset(2026, 9, 6, 14, 30, 0, TimeSpan.FromHours(9));
        var snapshot = DemoSnapshot(42) with { Fetched = now.AddMinutes(-5), Metric = "Messages" };

        var row = QuotaPresentationFormatter.Format(snapshot, now, UiLanguage.Japanese);

        Assert.Equal("使用率 42%・残り 58%", row.PercentText);
        Assert.Equal("余裕あり", row.StatusText);
        Assert.Equal("メッセージ", row.Metric);
        Assert.Equal("5分前に更新", row.FreshnessText);
        Assert.Contains("使用率", row.ProgressLabel);
    }

    [Fact]
    public void Japanese_metric_mapping_keeps_unknown_metric_verbatim()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Equal("リクエスト", QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Metric = "Requests" }, now, UiLanguage.Japanese).Metric);
        Assert.Equal("短時間枠", QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Metric = "Fast window" }, now, UiLanguage.Japanese).Metric);
        Assert.Equal("月次", QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Metric = "monthly" }, now, UiLanguage.Japanese).Metric);
        Assert.Equal(string.Empty, QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Metric = "weekly" }, now, UiLanguage.Japanese).Metric);
        Assert.Equal("Codexの主要利用枠", QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Metric = "Codex primary" }, now, UiLanguage.Japanese).Metric);
        Assert.Equal("Codexの副利用枠", QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Metric = "Codex secondary" }, now, UiLanguage.Japanese).Metric);
        Assert.Equal("Custom metric", QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Metric = "Custom metric" }, now, UiLanguage.Japanese).Metric);
    }

    [Fact]
    public void Japanese_copy_uses_natural_labels_without_enum_status_words()
    {
        var copy = new UiCopy(UiLanguage.Japanese);

        Assert.Equal("利用枠", copy.Window);
        Assert.Equal("使用率（%）", copy.UsedPercent);
        Assert.Equal("リセット日時（任意）", copy.ResetOptional);
        Assert.Equal("CSVをエクスポート", copy.ExportCsv);
        Assert.Equal("JSONをエクスポート", copy.ExportJson);
        Assert.Contains("テレメトリーは送信しません", copy.NoSecretsLeave);
        Assert.Contains("連携先への認証", copy.NoSecretsLeave);
        Assert.Contains("セッション中のみ", copy.OpenCodeStatus(new(true, "ignored")));
        Assert.Contains("トレイを利用できません", copy.TrayUnavailableTitle);
        Assert.Contains("現在の期間", copy.RefreshNoData([ProviderKind.ChatGpt], new HashSet<ProviderKind>()));
        Assert.DoesNotContain("device flow", copy.CopilotDescription, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Client Secret", copy.CopilotDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("デバイス認証", copy.CodexDescription);
        Assert.DoesNotContain("ローリング", copy.HistoryDescription);
        Assert.DoesNotContain("Unsupported", copy.Autostart);
        Assert.DoesNotContain("Unavailable", copy.CredentialUnavailable("Unavailable"));
    }

    [Fact]
    public void Japanese_provider_state_text_is_safe_and_localized_from_structured_state()
    {
        var vm = new MainViewModel(new EmptyDashboardSource()) { Language = UiLanguage.Japanese };
        vm.SetCodexResult(new(false, CodexAuthorizationState.Disconnected, "Codex is not connected."));

        Assert.Equal("Codexは未接続です。", vm.CodexStatusText);
        vm.SetCopilotDeviceResult(string.Empty, "GitHub authorization completed; token stored securely.");
        Assert.DoesNotContain("token", vm.CopilotDeviceResult, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("認証", vm.CopilotDeviceResult);
    }

    [Fact]
    public void Copilot_initial_state_is_idle_and_localized_without_claiming_failure()
    {
        var english = new MainViewModel(new EmptyDashboardSource());
        Assert.Equal("Copilot authentication has not started yet.", english.CopilotDeviceResult);

        var japanese = new MainViewModel(new EmptyDashboardSource()) { Language = UiLanguage.Japanese };
        Assert.Equal("Copilotの認証はまだ開始されていません。", japanese.CopilotDeviceResult);
    }

    [Fact]
    public void Copilot_started_keeps_safe_url_and_code_across_language_switch_and_terminal_states_clear_code()
    {
        var vm = new MainViewModel(new EmptyDashboardSource());
        vm.SetCopilotDeviceResult("https://github.example/device", "ABCD-EFGH", CopilotUiState.Started);
        Assert.Contains("https://github.example/device", vm.CopilotDeviceResult);
        Assert.Contains("ABCD-EFGH", vm.CopilotDeviceResult);
        vm.Language = UiLanguage.Japanese;
        Assert.Contains("https://github.example/device", vm.CopilotDeviceResult);
        Assert.Contains("ABCD-EFGH", vm.CopilotDeviceResult);
        vm.SetCopilotDeviceResult("https://ignored", "ignored", CopilotUiState.Completed);
        Assert.DoesNotContain("ignored", vm.CopilotDeviceResult);
        vm.SetCopilotDeviceResult("https://ignored", "ignored", CopilotUiState.Failed);
        Assert.DoesNotContain("ignored", vm.CopilotDeviceResult);
        vm.SetCopilotDeviceResult("https://ignored", "ignored", CopilotUiState.GhProbe);
        Assert.DoesNotContain("ignored", vm.CopilotDeviceResult);
    }

    [Fact]
    public void Japanese_history_choices_and_manual_window_choices_are_localized_without_changing_values()
    {
        var history = UiSettings.HistoryAccountChoices(UiLanguage.Japanese);
        var windows = UiSettings.HistoryWindowChoices(UiLanguage.Japanese);
        var manual = UiSettings.ManualWindowChoices(UiLanguage.Japanese);

        Assert.Equal("すべてのアカウント", history.Single(item => item.Value == "All accounts").DisplayName);
        Assert.Equal("個人", history.Single(item => item.Value == "Personal").DisplayName);
        Assert.Equal("すべての利用枠", windows.Single(item => item.Value == "All windows").DisplayName);
        Assert.Equal("週次", windows.Single(item => item.Value == "Weekly").DisplayName);
        Assert.Equal(QuotaWindowKind.Weekly, manual.Single(item => item.Value == QuotaWindowKind.Weekly).Value);
        Assert.Equal("週次", manual.Single(item => item.Value == QuotaWindowKind.Weekly).DisplayName);
    }

    [Fact]
    public void Usage_band_exposes_text_numeric_and_distinct_fill_brush()
    {
        var normal = QuotaPresentationFormatter.Format(DemoSnapshot(42), DateTimeOffset.UtcNow);
        var danger = QuotaPresentationFormatter.Format(DemoSnapshot(92), DateTimeOffset.UtcNow);
        var over = QuotaPresentationFormatter.Format(DemoSnapshot(125), DateTimeOffset.UtcNow);

        Assert.Equal(42d, normal.VisualPercent);
        Assert.Contains("Normal", normal.StatusText);
        Assert.Contains("92", danger.StatusText);
        Assert.Contains("Over", over.StatusText);
        Assert.NotEqual(normal.Band, danger.Band);
        Assert.NotEqual(danger.Band, over.Band);
    }

    [Fact]
    public void Usage_band_is_rendered_by_semantic_progress_bar_classes_not_fixed_brushes()
    {
        Assert.NotNull(typeof(QuotaRowViewModel).GetProperty(nameof(QuotaRowViewModel.IsAttention)));
        Assert.Null(typeof(QuotaRowViewModel).GetProperty("BandBrush"));
    }

    [Fact]
    public void Japanese_reset_uses_natural_local_datetime_and_direct_relative_wording()
    {
        var now = new DateTimeOffset(2026, 9, 6, 14, 30, 0, TimeSpan.FromHours(9));
        var reset = new DateTimeOffset(2026, 9, 8, 14, 30, 0, TimeSpan.FromHours(9));

        var text = QuotaPresentationFormatter.FormatReset(reset, now, UiLanguage.Japanese);
        var localReset = reset.ToLocalTime().ToString("M月d日(ddd) HH:mm", System.Globalization.CultureInfo.GetCultureInfo("ja-JP"));

        Assert.Equal($"{localReset}にリセット（あと2日）", text);
    }

    [Fact]
    public void Provider_choices_have_localized_display_names_while_preserving_identity()
    {
        var japanese = UiSettings.ProviderChoices(UiLanguage.Japanese);
        var english = UiSettings.ProviderChoices(UiLanguage.English);

        Assert.Equal("ChatGPT", japanese.Single(item => item.Choice == ProviderConnectionChoice.ChatGpt).DisplayName);
        Assert.Equal("Claude", english.Single(item => item.Choice == ProviderConnectionChoice.Claude).DisplayName);
        Assert.DoesNotContain(japanese, item => item.DisplayName == nameof(ProviderConnectionChoice.ChatGpt));
    }

    [Fact]
    public void Japanese_window_and_metric_copy_avoids_duplicate_synonyms_and_localizes_window_terms()
    {
        var now = DateTimeOffset.UtcNow;
        var monthly = QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Window = new(QuotaWindowKind.Monthly, now.AddHours(-1), now.AddHours(1)), Metric = "monthly" }, now, UiLanguage.Japanese);
        var rolling = QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Window = new(QuotaWindowKind.Rolling, now.AddHours(-1), now.AddHours(1)), Metric = "Fast window" }, now, UiLanguage.Japanese);

        Assert.Equal("月次", monthly.WindowName);
        Assert.Equal(string.Empty, monthly.Metric);
        Assert.Equal("monthly", QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Window = monthly.Snapshot!.Window, Metric = "monthly" }, now, UiLanguage.English).Metric);
        Assert.Equal("ローリング", rolling.WindowName);
        Assert.Equal("短時間枠", rolling.Metric);
    }

    [Fact]
    public void Japanese_over_limit_and_unknown_usage_copy_are_single_source_of_truth()
    {
        var now = DateTimeOffset.UtcNow;
        var over = QuotaPresentationFormatter.Format(DemoSnapshot(112), now, UiLanguage.Japanese);
        var unknown = QuotaPresentationFormatter.Format(DemoSnapshot(0) with { Used = null, Limit = null }, now, UiLanguage.Japanese);

        Assert.Equal("使用率 112%（残り 0%・上限を12%超過）", over.PercentText);
        Assert.Equal("上限を12%超過", over.StatusText);
        Assert.Equal("使用率を表示できません", unknown.PercentText);
        Assert.Equal("データなし・値: —", unknown.StatusText);
        Assert.Contains("。", unknown.ProgressLabel);
        Assert.DoesNotContain("取得中", unknown.ProgressLabel);
    }

    [Fact]
    public void Japanese_copy_localizes_theme_labels_and_notification_threshold()
    {
        var choices = UiSettings.ThemeChoices(UiLanguage.Japanese);
        var copy = new UiCopy(UiLanguage.Japanese);

        Assert.Equal("システム", choices.Single(item => item.Value == ThemeMode.System).DisplayName);
        Assert.Equal("ライト", choices.Single(item => item.Value == ThemeMode.Light).DisplayName);
        Assert.Equal("ダーク", choices.Single(item => item.Value == ThemeMode.Dark).DisplayName);
        Assert.Equal("通知する使用率（%）", copy.OverallThreshold);
        Assert.Equal("認証状態を確認", copy.CodexConfirm);
        Assert.Equal("認証状態を確認", copy.Poll);
        Assert.Equal("GitHub App Client ID（秘密情報ではありません）", copy.GithubClientId);
    }

    [Fact]
    public void Japanese_demo_accounts_localize_but_named_organization_does_not()
    {
        var vm = new MainViewModel(new DemoDashboardSource());
        vm.Language = UiLanguage.Japanese;
        Assert.Contains(vm.Cards, card => card.Account == "個人アカウント");
        Assert.Contains(vm.Cards, card => card.Account == "ワークスペース · デモ");
        Assert.Contains(vm.Cards, card => card.Account == "Acme Engineering");
        vm.Language = UiLanguage.English;
        Assert.Contains(vm.Cards, card => card.Account == "Personal account");
        Assert.Contains(vm.Cards, card => card.Account == "Workspace · demo");
    }

    [Fact]
    public void Theme_and_provider_choice_values_survive_localization()
    {
        var theme = UiSettings.ThemeChoices(UiLanguage.Japanese).Single(item => item.Value == ThemeMode.Dark);
        var provider = UiSettings.ManualProviderChoices(UiLanguage.Japanese).Single(item => item.Value == ProviderKind.Copilot);

        Assert.Equal(ThemeMode.Dark, theme.Value);
        Assert.Equal("ダーク", theme.DisplayName);
        Assert.Equal(ProviderKind.Copilot, provider.Value);
        Assert.Equal("GitHub Copilot", provider.DisplayName);
    }

    [Fact]
    public async Task History_window_display_localizes_without_changing_filter_or_export_identity()
    {
        using var history = new HistoryState(uiDispatcher: new ImmediateUiDispatcher());
        history.WindowFilter = "Weekly";
        history.SetLanguage(UiLanguage.Japanese);
        await history.WaitForPublishedAsync();

        Assert.NotEmpty(history.FilteredEntries);
        Assert.All(history.FilteredEntries, entry => Assert.Equal("週次", entry.WindowDisplay));
        Assert.Equal("Weekly", history.WindowFilter);
        Assert.Contains("Weekly", await history.ExportCsvAsync(), StringComparison.Ordinal);

        history.SetLanguage(UiLanguage.English);
        await history.WaitForPublishedAsync();
        Assert.All(history.FilteredEntries, entry => Assert.Equal("Weekly", entry.WindowDisplay));
    }

    [AvaloniaFact]
    public async Task History_visual_tree_contains_only_the_current_page_not_the_full_archive()
    {
        var source = new InMemoryQuotaHistory(Enumerable.Range(0, 500).Select(index => new QuotaSnapshot(ProviderKind.ChatGpt, "Personal", "Messages", new(QuotaWindowKind.Weekly, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(6)), null, null, index, "percent", DateTimeOffset.UtcNow.AddMinutes(index), DateTimeOffset.UtcNow.AddMinutes(index), QuotaSource.Manual, QuotaConfidence.Manual, null, "ChatGPT Plus")).ToList());
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaHistory: source);
        var window = new MainWindow(vm);
        try
        {
            await vm.History.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(10));
            vm.Navigate(AppPage.History);
            window.Show();

            var list = window.FindControl<ListBox>("HistoryEntriesList")!;
            Assert.Equal(HistoryState.PageSize, list.ItemCount);
            Assert.True(list.GetVisualDescendants().OfType<Border>().Count() < 500);
            Assert.Equal("History entries", AutomationProperties.GetName(list));
        }
        finally { window.Close(); vm.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Japanese_language_switch_updates_rendered_theme_and_manual_provider_choices()
    {
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource()), new UiProviderFacade());
        window.Show();
        await window.InitializeAsync();

        window.FindControl<ComboBox>("ThemeBox")!.SelectedItem = UiSettings.ThemeChoices(UiLanguage.English).Single(item => item.Value == ThemeMode.Dark);
        window.FindControl<ComboBox>("ManualProviderBox")!.SelectedItem = UiSettings.ManualProviderChoices(UiLanguage.English).Single(item => item.Value == ProviderKind.Copilot);
        window.FindControl<ComboBox>("LanguageBox")!.SelectedItem = "日本語";
        await Task.Delay(50);

        var selectedTheme = Assert.IsType<LocalizedChoice<ThemeMode>>(window.FindControl<ComboBox>("ThemeBox")!.SelectedItem);
        var selectedProvider = Assert.IsType<LocalizedChoice<ProviderKind>>(window.FindControl<ComboBox>("ManualProviderBox")!.SelectedItem);
        Assert.Equal(ThemeMode.Dark, selectedTheme.Value);
        Assert.Equal("ダーク", selectedTheme.DisplayName);
        Assert.Equal(ProviderKind.Copilot, selectedProvider.Value);
        Assert.Equal("GitHub Copilot", selectedProvider.DisplayName);
        window.Close();
    }

    [Fact]
    public void Language_is_passed_to_new_cards_and_stale_state_round_trips()
    {
        var now = new DateTimeOffset(2026, 9, 6, 14, 30, 0, TimeSpan.FromHours(9));
        var snapshot = DemoSnapshot(42) with { Fetched = now.AddHours(-2), FreshUntil = now.AddHours(-1) };

        var japanese = DashboardAggregation.ToCards([snapshot], now, UiLanguage.Japanese).Single();
        Assert.Equal("更新できませんでした", japanese.StateText);
        Assert.Contains("データが古い可能性があります（最終更新: 2時間前）", japanese.Windows.Single().FreshnessText);

        var english = DashboardAggregation.ToCards([snapshot], now, UiLanguage.English).Single();
        Assert.Equal("Stale · refresh failed", english.StateText);
        Assert.Contains("Stale", english.Windows.Single().FreshnessText);
    }


    [AvaloniaFact]
    public void Rendered_quota_rings_use_the_four_semantic_theme_fills()
    {
        var window = new MainWindow(new MainViewModel(new DemoDashboardSource()), new UiProviderFacade());
        window.Show();

        foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            UiSettings.ApplyTheme(theme);
            var expected = new Dictionary<UsageBand, Color>
            {
                [UsageBand.Normal] = theme == ThemeMode.Light ? Color.Parse("#167C68") : Color.Parse("#73D6B1"),
                [UsageBand.Attention] = theme == ThemeMode.Light ? Color.Parse("#9A6500") : Color.Parse("#F0C66A"),
                [UsageBand.Danger] = theme == ThemeMode.Light ? Color.Parse("#B33B36") : Color.Parse("#FF918A"),
                [UsageBand.OverLimit] = theme == ThemeMode.Light ? Color.Parse("#7952A3") : Color.Parse("#D0A4F2")
            };

            var rings = window.GetVisualDescendants().OfType<QuotaRingControl>().ToList();
            Assert.NotEmpty(rings);
            foreach (var ring in rings)
            {
                Assert.Equal(expected[UsageBand.Normal], ((ISolidColorBrush)ring.NormalBrush!).Color);
                Assert.Equal(expected[UsageBand.Attention], ((ISolidColorBrush)ring.AttentionBrush!).Color);
                Assert.Equal(expected[UsageBand.Danger], ((ISolidColorBrush)ring.DangerBrush!).Color);
                Assert.Equal(expected[UsageBand.OverLimit], ((ISolidColorBrush)ring.OverLimitBrush!).Color);
            }
        }

        window.Close();
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static QuotaSnapshot DemoSnapshot(decimal percent) => new(ProviderKind.ChatGpt, "acct", "Messages", new(QuotaWindowKind.Weekly, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)), percent, 100, null, "percent", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, QuotaSource.Manual, QuotaConfidence.Manual, DateTimeOffset.UtcNow.AddDays(1), "ChatGPT Plus");

    private static void AssertBrushColor(IBrush? actual, string expected)
    {
        Assert.Equal(Color.Parse(expected), Assert.IsAssignableFrom<ISolidColorBrush>(actual).Color);
    }

    private static IPseudoClasses GetPseudoClasses(Control control) =>
        (IPseudoClasses)control.GetType().GetProperty("PseudoClasses", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(control)!;

    private sealed class RecordingClipboard : IClipboardService
    {
        public string? LastText { get; private set; }
        public int CopyCount { get; private set; }
        public Task SetTextAsync(string text) { CopyCount++; LastText = text; return Task.CompletedTask; }
    }

    private sealed class RecordingGithubClientFactory : IGitHubClientFactory
    {
        public int CreateCount { get; private set; }
        public GitHubDeviceFlowClient Create(string clientId)
        {
            CreateCount++;
            return new GitHubDeviceFlowClient(new HttpClient(), clientId);
        }
    }

    private sealed class BlockingRefreshApplication : IQuotaApplication
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return new QuotaRefreshResult([], []);
        }
    }
}

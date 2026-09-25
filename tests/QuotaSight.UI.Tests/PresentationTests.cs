using Xunit;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Automation;
using QuotaSight.Core;
using QuotaSight.UI;

[assembly: AvaloniaTestApplication(typeof(App))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly, DisableTestParallelization = true)]

namespace QuotaSight.UI.Tests;

public sealed class PresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.FromHours(9));

    [Fact] public void Percent_clamps_visual_value_but_preserves_overage() { var s = DemoData.Create(Now).SelectMany(card => card.Windows).Single(row => row.IsOverLimit); Assert.Equal(100d, s.VisualPercent); Assert.Contains("over", s.StatusText, StringComparison.OrdinalIgnoreCase); }
    [Fact] public void Remaining_percent_is_explicit() { Assert.Contains("remaining", DemoData.Create(Now)[0].Windows[0].PercentText, StringComparison.OrdinalIgnoreCase); }
    [Fact] public void Relative_reset_uses_local_wording() { Assert.Contains("in", DemoData.Create(Now)[0].Windows[0].ResetText, StringComparison.OrdinalIgnoreCase); }
    [Fact] public void Manual_snapshot_is_badged_manual() { Assert.Equal("Manual", DemoData.Create(Now)[0].Windows[0].SourceBadge); }
    [Fact] public void Stale_snapshot_is_badged_stale() { Assert.Contains(DemoData.Create(Now), c => c.Windows.Any(w => w.IsStale)); }
    [Fact] public void Four_demo_accounts_are_present() { Assert.Equal(4, DemoData.Create(Now).Count); }
    [Fact] public void Demo_card_windows_put_the_shortest_actual_period_first_even_when_monthly_is_created_first() { Assert.All(DemoData.Create(Now), card => Assert.Equal(QuotaWindowKind.Daily, card.Windows[0].WindowKind)); }
    [Fact] public void Language_switch_has_two_supported_languages() { Assert.Equal(["English", "日本語"], UiSettings.SupportedLanguages); }
    [Fact] public void Refresh_interval_validation_rejects_short_values() { Assert.False(UiSettings.IsRefreshIntervalValid(TimeSpan.FromMinutes(4))); }
    [Fact] public void Refresh_interval_validation_rejects_long_values() { Assert.False(UiSettings.IsRefreshIntervalValid(TimeSpan.FromMinutes(16))); }
    [Fact] public void Refresh_interval_validation_accepts_five_to_fifteen() { Assert.True(UiSettings.IsRefreshIntervalValid(TimeSpan.FromMinutes(10))); }
    [Fact] public void Navigation_starts_on_dashboard() { var vm = new MainViewModel(new DemoDashboardSource()); Assert.Equal(AppPage.Dashboard, vm.CurrentPage); }
    [Fact] public void Navigation_can_open_history() { var vm = new MainViewModel(new DemoDashboardSource()); vm.Navigate(AppPage.History); Assert.Equal(AppPage.History, vm.CurrentPage); }
    [Fact] public void Navigation_can_open_settings() { var vm = new MainViewModel(new DemoDashboardSource()); vm.Navigate(AppPage.Settings); Assert.Equal(AppPage.Settings, vm.CurrentPage); }
    [Fact] public void Theme_mode_has_system_light_dark() { Assert.Equal(3, Enum.GetValues<ThemeMode>().Length); }
    [Fact] public void Demo_label_is_visible_in_source() { Assert.True(new DemoDashboardSource().IsDemo); }
    [Fact] public void Empty_state_is_safe() { var vm = new MainViewModel(new EmptyDashboardSource()); Assert.Equal("No quota data yet", vm.EmptyStateText); }
    [Fact] public void Unsupported_autostart_is_honest() { Assert.Contains("Unsupported", UiSettings.AutostartStatus); }

    [Fact]
    public void Navigation_raises_property_changed()
    {
        var vm = new MainViewModel(new DemoDashboardSource());
        var changed = new List<string>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);
        vm.Navigate(AppPage.History);
        Assert.Contains(nameof(vm.CurrentPage), changed);
        Assert.Contains(nameof(vm.IsDashboardVisible), changed);
    }

    [Fact]
    public void Language_changes_all_primary_ui_copy_and_notifies_each_binding()
    {
        var vm = new MainViewModel(new DemoDashboardSource());
        var changed = new List<string>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);
        Assert.Equal("Dashboard", vm.NavDashboardText);
        Assert.Equal("Refresh", vm.RefreshText);
        vm.Language = UiLanguage.Japanese;
        Assert.Equal("ダッシュボード", vm.NavDashboardText);
        Assert.Equal("更新", vm.RefreshText);
        Assert.Contains(nameof(vm.NavDashboardText), changed);
    }

    [Fact]
    public async Task ApplyManualSnapshotAsync_is_the_only_public_api_no_fire_and_forget()
    {
        var vm = new MainViewModel(new EmptyDashboardSource());
        var entry = new ManualQuotaEntry { Provider = ProviderKind.ChatGpt, AccountDisplayName = "Personal", UsedPercentText = "40" };
        Assert.True(entry.TryApply(out var snapshot));
        await vm.ApplyManualSnapshotAsync(snapshot!);
        Assert.Contains(vm.Cards, card => card.Account == "Personal");
        // Sync overload must not exist or must be private/internal; this test enforces async contract only
    }

    [Fact]
    public async Task History_delete_all_is_explicitly_available()
    {
        using var history = new HistoryState(uiDispatcher: new ImmediateUiDispatcher());
        await history.DeleteAllAsync();
        Assert.Empty(history.Entries);
    }

    [Fact]
    public async Task History_pages_limit_display_but_export_all_filtered_entries()
    {
        var source = new InMemoryQuotaHistory(Enumerable.Range(0, 500).Select(index => HistorySnapshot(index % 2 == 0 ? "Personal" : "Work", index)).ToList());
        using var history = new HistoryState(source, new ImmediateUiDispatcher());
        await history.InitializeAsync();

        Assert.Equal(50, history.DisplayedEntries.Count);
        Assert.Equal(10, history.PageCount);
        Assert.True(history.HasNextPage);
        history.NextPage();
        await history.WaitForPublishedAsync();
        Assert.Equal(50, history.DisplayedEntries.Count);
        Assert.Equal(history.Entries[50].Id, history.DisplayedEntries[0].Id);
        var csv = await history.ExportCsvAsync();
        Assert.Equal(500, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1);

        history.AccountFilter = "Personal";
        await history.WaitForPublishedAsync();
        Assert.Equal(0, history.CurrentPage);
        Assert.Equal(250, history.FilteredEntries.Count);
        Assert.True(history.HasNextPage);
        history.NextPage();
        history.NextPage();
        history.NextPage();
        history.NextPage();
        await history.WaitForPublishedAsync();
        Assert.True(await history.DeleteAsync(history.DisplayedEntries[^1].Id));
        Assert.Equal(4, history.CurrentPage);
    }

    [Fact]
    public async Task Sparkline_is_sampled_with_fixed_limit_and_keeps_temporal_endpoints()
    {
        var source = new InMemoryQuotaHistory(Enumerable.Range(0, 500).Select(index => HistorySnapshot("Personal", index)).ToList());
        using var history = new HistoryState(source, new ImmediateUiDispatcher());
        await history.InitializeAsync();

        Assert.InRange(history.SparklinePoints.Count, 2, HistoryState.SparklineLimit);
        Assert.Equal(0, history.SparklinePoints[0]);
        Assert.Equal(499, history.SparklinePoints[^1]);
    }

    [Fact]
    public async Task Sparkline_orders_reverse_input_filters_nulls_and_handles_empty_and_single_values()
    {
        var source = new InMemoryQuotaHistory([
            HistorySnapshot("Account", null, Now.AddMinutes(2)),
            HistorySnapshot("Account", 20, Now.AddMinutes(1)),
            HistorySnapshot("Account", 10, Now)
        ]);
        using var history = new HistoryState(source, new ImmediateUiDispatcher());
        await history.InitializeAsync();

        Assert.Equal([10d, 20d], history.SparklinePoints);
        var filtered = history.FilteredEntries;
        var sparkline = history.SparklinePoints;
        Assert.Same(filtered, history.FilteredEntries);
        Assert.Same(sparkline, history.SparklinePoints);
        await history.DeleteAllAsync();
        Assert.Empty(history.SparklinePoints);
        await history.AppendAndReloadAsync([HistorySnapshot("Account", 42, Now)]);
        Assert.Equal([42d], history.SparklinePoints);
    }

    [Fact]
    public async Task History_delete_normalizes_page_after_collection_shrinks()
    {
        var source = new InMemoryQuotaHistory(Enumerable.Range(0, 101).Select(index => HistorySnapshot("Account", index)).ToList());
        using var history = new HistoryState(source, new ImmediateUiDispatcher());
        await history.InitializeAsync();

        history.NextPage();
        history.NextPage();
        await history.WaitForPublishedAsync();
        Assert.Equal(2, history.CurrentPage);
        Assert.True(await history.DeleteAsync(history.Entries[^1].Id));

        Assert.Equal(1, history.CurrentPage);
        Assert.NotEmpty(history.DisplayedEntries);
    }

    private static QuotaSnapshot HistorySnapshot(string account, decimal? percent, DateTimeOffset? observed = null) => new(ProviderKind.ChatGpt, account, "Messages", new(QuotaWindowKind.Weekly, Now.AddDays(-1), Now.AddDays(6)), null, null, percent, "percent", observed ?? Now, observed ?? Now, QuotaSource.Manual, QuotaConfidence.Manual, null, "ChatGPT Plus");
    private static QuotaSnapshot HistorySnapshot(string account, int index) => HistorySnapshot(account, index, Now.AddMinutes(index));

    [AvaloniaFact]
    public void Warning_foreground_meets_wcag_contrast_on_warning_background_in_light_and_dark_themes()
    {
        var app = Assert.IsType<App>(Avalonia.Application.Current);

        foreach (var themeKey in new[] { "Light", "Dark" })
        {
            var theme = (ResourceDictionary)app.Resources.ThemeDictionaries.First(pair => pair.Key.ToString() == themeKey).Value!;
            var warningBackground = ((ISolidColorBrush)theme["WarningBrush"]!).Color;
            var warningForeground = ((ISolidColorBrush)theme["WarningForegroundBrush"]!).Color;

            Assert.True(
                ContrastRatio(warningBackground, warningForeground) >= 4.5,
                $"{themeKey} warning contrast must be at least 4.5:1.");
        }
    }

    [AvaloniaFact]
    public void Semantic_owned_colors_follow_actual_light_and_dark_theme_variants_with_readable_contrast()
    {
        var app = Assert.IsType<App>(Avalonia.Application.Current);
        var previousTheme = UiSettings.AppliedTheme;
        MainWindow? lightWindow = null;
        MainWindow? darkWindow = null;
        try
        {
            UiSettings.ApplyTheme(ThemeMode.System);
            Assert.Equal(ThemeVariant.Default, UiSettings.AppliedTheme);
            app.RequestedThemeVariant = ThemeVariant.Light;
            lightWindow = new MainWindow(new MainViewModel(new DemoDashboardSource())) { RequestedThemeVariant = ThemeVariant.Light };
            lightWindow.Show();
            var lightTheme = (ResourceDictionary)app.Resources.ThemeDictionaries.First(pair => pair.Key.ToString() == "Light").Value!;
            var lightBackground = ((ISolidColorBrush)lightTheme["ContentBackgroundBrush"]!).Color;
            var lightForeground = ((ISolidColorBrush)lightTheme["TextPrimaryBrush"]!).Color;

            app.RequestedThemeVariant = ThemeVariant.Dark;
            darkWindow = new MainWindow(new MainViewModel(new DemoDashboardSource())) { RequestedThemeVariant = ThemeVariant.Dark };
            darkWindow.Show();
            var darkTheme = (ResourceDictionary)app.Resources.ThemeDictionaries.First(pair => pair.Key.ToString() == "Dark").Value!;
            var darkBackground = ((ISolidColorBrush)darkTheme["ContentBackgroundBrush"]!).Color;
            var darkForeground = ((ISolidColorBrush)darkTheme["TextPrimaryBrush"]!).Color;

            Assert.Equal(ThemeVariant.Light, lightWindow.ActualThemeVariant);
            Assert.Equal(ThemeVariant.Dark, darkWindow.ActualThemeVariant);
            Assert.NotEqual(lightBackground, darkBackground);
            Assert.NotEqual(lightForeground, darkForeground);
            Assert.True(ContrastRatio(lightBackground, lightForeground) >= 4.5);
            Assert.True(ContrastRatio(darkBackground, darkForeground) >= 4.5);
        }
        finally
        {
            darkWindow?.Close();
            darkWindow?.ViewModel.Dispose();
            lightWindow?.Close();
            lightWindow?.ViewModel.Dispose();
            UiSettings.ApplyTheme(previousTheme switch
            {
                var theme when theme == ThemeVariant.Light => ThemeMode.Light,
                var theme when theme == ThemeVariant.Dark => ThemeMode.Dark,
                _ => ThemeMode.System
            });
        }
    }

    [AvaloniaFact]
    public void Existing_window_resolves_semantic_brushes_and_updates_when_theme_changes()
    {
        var app = Assert.IsType<App>(Avalonia.Application.Current);
        var previousTheme = UiSettings.AppliedTheme;
        var window = new MainWindow(new MainViewModel(new DemoDashboardSource()));
        try
        {
            UiSettings.ApplyTheme(ThemeMode.System);
            window.Show();
            window.UpdateLayout();

            var contentScroll = window.FindControl<ScrollViewer>("ContentScroll")!;
            var contentSurface = window.FindControl<StackPanel>("ContentSurface")!;
            var title = window.FindControl<StackPanel>("DashboardHeaderCopy")!.Children.OfType<TextBlock>().Single(text => text.Classes.Contains("title"));
            var muted = window.FindControl<StackPanel>("DashboardHeaderCopy")!.Children.OfType<TextBlock>().Single(text => text.Classes.Contains("muted"));

            window.RequestedThemeVariant = ThemeVariant.Light;
            window.UpdateLayout();
            Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);
            var lightTheme = (ResourceDictionary)app.Resources.ThemeDictionaries.First(pair => pair.Key.ToString() == "Light").Value!;
            AssertResolvedBrush(contentScroll.Background, lightTheme, "ContentBackgroundBrush");
            AssertResolvedBrush(contentSurface.Background, lightTheme, "ContentBackgroundBrush");
            AssertResolvedBrush(title.Foreground, lightTheme, "TextPrimaryBrush");
            AssertResolvedBrush(muted.Foreground, lightTheme, "TextMutedBrush");

            window.RequestedThemeVariant = ThemeVariant.Dark;
            window.UpdateLayout();
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            var darkTheme = (ResourceDictionary)app.Resources.ThemeDictionaries.First(pair => pair.Key.ToString() == "Dark").Value!;
            AssertResolvedBrush(contentScroll.Background, darkTheme, "ContentBackgroundBrush");
            AssertResolvedBrush(contentSurface.Background, darkTheme, "ContentBackgroundBrush");
            AssertResolvedBrush(title.Foreground, darkTheme, "TextPrimaryBrush");
            AssertResolvedBrush(muted.Foreground, darkTheme, "TextMutedBrush");

            Assert.NotEqual(((ISolidColorBrush)lightTheme["ContentBackgroundBrush"]!).Color, ((ISolidColorBrush)darkTheme["ContentBackgroundBrush"]!).Color);
            Assert.NotEqual(((ISolidColorBrush)lightTheme["TextPrimaryBrush"]!).Color, ((ISolidColorBrush)darkTheme["TextPrimaryBrush"]!).Color);
            Assert.NotEqual(((ISolidColorBrush)lightTheme["TextMutedBrush"]!).Color, ((ISolidColorBrush)darkTheme["TextMutedBrush"]!).Color);

            window.RequestedThemeVariant = ThemeVariant.Default;
            window.UpdateLayout();
            Assert.True(window.ActualThemeVariant == ThemeVariant.Light || window.ActualThemeVariant == ThemeVariant.Dark);
            var systemTheme = window.ActualThemeVariant == ThemeVariant.Light ? lightTheme : darkTheme;
            AssertResolvedBrush(contentScroll.Background, systemTheme, "ContentBackgroundBrush");
            AssertResolvedBrush(contentSurface.Background, systemTheme, "ContentBackgroundBrush");
            AssertResolvedBrush(title.Foreground, systemTheme, "TextPrimaryBrush");
            AssertResolvedBrush(muted.Foreground, systemTheme, "TextMutedBrush");
        }
        finally
        {
            window.Close();
            window.ViewModel.Dispose();
            UiSettings.ApplyTheme(previousTheme switch
            {
                var theme when theme == ThemeVariant.Light => ThemeMode.Light,
                var theme when theme == ThemeVariant.Dark => ThemeMode.Dark,
                _ => ThemeMode.System
            });
        }
    }

    [AvaloniaFact]
    public void Dashboard_header_keeps_copy_and_refresh_separate_at_compact_width()
    {
        var window = new MainWindow(new MainViewModel(new DemoDashboardSource())) { Width = 420 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var copy = window.FindControl<StackPanel>("DashboardHeaderCopy")!;
            var refresh = window.FindControl<Button>("RefreshButton")!;

            Assert.False(copy.Bounds.Intersects(refresh.Bounds));
            Assert.True(refresh.Bounds.Y >= copy.Bounds.Bottom);
        }
        finally
        {
            window.Close();
            window.ViewModel.Dispose();
        }
    }

    [AvaloniaFact]
    public void Main_window_page_visibility_follows_navigation_state()
    {
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource()));
        try
        {
            window.Show();
            var vm = window.ViewModel;
            vm.Navigate(AppPage.History);
            Assert.False(vm.IsDashboardVisible);
            Assert.True(vm.IsHistoryVisible);
        }
        finally
        {
            window.Close();
            window.ViewModel.Dispose();
        }
    }

    private static void AssertResolvedBrush(IBrush? actual, ResourceDictionary theme, string key)
    {
        var actualBrush = Assert.IsAssignableFrom<ISolidColorBrush>(actual);
        var expectedBrush = Assert.IsAssignableFrom<ISolidColorBrush>(theme[key]);
        Assert.Equal(expectedBrush.Color, actualBrush.Color);
    }

    private static double ContrastRatio(Color background, Color foreground)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255d;
            return normalized <= 0.03928 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        var backgroundLuminance = 0.2126 * Channel(background.R) + 0.7152 * Channel(background.G) + 0.0722 * Channel(background.B);
        var foregroundLuminance = 0.2126 * Channel(foreground.R) + 0.7152 * Channel(foreground.G) + 0.0722 * Channel(foreground.B);
        var lighter = Math.Max(backgroundLuminance, foregroundLuminance);
        var darker = Math.Min(backgroundLuminance, foregroundLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }
}
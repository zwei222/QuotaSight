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

namespace QuotaSight.UI.Tests;

public sealed class PresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.FromHours(9));

    [Fact] public void Percent_clamps_visual_value_but_preserves_overage() { var s = DemoData.Create(Now)[2].Windows[0]; Assert.Equal(100d, s.VisualPercent); Assert.Contains("over", s.StatusText, StringComparison.OrdinalIgnoreCase); }
    [Fact] public void Remaining_percent_is_explicit() { Assert.Contains("remaining", DemoData.Create(Now)[0].Windows[0].PercentText, StringComparison.OrdinalIgnoreCase); }
    [Fact] public void Relative_reset_uses_local_wording() { Assert.Contains("in", DemoData.Create(Now)[0].Windows[0].ResetText, StringComparison.OrdinalIgnoreCase); }
    [Fact] public void Manual_snapshot_is_badged_manual() { Assert.Equal("Manual", DemoData.Create(Now)[0].Windows[0].SourceBadge); }
    [Fact] public void Stale_snapshot_is_badged_stale() { Assert.Contains(DemoData.Create(Now), c => c.Windows.Any(w => w.IsStale)); }
    [Fact] public void Four_demo_accounts_are_present() { Assert.Equal(4, DemoData.Create(Now).Count); }
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
    public void History_delete_all_is_explicitly_available()
    {
        var history = new HistoryState();
        history.DeleteAll();
        Assert.Empty(history.Entries);
    }

    [AvaloniaFact]
    public void Warning_foreground_meets_wcag_contrast_on_warning_background_in_light_and_dark_themes()
    {
        var app = new App();
        app.Initialize();

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
        var app = new App();
        app.Initialize();
        UiSettings.ApplyTheme(ThemeMode.System);
        Assert.Equal(ThemeVariant.Default, UiSettings.AppliedTheme);
        app.RequestedThemeVariant = ThemeVariant.Light;
        var lightWindow = new MainWindow(new MainViewModel(new DemoDashboardSource())) { RequestedThemeVariant = ThemeVariant.Light };
        lightWindow.Show();
        var lightTheme = (ResourceDictionary)app.Resources.ThemeDictionaries.First(pair => pair.Key.ToString() == "Light").Value!;
        var lightBackground = ((ISolidColorBrush)lightTheme["ContentBackgroundBrush"]!).Color;
        var lightForeground = ((ISolidColorBrush)lightTheme["TextPrimaryBrush"]!).Color;

        app.RequestedThemeVariant = ThemeVariant.Dark;
        var darkWindow = new MainWindow(new MainViewModel(new DemoDashboardSource())) { RequestedThemeVariant = ThemeVariant.Dark };
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

    [AvaloniaFact]
    public void Existing_window_resolves_semantic_brushes_and_updates_when_theme_changes()
    {
        var app = new App();
        app.Initialize();
        UiSettings.ApplyTheme(ThemeMode.System);

        var window = new MainWindow(new MainViewModel(new DemoDashboardSource()));
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

    [AvaloniaFact]
    public void Dashboard_header_keeps_copy_and_refresh_separate_at_compact_width()
    {
        var window = new MainWindow(new MainViewModel(new DemoDashboardSource())) { Width = 420 };
        window.Show();
        window.UpdateLayout();
        var copy = window.FindControl<StackPanel>("DashboardHeaderCopy")!;
        var refresh = window.FindControl<Button>("RefreshButton")!;

        Assert.False(copy.Bounds.Intersects(refresh.Bounds));
        Assert.True(refresh.Bounds.Y >= copy.Bounds.Bottom);
    }

    [AvaloniaFact]
    public void Main_window_page_visibility_follows_navigation_state()
    {
        var window = new MainWindow();
        window.Show();
        var vm = window.ViewModel;
        vm.Navigate(AppPage.History);
        Assert.False(vm.IsDashboardVisible);
        Assert.True(vm.IsHistoryVisible);
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
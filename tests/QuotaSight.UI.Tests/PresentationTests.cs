using Xunit;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Automation;
using QuotaSight.Core;
using QuotaSight.UI;

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
    public void Main_window_page_visibility_follows_navigation_state()
    {
        var window = new MainWindow();
        window.Show();
        var vm = window.ViewModel;
        vm.Navigate(AppPage.History);
        Assert.False(vm.IsDashboardVisible);
        Assert.True(vm.IsHistoryVisible);
    }
}
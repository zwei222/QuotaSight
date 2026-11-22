using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// Visual contracts of the compact window after the quota-UI removal: the tray host-loss
/// recovery is unchanged, and the compact window's own surfaces are the observed-usage
/// empty state and its localized accessibility names.
/// </summary>
public sealed class ResidentModeVisualTests
{
    [AvaloniaFact]
    public void Host_loss_recovers_hidden_resident_window_and_disables_close_to_tray()
    {
        var vm = new MainViewModel();
        var window = new MainWindow(vm);
        var compact = new CompactQuotaWindow(vm);
        try
        {
            window.Show();
            window.SetTrayCapability(true);
            window.SyncResidentMode(true);
            compact.Show();
            window.Hide();

            window.ApplyTrayAvailability(false, compact);

            Assert.True(window.IsVisible);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.False(compact.IsVisible);
            Assert.False(window.TrayAvailable);
            Assert.False(window.EffectiveResidentMode);
        }
        finally { compact.Close(); window.Close(); vm.Dispose(); }
    }

    [AvaloniaFact]
    public void Host_loss_preserves_an_already_visible_minimized_window()
    {
        var vm = new MainViewModel();
        var window = new MainWindow(vm);
        var compact = new CompactQuotaWindow(vm);
        try
        {
            window.Show();
            window.SetTrayCapability(true);
            window.SyncResidentMode(true);
            window.WindowState = WindowState.Minimized;
            compact.Show();

            window.ApplyTrayAvailability(false, compact);

            Assert.True(window.IsVisible);
            Assert.Equal(WindowState.Minimized, window.WindowState);
        }
        finally { compact.Close(); window.Close(); vm.Dispose(); }
    }

    [AvaloniaFact]
    public void Fallback_does_not_activate_or_change_existing_window_state_but_full_show_does()
    {
        var vm = new MainViewModel();
        var window = new MainWindow(vm);
        try
        {
            window.ShowActivated = true;
            window.Show();
            window.WindowState = WindowState.Minimized;
            window.ShowFallbackWindow();
            Assert.True(window.ShowActivated);
            Assert.Equal(WindowState.Minimized, window.WindowState);

            window.ShowFullWindow();
            Assert.True(window.ShowActivated);
            Assert.Equal(WindowState.Normal, window.WindowState);
        }
        finally { window.Close(); vm.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Compact_window_without_selection_shows_localized_empty_state_instead_of_a_blank_card_list()
    {
        var vm = new MainViewModel();
        var window = new CompactQuotaWindow(vm);
        try
        {
            window.Show();
            await vm.RefreshTokScaleAsync();
            var empty = window.FindControl<Border>("CompactTokScaleEmptyState")!;

            Assert.False(empty.IsEffectivelyVisible);
            Assert.Equal(vm.TokScaleEmptyTitleText, window.FindControl<TextBlock>("CompactTokScaleEmptyTitle")!.Text);
            Assert.Equal(vm.TokScaleEmptyBodyText, window.FindControl<TextBlock>("CompactTokScaleEmptyBody")!.Text);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void Compact_accessibility_names_follow_localized_copy()
    {
        var vm = new MainViewModel();
        var window = new CompactQuotaWindow(vm);
        try
        {
            window.Show();
            var providers = window.FindControl<ItemsControl>("CompactTokScaleCards")!;
            Assert.Equal("Observed usage (TokScale)", AutomationProperties.GetName(providers));

            vm.Language = UiLanguage.Japanese;
            Assert.Equal("観測使用量（TokScale）", AutomationProperties.GetName(providers));
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}

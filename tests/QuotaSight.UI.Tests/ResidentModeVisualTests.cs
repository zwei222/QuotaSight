using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using QuotaSight.Application;
using QuotaSight.Core;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class ResidentModeVisualTests
{
    [AvaloniaFact]
    public void Host_loss_recovers_hidden_resident_window_and_disables_close_to_tray()
    {
        var vm = new MainViewModel(new EmptyDashboardSource());
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
            Assert.False(compact.IsVisible);
            Assert.False(window.TrayAvailable);
            Assert.False(window.EffectiveResidentMode);
        }
        finally { compact.Close(); window.Close(); vm.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Empty_compact_window_shows_localized_empty_state_instead_of_a_blank_card_list()
    {
        var vm = new MainViewModel(new EmptyDashboardSource());
        var window = new CompactQuotaWindow(vm);
        try
        {
            window.Show();
            await vm.RefreshAsync();
            var empty = window.FindControl<Border>("CompactEmptyState")!;

            Assert.True(empty.IsVisible);
            Assert.Equal(vm.EmptyStateText, window.FindControl<TextBlock>("CompactEmptyStateText")!.Text);
            Assert.Equal(vm.EmptyStateDescription, window.FindControl<TextBlock>("CompactEmptyStateDescription")!.Text);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Failure_banner_stays_visible_after_busy_completes_while_cached_cards_remain()
    {
        var vm = new MainViewModel(new DemoDashboardSource(), quotaApplication: new FailedRefreshApplication());
        var window = new CompactQuotaWindow(vm);
        try
        {
            window.Show();
            var cards = window.FindControl<ItemsControl>("CompactProviderCards")!;
            Assert.True(cards.IsVisible);

            await vm.RefreshAsync();

            var banner = window.FindControl<Border>("CompactNotificationBanner")!;
            Assert.False(vm.IsQuotaDataBusy);
            Assert.True(vm.IsNotificationVisible);
            Assert.True(banner.IsVisible);
            Assert.Contains("Temporary fetch failure", vm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(vm.Cards);
            Assert.Equal(vm.NotificationBannerText, AutomationProperties.GetName(banner));
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
        var vm = new MainViewModel(new EmptyDashboardSource());
        var window = new CompactQuotaWindow(vm);
        try
        {
            window.Show();
            var providers = window.FindControl<ItemsControl>("CompactProviderCards")!;
            Assert.Equal("Quota providers", AutomationProperties.GetName(providers));

            vm.Language = UiLanguage.Japanese;
            Assert.Equal("プロバイダー", AutomationProperties.GetName(providers));
            Assert.NotEqual("Quota providers", AutomationProperties.GetName(providers));
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void Compact_gauges_resolve_normal_attention_danger_and_over_limit_brushes_in_light_and_dark()
    {
        var app = Assert.IsType<App>(Avalonia.Application.Current);
        var previous = UiSettings.AppliedTheme;
        var rows = new[] { UsageBand.Normal, UsageBand.Attention, UsageBand.Danger, UsageBand.OverLimit }
            .Select((band, index) => DemoData.Create(DateTimeOffset.Now)[0].Windows[0] with
            {
                Band = band,
                VisualPercent = index == 3 ? 100 : 50 + index * 10
            }).ToArray();
        var source = new SingleCardSource(rows);
        var window = new CompactQuotaWindow(new MainViewModel(source));
        try
        {
            window.Show();
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = variant;
                window.UpdateLayout();
                var theme = (ResourceDictionary)app.Resources.ThemeDictionaries.First(pair => pair.Key?.ToString() == (variant == ThemeVariant.Light ? "Light" : "Dark")).Value!;
                var expected = new[] { "GaugeNormalBrush", "GaugeAttentionBrush", "GaugeDangerBrush", "GaugeOverBrush" };
                var gauges = window.FindControl<ItemsControl>("CompactProviderCards")!.GetVisualDescendants().OfType<ProgressBar>().ToArray();

                Assert.Equal(4, gauges.Length);
                for (var index = 0; index < gauges.Length; index++)
                {
                    var actual = Assert.IsAssignableFrom<ISolidColorBrush>(gauges[index].Foreground);
                    var wanted = Assert.IsAssignableFrom<ISolidColorBrush>(theme[expected[index]]);
                    Assert.Equal(wanted.Color, actual.Color);
                }
            }
        }
        finally
        {
            window.Close();
            window.ViewModel.Dispose();
            UiSettings.ApplyTheme(previous switch
            {
                var theme when theme == ThemeVariant.Light => ThemeMode.Light,
                var theme when theme == ThemeVariant.Dark => ThemeMode.Dark,
                _ => ThemeMode.System
            });
        }
    }

    private sealed class FailedRefreshApplication : IQuotaApplication
    {
        public ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new QuotaRefreshResult([], [new(ProviderKind.OpenCode, FetchStatus.TransientFailure)]));
    }

    private sealed class SingleCardSource(IReadOnlyList<QuotaRowViewModel> rows) : IDashboardSource
    {
        public bool IsDemo => false;
        public IReadOnlyList<ProviderCardViewModel> Load() =>
            [new(ProviderKind.OpenCode, "OpenCode Go", "Test account", "#5ED6C0", "Test", false, rows)];
    }
}

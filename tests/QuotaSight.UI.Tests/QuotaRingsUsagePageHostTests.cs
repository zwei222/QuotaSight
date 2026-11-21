using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using QuotaSight.UI;
using QuotaSight.UI.Tests.Support;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// Contract tests for the capture host's <c>--usage-page</c> argument. Each state is built from an
/// in-memory read port and the fixture's fixed clock, so the assertions never touch a credential,
/// a settings file or a network endpoint. The page is opened directly: no settings page and no
/// Tab-driven scroll is involved.
/// </summary>
[Collection(QuotaRingsSettingsHostCollection.Name)]
public sealed class QuotaRingsUsagePageHostTests
{
    public static IEnumerable<object[]> States() =>
        QuotaRingsHost.UsagePageStates.Select(state => new object[] { state });

    [AvaloniaTheory]
    [MemberData(nameof(States))]
    public async Task Every_usage_page_argument_state_builds_the_expected_view_model_state(string state)
    {
        var host = QuotaRingsHost.CreateHost(["--main", "--dark", "--usage-page", state]);
        try
        {
            Assert.IsType<MainWindow>(host.Window);
            Assert.Equal(state, host.UsagePageState);
            Assert.Equal(AppPage.Usage, host.ViewModel.CurrentPage);
            Assert.True(host.ViewModel.IsUsageVisible);
            Assert.Null(host.UsageState);
            Assert.Null(host.UsageCredentialStore);

            // The loading state's second read is what hangs, so its task must not be awaited.
            if (state != "loading")
                await host.ViewModel.UsageLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

            var copy = new UiCopy(UiLanguage.English);
            switch (state)
            {
                case "loading":
                    // The first read's values stay on screen while the refresh is in flight.
                    Assert.True(host.ViewModel.IsUsageLoading);
                    Assert.Equal(5, host.ViewModel.UsageRows.Count);
                    Assert.False(string.IsNullOrEmpty(host.ViewModel.UsageTotalText));
                    Assert.Equal(UsageFailureKind.None, host.ViewModel.UsageFailure);
                    Assert.False(host.ViewModel.IsUsageStale);
                    Assert.Equal(2, host.UsagePageReadPort!.Calls);
                    break;

                case "ready":
                    Assert.Equal(copy.UsageState("Ready"), host.ViewModel.UsageStateText);
                    Assert.False(host.ViewModel.IsUsageStale);
                    Assert.False(host.ViewModel.IsUsageDelayed);
                    Assert.Equal(UsageFailureKind.None, host.ViewModel.UsageFailure);
                    Assert.Equal(5, host.ViewModel.UsageRows.Count);
                    Assert.True(host.ViewModel.IsUsageTotalVisible);
                    Assert.Equal("Last received: 2026-11-20 11:00", host.ViewModel.UsageFreshnessText);
                    break;

                case "delayed":
                    Assert.Equal(copy.UsageState("Ready"), host.ViewModel.UsageStateText);
                    Assert.False(host.ViewModel.IsUsageStale);
                    Assert.True(host.ViewModel.IsUsageDelayed);
                    Assert.Equal(copy.UsageDelayed, host.ViewModel.UsageDelayedText);
                    Assert.Equal(UsageFailureKind.None, host.ViewModel.UsageFailure);
                    Assert.Equal(5, host.ViewModel.UsageRows.Count);
                    Assert.Equal("Last received: 2026-11-19 23:00", host.ViewModel.UsageFreshnessText);
                    break;

                case "stale":
                    Assert.Equal(copy.UsageState("Stale"), host.ViewModel.UsageStateText);
                    Assert.True(host.ViewModel.IsUsageStale);
                    Assert.False(host.ViewModel.IsUsageDelayed);
                    Assert.Equal(UsageFailureKind.Transient, host.ViewModel.UsageFailure);
                    Assert.Equal(5, host.ViewModel.UsageRows.Count);
                    Assert.True(host.ViewModel.IsUsageTotalVisible);
                    Assert.True(host.ViewModel.IsUsageFreshnessVisible);
                    Assert.Equal(2, host.UsagePageReadPort!.Calls);
                    break;

                case "stale-delayed":
                    Assert.Equal(copy.UsageState("Stale"), host.ViewModel.UsageStateText);
                    Assert.True(host.ViewModel.IsUsageStale);
                    Assert.True(host.ViewModel.IsUsageDelayed);
                    Assert.Equal(UsageFailureKind.Transient, host.ViewModel.UsageFailure);
                    Assert.Equal(5, host.ViewModel.UsageRows.Count);
                    Assert.True(host.ViewModel.IsUsageTotalVisible);
                    Assert.Equal(2, host.UsagePageReadPort!.Calls);
                    break;

                case "error-unauthorized":
                    Assert.Equal(copy.UsageState("Error"), host.ViewModel.UsageStateText);
                    Assert.False(host.ViewModel.IsUsageStale);
                    Assert.False(host.ViewModel.IsUsageDelayed);
                    Assert.Equal(UsageFailureKind.Unauthorized, host.ViewModel.UsageFailure);
                    Assert.Empty(host.ViewModel.UsageRows);
                    Assert.Empty(host.ViewModel.UsageTotalText);
                    Assert.Equal(1, host.UsagePageReadPort!.Calls);
                    break;

                case "error-transient":
                    Assert.Equal(copy.UsageState("Error"), host.ViewModel.UsageStateText);
                    Assert.False(host.ViewModel.IsUsageStale);
                    Assert.Equal(UsageFailureKind.Transient, host.ViewModel.UsageFailure);
                    Assert.Empty(host.ViewModel.UsageRows);
                    Assert.Equal(1, host.UsagePageReadPort!.Calls);
                    break;

                case "empty":
                    Assert.Equal(copy.UsageState("Empty"), host.ViewModel.UsageStateText);
                    Assert.Equal(UsageFailureKind.None, host.ViewModel.UsageFailure);
                    Assert.Empty(host.ViewModel.UsageRows);
                    Assert.False(host.ViewModel.IsUsageTotalVisible);
                    Assert.False(host.ViewModel.IsUsageDelayed);
                    break;

                case "unconfigured":
                    Assert.Equal(copy.UsageState("Unconfigured"), host.ViewModel.UsageStateText);
                    Assert.Equal(UsageFailureKind.None, host.ViewModel.UsageFailure);
                    Assert.Empty(host.ViewModel.UsageRows);
                    Assert.False(host.ViewModel.IsUsageRangeVisible);
                    Assert.Null(host.UsagePageReadPort);
                    break;

                default:
                    Assert.Fail($"No expectations are declared for state '{state}'.");
                    break;
            }
        }
        finally
        {
            host.Window.Close();
            host.UsagePageReadPort?.Release(() => new DailyUsage(default, default, [], 0));
            host.ViewModel.Dispose();
        }
    }

    [AvaloniaFact]
    public void Ready_state_shows_the_usage_page_without_the_settings_page_or_any_scroll()
    {
        var host = QuotaRingsHost.CreateHost(["--main", "--dark", "--usage-page", "ready"]);
        try
        {
            host.Window.Show();
            host.Window.UpdateLayout();

            Assert.True(host.Window.FindControl<Grid>("UsagePage")!.IsVisible);
            Assert.False(host.Window.FindControl<Grid>("SettingsPage")!.IsVisible);
            Assert.True(host.Window.FindControl<Border>("UsageDailyCard")!.IsVisible);
            Assert.True(host.Window.FindControl<TextBlock>("UsageTotal")!.IsVisible);
            Assert.True(host.Window.FindControl<TextBlock>("UsageFreshnessLine")!.IsVisible);
            Assert.False(host.Window.FindControl<Border>("UsageFailurePanel")!.IsVisible);
            Assert.False(host.Window.FindControl<Border>("UsageDelayedBanner")!.IsVisible);
            Assert.Equal(0d, host.Window.FindControl<ScrollViewer>("ContentScroll")!.Offset.Y);
        }
        finally { host.Window.Close(); host.ViewModel.Dispose(); }
    }

    [AvaloniaFact]
    public void Stale_state_keeps_the_rows_and_shows_the_retry_action()
    {
        var host = QuotaRingsHost.CreateHost(["--main", "--dark", "--usage-page", "stale"]);
        try
        {
            host.Window.Show();
            host.Window.UpdateLayout();

            var panel = host.Window.FindControl<Border>("UsageFailurePanel")!;
            Assert.True(panel.IsVisible);
            Assert.Contains("stale", panel.Classes);
            Assert.True(host.Window.FindControl<Button>("UsageRetryButton")!.IsVisible);
            Assert.False(host.Window.FindControl<Button>("UsageOpenSettingsButton")!.IsVisible);
            Assert.True(host.Window.FindControl<TextBlock>("UsageTotal")!.IsVisible);
            Assert.True(host.Window.FindControl<TextBlock>("UsageFreshnessLine")!.IsVisible);
        }
        finally { host.Window.Close(); host.ViewModel.Dispose(); }
    }

    [AvaloniaFact]
    public void Unauthorized_failure_shows_its_class_and_the_settings_detour()
    {
        var host = QuotaRingsHost.CreateHost(["--main", "--dark", "--usage-page", "error-unauthorized"]);
        try
        {
            host.Window.Show();
            host.Window.UpdateLayout();

            var panel = host.Window.FindControl<Border>("UsageFailurePanel")!;
            Assert.True(panel.IsVisible);
            Assert.Contains("error", panel.Classes);
            Assert.True(host.Window.FindControl<Button>("UsageOpenSettingsButton")!.IsVisible);
            Assert.Equal(new UiCopy(UiLanguage.English).UsageFailure(UsageFailureKind.Unauthorized),
                host.Window.FindControl<TextBlock>("UsageFailureTextBlock")!.Text);
            Assert.False(host.Window.FindControl<TextBlock>("UsageTotal")!.IsVisible);
        }
        finally { host.Window.Close(); host.ViewModel.Dispose(); }
    }

    [AvaloniaFact]
    public void Loading_state_keeps_the_previous_values_while_the_read_is_in_flight()
    {
        var host = QuotaRingsHost.CreateHost(["--main", "--dark", "--usage-page", "loading"]);
        try
        {
            host.Window.Show();
            host.Window.UpdateLayout();

            Assert.True(host.Window.FindControl<ProgressBar>("UsageLoadingBar")!.IsVisible);
            Assert.True(host.Window.FindControl<TextBlock>("UsageTotal")!.IsVisible);
            Assert.True(host.Window.FindControl<TextBlock>("UsageFreshnessLine")!.IsVisible);
            Assert.False(host.Window.FindControl<Border>("UsageFailurePanel")!.IsVisible);
        }
        finally
        {
            host.Window.Close();
            host.UsagePageReadPort!.Release(() => new DailyUsage(default, default, [], 0));
            host.ViewModel.Dispose();
        }
    }

    [AvaloniaFact]
    public void Narrow_japanese_combination_keeps_the_page_at_520_and_localizes_the_state()
    {
        var host = QuotaRingsHost.CreateHost(["--main", "--dark", "--narrow", "--ja", "--usage-page", "delayed"]);
        try
        {
            Assert.Equal(520d, host.Window.Width);
            Assert.Equal(740d, host.Window.Height);
            Assert.True(((MainWindow)host.Window).IsCompactLayout);
            Assert.Equal(AppPage.Usage, host.ViewModel.CurrentPage);
            Assert.Equal(new UiCopy(UiLanguage.Japanese).UsageState("Ready"), host.ViewModel.UsageStateText);
            Assert.Equal(new UiCopy(UiLanguage.Japanese).UsageDelayed, host.ViewModel.UsageDelayedText);

            host.Window.Show();
            host.Window.UpdateLayout();
            Assert.True(host.Window.FindControl<Grid>("UsagePage")!.IsVisible);
        }
        finally { host.Window.Close(); host.ViewModel.Dispose(); }
    }

    [AvaloniaFact]
    public void Light_theme_combination_still_opens_the_loaded_usage_page()
    {
        var host = QuotaRingsHost.CreateHost(["--main", "--light", "--usage-page", "empty"]);
        try
        {
            Assert.Equal("Light", host.ViewModel.CurrentSettingsForTests.Theme);
            Assert.Equal(AppPage.Usage, host.ViewModel.CurrentPage);
            Assert.Equal(new UiCopy(UiLanguage.English).UsageState("Empty"), host.ViewModel.UsageStateText);
        }
        finally { host.Window.Close(); host.ViewModel.Dispose(); }
    }

    [AvaloniaFact]
    public void Usage_page_does_not_require_the_main_argument()
    {
        var host = QuotaRingsHost.CreateHost(["--usage-page", "ready"]);
        try
        {
            Assert.IsType<MainWindow>(host.Window);
            Assert.Equal(1100d, host.Window.Width);
            Assert.Equal(740d, host.Window.Height);
            Assert.Equal(AppPage.Usage, host.ViewModel.CurrentPage);
        }
        finally { host.Window.Close(); host.ViewModel.Dispose(); }
    }

    [Fact]
    public void Unknown_usage_page_state_is_refused_instead_of_rendering_a_wrong_state()
    {
        var error = Assert.Throws<ArgumentException>(() => QuotaRingsHost.CreateHost(["--main", "--dark", "--usage-page", "overdue"]));
        Assert.Contains("--usage-page", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_usage_page_value_is_refused()
    {
        Assert.Throws<ArgumentException>(() => QuotaRingsHost.CreateHost(["--main", "--dark", "--usage-page"]));
    }

    [Fact]
    public void Usage_page_argument_is_refused_for_the_widget_window()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            QuotaRingsHost.CreateHost(["--widget", "--light", "--usage-page", "ready"]));
        Assert.Contains("--usage-page", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Usage_page_argument_is_refused_with_the_settings_page()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            QuotaRingsHost.CreateHost(["--main", "--dark", "--settings", "--usage-page", "ready"]));
        Assert.Contains("--usage-page", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Usage_page_argument_is_refused_with_the_connection_state_argument()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            QuotaRingsHost.CreateHost(["--main", "--dark", "--usage-state", "configured", "--usage-page", "ready"]));
        Assert.Contains("--usage-page", error.Message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Existing_arguments_still_open_the_dashboard_without_a_usage_page_state()
    {
        var host = QuotaRingsHost.CreateHost(["--main", "--dark"]);
        try
        {
            Assert.Equal(AppPage.Dashboard, host.ViewModel.CurrentPage);
            Assert.Null(host.UsagePageState);
            Assert.Null(host.UsagePageReadPort);
        }
        finally { host.Window.Close(); host.ViewModel.Dispose(); }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// Layout and behavior tests for the usage page's status surface: the freshness line,
/// the delayed-data banner and the typed failure banner with its retry / settings detour.
///
/// The surface must never let retained numbers read as current — delayed data stays
/// visible but is labelled, a failed refresh keeps the previous rows and total while a
/// stale banner explains them, and a first failure names its class. Every state is
/// pinned at 520px in both languages with a deliberately wide font: bounds alone cannot
/// prove a banner is readable, so the widest rendered text line is also measured against
/// its element, the way the settings card is checked.
/// </summary>
public sealed class UsageStatusSurfaceTests
{
    private const double WindowWidth = 520;
    private const double Tolerance = 0.5;
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
    private static readonly DateOnly From = new(2026, 10, 22);
    private static readonly DateOnly To = new(2026, 11, 20);
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 5, 0, 0, TimeSpan.Zero);

    public enum SurfaceState { Ready, Delayed, Stale, StaleAndDelayed, ErrorTransient, ErrorCredentialMissing, Unconfigured, Empty, Loading }

    private static DailyUsage Usage(long totalTokens, params UsageCollectorReceipt[] collectors) =>
        new(From, To, [new UsageDay(To, "collector-a", totalTokens)], totalTokens, collectors);

    private static UsageCollectorReceipt Reported(DateTimeOffset receivedAt) => new("collector-a", 1, receivedAt);

    [AvaloniaFact]
    public async Task The_freshness_line_sits_quietly_beside_the_total_and_wraps_in_both_languages()
    {
        var port = new ScriptedUsageReadPort(() => Usage(17, Reported(Now.AddHours(-1))));
        var window = await CreateStateAsync(SurfaceState.Ready, port);
        try
        {

            var line = window.FindControl<TextBlock>("UsageFreshnessLine")!;
            Assert.True(line.IsVisible);
            Assert.Contains("muted", line.Classes);
            Assert.Equal(TextWrapping.Wrap, line.TextWrapping);
            Assert.Equal(new UiCopy(UiLanguage.English).UsageFreshnessLastReceived(Now.AddHours(-1).Add(Tokyo.GetUtcOffset(Now))), line.Text);
            var card = window.FindControl<Border>("UsageDailyCard")!;
            var lineTop = line.TranslatePoint(default, card)!.Value.Y;
            var totalTop = window.FindControl<Grid>("UsageTotalRow")!.TranslatePoint(default, card)!.Value.Y;
            Assert.True(lineTop > totalTop,
                "The freshness line must sit below the total, next to it in reading order.");

            window.ViewModel.Language = UiLanguage.Japanese;
            window.UpdateLayout();
            Assert.True(line.IsVisible);
            Assert.Equal(new UiCopy(UiLanguage.Japanese).UsageFreshnessLastReceived(Now.AddHours(-1).Add(Tokyo.GetUtcOffset(Now))), line.Text);
            AssertEveryTextStaysInsideTheDailyCard(window);
        }
        finally
        {
            window.Close();
            window.ViewModel.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Delayed_data_is_labelled_in_warning_language_while_the_numbers_stay_visible()
    {
        var port = new ScriptedUsageReadPort(() => Usage(17, Reported(Now.AddHours(-13))));
        var window = await CreateStateAsync(SurfaceState.Delayed, port);
        try
        {

            var banner = window.FindControl<Border>("UsageDelayedBanner")!;
            Assert.True(banner.IsVisible);
            Assert.Contains("delayed", banner.Classes);
            Assert.True(window.FindControl<PathIcon>("UsageDelayedIcon")!.IsVisible);
            Assert.Equal(new UiCopy(UiLanguage.English).UsageDelayed, window.FindControl<TextBlock>("UsageDelayedTextBlock")!.Text);
            Assert.Equal(TextWrapping.Wrap, window.FindControl<TextBlock>("UsageDelayedTextBlock")!.TextWrapping);

            // The retained numbers remain on screen and the total is never hidden by the banner.
            Assert.True(window.FindControl<TextBlock>("UsageTotal")!.IsVisible);
            Assert.Equal("17", window.ViewModel.UsageTotalText);
            AssertEveryTextStaysInsideTheDailyCard(window);

            window.ViewModel.Language = UiLanguage.Japanese;
            window.UpdateLayout();
            Assert.Equal(new UiCopy(UiLanguage.Japanese).UsageDelayed, window.FindControl<TextBlock>("UsageDelayedTextBlock")!.Text);
            Assert.True(banner.IsVisible);
        }
        finally
        {
            window.Close();
            window.ViewModel.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task A_stale_surface_shows_the_failure_kind_with_a_prominent_retry_and_keeps_every_previous_value()
    {
        var port = new ScriptedUsageReadPort(
            () => Usage(17, Reported(Now.AddHours(-1))),
            () => throw new UsageReadException(UsageReadFailure.Timeout, "offline"),
            () => Usage(17, Reported(Now.AddHours(-1))));
        var window = await CreateStateAsync(SurfaceState.Stale, port, loadSecondRead: true);
        try
        {

            var panel = window.FindControl<Border>("UsageFailurePanel")!;
            Assert.True(panel.IsVisible);
            Assert.Contains("stale", panel.Classes);
            Assert.True(window.FindControl<PathIcon>("UsageFailureStaleIcon")!.IsVisible);
            Assert.False(window.FindControl<PathIcon>("UsageFailureErrorIcon")!.IsVisible);
            Assert.Equal(new UiCopy(UiLanguage.English).UsageFailure(UsageFailureKind.Transient),
                window.FindControl<TextBlock>("UsageFailureTextBlock")!.Text);

            var retry = window.FindControl<Button>("UsageRetryButton")!;
            Assert.True(retry.IsVisible);
            Assert.Contains("reauth-action", retry.Classes);
            Assert.True(retry.Focusable);
            Assert.True(retry.Focus());
            Assert.True(retry.IsFocused, "The retry action must be reachable by keyboard focus.");

            // A transient failure offers no settings detour; the top refresh stays available too.
            Assert.False(window.FindControl<Button>("UsageOpenSettingsButton")!.IsVisible);
            Assert.True(window.ViewModel.CanRefreshUsage);

            // Nothing the failed refresh touches has disappeared: rows, total and freshness stay.
            Assert.True(window.FindControl<TextBlock>("UsageTotal")!.IsVisible);
            Assert.Single(window.ViewModel.UsageRows);
            Assert.True(window.FindControl<TextBlock>("UsageFreshnessLine")!.IsVisible);

            window.ViewModel.Language = UiLanguage.Japanese;
            window.UpdateLayout();
            Assert.Equal(new UiCopy(UiLanguage.Japanese).UsageFailure(UsageFailureKind.Transient),
                window.FindControl<TextBlock>("UsageFailureTextBlock")!.Text);
            AssertEveryTextStaysInsideTheDailyCard(window);

            // The retry button wires to the same refresh path and clears the stale marker.
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.ViewModel.UsageLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(panel.IsVisible);
            Assert.Equal(UsageFailureKind.None, window.ViewModel.UsageFailure);
            Assert.Equal(3, port.Calls);
        }
        finally
        {
            window.Close();
            window.ViewModel.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task A_stale_and_delayed_surface_carries_both_labels_without_conflict()
    {
        var port = new ScriptedUsageReadPort(
            () => Usage(17, Reported(Now.AddHours(-13))),
            () => throw new UsageReadException(UsageReadFailure.Timeout, "offline"));
        var window = await CreateStateAsync(SurfaceState.StaleAndDelayed, port, loadSecondRead: true);
        try
        {

            Assert.True(window.FindControl<Border>("UsageDelayedBanner")!.IsVisible);
            var panel = window.FindControl<Border>("UsageFailurePanel")!;
            Assert.True(panel.IsVisible);
            Assert.Equal(new UiCopy(UiLanguage.English).UsageDelayed, window.FindControl<TextBlock>("UsageDelayedTextBlock")!.Text);
            Assert.Equal(new UiCopy(UiLanguage.English).UsageFailure(UsageFailureKind.Transient),
                window.FindControl<TextBlock>("UsageFailureTextBlock")!.Text);

            // The labelled numbers are still the previous read's numbers.
            Assert.True(window.FindControl<TextBlock>("UsageTotal")!.IsVisible);
            Assert.Equal("17", window.ViewModel.UsageTotalText);
            Assert.True(window.FindControl<TextBlock>("UsageFreshnessLine")!.IsVisible);
            AssertEveryTextStaysInsideTheDailyCard(window);

            window.ViewModel.Language = UiLanguage.Japanese;
            window.UpdateLayout();
            Assert.True(window.FindControl<Border>("UsageDelayedBanner")!.IsVisible);
            Assert.True(panel.IsVisible);
            Assert.Equal(new UiCopy(UiLanguage.Japanese).UsageDelayed, window.FindControl<TextBlock>("UsageDelayedTextBlock")!.Text);
        }
        finally
        {
            window.Close();
            window.ViewModel.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(UsageFailureKind.Unauthorized, true)]
    [InlineData(UsageFailureKind.Forbidden, true)]
    [InlineData(UsageFailureKind.NotFound, false)]
    [InlineData(UsageFailureKind.InvalidResponse, false)]
    [InlineData(UsageFailureKind.Transient, false)]
    [InlineData(UsageFailureKind.Redirected, false)]
    [InlineData(UsageFailureKind.CredentialMissing, true)]
    public async Task A_first_failure_names_its_class_and_routes_credential_problems_to_settings(
        UsageFailureKind kind, bool expectsSettingsDetour)
    {
        var failure = kind switch
        {
            UsageFailureKind.Unauthorized => UsageReadFailure.Unauthorized,
            UsageFailureKind.Forbidden => UsageReadFailure.Forbidden,
            UsageFailureKind.NotFound => UsageReadFailure.NotFound,
            UsageFailureKind.InvalidResponse => UsageReadFailure.MalformedResponse,
            UsageFailureKind.Redirected => UsageReadFailure.RedirectRejected,
            UsageFailureKind.CredentialMissing => UsageReadFailure.CredentialUnavailable,
            _ => UsageReadFailure.Timeout,
        };
        var port = new ScriptedUsageReadPort(() => throw new UsageReadException(failure, "classified"));
        var window = await CreateStateAsync(SurfaceState.ErrorTransient, port);
        try
        {

            var panel = window.FindControl<Border>("UsageFailurePanel")!;
            Assert.True(panel.IsVisible);
            Assert.Contains("error", panel.Classes);
            Assert.True(window.FindControl<PathIcon>("UsageFailureErrorIcon")!.IsVisible);
            Assert.False(window.FindControl<PathIcon>("UsageFailureStaleIcon")!.IsVisible);
            Assert.Equal(new UiCopy(UiLanguage.English).UsageFailure(kind),
                window.FindControl<TextBlock>("UsageFailureTextBlock")!.Text);

            var settings = window.FindControl<Button>("UsageOpenSettingsButton")!;
            Assert.Equal(expectsSettingsDetour, settings.IsVisible);
            Assert.True(window.FindControl<Button>("UsageRetryButton")!.IsVisible);
            Assert.False(window.ViewModel.IsUsageStale);
            AssertEveryTextStaysInsideTheDailyCard(window);

            window.ViewModel.Language = UiLanguage.Japanese;
            window.UpdateLayout();
            Assert.Equal(new UiCopy(UiLanguage.Japanese).UsageFailure(kind),
                window.FindControl<TextBlock>("UsageFailureTextBlock")!.Text);
            Assert.Equal(expectsSettingsDetour, settings.IsVisible);

            if (expectsSettingsDetour)
            {
                settings.Focus();
                Assert.True(settings.IsFocused, "The settings detour must be reachable by keyboard focus.");
                settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(AppPage.Settings, window.ViewModel.CurrentPage);
                Assert.True(window.ViewModel.IsSettingsVisible);
            }
        }
        finally
        {
            window.Close();
            window.ViewModel.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task An_unconfigured_connection_shows_no_status_furniture_at_all()
    {
        var window = await CreateStateAsync(SurfaceState.Unconfigured, null);
        try
        {

            Assert.False(window.FindControl<Border>("UsageFailurePanel")!.IsVisible);
            Assert.False(window.FindControl<Border>("UsageDelayedBanner")!.IsVisible);
            Assert.False(window.FindControl<TextBlock>("UsageFreshnessLine")!.IsVisible);
            AssertEveryTextStaysInsideTheDailyCard(window);
        }
        finally
        {
            window.Close();
            window.ViewModel.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Loading_keeps_the_previous_values_on_screen_until_a_result_arrives()
    {
        var port = new GatedUsageReadPort(() => Usage(17, Reported(Now.AddHours(-1))));
        using var viewModel = CreateViewModel(port, UiLanguage.English, out _);
        viewModel.NavigateToUsage();
        await viewModel.UsageLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
        var window = ShowWindow(viewModel);
        try
        {
            // The first read is done; the second (the refresh) is what hangs in flight.
            var refresh = viewModel.RefreshUsageAsync();
            window.UpdateLayout();

            // The load keeps the rows, total and freshness on screen while it runs, and the
            // progress strip says so instead of leaving the surface silent.
            Assert.True(viewModel.IsUsageLoading);
            Assert.True(window.FindControl<ProgressBar>("UsageLoadingBar")!.IsVisible);
            Assert.True(window.FindControl<TextBlock>("UsageTotal")!.IsVisible);
            Assert.Equal("17", viewModel.UsageTotalText);
            Assert.Single(viewModel.UsageRows);
            Assert.True(window.FindControl<TextBlock>("UsageFreshnessLine")!.IsVisible);
            Assert.False(window.FindControl<Border>("UsageFailurePanel")!.IsVisible);

            port.Release(() => Usage(17, Reported(Now.AddHours(-1))));
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            window.UpdateLayout();
            Assert.False(viewModel.IsUsageLoading);
            Assert.True(window.FindControl<TextBlock>("UsageTotal")!.IsVisible);
        }
        finally
        {
            window.Close();
            port.Release(() => Usage(17, Reported(Now.AddHours(-1))));
            try { await viewModel.UsageLoadTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* already completed */ }
            viewModel.Dispose();
        }
    }

    public static IEnumerable<object[]> SurfaceStates()
    {
        foreach (var state in new[] { SurfaceState.Ready, SurfaceState.Delayed, SurfaceState.Stale, SurfaceState.StaleAndDelayed, SurfaceState.ErrorTransient, SurfaceState.ErrorCredentialMissing, SurfaceState.Unconfigured, SurfaceState.Empty, SurfaceState.Loading })
        {
            yield return new object[] { state, UiLanguage.English };
            yield return new object[] { state, UiLanguage.Japanese };
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(SurfaceStates))]
    public async Task Every_state_wraps_inside_the_daily_card_at_narrow_width_with_a_wide_font(
        SurfaceState state, UiLanguage language)
    {
        ScriptedUsageReadPort? scripted = state switch
        {
            SurfaceState.Ready => new ScriptedUsageReadPort(() => Usage(17, Reported(Now.AddHours(-1)))),
            SurfaceState.Delayed => new ScriptedUsageReadPort(() => Usage(17, Reported(Now.AddHours(-13)))),
            SurfaceState.Stale => new ScriptedUsageReadPort(() => Usage(17, Reported(Now.AddHours(-1))), () => throw new UsageReadException(UsageReadFailure.Timeout, "offline")),
            SurfaceState.StaleAndDelayed => new ScriptedUsageReadPort(() => Usage(17, Reported(Now.AddHours(-13))), () => throw new UsageReadException(UsageReadFailure.Timeout, "offline")),
            SurfaceState.ErrorTransient => new ScriptedUsageReadPort(() => throw new UsageReadException(UsageReadFailure.Timeout, "offline")),
            SurfaceState.ErrorCredentialMissing => new ScriptedUsageReadPort(() => throw new UsageReadException(UsageReadFailure.CredentialUnavailable, "missing")),
            SurfaceState.Empty => new ScriptedUsageReadPort(() => new DailyUsage(From, To, [], 0)),
            _ => null,
        };

        MainWindow? window;
        if (state == SurfaceState.Loading)
        {
            var gated = new GatedUsageReadPort(() => Usage(17, Reported(Now.AddHours(-1))));
            var viewModel = CreateViewModel(gated, language, out _);
            viewModel.NavigateToUsage();
            await viewModel.UsageLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
            window = ShowWindow(viewModel);
            var refresh = viewModel.RefreshUsageAsync();
            try
            {
                AssertSurface(state, language, window);
                AssertEveryTextStaysInsideTheDailyCard(window);
            }
            finally
            {
                window.Close();
                gated.Release(() => Usage(17, Reported(Now.AddHours(-1))));
                try { await refresh.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                viewModel.Dispose();
            }
            return;
        }

        if (state == SurfaceState.Unconfigured)
        {
            window = await CreateStateAsync(state, null, language: language);
        }
        else
        {
            window = await CreateStateAsync(state, scripted!, language: language);
            if (state is SurfaceState.Stale or SurfaceState.StaleAndDelayed)
                await window.ViewModel.RefreshUsageAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        try
        {
            window.UpdateLayout();
            AssertSurface(state, language, window);
            AssertEveryTextStaysInsideTheDailyCard(window);
        }
        finally
        {
            window.Close();
            window.ViewModel.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task The_error_status_line_stays_neutral_while_the_banner_names_the_problem()
    {
        // The status line is a state, not advice: "try again" would contradict a banner
        // that says the credential must be reviewed in settings. The banner owns the fix.
        var port = new ScriptedUsageReadPort(() => throw new UsageReadException(UsageReadFailure.Unauthorized, "classified"));
        var window = await CreateStateAsync(SurfaceState.ErrorTransient, port);
        try
        {
            Assert.Equal("Could not load usage.", window.FindControl<TextBlock>("UsageStatus")!.Text);
            Assert.DoesNotContain("Try again", window.FindControl<TextBlock>("UsageStatus")!.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new UiCopy(UiLanguage.English).UsageFailure(UsageFailureKind.Unauthorized),
                window.FindControl<TextBlock>("UsageFailureTextBlock")!.Text);
            Assert.True(window.FindControl<Button>("UsageRetryButton")!.IsVisible);

            window.ViewModel.Language = UiLanguage.Japanese;
            window.UpdateLayout();
            Assert.Equal("利用量を読み込めませんでした。", window.FindControl<TextBlock>("UsageStatus")!.Text);
            Assert.DoesNotContain("再試行", window.FindControl<TextBlock>("UsageStatus")!.Text, StringComparison.Ordinal);
            Assert.Equal(new UiCopy(UiLanguage.Japanese).UsageFailure(UsageFailureKind.Unauthorized),
                window.FindControl<TextBlock>("UsageFailureTextBlock")!.Text);
        }
        finally
        {
            window.Close();
            window.ViewModel.Dispose();
        }
    }

    public static IEnumerable<object?[]> BannerFonts()
    {
        // English copy on purpose: Japanese glyphs fall back to a CJK font whose tall
        // metrics would mask the font under test. The three cases are the theme default
        // (what CI resolves to a DejaVu-class sans), a deliberately wide serif, and the
        // exact CI repro: DejaVu Sans, whose first line measures only 16.3px at 14px.
        yield return new object?[] { null, "theme default" };
        yield return new object?[] { "DejaVu Serif", "deliberately wide metrics" };
        yield return new object?[] { "DejaVu Sans", "the CI font whose first line is 16.3px" };
    }

    [AvaloniaTheory]
    [MemberData(nameof(BannerFonts))]
    public async Task Banner_icons_align_with_the_first_text_line_not_the_paragraph_middle(
        string? fontFamily, string why)
    {
        var port = new ScriptedUsageReadPort(
            () => Usage(17, Reported(Now.AddHours(-13))),
            () => throw new UsageReadException(UsageReadFailure.Timeout, "offline"));
        var window = await CreateStateAsync(SurfaceState.StaleAndDelayed, port, loadSecondRead: true);
        try
        {
            window.FindControl<Border>("UsageDailyCard")!.SetValue(TextElement.FontFamilyProperty,
                fontFamily is null ? FontFamily.Default : new FontFamily(fontFamily));
            window.UpdateLayout();

            AssertFirstLineAligned(window, "UsageFailurePanel", "UsageFailureStaleIcon", "UsageFailureTextBlock", fontFamily, why);
            AssertFirstLineAligned(window, "UsageDelayedBanner", "UsageDelayedIcon", "UsageDelayedTextBlock", fontFamily, why);
            AssertEveryTextStaysInsideTheDailyCard(window);
        }
        finally
        {
            window.Close();
            window.ViewModel.Dispose();
        }
    }

    private static void AssertFirstLineAligned(
        MainWindow window, string bannerName, string iconName, string textName, string? fontFamily, string why)
    {
        var banner = window.FindControl<Border>(bannerName)!;
        var icon = window.FindControl<PathIcon>(iconName)!;
        var text = window.FindControl<TextBlock>(textName)!;
        var iconTop = icon.TranslatePoint(default, banner)!.Value.Y;
        var textTop = text.TranslatePoint(default, banner)!.Value.Y;
        var firstLineHeight = text.TextLayout.TextLines[0].Height;
        // No slack: a margin of a few pixels flips with font metrics (DejaVu Sans renders
        // a 16.3px first line, others render 17-19px). The icon must sit inside the first
        // line's box and be centered on it, whatever font lays the text out ({why}).
        Assert.True(iconTop >= textTop,
            $"[{fontFamily ?? "default"}] The '{iconName}' icon floats above the first text line by {textTop - iconTop:0.#}px.");
        Assert.True(iconTop + icon.Bounds.Height <= textTop + firstLineHeight,
            $"[{fontFamily ?? "default"}] The '{iconName}' icon ends at {iconTop + icon.Bounds.Height - textTop:0.#}px into the text, beyond the first line ({firstLineHeight:0.#}px): it drifts toward the paragraph middle.");
        var iconCenter = iconTop + icon.Bounds.Height / 2;
        var lineCenter = textTop + firstLineHeight / 2;
        Assert.True(Math.Abs(iconCenter - lineCenter) <= 3,
            $"[{fontFamily ?? "default"}] The '{iconName}' icon center sits {iconCenter - lineCenter:+0.#;-0.#}px off the first line's center ({lineCenter - textTop:0.#}px).");
    }

    [AvaloniaFact]
    public async Task The_settings_detour_reads_as_a_real_button_but_stays_secondary_to_retry()
    {
        var port = new ScriptedUsageReadPort(() => throw new UsageReadException(UsageReadFailure.CredentialUnavailable, "missing"));
        var window = await CreateStateAsync(SurfaceState.ErrorTransient, port);
        try
        {
            var retry = window.FindControl<Button>("UsageRetryButton")!;
            var settings = window.FindControl<Button>("UsageOpenSettingsButton")!;

            // Green text alone did not read as clickable: the detour carries a visible
            // outline like a button, while staying visually quieter than the retry action.
            Assert.True(settings.BorderThickness.Left >= 1, "The settings detour needs a visible border to read as a button.");
            Assert.NotEqual(FontWeight.SemiBold, settings.FontWeight);
            Assert.Equal(FontWeight.SemiBold, retry.FontWeight);

            // Order in the row: the retry action leads, the detour follows it.
            var actions = window.FindControl<WrapPanel>("UsageFailureActions")!;
            Assert.True(actions.Children.IndexOf(retry) < actions.Children.IndexOf(settings));
        }
        finally
        {
            window.Close();
            window.ViewModel.Dispose();
        }
    }

    private static void AssertSurface(SurfaceState state, UiLanguage language, MainWindow window)
    {
        var copy = new UiCopy(language);
        var delayed = window.FindControl<Border>("UsageDelayedBanner")!;
        var failure = window.FindControl<Border>("UsageFailurePanel")!;
        var freshness = window.FindControl<TextBlock>("UsageFreshnessLine")!;
        switch (state)
        {
            case SurfaceState.Ready:
                Assert.True(freshness.IsVisible);
                Assert.False(delayed.IsVisible);
                Assert.False(failure.IsVisible);
                break;
            case SurfaceState.Delayed:
                Assert.True(delayed.IsVisible);
                Assert.Equal(copy.UsageDelayed, window.FindControl<TextBlock>("UsageDelayedTextBlock")!.Text);
                Assert.False(failure.IsVisible);
                Assert.True(window.FindControl<TextBlock>("UsageTotal")!.IsVisible);
                break;
            case SurfaceState.Stale:
                Assert.True(failure.IsVisible);
                Assert.Contains("stale", failure.Classes);
                Assert.False(delayed.IsVisible);
                Assert.Equal(copy.UsageFailure(UsageFailureKind.Transient), window.FindControl<TextBlock>("UsageFailureTextBlock")!.Text);
                Assert.True(window.FindControl<TextBlock>("UsageTotal")!.IsVisible);
                break;
            case SurfaceState.StaleAndDelayed:
                Assert.True(delayed.IsVisible);
                Assert.True(failure.IsVisible);
                Assert.Equal(copy.UsageDelayed, window.FindControl<TextBlock>("UsageDelayedTextBlock")!.Text);
                Assert.True(window.FindControl<TextBlock>("UsageTotal")!.IsVisible);
                break;
            case SurfaceState.ErrorTransient or SurfaceState.ErrorCredentialMissing:
                Assert.True(failure.IsVisible);
                Assert.Contains("error", failure.Classes);
                Assert.Equal(copy.UsageState("Error"), window.FindControl<TextBlock>("UsageStatus")!.Text);
                Assert.False(window.FindControl<TextBlock>("UsageTotal")!.IsVisible);
                break;
            case SurfaceState.Unconfigured:
                Assert.Equal(copy.UsageState("Unconfigured"), window.FindControl<TextBlock>("UsageStatus")!.Text);
                break;
            case SurfaceState.Empty:
                Assert.Equal(copy.UsageState("Empty"), window.FindControl<TextBlock>("UsageStatus")!.Text);
                Assert.False(delayed.IsVisible);
                Assert.False(failure.IsVisible);
                break;
            case SurfaceState.Loading:
                Assert.Equal(copy.UsageState("Loading"), window.FindControl<TextBlock>("UsageStatus")!.Text);
                Assert.True(window.FindControl<ProgressBar>("UsageLoadingBar")!.IsVisible);
                Assert.False(failure.IsVisible);
                break;
        }
    }

    private static async Task<MainWindow> CreateStateAsync(
        SurfaceState state,
        ScriptedUsageReadPort? port,
        UiLanguage language = UiLanguage.English,
        bool loadSecondRead = false)
    {
        var viewModel = CreateViewModel(port, language, out _);
        viewModel.NavigateToUsage();
        await viewModel.UsageLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
        if (loadSecondRead)
            await viewModel.RefreshUsageAsync().WaitAsync(TimeSpan.FromSeconds(5));
        return ShowWindow(viewModel);
    }

    private static MainViewModel CreateViewModel(IUsageReadPort? port, UiLanguage language, out MutableTimeProvider clock)
    {
        clock = new MutableTimeProvider(Now);
        var viewModel = port is null
            ? new MainViewModel(new EmptyDashboardSource(), timeProvider: clock, uiDispatcher: new ImmediateUiDispatcher(), localTimeZone: Tokyo)
            : new MainViewModel(new EmptyDashboardSource(), timeProvider: clock, uiDispatcher: new ImmediateUiDispatcher(), usageReadPort: port, localTimeZone: Tokyo);
        viewModel.Language = language;
        return viewModel;
    }

    private static MainWindow ShowWindow(MainViewModel viewModel)
    {
        var window = new MainWindow(viewModel) { Width = WindowWidth };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static void AssertEveryTextStaysInsideTheDailyCard(MainWindow window)
    {
        window.UpdateLayout();
        var card = window.FindControl<Border>("UsageDailyCard")!;
        card.SetValue(TextElement.FontFamilyProperty, new FontFamily("DejaVu Serif"));
        window.UpdateLayout();

        var innerRight = card.Bounds.Width - card.BorderThickness.Right - card.Padding.Right;
        Assert.True(innerRight > 100, $"The card is unexpectedly narrow: inner edge at {innerRight:0.#}px.");

        foreach (var block in card.GetVisualDescendants().OfType<TextBlock>()
                     .Where(block => block.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(block.Text)))
        {
            var rightInCard = block.TranslatePoint(new Point(block.Bounds.Width, block.Bounds.Height), card)?.X
                ?? throw new InvalidOperationException($"'{block.Text}' is not attached to the card visual tree.");
            Assert.True(rightInCard <= innerRight + Tolerance,
                $"'{block.Text}' ends at {rightInCard:0.#}px but the card's inner edge is {innerRight:0.#}px; the text runs past the card.");

            var lines = block.TextLayout.TextLines;
            Assert.True(lines.Count > 0, $"'{block.Text}' has no measured text lines.");
            var widestLine = lines.Max(line => line.Width);
            Assert.True(widestLine <= block.Bounds.Width + 1,
                $"The widest line of '{block.Text}' measures {widestLine:0.#}px but its element is only {block.Bounds.Width:0.#}px wide; the glyphs would clip even though the element fits.");
        }
    }

    private sealed class ScriptedUsageReadPort(params Func<DailyUsage>[] outcomes) : IUsageReadPort
    {
        public int Calls { get; private set; }

        public Task<DailyUsage> ReadDailyAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken)
        {
            var index = Math.Min(Calls, outcomes.Length - 1);
            Calls++;
            return Task.FromResult(outcomes[index]());
        }
    }

    private sealed class GatedUsageReadPort(Func<DailyUsage> success) : IUsageReadPort
    {
        private readonly TaskCompletionSource<DailyUsage> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool first = true;

        public Task<DailyUsage> ReadDailyAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref first, false)) return Task.FromResult(success());
            return gate.Task;
        }

        public void Release(Func<DailyUsage> next) => gate.TrySetResult(next());
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void SetUtcNow(DateTimeOffset value) => current = value;
    }
}

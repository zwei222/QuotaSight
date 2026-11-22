using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuotaSight.UI;
using QuotaSight.UI.Tests.Support;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// Keeps this class out of the parallel render wave: its windows include an indeterminate
/// progress bar while a synthetic save is pending, and the pixel assertions elsewhere in this
/// suite are timing sensitive. Serializing here keeps that pre-existing behaviour untouched.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class QuotaRingsSettingsHostCollection
{
    public const string Name = "quota-rings-settings-host";
}

/// <summary>
/// Contract tests for the capture host's settings-page arguments. Every credential value and
/// endpoint here is a fixture literal and every store is the host's in-memory fake.</summary>
[Collection(QuotaRingsSettingsHostCollection.Name)]
public sealed class QuotaRingsSettingsHostTests
{
    [AvaloniaFact]
    public void Settings_argument_opens_the_settings_page_at_the_main_window_size()
    {
        var host = QuotaRingsHost.CreateHost(["--main", "--dark", "--settings", "--usage-state", "configured"]);
        try
        {
            Assert.Equal(AppPage.Settings, host.ViewModel.CurrentPage);
            Assert.True(host.ViewModel.IsSettingsVisible);
            Assert.IsType<MainWindow>(host.Window);
            Assert.Equal(1100d, host.Window.Width);
            Assert.Equal(740d, host.Window.Height);

            host.Window.Show();
            host.Window.UpdateLayout();

            Assert.True(host.Window.FindControl<Grid>("SettingsPage")!.IsVisible);
            Assert.True(host.Window.FindControl<Border>("UsageConnectionCard")!.IsVisible);
            Assert.Equal(QuotaRingsHost.SyntheticUsageEndpoint, host.Window.FindControl<TextBox>("UsageEndpointBox")!.Text);
        }
        finally { host.Window.Close(); host.ViewModel.Dispose(); }
    }

    [AvaloniaFact]
    public void Narrow_argument_shrinks_the_main_window_to_520x740_and_keeps_the_settings_page()
    {
        var host = QuotaRingsHost.CreateHost(["--main", "--dark", "--settings", "--narrow", "--usage-state", "configured"]);
        try
        {
            Assert.Equal(520d, host.Window.Width);
            Assert.Equal(740d, host.Window.Height);
            Assert.True(((MainWindow)host.Window).IsCompactLayout);
            Assert.Equal(AppPage.Settings, host.ViewModel.CurrentPage);

            host.Window.Show();
            host.Window.UpdateLayout();

            var card = host.Window.FindControl<Border>("UsageConnectionCard")!;
            var scroll = host.Window.FindControl<ScrollViewer>("ContentScroll")!;
            Assert.True(card.Bounds.Width > 0, "The usage connection card must be laid out.");
            Assert.True(card.Bounds.Width <= scroll.Viewport.Width + 0.5,
                $"The card is {card.Bounds.Width:0.#}px wide but the viewport is only {scroll.Viewport.Width:0.#}px.");
            Assert.True(host.Window.FindControl<TextBox>("UsageEndpointBox")!.Bounds.Width > 0);
        }
        finally { host.Window.Close(); host.ViewModel.Dispose(); }
    }

    [AvaloniaFact]
    public void Existing_main_and_widget_arguments_keep_their_page_and_size()
    {
        var main = QuotaRingsHost.CreateHost(["--main", "--dark"]);
        try
        {
            Assert.IsType<MainWindow>(main.Window);
            Assert.Equal(1100d, main.Window.Width);
            Assert.Equal(740d, main.Window.Height);
            Assert.Equal(AppPage.Dashboard, main.ViewModel.CurrentPage);
            Assert.Null(main.UsageState);
            Assert.Null(main.UsageCredentialStore);
        }
        finally { main.Window.Close(); main.ViewModel.Dispose(); }

        var widget = QuotaRingsHost.CreateHost(["--widget", "--light"]);
        try
        {
            Assert.IsType<CompactQuotaWindow>(widget.Window);
            Assert.Equal(410d, widget.Window.Width);
            Assert.Equal(620d, widget.Window.Height);
            Assert.Equal(AppPage.Dashboard, widget.ViewModel.CurrentPage);
        }
        finally { widget.Window.Close(); widget.ViewModel.Dispose(); }
    }

    [AvaloniaTheory]
    [InlineData("unconfigured", UsageConnectionState.Unconfigured, UsageConnectionError.None, false)]
    [InlineData("configured", UsageConnectionState.Configured, UsageConnectionError.None, true)]
    [InlineData("session-only", UsageConnectionState.SessionOnly, UsageConnectionError.None, true)]
    [InlineData("store-unavailable", UsageConnectionState.StoreUnavailable, UsageConnectionError.CredentialStoreUnavailable, false)]
    [InlineData("invalid-endpoint", UsageConnectionState.Unconfigured, UsageConnectionError.NotHttps, false)]
    public void Usage_state_argument_builds_the_expected_connection_state(
        string state, UsageConnectionState expected, UsageConnectionError expectedError, bool credentialStored)
    {
        var host = QuotaRingsHost.CreateHost(["--main", "--dark", "--settings", "--usage-state", state]);
        try
        {
            Assert.Equal(state, host.UsageState);
            Assert.Equal(expected, host.ViewModel.UsageConnectionState);
            Assert.Equal(expectedError, host.ViewModel.UsageConnectionError);
            Assert.Equal(credentialStored, host.ViewModel.HasUsageCredential);
            Assert.Equal(expectedError is not UsageConnectionError.None, host.ViewModel.HasUsageConnectionError);
            Assert.Equal(credentialStored ? 2 : 0, host.UsageCredentialStore!.Count);
            Assert.Equal(AppPage.Settings, host.ViewModel.CurrentPage);
        }
        finally { host.Window.Close(); host.ViewModel.Dispose(); }
    }

    [AvaloniaFact]
    public void Configured_state_shows_the_endpoint_and_never_the_credential_values()
    {
        var host = QuotaRingsHost.CreateHost(["--main", "--dark", "--settings", "--usage-state", "configured"]);
        try
        {
            var store = host.UsageCredentialStore!;
            Assert.Equal(QuotaRingsHost.SyntheticUsageClientId, store.Peek(StoreUsageCredentialProvider.ClientIdAccount));
            Assert.Equal(QuotaRingsHost.SyntheticUsageClientSecret, store.Peek(StoreUsageCredentialProvider.ClientSecretAccount));
            Assert.Equal(QuotaRingsHost.SyntheticUsageEndpoint, host.ViewModel.UsageApiEndpointText);
            Assert.True(host.ViewModel.IsUsageConnectionConfigured);

            host.Window.Show();
            host.Window.UpdateLayout();

            Assert.Equal(QuotaRingsHost.SyntheticUsageEndpoint, host.Window.FindControl<TextBox>("UsageEndpointBox")!.Text);
            Assert.Equal(string.Empty, host.Window.FindControl<TextBox>("UsageClientIdBox")!.Text);
            Assert.Equal(string.Empty, host.Window.FindControl<TextBox>("UsageClientSecretBox")!.Text);
            Assert.Contains("configured", host.Window.FindControl<Border>("UsageConnectionStatusPanel")!.Classes);
            Assert.False(host.Window.FindControl<TextBlock>("UsageConnectionValidationText")!.IsVisible);
        }
        finally { host.Window.Close(); host.ViewModel.Dispose(); }
    }

    [AvaloniaFact]
    public void Invalid_endpoint_state_keeps_the_rejected_input_and_shows_fixed_error_copy()
    {
        var host = QuotaRingsHost.CreateHost(["--main", "--dark", "--settings", "--usage-state", "invalid-endpoint"]);
        try
        {
            host.Window.Show();
            host.Window.UpdateLayout();

            var validation = host.Window.FindControl<TextBlock>("UsageConnectionValidationText")!;
            Assert.True(validation.IsVisible);
            Assert.Equal(new UiCopy(UiLanguage.English).UsageConnectionErrorText(UsageConnectionError.NotHttps), validation.Text);
            Assert.DoesNotContain("usage.example.test", validation.Text ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(QuotaRingsHost.SyntheticUsageInvalidEndpoint, host.Window.FindControl<TextBox>("UsageEndpointBox")!.Text);
            Assert.Equal(0, host.UsageCredentialStore!.Count);
        }
        finally { host.Window.Close(); host.ViewModel.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Saving_state_freezes_on_the_progress_panel_with_the_store_write_pending()
    {
        var host = QuotaRingsHost.CreateHost(["--main", "--dark", "--settings", "--usage-state", "saving"]);
        try
        {
            host.Window.Show();
            host.Window.UpdateLayout();
            await host.UsageCredentialStore!.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.False(host.ViewModel.HasUsageCredential);
            Assert.Equal(UsageConnectionState.Unconfigured, host.ViewModel.UsageConnectionState);
            Assert.Equal(0, host.UsageCredentialStore.Count);

            Assert.True(host.Window.FindControl<StackPanel>("UsageConnectionBusyPanel")!.IsVisible);
            Assert.True(host.Window.FindControl<ProgressBar>("UsageConnectionProgressBar")!.IsIndeterminate);
            Assert.Contains("Saving", host.Window.FindControl<TextBlock>("UsageConnectionBusyText")!.Text, StringComparison.OrdinalIgnoreCase);
            Assert.False(host.Window.FindControl<Button>("UsageConnectionSaveButton")!.IsEnabled);
            Assert.False(host.Window.FindControl<Button>("UsageConnectionRemoveButton")!.IsEnabled);
            Assert.False(host.Window.FindControl<TextBox>("UsageEndpointBox")!.IsEnabled);
            Assert.False(host.Window.FindControl<TextBox>("UsageClientIdBox")!.IsEnabled);
            Assert.False(host.Window.FindControl<TextBox>("UsageClientSecretBox")!.IsEnabled);
        }
        finally { host.Window.Close(); host.ViewModel.Dispose(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Usage_connection_buttons_keep_a_gap_and_stay_inside_the_card(bool narrow)
    {
        var args = new List<string> { "--main", "--dark", "--settings", "--usage-state", "configured" };
        if (narrow)
            args.Add("--narrow");
        var host = QuotaRingsHost.CreateHost([.. args]);
        try
        {
            host.Window.Show();
            host.Window.UpdateLayout();

            var row = host.Window.FindControl<WrapPanel>("UsageConnectionActions")!;
            var save = host.Window.FindControl<Button>("UsageConnectionSaveButton")!;
            var remove = host.Window.FindControl<Button>("UsageConnectionRemoveButton")!;
            Assert.Contains("danger-outline", remove.Classes);

            var saveTopLeft = save.TranslatePoint(new Point(0, 0), row)!.Value;
            var removeTopLeft = remove.TranslatePoint(new Point(0, 0), row)!.Value;
            Assert.True(saveTopLeft.X + save.Bounds.Width <= row.Bounds.Width + 0.5,
                $"The save button ends at {saveTopLeft.X + save.Bounds.Width:0.#}px but the row is only {row.Bounds.Width:0.#}px wide.");
            Assert.True(removeTopLeft.X + remove.Bounds.Width <= row.Bounds.Width + 0.5,
                $"The remove button ends at {removeTopLeft.X + remove.Bounds.Width:0.#}px but the row is only {row.Bounds.Width:0.#}px wide.");

            var sameRow = Math.Abs(saveTopLeft.Y - removeTopLeft.Y) < 0.5;
            if (sameRow)
            {
                var horizontalGap = removeTopLeft.X > saveTopLeft.X
                    ? removeTopLeft.X - (saveTopLeft.X + save.Bounds.Width)
                    : saveTopLeft.X - (removeTopLeft.X + remove.Bounds.Width);
                Assert.True(horizontalGap >= 8,
                    $"The two connection buttons touch: {horizontalGap:0.#}px between them at width {row.Bounds.Width:0.#}.");
            }
            else
            {
                var top = Math.Min(saveTopLeft.Y, removeTopLeft.Y);
                var lower = saveTopLeft.Y > removeTopLeft.Y ? save : remove;
                var verticalGap = lower.TranslatePoint(new Point(0, 0), row)!.Value.Y - (top + save.Bounds.Height);
                Assert.True(verticalGap >= 4,
                    $"The wrapped button rows touch: {verticalGap:0.#}px between them.");
            }
        }
        finally { host.Window.Close(); host.ViewModel.Dispose(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saving_state_names_the_operation_in_the_status_banner(bool japanese)
    {
        var args = new List<string> { "--main", "--dark", "--settings", "--usage-state", "saving" };
        if (japanese)
            args.Add("--ja");
        var host = QuotaRingsHost.CreateHost([.. args]);
        try
        {
            host.Window.Show();
            host.Window.UpdateLayout();
            await host.UsageCredentialStore!.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            var banner = host.Window.FindControl<TextBlock>("UsageConnectionStatusText")!;
            var expected = new UiCopy(japanese ? UiLanguage.Japanese : UiLanguage.English).UsageConnectionSaving;
            Assert.Equal(expected, banner.Text);
            Assert.DoesNotContain("not configured", banner.Text ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("未設定", banner.Text ?? string.Empty, StringComparison.Ordinal);
        }
        finally { host.Window.Close(); host.ViewModel.Dispose(); }
    }

    [AvaloniaTheory]
    [InlineData("unconfigured")]
    [InlineData("configured")]
    [InlineData("session-only")]
    [InlineData("store-unavailable")]
    [InlineData("invalid-endpoint")]
    [InlineData("saving")]
    public async Task Rendered_settings_page_never_puts_the_synthetic_credentials_in_text(string state)
    {
        var host = QuotaRingsHost.CreateHost(["--main", "--dark", "--settings", "--usage-state", state]);
        try
        {
            host.Window.Show();
            host.Window.UpdateLayout();
            // The saving case deliberately leaves a store write pending. An async test body lets
            // the headless session complete instead of waiting on that still-running operation.
            await Task.Yield();

            var text = host.Window.GetVisualDescendants().OfType<TextBlock>()
                .Select(block => block.Text ?? string.Empty)
                .ToArray();
            Assert.All(text, value =>
            {
                Assert.DoesNotContain(QuotaRingsHost.SyntheticUsageClientId, value, StringComparison.Ordinal);
                Assert.DoesNotContain(QuotaRingsHost.SyntheticUsageClientSecret, value, StringComparison.Ordinal);
            });

            var secret = host.Window.FindControl<TextBox>("UsageClientSecretBox")!;
            Assert.NotEqual(default, secret.PasswordChar);
        }
        finally { host.Window.Close(); host.ViewModel.Dispose(); }
    }

    [AvaloniaTheory]
    [InlineData("unconfigured", false)]
    [InlineData("configured", false)]
    [InlineData("session-only", false)]
    [InlineData("store-unavailable", false)]
    [InlineData("invalid-endpoint", false)]
    [InlineData("saving", false)]
    [InlineData("unconfigured", true)]
    [InlineData("configured", true)]
    [InlineData("session-only", true)]
    [InlineData("store-unavailable", true)]
    [InlineData("invalid-endpoint", true)]
    [InlineData("saving", true)]
    public async Task Scroll_usage_argument_fits_the_card_inside_the_viewport_without_keyboard_focus(string state, bool narrow)
    {
        var args = new List<string> { "--main", "--dark", "--settings", "--usage-state", state, "--scroll-usage" };
        if (narrow)
            args.Add("--narrow");
        var host = QuotaRingsHost.CreateHost([.. args]);
        try
        {
            host.Window.Show();
            host.Window.UpdateLayout();
            // The host scrolls from a Dispatcher post after the first layout pass, which is what
            // an unattended capture needs: no Tab presses and no focus moves.
            Dispatcher.UIThread.RunJobs();

            var card = host.Window.FindControl<Border>("UsageConnectionCard")!;
            var scroll = host.Window.FindControl<ScrollViewer>("ContentScroll")!;
            var origin = card.TranslatePoint(new Point(0, 0), scroll);
            Assert.True(origin.HasValue, "The card must be connected to the scroll viewer.");
            var top = origin!.Value.Y;
            var bottom = top + card.Bounds.Height;
            Assert.True(scroll.Offset.Y > 0,
                $"{state} (narrow={narrow}): the page was not scrolled at all, so the card is still below the fold.");

            Assert.True(top >= -0.5, $"{state} (narrow={narrow}): the card top is at {top:0.#}px above the viewport.");
            Assert.True(bottom <= scroll.Viewport.Height + 0.5,
                $"{state} (narrow={narrow}): the card bottom is at {bottom:0.#}px but the viewport is {scroll.Viewport.Height:0.#}px.");

            if (state is "saving" or "invalid-endpoint" or "store-unavailable")
            {
                // These states carry the error copy or the progress strip at the bottom of the
                // card, so the capture must end with that edge visible.
                Assert.True(bottom >= scroll.Viewport.Height - 40,
                    $"{state}: expected the card bottom near the viewport bottom, got {bottom:0.#}px of {scroll.Viewport.Height:0.#}px.");
            }

            var focused = host.Window.FocusManager?.GetFocusedElement();
            Assert.DoesNotContain(card.GetVisualDescendants().OfType<Control>(),
                control => ReferenceEquals(control, focused));
            await Task.Yield();
        }
        finally { host.Window.Close(); host.ViewModel.Dispose(); }
    }

    [AvaloniaFact]
    public void Scroll_usage_argument_is_refused_without_the_settings_argument()
    {
        var error = Assert.Throws<InvalidOperationException>(() => QuotaRingsHost.CreateHost(["--main", "--dark", "--scroll-usage"]));
        Assert.Contains("--scroll-usage", error.Message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Scroll_usage_argument_is_refused_for_the_widget_window()
    {
        var error = Assert.Throws<InvalidOperationException>(() => QuotaRingsHost.CreateHost(["--widget", "--light", "--scroll-usage"]));
        Assert.Contains("--scroll-usage", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_usage_state_is_refused_instead_of_rendering_a_wrong_state()
    {
        var error = Assert.Throws<ArgumentException>(() => QuotaRingsHost.CreateHost(["--main", "--dark", "--usage-state", "unsaved"]));
        Assert.Contains("--usage-state", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_usage_state_value_is_refused()
    {
        Assert.Throws<ArgumentException>(() => QuotaRingsHost.CreateHost(["--main", "--dark", "--usage-state"]));
    }

    [Fact]
    public void Settings_argument_is_refused_for_the_widget_window()
    {
        Assert.Throws<InvalidOperationException>(() => QuotaRingsHost.CreateHost(["--widget", "--light", "--settings"]));
    }
}

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using QuotaSight.Application;
using QuotaSight.Infrastructure;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// Headless layout and focus tests for the Usage connection section of the settings page.
/// Every credential value used here is synthetic and every store is an in-memory fake.
/// </summary>
public sealed class UsageConnectionSettingsUiTests
{
    private const string Endpoint = "https://usage.example.test";
    private const string ClientId = "synthetic-ui-client-id";
    private const string ClientSecret = "synthetic-ui-client-secret";

    [AvaloniaFact]
    public void Settings_page_shows_a_masked_secret_field_without_any_copy_command()
    {
        using var scope = Scope.Create();
        var window = scope.Window;

        var endpoint = window.FindControl<TextBox>("UsageEndpointBox");
        var clientId = window.FindControl<TextBox>("UsageClientIdBox");
        var secret = window.FindControl<TextBox>("UsageClientSecretBox");
        var save = window.FindControl<Button>("UsageConnectionSaveButton");
        var remove = window.FindControl<Button>("UsageConnectionRemoveButton");

        Assert.NotNull(endpoint);
        Assert.NotNull(clientId);
        Assert.NotNull(secret);
        Assert.NotNull(save);
        Assert.NotNull(remove);
        Assert.NotEqual(default, secret!.PasswordChar);
        Assert.NotNull(secret.ContextMenu);
        Assert.Empty(secret.ContextMenu.Items);
        Assert.Contains("secret", AutomationProperties.GetName(secret), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("endpoint", AutomationProperties.GetName(endpoint!), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Client ID", AutomationProperties.GetName(clientId!), StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void Secret_field_swallows_copy_and_cut_keys_so_the_value_cannot_leave_the_mask()
    {
        using var scope = Scope.Create();
        var secret = scope.Window.FindControl<TextBox>("UsageClientSecretBox")!;
        secret.Text = ClientSecret;

        var copy = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.C, KeyModifiers = KeyModifiers.Control };
        secret.RaiseEvent(copy);
        var cut = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.X, KeyModifiers = KeyModifiers.Control };
        secret.RaiseEvent(cut);

        Assert.True(copy.Handled, "Ctrl+C must be swallowed in the secret field.");
        Assert.True(cut.Handled, "Ctrl+X must be swallowed in the secret field.");
    }

    [AvaloniaFact]
    public void Masked_secret_field_has_no_copy_or_cut_command_of_its_own()
    {
        using var scope = Scope.Create();
        var secret = scope.Window.FindControl<TextBox>("UsageClientSecretBox")!;
        secret.Text = ClientSecret;
        secret.SelectAll();

        // Measured, not assumed: with PasswordChar set, Avalonia itself reports the copy
        // and cut commands as unavailable, so Copy() transfers nothing even if the key
        // handler never ran. The handler is therefore a second line of defence, and it
        // stays because its wiring lives in Views/MainWindow.axaml, which this change
        // does not touch; removing it would mean editing the XAML attribute too.
        Assert.NotEqual(default, secret.PasswordChar);
        Assert.False(secret.CanCopy, "A masked field must not offer a copy command.");
        Assert.False(secret.CanCut, "A masked field must not offer a cut command.");

        secret.Copy();
        Assert.Equal(ClientSecret, secret.Text);
    }

    [AvaloniaFact]
    public async Task Successful_save_clears_the_credential_fields_and_shows_presence_only()
    {
        var store = new FakeCredentialStore();
        using var scope = Scope.Create(store);
        var window = scope.Window;
        window.Show();
        FillConnection(window, Endpoint + "/", ClientId, ClientSecret);

        window.FindControl<Button>("UsageConnectionSaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => window.ViewModel.HasUsageCredential);

        var endpoint = window.FindControl<TextBox>("UsageEndpointBox")!;
        var clientId = window.FindControl<TextBox>("UsageClientIdBox")!;
        var secret = window.FindControl<TextBox>("UsageClientSecretBox")!;
        Assert.Equal(Endpoint, endpoint.Text);
        Assert.Equal(string.Empty, clientId.Text);
        Assert.Equal(string.Empty, secret.Text);
        Assert.Equal(UsageConnectionState.Configured, window.ViewModel.UsageConnectionState);
        Assert.Contains("configured", window.FindControl<Border>("UsageConnectionStatusPanel")!.Classes);

        // The credential pair must never be echoed by any on-screen text; only presence is shown.
        var visibleText = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text ?? string.Empty);
        foreach (var text in visibleText)
        {
            Assert.DoesNotContain(ClientId, text, StringComparison.Ordinal);
            Assert.DoesNotContain(ClientSecret, text, StringComparison.Ordinal);
        }

        var presence = window.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == window.ViewModel.UsageCredentialStatusText);
        Assert.Contains("saved", presence.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, window.FindControl<TextBlock>("UsageConnectionValidationText")!.Text);
        Assert.Equal(ClientId, store.Peek(StoreUsageCredentialProvider.ClientIdAccount));
    }

    [AvaloniaFact]
    public async Task Failed_save_shows_fixed_copy_near_the_fields_and_keeps_the_input_for_correction()
    {
        var store = new FakeCredentialStore();
        using var scope = Scope.Create(store);
        var window = scope.Window;
        window.Show();
        FillConnection(window, "http://usage.example.test", ClientId, ClientSecret);

        window.FindControl<Button>("UsageConnectionSaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => window.FindControl<TextBlock>("UsageConnectionValidationText")!.IsVisible);

        var validation = window.FindControl<TextBlock>("UsageConnectionValidationText")!;
        Assert.Equal(window.ViewModel.UsageConnectionErrorText, validation.Text);
        Assert.Equal(new UiCopy(UiLanguage.English).UsageConnectionErrorText(UsageConnectionError.NotHttps), validation.Text);
        Assert.DoesNotContain("usage.example.test", validation.Text, StringComparison.Ordinal);
        Assert.Equal("http://usage.example.test", window.FindControl<TextBox>("UsageEndpointBox")!.Text);
        Assert.False(window.ViewModel.HasUsageCredential);
        Assert.False(window.FindControl<StackPanel>("UsageConnectionBusyPanel")!.IsVisible);
    }

    [AvaloniaFact]
    public async Task Saving_shows_progress_and_suppresses_input_until_the_save_finishes()
    {
        var store = new FakeCredentialStore(gated: true);
        using var scope = Scope.Create(store);
        var window = scope.Window;
        window.Show();
        FillConnection(window, Endpoint, ClientId, ClientSecret);

        window.FindControl<Button>("UsageConnectionSaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var busyPanel = window.FindControl<StackPanel>("UsageConnectionBusyPanel")!;
        var progress = window.FindControl<ProgressBar>("UsageConnectionProgressBar")!;
        Assert.True(busyPanel.IsVisible);
        Assert.True(progress.IsVisible);
        Assert.True(progress.IsIndeterminate);
        Assert.Contains("Saving", window.FindControl<TextBlock>("UsageConnectionBusyText")!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(window.FindControl<Button>("UsageConnectionSaveButton")!.IsEnabled);
        Assert.False(window.FindControl<Button>("UsageConnectionRemoveButton")!.IsEnabled);
        Assert.False(window.FindControl<TextBox>("UsageEndpointBox")!.IsEnabled);
        Assert.False(window.FindControl<TextBox>("UsageClientSecretBox")!.IsEnabled);

        store.Release();
        await WaitUntilAsync(() => !busyPanel.IsVisible);

        Assert.True(window.ViewModel.HasUsageCredential);
        Assert.Equal(string.Empty, window.FindControl<TextBox>("UsageClientSecretBox")!.Text);
        Assert.True(window.FindControl<Button>("UsageConnectionSaveButton")!.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Remove_asks_for_confirmation_and_then_clears_the_connection()
    {
        var store = new FakeCredentialStore();
        using var scope = Scope.Create(store);
        var window = scope.Window;
        window.Show();
        FillConnection(window, Endpoint, ClientId, ClientSecret);
        window.FindControl<Button>("UsageConnectionSaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => window.ViewModel.HasUsageCredential);

        var confirmPanel = window.FindControl<StackPanel>("UsageRemoveConfirmPanel")!;
        var remove = window.FindControl<Button>("UsageConnectionRemoveButton")!;
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(confirmPanel.IsVisible);
        Assert.Contains("cannot be undone", window.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == window.ViewModel.CopyText.UsageConnectionRemoveConfirm).Text, StringComparison.OrdinalIgnoreCase);

        window.FindControl<Button>("UsageConnectionCancelRemoveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(confirmPanel.IsVisible);
        Assert.Equal(2, store.Count);

        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.FindControl<Button>("UsageConnectionConfirmRemoveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => window.ViewModel.UsageConnectionState == UsageConnectionState.Unconfigured);

        Assert.False(confirmPanel.IsVisible);
        Assert.Equal(string.Empty, window.FindControl<TextBox>("UsageEndpointBox")!.Text);
        Assert.Equal(string.Empty, window.FindControl<TextBox>("UsageClientSecretBox")!.Text);
        Assert.False(window.ViewModel.HasUsageCredential);
        Assert.Equal(0, store.Count);
    }

    [AvaloniaFact]
    public void Remove_button_is_suppressed_until_a_connection_exists()
    {
        using var scope = Scope.Create();

        Assert.False(scope.Window.FindControl<Button>("UsageConnectionRemoveButton")!.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Session_only_state_shows_a_session_badge_and_a_warning_status_color()
    {
        var store = new FakeCredentialStore { Availability = CredentialStoreAvailability.SessionOnly };
        using var scope = Scope.Create(store);
        var window = scope.Window;
        window.Show();
        FillConnection(window, Endpoint, ClientId, ClientSecret);
        window.FindControl<Button>("UsageConnectionSaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => window.ViewModel.UsageConnectionState == UsageConnectionState.SessionOnly);

        var panel = window.FindControl<Border>("UsageConnectionStatusPanel")!;
        Assert.Contains("session-only", panel.Classes);
        Assert.True(window.FindControl<PathIcon>("UsageStatusSessionIcon")!.IsVisible);
        var badge = window.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == window.ViewModel.CopyText.UsageSessionOnlyBadge);
        Assert.True(badge.IsVisible);
        Assert.Equal(window.ViewModel.CopyText.UsageSessionOnlyBadge, badge.Text);
        var storeBadge = window.GetVisualDescendants().OfType<TextBlock>().SingleOrDefault(block => block.Text == window.ViewModel.CopyText.UsageStoreUnavailableBadge);
        Assert.True(storeBadge is null || !storeBadge.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Store_unavailable_state_explains_that_saving_is_impossible()
    {
        var store = new FakeCredentialStore();
        using var scope = Scope.Create(store);
        var window = scope.Window;
        window.Show();
        FillConnection(window, Endpoint, ClientId, ClientSecret);
        store.Availability = CredentialStoreAvailability.Unavailable;

        window.FindControl<Button>("UsageConnectionSaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => window.ViewModel.UsageConnectionState == UsageConnectionState.StoreUnavailable);

        var panel = window.FindControl<Border>("UsageConnectionStatusPanel")!;
        Assert.Contains("store-unavailable", panel.Classes);
        Assert.True(window.FindControl<PathIcon>("UsageStatusStoreIcon")!.IsVisible);
        var badge = window.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == window.ViewModel.CopyText.UsageStoreUnavailableBadge);
        Assert.True(badge.IsVisible);
        Assert.Contains("Cannot save", badge.Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(window.ViewModel.HasUsageCredential);
    }

    [AvaloniaFact]
    public async Task Language_switch_relocalizes_the_usage_connection_card()
    {
        var store = new FakeCredentialStore { Availability = CredentialStoreAvailability.SessionOnly };
        using var scope = Scope.Create(store);
        var window = scope.Window;
        window.Show();
        FillConnection(window, Endpoint, ClientId, ClientSecret);
        window.FindControl<Button>("UsageConnectionSaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => window.ViewModel.UsageConnectionState == UsageConnectionState.SessionOnly);

        window.ViewModel.Language = UiLanguage.Japanese;

        var status = window.FindControl<TextBlock>("UsageConnectionStatusText")!;
        Assert.Equal(new UiCopy(UiLanguage.Japanese).UsageConnectionStatus(UsageConnectionState.SessionOnly), status.Text);
        var badge = window.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == window.ViewModel.CopyText.UsageSessionOnlyBadge);
        Assert.Equal("このセッション限り", badge.Text);
        Assert.Contains("利用量 API 接続", window.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == window.ViewModel.CopyText.UsageConnectionTitle).Text, StringComparison.Ordinal);

        window.ViewModel.Language = UiLanguage.English;
        Assert.Equal(new UiCopy(UiLanguage.English).UsageConnectionStatus(UsageConnectionState.SessionOnly), window.FindControl<TextBlock>("UsageConnectionStatusText")!.Text);
    }

    [AvaloniaFact]
    public async Task Narrow_window_keeps_the_usage_connection_card_inside_the_viewport()
    {
        var store = new FakeCredentialStore();
        using var scope = Scope.Create(store);
        var window = scope.Window;
        window.Width = 460;
        window.ViewModel.Navigate(AppPage.Settings);
        window.Show();
        window.UpdateLayout();

        var card = window.FindControl<Border>("UsageConnectionCard")!;
        var scroll = window.FindControl<ScrollViewer>("ContentScroll")!;
        Assert.True(card.Bounds.Width > 0, "The usage connection card must be laid out.");
        Assert.True(card.Bounds.Width <= scroll.Viewport.Width + 0.5,
            $"The card is {card.Bounds.Width:0.#}px wide but the viewport is only {scroll.Viewport.Width:0.#}px.");

        var inner = card.Bounds.Width - card.Padding.Left - card.Padding.Right - card.BorderThickness.Left - card.BorderThickness.Right;
        foreach (var block in card.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible))
        {
            Assert.True(block.Bounds.Width <= inner + 0.5,
                $"'{block.Text}' is {block.Bounds.Width:0.#}px wide inside a {inner:0.#}px card; text would be clipped at narrow widths.");
        }

        var actions = card.GetVisualDescendants().OfType<WrapPanel>().Single(panel => panel.Name == "UsageConnectionActions");
        Assert.True(actions.Bounds.Height > 0);
        Assert.All(actions.Children.OfType<Button>(), button => Assert.True(button.Bounds.Width > 0));
    }

    [AvaloniaFact]
    public void Tab_moves_focus_from_the_endpoint_to_the_client_id_and_then_the_secret()
    {
        using var scope = Scope.Create();
        var window = scope.Window;
        window.Show();
        window.ViewModel.Navigate(AppPage.Settings);
        var endpoint = window.FindControl<TextBox>("UsageEndpointBox")!;
        var clientId = window.FindControl<TextBox>("UsageClientIdBox")!;
        var secret = window.FindControl<TextBox>("UsageClientSecretBox")!;
        Assert.True(endpoint.Focus());

        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
        Assert.Equal(clientId, window.FocusManager?.GetFocusedElement());

        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
        Assert.Equal(secret, window.FocusManager?.GetFocusedElement());
    }

    private static void FillConnection(MainWindow window, string endpoint, string clientId, string clientSecret)
    {
        window.FindControl<TextBox>("UsageEndpointBox")!.Text = endpoint;
        window.FindControl<TextBox>("UsageClientIdBox")!.Text = clientId;
        window.FindControl<TextBox>("UsageClientSecretBox")!.Text = clientSecret;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The expected condition was not reached in time.");
            await Task.Delay(10);
        }
    }

    private sealed class Scope : IDisposable
    {
        private Scope(MainWindow window)
        {
            Window = window;
        }

        public MainWindow Window { get; }

        public static Scope Create(FakeCredentialStore? store = null)
        {
            var viewModel = store is null
                ? new MainViewModel(new EmptyDashboardSource())
                : new MainViewModel(new EmptyDashboardSource(), usageCredentialStore: store);
            return new Scope(new MainWindow(viewModel));
        }

        public void Dispose()
        {
            Window.Close();
            Window.ViewModel.Dispose();
        }
    }

    private sealed class FakeCredentialStore(bool gated = false) : ICredentialStore
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CredentialStoreAvailability Availability { get; set; } = CredentialStoreAvailability.SecureStore;
        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count => values.Count;

        public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken) =>
            ValueTask.FromResult(values.GetValueOrDefault(account));

        public async ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken)
        {
            SaveStarted.TrySetResult();
            if (gated)
            {
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }

            values[account] = secret;
        }

        public ValueTask RemoveAsync(string account, CancellationToken cancellationToken)
        {
            values.Remove(account);
            return ValueTask.CompletedTask;
        }

        public void Release() => gate.TrySetResult();

        public string? Peek(string account) => values.GetValueOrDefault(account);
    }
}

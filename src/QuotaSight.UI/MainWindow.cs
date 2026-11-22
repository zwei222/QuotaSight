using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using System.ComponentModel;
using System.Globalization;
using QuotaSight.Application;
using QuotaSight.Infrastructure;

namespace QuotaSight.UI;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    public bool TrayAvailable { get; set; }
    public Action<bool>? ResidentModeChanged { get; set; }
    public Func<Task>? ExitRequestedAsync { get; set; }
    public Func<Func<CancellationToken, Task>, Task>? OperationRunner { get; set; }
    private bool effectiveResidentMode;
    private bool persistedResidentMode;
    private bool residentSaveInProgress;
    private long residentRequestGeneration;
    private bool isInitializing = true;
    private bool isConfiguringLists;
    private bool? appliedCompactLayout;
    private Task? initializationTask;
    public bool IsCompactLayout => Width > 0 && Width < 760;
    public MainWindow() : this(CompositionRoot.CreateMainViewModel()) { }
    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = ViewModel;
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        ApplyResponsiveLayout();
        Closing += OnClosing;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        ApplyUsageConnectionState();
        ApplyUsageStateVisuals();
        ApplyBreakdownStateVisuals();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(MainViewModel.UsageConnectionState) or nameof(MainViewModel.UsageConnectionStatusText))
        {
            ApplyUsageConnectionState();
        }

        if (e.PropertyName is null or nameof(MainViewModel.UsageFailure) or nameof(MainViewModel.UsageFailureText) or nameof(MainViewModel.IsUsageStale))
        {
            ApplyUsageStateVisuals();
        }

        if (e.PropertyName is null or nameof(MainViewModel.BreakdownFailure) or nameof(MainViewModel.BreakdownFailureText) or nameof(MainViewModel.IsBreakdownStale))
        {
            ApplyBreakdownStateVisuals();
        }

        // A language change must also rebuild the dimension switcher's choices: the VM
        // re-raises BreakdownDimensionChoices with the new display names.
        if (e.PropertyName is null or nameof(MainViewModel.Language))
        {
            ReconfigureBreakdownDimensionBox();
            ReconfigureProviderPicker();
            ReconfigureThemeBox();
        }
    }

    private void ReconfigureBreakdownDimensionBox()
    {
        if (this.FindControl<ComboBox>("BreakdownDimensionBox") is not { } dimensionBox) return;
        var selectedDimension = dimensionBox.SelectedItem switch
        {
            LocalizedChoice<UsageBreakdownDimension> current => current.Value,
            _ => ViewModel.SelectedBreakdownDimension,
        };
        var wasConfiguringLists = isConfiguringLists;
        isConfiguringLists = true;
        try
        {
            dimensionBox.ItemsSource = ViewModel.BreakdownDimensionChoices;
            dimensionBox.SelectedItem = ViewModel.BreakdownDimensionChoices.Single(item => item.Value == selectedDimension);
        }
        finally { isConfiguringLists = wasConfiguringLists; }
    }

    private void ReconfigureProviderPicker()
    {
        if (this.FindControl<ComboBox>("ProviderPicker") is not { } picker) return;
        var selectedId = picker.SelectedItem switch
        {
            ProviderChoiceViewModel current => current.ProviderId,
            _ => (ViewModel.SelectedTokScaleProvider?.Id),
        };
        var wasConfiguringLists = isConfiguringLists;
        isConfiguringLists = true;
        try
        {
            picker.ItemsSource = ViewModel.ProviderChoices;
            picker.SelectedItem = selectedId is null ? null : ViewModel.ProviderChoices.FirstOrDefault(item => item.ProviderId == selectedId);
        }
        finally { isConfiguringLists = wasConfiguringLists; }
    }

    private void ReconfigureThemeBox()
    {
        if (this.FindControl<ComboBox>("ThemeBox") is not { } theme) return;
        var selectedTheme = theme.SelectedItem switch
        {
            LocalizedChoice<ThemeMode> current => current.Value,
            ThemeMode legacy => legacy,
            _ => ViewModel.Settings.Theme,
        };
        var wasConfiguringLists = isConfiguringLists;
        isConfiguringLists = true;
        try
        {
            theme.ItemsSource = UiSettings.ThemeChoices(ViewModel.Language);
            theme.SelectedItem = UiSettings.ThemeChoices(ViewModel.Language).Single(item => item.Value == selectedTheme);
        }
        finally { isConfiguringLists = wasConfiguringLists; }
    }

    private void ApplyUsageConnectionState()
    {
        if (this.FindControl<Border>("UsageConnectionStatusPanel") is not { } panel) return;
        foreach (var name in new[] { "unconfigured", "configured", "session-only", "store-unavailable", "invalid" })
        {
            panel.Classes.Remove(name);
        }

        panel.Classes.Add(ViewModel.UsageConnectionState switch
        {
            UsageConnectionState.Configured => "configured",
            UsageConnectionState.SessionOnly => "session-only",
            UsageConnectionState.StoreUnavailable => "store-unavailable",
            UsageConnectionState.Invalid => "invalid",
            _ => "unconfigured",
        });
    }

    // The daily usage card's failure banner is colored by state the same way the settings
    // card is: one class per visual state, applied here because the fixed sentence is
    // already bound. Stale (values kept) warns, a first failure with no values reads as an
    // error. The settings detour appears only for failures a retry cannot fix.
    private void ApplyUsageStateVisuals()
    {
        if (this.FindControl<Border>("UsageFailurePanel") is not { } panel) return;
        var failure = ViewModel.UsageFailure;
        panel.IsVisible = failure is not UsageFailureKind.None;
        foreach (var name in new[] { "stale", "error" })
        {
            panel.Classes.Remove(name);
        }

        if (panel.IsVisible)
        {
            panel.Classes.Add(ViewModel.IsUsageStale ? "stale" : "error");
        }

        if (this.FindControl<Button>("UsageOpenSettingsButton") is { } settings)
        {
            // A redirect is reported as "check the endpoint", which is a settings-page
            // action: the detour is offered for it exactly like a credential problem.
            settings.IsVisible = failure is UsageFailureKind.Unauthorized or UsageFailureKind.Forbidden
                or UsageFailureKind.Redirected or UsageFailureKind.CredentialMissing;
        }
    }

    // The breakdown card's failure banner colors by the same state language as the daily
    // card's, with one difference in the detour: a server without breakdown support has
    // no settings action, while an unknown collector name is exactly a settings problem.
    private void ApplyBreakdownStateVisuals()
    {
        if (this.FindControl<Border>("UsageBreakdownFailurePanel") is not { } panel) return;
        var failure = ViewModel.BreakdownFailure;
        panel.IsVisible = failure is not UsageFailureKind.None;
        foreach (var name in new[] { "stale", "error" })
        {
            panel.Classes.Remove(name);
        }

        if (panel.IsVisible)
        {
            panel.Classes.Add(ViewModel.IsBreakdownStale ? "stale" : "error");
        }

        if (this.FindControl<Button>("UsageBreakdownOpenSettingsButton") is { } settings)
        {
            settings.IsVisible = failure is UsageFailureKind.Unauthorized or UsageFailureKind.Forbidden
                or UsageFailureKind.Redirected or UsageFailureKind.CredentialMissing or UsageFailureKind.CollectorNotFound;
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => initializationTask ??= InitializeCoreAsync(cancellationToken);

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        await ViewModel.InitializeAsync(cancellationToken);
        ConfigureLists();
        isInitializing = false;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    private void ConfigureLists()
    {
        var wasConfiguringLists = isConfiguringLists;
        isConfiguringLists = true;
        try
        {
            var theme = this.FindControl<ComboBox>("ThemeBox")!;
            var language = this.FindControl<ComboBox>("LanguageBox")!;
            var selectedTheme = theme.SelectedItem switch { LocalizedChoice<ThemeMode> current => current.Value, ThemeMode legacy => legacy, _ => ViewModel.Settings.Theme };
            theme.ItemsSource = UiSettings.ThemeChoices(ViewModel.Language);
            language.ItemsSource = UiSettings.SupportedLanguages;
            ReconfigureBreakdownDimensionBox();
            ReconfigureProviderPicker();
            theme.SelectedItem = UiSettings.ThemeChoices(ViewModel.Language).Single(item => item.Value == selectedTheme);
            language.SelectedIndex = ViewModel.Language == UiLanguage.Japanese ? 1 : 0;
            this.FindControl<NumericUpDown>("RefreshMinutesBox")!.Value = ViewModel.Settings.RefreshMinutes;
            persistedResidentMode = ViewModel.Settings.ResidentMode;
            effectiveResidentMode = persistedResidentMode;
            this.FindControl<CheckBox>("ResidentModeBox")!.IsChecked = persistedResidentMode;
            if (isInitializing)
            {
                // The endpoint is an ordinary setting and is safe to show; the credential pair
                // is never repopulated once it has been saved to the credential store.
                this.FindControl<TextBox>("UsageEndpointBox")!.Text = ViewModel.UsageApiEndpointText;
            }
        }
        finally { isConfiguringLists = wasConfiguringLists; }
    }

    private void ApplyResponsiveLayout()
    {
        var compact = Width > 0 && Width < 760;
        if (appliedCompactLayout == compact) return;
        appliedCompactLayout = compact;
        var shell = this.FindControl<Grid>("ShellGrid")!;
        var sidebar = this.FindControl<Border>("Sidebar")!;
        var content = this.FindControl<ScrollViewer>("ContentScroll")!;
        var nav = this.FindControl<StackPanel>("NavLinks")!;
        var localFirst = this.FindControl<Border>("LocalFirstCard")!;
        var header = this.FindControl<Grid>("DashboardHeader")!;
        var headerCopy = this.FindControl<StackPanel>("DashboardHeaderCopy")!;
        var refresh = this.FindControl<Button>("RefreshButton")!;
        if (compact)
        {
            shell.ColumnDefinitions = new ColumnDefinitions("*");
            shell.RowDefinitions = new RowDefinitions("Auto,*");
            Grid.SetColumn(sidebar, 0); Grid.SetRow(sidebar, 0);
            Grid.SetColumn(content, 0); Grid.SetRow(content, 1);
            sidebar.Padding = new Thickness(12, 10);
            nav.Orientation = Orientation.Horizontal;
            localFirst.IsVisible = false;
            nav.Children[1].IsVisible = false;
            header.ColumnDefinitions = new ColumnDefinitions("*");
            header.RowDefinitions = new RowDefinitions("Auto,Auto");
            Grid.SetColumn(headerCopy, 0); Grid.SetRow(headerCopy, 0);
            Grid.SetColumn(refresh, 0); Grid.SetRow(refresh, 1);
            refresh.HorizontalAlignment = HorizontalAlignment.Right;
        }
        else
        {
            shell.ColumnDefinitions = new ColumnDefinitions("220,*");
            shell.RowDefinitions = new RowDefinitions("*");
            Grid.SetColumn(sidebar, 0); Grid.SetRow(sidebar, 0);
            Grid.SetColumn(content, 1); Grid.SetRow(content, 0);
            sidebar.Padding = new Thickness(16, 24);
            nav.Orientation = Orientation.Vertical;
            localFirst.IsVisible = true;
            nav.Children[1].IsVisible = true;
            header.ColumnDefinitions = new ColumnDefinitions("*,Auto");
            header.RowDefinitions = new RowDefinitions("Auto");
            Grid.SetColumn(headerCopy, 0); Grid.SetRow(headerCopy, 0);
            Grid.SetColumn(refresh, 1); Grid.SetRow(refresh, 0);
            refresh.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
    }

    private void AddProviderClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.OpenProviderFlow();
        this.FindControl<ComboBox>("ProviderPicker")!.SelectedItem = null;
    }
    private void CloseProviderClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.CloseProviderFlow();
        this.FindControl<ComboBox>("ProviderPicker")!.SelectedItem = null;
    }
    private void ProviderSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing || isConfiguringLists) return;
        if (sender is ComboBox { SelectedItem: ProviderChoiceViewModel choice })
        {
            ViewModel.SelectTokScaleProvider(choice.ProviderId);
        }
    }
    private async void TokScaleAddClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTokScaleProvider is not { } provider) return;
        try { await RunTrackedAsync(token => ViewModel.AddTokScaleProviderAsync(provider.Id, token)); }
        catch (OperationCanceledException) { }
    }
    private async void TokScaleRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTokScaleProvider is not { } provider) return;
        try { await RunTrackedAsync(token => ViewModel.RemoveTokScaleProviderAsync(provider.Id, token)); }
        catch (OperationCanceledException) { }
    }

    private void DashboardClick(object? sender, RoutedEventArgs e) => ViewModel.Navigate(AppPage.Dashboard);
    private void UsageClick(object? sender, RoutedEventArgs e) => ViewModel.NavigateToUsage();
    private void SettingsClick(object? sender, RoutedEventArgs e) => ViewModel.Navigate(AppPage.Settings);
    private void UsageOpenSettingsClick(object? sender, RoutedEventArgs e) => ViewModel.Navigate(AppPage.Settings);
    private void UsageRetryClick(object? sender, RoutedEventArgs e) => UsageRefreshClick(sender, e);
    private void BreakdownDimensionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing || isConfiguringLists) return;
        if (sender is ComboBox { SelectedItem: LocalizedChoice<UsageBreakdownDimension> choice })
        {
            ViewModel.SelectedBreakdownDimension = choice.Value;
        }
    }
    private async void UsageRefreshClick(object? sender, RoutedEventArgs e)
    {
        try { await ViewModel.RefreshUsageAsync(); }
        catch (OperationCanceledException) { }
    }
    private async void RefreshClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.IsUsageVisible) { await UsageRefreshClickAsync(); return; }
        try { await RunTrackedAsync(token => ViewModel.RefreshTokScaleAsync(force: true)); }
        catch (OperationCanceledException) { }
    }
    private async Task UsageRefreshClickAsync() { try { await ViewModel.RefreshUsageAsync(); } catch (OperationCanceledException) { } }
    private async void ThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!isInitializing && sender is ComboBox box && box.SelectedItem is LocalizedChoice<ThemeMode> theme)
            try { await RunTrackedAsync(token => ViewModel.SetThemeAsync(theme.Value, token)); } catch (OperationCanceledException) { }
    }
    private async void LanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!isInitializing && sender is ComboBox box && box.SelectedItem is string language)
        {
            isConfiguringLists = true;
            var selectedProviderId = this.FindControl<ComboBox>("ProviderPicker")!.SelectedItem switch
            {
                ProviderChoiceViewModel choice => choice.ProviderId,
                _ => ViewModel.SelectedTokScaleProvider?.Id,
            };
            var selectedTheme = this.FindControl<ComboBox>("ThemeBox")!.SelectedItem switch { LocalizedChoice<ThemeMode> choice => choice.Value, _ => ViewModel.Settings.Theme };
            try
            {
                await RunTrackedAsync(async token =>
                {
                    try
                    {
                        await ViewModel.SetLanguageAsync(language == "日本語" ? UiLanguage.Japanese : UiLanguage.English, token);
                        ConfigureLists();
                        this.FindControl<ComboBox>("ProviderPicker")!.SelectedItem = selectedProviderId is null ? null : ViewModel.ProviderChoices.FirstOrDefault(item => item.ProviderId == selectedProviderId);
                        this.FindControl<ComboBox>("ThemeBox")!.SelectedItem = UiSettings.ThemeChoices(ViewModel.Language).Single(item => item.Value == selectedTheme);
                    }
                    finally { isConfiguringLists = false; }
                });
            }
            catch (OperationCanceledException) { return; }
        }
    }
    private async void RefreshMinutesChanged(object? sender, NumericUpDownValueChangedEventArgs e) { if (!isInitializing && e.NewValue is { } value) try { await RunTrackedAsync(token => ViewModel.SetRefreshMinutesAsync((int)value, token)); } catch (OperationCanceledException) { } }
    private async void ResidentModeChangedClick(object? sender, RoutedEventArgs e)
    {
        if (isInitializing || residentSaveInProgress || sender is not CheckBox) return;
        try { await (OperationRunner?.Invoke(RunResidentModeTransitionAsync) ?? RunResidentModeTransitionAsync(CancellationToken.None)); }
        catch (OperationCanceledException) { }
    }

    private async Task RunResidentModeTransitionAsync(CancellationToken cancellationToken)
    {
        if (this.FindControl<CheckBox>("ResidentModeBox") is not { } box) return;
        var requested = box.IsChecked == true;
        var generation = Interlocked.Increment(ref residentRequestGeneration);
        var oldPersisted = persistedResidentMode;
        var oldEffective = effectiveResidentMode;
        residentSaveInProgress = true;
        box.IsEnabled = false;
        if (!requested) effectiveResidentMode = false;
        try
        {
            while (true)
            {
                await ViewModel.SetResidentModeAsync(requested, cancellationToken);
                if (generation != Volatile.Read(ref residentRequestGeneration)) return;
                if (requested != (box.IsChecked == true))
                {
                    requested = box.IsChecked == true;
                    generation = Interlocked.Increment(ref residentRequestGeneration);
                    continue;
                }
                persistedResidentMode = requested;
                effectiveResidentMode = requested && TrayAvailable;
                ResidentModeChanged?.Invoke(effectiveResidentMode);
                if (requested && !TrayAvailable)
                    ViewModel.NotifyLocalized(copy => (copy.TrayUnavailableTitle, copy.TrayUnavailable));
                break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            ViewModel.RestoreResidentMode(oldPersisted);
            persistedResidentMode = oldPersisted;
            effectiveResidentMode = oldEffective;
            box.IsChecked = oldPersisted;
            ResidentModeChanged?.Invoke(oldEffective);
            ViewModel.NotifyLocalized(copy => (copy.ResidentMode, copy.ResidentSaveFailure));
        }
        finally
        {
            residentSaveInProgress = false;
            box.IsEnabled = true;
        }
    }

    // Usage connection section. The endpoint is an ordinary setting and may stay visible;
    // the client id and the client secret are credentials: once saved they are cleared from
    // the screen and only their presence ("saved") is shown.
    private async void UsageConnectionSaveClick(object? sender, RoutedEventArgs e)
    {
        var endpointBox = this.FindControl<TextBox>("UsageEndpointBox");
        var idBox = this.FindControl<TextBox>("UsageClientIdBox");
        var secretBox = this.FindControl<TextBox>("UsageClientSecretBox");
        var validation = this.FindControl<TextBlock>("UsageConnectionValidationText");
        SetUsageConnectionBusy(true, removing: false);
        try
        {
            UsageConnectionSaveResult result = new(false, UsageConnectionError.None, ViewModel.UsageConnectionState);
            await RunTrackedAsync(async token => result = await ViewModel.SaveUsageConnectionAsync(
                endpointBox?.Text ?? string.Empty,
                idBox?.Text ?? string.Empty,
                secretBox?.Text ?? string.Empty,
                token));
            if (validation is not null) validation.Text = result.Succeeded ? string.Empty : ViewModel.UsageConnectionErrorText;
            if (result.Succeeded)
            {
                if (endpointBox is { } endpoint) endpoint.Text = ViewModel.UsageApiEndpointText;
                if (idBox is { } id) id.Text = string.Empty;
                if (secretBox is { } secret) secret.Text = string.Empty;
                HideUsageRemoveConfirmation();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (validation is not null) validation.Text = ViewModel.CopyText.UsageConnectionSaveFailure;
        }
        finally { SetUsageConnectionBusy(false, removing: false); }
    }

    private void UsageConnectionRemoveClick(object? sender, RoutedEventArgs e) => ShowUsageRemoveConfirmation();
    private void UsageConnectionCancelRemoveClick(object? sender, RoutedEventArgs e) => HideUsageRemoveConfirmation();

    private async void UsageConnectionConfirmRemoveClick(object? sender, RoutedEventArgs e)
    {
        SetUsageConnectionBusy(true, removing: true);
        try
        {
            await RunTrackedAsync(token => ViewModel.ClearUsageConnectionAsync(token));
            if (this.FindControl<TextBox>("UsageEndpointBox") is { } endpoint) endpoint.Text = string.Empty;
            if (this.FindControl<TextBox>("UsageClientIdBox") is { } id) id.Text = string.Empty;
            if (this.FindControl<TextBox>("UsageClientSecretBox") is { } secret) secret.Text = string.Empty;
            if (this.FindControl<TextBlock>("UsageConnectionValidationText") is { } validation) validation.Text = string.Empty;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (this.FindControl<TextBlock>("UsageConnectionValidationText") is { } validation) validation.Text = ViewModel.UsageConnectionErrorText;
        }
        finally
        {
            SetUsageConnectionBusy(false, removing: true);
            HideUsageRemoveConfirmation();
        }
    }

    private void ShowUsageRemoveConfirmation()
    {
        if (this.FindControl<StackPanel>("UsageRemoveConfirmPanel") is { } panel) panel.IsVisible = true;
    }

    private void HideUsageRemoveConfirmation()
    {
        if (this.FindControl<StackPanel>("UsageRemoveConfirmPanel") is { } panel) panel.IsVisible = false;
    }

    private void UsageSecretKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox) return;
        // Second line of defence only: PasswordChar already makes Avalonia report
        // CanCopy/CanCut as false (pinned in UsageConnectionSettingsUiTests), so the
        // commands and their gestures are already inert. This handler stays because its
        // wiring is the KeyDown attribute in Views/MainWindow.axaml, which a non-visual
        // change does not edit; dropping it here alone would break XAML loading.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.C or Key.X or Key.Insert)
        {
            e.Handled = true;
        }
    }

    private void SetUsageConnectionBusy(bool busy, bool removing)
    {
        ViewModel.SetUsageConnectionBusy(busy, removing);
        if (this.FindControl<TextBox>("UsageEndpointBox") is { } endpoint) endpoint.IsEnabled = !busy;
        if (this.FindControl<TextBox>("UsageClientIdBox") is { } id) id.IsEnabled = !busy;
        if (this.FindControl<TextBox>("UsageClientSecretBox") is { } secret) secret.IsEnabled = !busy;
        if (this.FindControl<Button>("UsageConnectionSaveButton") is { } save) save.IsEnabled = !busy;
        if (this.FindControl<Button>("UsageConnectionRemoveButton") is { } remove) remove.IsEnabled = !busy;
        if (this.FindControl<Button>("UsageConnectionConfirmRemoveButton") is { } confirm) confirm.IsEnabled = !busy;
        if (this.FindControl<Button>("UsageConnectionCancelRemoveButton") is { } cancel) cancel.IsEnabled = !busy;
        if (this.FindControl<StackPanel>("UsageConnectionBusyPanel") is { } busyPanel) busyPanel.IsVisible = busy;
        if (this.FindControl<TextBlock>("UsageConnectionBusyText") is { } busyText)
        {
            busyText.Text = busy ? (removing ? ViewModel.CopyText.UsageConnectionRemoving : ViewModel.CopyText.UsageConnectionSaving) : string.Empty;
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (App.ExitRequested) return;
        if (!ResidentModePolicy.ShouldHideOnClose(effectiveResidentMode, TrayAvailable, exiting: false))
        {
            e.Cancel = true;
            _ = ExitRequestedAsync?.Invoke();
            return;
        }
        e.Cancel = true;
        Hide();
    }

    public void SetTrayCapability(bool available)
    {
        TrayAvailable = available;
        if (!available) effectiveResidentMode = false;
        else effectiveResidentMode = persistedResidentMode;
    }
    public void SyncResidentMode(bool residentMode) { persistedResidentMode = residentMode; effectiveResidentMode = residentMode && TrayAvailable; }
    public void ApplyTrayAvailability(bool available, CompactQuotaWindow compact)
    {
        SetTrayCapability(available);
        if (!available && persistedResidentMode && !IsVisible)
        {
            compact.Hide();
            ShowFallbackWindow();
        }
    }
    public bool EffectiveResidentMode => effectiveResidentMode;
    public void SetExiting() => effectiveResidentMode = false;
    public void ShowFullWindow()
    {
        ShowActivated = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
    public void ShowFallbackWindow()
    {
        var previousShowActivated = ShowActivated;
        try
        {
            ShowActivated = false;
            Show();
        }
        finally { ShowActivated = previousShowActivated; }
    }
    public void ShutdownCleanup() => Close();

    private Task RunTrackedAsync(Func<CancellationToken, Task> operation) =>
        OperationRunner?.Invoke(operation) ?? operation(CancellationToken.None);
}

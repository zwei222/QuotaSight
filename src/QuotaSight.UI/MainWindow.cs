using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Layout;
using QuotaSight.Core;
using QuotaSight.Infrastructure;

namespace QuotaSight.UI;

public interface IClipboardService { Task SetTextAsync(string text); }
public interface IUriLauncher { Task<bool> LaunchUriAsync(Uri uri); }
public sealed class AvaloniaUriLauncher(Func<Uri, Task<bool>> launch) : IUriLauncher
{
    public Task<bool> LaunchUriAsync(Uri uri) => launch(uri);
}
public interface IHistoryExportDestination
{
    Task<IHistoryExportFile?> PickAsync(string suggestedName, string contentType);
}
public interface IHistoryExportFile
{
    Task<Stream> OpenWriteAsync();
}

public sealed class AvaloniaHistoryExportDestination(TopLevel owner) : IHistoryExportDestination
{
    public async Task<IHistoryExportFile?> PickAsync(string suggestedName, string contentType)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            FileTypeChoices = [new FilePickerFileType(contentType) { Patterns = ["*.*"] }]
        });
        return file is null ? null : new AvaloniaHistoryExportFile(file);
    }

    private sealed class AvaloniaHistoryExportFile(IStorageFile file) : IHistoryExportFile
    {
        public Task<Stream> OpenWriteAsync() => file.OpenWriteAsync();
    }
}

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private readonly IProviderUiService providerService;
    private readonly IClipboardService? clipboardService;
    private readonly IHistoryExportDestination exportDestination;
    private readonly IUriLauncher uriLauncher;
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
    public MainWindow() : this(CompositionRoot.CreateMainWindowParts()) { }
    public MainWindow((MainViewModel ViewModel, IProviderUiService Provider) parts) : this(parts.ViewModel, parts.Provider) { }
    public MainWindow(MainViewModel viewModel) : this(viewModel, new UiProviderFacade(), null) { }
    public MainWindow(MainViewModel viewModel, IProviderUiService providerService) : this(viewModel, providerService, null, null) { }
    public MainWindow(MainViewModel viewModel, IProviderUiService providerService, IClipboardService? clipboardService) : this(viewModel, providerService, clipboardService, null, null) { }
    public MainWindow(MainViewModel viewModel, IProviderUiService providerService, IClipboardService? clipboardService, IHistoryExportDestination? exportDestination) : this(viewModel, providerService, clipboardService, exportDestination, null) { }
    public MainWindow(MainViewModel viewModel, IProviderUiService providerService, IClipboardService? clipboardService, IHistoryExportDestination? exportDestination, IUriLauncher? uriLauncher)
    {
        ViewModel = viewModel;
        this.providerService = providerService;
        this.clipboardService = clipboardService;
        this.exportDestination = exportDestination ?? new AvaloniaHistoryExportDestination(this);
        this.uriLauncher = uriLauncher ?? new AvaloniaUriLauncher(uri => Launcher.LaunchUriAsync(uri));
        InitializeComponent();
        DataContext = ViewModel;
        if (providerService is UiProviderFacade facade)
        {
            ViewModel.SetCredentialAvailability(facade.CredentialAvailability);
            facade.SetOpenCodeSuccessHandler(snapshots => ViewModel.ApplyProviderSnapshotsAsync(snapshots));
        }
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        ApplyResponsiveLayout();
        Closing += OnClosing;

    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => initializationTask ??= InitializeCoreAsync(cancellationToken);

    private async Task InitializeSafelyAsync()
    {
        try { await InitializeAsync(); }
        catch { ViewModel.SetCodexResult(new(false, CodexAuthorizationState.Error, "Unable to initialize provider state.")); }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        await ViewModel.InitializeAsync(cancellationToken);
        if (providerService is UiProviderFacade facade)
            ViewModel.SetCodexCredentialPresence(await facade.HasCodexCredentialAsync(cancellationToken));
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
            var provider = this.FindControl<ComboBox>("ManualProviderBox")!;
            var window = this.FindControl<ComboBox>("ManualWindowBox")!;
            var account = this.FindControl<TextBox>("AccountNameBox")!;
            var usedPercent = this.FindControl<TextBox>("UsedPercentBox")!;
            var resetAt = this.FindControl<TextBox>("ResetAtBox")!;
            var theme = this.FindControl<ComboBox>("ThemeBox")!;
            var language = this.FindControl<ComboBox>("LanguageBox")!;
            var accountFilter = this.FindControl<ComboBox>("HistoryAccountFilter")!;
            var windowFilter = this.FindControl<ComboBox>("HistoryWindowFilter")!;
            var selectedProvider = provider.SelectedItem switch { LocalizedChoice<ProviderKind> current => current.Value, ProviderKind legacy => legacy, _ => ProviderKind.ChatGpt };
            var selectedWindow = window.SelectedItem is LocalizedChoice<QuotaWindowKind> currentWindow ? currentWindow.Value : QuotaWindowKind.Weekly;
            var selectedTheme = theme.SelectedItem switch { LocalizedChoice<ThemeMode> current => current.Value, ThemeMode legacy => legacy, _ => ViewModel.Settings.Theme };
            var accountText = account.Text;
            var usedPercentText = usedPercent.Text;
            var resetAtText = resetAt.Text;
            provider.ItemsSource = UiSettings.ManualProviderChoices(ViewModel.Language);
            window.ItemsSource = UiSettings.ManualWindowChoices(ViewModel.Language);
            theme.ItemsSource = UiSettings.ThemeChoices(ViewModel.Language);
            language.ItemsSource = UiSettings.SupportedLanguages;
            accountFilter.ItemsSource = UiSettings.HistoryAccountChoices(ViewModel.Language);
            windowFilter.ItemsSource = UiSettings.HistoryWindowChoices(ViewModel.Language);
            provider.SelectedItem = UiSettings.ManualProviderChoices(ViewModel.Language).Single(item => item.Value == selectedProvider);
            this.FindControl<ComboBox>("ProviderPicker")!.SelectedItem = ViewModel.ProviderChoices.First(item => item.Choice == ViewModel.SelectedProvider);
            window.SelectedItem = UiSettings.ManualWindowChoices(ViewModel.Language).Single(item => item.Value == selectedWindow);
            account.Text = accountText;
            usedPercent.Text = usedPercentText;
            resetAt.Text = resetAtText;
            theme.SelectedItem = UiSettings.ThemeChoices(ViewModel.Language).Single(item => item.Value == selectedTheme);
            language.SelectedIndex = ViewModel.Language == UiLanguage.Japanese ? 1 : 0;
            this.FindControl<NumericUpDown>("RefreshMinutesBox")!.Value = ViewModel.Settings.RefreshMinutes;
            this.FindControl<NumericUpDown>("ThresholdBox")!.Value = ViewModel.Settings.OverallThreshold;
            this.FindControl<CheckBox>("NotificationsBox")!.IsChecked = ViewModel.Settings.NotificationsEnabled;
            persistedResidentMode = ViewModel.Settings.ResidentMode;
            effectiveResidentMode = persistedResidentMode;
            this.FindControl<CheckBox>("ResidentModeBox")!.IsChecked = persistedResidentMode;
            this.FindControl<TextBox>("GithubClientIdBox")!.Text = ViewModel.Settings.GithubOAuthClientId;
            accountFilter.SelectedIndex = 0;
            windowFilter.SelectedIndex = 0;
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

    public async Task CopyCodexCodeAsync()
    {
        var code = ViewModel.CodexUserCode;
        if (string.IsNullOrWhiteSpace(code)) return;
        if (clipboardService is not null) { await clipboardService.SetTextAsync(code); return; }
        await (TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(code) ?? Task.CompletedTask);
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
            ViewModel.SelectProvider(choice.Choice);
            if (choice.Choice is ProviderConnectionChoice.ChatGpt or ProviderConnectionChoice.Claude)
            {
                var provider = choice.Choice == ProviderConnectionChoice.ChatGpt ? ProviderKind.ChatGpt : ProviderKind.Claude;
                this.FindControl<ComboBox>("ManualProviderBox")!.SelectedItem = UiSettings.ManualProviderChoices(ViewModel.Language).Single(item => item.Value == provider);
            }
        }
    }
    private async void CodexCopyClick(object? sender, RoutedEventArgs e) => await CopyCodexCodeAsync();

    private void DashboardClick(object? sender, RoutedEventArgs e) => ViewModel.Navigate(AppPage.Dashboard);
    private void HistoryClick(object? sender, RoutedEventArgs e) => ViewModel.Navigate(AppPage.History);
    private void SettingsClick(object? sender, RoutedEventArgs e) => ViewModel.Navigate(AppPage.Settings);
    private async void RefreshClick(object? sender, RoutedEventArgs e)
    {
        try { await RunTrackedAsync(token => ViewModel.RefreshAsync(token)); }
        catch (OperationCanceledException) { }
    }
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
            var selected = ViewModel.SelectedProvider;
            var manualProvider = this.FindControl<ComboBox>("ManualProviderBox")!.SelectedItem switch { LocalizedChoice<ProviderKind> choice => choice.Value, ProviderKind legacy => legacy, _ => ProviderKind.ChatGpt };
            var manualWindow = this.FindControl<ComboBox>("ManualWindowBox")!.SelectedItem is LocalizedChoice<QuotaWindowKind> quotaWindow ? quotaWindow.Value : QuotaWindowKind.Weekly;
            var accountText = this.FindControl<TextBox>("AccountNameBox")!.Text;
            var usedPercentText = this.FindControl<TextBox>("UsedPercentBox")!.Text;
            var resetAtText = this.FindControl<TextBox>("ResetAtBox")!.Text;
            var accountFilter = ViewModel.History.AccountFilter;
            var windowFilter = ViewModel.History.WindowFilter;
            try
            {
                await RunTrackedAsync(async token =>
                {
                    try
                    {
                        await ViewModel.SetLanguageAsync(language == "日本語" ? UiLanguage.Japanese : UiLanguage.English, token);
                        ConfigureLists();
                        this.FindControl<ComboBox>("ManualProviderBox")!.SelectedItem = UiSettings.ManualProviderChoices(ViewModel.Language).Single(item => item.Value == manualProvider);
                        this.FindControl<ComboBox>("ManualWindowBox")!.SelectedItem = UiSettings.ManualWindowChoices(ViewModel.Language).Single(item => item.Value == manualWindow);
                        this.FindControl<TextBox>("AccountNameBox")!.Text = accountText;
                        this.FindControl<TextBox>("UsedPercentBox")!.Text = usedPercentText;
                        this.FindControl<TextBox>("ResetAtBox")!.Text = resetAtText;
                        this.FindControl<ComboBox>("HistoryAccountFilter")!.SelectedItem = UiSettings.HistoryAccountChoices(ViewModel.Language).Single(item => item.Value == accountFilter);
                        this.FindControl<ComboBox>("HistoryWindowFilter")!.SelectedItem = UiSettings.HistoryWindowChoices(ViewModel.Language).Single(item => item.Value == windowFilter);
                        this.FindControl<ComboBox>("ProviderPicker")!.SelectedItem = ViewModel.ProviderChoices.First(item => item.Choice == selected);
                    }
                    finally { isConfiguringLists = false; }
                });
            }
            catch (OperationCanceledException) { return; }
        }
    }
    private async void RefreshMinutesChanged(object? sender, NumericUpDownValueChangedEventArgs e) { if (!isInitializing && e.NewValue is { } value) try { await RunTrackedAsync(token => ViewModel.SetRefreshMinutesAsync((int)value, token)); } catch (OperationCanceledException) { } }
    private async void NotificationsChanged(object? sender, RoutedEventArgs e) { if (!isInitializing && sender is CheckBox box) try { await RunTrackedAsync(token => ViewModel.SetNotificationsAsync(box.IsChecked == true, token)); } catch (OperationCanceledException) { } }
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
                    ViewModel.Notify(ViewModel.CopyText.TrayUnavailableTitle, ViewModel.CopyText.TrayUnavailable);
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
            ViewModel.Notify(ViewModel.CopyText.RefreshError, ViewModel.CopyText.RefreshError);
        }
        finally
        {
            residentSaveInProgress = false;
            box.IsEnabled = true;
        }
    }
    private async void ThresholdChanged(object? sender, NumericUpDownValueChangedEventArgs e) { if (!isInitializing && e.NewValue is { } value) try { await RunTrackedAsync(token => ViewModel.SetThresholdAsync(value, token)); } catch (OperationCanceledException) { } }
    private async void GithubClientIdChanged(object? sender, TextChangedEventArgs e)
    {
        if (isInitializing || sender is not TextBox box) return;
        var clientId = (box.Text ?? string.Empty).Trim();
        if (clientId == ViewModel.Settings.GithubOAuthClientId) return;
        try
        {
            await RunTrackedAsync(async token => { await ViewModel.SetGithubClientIdAsync(clientId, token); if (providerService is UiProviderFacade facade) facade.UpdateGitHubClientId(clientId); });
        }
        catch (OperationCanceledException) { }
    }
    private void HistoryAccountFilterChanged(object? sender, SelectionChangedEventArgs e) { if (sender is ComboBox { SelectedItem: LocalizedChoice<string> choice }) ViewModel.History.AccountFilter = choice.Value; }
    private void HistoryWindowFilterChanged(object? sender, SelectionChangedEventArgs e) { if (sender is ComboBox { SelectedItem: LocalizedChoice<string> choice }) ViewModel.History.WindowFilter = choice.Value; }
    private void HistoryPreviousClick(object? sender, RoutedEventArgs e) => ViewModel.History.PreviousPage();
    private void HistoryNextClick(object? sender, RoutedEventArgs e) => ViewModel.History.NextPage();
    private async void AddManualClick(object? sender, RoutedEventArgs e)
    {
        var providerBox = this.FindControl<ComboBox>("ManualProviderBox");
        var windowBox = this.FindControl<ComboBox>("ManualWindowBox");
        var accountBox = this.FindControl<TextBox>("AccountNameBox");
        var usedBox = this.FindControl<TextBox>("UsedPercentBox");
        var resetBox = this.FindControl<TextBox>("ResetAtBox");
        var validation = this.FindControl<TextBlock>("ManualValidationText");
        var entry = new ManualQuotaEntry
        {
            Provider = providerBox?.SelectedItem is LocalizedChoice<ProviderKind> provider ? provider.Value : providerBox?.SelectedItem is ProviderKind legacyProvider ? legacyProvider : ProviderKind.Manual,
            AccountDisplayName = accountBox?.Text ?? string.Empty,
            Window = windowBox?.SelectedItem is LocalizedChoice<QuotaWindowKind> window ? window.Value : QuotaWindowKind.Weekly,
            UsedPercentText = usedBox?.Text ?? string.Empty,
            ResetLocalText = resetBox?.Text ?? string.Empty
        };
        var valid = entry.TryApply(out var snapshot);
        if (valid && snapshot is not null)
        {
            try
            {
                await RunTrackedAsync(async token =>
                {
                    await ViewModel.ApplyManualSnapshotAsync(snapshot, token);
                    if (validation is not null) validation.Text = ViewModel.Language == UiLanguage.Japanese
                        ? $"{snapshot.Account}を追加しました · 使用率 {snapshot.EffectivePercent:0.#}%"
                        : $"Added {snapshot.Account} · {snapshot.EffectivePercent:0.#}% used.";
                });
            }
            catch (OperationCanceledException) { }
            return;
        }
        if (validation is not null) validation.Text = ViewModel.Language == UiLanguage.Japanese
            ? "アカウント、0以上の使用率、入力した場合は正しいリセット時刻を指定してください。"
            : "Enter an account, a non-negative used percentage, and a valid reset time if provided.";
    }
    private async void OpenCodeTestClick(object? sender, RoutedEventArgs e)
    {
        var key = this.FindControl<TextBox>("OpenCodeKeyBox")?.Text ?? string.Empty;
        try
        {
            await RunTrackedAsync(async token =>
            {
                var result = await providerService.TestOpenCodeAsync(key, token);
                if (this.FindControl<TextBox>("OpenCodeKeyBox") is { } keyBox) keyBox.Text = string.Empty;
                var validation = this.FindControl<TextBlock>("ManualValidationText");
                if (validation is not null) validation.Text = ViewModel.CopyText.OpenCodeStatus(result);
            });
        }
        catch (OperationCanceledException) { }
    }
    private DeviceAuthorizationStart? githubAuthorization;
    private async void CopilotStartClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.IsCopilotFlowActive) return;
        ViewModel.SetCopilotFlowActive(true);
        try
        {
            await RunTrackedAsync(async token =>
            {
                var result = await providerService.StartGitHubDeviceFlowAsync(token);
                if (!result.IsSuccess || result.Value is not { } auth)
                {
                    ViewModel.SetCopilotDeviceResult(string.Empty, string.Empty, CopilotStateFor(result.Status, result.Error), result.Error);
                    return;
                }

                githubAuthorization = auth;
                ViewModel.SetCopilotDeviceResult(auth.VerificationUri.ToString(), auth.UserCode, CopilotUiState.Started);
                await uriLauncher.LaunchUriAsync(auth.VerificationUri);
                await PollCopilotAsync(auth, token);
            });
        }
        catch (OperationCanceledException) { }
        finally { ViewModel.SetCopilotFlowActive(false); }
    }
    private async Task PollCopilotAsync(DeviceAuthorizationStart auth, CancellationToken token)
    {
        var result = await providerService.PollGitHubDeviceFlowAsync(auth, token);
        ViewModel.SetCopilotDeviceResult(string.Empty, string.Empty, result.IsSuccess ? CopilotUiState.Completed : CopilotStateFor(result.Status, result.Error), result.Error);
    }
    private async void CopilotProbeClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await RunTrackedAsync(async token => { await providerService.ProbeGitHubCliAsync(token); ViewModel.SetCopilotDeviceResult(string.Empty, string.Empty, CopilotUiState.GhProbe); });
        }
        catch (OperationCanceledException) { }
    }
    private async void CopilotPollClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.IsCopilotFlowActive || githubAuthorization is not { } auth) return;
        ViewModel.SetCopilotFlowActive(true);
        try
        {
            await RunTrackedAsync(token => PollCopilotAsync(auth, token));
        }
        catch (OperationCanceledException) { }
        finally { ViewModel.SetCopilotFlowActive(false); }
    }
    private static CopilotUiState CopilotStateFor(FetchStatus status, string? error)
    {
        var message = error ?? string.Empty;
        if (message.Contains("device_flow_disabled", StringComparison.OrdinalIgnoreCase) || message.Contains("device flow is disabled", StringComparison.OrdinalIgnoreCase) || message.Contains("device authorization is disabled", StringComparison.OrdinalIgnoreCase)) return CopilotUiState.DeviceFlowDisabled;
        if (message.Contains("not configured", StringComparison.OrdinalIgnoreCase) || message.Contains("incorrect_client_credentials", StringComparison.OrdinalIgnoreCase) || message.Contains("client credentials", StringComparison.OrdinalIgnoreCase) || message.Contains("incorrect credentials", StringComparison.OrdinalIgnoreCase)) return CopilotUiState.ConfigurationError;
        if (status == FetchStatus.Unsupported) return CopilotUiState.Unsupported;
        if (status == FetchStatus.Unauthorized) return CopilotUiState.Denied;
        if (error?.Contains("expired", StringComparison.OrdinalIgnoreCase) == true) return CopilotUiState.Expired;
        if (error?.Contains("pending", StringComparison.OrdinalIgnoreCase) == true) return CopilotUiState.Pending;
        return CopilotUiState.Failed;
    }
    private void CopilotOpenClick(object? sender, RoutedEventArgs e) { if (githubAuthorization is { } auth) _ = uriLauncher.LaunchUriAsync(auth.VerificationUri); }
    private void CopilotCopyClick(object? sender, RoutedEventArgs e) { if (githubAuthorization is { } auth) TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(auth.UserCode); }
    private void OpenChatGptClick(object? sender, RoutedEventArgs e) => _ = uriLauncher.LaunchUriAsync(new Uri(OfficialUsageUrls.ChatGpt));
    private async void CodexStartClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await RunTrackedAsync(async token =>
            {
                var result = await providerService.StartCodexAsync(token);
                ViewModel.SetCodexResult(result);
                if (result.Success) await OpenCodexBrowserAsync();
            });
        }
        catch (OperationCanceledException) { return; }
    }
    private async void CodexPollClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await RunTrackedAsync(async token =>
            {
                var result = await providerService.PollCodexAsync(token);
                ViewModel.SetCodexResult(result);
                if (result.Success) await ViewModel.RefreshAsync(token);
            });
        }
        catch (OperationCanceledException) { }
    }
    private async void CodexLogoutClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await RunTrackedAsync(async token =>
            {
                try
                {
                    var result = await providerService.LogoutCodexAsync(token);
                    ViewModel.SetCodexResult(result);
                    if (result.Success)
                    {
                        ViewModel.RemoveCodexCards();
                        await ViewModel.RefreshAsync(token);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { ViewModel.SetCodexResult(new(false, CodexAuthorizationState.Error, "Unable to disconnect Codex.")); }
            });
        }
        catch (OperationCanceledException) { }
    }
    private async void CodexOpenClick(object? sender, RoutedEventArgs e) => await OpenCodexBrowserAsync();
    private async Task OpenCodexBrowserAsync()
    {
        if (ViewModel.CodexVerificationUri is not { } uri) return;
        try { ViewModel.SetCodexBrowserStatus(await uriLauncher.LaunchUriAsync(uri)); }
        catch (OperationCanceledException) { throw; }
        catch { ViewModel.SetCodexBrowserStatus(false); }
    }
    private void OpenClaudeClick(object? sender, RoutedEventArgs e) => _ = uriLauncher.LaunchUriAsync(new Uri(OfficialUsageUrls.Claude));
    private void OpenCopilotClick(object? sender, RoutedEventArgs e) => _ = uriLauncher.LaunchUriAsync(new Uri(OfficialUsageUrls.Copilot));
    private async void DeleteHistoryClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await RunTrackedAsync(async token =>
            {
                try
                {
                    if (sender is Button { Tag: Guid id }) await ViewModel.History.DeleteAsync(id, token);
                }
                catch (OperationCanceledException) { throw; }
                catch { ViewModel.Notify(ViewModel.CopyText.HistoryNotificationTitle, ViewModel.CopyText.HistoryDeleteFailure); }
            });
        }
        catch (OperationCanceledException) { }
    }
    private async void DeleteAllHistoryClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await RunTrackedAsync(async token =>
            {
                try { await ViewModel.History.DeleteAllAsync(token); }
                catch (OperationCanceledException) { throw; }
                catch { ViewModel.Notify(ViewModel.CopyText.HistoryNotificationTitle, ViewModel.CopyText.HistoryDeleteAllFailure); }
            });
        }
        catch (OperationCanceledException) { }
    }
    private async void ExportCsvClick(object? sender, RoutedEventArgs e) => await ExportLatestAsync("quotasight-history.csv", "text/csv", ViewModel.History.ExportCsvAsync);
    private async void ExportJsonClick(object? sender, RoutedEventArgs e) => await ExportLatestAsync("quotasight-history.json", "application/json", ViewModel.History.ExportJsonAsync);
    private async Task ExportLatestAsync(string suggestedName, string contentType, Func<CancellationToken, Task<string>> contentFactory)
    {
        try
        {
            string? content = null;
            await RunTrackedAsync(async token => content = await contentFactory(token));
            if (content is null) return;
            // The OS save dialog cannot be cancelled, so it must stay outside tracked work: app
            // exit never waits on it, and a late result runs into a no-op tracked operation, so
            // it can neither write nor notify after cleanup.
            var file = await exportDestination.PickAsync(suggestedName, contentType);
            if (file is null) return;
            await RunTrackedAsync(async token =>
            {
                await using var stream = await file.OpenWriteAsync();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(content.AsMemory(), token);
                await writer.FlushAsync(token);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ViewModel.Notify(ViewModel.CopyText.HistoryNotificationTitle, exception.Message); }
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

    private static bool IsCancellation(Exception exception) => exception is OperationCanceledException;
}

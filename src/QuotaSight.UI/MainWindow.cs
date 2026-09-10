using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Layout;
using QuotaSight.Core;
using QuotaSight.Infrastructure;

namespace QuotaSight.UI;

public interface IClipboardService { Task SetTextAsync(string text); }

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private readonly IProviderUiService providerService;
    private readonly IClipboardService? clipboardService;
    public bool TrayAvailable { get; set; }
    private bool isInitializing = true;
    private bool isConfiguringLists;
    private bool? appliedCompactLayout;
    private Task? initializationTask;
    public bool IsCompactLayout => Width > 0 && Width < 760;
    public MainWindow() : this(CompositionRoot.CreateMainWindowParts()) { }
    public MainWindow((MainViewModel ViewModel, IProviderUiService Provider) parts) : this(parts.ViewModel, parts.Provider) { }
    public MainWindow(MainViewModel viewModel) : this(viewModel, new UiProviderFacade(), null) { }
    public MainWindow(MainViewModel viewModel, IProviderUiService providerService) : this(viewModel, providerService, null) { }
    public MainWindow(MainViewModel viewModel, IProviderUiService providerService, IClipboardService? clipboardService)
    {
        ViewModel = viewModel;
        this.providerService = providerService;
        this.clipboardService = clipboardService;
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
        Opened += (_, _) => _ = InitializeSafelyAsync();
    }

    public Task InitializeAsync() => initializationTask ??= InitializeCoreAsync();

    private async Task InitializeSafelyAsync()
    {
        try { await InitializeAsync(); }
        catch { ViewModel.SetCodexResult(new(false, CodexAuthorizationState.Error, "Unable to initialize provider state.")); }
    }

    private async Task InitializeCoreAsync()
    {
        await ViewModel.InitializeAsync();
        if (providerService is UiProviderFacade facade)
            ViewModel.SetCodexCredentialPresence(await facade.HasCodexCredentialAsync(CancellationToken.None));
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
    private async void RefreshClick(object? sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();
    private async void ThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!isInitializing && sender is ComboBox box && box.SelectedItem is LocalizedChoice<ThemeMode> theme) await ViewModel.SetThemeAsync(theme.Value);
    }
    private async void LanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!isInitializing && sender is ComboBox box && box.SelectedItem is string language)
        {
            isConfiguringLists = true;
            try
            {
                var selected = ViewModel.SelectedProvider;
                var manualProvider = this.FindControl<ComboBox>("ManualProviderBox")!.SelectedItem switch { LocalizedChoice<ProviderKind> choice => choice.Value, ProviderKind legacy => legacy, _ => ProviderKind.ChatGpt };
                var manualWindow = this.FindControl<ComboBox>("ManualWindowBox")!.SelectedItem is LocalizedChoice<QuotaWindowKind> quotaWindow ? quotaWindow.Value : QuotaWindowKind.Weekly;
                var accountText = this.FindControl<TextBox>("AccountNameBox")!.Text;
                var usedPercentText = this.FindControl<TextBox>("UsedPercentBox")!.Text;
                var resetAtText = this.FindControl<TextBox>("ResetAtBox")!.Text;
                var accountFilter = ViewModel.History.AccountFilter;
                var windowFilter = ViewModel.History.WindowFilter;
                await ViewModel.SetLanguageAsync(language == "日本語" ? UiLanguage.Japanese : UiLanguage.English);
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
        }
    }
    private async void RefreshMinutesChanged(object? sender, NumericUpDownValueChangedEventArgs e) { if (!isInitializing && e.NewValue is { } value) await ViewModel.SetRefreshMinutesAsync((int)value); }
    private async void NotificationsChanged(object? sender, RoutedEventArgs e) { if (!isInitializing && sender is CheckBox box) await ViewModel.SetNotificationsAsync(box.IsChecked == true); }
    private async void ThresholdChanged(object? sender, NumericUpDownValueChangedEventArgs e) { if (!isInitializing && e.NewValue is { } value) await ViewModel.SetThresholdAsync(value); }
    private async void GithubClientIdChanged(object? sender, TextChangedEventArgs e)
    {
        if (isInitializing || sender is not TextBox box) return;
        var clientId = (box.Text ?? string.Empty).Trim();
        if (clientId == ViewModel.Settings.GithubOAuthClientId) return;
        await ViewModel.SetGithubClientIdAsync(clientId);
        if (providerService is UiProviderFacade facade) facade.UpdateGitHubClientId(clientId);
    }
    private void HistoryAccountFilterChanged(object? sender, SelectionChangedEventArgs e) { if (sender is ComboBox { SelectedItem: LocalizedChoice<string> choice }) ViewModel.History.AccountFilter = choice.Value; }
    private void HistoryWindowFilterChanged(object? sender, SelectionChangedEventArgs e) { if (sender is ComboBox { SelectedItem: LocalizedChoice<string> choice }) ViewModel.History.WindowFilter = choice.Value; }
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
        if (valid && snapshot is not null) await ViewModel.ApplyManualSnapshotAsync(snapshot);
        if (validation is not null) validation.Text = valid
            ? ViewModel.Language == UiLanguage.Japanese ? $"{snapshot!.Account}を追加しました · 使用率 {snapshot.EffectivePercent:0.#}%" : $"Added {snapshot!.Account} · {snapshot.EffectivePercent:0.#}% used."
            : ViewModel.Language == UiLanguage.Japanese ? "アカウント、0以上の使用率、入力した場合は正しいリセット時刻を指定してください。" : "Enter an account, a non-negative used percentage, and a valid reset time if provided.";
    }
    private async void OpenCodeTestClick(object? sender, RoutedEventArgs e)
    {
        var key = this.FindControl<TextBox>("OpenCodeKeyBox")?.Text ?? string.Empty;
        var result = await providerService.TestOpenCodeAsync(key, CancellationToken.None);
        if (this.FindControl<TextBox>("OpenCodeKeyBox") is { } keyBox) keyBox.Text = string.Empty;
        var validation = this.FindControl<TextBlock>("ManualValidationText");
        if (validation is not null) validation.Text = ViewModel.CopyText.OpenCodeStatus(result);
    }
    private DeviceAuthorizationStart? githubAuthorization;
    private async void CopilotStartClick(object? sender, RoutedEventArgs e)
    {
        var result = await providerService.StartGitHubDeviceFlowAsync(CancellationToken.None);
        if (result.IsSuccess && result.Value is { } auth) { githubAuthorization = auth; ViewModel.SetCopilotDeviceResult(auth.VerificationUri.ToString(), auth.UserCode, CopilotUiState.Started); }
        else ViewModel.SetCopilotDeviceResult(string.Empty, string.Empty, CopilotUiState.Failed);
    }
    private async void CopilotProbeClick(object? sender, RoutedEventArgs e) { await providerService.ProbeGitHubCliAsync(CancellationToken.None); ViewModel.SetCopilotDeviceResult(string.Empty, string.Empty, CopilotUiState.GhProbe); }
    private async void CopilotPollClick(object? sender, RoutedEventArgs e)
    {
        if (githubAuthorization is not { } auth) return;
        var result = await providerService.PollGitHubDeviceFlowAsync(auth, CancellationToken.None);
        ViewModel.SetCopilotDeviceResult(string.Empty, string.Empty, result.IsSuccess ? CopilotUiState.Completed : CopilotUiState.Failed);
    }
    private void CopilotOpenClick(object? sender, RoutedEventArgs e) { if (githubAuthorization is { } auth) _ = Launcher.LaunchUriAsync(auth.VerificationUri); }
    private void CopilotCopyClick(object? sender, RoutedEventArgs e) { if (githubAuthorization is { } auth) TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(auth.UserCode); }
    private void OpenChatGptClick(object? sender, RoutedEventArgs e) => _ = Launcher.LaunchUriAsync(new Uri(OfficialUsageUrls.ChatGpt));
    private async void CodexStartClick(object? sender, RoutedEventArgs e)
    {
        var result = await providerService.StartCodexAsync(CancellationToken.None);
        ViewModel.SetCodexResult(result);
        if (result.Success) await OpenCodexBrowserAsync();
    }
    private async void CodexPollClick(object? sender, RoutedEventArgs e)
    {
        var result = await providerService.PollCodexAsync(CancellationToken.None);
        ViewModel.SetCodexResult(result);
        if (result.Success) await ViewModel.RefreshAsync();
    }
    private async void CodexLogoutClick(object? sender, RoutedEventArgs e)
    {
        CodexUiResult result;
        try { result = await providerService.LogoutCodexAsync(CancellationToken.None); }
        catch { result = new(false, CodexAuthorizationState.Error, "Unable to disconnect Codex."); }
        ViewModel.SetCodexResult(result);
        if (result.Success)
        {
            ViewModel.RemoveCodexCards();
            await ViewModel.RefreshAsync();
        }
    }
    private async void CodexOpenClick(object? sender, RoutedEventArgs e) => await OpenCodexBrowserAsync();
    private async Task OpenCodexBrowserAsync()
    {
        if (ViewModel.CodexVerificationUri is not { } uri) return;
        try { ViewModel.SetCodexBrowserStatus(await Launcher.LaunchUriAsync(uri)); }
        catch { ViewModel.SetCodexBrowserStatus(false); }
    }
    private void OpenClaudeClick(object? sender, RoutedEventArgs e) => _ = Launcher.LaunchUriAsync(new Uri(OfficialUsageUrls.Claude));
    private void OpenCopilotClick(object? sender, RoutedEventArgs e) => _ = Launcher.LaunchUriAsync(new Uri(OfficialUsageUrls.Copilot));
    private async void DeleteHistoryClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button { Tag: Guid id }) await ViewModel.History.DeleteAsync(id);
        }
        catch
        {
            ViewModel.Notify(ViewModel.CopyText.HistoryNotificationTitle, ViewModel.CopyText.HistoryDeleteFailure);
        }
    }
    private async void DeleteAllHistoryClick(object? sender, RoutedEventArgs e)
    {
        try { await ViewModel.History.DeleteAllAsync(); }
        catch { ViewModel.Notify(ViewModel.CopyText.HistoryNotificationTitle, ViewModel.CopyText.HistoryDeleteAllFailure); }
    }
    private async void ExportCsvClick(object? sender, RoutedEventArgs e) => await ExportAsync("quotasight-history.csv", "text/csv", ViewModel.History.ExportCsv());
    private async void ExportJsonClick(object? sender, RoutedEventArgs e) => await ExportAsync("quotasight-history.json", "application/json", ViewModel.History.ExportJson());
    private async Task ExportAsync(string suggestedName, string contentType, string content)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            FileTypeChoices = [new FilePickerFileType(contentType) { Patterns = ["*.*"] }]
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content);
    }
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (App.ExitRequested || !TrayAvailable) return;
        e.Cancel = true;
        Hide();
    }
}

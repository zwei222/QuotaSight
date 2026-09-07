using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Layout;
using QuotaSight.Core;
using QuotaSight.Infrastructure;

namespace QuotaSight.UI;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private readonly IProviderUiService providerService;
    public bool TrayAvailable { get; set; }
    private bool isInitializing = true;
    private bool? appliedCompactLayout;
    private Task? initializationTask;
    public bool IsCompactLayout => Width > 0 && Width < 760;
    public MainWindow() : this(CompositionRoot.CreateMainWindowParts()) { }
    public MainWindow((MainViewModel ViewModel, IProviderUiService Provider) parts) : this(parts.ViewModel, parts.Provider) { }
    public MainWindow(MainViewModel viewModel) : this(viewModel, new UiProviderFacade()) { }
    public MainWindow(MainViewModel viewModel, IProviderUiService providerService)
    {
        ViewModel = viewModel;
        this.providerService = providerService;
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
        var provider = this.FindControl<ComboBox>("ManualProviderBox")!;
        var window = this.FindControl<ComboBox>("ManualWindowBox")!;
        var theme = this.FindControl<ComboBox>("ThemeBox")!;
        var language = this.FindControl<ComboBox>("LanguageBox")!;
        var accountFilter = this.FindControl<ComboBox>("HistoryAccountFilter")!;
        var windowFilter = this.FindControl<ComboBox>("HistoryWindowFilter")!;
        provider.ItemsSource = new[] { ProviderKind.ChatGpt, ProviderKind.Claude, ProviderKind.Copilot };
        window.ItemsSource = Enum.GetValues<QuotaWindowKind>();
        theme.ItemsSource = Enum.GetValues<ThemeMode>();
        language.ItemsSource = UiSettings.SupportedLanguages;
        accountFilter.ItemsSource = new[] { "All accounts", "Personal", "Work" };
        windowFilter.ItemsSource = new[] { "All windows", "Weekly" };
        provider.SelectedIndex = 0;
        window.SelectedItem = QuotaWindowKind.Weekly;
        theme.SelectedItem = ViewModel.Settings.Theme;
        language.SelectedIndex = ViewModel.Language == UiLanguage.Japanese ? 1 : 0;
        this.FindControl<NumericUpDown>("RefreshMinutesBox")!.Value = ViewModel.Settings.RefreshMinutes;
        this.FindControl<NumericUpDown>("ThresholdBox")!.Value = ViewModel.Settings.OverallThreshold;
        this.FindControl<CheckBox>("NotificationsBox")!.IsChecked = ViewModel.Settings.NotificationsEnabled;
        this.FindControl<TextBox>("GithubClientIdBox")!.Text = ViewModel.Settings.GithubOAuthClientId;
        accountFilter.SelectedIndex = 0;
        windowFilter.SelectedIndex = 0;
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
        }
    }

    private void DashboardClick(object? sender, RoutedEventArgs e) => ViewModel.Navigate(AppPage.Dashboard);
    private void HistoryClick(object? sender, RoutedEventArgs e) => ViewModel.Navigate(AppPage.History);
    private void SettingsClick(object? sender, RoutedEventArgs e) => ViewModel.Navigate(AppPage.Settings);
    private async void RefreshClick(object? sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();
    private async void ThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!isInitializing && sender is ComboBox box && box.SelectedItem is ThemeMode theme) await ViewModel.SetThemeAsync(theme);
    }
    private async void LanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!isInitializing && sender is ComboBox box && box.SelectedItem is string language) await ViewModel.SetLanguageAsync(language == "日本語" ? UiLanguage.Japanese : UiLanguage.English);
    }
    private async void RefreshMinutesChanged(object? sender, NumericUpDownValueChangedEventArgs e) { if (!isInitializing && e.NewValue is { } value) await ViewModel.SetRefreshMinutesAsync((int)value); }
    private async void NotificationsChanged(object? sender, RoutedEventArgs e) { if (!isInitializing && sender is CheckBox box) await ViewModel.SetNotificationsAsync(box.IsChecked == true); }
    private async void ThresholdChanged(object? sender, NumericUpDownValueChangedEventArgs e) { if (!isInitializing && e.NewValue is { } value) await ViewModel.SetThresholdAsync(value); }
    private async void GithubClientIdChanged(object? sender, TextChangedEventArgs e)
    {
        if (isInitializing || sender is not TextBox box) return;
        var clientId = box.Text ?? string.Empty;
        await ViewModel.SetGithubClientIdAsync(clientId);
        if (providerService is UiProviderFacade facade) facade.UpdateGitHubClientId(clientId);
    }
    private void HistoryAccountFilterChanged(object? sender, SelectionChangedEventArgs e) { if (sender is ComboBox box && box.SelectedItem is string value) ViewModel.History.AccountFilter = value; }
    private void HistoryWindowFilterChanged(object? sender, SelectionChangedEventArgs e) { if (sender is ComboBox box && box.SelectedItem is string value) ViewModel.History.WindowFilter = value; }
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
            Provider = providerBox?.SelectedItem is ProviderKind provider ? provider : ProviderKind.Manual,
            AccountDisplayName = accountBox?.Text ?? string.Empty,
            Window = windowBox?.SelectedItem is QuotaWindowKind window ? window : QuotaWindowKind.Weekly,
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
        if (validation is not null) validation.Text = result.Message;
    }
    private DeviceAuthorizationStart? githubAuthorization;
    private async void CopilotStartClick(object? sender, RoutedEventArgs e)
    {
        var result = await providerService.StartGitHubDeviceFlowAsync(CancellationToken.None);
        if (result.IsSuccess && result.Value is { } auth) { githubAuthorization = auth; ViewModel.SetCopilotDeviceResult(auth.VerificationUri.ToString(), auth.UserCode); }
        else ViewModel.SetCopilotDeviceResult(string.Empty, result.Error ?? "GitHub device flow unavailable.");
    }
    private async void CopilotProbeClick(object? sender, RoutedEventArgs e) => ViewModel.SetCopilotDeviceResult(string.Empty, await providerService.ProbeGitHubCliAsync(CancellationToken.None));
    private async void CopilotPollClick(object? sender, RoutedEventArgs e)
    {
        if (githubAuthorization is not { } auth) return;
        var result = await providerService.PollGitHubDeviceFlowAsync(auth, CancellationToken.None);
        ViewModel.SetCopilotDeviceResult(auth.VerificationUri.ToString(), result.IsSuccess ? "GitHub authorization completed; token stored securely." : result.Error ?? "GitHub authorization failed.");
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
            ViewModel.Notify("History", "Unable to delete this history entry.");
        }
    }
    private async void DeleteAllHistoryClick(object? sender, RoutedEventArgs e)
    {
        try { await ViewModel.History.DeleteAllAsync(); }
        catch { ViewModel.Notify("History", "Unable to delete history."); }
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

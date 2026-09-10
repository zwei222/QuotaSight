using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;

using QuotaSight.Core;
using QuotaSight.Application;
using QuotaSight.Infrastructure;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class UiRequirementsTests
{
    [AvaloniaFact]
    public async Task Codex_code_copy_uses_injected_clipboard_service()
    {
        var clipboard = new RecordingClipboard();
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource()), new UiProviderFacade(), clipboard);
        window.ViewModel.SetCodexResult(new(true, CodexAuthorizationState.AwaitingAuthorization, "waiting", new("DEVICE-123", new Uri("https://example.test"), DateTimeOffset.UtcNow.AddMinutes(5))));

        await window.CopyCodexCodeAsync();

        Assert.Equal("DEVICE-123", clipboard.LastText);
    }

    [AvaloniaFact]
    public async Task Codex_copy_button_click_copies_only_the_code_once_and_empty_or_missing_clipboard_is_safe()
    {
        var clipboard = new RecordingClipboard();
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource()), new UiProviderFacade(), clipboard);
        window.ViewModel.SetCodexResult(new(true, CodexAuthorizationState.AwaitingAuthorization, "waiting", new("DEVICE-456", new Uri("https://example.test"), DateTimeOffset.UtcNow.AddMinutes(5))));
        window.FindControl<Button>("CodexCopyButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(10);
        Assert.Equal(1, clipboard.CopyCount);
        Assert.Equal("DEVICE-456", clipboard.LastText);
        window.ViewModel.SetCodexResult(new(false, CodexAuthorizationState.Disconnected, "disconnected"));
        window.FindControl<Button>("CodexCopyButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(10);
        Assert.Equal(1, clipboard.CopyCount);
        window.Close();

        var noClipboardWindow = new MainWindow(new MainViewModel(new EmptyDashboardSource()), new UiProviderFacade());
        noClipboardWindow.ViewModel.SetCodexResult(new(false, CodexAuthorizationState.Disconnected, "disconnected"));
        var exception = Record.Exception(() => noClipboardWindow.FindControl<Button>("CodexCopyButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        Assert.Null(exception);
        noClipboardWindow.Close();
    }

    [AvaloniaFact]
    public async Task Add_provider_flow_shows_selection_then_only_selected_provider_panel_and_can_close()
    {
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource()), new UiProviderFacade());
        window.Show();
        await window.InitializeAsync();
        var add = window.FindControl<Button>("AddProviderButton")!;
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(window.FindControl<ComboBox>("ProviderPicker")!.IsVisible);
        Assert.False(window.FindControl<Border>("ManualProviderPanel")!.IsVisible);

        var picker = window.FindControl<ComboBox>("ProviderPicker")!;
        var codex = window.ViewModel.ProviderChoices.Single(item => item.Choice == ProviderConnectionChoice.Codex);
        picker.SelectedItem = codex;
        picker.RaiseEvent(new SelectionChangedEventArgs(ComboBox.SelectionChangedEvent, new List<object?>(), new List<object?> { codex }));
        Assert.True(window.FindControl<Border>("CodexCard")!.IsVisible);
        Assert.False(window.FindControl<Border>("OpenCodePanel")!.IsVisible);
        Assert.True(window.FindControl<Button>("CloseProviderButton")!.IsVisible);

        window.ViewModel.CloseProviderFlow();
        Assert.False(window.FindControl<ComboBox>("ProviderPicker")!.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Reopening_provider_flow_shows_picker_without_previous_panel()
    {
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource()), new UiProviderFacade());
        window.Show();
        await window.InitializeAsync();
        var add = window.FindControl<Button>("AddProviderButton")!;
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var picker = window.FindControl<ComboBox>("ProviderPicker")!;
        var codex = window.ViewModel.ProviderChoices.Single(item => item.Choice == ProviderConnectionChoice.Codex);
        picker.SelectedItem = codex;
        picker.RaiseEvent(new SelectionChangedEventArgs(ComboBox.SelectionChangedEvent, new List<object?>(), new List<object?> { codex }));
        window.ViewModel.CloseProviderFlow();

        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(picker.IsVisible);
        Assert.False(window.FindControl<Border>("CodexCard")!.IsVisible);
        Assert.False(window.FindControl<Border>("ManualProviderPanel")!.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Real_provider_picker_selection_opens_chatgpt_and_same_provider_without_manual_raise_event()
    {
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource()), new UiProviderFacade());
        window.Show();
        await window.InitializeAsync();
        var add = window.FindControl<Button>("AddProviderButton")!;
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var picker = window.FindControl<ComboBox>("ProviderPicker")!;
        var chatGpt = window.ViewModel.ProviderChoices.Single(item => item.Choice == ProviderConnectionChoice.ChatGpt);

        picker.SelectedItem = chatGpt;
        Assert.True(window.FindControl<Border>("ManualProviderPanel")!.IsVisible);
        var manualProvider = Assert.IsType<LocalizedChoice<ProviderKind>>(window.FindControl<ComboBox>("ManualProviderBox")!.SelectedItem);
        Assert.Equal(ProviderKind.ChatGpt, manualProvider.Value);
        Assert.Equal("ChatGPT", manualProvider.DisplayName);
        picker.SelectedItem = null;
        picker.SelectedItem = chatGpt;
        Assert.True(window.FindControl<Border>("ManualProviderPanel")!.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Language_switch_keeps_closed_provider_flow_closed()
    {
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource()), new UiProviderFacade());
        window.Show();
        await window.InitializeAsync();

        var language = window.FindControl<ComboBox>("LanguageBox")!;
        language.SelectedItem = "日本語";
        await Task.Delay(50);

        Assert.False(window.ViewModel.IsProviderFlowOpen);
        Assert.False(window.FindControl<ComboBox>("ProviderPicker")!.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Quota_refresh_status_is_visible_disables_refresh_and_is_announced_until_done()
    {
        var application = new BlockingRefreshApplication();
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource(), quotaApplication: application), new UiProviderFacade());
        window.Show();

        var refresh = window.ViewModel.RefreshAsync();
        await application.Started.Task;

        var status = window.FindControl<Border>("QuotaRefreshStatus")!;
        Assert.True(status.IsVisible);
        Assert.True(window.FindControl<ProgressBar>("QuotaRefreshProgressBar")!.IsIndeterminate);
        Assert.False(window.FindControl<Button>("RefreshButton")!.IsEnabled);
        Assert.Equal(window.ViewModel.QuotaStatusText, AutomationProperties.GetName(status));

        application.Release.TrySetResult(true);
        await refresh;
        Assert.False(status.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Language_switch_preserves_manual_controls_and_saves_the_selected_claude_monthly_identity()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        MainWindow? window = null;
        MainViewModel? viewModel = null;
        JsonlQuotaHistory? history = null;
        try
        {
            var store = new AppSettingsStore(root);
            const string githubClientId = "github-client-before-language-switch";
            store.Save(new AppSettingsDto(Language: "English", GithubOAuthClientId: githubClientId));
            history = new JsonlQuotaHistory(root);
            viewModel = new MainViewModel(new EmptyDashboardSource(), new ManualQuotaService(history), history, settingsStore: store);
            window = new MainWindow(viewModel, new UiProviderFacade());
            window.Show();
            await window.InitializeAsync();
            var githubClientIdBox = window.FindControl<TextBox>("GithubClientIdBox")!;
            Assert.Equal(githubClientId, githubClientIdBox.Text);
            Assert.Equal(githubClientId, viewModel.Settings.GithubOAuthClientId);

            window.FindControl<Button>("AddProviderButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.FindControl<ComboBox>("ProviderPicker")!.SelectedItem = window.ViewModel.ProviderChoices.Single(item => item.Choice == ProviderConnectionChoice.Claude);
            var provider = window.FindControl<ComboBox>("ManualProviderBox")!;
            var quotaWindow = window.FindControl<ComboBox>("ManualWindowBox")!;
            var account = window.FindControl<TextBox>("AccountNameBox")!;
            var percent = window.FindControl<TextBox>("UsedPercentBox")!;
            var reset = window.FindControl<TextBox>("ResetAtBox")!;
            provider.SelectedItem = UiSettings.ManualProviderChoices(UiLanguage.English).Single(item => item.Value == ProviderKind.Claude);
            quotaWindow.SelectedItem = UiSettings.ManualWindowChoices(UiLanguage.English).Single(item => item.Value == QuotaWindowKind.Monthly);
            account.Text = "Claude account";
            percent.Text = "37";
            reset.Text = string.Empty;

            window.FindControl<ComboBox>("LanguageBox")!.SelectedItem = "日本語";
            await WaitForAsync(async () => (await store.LoadAsync()).Language == "Japanese");
            var savedSettings = await store.LoadAsync();

            Assert.Equal(UiLanguage.Japanese, viewModel.Language);
            Assert.Equal(githubClientId, githubClientIdBox.Text);
            Assert.Equal(githubClientId, viewModel.Settings.GithubOAuthClientId);
            Assert.Equal(githubClientId, savedSettings.GithubOAuthClientId);
            Assert.Equal("Japanese", savedSettings.Language);
            Assert.Equal(ProviderKind.Claude, Assert.IsType<LocalizedChoice<ProviderKind>>(provider.SelectedItem).Value);
            Assert.Equal<QuotaWindowKind>(QuotaWindowKind.Monthly, ((LocalizedChoice<QuotaWindowKind>)quotaWindow.SelectedItem!).Value);
            Assert.Equal("Claude account", account.Text);
            Assert.Equal("37", percent.Text);
            Assert.Equal(string.Empty, reset.Text);

            var addAccount = window.GetVisualDescendants().OfType<Button>().Single(button => button.Content?.ToString() == window.ViewModel.CopyText.AddAccount);
            addAccount.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => Task.FromResult(window.ViewModel.History.Entries.Any(entry => entry.Account == "Claude account")));
            var saved = Assert.Single(window.ViewModel.History.Entries, entry => entry.Account == "Claude account");
            Assert.Equal("Claude", saved.Provider);
            Assert.Equal("Monthly", saved.Window);
        }
        finally
        {
            window?.Close();
            if (viewModel is not null) viewModel.Dispose();
            else history?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task Github_client_id_same_value_text_change_does_not_mutate_but_changed_input_persists()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        MainWindow? window = null;
        MainViewModel? viewModel = null;
        try
        {
            var store = new AppSettingsStore(root);
            const string originalClientId = "github-client-original";
            store.Save(new AppSettingsDto(GithubOAuthClientId: originalClientId));
            var viewModelFactory = new RecordingGithubClientFactory();
            var facadeFactory = new RecordingGithubClientFactory();
            viewModel = new MainViewModel(new EmptyDashboardSource(), settingsStore: store, githubFactory: viewModelFactory);
            window = new MainWindow(viewModel, new UiProviderFacade(githubFactory: facadeFactory));
            window.Show();
            await window.InitializeAsync();

            var clientIdBox = window.FindControl<TextBox>("GithubClientIdBox")!;
            Assert.Equal(1, viewModelFactory.CreateCount);
            Assert.Equal(0, facadeFactory.CreateCount);

            clientIdBox.Text = originalClientId;
            await Task.Delay(50);

            Assert.Equal(originalClientId, viewModel.Settings.GithubOAuthClientId);
            Assert.Equal(1, viewModelFactory.CreateCount);
            Assert.Equal(0, facadeFactory.CreateCount);

            const string changedClientId = "github-client-changed";
            clientIdBox.Text = $"  {changedClientId}  ";
            await WaitForAsync(async () =>
                (await store.LoadAsync()).GithubOAuthClientId == changedClientId
                && facadeFactory.CreateCount == 1);

            Assert.Equal(changedClientId, viewModel.Settings.GithubOAuthClientId);
            Assert.Equal(2, viewModelFactory.CreateCount);
            Assert.Equal(1, facadeFactory.CreateCount);
        }
        finally
        {
            window?.Close();
            viewModel?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Japanese_language_switch_relocalizes_existing_usage_rows()
    {
        var vm = new MainViewModel(new DemoDashboardSource());
        var english = vm.Cards[0].Windows[0];
        vm.Language = UiLanguage.Japanese;
        var japanese = vm.Cards[0].Windows[0];

        Assert.Contains("使用", japanese.PercentText);
        Assert.Contains("残り", japanese.PercentText);
        Assert.Contains("更新", japanese.FreshnessText);
        Assert.NotEqual(english.PercentText, japanese.PercentText);
    }

    [Fact]
    public void Japanese_usage_copy_uses_natural_status_and_metric_labels()
    {
        var now = new DateTimeOffset(2026, 9, 6, 14, 30, 0, TimeSpan.FromHours(9));
        var snapshot = DemoSnapshot(42) with { Fetched = now.AddMinutes(-5), Metric = "Messages" };

        var row = QuotaPresentationFormatter.Format(snapshot, now, UiLanguage.Japanese);

        Assert.Equal("使用済み 42%・残り 58%", row.PercentText);
        Assert.Equal("余裕あり", row.StatusText);
        Assert.Equal("メッセージ", row.Metric);
        Assert.Equal("5分前に更新", row.FreshnessText);
        Assert.Contains("使用済み", row.ProgressLabel);
    }

    [Fact]
    public void Japanese_metric_mapping_keeps_unknown_metric_verbatim()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Equal("リクエスト", QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Metric = "Requests" }, now, UiLanguage.Japanese).Metric);
        Assert.Equal("短時間枠", QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Metric = "Fast window" }, now, UiLanguage.Japanese).Metric);
        Assert.Equal("月次", QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Metric = "monthly" }, now, UiLanguage.Japanese).Metric);
        Assert.Equal(string.Empty, QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Metric = "weekly" }, now, UiLanguage.Japanese).Metric);
        Assert.Equal("Codex主要枠", QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Metric = "Codex primary" }, now, UiLanguage.Japanese).Metric);
        Assert.Equal("Codex副枠", QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Metric = "Codex secondary" }, now, UiLanguage.Japanese).Metric);
        Assert.Equal("Custom metric", QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Metric = "Custom metric" }, now, UiLanguage.Japanese).Metric);
    }

    [Fact]
    public void Japanese_copy_uses_natural_labels_without_enum_status_words()
    {
        var copy = new UiCopy(UiLanguage.Japanese);

        Assert.Equal("利用枠", copy.Window);
        Assert.Equal("使用率（%）", copy.UsedPercent);
        Assert.Equal("リセット日時（任意）", copy.ResetOptional);
        Assert.DoesNotContain("device flow", copy.CopilotDescription, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Client Secret", copy.CopilotDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("デバイス認証", copy.CodexDescription);
        Assert.DoesNotContain("ローリング", copy.HistoryDescription);
        Assert.DoesNotContain("Unsupported", copy.Autostart);
        Assert.DoesNotContain("Unavailable", copy.CredentialUnavailable("Unavailable"));
    }

    [Fact]
    public void Japanese_provider_state_text_is_safe_and_localized_from_structured_state()
    {
        var vm = new MainViewModel(new EmptyDashboardSource()) { Language = UiLanguage.Japanese };
        vm.SetCodexResult(new(false, CodexAuthorizationState.Disconnected, "Codex is not connected."));

        Assert.Equal("Codexは未接続です。", vm.CodexStatusText);
        vm.SetCopilotDeviceResult(string.Empty, "GitHub authorization completed; token stored securely.");
        Assert.DoesNotContain("token", vm.CopilotDeviceResult, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("認証", vm.CopilotDeviceResult);
    }

    [Fact]
    public void Copilot_initial_state_is_idle_and_localized_without_claiming_failure()
    {
        var english = new MainViewModel(new EmptyDashboardSource());
        Assert.Equal("Copilot authentication has not started yet.", english.CopilotDeviceResult);

        var japanese = new MainViewModel(new EmptyDashboardSource()) { Language = UiLanguage.Japanese };
        Assert.Equal("Copilotの認証はまだ開始されていません。", japanese.CopilotDeviceResult);
    }

    [Fact]
    public void Copilot_started_keeps_safe_url_and_code_across_language_switch_and_terminal_states_clear_code()
    {
        var vm = new MainViewModel(new EmptyDashboardSource());
        vm.SetCopilotDeviceResult("https://github.example/device", "ABCD-EFGH", CopilotUiState.Started);
        Assert.Contains("https://github.example/device", vm.CopilotDeviceResult);
        Assert.Contains("ABCD-EFGH", vm.CopilotDeviceResult);
        vm.Language = UiLanguage.Japanese;
        Assert.Contains("https://github.example/device", vm.CopilotDeviceResult);
        Assert.Contains("ABCD-EFGH", vm.CopilotDeviceResult);
        vm.SetCopilotDeviceResult("https://ignored", "ignored", CopilotUiState.Completed);
        Assert.DoesNotContain("ignored", vm.CopilotDeviceResult);
        vm.SetCopilotDeviceResult("https://ignored", "ignored", CopilotUiState.Failed);
        Assert.DoesNotContain("ignored", vm.CopilotDeviceResult);
        vm.SetCopilotDeviceResult("https://ignored", "ignored", CopilotUiState.GhProbe);
        Assert.DoesNotContain("ignored", vm.CopilotDeviceResult);
    }

    [Fact]
    public void Japanese_history_choices_and_manual_window_choices_are_localized_without_changing_values()
    {
        var history = UiSettings.HistoryAccountChoices(UiLanguage.Japanese);
        var windows = UiSettings.HistoryWindowChoices(UiLanguage.Japanese);
        var manual = UiSettings.ManualWindowChoices(UiLanguage.Japanese);

        Assert.Equal("すべてのアカウント", history.Single(item => item.Value == "All accounts").DisplayName);
        Assert.Equal("個人", history.Single(item => item.Value == "Personal").DisplayName);
        Assert.Equal("すべての利用枠", windows.Single(item => item.Value == "All windows").DisplayName);
        Assert.Equal("週次", windows.Single(item => item.Value == "Weekly").DisplayName);
        Assert.Equal(QuotaWindowKind.Weekly, manual.Single(item => item.Value == QuotaWindowKind.Weekly).Value);
        Assert.Equal("週次", manual.Single(item => item.Value == QuotaWindowKind.Weekly).DisplayName);
    }

    [Fact]
    public void Usage_band_exposes_text_numeric_and_distinct_fill_brush()
    {
        var normal = QuotaPresentationFormatter.Format(DemoSnapshot(42), DateTimeOffset.UtcNow);
        var danger = QuotaPresentationFormatter.Format(DemoSnapshot(92), DateTimeOffset.UtcNow);
        var over = QuotaPresentationFormatter.Format(DemoSnapshot(125), DateTimeOffset.UtcNow);

        Assert.Equal(42d, normal.VisualPercent);
        Assert.Contains("Normal", normal.StatusText);
        Assert.Contains("92", danger.StatusText);
        Assert.Contains("Over", over.StatusText);
        Assert.NotEqual(normal.Band, danger.Band);
        Assert.NotEqual(danger.Band, over.Band);
    }

    [Fact]
    public void Usage_band_is_rendered_by_semantic_progress_bar_classes_not_fixed_brushes()
    {
        Assert.NotNull(typeof(QuotaRowViewModel).GetProperty(nameof(QuotaRowViewModel.IsAttention)));
        Assert.Null(typeof(QuotaRowViewModel).GetProperty("BandBrush"));
    }

    [Fact]
    public void Japanese_reset_uses_natural_local_datetime_and_direct_relative_wording()
    {
        var now = new DateTimeOffset(2026, 9, 6, 14, 30, 0, TimeSpan.FromHours(9));
        var reset = new DateTimeOffset(2026, 9, 8, 14, 30, 0, TimeSpan.FromHours(9));

        var text = QuotaPresentationFormatter.FormatReset(reset, now, UiLanguage.Japanese);
        var localReset = reset.ToLocalTime().ToString("M月d日(ddd) HH:mm", System.Globalization.CultureInfo.GetCultureInfo("ja-JP"));

        Assert.Equal($"{localReset}にリセット（あと2日）", text);
    }

    [Fact]
    public void Provider_choices_have_localized_display_names_while_preserving_identity()
    {
        var japanese = UiSettings.ProviderChoices(UiLanguage.Japanese);
        var english = UiSettings.ProviderChoices(UiLanguage.English);

        Assert.Equal("ChatGPT", japanese.Single(item => item.Choice == ProviderConnectionChoice.ChatGpt).DisplayName);
        Assert.Equal("Claude", english.Single(item => item.Choice == ProviderConnectionChoice.Claude).DisplayName);
        Assert.DoesNotContain(japanese, item => item.DisplayName == nameof(ProviderConnectionChoice.ChatGpt));
    }

    [Fact]
    public void Japanese_window_and_metric_copy_avoids_duplicate_synonyms_and_localizes_window_terms()
    {
        var now = DateTimeOffset.UtcNow;
        var monthly = QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Window = new(QuotaWindowKind.Monthly, now.AddHours(-1), now.AddHours(1)), Metric = "monthly" }, now, UiLanguage.Japanese);
        var rolling = QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Window = new(QuotaWindowKind.Rolling, now.AddHours(-1), now.AddHours(1)), Metric = "Fast window" }, now, UiLanguage.Japanese);

        Assert.Equal("月次", monthly.WindowName);
        Assert.Equal(string.Empty, monthly.Metric);
        Assert.Equal("monthly", QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Window = monthly.Snapshot!.Window, Metric = "monthly" }, now, UiLanguage.English).Metric);
        Assert.Equal("ローリング枠", rolling.WindowName);
        Assert.Equal("短時間枠", rolling.Metric);
    }

    [Fact]
    public void Japanese_over_limit_and_unknown_usage_copy_are_single_source_of_truth()
    {
        var now = DateTimeOffset.UtcNow;
        var over = QuotaPresentationFormatter.Format(DemoSnapshot(112), now, UiLanguage.Japanese);
        var unknown = QuotaPresentationFormatter.Format(DemoSnapshot(0) with { Used = null, Limit = null }, now, UiLanguage.Japanese);

        Assert.Equal("使用済み 112%", over.PercentText);
        Assert.Equal("上限を12%超過", over.StatusText);
        Assert.Equal("使用量を表示できません", unknown.PercentText);
        Assert.Equal("未取得", unknown.StatusText);
        Assert.Contains("。", unknown.ProgressLabel);
        Assert.DoesNotContain("取得中", unknown.ProgressLabel);
    }

    [Fact]
    public void Japanese_copy_localizes_theme_labels_and_notification_threshold()
    {
        var choices = UiSettings.ThemeChoices(UiLanguage.Japanese);
        var copy = new UiCopy(UiLanguage.Japanese);

        Assert.Equal("システム", choices.Single(item => item.Value == ThemeMode.System).DisplayName);
        Assert.Equal("ライト", choices.Single(item => item.Value == ThemeMode.Light).DisplayName);
        Assert.Equal("ダーク", choices.Single(item => item.Value == ThemeMode.Dark).DisplayName);
        Assert.Equal("通知する使用率（%）", copy.OverallThreshold);
        Assert.Equal("認証状態を確認", copy.CodexConfirm);
        Assert.Equal("認証状態を確認", copy.Poll);
        Assert.Equal("GitHub App Client ID（秘密情報ではありません）", copy.GithubClientId);
    }

    [Fact]
    public void Japanese_demo_accounts_localize_but_named_organization_does_not()
    {
        var vm = new MainViewModel(new DemoDashboardSource());
        vm.Language = UiLanguage.Japanese;
        Assert.Contains(vm.Cards, card => card.Account == "個人アカウント");
        Assert.Contains(vm.Cards, card => card.Account == "ワークスペース · デモ");
        Assert.Contains(vm.Cards, card => card.Account == "Acme Engineering");
        vm.Language = UiLanguage.English;
        Assert.Contains(vm.Cards, card => card.Account == "Personal account");
        Assert.Contains(vm.Cards, card => card.Account == "Workspace · demo");
    }

    [Fact]
    public void Theme_and_provider_choice_values_survive_localization()
    {
        var theme = UiSettings.ThemeChoices(UiLanguage.Japanese).Single(item => item.Value == ThemeMode.Dark);
        var provider = UiSettings.ManualProviderChoices(UiLanguage.Japanese).Single(item => item.Value == ProviderKind.Copilot);

        Assert.Equal(ThemeMode.Dark, theme.Value);
        Assert.Equal("ダーク", theme.DisplayName);
        Assert.Equal(ProviderKind.Copilot, provider.Value);
        Assert.Equal("GitHub Copilot", provider.DisplayName);
    }

    [Fact]
    public void History_window_display_localizes_without_changing_filter_or_export_identity()
    {
        var history = new HistoryState();
        history.WindowFilter = "Weekly";
        history.SetLanguage(UiLanguage.Japanese);

        Assert.All(history.Entries, entry => Assert.Equal("週次", entry.WindowDisplay));
        Assert.Equal("Weekly", history.WindowFilter);
        Assert.Contains("Weekly", history.ExportCsv(), StringComparison.Ordinal);

        history.SetLanguage(UiLanguage.English);
        Assert.All(history.Entries, entry => Assert.Equal("Weekly", entry.WindowDisplay));
    }

    [AvaloniaFact]
    public async Task Japanese_language_switch_updates_rendered_theme_and_manual_provider_choices()
    {
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource()), new UiProviderFacade());
        window.Show();
        await window.InitializeAsync();

        window.FindControl<ComboBox>("ThemeBox")!.SelectedItem = UiSettings.ThemeChoices(UiLanguage.English).Single(item => item.Value == ThemeMode.Dark);
        window.FindControl<ComboBox>("ManualProviderBox")!.SelectedItem = UiSettings.ManualProviderChoices(UiLanguage.English).Single(item => item.Value == ProviderKind.Copilot);
        window.FindControl<ComboBox>("LanguageBox")!.SelectedItem = "日本語";
        await Task.Delay(50);

        var selectedTheme = Assert.IsType<LocalizedChoice<ThemeMode>>(window.FindControl<ComboBox>("ThemeBox")!.SelectedItem);
        var selectedProvider = Assert.IsType<LocalizedChoice<ProviderKind>>(window.FindControl<ComboBox>("ManualProviderBox")!.SelectedItem);
        Assert.Equal(ThemeMode.Dark, selectedTheme.Value);
        Assert.Equal("ダーク", selectedTheme.DisplayName);
        Assert.Equal(ProviderKind.Copilot, selectedProvider.Value);
        Assert.Equal("GitHub Copilot", selectedProvider.DisplayName);
        window.Close();
    }

    [Fact]
    public void Language_is_passed_to_new_cards_and_stale_state_round_trips()
    {
        var now = new DateTimeOffset(2026, 9, 6, 14, 30, 0, TimeSpan.FromHours(9));
        var snapshot = DemoSnapshot(42) with { Fetched = now.AddHours(-2), FreshUntil = now.AddHours(-1) };

        var japanese = DashboardAggregation.ToCards([snapshot], now, UiLanguage.Japanese).Single();
        Assert.Equal("更新できませんでした", japanese.StateText);
        Assert.Contains("データが古い可能性があります（最終更新: 2時間前）", japanese.Windows.Single().FreshnessText);

        var english = DashboardAggregation.ToCards([snapshot], now, UiLanguage.English).Single();
        Assert.Equal("Stale · refresh failed", english.StateText);
        Assert.Contains("Stale", english.Windows.Single().FreshnessText);
    }

    [AvaloniaFact]
    public void Rendered_progress_bars_use_the_four_semantic_theme_fills()
    {
        var window = new MainWindow(new MainViewModel(new DemoDashboardSource()), new UiProviderFacade());
        window.Show();

        foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            UiSettings.ApplyTheme(theme);
            var expected = new Dictionary<UsageBand, Color>
            {
                [UsageBand.Normal] = theme == ThemeMode.Light ? Color.Parse("#2457D6") : Color.Parse("#8EA7FF"),
                [UsageBand.Attention] = theme == ThemeMode.Light ? Color.Parse("#9A5B00") : Color.Parse("#FFD166"),
                [UsageBand.Danger] = theme == ThemeMode.Light ? Color.Parse("#B3261E") : Color.Parse("#FF8A80"),
                [UsageBand.OverLimit] = theme == ThemeMode.Light ? Color.Parse("#7A1FA2") : Color.Parse("#E0A0FF")
            };

            var bars = window.GetVisualDescendants().OfType<ProgressBar>().Where(bar => !bar.IsIndeterminate).ToList();
            Assert.Equal(8, bars.Count);
            foreach (var bar in bars)
            {
                Assert.NotNull(bar.DataContext);
                var row = Assert.IsType<QuotaRowViewModel>(bar.DataContext);
                Assert.Equal(expected[row.Band], ((ISolidColorBrush)bar.Foreground!).Color);
            }
        }

        window.Close();
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static QuotaSnapshot DemoSnapshot(decimal percent) => new(ProviderKind.ChatGpt, "acct", "Messages", new(QuotaWindowKind.Weekly, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)), percent, 100, null, "percent", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, QuotaSource.Manual, QuotaConfidence.Manual, DateTimeOffset.UtcNow.AddDays(1), "ChatGPT Plus");

    private sealed class RecordingClipboard : IClipboardService
    {
        public string? LastText { get; private set; }
        public int CopyCount { get; private set; }
        public Task SetTextAsync(string text) { CopyCount++; LastText = text; return Task.CompletedTask; }
    }

    private sealed class RecordingGithubClientFactory : IGitHubClientFactory
    {
        public int CreateCount { get; private set; }
        public GitHubDeviceFlowClient Create(string clientId)
        {
            CreateCount++;
            return new GitHubDeviceFlowClient(new HttpClient(), clientId);
        }
    }

    private sealed class BlockingRefreshApplication : IQuotaApplication
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return new QuotaRefreshResult([], []);
        }
    }
}

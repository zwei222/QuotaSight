using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;

using QuotaSight.Core;
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
    public async Task Language_switch_preserves_manual_controls_and_saves_the_selected_claude_monthly_identity()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AppSettingsStore(root);
            using var history = new JsonlQuotaHistory(root);
            var window = new MainWindow(new MainViewModel(new EmptyDashboardSource(), new ManualQuotaService(history), history, settingsStore: store), new UiProviderFacade());
            window.Show();
            await window.InitializeAsync();
            window.FindControl<Button>("AddProviderButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.FindControl<ComboBox>("ProviderPicker")!.SelectedItem = window.ViewModel.ProviderChoices.Single(item => item.Choice == ProviderConnectionChoice.Claude);
            var provider = window.FindControl<ComboBox>("ManualProviderBox")!;
            var quotaWindow = window.FindControl<ComboBox>("ManualWindowBox")!;
            var account = window.FindControl<TextBox>("AccountNameBox")!;
            var percent = window.FindControl<TextBox>("UsedPercentBox")!;
            var reset = window.FindControl<TextBox>("ResetAtBox")!;
            provider.SelectedItem = ProviderKind.Claude;
            quotaWindow.SelectedItem = UiSettings.ManualWindowChoices(UiLanguage.English).Single(item => item.Value == QuotaWindowKind.Monthly);
            account.Text = "Claude account";
            percent.Text = "37";
            reset.Text = string.Empty;

            var language = window.FindControl<ComboBox>("LanguageBox")!;
            language.SelectedItem = "日本語";
            await Task.Delay(50);

            Assert.Equal(ProviderKind.Claude, Assert.IsType<ProviderKind>(provider.SelectedItem));
            Assert.Equal<QuotaWindowKind>(QuotaWindowKind.Monthly, ((LocalizedChoice<QuotaWindowKind>)quotaWindow.SelectedItem!).Value);
            Assert.Equal("Claude account", account.Text);
            Assert.Equal("37", percent.Text);
            Assert.Equal(string.Empty, reset.Text);

            var addAccount = window.GetVisualDescendants().OfType<Button>().Single(button => button.Content?.ToString() == window.ViewModel.CopyText.AddAccount);
            addAccount.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(50);
            var saved = Assert.Single(window.ViewModel.History.Entries, entry => entry.Account == "Claude account");
            Assert.Equal("Claude", saved.Provider);
            Assert.Equal("Monthly", saved.Window);
            window.Close();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
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

        Assert.Equal("42% 使用済み・残り 58%", row.PercentText);
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
        Assert.Equal("高速枠", QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Metric = "Fast window" }, now, UiLanguage.Japanese).Metric);
        Assert.Equal("月次", QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Metric = "monthly" }, now, UiLanguage.Japanese).Metric);
        Assert.Equal("週次", QuotaPresentationFormatter.Format(DemoSnapshot(10) with { Metric = "weekly" }, now, UiLanguage.Japanese).Metric);
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

        Assert.Equal("9月8日(火) 14:30にリセット（あと2日）", text);
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

            var bars = window.GetVisualDescendants().OfType<ProgressBar>().ToList();
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

    private static QuotaSnapshot DemoSnapshot(decimal percent) => new(ProviderKind.ChatGpt, "acct", "Messages", new(QuotaWindowKind.Weekly, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)), percent, 100, null, "percent", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, QuotaSource.Manual, QuotaConfidence.Manual, DateTimeOffset.UtcNow.AddDays(1), "ChatGPT Plus");

    private sealed class RecordingClipboard : IClipboardService
    {
        public string? LastText { get; private set; }
        public int CopyCount { get; private set; }
        public Task SetTextAsync(string text) { CopyCount++; LastText = text; return Task.CompletedTask; }
    }
}

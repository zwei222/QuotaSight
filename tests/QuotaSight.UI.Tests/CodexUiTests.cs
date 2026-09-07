using System.Net;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using QuotaSight.Application;
using QuotaSight.Core;
using QuotaSight.Infrastructure;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class CodexUiTests
{
    [Fact]
    public async Task StartCodex_exposes_only_safe_prompt_and_no_device_identifier_or_token()
    {
        var store = new InMemoryCredentialStore();
        using var client = new HttpClient(new CodexHandler());
        var manager = CreateManager(client, store);
        var facade = new UiProviderFacade(credentialStore: store, codexSessionManager: manager);

        var result = await facade.StartCodexAsync(default);

        Assert.True(result.Success);
        Assert.Equal(CodexAuthorizationState.AwaitingAuthorization, result.State);
        Assert.NotNull(result.Prompt);
        Assert.Equal("SAFE-CODE", result.Prompt!.UserCode);
        Assert.DoesNotContain("device", string.Join('|', result.Prompt.GetType().GetProperties().Select(p => p.Name)), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", string.Join('|', result.Prompt.GetType().GetProperties().Select(p => p.Name)), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-response", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollCodex_reports_pending_then_success_and_stores_credential_without_returning_token()
    {
        var store = new InMemoryCredentialStore();
        var clock = new FixedTimeProvider();
        var handler = new CodexHandler(pendingOnce: true);
        using var client = new HttpClient(handler);
        var manager = CreateManager(client, store, clock);
        var facade = new UiProviderFacade(credentialStore: store, codexSessionManager: manager, timeProvider: clock);
        await facade.StartCodexAsync(default);

        var pending = await facade.PollCodexAsync(default);
        Assert.Equal(CodexAuthorizationState.Pending, pending.State);
        Assert.False(pending.Success);
        Assert.Equal(TimeSpan.FromSeconds(3), pending.RetryAfter);
        Assert.DoesNotContain("secret-response", pending.Message, StringComparison.Ordinal);

        var stillPending = await facade.PollCodexAsync(default);
        Assert.Equal(CodexAuthorizationState.Pending, stillPending.State);
        Assert.Equal(1, handler.DevicePollCount);

        clock.Advance(TimeSpan.FromSeconds(3));
        var success = await facade.PollCodexAsync(default);
        Assert.True(success.Success);
        Assert.Equal(CodexAuthorizationState.Connected, success.State);
        Assert.DoesNotContain("access-token", success.Message, StringComparison.Ordinal);
        Assert.NotNull(await store.GetAsync(CodexOAuthClient.CredentialKey, default));
    }

    [Fact]
    public async Task LogoutCodex_removes_credential_and_follow_up_fetch_is_unauthorized()
    {
        var store = new InMemoryCredentialStore();
        using var client = new HttpClient(new CodexHandler());
        var manager = CreateManager(client, store);
        var facade = new UiProviderFacade(credentialStore: store, codexSessionManager: manager);
        await facade.StartCodexAsync(default);
        await facade.PollCodexAsync(default);

        var logout = await facade.LogoutCodexAsync(default);

        Assert.True(logout.Success);
        Assert.Equal(CodexAuthorizationState.Disconnected, logout.State);
        Assert.Null(await store.GetAsync(CodexOAuthClient.CredentialKey, default));
        Assert.Equal(FetchStatus.Unauthorized, (await manager.FetchAsync(default)).Status);
    }

    [Fact]
    public async Task CompositionRoot_wires_the_same_codex_session_into_ui_and_automatic_refresh()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var client = new HttpClient(new CodexHandler());
        var store = new InMemoryCredentialStore();
        try
        {
            var parts = CompositionRoot.CreateMainWindowParts(root, client, store);
            using var viewModel = parts.ViewModel;
            var started = await parts.Provider.StartCodexAsync(default);
            Assert.True(started.Success);
            var connected = await parts.Provider.PollCodexAsync(default);
            Assert.True(connected.Success);
            Assert.NotNull(await store.GetAsync(CodexOAuthClient.CredentialKey, default));
            await viewModel.RefreshAsync();
            Assert.Contains(viewModel.Cards, card => card.Provider == QuotaSight.Core.ProviderKind.ChatGpt);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void Headless_codex_logout_button_tracks_safe_connection_error_and_disconnect_transitions()
    {
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource()), new UiProviderFacade());
        window.Show();
        window.ViewModel.Navigate(AppPage.Settings);
        var button = window.FindControl<Button>("CodexLogoutButton")!;

        window.ViewModel.SetCodexResult(new(true, CodexAuthorizationState.Connected, "Codex connected."));
        Assert.True(button.IsVisible);
        window.ViewModel.SetCodexResult(new(false, CodexAuthorizationState.Error, "Unable to disconnect Codex."));
        Assert.True(button.IsVisible);
        window.ViewModel.SetCodexResult(new(true, CodexAuthorizationState.Disconnected, "Codex disconnected."));
        Assert.False(button.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Headless_settings_card_starts_with_confirm_disabled_and_logout_hidden()
    {
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource()), new UiProviderFacade());
        window.Show();
        window.ViewModel.Navigate(AppPage.Settings);
        Assert.False(window.FindControl<Button>("CodexPollButton")!.IsEnabled);
        Assert.False(window.FindControl<Button>("CodexLogoutButton")!.IsVisible);
        Assert.Equal("OpenAI Codex", window.FindControl<Border>("CodexCard")!.Child is not null ? "OpenAI Codex" : string.Empty);
        window.Close();
    }

    [Fact]
    public void Codex_copy_and_xaml_keep_scope_accessibility_and_compact_layout_contract()
    {
        var japanese = new UiCopy(UiLanguage.Japanese);
        var english = new UiCopy(UiLanguage.English);
        Assert.Contains("Codex", japanese.CodexDescription, StringComparison.Ordinal);
        Assert.Contains("ChatGPT Plus/Pro", japanese.CodexDescription, StringComparison.Ordinal);
        Assert.Contains("Codex", english.CodexDescription, StringComparison.Ordinal);
        Assert.Contains("ChatGPT Plus/Pro", english.CodexDescription, StringComparison.Ordinal);
        Assert.Contains("Experimental", english.CodexBadge, StringComparison.Ordinal);

        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "QuotaSight.UI", "Views", "MainWindow.axaml");
        var xaml = File.ReadAllText(Path.GetFullPath(path));
        Assert.Contains("CodexStartButton", xaml, StringComparison.Ordinal);
        Assert.Contains("CodexPollButton", xaml, StringComparison.Ordinal);
        Assert.Contains("CodexLogoutButton", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding CopyText.Codex", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"440\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyProviderSnapshots_replaces_same_codex_identity_without_duplicate_card_or_window_and_keeps_manual_chatgpt()
    {
        var vm = new MainViewModel(new EmptyDashboardSource());
        var manual = Snapshot(18, "manual-account", "Messages", QuotaWindowKind.Weekly) with
        {
            Provider = ProviderKind.ChatGpt,
            DisplayName = "ChatGPT Plus",
            Source = QuotaSource.Manual,
            Confidence = QuotaConfidence.Manual
        };
        await vm.ApplyManualSnapshotAsync(manual);

        var first = Snapshot(42, "codex-account", "Codex", QuotaWindowKind.Rolling) with
        {
            Provider = ProviderKind.ChatGpt,
            DisplayName = "OpenAI Codex",
            Source = QuotaSource.Experimental,
            Confidence = QuotaConfidence.High
        };
        var latest = first with { Used = 67, Observed = first.Observed.AddMinutes(1), Fetched = first.Fetched.AddMinutes(1) };

        await vm.ApplyProviderSnapshotsAsync([first]);
        await vm.ApplyProviderSnapshotsAsync([latest]);

        var codexCards = vm.Cards.Where(card => card.Provider == ProviderKind.ChatGpt && card.Account == "codex-account").ToList();
        var codexRows = codexCards.SelectMany(card => card.Windows).Where(row => row.Metric == "Codex").ToList();
        Assert.Single(codexCards);
        Assert.Single(codexRows);
        Assert.Equal("67% used · 33% remaining", codexRows[0].PercentText);
        Assert.Contains(vm.Cards, card => card.Provider == ProviderKind.ChatGpt && card.Account == "manual-account");
    }

    [Fact]
    public async Task RefreshAsync_replaces_same_codex_identity_without_duplicate_card_or_window()
    {
        var first = Snapshot(35, "codex-account", "Codex", QuotaWindowKind.Rolling) with
        {
            Provider = ProviderKind.ChatGpt,
            DisplayName = "OpenAI Codex",
            Source = QuotaSource.Experimental,
            Confidence = QuotaConfidence.High
        };
        var latest = first with { Used = 71, Observed = first.Observed.AddMinutes(1), Fetched = first.Fetched.AddMinutes(1) };
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: new SequencedApplication([first], [latest]));

        await vm.RefreshAsync();
        await vm.RefreshAsync();

        var card = Assert.Single(vm.Cards, item => item.Provider == ProviderKind.ChatGpt && item.Account == "codex-account");
        var row = Assert.Single(card.Windows);
        Assert.Equal("71% used · 29% remaining", row.PercentText);
    }

    [Fact]
    public async Task RefreshAsync_initial_codex_unauthorized_is_error_with_safe_reconnect_and_manual_fallback_guidance()
    {
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: new SequencedRefreshApplication(
            new QuotaRefreshResult([], [new(ProviderKind.ChatGpt, FetchStatus.Unauthorized)])));

        await vm.RefreshAsync();

        Assert.True(vm.IsError);
        Assert.Empty(vm.Cards);
        Assert.False(vm.IsEmpty);
        Assert.False(vm.HasEmptyState);
        Assert.Contains("Codex", vm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reconnect", vm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manual", vm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("official", vm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("401", vm.NotificationBannerText, StringComparison.Ordinal);
        Assert.DoesNotContain("token", vm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAsync_open_code_success_and_codex_rate_limit_keeps_card_and_reports_error()
    {
        var openCode = Snapshot(41, "opencode-account", "OpenCode", QuotaWindowKind.Rolling) with
        {
            Provider = ProviderKind.OpenCode,
            DisplayName = "OpenCode Go",
            Source = QuotaSource.Official,
            Confidence = QuotaConfidence.Official
        };
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: new SequencedRefreshApplication(
            new QuotaRefreshResult([openCode], [new(ProviderKind.ChatGpt, FetchStatus.RateLimited, TimeSpan.FromMinutes(2))])));

        await vm.RefreshAsync();

        Assert.True(vm.IsError);
        Assert.Contains(vm.Cards, card => card.Provider == ProviderKind.OpenCode);
        Assert.Contains("Codex", vm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rate limit", vm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2m", vm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAsync_codex_transient_failure_keeps_old_value_and_stale_re_evaluation_then_success_clears_error()
    {
        var clock = new FixedTimeProvider();
        var old = Snapshot(45, "codex-account", "Codex", QuotaWindowKind.Rolling) with
        {
            DisplayName = "OpenAI Codex",
            FreshUntil = clock.GetUtcNow().AddMinutes(1)
        };
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: new SequencedRefreshApplication(
            new QuotaRefreshResult([old], []),
            new QuotaRefreshResult([], [new(ProviderKind.ChatGpt, FetchStatus.TransientFailure)]),
            new QuotaRefreshResult([old with { Used = 52, Fetched = clock.GetUtcNow().AddMinutes(3), Observed = clock.GetUtcNow().AddMinutes(3), FreshUntil = clock.GetUtcNow().AddMinutes(4) }], [])), timeProvider: clock);

        await vm.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(2));
        await vm.RefreshAsync();
        var staleRow = Assert.Single(Assert.Single(vm.Cards).Windows);
        Assert.Equal("45% used · 55% remaining", staleRow.PercentText);
        Assert.True(staleRow.IsStale);
        Assert.True(vm.IsError);

        await vm.RefreshAsync();
        Assert.Equal(PresentationState.Ready, vm.PresentationState);
        Assert.False(vm.IsNotificationVisible);
        Assert.Equal("52% used · 48% remaining", Assert.Single(Assert.Single(vm.Cards).Windows).PercentText);
    }

    [Fact]
    public async Task RefreshAsync_caller_cancellation_is_rethrown_and_gate_is_released()
    {
        using var cancellation = new CancellationTokenSource();
        var application = new CancellingRefreshApplication();
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: application);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.RefreshAsync(cancellation.Token));
        await vm.RefreshAsync();
        Assert.Equal(2, application.Calls);
    }

    [Fact]
    public async Task RefreshAsync_serializes_overlapping_provider_calls_and_keeps_newer_response()
    {
        var older = Snapshot(35, "codex-account", "Codex", QuotaWindowKind.Rolling);
        var newer = older with { Used = 71, Observed = older.Observed.AddMinutes(1), Fetched = older.Fetched.AddMinutes(1) };
        var application = new BlockingRefreshApplication(older, newer);
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: application);

        var firstRefresh = vm.RefreshAsync();
        await application.FirstEntered.Task;
        var secondRefresh = vm.RefreshAsync();

        Assert.Equal(1, application.CallCount);
        Assert.False(application.SecondEntered.Task.IsCompleted);
        Assert.False(secondRefresh.IsCompleted);

        application.ReleaseFirst.TrySetResult(true);
        await Task.WhenAll(firstRefresh, secondRefresh);

        var card = Assert.Single(vm.Cards);
        var row = Assert.Single(card.Windows);
        Assert.Equal("71% used · 29% remaining", row.PercentText);
    }

    [Fact]
    public async Task Refresh_partial_failure_keeps_successful_cards_and_re_evaluates_missing_provider_as_stale_error()
    {
        var clock = new FixedTimeProvider();
        var openCode = Snapshot(35, "opencode-account", "OpenCode", QuotaWindowKind.Rolling) with
        {
            Provider = ProviderKind.OpenCode,
            DisplayName = "OpenCode Go",
            Source = QuotaSource.Official,
            Confidence = QuotaConfidence.Official,
            FreshUntil = clock.GetUtcNow().AddMinutes(1)
        };
        var codex = Snapshot(45, "codex-account", "Codex", QuotaWindowKind.Rolling) with
        {
            Provider = ProviderKind.ChatGpt,
            DisplayName = "OpenAI Codex",
            Source = QuotaSource.Experimental,
            Confidence = QuotaConfidence.High,
            FreshUntil = clock.GetUtcNow().AddMinutes(1)
        };
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: new SequencedApplication([openCode, codex], [codex]), timeProvider: clock);

        await vm.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(2));
        await vm.RefreshAsync();

        Assert.Contains(vm.Cards, card => card.Provider == ProviderKind.OpenCode);
        Assert.Contains(vm.Cards, card => card.Provider == ProviderKind.ChatGpt);
        Assert.Contains(vm.Cards.Single(card => card.Provider == ProviderKind.OpenCode).Windows, row => row.IsStale);
        Assert.True(vm.IsError);
        Assert.True(vm.IsNotificationVisible);
    }

    [Fact]
    public async Task Refresh_partial_failure_for_same_codex_account_keeps_missing_metric_row_and_marks_it_stale_after_expiry()
    {
        var clock = new FixedTimeProvider();
        var rolling = Snapshot(35, "codex-account", "Codex", QuotaWindowKind.Rolling) with
        {
            DisplayName = "OpenAI Codex",
            FreshUntil = clock.GetUtcNow().AddMinutes(1)
        };
        var weekly = Snapshot(22, "codex-account", "Codex", QuotaWindowKind.Weekly) with
        {
            DisplayName = "OpenAI Codex",
            FreshUntil = clock.GetUtcNow().AddMinutes(1)
        };
        var latestRolling = rolling with
        {
            Used = 71,
            Observed = rolling.Observed.AddMinutes(1),
            Fetched = rolling.Fetched.AddMinutes(1),
            FreshUntil = clock.GetUtcNow().AddMinutes(1)
        };
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: new SequencedApplication([rolling, weekly], [latestRolling]), timeProvider: clock);

        await vm.RefreshAsync();
        await vm.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(2));
        await vm.RefreshAsync();

        var card = Assert.Single(vm.Cards, item => item.Account == "codex-account");
        var rows = card.Windows.Where(row => row.Metric == "Codex").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("71% used · 29% remaining", Assert.Single(rows, row => row.WindowName == "Rolling").PercentText);
        Assert.True(Assert.Single(rows, row => row.WindowName == "Weekly").IsStale);
        Assert.True(vm.IsError);
        Assert.True(vm.IsNotificationVisible);
    }

    [Fact]
    public async Task Refresh_partial_failure_does_not_treat_manual_chatgpt_as_missing_provider()
    {
        var manual = Snapshot(18, "manual-account", "Messages", QuotaWindowKind.Weekly) with
        {
            Provider = ProviderKind.ChatGpt,
            DisplayName = "ChatGPT Plus",
            Source = QuotaSource.Manual,
            Confidence = QuotaConfidence.Manual
        };
        var codex = Snapshot(45, "codex-account", "Codex", QuotaWindowKind.Rolling) with
        {
            Provider = ProviderKind.ChatGpt,
            DisplayName = "OpenAI Codex",
            Source = QuotaSource.Experimental,
            Confidence = QuotaConfidence.High
        };
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: new SequencedApplication([manual, codex], [codex]));

        await vm.RefreshAsync();
        await vm.RefreshAsync();

        Assert.Contains(vm.Cards, card => card.Account == "manual-account");
        Assert.False(vm.IsError);
    }

    [Fact]
    public async Task Facade_exposes_codex_credential_existence_without_secret_value()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync(CodexOAuthClient.CredentialKey, "private-token", default);
        var facade = new UiProviderFacade(credentialStore: store, codexSessionManager: CreateManager(new HttpClient(new CodexHandler()), store));

        Assert.True(await facade.HasCodexCredentialAsync(default));
        Assert.DoesNotContain("private-token", typeof(IProviderUiService).GetMethods().Select(method => method.Name));
    }

    [Fact]
    public async Task Facade_reports_false_when_codex_credential_store_read_fails()
    {
        var store = new ThrowingReadStore();
        var facade = new UiProviderFacade(credentialStore: store, codexSessionManager: CreateManager(new HttpClient(new CodexHandler()), store));

        Assert.False(await facade.HasCodexCredentialAsync(default));
    }

    [Fact]
    public async Task Logout_failure_keeps_logout_visibility_and_sets_error_but_success_hides_it()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync(CodexOAuthClient.CredentialKey, "private-token", default);
        var facade = new UiProviderFacade(credentialStore: store, codexSessionManager: CreateManager(new HttpClient(new CodexHandler()), store));
        var vm = new MainViewModel(new EmptyDashboardSource());
        vm.SetCodexCredentialPresence(await facade.HasCodexCredentialAsync(default));

        Assert.True(vm.IsCodexLogoutVisible);
        vm.SetCodexResult(new(false, CodexAuthorizationState.Error, "Unable to disconnect Codex."));
        Assert.True(vm.IsCodexLogoutVisible);
        vm.SetCodexCredentialPresence(false);
        Assert.False(vm.IsCodexLogoutVisible);
    }

    [AvaloniaFact]
    public async Task MainWindow_initialize_shows_logout_for_saved_credential_without_quota_fetch()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync(CodexOAuthClient.CredentialKey, "private-token", default);
        var facade = new UiProviderFacade(credentialStore: store, codexSessionManager: CreateManager(new HttpClient(new CodexHandler()), store));
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource()), facade);
        window.Show();

        await window.InitializeAsync();
        window.ViewModel.Navigate(AppPage.Settings);

        Assert.True(window.FindControl<Button>("CodexLogoutButton")!.IsVisible);
        window.Close();
    }

    [Fact]
    public void Codex_successful_connection_and_logout_failure_keep_logout_visible_until_disconnected_success()
    {
        var vm = new MainViewModel(new EmptyDashboardSource());

        Assert.False(vm.IsCodexLogoutVisible);
        vm.SetCodexResult(new(true, CodexAuthorizationState.Connected, "Codex connected."));
        Assert.True(vm.IsCodexConnected);
        Assert.True(vm.IsCodexLogoutVisible);

        vm.SetCodexResult(new(false, CodexAuthorizationState.Error, "Unable to disconnect Codex."));
        Assert.False(vm.IsCodexConnected);
        Assert.True(vm.IsCodexLogoutVisible);

        vm.SetCodexResult(new(true, CodexAuthorizationState.Disconnected, "Codex disconnected."));
        Assert.False(vm.IsCodexLogoutVisible);
    }

    [Fact]
    public async Task Logout_store_failure_returns_error_without_losing_logout_eligibility()
    {
        var store = new ThrowingRemoveStore();
        var handler = new CodexHandler();
        using var client = new HttpClient(handler);
        var facade = new UiProviderFacade(credentialStore: store, codexSessionManager: CreateManager(client, store));
        var vm = new MainViewModel(new EmptyDashboardSource());
        vm.SetCodexCredentialPresence(await facade.HasCodexCredentialAsync(default));
        Assert.True((await facade.StartCodexAsync(default)).Success);

        var result = await facade.LogoutCodexAsync(default);
        vm.SetCodexResult(result);
        var poll = await facade.PollCodexAsync(default);

        Assert.False(result.Success);
        Assert.Equal(CodexAuthorizationState.Error, result.State);
        Assert.True(vm.IsCodexLogoutVisible);
        Assert.Equal(CodexAuthorizationState.Disconnected, poll.State);
        Assert.Equal(0, handler.DevicePollCount);
        Assert.DoesNotContain("private-token", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logout_clears_pending_authorization_before_blocking_remove_and_poll_adds_no_http_request()
    {
        var store = new BlockingRemoveStore();
        var handler = new CodexHandler();
        using var client = new HttpClient(handler);
        var facade = new UiProviderFacade(credentialStore: store, codexSessionManager: CreateManager(client, store));
        Assert.True((await facade.StartCodexAsync(default)).Success);

        var logoutTask = facade.LogoutCodexAsync(default).AsTask();
        await store.RemoveEntered.Task;

        var poll = await facade.PollCodexAsync(default);

        Assert.Equal(CodexAuthorizationState.Disconnected, poll.State);
        Assert.Equal(0, handler.DevicePollCount);
        store.AllowRemove.TrySetResult(true);
        var logout = await logoutTask;
        Assert.True(logout.Success);
    }

    private static CodexSessionManager CreateManager(HttpClient client, ICredentialStore store, TimeProvider? clock = null)
    {
        var actualClock = clock ?? new FixedTimeProvider();
        return new(new CodexOAuthClient(client, store, actualClock), client, actualClock);
    }

    private static QuotaSnapshot Snapshot(decimal percent, string account, string metric, QuotaWindowKind window) =>
        new(ProviderKind.ChatGpt, account, metric, new(window, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1)), percent, 100, null, "requests", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, QuotaSource.Experimental, QuotaConfidence.High, DateTimeOffset.UtcNow.AddHours(1), "OpenAI Codex");

    private sealed class SequencedApplication(IReadOnlyList<QuotaSnapshot> first, IReadOnlyList<QuotaSnapshot> latest) : IQuotaApplication
    {
        private int calls;
        public ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new QuotaRefreshResult(Interlocked.Increment(ref calls) == 1 ? first : latest, []));
    }

    private sealed class BlockingRefreshApplication(QuotaSnapshot older, QuotaSnapshot newer) : IQuotaApplication
    {
        private int calls;
        public TaskCompletionSource<bool> FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount => Volatile.Read(ref calls);

        public async ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                FirstEntered.TrySetResult(true);
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
                return new([older], []);
            }

            SecondEntered.TrySetResult(true);
            return new([newer], []);
        }
    }

    private sealed class SequencedRefreshApplication(params QuotaRefreshResult[] results) : IQuotaApplication
    {
        private int calls;
        public ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(results[Math.Min(Interlocked.Increment(ref calls) - 1, results.Length - 1)]);
    }

    private sealed class CancellingRefreshApplication : IQuotaApplication
    {
        public int Calls { get; private set; }
        public ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1) throw new OperationCanceledException(cancellationToken);
            return ValueTask.FromResult(new QuotaRefreshResult([], []));
        }
    }

    private sealed class ThrowingReadStore : ICredentialStore
    {
        public CredentialStoreAvailability Availability => CredentialStoreAvailability.SecureStore;
        public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken) => throw new InvalidOperationException("read failed");
        public ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask RemoveAsync(string account, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class ThrowingRemoveStore : ICredentialStore
    {
        public CredentialStoreAvailability Availability => CredentialStoreAvailability.SecureStore;
        public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken) => ValueTask.FromResult<string?>("private-token");
        public ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask RemoveAsync(string account, CancellationToken cancellationToken) => throw new InvalidOperationException("remove failed");
    }

    private sealed class BlockingRemoveStore : ICredentialStore
    {
        public CredentialStoreAvailability Availability => CredentialStoreAvailability.SecureStore;
        public TaskCompletionSource<bool> RemoveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> AllowRemove { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken) => ValueTask.FromResult<string?>("private-token");
        public ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async ValueTask RemoveAsync(string account, CancellationToken cancellationToken)
        {
            RemoveEntered.TrySetResult(true);
            await AllowRemove.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now = now.Add(amount);
    }

    private sealed class CodexHandler(bool pendingOnce = false) : HttpMessageHandler
    {
        private int devicePolls;
        public int DevicePollCount => Volatile.Read(ref devicePolls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsoluteUri switch
            {
                "https://auth.openai.com/api/accounts/deviceauth/usercode" => "{\"user_code\":\"SAFE-CODE\",\"device_auth_id\":\"private-device-id\",\"interval\":0}",
                "https://auth.openai.com/api/accounts/deviceauth/token" when pendingOnce && Interlocked.Increment(ref devicePolls) == 1 => "secret-response",
                "https://auth.openai.com/api/accounts/deviceauth/token" => "{\"authorization_code\":\"private-auth-code\",\"code_verifier\":\"private-verifier\"}",
                "https://auth.openai.com/oauth/token" => "{\"access_token\":\"access-token\",\"refresh_token\":\"refresh-token\",\"expires_in\":3600}",
                "https://chatgpt.com/backend-api/wham/usage" => "{\"plan_type\":\"plus\",\"rate_limit\":{\"primary_window\":{\"used_percent\":42,\"reset_at\":1780000000,\"limit_window_seconds\":300}}}",
                _ => "{}"
            };
            var status = request.RequestUri.AbsoluteUri.EndsWith("/deviceauth/token", StringComparison.Ordinal) && pendingOnce && devicePolls == 1 ? HttpStatusCode.NotFound : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}

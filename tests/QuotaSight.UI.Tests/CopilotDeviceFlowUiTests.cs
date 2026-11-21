using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using System.Net;
using QuotaSight.Application;
using QuotaSight.Core;
using QuotaSight.Infrastructure;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class CopilotDeviceFlowUiTests
{
    [AvaloniaFact]
    public void Shown_copilot_panel_renders_the_quota_notice_in_the_visual_tree()
    {
        var window = CreateWindow(new RecordingCopilotProvider());
        var notice = window.FindControl<TextBlock>("CopilotQuotaNoticeText");

        Assert.NotNull(notice);
        Assert.False(string.IsNullOrWhiteSpace(notice!.Text));
        Assert.Contains("quota", notice.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manual", notice.Text, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task Copilot_unauthorized_refresh_shows_explicit_reauthentication_action_without_opening_browser()
    {
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: new UnauthorizedCopilotApplication());
        var launcher = new RecordingBrowserLauncher();
        var window = new MainWindow(vm, new RecordingCopilotProvider(), null, null, launcher);
        window.Show();

        Assert.False(window.FindControl<Button>("CopilotReauthenticateButton")!.IsVisible);
        await vm.RefreshAsync();

        var action = window.FindControl<Button>("CopilotReauthenticateButton")!;
        Assert.True(action.IsVisible);
        Assert.Contains("re-auth", action.Content?.ToString(), StringComparison.OrdinalIgnoreCase);
        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(vm.IsCopilotProviderPanelVisible);
        Assert.Empty(launcher.LaunchedUris);
    }

    [Fact]
    public void Reauthentication_copy_is_cautious_and_localized()
    {
        Assert.Contains("try", new UiCopy(UiLanguage.English).CopilotReauthenticate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("再認証をお試し", new UiCopy(UiLanguage.Japanese).CopilotReauthenticate, StringComparison.Ordinal);
    }

    [AvaloniaTheory]
    [InlineData(CredentialStoreAvailability.Unavailable, true)]
    [InlineData(CredentialStoreAvailability.Locked, true)]
    [InlineData(CredentialStoreAvailability.SecureStore, false)]
    public async Task Successful_device_flow_explains_session_only_storage_only_when_secure_store_is_unavailable(CredentialStoreAvailability availability, bool expectedVisible)
    {
        var window = CreateWindow(new RecordingCopilotProvider());
        window.ViewModel.SetCopilotCredentialAvailability(availability, true);

        var notice = window.FindControl<TextBlock>("CopilotSessionStorageNoticeText")!;
        Assert.Equal(expectedVisible, notice.IsVisible);
        if (expectedVisible)
        {
            Assert.Contains("restarting", notice.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ABCD-EFGH", notice.Text);
        }
    }

    [Fact]
    public async Task Device_flow_client_id_change_returns_configuration_failure_and_does_not_save_tokens()
    {
        var configuredClientId = "started-client";
        var store = new InMemoryCredentialStore();
        using var client = new HttpClient(new DeviceFlowSuccessHandler(() => configuredClientId = "changed-client"));
        var github = new GitHubDeviceFlowClient(client, "started-client", delay: new ImmediateDeviceDelay());
        var tokenSession = new GitHubUserTokenSession(store, () => configuredClientId,
            (_, _) => throw new InvalidOperationException("refresh is not expected"));
        var facade = new UiProviderFacade(github: github, credentialStore: store, githubTokenSession: tokenSession);
        var start = await facade.StartGitHubDeviceFlowAsync(default);
        Assert.True(start.IsSuccess);

        var result = await facade.PollGitHubDeviceFlowAsync(start.Value!, default);

        Assert.False(result.Success);
        Assert.Equal(FetchStatus.ConfigurationError, result.Status);
        Assert.Null(await store.GetAsync("github", default));
    }

    [AvaloniaFact]
    public async Task Unauthorized_failure_from_another_provider_does_not_show_copilot_action()
    {
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: new UnauthorizedCopilotApplication(ProviderKind.Claude));
        var window = new MainWindow(vm, new RecordingCopilotProvider());
        window.Show();

        await vm.RefreshAsync();

        Assert.False(window.FindControl<Button>("CopilotReauthenticateButton")!.IsVisible);
    }

    [AvaloniaFact]
    public async Task Start_device_flow_opens_browser_and_automatically_polls_without_claiming_quota_success()
    {
        var provider = new RecordingCopilotProvider
        {
            StartResult = new FetchResult<DeviceAuthorizationStart>(FetchStatus.Success, new DeviceAuthorizationStart("device", "ABCD-EFGH", new Uri("https://github.com/login/device"), DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.Zero)),
            PollResult = new GitHubDeviceFlowResult(true, FetchStatus.Success)
        };
        var window = CreateWindow(provider);

        window.FindControl<Button>("CopilotStartButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await provider.PollStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await provider.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, provider.StartCalls);
        Assert.Equal(1, provider.PollCalls);
        Assert.Equal(CopilotUiState.Completed, window.ViewModel.CopilotState);
        Assert.Contains("authorization", window.ViewModel.CopilotDeviceResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quota", window.ViewModel.CopilotDeviceResult, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("quota", window.ViewModel.CopyText.CopilotQuotaNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("official", window.ViewModel.CopyText.CopilotQuotaNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manual", window.ViewModel.CopyText.CopilotQuotaNotice, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task Start_success_passes_the_verification_uri_to_the_browser_launcher_exactly_once()
    {
        var provider = new RecordingCopilotProvider
        {
            StartResult = new FetchResult<DeviceAuthorizationStart>(FetchStatus.Success, new DeviceAuthorizationStart("device", "ABCD-EFGH", new Uri("https://github.com/login/device"), DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.Zero)),
            PollResult = new GitHubDeviceFlowResult(true, FetchStatus.Success)
        };
        var launcher = new RecordingBrowserLauncher();
        var window = CreateWindow(provider, launcher);

        window.FindControl<Button>("CopilotStartButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await provider.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Single(launcher.LaunchedUris);
        Assert.Equal(new Uri("https://github.com/login/device"), launcher.LaunchedUris[0]);
    }

    [AvaloniaFact]
    public async Task Start_device_flow_suppresses_duplicate_start_while_poll_is_waiting()
    {
        var provider = new RecordingCopilotProvider
        {
            StartResult = new FetchResult<DeviceAuthorizationStart>(FetchStatus.Success, new DeviceAuthorizationStart("device", "ABCD-EFGH", new Uri("https://github.com/login/device"), DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.Zero)),
            PollResult = new GitHubDeviceFlowResult(false, FetchStatus.TransientFailure, "authorization pending")
        };
        provider.PollRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = CreateWindow(provider);

        var start = window.FindControl<Button>("CopilotStartButton")!;
        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await provider.PollStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(50);

        Assert.Equal(1, provider.StartCalls);
        Assert.Equal(1, provider.PollCalls);
        Assert.True(window.ViewModel.IsCopilotFlowActive);
        Assert.Contains("waiting", window.ViewModel.CopilotDeviceResult, StringComparison.OrdinalIgnoreCase);

        provider.PollRelease.TrySetResult(true);
        await provider.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [AvaloniaFact]
    public async Task Active_device_flow_shows_indeterminate_waiting_progress_and_disables_start()
    {
        var provider = new RecordingCopilotProvider
        {
            StartResult = new FetchResult<DeviceAuthorizationStart>(FetchStatus.Success, new DeviceAuthorizationStart("device", "ABCD-EFGH", new Uri("https://github.com/login/device"), DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.Zero)),
            PollResult = new GitHubDeviceFlowResult(false, FetchStatus.TransientFailure, "authorization pending")
        };
        provider.PollRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = CreateWindow(provider);

        window.FindControl<Button>("CopilotStartButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await provider.PollStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var progress = window.FindControl<ProgressBar>("CopilotFlowProgressBar");
        Assert.True(window.ViewModel.IsCopilotFlowActive);
        Assert.False(window.FindControl<Button>("CopilotStartButton")!.IsEnabled);
        Assert.NotNull(progress);
        Assert.True(progress!.IsIndeterminate);
        Assert.Contains("waiting", window.FindControl<TextBlock>("CopilotFlowWaitingText")!.Text, StringComparison.OrdinalIgnoreCase);

        provider.PollRelease.TrySetResult(true);
        await provider.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [AvaloniaFact]
    public async Task Shutdown_cancels_waiting_copilot_poll_without_dispatcher_error_or_completed_transition()
    {
        var provider = new RecordingCopilotProvider
        {
            StartResult = new FetchResult<DeviceAuthorizationStart>(FetchStatus.Success, new DeviceAuthorizationStart("device", "ABCD-EFGH", new Uri("https://github.com/login/device"), DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.Zero)),
            PollResult = new GitHubDeviceFlowResult(true, FetchStatus.Success)
        };
        provider.PollRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = CreateWindow(provider, new RecordingBrowserLauncher());
        var coordinator = new AppLifecycleCoordinator(() => ValueTask.CompletedTask);
        window.OperationRunner = coordinator.Run;
        var unhandled = new List<Exception>();
        void OnUnhandled(object? sender, Avalonia.Threading.DispatcherUnhandledExceptionEventArgs args) { unhandled.Add(args.Exception); args.Handled = true; }
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += OnUnhandled;
        try
        {
            window.FindControl<Button>("CopilotStartButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await provider.PollStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var exit = coordinator.BeginExitAsync();
            await provider.PollCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await exit.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(window.ViewModel.IsCopilotFlowActive);
            Assert.NotEqual(CopilotUiState.Completed, window.ViewModel.CopilotState);
            Assert.Empty(unhandled);
        }
        finally { Avalonia.Threading.Dispatcher.UIThread.UnhandledException -= OnUnhandled; }
    }

    [Fact]
    public void Completed_status_contains_short_completion_only_and_quota_notice_is_separate()
    {
        var copy = new UiCopy(UiLanguage.English);

        Assert.Equal("GitHub authorization completed.", copy.CopilotStatus(CopilotUiState.Completed));
        Assert.Contains("quota", copy.CopilotQuotaNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unsupported_copilot_errors_have_distinct_safe_states_in_both_languages()
    {
        var english = new UiCopy(UiLanguage.English);
        var japanese = new UiCopy(UiLanguage.Japanese);

        Assert.Contains("disabled", english.CopilotStatus(CopilotUiState.DeviceFlowDisabled), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("無効", japanese.CopilotStatus(CopilotUiState.DeviceFlowDisabled), StringComparison.Ordinal);
        Assert.Contains("unsupported", english.CopilotStatus(CopilotUiState.Unsupported), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("非対応", japanese.CopilotStatus(CopilotUiState.Unsupported), StringComparison.Ordinal);
    }

    [AvaloniaTheory]
    [InlineData("GitHub client ID is not configured.", CopilotUiState.ConfigurationError)]
    [InlineData("GitHub client credentials were rejected.", CopilotUiState.ConfigurationError)]
    [InlineData("GitHub device authorization is disabled.", CopilotUiState.DeviceFlowDisabled)]
    [InlineData("Copilot quota contract is unavailable.", CopilotUiState.Unsupported)]
    public async Task Unsupported_errors_map_to_the_specific_copilot_state(string error, CopilotUiState expected)
    {
        var provider = new RecordingCopilotProvider { StartResult = new FetchResult<DeviceAuthorizationStart>(FetchStatus.Unsupported, Error: error) };
        var window = CreateWindow(provider);

        window.FindControl<Button>("CopilotStartButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(50);

        Assert.Equal(expected, window.ViewModel.CopilotState);
    }

    [AvaloniaFact]
    public void Pending_result_keeps_the_device_url_and_code_for_manual_fallback()
    {
        var window = CreateWindow(new RecordingCopilotProvider());
        var url = "https://github.com/login/device";

        window.ViewModel.SetCopilotDeviceResult(url, "ABCD-EFGH", CopilotUiState.Started);
        window.ViewModel.SetCopilotDeviceResult(string.Empty, string.Empty, CopilotUiState.Pending, "authorization pending");

        Assert.Contains(url, window.ViewModel.CopilotDeviceResult, StringComparison.Ordinal);
        Assert.Contains("ABCD-EFGH", window.ViewModel.CopilotDeviceResult, StringComparison.Ordinal);
    }

    [Fact]
    public void Copilot_copy_distinguishes_denied_expired_and_configuration_errors_in_both_languages()
    {
        var english = new UiCopy(UiLanguage.English);
        var japanese = new UiCopy(UiLanguage.Japanese);

        Assert.Contains("denied", english.CopilotStatus(CopilotUiState.Denied), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expired", english.CopilotStatus(CopilotUiState.Expired), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Client ID", english.CopilotStatus(CopilotUiState.ConfigurationError), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("拒否", japanese.CopilotStatus(CopilotUiState.Denied), StringComparison.Ordinal);
        Assert.Contains("期限", japanese.CopilotStatus(CopilotUiState.Expired), StringComparison.Ordinal);
        Assert.Contains("Client ID", japanese.CopilotStatus(CopilotUiState.ConfigurationError), StringComparison.Ordinal);
    }

    private static MainWindow CreateWindow(RecordingCopilotProvider provider, IUriLauncher? launcher = null)
    {
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource()), provider, null, null, launcher);
        window.Show();
        window.ViewModel.OpenProviderFlow();
        window.ViewModel.SelectProvider(ProviderConnectionChoice.Copilot);
        return window;
    }

    private sealed class DeviceFlowSuccessHandler(Action onTokenResponse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/login/device/code") onTokenResponse();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri.AbsolutePath == "/login/device/code"
                    ? "{\"device_code\":\"device\",\"user_code\":\"user-code\",\"verification_uri\":\"https://github.com/login/device\",\"expires_in\":300,\"interval\":0}"
                    : "{\"access_token\":\"access-token\",\"refresh_token\":\"refresh-token\",\"expires_in\":3600,\"refresh_token_expires_in\":86400}")
            });
        }
    }

    private sealed class ImmediateDeviceDelay : IDeviceFlowDelay
    {
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class RecordingBrowserLauncher : IUriLauncher
    {
        public List<Uri> LaunchedUris { get; } = [];
        public Task<bool> LaunchUriAsync(Uri uri) { LaunchedUris.Add(uri); return Task.FromResult(true); }
    }

    private sealed class UnauthorizedCopilotApplication(ProviderKind provider = ProviderKind.Copilot) : QuotaSight.Application.IQuotaApplication
    {
        public ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new QuotaRefreshResult([], [new(provider, FetchStatus.Unauthorized)]));
    }

    private sealed class RecordingCopilotProvider : IProviderUiService
    {
        public FetchResult<DeviceAuthorizationStart> StartResult { get; init; } = new(FetchStatus.TransientFailure);
        public GitHubDeviceFlowResult PollResult { get; init; } = new(false, FetchStatus.TransientFailure);
        public CredentialStoreAvailability CredentialAvailability { get; init; } = CredentialStoreAvailability.SecureStore;
        public TaskCompletionSource PollStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PollCancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool>? PollRelease { get; set; }
        public int StartCalls { get; private set; }
        public int PollCalls { get; private set; }
        public ValueTask<ProviderConnectionResult> TestOpenCodeAsync(string key, CancellationToken token) => ValueTask.FromResult(new ProviderConnectionResult(false, "unused"));
        public ValueTask<string> ProbeGitHubCliAsync(CancellationToken token) => ValueTask.FromResult("unused");
        public ValueTask<FetchResult<DeviceAuthorizationStart>> StartGitHubDeviceFlowAsync(CancellationToken token) { StartCalls++; return ValueTask.FromResult(StartResult); }
        public async ValueTask<GitHubDeviceFlowResult> PollGitHubDeviceFlowAsync(DeviceAuthorizationStart authorization, CancellationToken token)
        {
            PollCalls++;
            PollStarted.TrySetResult();
            try
            {
                if (PollRelease is not null) await PollRelease.Task.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                PollCancellationObserved.TrySetResult();
                throw;
            }
            Completed.TrySetResult();
            return PollResult;
        }
        public ValueTask<CodexUiResult> StartCodexAsync(CancellationToken token) => ValueTask.FromResult(new CodexUiResult(false, CodexAuthorizationState.Error, "unused"));
        public ValueTask<CodexUiResult> PollCodexAsync(CancellationToken token) => ValueTask.FromResult(new CodexUiResult(false, CodexAuthorizationState.Error, "unused"));
        public ValueTask<CodexUiResult> LogoutCodexAsync(CancellationToken token) => ValueTask.FromResult(new CodexUiResult(false, CodexAuthorizationState.Error, "unused"));
        public ValueTask<bool> HasCodexCredentialAsync(CancellationToken token) => ValueTask.FromResult(false);
    }
}

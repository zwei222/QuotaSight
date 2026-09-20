using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using QuotaSight.Application;
using QuotaSight.Infrastructure;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class CopilotCompositionIntegrationTests
{
    [AvaloniaFact]
    public async Task Composition_refresh_reads_saved_organization_and_shared_credential_into_copilot_card()
    {
        const string token = "github-token-must-not-leak";
        const string organization = "acme-engineering";
        var root = Path.Combine(Path.GetTempPath(), "quotasight-ui-" + Guid.NewGuid().ToString("N"));
        var handler = new CopilotBillingHandler(organization, "octocat", token);
        using var client = new HttpClient(handler);
        var store = new InMemoryCredentialStore();
        try
        {
            new AppSettingsStore(root).Save(new AppSettingsDto(GithubOrganization: organization));
            await store.SetAsync("github", token, default);
            var parts = CompositionRoot.CreateMainWindowParts(root, client, store);

            try
            {
                await parts.ViewModel.RefreshAsync();
                var card = Assert.Single(parts.ViewModel.Cards, item => item.Provider == QuotaSight.Core.ProviderKind.Copilot);
                var row = Assert.Single(card.Windows);
                Assert.True(row.IsQuantityOnly);
                Assert.Equal("1,950 credits used", row.QuantityGrossText);
                Assert.Equal("1,200 included", row.QuantityDiscountText);
                Assert.Equal("750 additional", row.QuantityNetText);
                Assert.True(row.FetchedAt > DateTimeOffset.MinValue);
                Assert.Contains("Monthly", row.WindowName, StringComparison.Ordinal);
                Assert.Contains("Delayed", row.SourceBadge, StringComparison.Ordinal);
                Assert.Contains("Updated", row.FreshnessText, StringComparison.Ordinal);
                Assert.DoesNotContain("OverLimit", row.GetType().ToString(), StringComparison.Ordinal);
                Assert.False(row.IsOverLimit);

                var window = new MainWindow(parts);
                window.Show();
                var gauges = window.GetVisualDescendants().OfType<ProgressBar>().Where(control => control.Classes.Contains("QuotaGauge")).ToArray();
                Assert.DoesNotContain(gauges, gauge => gauge.IsVisible && gauge.Classes.Contains("OverLimit"));
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text == "1,950 credits used");
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text == "1,200 included");
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text == "750 additional");
                window.Close();

                var export = await parts.ViewModel.History.ExportJsonAsync();
                Assert.DoesNotContain(token, export, StringComparison.Ordinal);
                Assert.DoesNotContain(token, string.Join('|', parts.ViewModel.History.Entries.Select(entry => entry.Account)), StringComparison.Ordinal);
                Assert.DoesNotContain(token, parts.ViewModel.NotificationBannerText, StringComparison.Ordinal);
                Assert.DoesNotContain(token, parts.ViewModel.CopilotDeviceResult, StringComparison.Ordinal);
            }
            finally
            {
                parts.ViewModel.Dispose();
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer " + token, request.Authorization));
        Assert.Contains(handler.Requests, request => request.Path == "/user");
        var usage = Assert.Single(handler.Requests, request => request.Path.Contains("/organizations/", StringComparison.Ordinal));
        Assert.Contains("/organizations/acme-engineering/settings/billing/ai_credit/usage", usage.Path, StringComparison.Ordinal);
        Assert.Contains("year=", usage.Query, StringComparison.Ordinal);
        Assert.Contains("month=", usage.Query, StringComparison.Ordinal);
        Assert.Contains("user=octocat", usage.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dynamic_copilot_adapter_reads_shared_fallback_when_secure_store_is_unavailable()
    {
        const string token = "fallback-token-must-not-leak";
        var fallback = new InMemoryCredentialStore();
        var credentials = new FallbackCredentialStore(new UnavailableCredentialStore(), fallback);
        await fallback.SetAsync("github", token, default);
        using var client = new HttpClient(new CopilotBillingHandler("acme", "octocat", token));
        var adapter = new DynamicCopilotAdapter(client, credentials, () => "acme");

        var result = await adapter.FetchAsync("GitHub Copilot", default);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!);
        Assert.DoesNotContain(token, result.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_uses_one_token_snapshot_when_credential_changes_while_user_request_is_waiting()
    {
        const string tokenA = "token-A-must-not-leak";
        const string tokenB = "token-B-must-not-leak";
        var store = new InMemoryCredentialStore();
        await store.SetAsync("github", tokenA, default);
        var userStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUser = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new TokenBarrierHandler(tokenA, userStarted, releaseUser));
        var adapter = new DynamicCopilotAdapter(client, store, () => "acme");

        var refresh = adapter.FetchAsync("GitHub Copilot", default).AsTask();
        await userStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await store.SetAsync("github", tokenB, default);
        releaseUser.TrySetResult(true);
        var result = await refresh;

        Assert.True(result.IsSuccess);
        Assert.Equal("acme/octocat-a", result.Value![0].Account);
    }

    [Fact]
    public async Task Device_flow_save_cancellation_is_rethrown_without_private_session_fallback()
    {
        using var client = new HttpClient(new DeviceTokenHandler());
        using var cancellation = new CancellationTokenSource();
        var store = new CancellationStore(cancellation);
        var github = new GitHubDeviceFlowClient(client, "client-id", delay: new ImmediateDeviceDelay());
        var facade = new UiProviderFacade(github: github, credentialStore: store);
        var authorization = new DeviceAuthorizationStart("device-code", "ABCD-EFGH", new Uri("https://github.com/login/device"), DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.Zero);

        await Assert.ThrowsAsync<OperationCanceledException>(() => facade.PollGitHubDeviceFlowAsync(authorization, cancellation.Token).AsTask());
        Assert.Null(await facade.GetStoredOpenCodeKeyAsync(default));
        Assert.Null(await store.GetAsync("github", default));
    }

    private sealed class ImmediateDeviceDelay : IDeviceFlowDelay
    {
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class DeviceTokenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"github-token-must-not-leak\"}", Encoding.UTF8, "application/json") });
    }

    private sealed class CancellationStore(CancellationTokenSource cancellation) : ICredentialStore
    {
        public CredentialStoreAvailability Availability => CredentialStoreAvailability.SecureStore;
        public ValueTask<string?> GetAsync(string account, CancellationToken token) => ValueTask.FromResult<string?>(null);
        public ValueTask SetAsync(string account, string secret, CancellationToken token)
        {
            cancellation.Cancel();
            return ValueTask.FromException(new OperationCanceledException(cancellation.Token));
        }
        public ValueTask RemoveAsync(string account, CancellationToken token) => ValueTask.CompletedTask;
    }

    private sealed class TokenBarrierHandler(string tokenA, TaskCompletionSource<bool> userStarted, TaskCompletionSource<bool> releaseUser) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(tokenA, request.Headers.Authorization?.Parameter);
            if (request.RequestUri!.AbsolutePath == "/user")
            {
                userStarted.TrySetResult(true);
                await releaseUser.Task.WaitAsync(cancellationToken);
                return Json("{\"login\":\"octocat-a\"}");
            }
            Assert.Equal("/organizations/acme/settings/billing/ai_credit/usage", request.RequestUri.AbsolutePath);
            return Json($"{{\"timePeriod\":{{\"year\":{DateTimeOffset.UtcNow.Year},\"month\":{DateTimeOffset.UtcNow.Month}}},\"organization\":\"acme\",\"user\":\"octocat-a\",\"usageItems\":[{{\"product\":\"Copilot\",\"unitType\":\"credits\",\"grossQuantity\":3,\"discountQuantity\":0,\"netQuantity\":3}}]}}");
        }
        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }

    [AvaloniaFact]
    public async Task Main_window_device_flow_to_billing_refresh_uses_saved_fallback_token_without_leaking_it()
    {
        const string token = "e2e-device-token-must-not-leak";
        var root = Path.Combine(Path.GetTempPath(), "quotasight-e2e-" + Guid.NewGuid().ToString("N"));
        var fallback = new InMemoryCredentialStore();
        var credentials = new FallbackCredentialStore(new UnavailableCredentialStore(), fallback);
        var handler = new DeviceToBillingHandler(token);
        using var client = new HttpClient(handler);
        try
        {
            new AppSettingsStore(root).Save(new AppSettingsDto(GithubOAuthClientId: "client-id", GithubOrganization: "acme"));
            var parts = CompositionRoot.CreateMainWindowParts(root, client, credentials);
            var window = new MainWindow(parts);
            try
            {
                window.Show();
                await window.InitializeAsync();
                parts.ViewModel.OpenProviderFlow();
                parts.ViewModel.SelectProvider(ProviderConnectionChoice.Copilot);
                window.FindControl<Button>("CopilotStartButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await handler.TokenIssued.Task.WaitAsync(TimeSpan.FromSeconds(30));
                await handler.UserRequestSeen.Task.WaitAsync(TimeSpan.FromSeconds(30));
                await handler.BillingRequestSeen.Task.WaitAsync(TimeSpan.FromSeconds(30));
                var cardsPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                parts.ViewModel.Cards.CollectionChanged += (_, _) => cardsPublished.TrySetResult(true);
                handler.BillingRelease.TrySetResult(true);
                await cardsPublished.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(token, await fallback.GetAsync("github", default));

                var card = Assert.Single(parts.ViewModel.Cards, item => item.Provider == QuotaSight.Core.ProviderKind.Copilot);
                var row = Assert.Single(card.Windows);
                Assert.Equal("3 credits used", row.QuantityGrossText);
                Assert.DoesNotContain(token, await parts.ViewModel.History.ExportJsonAsync(), StringComparison.Ordinal);
                Assert.DoesNotContain(token, parts.ViewModel.NotificationBannerText, StringComparison.Ordinal);
                Assert.DoesNotContain(token, parts.ViewModel.CopilotDeviceResult, StringComparison.Ordinal);
                Assert.Equal(["device", "token", "user", "billing"], handler.RequestKinds);
            }
            finally
            {
                window.Close();
                parts.ViewModel.Dispose();
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class DeviceToBillingHandler(string token) : HttpMessageHandler
    {
        public TaskCompletionSource<bool> TokenIssued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> UserRequestSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> BillingRequestSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> BillingRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> RequestKinds { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/login/device/code")
            {
                RequestKinds.Add("device");
                return Json("{\"device_code\":\"device-code\",\"user_code\":\"ABCD-EFGH\",\"verification_uri\":\"https://github.com/login/device\",\"expires_in\":600,\"interval\":0}");
            }
            if (path == "/login/oauth/access_token")
            {
                RequestKinds.Add("token");
                TokenIssued.TrySetResult(true);
                return Json($"{{\"access_token\":\"{token}\"}}");
            }
            if (path == "/user")
            {
                RequestKinds.Add("user");
                UserRequestSeen.TrySetResult(true);
                return Json("{\"login\":\"octocat\"}");
            }
            if (path == "/organizations/acme/settings/billing/ai_credit/usage")
            {
                RequestKinds.Add("billing");
                BillingRequestSeen.TrySetResult(true);
                await BillingRelease.Task.WaitAsync(cancellationToken);
                var now = DateTimeOffset.UtcNow;
                return Json($"{{\"timePeriod\":{{\"year\":{now.Year},\"month\":{now.Month}}},\"organization\":\"acme\",\"user\":\"octocat\",\"usageItems\":[{{\"product\":\"Copilot\",\"unitType\":\"credits\",\"grossQuantity\":3,\"discountQuantity\":0,\"netQuantity\":3}}]}}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class UnavailableCredentialStore : ICredentialStore
    {
        public CredentialStoreAvailability Availability => CredentialStoreAvailability.Unavailable;
        public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
        public ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken) => ValueTask.FromException(new PlatformNotSupportedException());
        public ValueTask RemoveAsync(string account, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class CopilotBillingHandler(string organization, string user, string token) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.RequestUri!.AbsolutePath, request.RequestUri.Query, request.Headers.Authorization?.ToString() ?? string.Empty));
            if (request.Headers.Authorization?.Parameter != token) return Task.FromResult<HttpResponseMessage>(new(HttpStatusCode.Unauthorized));
            if (request.RequestUri.AbsolutePath == "/user") return Task.FromResult(Json($"{{\"login\":\"{user}\"}}"));
            if (request.RequestUri.AbsolutePath == $"/organizations/{organization}/settings/billing/ai_credit/usage")
            {
                var now = DateTimeOffset.UtcNow;
                return Task.FromResult(Json($"{{\"timePeriod\":{{\"year\":{now.Year},\"month\":{now.Month}}},\"organization\":\"{organization}\",\"user\":\"{user}\",\"usageItems\":[{{\"product\":\"Copilot\",\"unitType\":\"credits\",\"grossQuantity\":1950,\"discountQuantity\":1200,\"netQuantity\":750}}]}}"));
            }
            return Task.FromResult<HttpResponseMessage>(new(HttpStatusCode.NotFound));
        }
        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }

    private sealed record CapturedRequest(string Path, string Query, string Authorization);
}

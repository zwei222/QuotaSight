using System.Net;
using System.Text;
using QuotaSight.Application;
using QuotaSight.Core;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class BlockingIdentityIsolationAdapterTests
{
    [Fact]
    public async Task FetchWithActiveAccount_returns_active_identity_for_success()
    {
        var now = DateTimeOffset.UtcNow;
        using var client = new HttpClient(new SequenceHandler(
            Json(HttpStatusCode.OK, "{\"login\":\"alice\"}"),
            Json(HttpStatusCode.OK, $"{{\"timePeriod\":{{\"year\":{now.Year},\"month\":{now.Month}}},\"organization\":\"acme\",\"user\":\"alice\",\"usageItems\":[{{\"product\":\"Copilot\",\"unitType\":\"credits\",\"grossQuantity\":3,\"discountQuantity\":0,\"netQuantity\":3}}]}}")));

        var result = await new DynamicCopilotAdapter(client, new CountingCredentialStore("token"), () => "acme").FetchWithActiveAccountAsync("GitHub Copilot", default);

        Assert.Equal(FetchStatus.Success, result.Result.Status);
        Assert.Equal("acme/alice", result.ActiveAccount);
        Assert.Equal("acme/alice", Assert.Single(result.Result.Value!).Account);
    }

    [Fact]
    public async Task FetchWithActiveAccount_returns_active_identity_for_success_and_no_data()
    {
        var store = new CountingCredentialStore("token");
        using var client = new HttpClient(new SequenceHandler(
            Json(HttpStatusCode.OK, "{\"login\":\"alice\"}"),
            Json(HttpStatusCode.OK, $"{{\"timePeriod\":{{\"year\":{DateTimeOffset.UtcNow.Year},\"month\":{DateTimeOffset.UtcNow.Month}}},\"organization\":\"acme\",\"user\":\"alice\",\"usageItems\":[]}}")));
        var adapter = new DynamicCopilotAdapter(client, store, () => "  acme  ");

        var result = await adapter.FetchWithActiveAccountAsync("GitHub Copilot", default);

        Assert.Equal(FetchStatus.NoData, result.Result.Status);
        Assert.Equal("acme/alice", result.ActiveAccount);
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public async Task FetchWithActiveAccount_maps_forbidden_and_rate_limit_without_exposing_token()
    {
        const string token = "secret-token";
        foreach (var (billingResponse, expectedStatus) in new[]
        {
            (Json(HttpStatusCode.Forbidden, "", ("X-RateLimit-Remaining", "0")), FetchStatus.RateLimited),
            (Json(HttpStatusCode.Forbidden, "", ("Retry-After", "60")), FetchStatus.RateLimited),
            (Json(HttpStatusCode.Forbidden, ""), FetchStatus.Forbidden),
            (Json((HttpStatusCode)429, ""), FetchStatus.RateLimited)
        })
        {
            var store = new CountingCredentialStore(token);
            using var client = new HttpClient(new SequenceHandler(Json(HttpStatusCode.OK, "{\"login\":\"alice\"}"), billingResponse));
            var adapter = new DynamicCopilotAdapter(client, store, () => "acme");

            var result = await adapter.FetchWithActiveAccountAsync("GitHub Copilot", default);

            Assert.Equal(expectedStatus, result.Result.Status);
            Assert.Equal("acme/alice", result.ActiveAccount);
            Assert.DoesNotContain(token, result.Result.Error ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task FetchWithActiveAccount_maps_user_retry_after_to_rate_limit_without_calling_billing()
    {
        var store = new CountingCredentialStore("token");
        var handler = new SequenceHandler(Json(HttpStatusCode.Forbidden, "", ("Retry-After", "60")));
        using var client = new HttpClient(handler);

        var result = await new DynamicCopilotAdapter(client, store, () => "acme").FetchWithActiveAccountAsync("GitHub Copilot", default);

        Assert.Equal(FetchStatus.RateLimited, result.Result.Status);
        Assert.Null(result.ActiveAccount);
        Assert.Single(handler.Requests);
        Assert.Equal("/user", handler.Requests[0].AbsolutePath);
    }

    [Fact]
    public async Task FetchWithActiveAccount_does_not_call_user_when_organization_is_unset_and_propagates_cancellation()
    {
        var store = new CountingCredentialStore("token");
        var handler = new SequenceHandler(Json(HttpStatusCode.OK, "{\"login\":\"alice\"}"));
        using var client = new HttpClient(handler);
        var adapter = new DynamicCopilotAdapter(client, store, () => "  ");

        var unsupported = await adapter.FetchWithActiveAccountAsync("GitHub Copilot", default);

        Assert.Equal(FetchStatus.ConfigurationError, unsupported.Result.Status);
        Assert.Null(unsupported.ActiveAccount);
        Assert.Equal(0, store.Reads);
        Assert.Empty(handler.Requests);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DynamicCopilotAdapter(client, store, () => "acme").FetchWithActiveAccountAsync("GitHub Copilot", cancellation.Token).AsTask());
    }

    [Theory]
    [InlineData("acme/engineering")]
    [InlineData("-acme")]
    [InlineData("acme-")]
    [InlineData("acme--engineering")]
    [InlineData("acme_engineering")]
    [InlineData("acmé")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task FetchWithActiveAccount_rejects_invalid_organization_slug_before_store_or_http(string organization)
    {
        var store = new CountingCredentialStore("token");
        var handler = new SequenceHandler(Json(HttpStatusCode.OK, "{\"login\":\"alice\"}"));
        using var client = new HttpClient(handler);

        var result = await new DynamicCopilotAdapter(client, store, () => organization).FetchWithActiveAccountAsync("GitHub Copilot", default);

        Assert.Equal(FetchStatus.ConfigurationError, result.Result.Status);
        Assert.Null(result.ActiveAccount);
        Assert.Equal(0, store.Reads);
        Assert.Empty(handler.Requests);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var header in headers) response.Headers.TryAddWithoutValidation(header.Name, header.Value);
        return response;
    }

    private sealed class CountingCredentialStore(string? token) : ICredentialStore
    {
        public int Reads { get; private set; }
        public CredentialStoreAvailability Availability => CredentialStoreAvailability.SecureStore;
        public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken) { Reads++; cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(token); }
        public ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask RemoveAsync(string account, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int index;
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return Task.FromResult(responses[index++]);
        }
    }
}

public sealed class BlockingIdentityIsolationViewModelTests
{
    [Fact]
    public async Task New_identity_no_data_removes_old_automatic_card_without_creating_zero_card()
    {
        var old = Snapshot("org-a/user-a");
        var application = new MutableResultApplication(new QuotaRefreshResult([old], [], activeAccounts: new Dictionary<ProviderKind, string> { [ProviderKind.Copilot] = old.Account }));
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: application, uiDispatcher: new ImmediateUiDispatcher());
        await vm.RefreshAsync();

        application.Result = new QuotaRefreshResult([], [], new HashSet<ProviderKind> { ProviderKind.Copilot }, new Dictionary<ProviderKind, string> { [ProviderKind.Copilot] = "org-b/user-b" });
        await vm.RefreshAsync();

        Assert.DoesNotContain(vm.Cards, card => card.Provider == ProviderKind.Copilot);
        Assert.Equal(PresentationState.Empty, vm.PresentationState);
        Assert.DoesNotContain("error", vm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task New_identity_forbidden_removes_old_automatic_card_but_keeps_error_and_other_provider()
    {
        var old = Snapshot("org-a/user-a");
        var other = Snapshot("other") with { Provider = ProviderKind.OpenCode };
        var application = new MutableResultApplication(new QuotaRefreshResult([old, other], [], activeAccounts: new Dictionary<ProviderKind, string> { [ProviderKind.Copilot] = old.Account }));
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: application, uiDispatcher: new ImmediateUiDispatcher());
        await vm.RefreshAsync();

        application.Result = new QuotaRefreshResult([], [new(ProviderKind.Copilot, FetchStatus.Forbidden)], activeAccounts: new Dictionary<ProviderKind, string> { [ProviderKind.Copilot] = "org-b/user-b" });
        await vm.RefreshAsync();

        Assert.DoesNotContain(vm.Cards, card => card.Provider == ProviderKind.Copilot);
        Assert.Contains(vm.Cards, card => card.Provider == ProviderKind.OpenCode);
        Assert.Equal(PresentationState.Error, vm.PresentationState);
        Assert.True(vm.IsError);
    }

    [Fact]
    public async Task New_identity_no_data_does_not_claim_copilot_last_value_from_unrelated_provider()
    {
        var old = Snapshot("org-a/user-a");
        var other = Snapshot("other") with { Provider = ProviderKind.OpenCode };
        var application = new MutableResultApplication(new QuotaRefreshResult([old, other], [], activeAccounts: new Dictionary<ProviderKind, string> { [ProviderKind.Copilot] = old.Account }));
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: application, uiDispatcher: new ImmediateUiDispatcher());
        await vm.RefreshAsync();

        application.Result = new QuotaRefreshResult([], [], new HashSet<ProviderKind> { ProviderKind.Copilot, ProviderKind.OpenCode }, new Dictionary<ProviderKind, string> { [ProviderKind.Copilot] = "org-b/user-b" });
        await vm.RefreshAsync();

        Assert.Contains(vm.Cards, card => card.Provider == ProviderKind.OpenCode);
        Assert.Contains("Copilot: no data for the current period.", vm.NotificationBannerText, StringComparison.Ordinal);
        Assert.DoesNotContain("Copilot: no data for the current period. Showing the last successful value.", vm.NotificationBannerText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_identity_no_data_keeps_previous_automatic_card()
    {
        var old = Snapshot("org-a/user-a");
        var application = new MutableResultApplication(new QuotaRefreshResult([old], [], activeAccounts: new Dictionary<ProviderKind, string> { [ProviderKind.Copilot] = old.Account }));
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: application, uiDispatcher: new ImmediateUiDispatcher());
        await vm.RefreshAsync();

        application.Result = new QuotaRefreshResult([], [], new HashSet<ProviderKind> { ProviderKind.Copilot }, new Dictionary<ProviderKind, string> { [ProviderKind.Copilot] = old.Account });
        vm.Language = UiLanguage.Japanese;
        await vm.RefreshAsync();

        Assert.Contains(vm.Cards, card => card.Provider == ProviderKind.Copilot && card.Account == old.Account);
        Assert.Equal(PresentationState.Ready, vm.PresentationState);
        Assert.False(vm.IsError);
        Assert.Contains("前回", vm.NotificationBannerText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task New_identity_keeps_manual_copilot_row_when_automatic_card_is_replaced()
    {
        var old = Snapshot("org-a/user-a");
        var manual = old with { Source = QuotaSource.Manual, Confidence = QuotaConfidence.Manual };
        var application = new MutableResultApplication(new QuotaRefreshResult([old, manual], [], activeAccounts: new Dictionary<ProviderKind, string> { [ProviderKind.Copilot] = old.Account }));
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: application, uiDispatcher: new ImmediateUiDispatcher());
        await vm.RefreshAsync();

        application.Result = new QuotaRefreshResult([], [], new HashSet<ProviderKind> { ProviderKind.Copilot }, new Dictionary<ProviderKind, string> { [ProviderKind.Copilot] = "org-b/user-b" });
        await vm.RefreshAsync();

        var card = Assert.Single(vm.Cards, item => item.Provider == ProviderKind.Copilot);
        Assert.Contains(card.Windows, window => window.SourceBadge == "Manual");
    }

    private static QuotaSnapshot Snapshot(string account) => ConcurrencyFixtures.Snapshot(42) with { Provider = ProviderKind.Copilot, Account = account, Source = QuotaSource.Official };

    private sealed class MutableResultApplication(QuotaRefreshResult result) : IQuotaApplication
    {
        public QuotaRefreshResult Result { get; set; } = result;
        public ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result);
    }
}

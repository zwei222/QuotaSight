using System.Net;
using System.Text;
using System.Text.Json;
using QuotaSight.Application;
using QuotaSight.Core;
using QuotaSight.Infrastructure;

namespace QuotaSight.Tests;

public sealed class CodexOAuthTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
    private const string Access = "test-access-token";
    private const string Refresh = "test-refresh-token";

    [Fact]
    public async Task Quota_reads_nested_account_claim_and_sends_header()
    {
        var token = JwtWithPayload(new Dictionary<string, object> { ["https://api.openai.com/auth"] = new Dictionary<string, string> { ["chatgpt_account_id"] = "acct-id" } });
        var result = await new CodexQuotaAdapter(new HttpClient(new AsyncHandler(async request =>
        {
            Assert.Equal("acct-id", request.Headers.GetValues("ChatGPT-Account-Id").Single());
            await Task.Yield();
            return Json(HttpStatusCode.OK, UsageJson(123.4m, 1788696000, 18000, 2m, 1789296000, 999));
        })), new FixedTimeProvider(Now)).FetchTokenAsync(token, "ChatGPT");
        Assert.True(result.IsSuccess);
        Assert.Equal(123.4m, result.Value![0].ReportedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788696000).Subtract(TimeSpan.FromSeconds(18000)), result.Value[0].Window.Start);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788696000), result.Value[0].Window.End);
        Assert.Equal(TimeSpan.FromSeconds(18000), result.Value[0].Window.Duration);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788696000), result.Value[0].Window.ResetAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789296000).Subtract(TimeSpan.FromSeconds(999)), result.Value[1].Window.Start);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789296000), result.Value[1].Window.End);
        Assert.Equal(TimeSpan.FromSeconds(999), result.Value[1].Window.Duration);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789296000), result.Value[1].Window.ResetAt);
    }

    [Fact]
    public async Task Quota_keeps_primary_and_secondary_when_both_windows_are_unknown_custom_duration()
    {
        var result = await FetchUsage(UsageJson(10m, 1788696000, 999, 20m, 1789296000, 999));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Count);
        Assert.Equal(["Codex primary", "Codex secondary"], result.Value.Select(snapshot => snapshot.Metric));
        Assert.All(result.Value, snapshot => Assert.Equal(QuotaWindowKind.Custom, snapshot.Window.Kind));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, FetchStatus.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, FetchStatus.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests, FetchStatus.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, FetchStatus.TransientFailure)]
    public async Task Quota_classifies_http_failures_without_secret_error(HttpStatusCode status, FetchStatus expected)
    {
        var result = await new CodexQuotaAdapter(new HttpClient(new AsyncHandler(_ => Task.FromResult(Json(status, "body-with-no-secret"))))).FetchTokenAsync(Access, "ChatGPT");
        Assert.Equal(expected, result.Status);
        Assert.DoesNotContain(Access, result.Error ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_missing_interval_defaults_to_five_seconds()
    {
        var client = new CodexOAuthClient(new HttpClient(new AsyncHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, "{\"user_code\":\"user\",\"device_auth_id\":\"device\"}")))), new InMemoryCredentialStore(), new FixedTimeProvider(Now));
        var result = await client.StartAsync(default);
        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.FromSeconds(5), result.Value!.Interval);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    [InlineData(3, 3)]
    [InlineData(9, 9)]
    [InlineData(-1, 3)]
    public async Task Start_clamps_device_poll_interval_to_at_least_three_seconds(int supplied, int expected)
    {
        var body = $"{{\"user_code\":\"user\",\"device_auth_id\":\"device\",\"interval\":{supplied}}}";
        var client = new CodexOAuthClient(new HttpClient(new AsyncHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, body)))), new InMemoryCredentialStore(), new FixedTimeProvider(Now));
        var result = await client.StartAsync(default);
        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.FromSeconds(expected), result.Value!.Interval);
    }

    [Fact]
    public async Task Start_invalid_interval_number_is_transient_failure()
    {
        var client = new CodexOAuthClient(new HttpClient(new AsyncHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, "{\"user_code\":\"user\",\"device_auth_id\":\"device\",\"interval\":\"invalid\"}")))), new InMemoryCredentialStore(), new FixedTimeProvider(Now));
        var result = await client.StartAsync(default);
        Assert.Equal(FetchStatus.TransientFailure, result.Status);
    }

    [Fact]
    public async Task Poll_pending_uses_start_interval_after_clamping()
    {
        var count = 0;
        var client = new CodexOAuthClient(new HttpClient(new AsyncHandler(_ =>
        {
            count++;
            return Task.FromResult(count == 1
                ? Json(HttpStatusCode.OK, "{\"user_code\":\"user\",\"device_auth_id\":\"device\",\"interval\":1}")
                : Json(HttpStatusCode.NotFound, "pending"));
        })), new InMemoryCredentialStore(), new FixedTimeProvider(Now));
        var started = await client.StartAsync(default);
        Assert.True(started.IsSuccess);
        var pending = await client.PollAndStoreAsync(started.Value!, default);
        Assert.Equal(FetchStatus.TransientFailure, pending.Status);
        Assert.Equal(TimeSpan.FromSeconds(3), pending.RetryAfter);
    }

    [Fact]
    public async Task Poll_expiry_avoids_http_and_pending_returns_interval()
    {
        var calls = 0;
        var client = new CodexOAuthClient(new HttpClient(new AsyncHandler(_ => { calls++; return Task.FromResult(Json(HttpStatusCode.NotFound, "pending")); })), new InMemoryCredentialStore(), new FixedTimeProvider(Now));
        var expired = new CodexDeviceAuthorization("user", "device", new Uri("https://auth.openai.com/codex/device"), Now.AddSeconds(-1), TimeSpan.FromSeconds(7));
        Assert.Equal(FetchStatus.TransientFailure, (await client.PollAndStoreAsync(expired, default)).Status);
        Assert.Equal(0, calls);
        var active = expired with { ExpiresAt = Now.AddMinutes(5) };
        var pending = await client.PollAndStoreAsync(active, default);
        Assert.Equal(FetchStatus.TransientFailure, pending.Status);
        Assert.Equal(TimeSpan.FromSeconds(7), pending.RetryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, FetchStatus.RateLimited)]
    [InlineData(HttpStatusCode.Unauthorized, FetchStatus.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, FetchStatus.Forbidden)]
    public async Task Poll_classifies_http_status(HttpStatusCode status, FetchStatus expected)
    {
        var client = new CodexOAuthClient(new HttpClient(new AsyncHandler(_ => Task.FromResult(Json(status, "status-body")))), new InMemoryCredentialStore(), new FixedTimeProvider(Now));
        var result = await client.PollAndStoreAsync(new("u", "d", new Uri("https://auth.openai.com/codex/device"), Now.AddMinutes(5), TimeSpan.FromSeconds(3)), default);
        Assert.Equal(expected, result.Status);
        Assert.DoesNotContain(Access, result.Error ?? "", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, FetchStatus.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, FetchStatus.Forbidden)]
    public async Task Start_classifies_authentication_status(HttpStatusCode status, FetchStatus expected)
    {
        var client = new CodexOAuthClient(new HttpClient(new AsyncHandler(_ => Task.FromResult(Json(status, "status-body")))), new InMemoryCredentialStore(), new FixedTimeProvider(Now));
        var result = await client.StartAsync(default);
        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task Start_classifies_rate_limit_and_preserves_retry_after()
    {
        var client = new CodexOAuthClient(new HttpClient(new AsyncHandler(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, "status-body");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(11));
            return Task.FromResult(response);
        })), new InMemoryCredentialStore(), new FixedTimeProvider(Now));
        var result = await client.StartAsync(default);
        Assert.Equal(FetchStatus.RateLimited, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(11), result.RetryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, FetchStatus.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, FetchStatus.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests, FetchStatus.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, FetchStatus.TransientFailure)]
    public async Task OAuth_exchange_classifies_status(HttpStatusCode status, FetchStatus expected)
    {
        var count = 0;
        var client = new CodexOAuthClient(new HttpClient(new AsyncHandler(_ =>
        {
            count++;
            return Task.FromResult(count == 1 ? Json(HttpStatusCode.OK, "{\"authorization_code\":\"code\",\"code_verifier\":\"verifier\"}") : Json(status, "oauth-body"));
        })), new InMemoryCredentialStore(), new FixedTimeProvider(Now));
        var result = await client.PollAndStoreAsync(new("u", "d", new Uri("https://auth.openai.com/codex/device"), Now.AddMinutes(5), TimeSpan.Zero), default);
        Assert.Equal(expected, result.Status);
        Assert.DoesNotContain(Access, result.Error ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Poll_store_failure_is_not_success_and_does_not_expose_token()
    {
        var store = new ThrowingStore();
        var count = 0;
        var client = new CodexOAuthClient(new HttpClient(new AsyncHandler(_ =>
        {
            count++;
            return Task.FromResult(count == 1 ? Json(HttpStatusCode.OK, "{\"authorization_code\":\"code\",\"code_verifier\":\"verifier\"}") : Json(HttpStatusCode.OK, $"{{\"access_token\":\"{Access}\",\"refresh_token\":\"{Refresh}\",\"expires_in\":3600}}"));
        })), store, new FixedTimeProvider(Now));
        var result = await client.PollAndStoreAsync(new("u", "d", new Uri("https://auth.openai.com/codex/device"), Now.AddMinutes(5), TimeSpan.Zero), default);
        Assert.Equal(FetchStatus.TransientFailure, result.Status);
        Assert.DoesNotContain(Access, result.Error ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_is_single_flight_near_expiry_and_rotates_refresh_token()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync(CodexOAuthClient.CredentialKey, BundleJson(Refresh, Now.AddMinutes(1)), default);
        var refreshCalls = 0;
        var handler = new AsyncHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal)) { Interlocked.Increment(ref refreshCalls); await Task.Delay(10); return Json(HttpStatusCode.OK, $"{{\"access_token\":\"{Access}\",\"refresh_token\":\"rotated-refresh\",\"expires_in\":3600}}"); }
            return Json(HttpStatusCode.OK, UsageJson(1, 1788696000, 18000, 2, 1789296000, 999));
        });
        var client = new CodexSessionManager(new CodexOAuthClient(new HttpClient(handler), store, new FixedTimeProvider(Now)), new HttpClient(handler), new FixedTimeProvider(Now));
        var results = await Task.WhenAll(client.FetchAsync(default).AsTask(), client.FetchAsync(default).AsTask());
        Assert.All(results, r => Assert.True(r.IsSuccess, $"status={r.Status}, error={r.Error}, refreshCalls={refreshCalls}"));
        Assert.Equal(1, refreshCalls);
        Assert.Contains("rotated-refresh", await store.GetAsync(CodexOAuthClient.CredentialKey, default));
    }

    [Fact]
    public async Task Logout_removes_key_and_fetch_is_unauthorized()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync(CodexOAuthClient.CredentialKey, BundleJson(Refresh, Now.AddHours(1)), default);
        var manager = new CodexSessionManager(new CodexOAuthClient(new HttpClient(new AsyncHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, "{}")))), store, new FixedTimeProvider(Now)), new HttpClient(), new FixedTimeProvider(Now));
        await manager.LogoutAsync(default);
        Assert.Equal(FetchStatus.Unauthorized, (await manager.FetchAsync(default)).Status);
    }

    [Fact]
    public async Task Logout_during_refresh_rejects_late_refresh_and_fetch()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync(CodexOAuthClient.CredentialKey, BundleJson(Refresh, Now.AddMinutes(1)), default);
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new AsyncHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal))
            {
                refreshStarted.SetResult();
                await releaseRefresh.Task;
                return Json(HttpStatusCode.OK, $"{{\"access_token\":\"{Access}\",\"refresh_token\":\"late-refresh\",\"expires_in\":3600}}");
            }
            return Json(HttpStatusCode.OK, UsageJson(1, 1788696000, 18000, 2, 1789296000, 999));
        });
        var manager = new CodexSessionManager(new CodexOAuthClient(new HttpClient(handler), store, new FixedTimeProvider(Now)), new HttpClient(handler), new FixedTimeProvider(Now));
        var fetch = manager.FetchAsync(default).AsTask();
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var logout = manager.LogoutAsync(default).AsTask();
        releaseRefresh.SetResult();

        await logout;
        var result = await fetch;

        Assert.Equal(FetchStatus.Unauthorized, result.Status);
        Assert.Null(await store.GetAsync(CodexOAuthClient.CredentialKey, default));
    }

    [Fact]
    public async Task Logout_during_quota_http_rejects_late_snapshot_before_fetch_returns()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync(CodexOAuthClient.CredentialKey, BundleJson(Refresh, Now.AddHours(1)), default);
        var quotaStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseQuota = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new AsyncHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/wham/usage", StringComparison.Ordinal))
            {
                quotaStarted.SetResult();
                await releaseQuota.Task;
                return Json(HttpStatusCode.OK, UsageJson(10m, 1788696000, 18000, 20m, 1789296000, 10080));
            }

            return Json(HttpStatusCode.InternalServerError, "unexpected");
        });
        var manager = new CodexSessionManager(
            new CodexOAuthClient(new HttpClient(handler), store, new FixedTimeProvider(Now)),
            new HttpClient(handler),
            new FixedTimeProvider(Now));

        var fetch = manager.FetchAsync(default).AsTask();
        await quotaStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var logout = manager.LogoutAsync(default).AsTask();
        releaseQuota.SetResult();

        var result = await fetch.WaitAsync(TimeSpan.FromSeconds(5));
        await logout.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(FetchStatus.Unauthorized, result.Status);
        Assert.Null(result.Value);
        Assert.Null(await store.GetAsync(CodexOAuthClient.CredentialKey, default));
    }

    [Fact]
    public async Task Logout_during_poll_store_rejects_old_authorization_and_never_repolls_it()
    {
        var store = new BlockingRemoveStore();
        await store.SetAsync(CodexOAuthClient.CredentialKey, BundleJson(Refresh, Now.AddHours(1)), default);
        var calls = 0;
        var client = new CodexOAuthClient(new HttpClient(new AsyncHandler(request =>
        {
            calls++;
            return Task.FromResult(DeviceFlowFixture(request));
        })), store, new FixedTimeProvider(Now));
        var manager = new CodexSessionManager(client, new HttpClient(), new FixedTimeProvider(Now));
        var startedResult = await client.StartAsync(default);
        Assert.True(startedResult.IsSuccess);
        var started = startedResult.Value!;
        var logout = manager.LogoutAsync(default).AsTask();
        await store.RemoveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var poll = client.PollAndStoreAsync(started, default).AsTask();
        store.ReleaseRemove();

        await logout.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(FetchStatus.Unauthorized, (await poll).Status);
        var callsAfterLogout = calls;
        Assert.Equal(FetchStatus.Unauthorized, (await client.PollAndStoreAsync(started, default)).Status);
        Assert.Equal(callsAfterLogout, calls);
        Assert.Null(await store.GetAsync(CodexOAuthClient.CredentialKey, default));

    }

    [Fact]
    public async Task Start_after_logout_during_response_returns_unauthorized_without_old_authorization()
    {
        var responseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new CodexOAuthClient(new HttpClient(new AsyncHandler(async request =>
        {
            responseStarted.SetResult();
            await releaseResponse.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return DeviceFlowFixture(request);
        })), new InMemoryCredentialStore(), new FixedTimeProvider(Now));
        var manager = new CodexSessionManager(client, new HttpClient(), new FixedTimeProvider(Now));
        var start = client.StartAsync(default).AsTask();
        await responseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await manager.LogoutAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        releaseResponse.SetResult();

        var result = await start.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(FetchStatus.Unauthorized, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task HasCredentialAsync_returns_only_existence_for_saved_bundle()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync(CodexOAuthClient.CredentialKey, BundleJson(Refresh, Now.AddHours(1)), default);
        var manager = new CodexSessionManager(new CodexOAuthClient(new HttpClient(), store, new FixedTimeProvider(Now)), new HttpClient(), new FixedTimeProvider(Now));

        Assert.True(await manager.HasCredentialAsync(default));
        Assert.DoesNotContain("CodexTokenBundle", typeof(CodexSessionManager).GetMethod(nameof(CodexSessionManager.HasCredentialAsync))!.ReturnType.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HasCredentialAsync_returns_false_when_store_read_fails_without_exposing_error()
    {
        var manager = new CodexSessionManager(new CodexOAuthClient(new HttpClient(), new ThrowingStore(), new FixedTimeProvider(Now)), new HttpClient(), new FixedTimeProvider(Now));

        Assert.False(await manager.HasCredentialAsync(default));
    }

    [Fact]
    public async Task HasCredentialAsync_propagates_external_cancellation_from_store_read()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var manager = new CodexSessionManager(new CodexOAuthClient(new HttpClient(), new CancellationReadingStore(), new FixedTimeProvider(Now)), new HttpClient(), new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<OperationCanceledException>(() => manager.HasCredentialAsync(cancellation.Token).AsTask());
    }

    [Fact]
    public async Task Poll_store_cancellation_does_not_succeed_or_write_fallback()
    {
        using var cancellation = new CancellationTokenSource();
        var fallback = new InMemoryCredentialStore();
        var primary = new CancellingStore(cancellation);
        var store = new FallbackCredentialStore(primary, fallback);
        var count = 0;
        var client = new CodexOAuthClient(new HttpClient(new AsyncHandler(_ =>
        {
            count++;
            return Task.FromResult(count == 1
                ? Json(HttpStatusCode.OK, "{\"authorization_code\":\"code\",\"code_verifier\":\"verifier\"}")
                : Json(HttpStatusCode.OK, $"{{\"access_token\":\"{Access}\",\"refresh_token\":\"{Refresh}\",\"expires_in\":3600}}"));
        })), store, new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<OperationCanceledException>(() => client.PollAndStoreAsync(new("u", "d", new Uri("https://auth.openai.com/codex/device"), Now.AddMinutes(5), TimeSpan.Zero), cancellation.Token).AsTask());
        Assert.Null(await fallback.GetAsync(CodexOAuthClient.CredentialKey, default));
    }

    [Fact]
    public async Task Quota_keeps_valid_window_when_other_window_missing_and_rejects_invalid_values()
    {
        var valid = await FetchUsage("{\"rate_limit\":{\"primary_window\":{\"used_percent\":150,\"reset_at\":1788696000,\"limit_window_seconds\":18000}}}");
        Assert.True(valid.IsSuccess); Assert.Single(valid.Value!); Assert.Equal(150m, valid.Value![0].ReportedPercent);
        var negative = await FetchUsage("{\"rate_limit\":{\"primary_window\":{\"used_percent\":-1,\"reset_at\":1788696000,\"limit_window_seconds\":18000},\"secondary_window\":{\"used_percent\":1,\"reset_at\":1788696000,\"limit_window_seconds\":0}}}");
        Assert.Equal(FetchStatus.TransientFailure, negative.Status);
        var invalidReset = await FetchUsage("{\"rate_limit\":{\"primary_window\":{\"used_percent\":1,\"reset_at\":999999999999999999,\"limit_window_seconds\":18000}}}");
        Assert.Equal(FetchStatus.TransientFailure, invalidReset.Status);
        var outOfRangeStart = await FetchUsage("{\"rate_limit\":{\"primary_window\":{\"used_percent\":1,\"reset_at\":-62135596800,\"limit_window_seconds\":1}}}");
        Assert.Equal(FetchStatus.TransientFailure, outOfRangeStart.Status);
    }

    [Fact]
    public void Public_poll_result_does_not_expose_token_bundle()
    {
        var method = typeof(CodexSessionManager).GetMethod(nameof(CodexSessionManager.PollAndStoreAsync));
        Assert.NotNull(method);
        Assert.DoesNotContain("CodexTokenBundle", method!.ReturnType.ToString(), StringComparison.Ordinal);
        Assert.False(typeof(CodexOAuthClient).Assembly.GetType("QuotaSight.Infrastructure.CodexTokenBundle")!.IsPublic);
    }

    private static async Task<FetchResult<IReadOnlyList<QuotaSnapshot>>> FetchUsage(string body)
        => await new CodexQuotaAdapter(new HttpClient(new AsyncHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, body)))), new FixedTimeProvider(Now)).FetchTokenAsync(Access, "ChatGPT");
    private static string BundleJson(string refresh, DateTimeOffset expires) => JsonSerializer.Serialize(new Dictionary<string, object?> { ["access_token"] = Access, ["refresh_token"] = refresh, ["id_token"] = null, ["expires_at"] = expires });
    private static string UsageJson(decimal p, long pr, long ps, decimal s, long sr, long ss) => $"{{\"rate_limit\":{{\"primary_window\":{{\"used_percent\":{p},\"reset_at\":{pr},\"limit_window_seconds\":{ps}}},\"secondary_window\":{{\"used_percent\":{s},\"reset_at\":{sr},\"limit_window_seconds\":{ss}}}}}}}";
    private static string JwtWithPayload(object payload) => $"header.{Base64Url(JsonSerializer.Serialize(payload))}.signature";
    private static string Base64Url(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage DeviceFlowFixture(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/api/accounts/deviceauth/usercode" => Json(HttpStatusCode.OK, "{\"user_code\":\"user\",\"device_auth_id\":\"device\"}"),
        "/api/accounts/deviceauth/token" => Json(HttpStatusCode.OK, "{\"authorization_code\":\"code\",\"code_verifier\":\"verifier\"}"),
        "/oauth/token" => Json(HttpStatusCode.OK, $"{{\"access_token\":\"{Access}\",\"refresh_token\":\"{Refresh}\",\"expires_in\":3600}}"),
        _ => Json(HttpStatusCode.InternalServerError, "unexpected")
    };
    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request); }
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class ThrowingStore : ICredentialStore { public CredentialStoreAvailability Availability => CredentialStoreAvailability.SecureStore; public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null); public ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken) => throw new InvalidOperationException("store failed"); public ValueTask RemoveAsync(string account, CancellationToken cancellationToken) => ValueTask.CompletedTask; }
    private sealed class CancellationReadingStore : ICredentialStore { public CredentialStoreAvailability Availability => CredentialStoreAvailability.SecureStore; public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken) => throw new OperationCanceledException(cancellationToken); public ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken) => ValueTask.CompletedTask; public ValueTask RemoveAsync(string account, CancellationToken cancellationToken) => ValueTask.CompletedTask; }
    private sealed class CancellingStore(CancellationTokenSource cancellation) : ICredentialStore { public CredentialStoreAvailability Availability => CredentialStoreAvailability.SecureStore; public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null); public ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken) { cancellation.Cancel(); throw new OperationCanceledException(cancellationToken); } public ValueTask RemoveAsync(string account, CancellationToken cancellationToken) => ValueTask.CompletedTask; }
    private sealed class BlockingRemoveStore : ICredentialStore
    {
        public CredentialStoreAvailability Availability => CredentialStoreAvailability.SecureStore;
        private readonly InMemoryCredentialStore inner = new();
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RemoveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken) => inner.GetAsync(account, cancellationToken);
        public ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken) => inner.SetAsync(account, secret, cancellationToken);
        public async ValueTask RemoveAsync(string account, CancellationToken cancellationToken)
        {
            RemoveStarted.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            await inner.RemoveAsync(account, cancellationToken);
        }
        public void ReleaseRemove() => release.SetResult();
    }
}

using System.Net;
using QuotaSight.Application;
using QuotaSight.Core;
using QuotaSight.Infrastructure;

namespace QuotaSight.Tests;

public sealed class GitHubTokenRefreshTests
{
    [Fact]
    public void Linux_lock_path_uses_the_single_fixed_uid_specific_location()
    {
        var path = GitHubCredentialInterprocessLock.GetLinuxLockPath(12345);

        Assert.Equal("/var/tmp/quotasight-12345/QuotaSight/github-credential.lock", path);
    }

    [Fact]
    public async Task Linux_interprocess_lock_acquires_and_releases_os_file_lock()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var lease = await GitHubCredentialInterprocessLock.AcquireAsync(default);
    }

    [Fact]
    public async Task PollTokens_returns_rotating_tokens_and_expiration_seconds()
    {
        var authorization = Authorization();
        var client = CreateClient(_ => Json(HttpStatusCode.OK, "{\"access_token\":\"access-secret\",\"refresh_token\":\"refresh-secret\",\"expires_in\":3600,\"refresh_token_expires_in\":86400}"));
        var result = await client.PollTokensAsync(authorization, default);
        Assert.True(result.IsSuccess);
        Assert.Equal("access-secret", result.Value!.AccessToken);
        Assert.Equal("refresh-secret", result.Value.RefreshToken);
        Assert.Equal(3600, result.Value.ExpiresIn);
        Assert.Equal(86400, result.Value.RefreshTokenExpiresIn);
        Assert.True(result.Value.CanCreateBundle);

        var legacy = await CreateClient(_ => Json(HttpStatusCode.OK, "{\"access_token\":\"legacy-access\"}"))
            .PollTokensAsync(authorization, default);
        Assert.True(legacy.IsSuccess);
        Assert.Null(legacy.Value!.RefreshToken);
        Assert.False(legacy.Value.CanCreateBundle);
    }

    [Fact]
    public async Task Refresh_sends_only_client_id_grant_and_refresh_token_and_returns_rotated_bundle()
    {
        string? body = null;
        var client = CreateClient(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK, "{\"access_token\":\"new-access-secret\",\"refresh_token\":\"new-refresh-secret\",\"expires_in\":3600,\"refresh_token_expires_in\":86400}");
        });
        var result = await client.RefreshAsync("old-refresh-secret", default);
        Assert.True(result.IsSuccess);
        Assert.Equal("new-access-secret", result.Value!.AccessToken);
        Assert.Equal("new-refresh-secret", result.Value.RefreshToken);
        Assert.Equal("client_id=client-id&grant_type=refresh_token&refresh_token=old-refresh-secret", body);
        Assert.DoesNotContain("client_secret", body!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"error\":\"bad_refresh_token\"}", 200)]
    [InlineData("{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}", 200)]
    [InlineData("not-json", 200)]
    [InlineData("{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600,\"refresh_token_expires_in\":86400}", 401)]
    public async Task Refresh_failures_are_safe_and_never_return_partial_tokens(string responseBody, int status)
    {
        var result = await CreateClient(_ => Json((HttpStatusCode)status, responseBody)).RefreshAsync("refresh-secret", default);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.DoesNotContain("refresh-secret", result.Error ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(responseBody, result.Error ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void Bundle_roundtrips_v2_and_distinguishes_legacy_and_malformed_v2()
    {
        var tokens = new GitHubUserTokens("access-secret", "refresh-secret", 3600, 86400);
        var now = new DateTimeOffset(2026, 9, 23, 1, 2, 3, TimeSpan.Zero);
        var serialized = GitHubUserTokens.SerializeBundle("client-id", tokens, now);
        var parsed = GitHubUserTokens.ParseStoredValue(serialized);
        Assert.Equal(GitHubStoredTokenKind.V2Bundle, parsed.Kind);
        Assert.Equal("client-id", parsed.Bundle!.ClientId);
        Assert.Equal("access-secret", parsed.Bundle.AccessToken);
        Assert.Equal("refresh-secret", parsed.Bundle.RefreshToken);
        Assert.Equal(now.AddSeconds(3600), parsed.Bundle.AccessTokenExpiresAt);
        Assert.Equal(now.AddSeconds(86400), parsed.Bundle.RefreshTokenExpiresAt);
        Assert.Equal(GitHubStoredTokenKind.LegacyAccessToken, GitHubUserTokens.ParseStoredValue("legacy-access-token").Kind);
        Assert.Equal(GitHubStoredTokenKind.InvalidV2Bundle, GitHubUserTokens.ParseStoredValue("github-user-token:v2:not-json").Kind);
    }

    [Fact]
    public void Token_and_response_string_representations_redact_secrets()
    {
        const string access = "access-highly-secret";
        const string refresh = "refresh-highly-secret";
        var tokens = new GitHubUserTokens(access, refresh, 3600, 86400);
        var response = new GitHubTokenResponse(access, refresh, 3600, 86400, null);
        Assert.DoesNotContain(access, tokens.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(refresh, tokens.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(access, response.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(refresh, response.ToString(), StringComparison.Ordinal);
        const string external = "provider-secret-error";
        Assert.DoesNotContain(external, new GitHubTokenResponse(null, null, null, null, external).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_waits_for_refresh_and_refresh_cannot_overwrite_new_login()
    {
        var now = new DateTimeOffset(2026, 9, 23, 1, 2, 3, TimeSpan.Zero);
        var store = new InMemoryCredentialStore();
        await store.SetAsync("github", GitHubUserTokens.SerializeBundle("client-id", new("old-access", "old-refresh", 1, 86400), now.AddMinutes(-10)), default);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new GitHubUserTokenSession(store, () => "client-id", async (_, _) => { started.SetResult(); await release.Task; return FetchResult<GitHubUserTokens>.Success(new("stale-access", "stale-refresh", 3600, 86400)); }, new FixedTimeProvider(now));
        var refresh = session.GetAccessTokenAsync(default).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var login = session.SaveDeviceFlowTokensAsync(new("login-access", "login-refresh", 3600, 86400), default, "client-id").AsTask();
        release.SetResult();
        await Task.WhenAll(refresh, login);
        var saved = GitHubUserTokens.ParseStoredValue(await store.GetAsync("github", default));
        Assert.Equal("login-access", saved.Bundle!.AccessToken);
        Assert.Equal("login-access", await session.GetAccessTokenAsync(default));
    }

    [Fact]
    public async Task Different_sessions_sharing_a_store_serialize_login_with_refresh()
    {
        var now = new DateTimeOffset(2026, 9, 23, 1, 2, 3, TimeSpan.Zero);
        var store = new InMemoryCredentialStore();
        await store.SetAsync("github", GitHubUserTokens.SerializeBundle("client-id", new("old-access", "old-refresh", 1, 86400), now.AddMinutes(-10)), default);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshingSession = new GitHubUserTokenSession(store, () => "client-id", async (_, _) => { started.SetResult(); await release.Task; return FetchResult<GitHubUserTokens>.Success(new("stale-access", "stale-refresh", 3600, 86400)); }, new FixedTimeProvider(now));
        var loginSession = new GitHubUserTokenSession(store, () => "client-id", (_, _) => throw new InvalidOperationException("login session must not refresh"), new FixedTimeProvider(now));
        var refresh = refreshingSession.GetAccessTokenAsync(default).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var login = loginSession.SaveDeviceFlowTokensAsync(new("login-access", "login-refresh", 3600, 86400), default, "client-id").AsTask();
        release.SetResult();
        await Task.WhenAll(refresh, login);
        Assert.Equal("login-access", GitHubUserTokens.ParseStoredValue(await store.GetAsync("github", default)).Bundle!.AccessToken);
        Assert.Equal("login-access", await loginSession.GetAccessTokenAsync(default));
    }

    [Fact]
    public async Task Different_store_instances_sharing_credentials_do_not_lose_login_during_refresh_save()
    {
        var now = new DateTimeOffset(2026, 9, 23, 1, 2, 3, TimeSpan.Zero);
        var state = new SharedCredentialState();
        var refreshStore = new SharedCredentialStore(state, pauseRefreshWrite: true);
        var loginStore = new SharedCredentialStore(state);
        await refreshStore.SetAsync("github", GitHubUserTokens.SerializeBundle("client-id", new("old-access", "old-refresh", 1, 86400), now.AddMinutes(-10)), default);
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshSession = new GitHubUserTokenSession(refreshStore, () => "client-id", async (_, _) =>
        {
            refreshStarted.SetResult();
            await releaseRefresh.Task;
            return FetchResult<GitHubUserTokens>.Success(new("stale-access", "stale-refresh", 3600, 86400));
        }, new FixedTimeProvider(now));
        var loginSession = new GitHubUserTokenSession(loginStore, () => "client-id", (_, _) => throw new InvalidOperationException("login session must not refresh"), new FixedTimeProvider(now));

        var refresh = refreshSession.GetAccessTokenAsync(default).AsTask();
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        releaseRefresh.SetResult();
        await refreshStore.WritePaused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var login = loginSession.SaveDeviceFlowTokensAsync(new("login-access", "login-refresh", 3600, 86400), default, "client-id").AsTask();
        refreshStore.ReleaseWrite.SetResult();
        await Task.WhenAll(refresh, login);

        var stored = GitHubUserTokens.ParseStoredValue(await loginStore.GetAsync("github", default));
        Assert.Equal("login-access", stored.Bundle!.AccessToken);
        Assert.Equal("login-access", await loginSession.GetAccessTokenAsync(default));
    }

    [Fact]
    public async Task Refresh_result_reports_failure_status_instead_of_collapsing_to_unauthorized()
    {
        var now = new DateTimeOffset(2026, 9, 23, 1, 2, 3, TimeSpan.Zero);
        var store = new InMemoryCredentialStore();
        await store.SetAsync("github", GitHubUserTokens.SerializeBundle("client-id", new("old-access", "old-refresh", 1, 86400), now.AddMinutes(-10)), default);
        var session = new GitHubUserTokenSession(store, () => "client-id", (_, _) => ValueTask.FromResult(new FetchResult<GitHubUserTokens>(FetchStatus.RateLimited)), new FixedTimeProvider(now));
        Assert.Equal(FetchStatus.RateLimited, (await session.GetAccessTokenResultAsync(default)).Status);
    }

    [Fact]
    public async Task Successful_refresh_without_bundle_tokens_returns_safe_failure_and_preserves_stored_bundle()
    {
        var now = new DateTimeOffset(2026, 9, 23, 1, 2, 3, TimeSpan.Zero);
        var store = new InMemoryCredentialStore();
        var original = GitHubUserTokens.SerializeBundle("client-id", new("old-access", "old-refresh", 1, 86400), now.AddMinutes(-10));
        await store.SetAsync("github", original, default);
        var session = new GitHubUserTokenSession(store, () => "client-id",
            (_, _) => ValueTask.FromResult(FetchResult<GitHubUserTokens>.Success(new("partial-access", null, null, null))),
            new FixedTimeProvider(now));

        var result = await session.GetAccessTokenResultAsync(default);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Status, new[] { FetchStatus.Unauthorized, FetchStatus.TransientFailure });
        Assert.Null(result.Value);
        Assert.Equal(original, await store.GetAsync("github", default));
    }

    [Fact]
    public async Task Client_id_change_during_refresh_does_not_relabel_old_credentials()
    {
        var now = new DateTimeOffset(2026, 9, 23, 1, 2, 3, TimeSpan.Zero);
        var configuredId = "old-client";
        var store = new InMemoryCredentialStore();
        var original = GitHubUserTokens.SerializeBundle(configuredId, new("old-access", "old-refresh", 1, 86400), now.AddMinutes(-10));
        await store.SetAsync("github", original, default);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new GitHubUserTokenSession(store, () => configuredId, async (_, _) => { started.SetResult(); await release.Task; return FetchResult<GitHubUserTokens>.Success(new("rotated-access", "rotated-refresh", 3600, 86400)); }, new FixedTimeProvider(now));
        var operation = session.GetAccessTokenResultAsync(default).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        configuredId = "new-client";
        release.SetResult();
        Assert.Equal(FetchStatus.ConfigurationError, (await operation).Status);
        Assert.Equal(original, await store.GetAsync("github", default));
    }

    [Fact]
    public async Task Poll_rejects_authorization_started_with_another_client_id()
    {
        var client = CreateClient(_ => throw new InvalidOperationException("must not poll"));
        var mismatched = Authorization() with { ClientId = "another-client" };
        var result = await client.PollTokensAsync(mismatched, default);
        Assert.Equal(FetchStatus.ConfigurationError, result.Status);
    }

    [Fact]
    public async Task Expired_bundle_refreshes_and_saves_rotated_bundle()
    {
        var now = new DateTimeOffset(2026, 9, 23, 1, 2, 3, TimeSpan.Zero);
        var store = new InMemoryCredentialStore();
        await store.SetAsync("github", GitHubUserTokens.SerializeBundle("client-id", new("old-access", "old-refresh", 1, 86400), now.AddMinutes(-10)), default);
        var handler = new ResponseHandler(_ => Json(HttpStatusCode.OK, "{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":3600,\"refresh_token_expires_in\":86400}"));
        var session = new GitHubUserTokenSession(store, () => "client-id", new HttpClient(handler), new FixedTimeProvider(now));
        var token = await session.GetAccessTokenAsync(default);
        Assert.Equal("new-access", token);
        Assert.Equal(GitHubStoredTokenKind.V2Bundle, GitHubUserTokens.ParseStoredValue(await store.GetAsync("github", default)).Kind);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Legacy_tokens_and_client_id_mismatch_never_refresh_or_become_a_bearer()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync("github", "legacy-access", default);
        var legacyHandler = new ResponseHandler(_ => throw new InvalidOperationException("unexpected request"));
        var session = new GitHubUserTokenSession(store, () => "client-id", new HttpClient(legacyHandler));
        Assert.Null(await session.GetAccessTokenAsync(default, forceRefresh: true));
        Assert.Equal(0, legacyHandler.RequestCount);

        var now = DateTimeOffset.UtcNow;
        await store.SetAsync("github", GitHubUserTokens.SerializeBundle("old-client", new("old-access", "old-refresh", 3600, 86400), now), default);
        Assert.Null(await session.GetAccessTokenAsync(default));
        Assert.Equal(0, legacyHandler.RequestCount);
    }

    [Fact]
    public async Task Concurrent_expiration_refreshes_once()
    {
        var now = new DateTimeOffset(2026, 9, 23, 1, 2, 3, TimeSpan.Zero);
        var store = new InMemoryCredentialStore();
        await store.SetAsync("github", GitHubUserTokens.SerializeBundle("client-id", new("old-access", "old-refresh", 1, 86400), now.AddMinutes(-10)), default);
        var handler = new ResponseHandler(_ => Json(HttpStatusCode.OK, "{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":3600,\"refresh_token_expires_in\":86400}"));
        var session = new GitHubUserTokenSession(store, () => "client-id", new HttpClient(handler), new FixedTimeProvider(now));
        var results = await Task.WhenAll(session.GetAccessTokenAsync(default).AsTask(), session.GetAccessTokenAsync(default).AsTask());
        Assert.All(results, token => Assert.Equal("new-access", token));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Cancellation_after_refresh_starts_saves_rotation_before_throwing()
    {
        var now = new DateTimeOffset(2026, 9, 23, 1, 2, 3, TimeSpan.Zero);
        var store = new InMemoryCredentialStore();
        await store.SetAsync("github", GitHubUserTokens.SerializeBundle("client-id", new("old-access", "old-refresh", 1, 86400), now.AddMinutes(-10)), default);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new AsyncResponseHandler(async _ => { started.SetResult(); await release.Task; return Json(HttpStatusCode.OK, "{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":3600,\"refresh_token_expires_in\":86400}"); });
        var session = new GitHubUserTokenSession(store, () => "client-id", new HttpClient(handler), new FixedTimeProvider(now));
        using var cancellation = new CancellationTokenSource();
        var operation = session.GetAccessTokenAsync(cancellation.Token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        var saved = GitHubUserTokens.ParseStoredValue(await store.GetAsync("github", default));
        Assert.Equal("new-access", saved.Bundle!.AccessToken);
        Assert.Equal("new-refresh", saved.Bundle.RefreshToken);
    }

    private static DeviceAuthorizationStart Authorization() => new("device-code", "user-code", new Uri("https://github.com/login/device"), DateTimeOffset.UtcNow.AddMinutes(2), TimeSpan.Zero);
    private static GitHubDeviceFlowClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> response) => new(new HttpClient(new Handler(response)), "client-id", delay: new NoDelay());
    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { RequestCount++; return Task.FromResult(response(request)); }
    }
    private sealed class AsyncResponseHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request);
    }
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class NoDelay : IDeviceFlowDelay { public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => ValueTask.CompletedTask; }

    private sealed class SharedCredentialState
    {
        public string? Value { get; set; }
    }

    private sealed class SharedCredentialStore(SharedCredentialState state, bool pauseRefreshWrite = false) : ICredentialStore
    {
        public CredentialStoreAvailability Availability => CredentialStoreAvailability.SecureStore;
        public TaskCompletionSource WritePaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken) => ValueTask.FromResult(state.Value);
        public async ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken)
        {
            if (pauseRefreshWrite && secret.Contains("stale-access", StringComparison.Ordinal))
            {
                WritePaused.SetResult();
                await ReleaseWrite.Task.WaitAsync(cancellationToken);
            }
            state.Value = secret;
        }
        public ValueTask RemoveAsync(string account, CancellationToken cancellationToken) { state.Value = null; return ValueTask.CompletedTask; }
    }
}

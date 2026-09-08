using System.Net;
using System.Text;
using QuotaSight.Application;
using QuotaSight.Core;
using QuotaSight.Infrastructure;

namespace QuotaSight.Tests;

public sealed class CredentialBackedQuotaApplicationTests
{
    [Fact]
    public async Task Refresh_without_opencode_key_returns_codex_snapshot_and_appends_once()
    {
        var credentials = new InMemoryCredentialStore();
        await credentials.SetAsync(CodexOAuthClient.CredentialKey, "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_at\":\"2099-01-01T00:00:00+00:00\"}", default);
        var codex = new CodexSessionManager(
            new CodexOAuthClient(new HttpClient(new Handler(_ => Json("{\"rate_limit\":{\"primary_window\":{\"used_percent\":25,\"reset_at\":1788696000,\"limit_window_seconds\":18000}}}"))), credentials),
            new HttpClient(new Handler(_ => Json("{\"rate_limit\":{\"primary_window\":{\"used_percent\":25,\"reset_at\":1788696000,\"limit_window_seconds\":18000}}}"))));
        var history = new RecordingHistory();
        var application = new CredentialBackedQuotaApplication(
            _ => throw new Xunit.Sdk.XunitException("OpenCode adapter must not be called"),
            credentials,
            history,
            codexSessionManager: codex);

        var result = await application.RefreshAsync(default);

        Assert.Single(result.Snapshots);
        Assert.Equal(ProviderKind.ChatGpt, result.Snapshots[0].Provider);
        Assert.Empty(result.Failures);
        Assert.Equal(1, history.AppendCount);
        Assert.Single(history.LastSnapshots!);
    }

    [Fact]
    public async Task Refresh_starts_codex_while_opencode_is_stalled_and_returns_codex_snapshot_after_provider_timeout()
    {
        using var cancellation = new CancellationTokenSource();
        var codexStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var credentials = new InMemoryCredentialStore();
        await credentials.SetAsync(CodexOAuthClient.CredentialKey, "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_at\":\"2099-01-01T00:00:00+00:00\"}", default);
        await credentials.SetAsync("OpenCode Go", "open-code-key", default);
        var history = new RecordingHistory();
        var application = new CredentialBackedQuotaApplication(
            _ => new StallingAdapter(),
            credentials,
            history,
            null,
            CreateCodex(credentials, onRequest: () => codexStarted.TrySetResult()),
            TimeSpan.FromMilliseconds(50));

        var refresh = application.RefreshAsync(cancellation.Token).AsTask();
        try
        {
            await codexStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(250));
            var result = await refresh.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Single(result.Snapshots);
            Assert.Equal(ProviderKind.ChatGpt, result.Snapshots[0].Provider);
            var failure = Assert.Single(result.Failures);
            Assert.Equal(ProviderKind.OpenCode, failure.Provider);
            Assert.Equal(FetchStatus.TransientFailure, failure.Status);
            Assert.Equal(1, history.AppendCount);
        }
        finally
        {
            cancellation.Cancel();
            try { await refresh; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Refresh_returns_opencode_when_codex_is_stalled_until_provider_timeout()
    {
        var credentials = new InMemoryCredentialStore();
        await credentials.SetAsync(CodexOAuthClient.CredentialKey, "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_at\":\"2099-01-01T00:00:00+00:00\"}", default);
        await credentials.SetAsync("OpenCode Go", "open-code-key", default);
        var history = new RecordingHistory();
        var codex = new CodexSessionManager(
            new CodexOAuthClient(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK))), credentials),
            new HttpClient(new BlockingHandler()));
        var application = new CredentialBackedQuotaApplication(
            _ => new FixedAdapter(OpenCodeSnapshot()),
            credentials,
            history,
            null,
            codex,
            TimeSpan.FromMilliseconds(50));

        var result = await application.RefreshAsync(default);

        Assert.Single(result.Snapshots);
        Assert.Equal(ProviderKind.OpenCode, result.Snapshots[0].Provider);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(ProviderKind.ChatGpt, failure.Provider);
        Assert.Equal(FetchStatus.TransientFailure, failure.Status);
        Assert.Equal(1, history.AppendCount);
    }

    [Fact]
    public async Task Refresh_merges_successful_sources_and_appends_once()
    {
        var credentials = new InMemoryCredentialStore();
        await credentials.SetAsync(CodexOAuthClient.CredentialKey, "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_at\":\"2099-01-01T00:00:00+00:00\"}", default);
        await credentials.SetAsync("OpenCode Go", "open-code-key", default);
        var history = new RecordingHistory();
        var application = new CredentialBackedQuotaApplication(
            _ => new FixedAdapter(OpenCodeSnapshot()),
            credentials,
            history,
            null,
            CreateCodex(credentials));

        var result = await application.RefreshAsync(default);

        Assert.Equal(2, result.Snapshots.Count);
        Assert.Empty(result.Failures);
        Assert.Equal(1, history.AppendCount);
        Assert.Equal(2, history.LastSnapshots!.Count);
    }

    [Fact]
    public async Task Refresh_returns_opencode_when_codex_fails_and_does_not_expose_error()
    {
        var credentials = new InMemoryCredentialStore();
        await credentials.SetAsync("OpenCode Go", "open-code-key", default);
        await credentials.SetAsync(CodexOAuthClient.CredentialKey, "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_at\":\"2099-01-01T00:00:00+00:00\"}", default);
        var history = new RecordingHistory();
        var application = new CredentialBackedQuotaApplication(
            _ => new FixedAdapter(OpenCodeSnapshot()),
            credentials,
            history,
            null,
            CreateCodex(credentials, HttpStatusCode.Unauthorized));

        var result = await application.RefreshAsync(default);

        Assert.Single(result.Snapshots);
        Assert.Equal(ProviderKind.OpenCode, result.Snapshots[0].Provider);
        Assert.Single(result.Failures);
        Assert.Equal(FetchStatus.Unauthorized, result.Failures[0].Status);
        Assert.Equal(1, history.AppendCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, FetchStatus.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, FetchStatus.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests, FetchStatus.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, FetchStatus.TransientFailure)]
    public async Task Refresh_classifies_codex_failures_without_error_details(HttpStatusCode status, FetchStatus expected)
    {
        var credentials = new InMemoryCredentialStore();
        await credentials.SetAsync(CodexOAuthClient.CredentialKey, "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_at\":\"2099-01-01T00:00:00+00:00\"}", default);
        var application = new CredentialBackedQuotaApplication(
            _ => throw new Xunit.Sdk.XunitException("OpenCode adapter must not be called"),
            credentials,
            new RecordingHistory(),
            codexSessionManager: CreateCodex(credentials, status));

        var result = await application.RefreshAsync(default);

        Assert.Empty(result.Snapshots);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(ProviderKind.ChatGpt, failure.Provider);
        Assert.Equal(expected, failure.Status);
        Assert.Null(failure.RetryAfter);
        Assert.DoesNotContain("access", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_does_not_append_when_both_sources_are_empty()
    {
        var history = new RecordingHistory();
        var application = new CredentialBackedQuotaApplication(_ => new FixedAdapter(), new InMemoryCredentialStore(), history);

        var result = await application.RefreshAsync(default);

        Assert.Empty(result.Snapshots);
        Assert.Empty(result.Failures);
        Assert.Equal(0, history.AppendCount);
    }

    [Fact]
    public async Task Refresh_propagates_external_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var credentials = new InMemoryCredentialStore();
        await credentials.SetAsync("OpenCode Go", "open-code-key", default);
        var history = new RecordingHistory();
        var application = new CredentialBackedQuotaApplication(
            _ => new CancellableAdapter(cancellation.Token),
            credentials,
            history);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => application.RefreshAsync(cancellation.Token).AsTask());
    }

    private static CodexSessionManager CreateCodex(InMemoryCredentialStore credentials, HttpStatusCode status = HttpStatusCode.OK, Action? onRequest = null)
    {
        var responseFactory = status == HttpStatusCode.OK
            ? new Func<HttpResponseMessage>(() => Json("{\"rate_limit\":{\"primary_window\":{\"used_percent\":25,\"reset_at\":1788696000,\"limit_window_seconds\":18000}}}"))
            : new Func<HttpResponseMessage>(() => new HttpResponseMessage(status));
        return new CodexSessionManager(
            new CodexOAuthClient(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK))), credentials),
            new HttpClient(new Handler(_ => { onRequest?.Invoke(); return responseFactory(); })));
    }

    private static QuotaSnapshot OpenCodeSnapshot() => new(
        ProviderKind.OpenCode,
        "OpenCode Go",
        "usage",
        new QuotaWindow(QuotaWindowKind.Monthly, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)),
        10,
        100,
        null,
        "unit",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        QuotaSource.Official,
        QuotaConfidence.Official,
        null);

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    private sealed class FixedAdapter(params QuotaSnapshot[] snapshots) : IQuotaAdapter
    {
        public ProviderKind Provider => ProviderKind.OpenCode;
        public ValueTask<FetchResult<IReadOnlyList<QuotaSnapshot>>> FetchAsync(string account, CancellationToken cancellationToken) => ValueTask.FromResult(FetchResult<IReadOnlyList<QuotaSnapshot>>.Success(snapshots));
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class StallingAdapter : IQuotaAdapter
    {
        public ProviderKind Provider => ProviderKind.OpenCode;
        public async ValueTask<FetchResult<IReadOnlyList<QuotaSnapshot>>> FetchAsync(string account, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return FetchResult<IReadOnlyList<QuotaSnapshot>>.Success([]);
        }
    }

    private sealed class CancellableAdapter(CancellationToken token) : IQuotaAdapter
    {
        public ProviderKind Provider => ProviderKind.OpenCode;
        public ValueTask<FetchResult<IReadOnlyList<QuotaSnapshot>>> FetchAsync(string account, CancellationToken _) => ValueTask.FromCanceled<FetchResult<IReadOnlyList<QuotaSnapshot>>>(token);
    }

    private sealed class RecordingHistory : IQuotaHistory
    {
        public int AppendCount { get; private set; }
        public IReadOnlyList<QuotaSnapshot>? LastSnapshots { get; private set; }
        public ValueTask AppendAsync(IReadOnlyList<QuotaSnapshot> snapshots, CancellationToken cancellationToken) { AppendCount++; LastSnapshots = snapshots; return ValueTask.CompletedTask; }
        public ValueTask<IReadOnlyList<QuotaSnapshot>> ReadAsync(DateOnly day, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<QuotaSnapshot>>([]);
        public ValueTask<IReadOnlyList<QuotaHistoryEntry>> ReadEventsAsync(DateOnly day, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<QuotaHistoryEntry>>([]);
        public ValueTask DeleteEventAsync(Guid eventId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask PruneAsync(DateOnly before, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(DateOnly? day, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ExportJsonAsync(Stream output, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ExportCsvAsync(Stream output, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}

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

        Assert.Single(result);
        Assert.Equal(ProviderKind.ChatGpt, result[0].Provider);
        Assert.Equal(1, history.AppendCount);
        Assert.Single(history.LastSnapshots!);
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

        Assert.Equal(2, result.Count);
        Assert.Equal(1, history.AppendCount);
        Assert.Equal(2, history.LastSnapshots!.Count);
    }

    [Fact]
    public async Task Refresh_returns_opencode_when_codex_fails_and_does_not_expose_error()
    {
        var credentials = new InMemoryCredentialStore();
        await credentials.SetAsync("OpenCode Go", "open-code-key", default);
        var history = new RecordingHistory();
        var application = new CredentialBackedQuotaApplication(
            _ => new FixedAdapter(OpenCodeSnapshot()),
            credentials,
            history,
            null,
            CreateCodex(credentials, HttpStatusCode.InternalServerError));

        var result = await application.RefreshAsync(default);

        Assert.Single(result);
        Assert.Equal(ProviderKind.OpenCode, result[0].Provider);
        Assert.Equal(1, history.AppendCount);
    }

    [Fact]
    public async Task Refresh_does_not_append_when_both_sources_are_empty()
    {
        var history = new RecordingHistory();
        var application = new CredentialBackedQuotaApplication(_ => new FixedAdapter(), new InMemoryCredentialStore(), history);

        var result = await application.RefreshAsync(default);

        Assert.Empty(result);
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

    private static CodexSessionManager CreateCodex(InMemoryCredentialStore credentials, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new Handler(_ => status == HttpStatusCode.OK
            ? Json("{\"rate_limit\":{\"primary_window\":{\"used_percent\":25,\"reset_at\":1788696000,\"limit_window_seconds\":18000}}}")
            : new HttpResponseMessage(status));
        return new CodexSessionManager(
            new CodexOAuthClient(new HttpClient(handler), credentials),
            new HttpClient(handler));
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

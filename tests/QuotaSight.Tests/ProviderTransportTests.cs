using System.Net;
using System.Net.Http;
using QuotaSight.Core;
using QuotaSight.Infrastructure;

namespace QuotaSight.Tests;

public sealed class ProviderTransportTests
{
    [Fact]
    public async Task OpenCode_official_schema_maps_all_windows_without_inventing_totals()
    {
        const string json = """
            {"usage":{"rolling":{"status":"ok","percent":12.34,"resetsAt":"2026-09-06T12:00:00Z"},"weekly":{"status":"rate-limited","percent":56.78,"resetsAt":"2026-09-13T12:00:00Z"},"monthly":{"status":"ok","percent":90.12,"resetsAt":"2026-10-01T12:00:00Z"}}}
            """;
        var now = new DateTimeOffset(2026, 9, 6, 10, 0, 0, TimeSpan.Zero);
        var result = await new OpenCodeGoAdapter(new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, json))), "api-key", new Uri("https://example.test/usage"), new FixedTimeProvider(now)).FetchAsync("acct", default);

        Assert.True(result.IsSuccess);
        var snapshots = Assert.IsAssignableFrom<IReadOnlyList<QuotaSnapshot>>(result.Value);
        Assert.Equal(3, snapshots.Count);
        Assert.Collection(snapshots,
            snapshot => AssertSnapshot(snapshot, QuotaWindowKind.Rolling, 12.34m, "2026-09-06T12:00:00Z"),
            snapshot => AssertSnapshot(snapshot, QuotaWindowKind.Weekly, 56.78m, "2026-09-13T12:00:00Z"),
            snapshot => AssertSnapshot(snapshot, QuotaWindowKind.Monthly, 90.12m, "2026-10-01T12:00:00Z"));
    }

    [Fact]
    public async Task OpenCode_transport_and_json_failures_are_transient_without_secret_or_body()
    {
        const string secret = "super-secret-api-key";
        const string body = "secret response body";
        foreach (var handler in new HttpMessageHandler[]
        {
            new Handler(_ => throw new HttpRequestException("dns failure")),
            new Handler(_ => Json(HttpStatusCode.OK, "{not-json")),
            new Handler(_ => Json(HttpStatusCode.OK, body))
        })
        {
            var result = await new OpenCodeGoAdapter(new HttpClient(handler), secret, new Uri("https://example.test/usage"), new FixedTimeProvider(DateTimeOffset.UtcNow)).FetchAsync("acct", default);
            Assert.Equal(FetchStatus.TransientFailure, result.Status);
            Assert.DoesNotContain(secret, result.Error ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain(body, result.Error ?? "", StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GitHub_start_transport_and_json_failures_are_failures_without_secret_or_body()
    {
        const string body = "token-or-raw-body";
        foreach (var handler in new HttpMessageHandler[]
        {
            new Handler(_ => throw new HttpRequestException("dns failure")),
            new Handler(_ => Json(HttpStatusCode.OK, "{not-json")),
            new Handler(_ => Json(HttpStatusCode.OK, body))
        })
        {
            var result = await new GitHubDeviceFlowClient(new HttpClient(handler), "client-id").StartAsync(default);
            Assert.Equal(FetchStatus.TransientFailure, result.Status);
            Assert.DoesNotContain(body, result.Error ?? "", StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GitHub_poll_transport_and_json_failures_are_failures_without_token_or_body()
    {
        const string token = "access-token-secret";
        const string body = "token-or-raw-body";
        var authorization = new DeviceAuthorizationStart("device-code", "user-code", new Uri("https://github.com/login/device"), DateTimeOffset.UtcNow.AddMinutes(1), TimeSpan.Zero);
        foreach (var handler in new HttpMessageHandler[]
        {
            new Handler(_ => throw new HttpRequestException("dns failure")),
            new Handler(_ => Json(HttpStatusCode.OK, "{not-json")),
            new Handler(_ => Json(HttpStatusCode.OK, body)),
            new Handler(_ => Json(HttpStatusCode.OK, $"{{\"access_token\":\"{token}\""))
        })
        {
            var result = await new GitHubDeviceFlowClient(new HttpClient(handler), "client-id", delay: new NoDelay()).PollAsync(authorization, default);
            Assert.Equal(FetchStatus.TransientFailure, result.Status);
            Assert.Null(result.Value);
            Assert.DoesNotContain(token, result.Error ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain(body, result.Error ?? "", StringComparison.Ordinal);
        }
    }

    private static void AssertSnapshot(QuotaSnapshot snapshot, QuotaWindowKind kind, decimal percent, string resetAt)
    {
        Assert.Equal(kind, snapshot.Window.Kind);
        Assert.Equal(percent, snapshot.ReportedPercent);
        Assert.Equal(DateTimeOffset.Parse(resetAt), snapshot.Window.ResetAt);
        Assert.Equal(DateTimeOffset.Parse(resetAt), snapshot.Window.End);
        Assert.Null(snapshot.Used);
        Assert.Null(snapshot.Limit);
        Assert.Equal(QuotaSource.Official, snapshot.Source);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status) { Content = new StringContent(content) };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    private sealed class NoDelay : IDeviceFlowDelay
    {
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

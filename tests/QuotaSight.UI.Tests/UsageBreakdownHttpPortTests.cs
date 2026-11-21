using System.Globalization;
using System.Net;
using System.Text;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// Contract tests for the explicit HTTPS breakdown read adapter
/// (<c>GET /v1/usage/breakdown</c>). Every test drives a fake peer (an
/// <see cref="HttpMessageHandler"/>), so both the request that would hit the
/// network and the response that would be parsed are observed exactly.
/// </summary>
public sealed class UsageBreakdownHttpPortTests
{
    private static readonly Uri Origin = new("https://usage.test");
    private const string Identity = "owner-a";
    private static readonly DateOnly From = new(2026, 11, 1);
    private static readonly DateOnly To = new(2026, 11, 7);

    [Fact]
    public async Task ReadsBreakdownEndpointWithAccessHeadersAndVerifiedParts()
    {
        var peer = new FakePeer(_ => Json(BreakdownBody(
            Parts(Part("claude", "anthropic", "claude-sonnet-5-5[1m]", 10, 20, 5, 3, 2)),
            Coverage("2026-11-01", "col-a1", 40, 40),
            CollectorList(Collector("col-a1", 1, "\"2026-11-02T00:00:00.000Z\"")))));
        using var harness = Harness.Create(peer, Credential());

        var breakdown = await harness.Port.ReadBreakdownAsync(From, To, null, CancellationToken.None);

        Assert.Equal(From, breakdown.From);
        Assert.Equal(To, breakdown.To);
        var part = Assert.Single(breakdown.Parts);
        Assert.Equal(new UsagePart("claude", "anthropic", "claude-sonnet-5-5[1m]", 10, 20, 5, 3, 2, 40), part);
        var coverage = Assert.Single(breakdown.Coverage);
        Assert.Equal(new UsageCoverage(new DateOnly(2026, 11, 1), "col-a1", 40, 40), coverage);
        var receipt = Assert.Single(breakdown.Collectors);
        Assert.Equal("col-a1", receipt.CollectorId);
        Assert.Equal(1, receipt.Generation);

        var request = Assert.Single(peer.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(
            $"https://usage.test/v1/usage/breakdown?from=2026-11-01&to=2026-11-07",
            request.Uri);
        Assert.Equal("synthetic-id.access", request.AccessClientId);
        Assert.Equal("synthetic-secret", request.AccessClientSecret);
        Assert.Null(request.Authorization);
        Assert.Null(request.Cookie);
    }

    [Fact]
    public async Task BreakdownFilterIsSentAsAQueryParameter()
    {
        var peer = new FakePeer(_ => Json(BreakdownBody(
            Parts(),
            string.Empty,
            CollectorList(Collector("col-a1", 1, "null")))));
        using var harness = Harness.Create(peer, Credential());

        var breakdown = await harness.Port.ReadBreakdownAsync(From, To, "col-a1", CancellationToken.None);

        Assert.Empty(breakdown.Parts);
        Assert.Contains("collector=col-a1", Assert.Single(peer.Requests).Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreservesEmptyUnknownKeysAndCommaJoinedProviders()
    {
        var peer = new FakePeer(_ => Json(BreakdownBody(
            Parts(Part("", "anthropic,google", "", 1, 0, 0, 0, 0)),
            Coverage("2026-11-01", "col-a1", 1, 1),
            CollectorList(Collector("col-a1", 1, "null")))));
        using var harness = Harness.Create(peer, Credential());

        var breakdown = await harness.Port.ReadBreakdownAsync(From, To, null, CancellationToken.None);

        var part = Assert.Single(breakdown.Parts);
        Assert.Equal(string.Empty, part.Client);
        Assert.Equal("anthropic,google", part.Provider);
        Assert.Equal(string.Empty, part.Model);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public async Task RejectsRootContractViolations(string kind)
    {
        const string Good = "\"from\":\"2026-11-01\",\"to\":\"2026-11-07\",\"parts\":[],\"coverage\":[],\"collectors\":[]";
        var body = kind switch
        {
            "unknown" => "{\"from\":\"2026-11-01\",\"to\":\"2026-11-07\",\"parts\":[],\"coverage\":[],\"collectors\":[],\"totalTokens\":0}",
            "missing" => "{\"from\":\"2026-11-01\",\"to\":\"2026-11-07\",\"parts\":[],\"coverage\":[]}",
            _ => "{\"from\":\"2026-11-01\",\"to\":\"2026-11-07\",\"parts\":[],\"coverage\":[],\"collectors\":[],\"collectors\":[]}",
        };
        _ = Good;
        var peer = new FakePeer(_ => Json(body));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.MalformedResponse, failure.Failure);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public async Task RejectsPartRowContractViolations(string kind)
    {
        var row = kind switch
        {
            "extra" => "{\"client\":\"claude\",\"provider\":\"anthropic\",\"model\":\"m\",\"input\":1,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"reasoning\":0,\"total\":1,\"provider2\":\"x\"}",
            "missing" => "{\"client\":\"claude\",\"provider\":\"anthropic\",\"model\":\"m\",\"input\":1,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":1}",
            _ => "{\"client\":\"claude\",\"provider\":\"anthropic\",\"model\":\"m\",\"input\":1,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"reasoning\":0,\"reasoning\":0,\"total\":1}",
        };
        var peer = new FakePeer(_ => Json(BreakdownBody(
            Parts(row),
            Coverage("2026-11-01", "col-a1", 1, 1),
            CollectorList(Collector("col-a1", 1, "null")))));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.MalformedResponse, failure.Failure);
    }

    [Theory]
    [InlineData("\"input\":1.5")]
    [InlineData("\"output\":\"20\"")]
    [InlineData("\"cacheRead\":-1")]
    [InlineData("\"reasoning\":9007199254740992")]
    public async Task RejectsUnsafePartTokenValues(string fragment)
    {
        var row = "{\"client\":\"claude\",\"provider\":\"anthropic\",\"model\":\"m\"," + fragment
            + ",\"cacheRead\":0,\"cacheWrite\":0,\"reasoning\":0,\"total\":0}";
        var peer = new FakePeer(_ => Json(BreakdownBody(
            Parts(row),
            Coverage("2026-11-01", "col-a1", 0, 0),
            CollectorList(Collector("col-a1", 1, "null")))));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.MalformedResponse, failure.Failure);
    }

    [Fact]
    public async Task RejectsAPartWhoseTotalDiffersFromItsFiveComponents()
    {
        var row = "{\"client\":\"claude\",\"provider\":\"anthropic\",\"model\":\"m\",\"input\":10,\"output\":20,\"cacheRead\":5,\"cacheWrite\":3,\"reasoning\":2,\"total\":41}";
        var peer = new FakePeer(_ => Json(BreakdownBody(
            Parts(row),
            Coverage("2026-11-01", "col-a1", 40, 40),
            CollectorList(Collector("col-a1", 1, "null")))));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.MalformedResponse, failure.Failure);
    }

    [Fact]
    public async Task RejectsPartsThatDoNotSumToAttributedCoverage()
    {
        var peer = new FakePeer(_ => Json(BreakdownBody(
            Parts(Part("claude", "anthropic", "m", 10, 20, 5, 3, 2)),
            Coverage("2026-11-01", "col-a1", 50, 50),
            CollectorList(Collector("col-a1", 1, "null")))));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.InconsistentTotal, failure.Failure);
    }

    [Fact]
    public async Task RejectsPartsThatAreNotInDocumentedOrder()
    {
        var first = Part("claude", "anthropic", "m", 10, 0, 0, 0, 0);
        var second = Part("codex", "openai", "m", 20, 0, 0, 0, 0);
        var peer = new FakePeer(_ => Json(BreakdownBody(
            Parts(first, second),
            Coverage("2026-11-01", "col-a1", 30, 30),
            CollectorList(Collector("col-a1", 1, "null")))));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.MalformedResponse, failure.Failure);
    }

    [Fact]
    public async Task RejectsPartsWhoseTieIsNotInKeyOrder()
    {
        // Same total (10): (codex, openai) must come after (claude, anthropic).
        var first = Part("codex", "openai", "m", 10, 0, 0, 0, 0);
        var second = Part("claude", "anthropic", "m", 10, 0, 0, 0, 0);
        var peer = new FakePeer(_ => Json(BreakdownBody(
            Parts(first, second),
            Coverage("2026-11-01", "col-a1", 20, 20),
            CollectorList(Collector("col-a1", 1, "null")))));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.MalformedResponse, failure.Failure);
    }

    [Fact]
    public async Task RejectsCoverageRowsOutsideTheRequestedRange()
    {
        var peer = new FakePeer(_ => Json(BreakdownBody(
            Parts(),
            Coverage("2026-11-08", "col-a1", 0, 0),
            CollectorList(Collector("col-a1", 1, "null")))));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.RangeMismatch, failure.Failure);
    }

    [Fact]
    public async Task RejectsACoverageRowWhoseCollectorIsMissingFromTheFreshnessList()
    {
        var peer = new FakePeer(_ => Json(BreakdownBody(
            Parts(),
            Coverage("2026-11-01", "col-a2", 0, 0),
            CollectorList(Collector("col-a1", 1, "null")))));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.MalformedResponse, failure.Failure);
    }

    [Fact]
    public async Task RejectsACoverageRowForAnotherCollectorWhenFiltered()
    {
        var peer = new FakePeer(_ => Json(BreakdownBody(
            Parts(),
            Coverage("2026-11-01", "col-a2", 0, 0),
            CollectorList(Collector("col-a1", 1, "null"), Collector("col-a2", 1, "null")))));
        using var harness = Harness.Create(peer, Credential());

        var failure = await Assert.ThrowsAsync<UsageReadException>(() =>
            harness.Port.ReadBreakdownAsync(From, To, "col-a1", CancellationToken.None));

        Assert.Equal(UsageReadFailure.FilterMismatch, failure.Failure);
    }

    [Fact]
    public async Task RejectsADuplicateCoverageRow()
    {
        var peer = new FakePeer(_ => Json(BreakdownBody(
            Parts(),
            Coverage("2026-11-01", "col-a1", 0, 0) + "," + Coverage("2026-11-01", "col-a1", 0, 0),
            CollectorList(Collector("col-a1", 1, "null")))));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.MalformedResponse, failure.Failure);
    }

    [Fact]
    public async Task RejectsMorePartsThanTheDocumentedBound()
    {
        var parts = new List<string>();
        long total = 0;
        for (var index = 0; index < HttpUsageReadPort.MaxParts + 1; index++)
        {
            parts.Add(Part("c" + index.ToString("d4", CultureInfo.InvariantCulture), "p", "m", 1, 0, 0, 0, 0));
            total++;
        }

        var peer = new FakePeer(_ => Json(BreakdownBody(
            Parts([.. parts]),
            string.Empty,
            CollectorList(Collector("col-a1", 1, "null")))));
        _ = total;

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.ResponseTooLarge, failure.Failure);
    }

    [Fact]
    public async Task RejectsMoreCoverageRowsThanTheDocumentedBound()
    {
        var rows = new List<string>();
        for (var index = 0; index < HttpUsageReadPort.MaxRows + 1; index++)
        {
            var day = From.AddDays(index % 7);
            rows.Add(Coverage(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "col-" + index.ToString("d4", CultureInfo.InvariantCulture), 0, 0));
        }

        var peer = new FakePeer(_ => Json(BreakdownBody(
            Parts(),
            string.Join(",", rows),
            CollectorList(Collector("col-a1", 1, "null")))));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.ResponseTooLarge, failure.Failure);
    }

    [Fact]
    public async Task RejectsAnEchoedRangeMismatch()
    {
        var peer = new FakePeer(_ => Json(BreakdownBody(
            Parts(),
            string.Empty,
            CollectorList(Collector("col-a1", 1, "null")),
            "2026-11-02",
            "2026-11-09")));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.RangeMismatch, failure.Failure);
    }

    [Fact]
    public async Task RejectsAnInvalidCollectorFilterLocallyWithoutSending()
    {
        var peer = new FakePeer(_ => Json(BreakdownBody(Parts(), string.Empty, CollectorList(Collector("col-a1", 1, "null")))));
        using var harness = Harness.Create(peer, Credential());

        var failure = await Assert.ThrowsAsync<UsageReadException>(() =>
            harness.Port.ReadBreakdownAsync(From, To, "Not A Collector", CancellationToken.None));

        Assert.Equal(UsageReadFailure.InvalidRequest, failure.Failure);
        Assert.Empty(peer.Requests);
    }

    [Theory]
    [InlineData("not_found", UsageReadFailure.NotFound)]
    [InlineData("collector_not_found", UsageReadFailure.CollectorNotFound)]
    public async Task ClassifiesTheErrorCodeOfA404Response(string code, UsageReadFailure expected)
    {
        var peer = new FakePeer(_ => Error(HttpStatusCode.NotFound, code));
        using var harness = Harness.Create(peer, Credential());

        var failure = await Assert.ThrowsAsync<UsageReadException>(() =>
            harness.Port.ReadBreakdownAsync(From, To, null, CancellationToken.None));

        Assert.Equal(expected, failure.Failure);
        Assert.Equal(404, failure.StatusCode);
    }

    [Fact]
    public async Task FallsBackToNotFoundWhenA404BodyIsNotParseable()
    {
        var peer = new FakePeer(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("<html>no</html>", Encoding.UTF8, "text/html"),
        });
        using var harness = Harness.Create(peer, Credential());

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.NotFound, failure.Failure);
    }

    [Fact]
    public async Task Classifies413AsAResponseTooLargeFailure()
    {
        var peer = new FakePeer(_ => Error((HttpStatusCode)413, "result_too_large"));
        using var harness = Harness.Create(peer, Credential());

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.ResponseTooLarge, failure.Failure);
    }

    [Fact]
    public async Task UnconfiguredPortRefusesBreakdownWithoutASentRequest()
    {
        var port = new UnconfiguredUsageReadPort();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            port.ReadBreakdownAsync(From, To, null, CancellationToken.None));
    }

    [Fact]
    public async Task ReconfiguredAdapterForwardsBreakdownToTheInstalledPort()
    {
        var peer = new FakePeer(_ => Json(BreakdownBody(Parts(), string.Empty, CollectorList(Collector("col-a1", 1, "null")))));
        var client = new HttpClient(peer);
        using var adapter = new ReconfigurableUsageReadPort(client);
        adapter.Configure(new HttpUsageReadPort(client, Origin, Identity, new QueueCredentials(Credential())));

        var breakdown = await adapter.ReadBreakdownAsync(From, To, null, CancellationToken.None);

        Assert.Equal(From, breakdown.From);
        adapter.Reset();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.ReadBreakdownAsync(From, To, null, CancellationToken.None));
        client.Dispose();
    }

    private static async Task<UsageBreakdown> Read(FakePeer peer)
    {
        using var harness = Harness.Create(peer, Credential());
        return await harness.Port.ReadBreakdownAsync(From, To, null, CancellationToken.None);
    }

    private static UsageCredential Credential() => new(Identity, "synthetic-id.access", "synthetic-secret");

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string code)
    {
        var body = "{\"error\":{\"code\":\"" + code + "\",\"message\":\"synthetic\"}}";
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static string BreakdownBody(
        string parts,
        string coverage,
        string collectors,
        string? from = null,
        string? to = null) =>
        "{\"from\":\"" + (from ?? "2026-11-01") + "\",\"to\":\"" + (to ?? "2026-11-07")
        + "\",\"parts\":[" + parts + "],\"coverage\":[" + coverage + "],\"collectors\":[" + collectors + "]}";

    private static string Parts(params string[] rows) => string.Join(",", rows);

    private static string Part(string client, string provider, string model, long input, long output, long cacheRead, long cacheWrite, long reasoning) =>
        "{\"client\":\"" + client + "\",\"provider\":\"" + provider + "\",\"model\":\"" + model
        + "\",\"input\":" + input.ToString(CultureInfo.InvariantCulture)
        + ",\"output\":" + output.ToString(CultureInfo.InvariantCulture)
        + ",\"cacheRead\":" + cacheRead.ToString(CultureInfo.InvariantCulture)
        + ",\"cacheWrite\":" + cacheWrite.ToString(CultureInfo.InvariantCulture)
        + ",\"reasoning\":" + reasoning.ToString(CultureInfo.InvariantCulture)
        + ",\"total\":" + (input + output + cacheRead + cacheWrite + reasoning).ToString(CultureInfo.InvariantCulture) + "}";

    private static string Coverage(string date, string collectorId, long totalTokens, long attributedTokens) =>
        "{\"date\":\"" + date + "\",\"collectorId\":\"" + collectorId
        + "\",\"totalTokens\":" + totalTokens.ToString(CultureInfo.InvariantCulture)
        + ",\"attributedTokens\":" + attributedTokens.ToString(CultureInfo.InvariantCulture) + "}";

    private static string CollectorList(params string[] entries) => string.Join(",", entries);

    private static string Collector(string collectorId, long generation, string lastReceivedAtJson) =>
        "{\"collectorId\":\"" + collectorId + "\",\"generation\":"
        + generation.ToString(CultureInfo.InvariantCulture)
        + ",\"lastReceivedAt\":" + lastReceivedAtJson + "}";

    private sealed record RecordedRequest(
        string Method,
        string Uri,
        string? Authorization,
        string? AccessClientId,
        string? AccessClientSecret,
        string? Cookie);

    /// <summary>Owns the injected client so the fake peer is disposed with it.</summary>
    private sealed class Harness : IDisposable
    {
        private Harness(HttpClient client, HttpUsageReadPort port)
        {
            Client = client;
            Port = port;
        }

        public HttpClient Client { get; }

        public HttpUsageReadPort Port { get; }

        public static Harness Create(FakePeer peer, params UsageCredential?[] credentials)
        {
            var client = new HttpClient(peer);
            return new Harness(client, new HttpUsageReadPort(client, Origin, Identity, new QueueCredentials(credentials)));
        }

        public void Dispose() => Client.Dispose();
    }

    private sealed class FakePeer : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FakePeer(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var authorization = request.Headers.TryGetValues("Authorization", out var values)
                ? string.Join(",", values)
                : null;
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri?.AbsoluteUri ?? string.Empty,
                authorization,
                HeaderOrNull(request, "CF-Access-Client-Id"),
                HeaderOrNull(request, "CF-Access-Client-Secret"),
                HeaderOrNull(request, "Cookie")));

            return Task.FromResult(_respond(request));
        }

        private static string? HeaderOrNull(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
    }

    private sealed class QueueCredentials(params UsageCredential?[] credentials) : IUsageCredentialProvider
    {
        private readonly Queue<UsageCredential?> _pending = new(credentials);

        public ValueTask<UsageCredential?> GetCredentialAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(_pending.Count > 0 ? _pending.Dequeue() : null);
    }
}

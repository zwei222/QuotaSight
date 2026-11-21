using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;

namespace QuotaSight.Collector.Tests;

/// <summary>
/// Send is opt-in. These tests inject a fake handler and a credential source;
/// they never open a socket.
/// </summary>
public sealed class CollectorSendTests
{
    private const string SyntheticId = "synthetic-id";
    private const string SyntheticSecret = "synthetic-secret-value";
    private const string EndpointHost = "send-target.invalid";
    private const string Endpoint = "https://send-target.invalid";
    private const string CollectorId = "col-test";
    private const string Revision = "3";

    [Fact]
    public void PreviewAloneDoesNotSend()
    {
        using var harness = Harness.Create();

        var run = harness.Run(PreviewArgs());

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.StartsWith("{\"schemaVersion\":1,", run.Stdout, StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Captured);
        Assert.Equal(0, harness.Credentials.Reads);
        AssertNoLeak(run);
    }

    [Fact]
    public void SendWithoutPreviewTransmitsTheWireExactlyOnce()
    {
        using var harness = Harness.Create();

        var run = harness.Run(SendArgs());

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.Contains("sent: accepted", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(run.Stdout);
        Assert.Equal(1, harness.Credentials.Reads);
        var captured = Assert.Single(harness.Handler.Captured);
        Assert.Equal(HttpMethod.Put.Method, captured.Method);
        Assert.Equal("https", captured.Scheme);
        Assert.Equal(EndpointHost, captured.Host);
        Assert.Equal($"/v1/collectors/{CollectorId}/snapshots/{Revision}", captured.Path);
        Assert.Equal(string.Empty, captured.Query);
        Assert.Equal(string.Empty, captured.UserInfo);
        Assert.Equal("application/json", captured.ContentType);
        Assert.Null(captured.ContentCharset);
        Assert.Equal(ExpectedWire(), captured.Body);
        Assert.Equal(SyntheticId, Header(captured, "CF-Access-Client-Id"));
        Assert.Equal(SyntheticSecret, Header(captured, "CF-Access-Client-Secret"));
        Assert.False(captured.Headers.ContainsKey("Authorization"));
        Assert.False(captured.Headers.ContainsKey("Cookie"));
        AssertNoLeak(run);
    }

    [Fact]
    public void PreviewTogetherWithSendIsAUsageErrorAndDoesNotSend()
    {
        using var harness = Harness.Create();

        var run = harness.Run(SendArgs().Append("--preview").ToArray());

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.Contains("send_usage: --send and --preview cannot be combined.", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(run.Stdout);
        Assert.Empty(harness.Handler.Captured);
        Assert.Equal(0, harness.Credentials.Reads);
        AssertNoLeak(run);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("collector-id")]
    [InlineData("revision")]
    public void SendRequiresEndpointCollectorIdAndRevision(string missing)
    {
        using var harness = Harness.Create();

        var run = harness.Run(RemoveOption(SendArgs(), "--" + missing));

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.Contains($"send_usage: --send requires --{missing}.", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Captured);
        Assert.Equal(0, harness.Credentials.Reads);
        AssertNoLeak(run);
    }

    [Theory]
    [InlineData("http://send-target.invalid")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://localhost/")]
    [InlineData("http://[::1]/")]
    [InlineData("https://user:synthetic-userinfo-secret@send-target.invalid/")]
    [InlineData("https://send-target.invalid/v1")]
    [InlineData("https://send-target.invalid?x=synthetic-query-secret")]
    [InlineData("https://send-target.invalid/#synthetic-fragment")]
    public void SendRejectsNonHttpsOrDecoratedEndpointsWithoutEchoingThem(string endpoint)
    {
        using var harness = Harness.Create();

        var run = harness.Run(SendArgs(endpoint: endpoint));

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.Contains(
            "send_usage: --endpoint must be https with an empty path and no user info, query or fragment.",
            run.Stderr,
            StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Captured);
        AssertNoLeak(run, "synthetic-userinfo-secret", "synthetic-query-secret", "synthetic-fragment", endpoint);
    }

    [Fact]
    public void SendOnlyFlagsWithoutSendDoNotPreviewOrSend()
    {
        using var harness = Harness.Create();
        var args = PreviewArgs().Append("--endpoint").Append(Endpoint).ToArray();

        var run = harness.Run(args);

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.Contains("send_usage: --endpoint, --collector-id and --revision are only valid with --send.", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(run.Stdout);
        Assert.Empty(harness.Handler.Captured);
        AssertNoLeak(run);
    }

    [Fact]
    public void UpstreamPartialCannotBeSent()
    {
        using var harness = Harness.Create();

        var run = harness.Run(SendArgs().Append("--upstream-partial").ToArray());

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.Contains("send_usage: --send cannot be combined with --upstream-partial.", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Captured);
        AssertNoLeak(run);
    }

    [Fact]
    public void RejectedInputIsNotSent()
    {
        using var harness = Harness.Create();

        var run = harness.Run(SendArgs(), FixtureFiles.Bytes(FixtureFiles.VersionUnknown));

        Assert.Equal(CollectorExitCodes.Rejected, run.ExitCode);
        Assert.Empty(run.Stdout);
        Assert.Empty(harness.Handler.Captured);
        Assert.Equal(0, harness.Credentials.Reads);
        AssertNoLeak(run);
    }

    [Theory]
    [InlineData("authorization", "Bearer synthetic-bearer")]
    [InlineData("Authorization", "Bearer synthetic-bearer")]
    [InlineData("cookie", "synthetic-cookie=1")]
    [InlineData("COOKIE", "synthetic-cookie=1")]
    [InlineData("cf-access-client-id", "synthetic-id")]
    [InlineData("CF-Access-Client-Secret", "synthetic-secret-value")]
    public void InjectedClientWithForbiddenDefaultHeadersIsRefused(string name, string value)
    {
        using var harness = Harness.Create();
        Assert.True(harness.Client.DefaultRequestHeaders.TryAddWithoutValidation(name, value));

        var run = harness.Run(SendArgs());

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.Contains("send_usage: the injected client carries a forbidden default header.", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Captured);
        Assert.Equal(0, harness.Credentials.Reads);
        AssertNoLeak(run, value, "synthetic-bearer", "synthetic-cookie");
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.NoContent)]
    public void SuccessStatusesAreAcceptedWithoutQuotingTheBody(HttpStatusCode status)
    {
        using var harness = Harness.Create();
        harness.Handler.Enqueue(status, "{\"status\":\"replayed\",\"note\":\"synthetic-secret-value\"}");

        var run = harness.Run(SendArgs());

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.Contains("sent: accepted", run.Stderr, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Captured);
        AssertNoLeak(run);
    }

    [Fact]
    public void UnauthorizedIsNotRetried()
    {
        using var harness = Harness.Create();
        harness.Handler.Enqueue(HttpStatusCode.Unauthorized, "synthetic-secret-value");

        var run = harness.Run(SendArgs());

        Assert.Equal(CollectorExitCodes.SendUnauthorized, run.ExitCode);
        Assert.Contains("send_unauthorized: authentication failed", run.Stderr, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Captured);
        Assert.Empty(harness.Delays);
        AssertNoLeak(run);
    }

    [Fact]
    public void ForbiddenIsNotRetried()
    {
        using var harness = Harness.Create();
        harness.Handler.Enqueue(HttpStatusCode.Forbidden, "synthetic-secret-value");

        var run = harness.Run(SendArgs());

        Assert.Equal(CollectorExitCodes.SendForbidden, run.ExitCode);
        Assert.Contains("send_forbidden: authorization failed", run.Stderr, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Captured);
        Assert.Empty(harness.Delays);
        AssertNoLeak(run);
    }

    [Theory]
    [InlineData(HttpStatusCode.Moved)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.RedirectMethod)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public void RedirectsAreNotFollowed(HttpStatusCode status)
    {
        using var harness = Harness.Create();
        harness.Handler.Enqueue(
            status,
            "synthetic-secret-value",
            ("Location", "https://send-target.invalid/steal?synthetic-location-secret=1"));

        var run = harness.Run(SendArgs());

        Assert.Equal(CollectorExitCodes.SendRedirect, run.ExitCode);
        Assert.Contains("send_redirect: redirect not followed", run.Stderr, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Captured);
        Assert.Empty(harness.Delays);
        AssertNoLeak(run, "synthetic-location-secret");
    }

    [Fact]
    public void ConflictIsNotRetried()
    {
        using var harness = Harness.Create();
        harness.Handler.Enqueue(HttpStatusCode.Conflict, "{\"error\":{\"code\":\"revision_conflict\"}}");

        var run = harness.Run(SendArgs());

        Assert.Equal(CollectorExitCodes.SendConflict, run.ExitCode);
        Assert.Contains("send_conflict: revision rejected", run.Stderr, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Captured);
        AssertNoLeak(run, "revision_conflict");
    }

    [Fact]
    public void PreconditionFailureIsNotRetried()
    {
        using var harness = Harness.Create();
        harness.Handler.Enqueue((HttpStatusCode)412, "synthetic-secret-value");

        var run = harness.Run(SendArgs());

        Assert.Equal(CollectorExitCodes.SendPrecondition, run.ExitCode);
        Assert.Contains("send_precondition: precondition failed", run.Stderr, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Captured);
        AssertNoLeak(run);
    }

    [Fact]
    public void UnprocessableStatusIsNotRetried()
    {
        using var harness = Harness.Create();
        harness.Handler.Enqueue((HttpStatusCode)422, "synthetic-secret-value");

        var run = harness.Run(SendArgs());

        Assert.Equal(CollectorExitCodes.SendUnprocessable, run.ExitCode);
        Assert.Contains("send_unprocessable: snapshot not processable", run.Stderr, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Captured);
        AssertNoLeak(run);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    public void OtherClientErrorsAreNotRetried(HttpStatusCode status)
    {
        using var harness = Harness.Create();
        harness.Handler.Enqueue(status, "synthetic-secret-value");

        var run = harness.Run(SendArgs());

        Assert.Equal(CollectorExitCodes.SendRejected, run.ExitCode);
        Assert.Contains("send_rejected: request rejected", run.Stderr, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Captured);
        Assert.Empty(harness.Delays);
        AssertNoLeak(run);
    }

    [Fact]
    public void UnavailableIsRetriedWithTheSameRevisionAndBodyThenStops()
    {
        using var harness = Harness.Create();
        harness.Handler.Enqueue(HttpStatusCode.ServiceUnavailable, "auth_unavailable synthetic-secret-value");
        harness.Handler.Enqueue(HttpStatusCode.ServiceUnavailable, "auth_unavailable synthetic-secret-value");
        harness.Handler.Enqueue(HttpStatusCode.ServiceUnavailable, "auth_unavailable synthetic-secret-value");
        harness.Handler.Enqueue(HttpStatusCode.OK, "should-not-be-called");

        var run = harness.Run(SendArgs());

        Assert.Equal(CollectorExitCodes.SendUnavailable, run.ExitCode);
        Assert.Contains("send_unavailable: service unavailable", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(CollectorSnapshotTransport.MaxAttempts, harness.Handler.Captured.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, harness.Delays);
        AssertSameWire(harness);
        Assert.Equal(1, harness.Credentials.Reads);
        AssertNoLeak(run, "auth_unavailable");
    }

    [Fact]
    public void UnavailableThenSuccessStopsEarly()
    {
        using var harness = Harness.Create();
        harness.Handler.Enqueue(HttpStatusCode.BadGateway, "synthetic-secret-value");
        harness.Handler.Enqueue(HttpStatusCode.OK, "{\"status\":\"replayed\"}");

        var run = harness.Run(SendArgs());

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.Equal(2, harness.Handler.Captured.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, harness.Delays);
        AssertSameWire(harness);
        AssertNoLeak(run);
    }

    [Fact]
    public void UnavailableThenUnauthorizedDoesNotContinue()
    {
        using var harness = Harness.Create();
        harness.Handler.Enqueue(HttpStatusCode.ServiceUnavailable, "synthetic-secret-value");
        harness.Handler.Enqueue(HttpStatusCode.Unauthorized, "synthetic-secret-value");

        var run = harness.Run(SendArgs());

        Assert.Equal(CollectorExitCodes.SendUnauthorized, run.ExitCode);
        Assert.Equal(2, harness.Handler.Captured.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, harness.Delays);
        AssertNoLeak(run);
    }

    [Fact]
    public void TimeoutsAreRetriedUpToTheAttemptCap()
    {
        using var harness = Harness.Create();
        harness.Handler.EnqueueException(() => new TaskCanceledException("timed out"));

        var run = harness.Run(SendArgs());

        Assert.Equal(CollectorExitCodes.SendTimeout, run.ExitCode);
        Assert.Contains("send_timeout: deadline exceeded", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(CollectorSnapshotTransport.MaxAttempts, harness.Handler.Captured.Count);
        Assert.Equal(2, harness.Delays.Count);
        Assert.DoesNotContain("timed out", run.Stderr, StringComparison.Ordinal);
        AssertNoLeak(run);
    }

    [Fact]
    public void TransportFailuresAreRetriedWithoutQuotingTheException()
    {
        using var harness = Harness.Create();
        harness.Handler.EnqueueException(() => new HttpRequestException("failed " + Endpoint));

        var run = harness.Run(SendArgs());

        Assert.Equal(CollectorExitCodes.SendTransport, run.ExitCode);
        Assert.Contains("send_transport: transport failed", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(CollectorSnapshotTransport.MaxAttempts, harness.Handler.Captured.Count);
        AssertNoLeak(run);
    }

    [Fact]
    public void OversizedSuccessBodyIsRejectedWithoutRetryOrQuoting()
    {
        using var harness = Harness.Create();
        var marker = "synthetic-oversized-body";
        var body = Encoding.UTF8.GetBytes(marker);
        harness.Handler.Enqueue(HttpStatusCode.OK, body, contentLength: CollectorSnapshotTransport.MaxResponseBytes + 1);

        var run = harness.Run(SendArgs());

        Assert.Equal(CollectorExitCodes.SendResponseTooLarge, run.ExitCode);
        Assert.Contains("send_response_too_large: response too large", run.Stderr, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Captured);
        AssertNoLeak(run, marker);
    }

    [Fact]
    public void CredentialFailureDoesNotSend()
    {
        using var harness = Harness.Create(CollectorCredentialResult.Failed(CollectorCredentialFailure.DirectoryUnset));

        var run = harness.Run(SendArgs());

        Assert.Equal(CollectorExitCodes.CredentialUnavailable, run.ExitCode);
        Assert.Contains("credential_unavailable: DirectoryUnset", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Captured);
        AssertNoLeak(run);
    }

    [Fact]
    public void TransportFactoryDisablesRedirectsAndCookiesAndKeepsProxyDefault()
    {
        using var handler = CollectorSnapshotTransport.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.True(handler.UseProxy);
        Assert.Equal(TimeSpan.FromSeconds(30), CollectorSnapshotTransport.RequestTimeout);
        Assert.Equal(3, CollectorSnapshotTransport.MaxAttempts);
    }

    [Fact]
    public void BackoffGrowsExponentiallyAndClampsJitter()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), CollectorSnapshotTransport.Backoff(1, 0));
        Assert.Equal(TimeSpan.FromSeconds(2), CollectorSnapshotTransport.Backoff(2, 0));
        Assert.Equal(TimeSpan.FromMilliseconds(1250), CollectorSnapshotTransport.Backoff(1, 1));
        Assert.Equal(TimeSpan.FromSeconds(1), CollectorSnapshotTransport.Backoff(1, -4));
        Assert.Equal(TimeSpan.FromMilliseconds(1250), CollectorSnapshotTransport.Backoff(1, 9));
    }

    [Fact]
    public void CredentialTextOmitsTheToken()
    {
        var credential = new CollectorAccessCredential(SyntheticId, SyntheticSecret);

        Assert.DoesNotContain(SyntheticId, credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticSecret, credential.ToString(), StringComparison.Ordinal);
        var display = credential.GetType().GetCustomAttribute<DebuggerDisplayAttribute>();
        Assert.NotNull(display);
        Assert.DoesNotContain("ClientId", display!.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientSecret", display.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticSecret, display.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialConstructorDoesNotEchoRejectedValues()
    {
        const string bad = "synthetic-secret-value with space";

        var exception = Assert.Throws<ArgumentException>(() => new CollectorAccessCredential(SyntheticId, bad));

        Assert.DoesNotContain(bad, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticId, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticSecret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadmeDocumentsExplicitSendAndPlaceholderCredentials()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "src", "QuotaSight.Collector", "README.md"));

        Assert.Contains("--send", readme, StringComparison.Ordinal);
        Assert.Contains("--preview", readme, StringComparison.Ordinal);
        Assert.Contains("LoadCredential=access-client-id:/etc/example/", readme, StringComparison.Ordinal);
        Assert.Contains("LoadCredential=access-client-secret:/etc/example/", readme, StringComparison.Ordinal);
        Assert.Contains("Before sending real data", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("cloudflareaccess.com", readme, StringComparison.Ordinal);
    }

    private static void AssertSameWire(Harness harness)
    {
        var expected = ExpectedWire();
        Assert.NotEmpty(harness.Handler.Captured);
        foreach (var captured in harness.Handler.Captured)
        {
            Assert.Equal($"/v1/collectors/{CollectorId}/snapshots/{Revision}", captured.Path);
            Assert.Equal(expected, captured.Body);
            Assert.Equal(SyntheticId, Header(captured, "CF-Access-Client-Id"));
            Assert.Equal(SyntheticSecret, Header(captured, "CF-Access-Client-Secret"));
        }
    }

    private static byte[] ExpectedWire()
    {
        var candidate = TokscaleGraphNormalizer.Normalize(FixtureFiles.Bytes(FixtureFiles.Positive)).Candidate;
        Assert.NotNull(candidate);
        return SnapshotWire.Serialize(candidate!, generation: 7);
    }

    private static string Header(CapturedRequest captured, string name)
    {
        Assert.True(captured.Headers.TryGetValue(name, out var values));
        return Assert.Single(values!);
    }

    private static string[] PreviewArgs() =>
    [
        "--preview",
        "--generation",
        "7",
        "--bucket-time-zone",
        "Asia/Tokyo",
    ];

    private static string[] SendArgs(string? endpoint = null) =>
    [
        "--send",
        "--endpoint",
        endpoint ?? Endpoint,
        "--collector-id",
        CollectorId,
        "--revision",
        Revision,
        "--generation",
        "7",
        "--bucket-time-zone",
        "Asia/Tokyo",
    ];

    private static string[] RemoveOption(string[] args, string option)
    {
        var kept = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], option, StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            kept.Add(args[index]);
        }

        return kept.ToArray();
    }

    private static void AssertNoLeak(CliResult run, params string[] extra)
    {
        var text = run.Stdout + "\n" + run.Stderr;
        Assert.DoesNotContain(SyntheticId, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Endpoint, text, StringComparison.Ordinal);
        Assert.DoesNotContain(EndpointHost, text, StringComparison.Ordinal);
        foreach (var item in extra)
        {
            if (item.Length == 0)
            {
                continue;
            }

            Assert.DoesNotContain(item, text, StringComparison.Ordinal);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QuotaSight.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("repository root was not found");
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);

    private sealed class Harness : IDisposable
    {
        private Harness(ScriptedHandler handler, HttpClient client, CountingCredentials credentials, List<TimeSpan> delays)
        {
            Handler = handler;
            Client = client;
            Credentials = credentials;
            Delays = delays;
        }

        public ScriptedHandler Handler { get; }

        public HttpClient Client { get; }

        public CountingCredentials Credentials { get; }

        public List<TimeSpan> Delays { get; }

        public static Harness Create(CollectorCredentialResult? result = null)
        {
            var handler = new ScriptedHandler();
            var client = new HttpClient(handler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            var credentials = new CountingCredentials(result ?? CollectorCredentialResult.Success(
                new CollectorAccessCredential(SyntheticId, SyntheticSecret)));
            return new Harness(handler, client, credentials, []);
        }

        public CliResult Run(string[] args, byte[]? stdin = null)
        {
            stdin ??= FixtureFiles.Bytes(FixtureFiles.Positive);
            using var input = new MemoryStream(stdin, writable: false);
            using var output = new MemoryStream();
            using var stderr = new StringWriter();
            var context = new CollectorSendContext
            {
                HttpClient = Client,
                Credentials = Credentials,
                Delay = Delays.Add,
                NextJitterUnit = static () => 0d,
            };
            var exitCode = CollectorCli.Run(args, input, output, stderr, context);
            return new CliResult(exitCode, Encoding.UTF8.GetString(output.ToArray()), stderr.ToString());
        }

        public void Dispose()
        {
            Client.Dispose();
            Handler.Dispose();
        }
    }

    private sealed class CountingCredentials : ICollectorCredentialSource
    {
        private readonly CollectorCredentialResult _result;

        public CountingCredentials(CollectorCredentialResult result) => _result = result;

        public int Reads { get; private set; }

        public CollectorCredentialResult Read()
        {
            Reads++;
            return _result;
        }

        public override string ToString() => nameof(CountingCredentials);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly List<Func<CancellationToken, HttpResponseMessage>> _steps = [];

        public List<CapturedRequest> Captured { get; } = [];

        public void Enqueue(HttpStatusCode status, string body, params (string Name, string Value)[] headers) =>
            Enqueue(status, Encoding.UTF8.GetBytes(body), headers: headers);

        public void Enqueue(
            HttpStatusCode status,
            byte[] body,
            long? contentLength = null,
            params (string Name, string Value)[] headers)
        {
            _steps.Add(_ =>
            {
                var content = new ByteArrayContent(body);
                if (contentLength is not null)
                {
                    content.Headers.ContentLength = contentLength;
                }

                var response = new HttpResponseMessage(status) { Content = content };
                foreach (var (name, value) in headers)
                {
                    response.Headers.TryAddWithoutValidation(name, value);
                }

                return response;
            });
        }

        public void EnqueueException(Func<Exception> factory)
        {
            for (var index = 0; index < CollectorSnapshotTransport.MaxAttempts; index++)
            {
                _steps.Add(_ => throw factory());
            }
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Dispatch(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Dispatch(request));

        private HttpResponseMessage Dispatch(HttpRequestMessage request)
        {
            Captured.Add(CapturedRequest.From(request));
            if (Captured.Count <= _steps.Count)
            {
                return _steps[Captured.Count - 1](CancellationToken.None);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("{}"u8.ToArray()),
            };
        }
    }

    private sealed record CapturedRequest(
        string Method,
        string Scheme,
        string Host,
        string Path,
        string Query,
        string UserInfo,
        string? ContentType,
        string? ContentCharset,
        byte[] Body,
        IReadOnlyDictionary<string, string[]> Headers)
    {
        public static CapturedRequest From(HttpRequestMessage request)
        {
            var body = Array.Empty<byte>();
            if (request.Content is not null)
            {
                using var buffer = new MemoryStream();
                request.Content.CopyTo(buffer, context: null, cancellationToken: CancellationToken.None);
                body = buffer.ToArray();
            }

            var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
            {
                headers[header.Key] = header.Value.ToArray();
            }

            var uri = request.RequestUri;
            return new CapturedRequest(
                request.Method.Method,
                uri?.Scheme ?? string.Empty,
                uri?.IdnHost ?? string.Empty,
                uri?.AbsolutePath ?? string.Empty,
                uri?.Query ?? string.Empty,
                uri?.UserInfo ?? string.Empty,
                request.Content?.Headers.ContentType?.MediaType,
                request.Content?.Headers.ContentType?.CharSet,
                body,
                headers);
        }
    }
}

using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;

namespace QuotaSight.Collector.Tests;

/// <summary>
/// Send is opt-in. These tests inject a fake handler and a credential source;
/// they never open a socket and never reach a real state directory.
///
/// Three expectations changed with the revision allocation:
/// <list type="bullet">
/// <item>the revision is no longer an argument (<c>--revision</c> is a usage
/// error), so each send starts from a private temporary state file whose floor
/// is 2 and therefore allocates revision 3, as before;</item>
/// <item>a 409 now reports the reason word read from the body instead of a
/// generic message;</item>
/// <item>a 2xx is only a success when its body is the documented receipt, so a
/// scripted success carries one.</item>
/// </list>
/// </summary>
public sealed class CollectorSendTests
{
    private const string SyntheticId = SendHarness.SyntheticId;
    private const string SyntheticSecret = SendHarness.SyntheticSecret;
    private const string EndpointHost = SendHarness.EndpointHost;
    private const string Endpoint = SendHarness.Endpoint;
    private const string CollectorId = SendHarness.CollectorId;
    private const string Revision = SendHarness.Revision;

    [LinuxFact]
    public void PreviewAloneDoesNotSend()
    {
        using var harness = SendHarness.Create();

        var run = harness.Run(SendHarness.PreviewArgs());

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.StartsWith("{\"schemaVersion\":1,", run.Stdout, StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Captured);
        Assert.Equal(0, harness.Credentials.Reads);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void SendWithoutPreviewTransmitsTheWireExactlyOnce()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);

        var run = harness.RunSend();

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
        Assert.Equal(SendHarness.ExpectedWire(), captured.Body);
        Assert.Equal(SyntheticId, SendHarness.Header(captured, "CF-Access-Client-Id"));
        Assert.Equal(SyntheticSecret, SendHarness.Header(captured, "CF-Access-Client-Secret"));
        Assert.False(captured.Headers.ContainsKey("Authorization"));
        Assert.False(captured.Headers.ContainsKey("Cookie"));
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void PreviewTogetherWithSendIsAUsageErrorAndDoesNotSend()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);

        var run = harness.Run([.. SendHarness.SendArgs(), "--preview"]);

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.Contains("send_usage: --send and --preview cannot be combined.", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(run.Stdout);
        Assert.Empty(harness.Handler.Captured);
        Assert.Equal(0, harness.Credentials.Reads);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxTheory]
    [InlineData("endpoint")]
    [InlineData("collector-id")]
    public void SendRequiresEndpointAndCollectorId(string missing)
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);

        var run = harness.Run(SendHarness.RemoveOption(SendHarness.SendArgs(), "--" + missing));

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.Contains($"send_usage: --send requires --{missing}.", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Captured);
        Assert.Equal(0, harness.Credentials.Reads);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxTheory]
    [InlineData("http://send-target.invalid")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://localhost/")]
    [InlineData("http://[::1]/")]
    [InlineData("https://user:***@send-target.invalid/")]
    [InlineData("https://send-target.invalid/v1")]
    [InlineData("https://send-target.invalid?x=synthetic-query-secret")]
    [InlineData("https://send-target.invalid/#synthetic-fragment")]
    public void SendRejectsNonHttpsOrDecoratedEndpointsWithoutEchoingThem(string endpoint)
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);

        var run = harness.Run(SendHarness.SendArgs(endpoint));

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.Contains(
            "send_usage: --endpoint must be https with an empty path and no user info, query or fragment.",
            run.Stderr,
            StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Captured);
        SendHarness.AssertNoLeak(run, "synthetic-userinfo-secret", "synthetic-query-secret", "synthetic-fragment", endpoint);
    }

    [LinuxFact]
    public void SendOnlyFlagsWithoutSendDoNotPreviewOrSend()
    {
        using var harness = SendHarness.Create();
        var args = SendHarness.PreviewArgs().Append("--endpoint").Append(Endpoint).ToArray();

        var run = harness.Run(args);

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.Contains(
            "send_usage: --endpoint and --collector-id are only valid with --send.",
            run.Stderr,
            StringComparison.Ordinal);
        Assert.Empty(run.Stdout);
        Assert.Empty(harness.Handler.Captured);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void UpstreamPartialCannotBeSent()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);

        var run = harness.Run([.. SendHarness.SendArgs(), "--upstream-partial"]);

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.Contains("send_usage: --send cannot be combined with --upstream-partial.", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Captured);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void RejectedInputIsNotSent()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);

        var run = harness.RunSend(FixtureFiles.Bytes(FixtureFiles.VersionUnknown));

        Assert.Equal(CollectorExitCodes.Rejected, run.ExitCode);
        Assert.Empty(run.Stdout);
        Assert.Empty(harness.Handler.Captured);
        Assert.Equal(0, harness.Credentials.Reads);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxTheory]
    [InlineData("authorization", "Bearer synthetic-bearer")]
    [InlineData("Authorization", "Bearer synthetic-bearer")]
    [InlineData("cookie", "synthetic-cookie=1")]
    [InlineData("COOKIE", "synthetic-cookie=1")]
    [InlineData("cf-access-client-id", "synthetic-id")]
    [InlineData("CF-Access-Client-Secret", "synthetic-secret-value")]
    public void InjectedClientWithForbiddenDefaultHeadersIsRefused(string name, string value)
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        Assert.True(harness.Client.DefaultRequestHeaders.TryAddWithoutValidation(name, value));

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.Usage, run.ExitCode);
        Assert.Contains("send_usage: the injected client carries a forbidden default header.", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Captured);
        Assert.Equal(0, harness.Credentials.Reads);
        SendHarness.AssertNoLeak(run, value, "synthetic-bearer", "synthetic-cookie");
    }

    [LinuxFact]
    public void AReplayedReceiptIsAcceptedWithoutQuotingTheBody()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(HttpStatusCode.OK, SendHarness.Receipt(3, "replayed", changedDays: "[]"));

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.Contains("sent: accepted", run.Stderr, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Captured);
        SendHarness.AssertNoLeak(run, "\"replayed\"");
    }

    [LinuxFact]
    public void UnauthorizedIsNotRetried()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(HttpStatusCode.Unauthorized, "synthetic-secret-value");

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendUnauthorized, run.ExitCode);
        Assert.Contains("send_unauthorized: authentication failed", run.Stderr, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Captured);
        Assert.Empty(harness.Delays);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void ForbiddenIsNotRetried()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(HttpStatusCode.Forbidden, "synthetic-secret-value");

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendForbidden, run.ExitCode);
        Assert.Contains("send_forbidden: authorization failed", run.Stderr, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Captured);
        Assert.Empty(harness.Delays);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxTheory]
    [InlineData(HttpStatusCode.Moved)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.RedirectMethod)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public void RedirectsAreNotFollowed(HttpStatusCode status)
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(
            status,
            "synthetic-secret-value",
            ("Location", "https://send-target.invalid/steal?synthetic-location-secret=1"));

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendRedirect, run.ExitCode);
        Assert.Contains("send_redirect: redirect not followed", run.Stderr, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Captured);
        Assert.Empty(harness.Delays);
        SendHarness.AssertNoLeak(run, "synthetic-location-secret");
    }

    [LinuxFact]
    public void ConflictIsNotRetriedAndOnlyTheReasonWordIsReported()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(
            HttpStatusCode.Conflict,
            "{\"error\":{\"code\":\"revision_conflict\",\"message\":\"synthetic-secret-value\"}}");

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendConflict, run.ExitCode);
        Assert.Contains("send_conflict: revision_conflict", run.Stderr, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Captured);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void PreconditionFailureIsNotRetried()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue((HttpStatusCode)412, "synthetic-secret-value");

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendPrecondition, run.ExitCode);
        Assert.Contains("send_precondition: precondition failed", run.Stderr, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Captured);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void UnprocessableStatusIsNotRetried()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue((HttpStatusCode)422, "synthetic-secret-value");

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendUnprocessable, run.ExitCode);
        Assert.Contains("send_unprocessable: snapshot not processable", run.Stderr, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Captured);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxTheory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    public void OtherClientErrorsAreNotRetried(HttpStatusCode status)
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(status, "synthetic-secret-value");

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendRejected, run.ExitCode);
        Assert.Contains("send_rejected: request rejected", run.Stderr, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Captured);
        Assert.Empty(harness.Delays);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void UnavailableIsRetriedWithTheSameRevisionAndBodyThenStops()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(HttpStatusCode.ServiceUnavailable, "auth_unavailable synthetic-secret-value");
        harness.Handler.Enqueue(HttpStatusCode.ServiceUnavailable, "auth_unavailable synthetic-secret-value");
        harness.Handler.Enqueue(HttpStatusCode.ServiceUnavailable, "auth_unavailable synthetic-secret-value");
        harness.Handler.Enqueue(HttpStatusCode.OK, SendHarness.Receipt(3));

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendUnavailable, run.ExitCode);
        Assert.Contains("send_unavailable: service unavailable", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(CollectorSnapshotTransport.MaxAttempts, harness.Handler.Captured.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, harness.Delays);
        AssertSameWire(harness);
        Assert.Equal(1, harness.Credentials.Reads);
        SendHarness.AssertNoLeak(run, "auth_unavailable");
    }

    [LinuxFact]
    public void UnavailableThenSuccessStopsEarly()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(HttpStatusCode.BadGateway, "synthetic-secret-value");
        harness.Handler.Enqueue(HttpStatusCode.OK, SendHarness.Receipt(3, "replayed", changedDays: "[]"));

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
        Assert.Equal(2, harness.Handler.Captured.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, harness.Delays);
        AssertSameWire(harness);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void UnavailableThenUnauthorizedDoesNotContinue()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.Enqueue(HttpStatusCode.ServiceUnavailable, "synthetic-secret-value");
        harness.Handler.Enqueue(HttpStatusCode.Unauthorized, "synthetic-secret-value");

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendUnauthorized, run.ExitCode);
        Assert.Equal(2, harness.Handler.Captured.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, harness.Delays);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void TimeoutsAreRetriedUpToTheAttemptCap()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.EnqueueException(() => new TaskCanceledException("timed out"));

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendTimeout, run.ExitCode);
        Assert.Contains("send_timeout: deadline exceeded", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(CollectorSnapshotTransport.MaxAttempts, harness.Handler.Captured.Count);
        Assert.Equal(2, harness.Delays.Count);
        Assert.DoesNotContain("timed out", run.Stderr, StringComparison.Ordinal);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void TransportFailuresAreRetriedWithoutQuotingTheException()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        harness.Handler.EnqueueException(() => new HttpRequestException("failed " + Endpoint));

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendTransport, run.ExitCode);
        Assert.Contains("send_transport: transport failed", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(CollectorSnapshotTransport.MaxAttempts, harness.Handler.Captured.Count);
        SendHarness.AssertNoLeak(run);
    }

    [LinuxFact]
    public void OversizedSuccessBodyIsRejectedWithoutRetryOrQuoting()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(2);
        var marker = "synthetic-oversized-body";
        var body = Encoding.UTF8.GetBytes(marker);
        harness.Handler.Enqueue(HttpStatusCode.OK, body, contentLength: CollectorSnapshotTransport.MaxResponseBytes + 1);

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.SendResponseTooLarge, run.ExitCode);
        Assert.Contains("send_response_too_large: response too large", run.Stderr, StringComparison.Ordinal);
        Assert.Single(harness.Handler.Captured);
        SendHarness.AssertNoLeak(run, marker);
    }

    [LinuxFact]
    public void CredentialFailureDoesNotSend()
    {
        // The documented order checks the state before the credential, so this
        // case needs a usable state file to reach the credential read at all.
        using var harness = SendHarness.Create(CollectorCredentialResult.Failed(CollectorCredentialFailure.DirectoryUnset));
        harness.WriteState(2);

        var run = harness.RunSend();

        Assert.Equal(CollectorExitCodes.CredentialUnavailable, run.ExitCode);
        Assert.Contains("credential_unavailable: DirectoryUnset", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Captured);
        SendHarness.AssertNoLeak(run);
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
        Assert.Contains("--state-init", readme, StringComparison.Ordinal);
        Assert.Contains("--state-advance", readme, StringComparison.Ordinal);
        Assert.Contains("StateDirectory=example-collector", readme, StringComparison.Ordinal);
        Assert.Contains("StateDirectoryMode=0700", readme, StringComparison.Ordinal);
        Assert.Contains("LoadCredential=access-client-id:/etc/example/", readme, StringComparison.Ordinal);
        Assert.Contains("LoadCredential=access-client-secret:/etc/example/", readme, StringComparison.Ordinal);
        Assert.Contains("Before sending real data", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("--revision <", readme, StringComparison.Ordinal);
        Assert.Contains("--revision` no longer exists", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("cloudflareaccess.com", readme, StringComparison.Ordinal);
    }

    private static void AssertSameWire(SendHarness harness)
    {
        var expected = SendHarness.ExpectedWire();
        Assert.NotEmpty(harness.Handler.Captured);
        foreach (var captured in harness.Handler.Captured)
        {
            Assert.Equal($"/v1/collectors/{CollectorId}/snapshots/{Revision}", captured.Path);
            Assert.Equal(expected, captured.Body);
            Assert.Equal(SyntheticId, SendHarness.Header(captured, "CF-Access-Client-Id"));
            Assert.Equal(SyntheticSecret, SendHarness.Header(captured, "CF-Access-Client-Secret"));
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
}

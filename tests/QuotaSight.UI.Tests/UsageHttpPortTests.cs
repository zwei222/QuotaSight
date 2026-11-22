using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// Contract tests for the explicit HTTPS daily read adapter. Every test drives a
/// fake peer (an <see cref="HttpMessageHandler"/>), so both the request that
/// would hit the network and the response that would be parsed are observed
/// exactly.
/// </summary>
public sealed class UsageHttpPortTests
{
    private static readonly Uri Origin = new("https://usage.test");
    private const string Identity = "owner-a";

    [Fact]
    public async Task ReadSendsOneFixedOriginGetWithAccessHeadersAndReturnsVerifiedRows()
    {
        var peer = new FakePeer(_ => Json(Body(
            "2026-11-01",
            "2026-11-02",
            1600,
            Row("2026-11-01", "col-a1", 400),
            Row("2026-11-02", "col-a1", 0),
            Row("2026-11-02", "col-a2", 1200))));
        using var harness = Harness.Create(peer, Credential());

        var usage = await harness.Port.ReadDailyAsync(
            new DateOnly(2026, 11, 1),
            new DateOnly(2026, 11, 2),
            null,
            CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 11, 1), usage.From);
        Assert.Equal(new DateOnly(2026, 11, 2), usage.To);
        Assert.Equal(1600L, usage.TotalTokens);
        Assert.Collection(
            usage.Days,
            day => Assert.Equal(new UsageDay(new DateOnly(2026, 11, 1), "col-a1", 400), day),
            // A measured zero is data and is preserved, never read as "no row".
            day => Assert.Equal(new UsageDay(new DateOnly(2026, 11, 2), "col-a1", 0), day),
            day => Assert.Equal(new UsageDay(new DateOnly(2026, 11, 2), "col-a2", 1200), day));

        var request = Assert.Single(peer.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(
            "https://usage.test/v1/usage/daily?from=2026-11-01&to=2026-11-02",
            request.Uri);
        Assert.Equal("synthetic-id.access", request.AccessClientId);
        Assert.Equal("synthetic-secret", request.AccessClientSecret);
        Assert.Null(request.Authorization);
        Assert.Null(request.Cookie);
        Assert.Equal(new Uri("https://usage.test/v1/usage/daily"), harness.Port.Endpoint);
    }

    [Fact]
    public async Task ReadAttachesAccessServiceTokenHeadersOnceAndOmitsAuthorizationAndCookie()
    {
        const string clientId = "synthetic-id.access";
        const string clientSecret = "synthetic-secret";
        string? seenClientId = null;
        string? seenClientSecret = null;
        var clientIdCount = 0;
        var clientSecretCount = 0;
        var authorizationCount = 0;
        var cookieCount = 0;
        var peer = new FakePeer(request =>
        {
            clientIdCount = HeaderValueCount(request, "CF-Access-Client-Id");
            clientSecretCount = HeaderValueCount(request, "CF-Access-Client-Secret");
            authorizationCount = HeaderValueCount(request, "Authorization");
            cookieCount = HeaderValueCount(request, "Cookie");
            seenClientId = SingleHeaderOrNull(request, "CF-Access-Client-Id");
            seenClientSecret = SingleHeaderOrNull(request, "CF-Access-Client-Secret");
            return Json(Body("2026-11-01", "2026-11-01", 0));
        });
        using var harness = Harness.Create(
            peer,
            new UsageCredential(Identity, clientId, clientSecret));

        var usage = await harness.Port.ReadDailyAsync(
            new DateOnly(2026, 11, 1),
            new DateOnly(2026, 11, 1),
            null,
            CancellationToken.None);

        Assert.Equal(0L, usage.TotalTokens);
        Assert.Equal(1, clientIdCount);
        Assert.Equal(1, clientSecretCount);
        Assert.Equal(0, authorizationCount);
        Assert.Equal(0, cookieCount);
        Assert.Equal(clientId, seenClientId);
        Assert.Equal(clientSecret, seenClientSecret);
        Assert.False(harness.Client.DefaultRequestHeaders.Contains("CF-Access-Client-Id"));
        Assert.False(harness.Client.DefaultRequestHeaders.Contains("CF-Access-Client-Secret"));
        Assert.False(harness.Client.DefaultRequestHeaders.Contains("Authorization"));
        Assert.False(harness.Client.DefaultRequestHeaders.Contains("Cookie"));
        Assert.Null(harness.Client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData("", "synthetic-secret", "clientId")]
    [InlineData("synthetic-id.access", "", "clientSecret")]
    [InlineData("synthetic-id.access\n", "synthetic-secret", "clientId")]
    [InlineData("synthetic-id.access", "synthetic-secret\r\n", "clientSecret")]
    [InlineData("synthetic-id\u0001.access", "synthetic-secret", "clientId")]
    [InlineData("synthetic-id.access", "synthetic\u0007secret", "clientSecret")]
    [InlineData("synthetic-id\u007f.access", "synthetic-secret", "clientId")]
    [InlineData("synthetic-idé.access", "synthetic-secret", "clientId")]
    [InlineData("synthetic-id.access", "synthetic-sécret", "clientSecret")]
    [InlineData("synthetic id.access", "synthetic-secret", "clientId")]
    [InlineData("synthetic-id.access", "synthetic secret", "clientSecret")]
    [InlineData("\tsynthetic-id.access", "synthetic-secret", "clientId")]
    public void RejectsUnsafeAccessServiceTokenMaterial(string clientId, string clientSecret, string parameter)
    {
        var failure = Assert.Throws<ArgumentException>(() => new UsageCredential(Identity, clientId, clientSecret));

        Assert.Equal(parameter, failure.ParamName);
        AssertSecretIsAbsent(failure, clientId);
        AssertSecretIsAbsent(failure, clientSecret);
    }

    [Fact]
    public void RejectsNullAccessServiceTokenMaterialWithoutEchoingIt()
    {
        var missingId = Assert.Throws<ArgumentException>(() => new UsageCredential(Identity, null!, "synthetic-secret"));
        var missingSecret = Assert.Throws<ArgumentException>(() => new UsageCredential(Identity, "synthetic-id.access", null!));

        Assert.Equal("clientId", missingId.ParamName);
        Assert.Equal("clientSecret", missingSecret.ParamName);
        Assert.DoesNotContain("synthetic-secret", missingId.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-id.access", missingSecret.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsOverlongAccessServiceTokenMaterialWithoutEchoingIt()
    {
        var clientId = new string('a', 257);
        var clientSecret = new string('b', 513);

        var idFailure = Assert.Throws<ArgumentException>(() => new UsageCredential(Identity, clientId, "synthetic-secret"));
        var secretFailure = Assert.Throws<ArgumentException>(() => new UsageCredential(Identity, "synthetic-id.access", clientSecret));

        Assert.Equal("clientId", idFailure.ParamName);
        Assert.Equal("clientSecret", secretFailure.ParamName);
        AssertSecretIsAbsent(idFailure, clientId);
        AssertSecretIsAbsent(idFailure, "synthetic-secret");
        AssertSecretIsAbsent(secretFailure, clientSecret);
        AssertSecretIsAbsent(secretFailure, "synthetic-id.access");
    }

    [Fact]
    public void AcceptsAccessServiceTokenMaterialAtTheInclusiveLengthBounds()
    {
        var clientId = new string('a', 256);
        var clientSecret = new string('b', 512);
        var credential = new UsageCredential(Identity, clientId, clientSecret);
        var shortest = new UsageCredential(Identity, "a", "b");

        Assert.Equal(clientId, credential.ClientId);
        Assert.Equal(clientSecret, credential.ClientSecret);
        Assert.Equal("a", shortest.ClientId);
        Assert.Equal("b", shortest.ClientSecret);
    }

    [Fact]
    public async Task CollectorFilterIsSentAsExactlyOneQueryParameter()
    {
        var peer = new FakePeer(_ => Json(Body(
            "2026-11-01",
            "2026-11-01",
            400,
            Row("2026-11-01", "col-a1", 400))));
        using var harness = Harness.Create(peer, Credential());

        var usage = await harness.Port.ReadDailyAsync(
            new DateOnly(2026, 11, 1),
            new DateOnly(2026, 11, 1),
            "col-a1",
            CancellationToken.None);

        Assert.Equal(400L, usage.TotalTokens);
        Assert.Equal(
            "https://usage.test/v1/usage/daily?from=2026-11-01&to=2026-11-01&collector=col-a1",
            Assert.Single(peer.Requests).Uri);
    }

    [Fact]
    public async Task CredentialIsReadPerRequestAndASecondIdentityNeverReusesTheFirstResponse()
    {
        const string firstId = "synthetic-id-a.access";
        const string firstSecret = "synthetic-secret-a";
        const string secondId = "synthetic-id-b.access";
        const string secondSecret = "synthetic-secret-b";
        var peer = new FakePeer(request =>
        {
            var clientId = request.Headers.TryGetValues("CF-Access-Client-Id", out var values)
                ? string.Join(",", values)
                : null;
            return clientId switch
            {
                firstId => Json(Body("2026-11-01", "2026-11-01", 10, Row("2026-11-01", "col-a1", 10))),
                secondId => Json(Body("2026-11-01", "2026-11-01", 20, Row("2026-11-01", "col-a1", 20))),
                _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            };
        });
        var credentials = new QueueCredentials(
            new UsageCredential(Identity, firstId, firstSecret),
            new UsageCredential(Identity, secondId, secondSecret));
        using var client = new HttpClient(peer);
        var port = new HttpUsageReadPort(client, Origin, Identity, credentials);

        var first = await port.ReadDailyAsync(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), null, CancellationToken.None);
        var second = await port.ReadDailyAsync(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), null, CancellationToken.None);

        Assert.Equal(10L, first.TotalTokens);
        Assert.Equal(20L, second.TotalTokens);
        Assert.Equal(2, credentials.Calls);
        Assert.Equal(2, peer.Requests.Count);
        Assert.Equal(firstId, peer.Requests[0].AccessClientId);
        Assert.Equal(firstSecret, peer.Requests[0].AccessClientSecret);
        Assert.Equal(secondId, peer.Requests[1].AccessClientId);
        Assert.Equal(secondSecret, peer.Requests[1].AccessClientSecret);
        Assert.DoesNotContain(firstSecret, peer.Requests[1].AccessClientSecret, StringComparison.Ordinal);
        Assert.Null(peer.Requests[0].Authorization);
        Assert.Null(peer.Requests[1].Authorization);
        Assert.Null(peer.Requests[0].Cookie);
        Assert.Null(peer.Requests[1].Cookie);
        Assert.False(client.DefaultRequestHeaders.Contains("CF-Access-Client-Id"));
        Assert.False(client.DefaultRequestHeaders.Contains("CF-Access-Client-Secret"));
        Assert.False(client.DefaultRequestHeaders.Contains("Authorization"));
        Assert.False(client.DefaultRequestHeaders.Contains("Cookie"));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public void RejectsNonHttpsOrigin()
    {
        var peer = new FakePeer(_ => Json(Body("2026-11-01", "2026-11-01", 0)));
        using var client = new HttpClient(peer);

        Assert.Throws<ArgumentException>(() => new HttpUsageReadPort(
            client, new Uri("http://usage.test"), Identity, new QueueCredentials(Credential())));
    }

    [Theory]
    [InlineData("https://usage.test/api")]
    [InlineData("https://usage.test/?from=2026-01-01")]
    [InlineData("https://user:pass@usage.test")]
    public void RejectsOriginThatIsNotABareHttpsOrigin(string origin)
    {
        var peer = new FakePeer(_ => Json(Body("2026-11-01", "2026-11-01", 0)));
        using var client = new HttpClient(peer);

        Assert.Throws<ArgumentException>(() => new HttpUsageReadPort(
            client, new Uri(origin), Identity, new QueueCredentials(Credential())));
    }

    [Fact]
    public void RejectsClientThatAlreadyCarriesADefaultAuthorizationHeader()
    {
        var peer = new FakePeer(_ => Json(Body("2026-11-01", "2026-11-01", 0)));
        using var client = new HttpClient(peer);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "stale-identity");

        Assert.Throws<ArgumentException>(() => new HttpUsageReadPort(
            client, Origin, Identity, new QueueCredentials(Credential())));
    }

    [Theory]
    [InlineData("COLLECTOR")]
    [InlineData("col-a1&from=2026-01-01")]
    [InlineData("col/a1")]
    [InlineData("ab")]
    public async Task RejectsInvalidCollectorFilterWithoutSending(string collectorId)
    {
        var peer = new FakePeer(_ => Json(Body("2026-11-01", "2026-11-01", 0)));
        using var harness = Harness.Create(peer, Credential());

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => harness.Port.ReadDailyAsync(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), collectorId, CancellationToken.None));

        Assert.Equal(UsageReadFailure.InvalidRequest, failure.Failure);
        Assert.Empty(peer.Requests);
    }

    [Fact]
    public async Task RejectsRangeLongerThanTheQueryWindowWithoutSending()
    {
        var peer = new FakePeer(_ => Json(Body("2025-01-01", "2026-11-01", 0)));
        using var harness = Harness.Create(peer, Credential());

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => harness.Port.ReadDailyAsync(
            new DateOnly(2025, 1, 1), new DateOnly(2026, 11, 1), null, CancellationToken.None));

        Assert.Equal(UsageReadFailure.InvalidRequest, failure.Failure);
        Assert.Empty(peer.Requests);
    }

    [Fact]
    public async Task RejectsInvertedRangeWithoutSending()
    {
        var peer = new FakePeer(_ => Json(Body("2026-11-01", "2026-11-01", 0)));
        using var harness = Harness.Create(peer, Credential());

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => harness.Port.ReadDailyAsync(
            new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 1), null, CancellationToken.None));

        Assert.Equal(UsageReadFailure.InvalidRequest, failure.Failure);
        Assert.Empty(peer.Requests);
    }

    [Fact]
    public async Task RejectsMissingCredentialWithoutSending()
    {
        var peer = new FakePeer(_ => Json(Body("2026-11-01", "2026-11-01", 0)));
        using var harness = Harness.Create(peer);

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => harness.Port.ReadDailyAsync(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), null, CancellationToken.None));

        Assert.Equal(UsageReadFailure.CredentialUnavailable, failure.Failure);
        Assert.Empty(peer.Requests);
    }

    [Fact]
    public async Task RejectsCredentialIssuedForAnotherIdentityWithoutSending()
    {
        var peer = new FakePeer(_ => Json(Body("2026-11-01", "2026-11-01", 0)));
        using var harness = Harness.Create(peer, new UsageCredential("owner-b", "synthetic-id.access", "synthetic-secret"));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => harness.Port.ReadDailyAsync(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), null, CancellationToken.None));

        Assert.Equal(UsageReadFailure.IdentityMismatch, failure.Failure);
        Assert.Empty(peer.Requests);
    }

    [Fact]
    public async Task RejectsResponseThatEchoesADifferentRange()
    {
        var peer = new FakePeer(_ => Json(Body("2026-11-01", "2026-11-03", 0)));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.RangeMismatch, failure.Failure);
    }

    [Fact]
    public async Task RejectsRowOutsideTheRequestedRange()
    {
        var peer = new FakePeer(_ => Json(Body(
            "2026-11-01", "2026-11-02", 400, Row("2026-11-05", "col-a1", 400))));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer, new DateOnly(2026, 11, 2)));

        Assert.Equal(UsageReadFailure.RangeMismatch, failure.Failure);
    }

    [Fact]
    public async Task RejectsRowForAnotherCollectorWhenAFilterWasRequested()
    {
        var peer = new FakePeer(_ => Json(Body(
            "2026-11-01", "2026-11-01", 400, Row("2026-11-01", "col-a2", 400))));
        using var harness = Harness.Create(peer, Credential());

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => harness.Port.ReadDailyAsync(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), "col-a1", CancellationToken.None));

        Assert.Equal(UsageReadFailure.FilterMismatch, failure.Failure);
    }

    [Theory]
    [InlineData("{\"from\":\"2026-11-01\",\"to\":\"2026-11-01\",\"days\":[],\"totalTokens\":0,\"extra\":1}")]
    [InlineData("{\"from\":\"2026-11-01\",\"to\":\"2026-11-01\",\"days\":[{\"date\":\"2026-11-01\",\"collectorId\":\"col-a1\"}],\"totalTokens\":0}")]
    [InlineData("{\"to\":\"2026-11-01\",\"days\":[],\"totalTokens\":0}")]
    [InlineData("{\"from\":\"2026-11-01\",\"to\":\"2026-11-01\",\"days\":{},\"totalTokens\":0}")]
    [InlineData("{\"from\":\"2026-11-01\",\"to\":\"2026-11-01\"}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task RejectsUnknownMissingOrMalformedFieldsInsteadOfDefaultingToZero(string body)
    {
        var peer = new FakePeer(_ => Json(body));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.MalformedResponse, failure.Failure);
    }

    [Theory]
    [InlineData("{\"from\":\"2026-11-01\",\"to\":\"2026-11-01\",\"days\":[{\"date\":\"2026-11-01\",\"collectorId\":\"col-a1\",\"totalTokens\":1.5}],\"totalTokens\":1.5}")]
    [InlineData("{\"from\":\"2026-11-01\",\"to\":\"2026-11-01\",\"days\":[{\"date\":\"2026-11-01\",\"collectorId\":\"col-a1\",\"totalTokens\":\"12\"}],\"totalTokens\":12}")]
    [InlineData("{\"from\":\"2026-11-01\",\"to\":\"2026-11-01\",\"days\":[{\"date\":\"2026-11-01\",\"collectorId\":\"col-a1\",\"totalTokens\":-1}],\"totalTokens\":-1}")]
    [InlineData("{\"from\":\"2026-11-01\",\"to\":\"2026-11-01\",\"days\":[{\"date\":\"2026-11-01\",\"collectorId\":\"col-a1\",\"totalTokens\":true}],\"totalTokens\":1}")]
    [InlineData("{\"from\":\"2026-11-01\",\"to\":\"2026-11-01\",\"days\":[{\"date\":\"2026-11-01\",\"collectorId\":\"col-a1\",\"totalTokens\":9007199254740992}],\"totalTokens\":9007199254740992}")]
    [InlineData("{\"from\":\"2026-11-01\",\"to\":\"2026-11-01\",\"days\":[{\"date\":\"2026/11/01\",\"collectorId\":\"col-a1\",\"totalTokens\":0}],\"totalTokens\":0}")]
    [InlineData("{\"from\":\"2026-11-01\",\"to\":\"2026-11-01\",\"days\":[{\"date\":\"2026-11-01\",\"collectorId\":\"col-A1\",\"totalTokens\":0}],\"totalTokens\":0}")]
    public async Task RejectsUnsafeOrNonIntegerNumbersAndNonCanonicalDates(string body)
    {
        var peer = new FakePeer(_ => Json(body));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.MalformedResponse, failure.Failure);
    }

    [Fact]
    public async Task RejectsEchoedTotalThatDoesNotEqualTheDaySum()
    {
        var peer = new FakePeer(_ => Json(Body(
            "2026-11-01", "2026-11-01", 999, Row("2026-11-01", "col-a1", 400))));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.InconsistentTotal, failure.Failure);
    }

    [Theory]
    [InlineData("{\"from\":\"2026-11-01\",\"to\":\"2026-11-02\",\"days\":[{\"date\":\"2026-11-02\",\"collectorId\":\"col-a1\",\"totalTokens\":1},{\"date\":\"2026-11-01\",\"collectorId\":\"col-a1\",\"totalTokens\":2}],\"totalTokens\":3}")]
    [InlineData("{\"from\":\"2026-11-01\",\"to\":\"2026-11-02\",\"days\":[{\"date\":\"2026-11-01\",\"collectorId\":\"col-a1\",\"totalTokens\":1},{\"date\":\"2026-11-01\",\"collectorId\":\"col-a1\",\"totalTokens\":1}],\"totalTokens\":2}")]
    public async Task RejectsUnsortedOrDuplicatedRows(string body)
    {
        var peer = new FakePeer(_ => Json(body));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer, new DateOnly(2026, 11, 2)));

        Assert.Equal(UsageReadFailure.MalformedResponse, failure.Failure);
    }

    [Theory]
    [InlineData("{\"from\":\"2026-11-01\",\"to\":\"2026-11-01\",\"days\":[],\"totalTokens\":0,\"totalTokens\":0}")]
    [InlineData("{\"from\":\"2026-11-01\",\"to\":\"2026-11-01\",\"days\":[],\"days\":[]}")]
    [InlineData("{\"from\":\"2026-11-01\",\"from\":\"2026-11-01\",\"to\":\"2026-11-01\",\"days\":[]}")]
    [InlineData("{\"from\":\"2026-11-01\",\"to\":\"2026-11-01\",\"days\":[{\"date\":\"2026-11-01\",\"collectorId\":\"col-a1\",\"collectorId\":\"col-a1\"}],\"totalTokens\":0}")]
    [InlineData("{\"from\":\"2026-11-01\",\"to\":\"2026-11-01\",\"days\":[{\"date\":\"2026-11-01\",\"date\":\"2026-11-01\",\"collectorId\":\"col-a1\"}],\"totalTokens\":0}")]
    [InlineData("{\"from\":\"2026-11-01\",\"to\":\"2026-11-01\",\"days\":[{\"date\":\"2026-11-01\",\"collectorId\":\"col-a1\",\"totalTokens\":0,\"totalTokens\":0}],\"totalTokens\":0}")]
    public async Task RejectsRepeatedContractFieldsAsTypedMalformedResponses(string body)
    {
        var peer = new FakePeer(_ => Json(body));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.MalformedResponse, failure.Failure);
    }

    [Fact]
    public async Task RejectsMoreRowsThanTheDailyReadBound()
    {
        var rows = new StringBuilder();
        for (var index = 0; index <= HttpUsageReadPort.MaxRows; index++)
        {
            if (index > 0)
            {
                rows.Append(',');
            }

            rows.Append(Row("2026-11-01", "col-a1", 0));
        }

        var body = "{\"from\":\"2026-11-01\",\"to\":\"2026-11-01\",\"days\":[" + rows
            + "],\"totalTokens\":0,\"collectors\":[" + Collector("col-a1", 1, "null") + "]}";
        var peer = new FakePeer(_ => Json(body));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.ResponseTooLarge, failure.Failure);
    }

    [Fact]
    public async Task RejectsDeclaredOversizedContentLengthBeforeParsing()
    {
        var peer = new FakePeer(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[HttpUsageReadPort.MaxResponseBytes + 1]),
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return response;
        });

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.ResponseTooLarge, failure.Failure);
    }

    [Fact]
    public async Task RejectsStreamedOversizedBodyWithNoContentLength()
    {
        var payload = new byte[HttpUsageReadPort.MaxResponseBytes + 64];
        var peer = new FakePeer(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NonSeekableStream(payload)),
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return response;
        });

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.ResponseTooLarge, failure.Failure);
    }

    [Fact]
    public async Task RejectsRedirectWithoutFollowingTheLocation()
    {
        var peer = new FakePeer(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://elsewhere.test/v1/usage/daily");
            return redirect;
        });

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.RedirectRejected, failure.Failure);
        Assert.Single(peer.Requests);
    }

    [Fact]
    public async Task RejectsResponseThatReportsAPostRedirectRequestInsteadOfTheRequestedOne()
    {
        var peer = new FakePeer(_ =>
        {
            var response = Json(Body("2026-11-01", "2026-11-01", 0));
            response.RequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "https://elsewhere.test/v1/usage/daily?from=2026-11-01&to=2026-11-01");
            return response;
        });

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.RedirectRejected, failure.Failure);
    }

    [Theory]
    [InlineData("https://usage.test/v1/usage/daily?from=2026-11-01&to=2026-11-02")]
    [InlineData("https://usage.test/v1/usage/daily?from=2026-11-01")]
    [InlineData("https://usage.test/v1/usage/other?from=2026-11-01&to=2026-11-01")]
    public async Task RejectsResponseThatReportsADifferentUriOnTheSameOrigin(string answered)
    {
        var peer = new FakePeer(_ =>
        {
            var response = Json(Body("2026-11-01", "2026-11-01", 0));
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, answered);
            return response;
        });

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.RedirectRejected, failure.Failure);
    }

    [Fact]
    public async Task AcceptsAResponseThatReportsTheRequestedUri()
    {
        var peer = new FakePeer(request =>
        {
            var response = Json(Body("2026-11-01", "2026-11-01", 0));
            response.RequestMessage = request;
            return response;
        });

        var usage = await Read(peer);

        Assert.Equal(0L, usage.TotalTokens);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, UsageReadFailure.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, UsageReadFailure.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, UsageReadFailure.NotFound)]
    [InlineData(HttpStatusCode.RequestTimeout, UsageReadFailure.UnexpectedStatus)]
    [InlineData((HttpStatusCode)413, UsageReadFailure.UnexpectedStatus)]
    [InlineData(HttpStatusCode.TooManyRequests, UsageReadFailure.Transient)]
    [InlineData(HttpStatusCode.InternalServerError, UsageReadFailure.Transient)]
    [InlineData(HttpStatusCode.BadGateway, UsageReadFailure.Transient)]
    public async Task MapsStatusFailuresAndNeverEchoesTheResponseBody(
        HttpStatusCode status,
        UsageReadFailure expected)
    {
        const string Secret = "response-body-secret";
        var peer = new FakePeer(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("{\"error\":\"" + Secret + "\"}", Encoding.UTF8, "application/json"),
        });

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(expected, failure.Failure);
        Assert.Equal((int)status, failure.StatusCode);
        Assert.DoesNotContain(Secret, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("usage.test", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsNonJsonMediaType()
    {
        var peer = new FakePeer(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Body("2026-11-01", "2026-11-01", 0), Encoding.UTF8, "text/plain"),
        });

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.UnsupportedContent, failure.Failure);
    }

    [Fact]
    public async Task RejectsCompressedResponseBecauseTheTransportNegotiatesNone()
    {
        var peer = new FakePeer(_ =>
        {
            var response = Json(Body("2026-11-01", "2026-11-01", 0));
            response.Content.Headers.ContentEncoding.Add("gzip");
            return response;
        });

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.UnsupportedContent, failure.Failure);
    }

    [Fact]
    public async Task ReportsTransportFailureWithoutEchoingTheInnerExceptionMessage()
    {
        const string Secret = "/home/someone/.config/secret";
        var peer = new FakePeer(_ => throw new HttpRequestException("failed at " + Secret));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.TransportFailure, failure.Failure);
        Assert.DoesNotContain(Secret, failure.Message, StringComparison.Ordinal);
        Assert.IsType<HttpRequestException>(failure.InnerException);
    }

    [Fact]
    public async Task PropagatesCallerCancellationInsteadOfReportingATransportFailure()
    {
        var peer = new FakePeer(_ => Json(Body("2026-11-01", "2026-11-01", 0)));
        using var harness = Harness.Create(peer, Credential());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var exception = await Record.ExceptionAsync(() => harness.Port.ReadDailyAsync(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), null, cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.IsNotType<UsageReadException>(exception);
        Assert.Empty(peer.Requests);
    }

    [Fact]
    public async Task ClientTimeoutIsReportedAsTimeoutRatherThanCancellation()
    {
        var peer = new FakePeer(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Json(Body("2026-11-01", "2026-11-01", 0));
        });
        using var client = new HttpClient(peer) { Timeout = TimeSpan.FromMilliseconds(50) };
        var port = new HttpUsageReadPort(client, Origin, Identity, new QueueCredentials(Credential()));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => port.ReadDailyAsync(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), null, CancellationToken.None));

        Assert.Equal(UsageReadFailure.Timeout, failure.Failure);
    }

    [Fact]
    public async Task HeadersThatDoNotArriveWithinTheRequestDeadlineAreReportedAsTimeout()
    {
        var peer = new FakePeer(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            return Json(Body("2026-11-01", "2026-11-01", 0));
        });
        using var client = new HttpClient(peer) { Timeout = Timeout.InfiniteTimeSpan };
        var port = new HttpUsageReadPort(
            client, Origin, Identity, new QueueCredentials(Credential()), TimeSpan.FromMilliseconds(150));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => port.ReadDailyAsync(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), null, CancellationToken.None));

        Assert.Equal(UsageReadFailure.Timeout, failure.Failure);
        Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
    }

    [Fact]
    public async Task BodyThatArrivesAfterTheRequestDeadlineIsReportedAsTimeout()
    {
        // The headers arrive at once; only the body is slow. A client timeout alone
        // stops applying once the headers are read, so the port must bound the body.
        var payload = Encoding.UTF8.GetBytes(Body("2026-11-01", "2026-11-01", 0));
        var peer = new FakePeer((_, _) => Task.FromResult(
            JsonWithStream(new DelayedStream(payload, TimeSpan.FromSeconds(2)))));
        using var client = new HttpClient(peer) { Timeout = Timeout.InfiniteTimeSpan };
        var port = new HttpUsageReadPort(
            client, Origin, Identity, new QueueCredentials(Credential()), TimeSpan.FromMilliseconds(150));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => port.ReadDailyAsync(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), null, CancellationToken.None));

        Assert.Equal(UsageReadFailure.Timeout, failure.Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BodyTransportFailureIsClassifiedInsteadOfEscapingThePort(bool httpFailure)
    {
        const string Secret = "/home/someone/.config/secret";
        var peer = new FakePeer((_, _) => Task.FromResult(JsonWithStream(new ThrowingStream(
            httpFailure
                ? new HttpRequestException("body reset at " + Secret)
                : new IOException("body broken at " + Secret)))));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.TransportFailure, failure.Failure);
        Assert.DoesNotContain(Secret, failure.Message, StringComparison.Ordinal);
        Assert.IsType(httpFailure ? typeof(HttpRequestException) : typeof(IOException), failure.InnerException);
    }

    [Fact]
    public async Task BodyThatFailsAfterAPartialReadIsReportedAsTransportFailure()
    {
        // A truncated body is a transport failure, never "malformed JSON": a parsed
        // prefix must not be mistaken for a complete answer.
        var prefix = Encoding.UTF8.GetBytes(Body("2026-11-01", "2026-11-01", 0));
        var peer = new FakePeer((_, _) => Task.FromResult(JsonWithStream(new PrefixThenThrowStream(prefix))));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.TransportFailure, failure.Failure);
        Assert.IsType<IOException>(failure.InnerException);
    }

    [Fact]
    public async Task PropagatesCallerCancellationWhileTheBodyIsBeingRead()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = new FakePeer((_, _) => Task.FromResult(JsonWithStream(new BlockingStream(started))));
        using var client = new HttpClient(peer) { Timeout = Timeout.InfiniteTimeSpan };
        var port = new HttpUsageReadPort(
            client, Origin, Identity, new QueueCredentials(Credential()), TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();

        var read = port.ReadDailyAsync(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), null, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        var exception = await Record.ExceptionAsync(() => read);

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.IsNotType<UsageReadException>(exception);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsANonPositiveRequestDeadline(int milliseconds)
    {
        var peer = new FakePeer(_ => Json(Body("2026-11-01", "2026-11-01", 0)));
        using var client = new HttpClient(peer);

        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpUsageReadPort(
            client,
            Origin,
            Identity,
            new QueueCredentials(Credential()),
            TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void RejectsAnUnboundedRequestDeadline()
    {
        var peer = new FakePeer(_ => Json(Body("2026-11-01", "2026-11-01", 0)));
        using var client = new HttpClient(peer);

        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpUsageReadPort(
            client, Origin, Identity, new QueueCredentials(Credential()), Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public void DefaultRequestDeadlineIsFiniteAndPositive()
    {
        Assert.True(HttpUsageReadPort.DefaultRequestDeadline > TimeSpan.Zero);
        Assert.NotEqual(Timeout.InfiniteTimeSpan, HttpUsageReadPort.DefaultRequestDeadline);

        var peer = new FakePeer(_ => Json(Body("2026-11-01", "2026-11-01", 0)));
        using var client = new HttpClient(peer);
        var port = new HttpUsageReadPort(client, Origin, Identity, new QueueCredentials(Credential()));
        Assert.Equal(HttpUsageReadPort.DefaultRequestDeadline, port.RequestDeadline);
    }

    [Fact]
    public void TransportFactoryDisablesRedirectsCookiesAndDecompression()
    {
        using var handler = UsageReadTransport.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.True(handler.ConnectTimeout > TimeSpan.Zero);

        using var client = UsageReadTransport.CreateClient(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(5), client.Timeout);
    }

    [Fact]
    public void CredentialOmitsTheTokenFromItsTextRepresentation()
    {
        var credential = new UsageCredential(Identity, "synthetic-id.access", "synthetic-secret");

        Assert.DoesNotContain("synthetic-id.access", credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret", credential.ToString(), StringComparison.Ordinal);
        Assert.Contains(Identity, credential.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialDebugDisplayOmitsClientIdAndClientSecret()
    {
        var credential = new UsageCredential(Identity, "synthetic-id.access", "synthetic-secret");
        var display = credential.GetType().GetCustomAttribute<DebuggerDisplayAttribute>();

        Assert.NotNull(display);
        Assert.DoesNotContain("ClientId", display.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientSecret", display.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-id.access", display.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret", display.Value, StringComparison.Ordinal);

        foreach (var name in new[] { nameof(UsageCredential.ClientId), nameof(UsageCredential.ClientSecret) })
        {
            var browsable = typeof(UsageCredential).GetProperty(name)!.GetCustomAttribute<DebuggerBrowsableAttribute>();
            Assert.NotNull(browsable);
            Assert.Equal(DebuggerBrowsableState.Never, browsable.State);
        }
    }

    [Theory]
    [InlineData("authorization")]
    [InlineData("Authorization")]
    [InlineData("CF-Access-Client-Id")]
    [InlineData("cf-access-client-id")]
    [InlineData("Cf-Access-Client-Secret")]
    [InlineData("CF-Access-Client-Secret")]
    [InlineData("Cookie")]
    [InlineData("cookie")]
    [InlineData("COOKIE")]
    public void RejectsClientThatAlreadyCarriesAForbiddenDefaultHeader(string headerName)
    {
        const string secret = "synthetic-secret";
        var peer = new FakePeer(_ => Json(Body("2026-11-01", "2026-11-01", 0)));
        using var client = new HttpClient(peer);
        Assert.True(client.DefaultRequestHeaders.TryAddWithoutValidation(headerName, secret));

        var failure = Assert.Throws<ArgumentException>(() => new HttpUsageReadPort(
            client, Origin, Identity, new QueueCredentials(Credential())));

        Assert.Equal("client", failure.ParamName);
        Assert.DoesNotContain(secret, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsAccessLoginRedirectWithoutFollowingOrReadingTheBody()
    {
        var peer = new FakePeer(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://usage.test/cdn-cgi/access/login");
            redirect.Headers.TryAddWithoutValidation("Set-Cookie", "CF_Authorization=synthetic-session; Path=/");
            redirect.Content = new StreamContent(new ThrowingStream(new InvalidOperationException("body was read")));
            return redirect;
        });

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.RedirectRejected, failure.Failure);
        Assert.Equal(302, failure.StatusCode);
        Assert.Single(peer.Requests);
        Assert.Equal(
            "https://usage.test/v1/usage/daily?from=2026-11-01&to=2026-11-01",
            peer.Requests[0].Uri);
        Assert.DoesNotContain("synthetic-session", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn-cgi", failure.Message, StringComparison.Ordinal);
        Assert.Null(peer.Requests[0].Cookie);
    }

    [Fact]
    public async Task ClassifiesAuthUnavailableAsRetryableAndUnauthorizedAsUserAction()
    {
        const string secret = "synthetic-body-secret";
        var unavailable = new FakePeer(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(
                "{\"error\":{\"code\":\"auth_unavailable\",\"message\":\"" + secret + "\"}}",
                Encoding.UTF8,
                "application/json"),
        });
        var unauthorized = new FakePeer(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                "{\"error\":{\"code\":\"unauthorized\",\"message\":\"" + secret + "\"}}",
                Encoding.UTF8,
                "application/json"),
        });

        var transient = await Assert.ThrowsAsync<UsageReadException>(() => Read(unavailable));
        var rejected = await Assert.ThrowsAsync<UsageReadException>(() => Read(unauthorized));

        Assert.Equal(UsageReadFailure.AuthUnavailable, transient.Failure);
        Assert.Equal(503, transient.StatusCode);
        Assert.True(transient.IsRetryable);
        Assert.False(transient.RequiresUserAction);
        Assert.DoesNotContain(secret, transient.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("auth_unavailable", transient.Message, StringComparison.Ordinal);

        Assert.Equal(UsageReadFailure.Unauthorized, rejected.Failure);
        Assert.Equal(401, rejected.StatusCode);
        Assert.False(rejected.IsRetryable);
        Assert.True(rejected.RequiresUserAction);
        Assert.DoesNotContain(secret, rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadParsesCollectorFreshnessIncludingCollectorsThatNeverReported()
    {
        var peer = new FakePeer(_ => Json(BodyWithCollectors(
            "2026-11-01",
            "2026-11-02",
            1600,
            CollectorList(
                Collector("col-a1", 3, "\"2026-11-20T03:04:05.006Z\""),
                Collector("col-a2", 1, "null")),
            Row("2026-11-01", "col-a1", 400),
            Row("2026-11-02", "col-a1", 1200))));

        var usage = await Read(peer, new DateOnly(2026, 11, 2));

        Assert.Collection(
            usage.Collectors,
            entry =>
            {
                Assert.Equal(new UsageCollectorReceipt(
                    "col-a1",
                    3,
                    new DateTimeOffset(2026, 11, 20, 3, 4, 5, 6, TimeSpan.Zero)), entry);
                Assert.Equal(TimeSpan.Zero, entry.LastReceivedAt!.Value.Offset);
            },
            entry => Assert.Equal(new UsageCollectorReceipt("col-a2", 1, null), entry));
    }

    [Theory]
    [InlineData("col-b1", "col-a1", "a collector entry is out of order")]
    [InlineData("col-a1", "col-a1", "a collector entry is repeated")]
    public async Task RejectsUnsortedOrDuplicatedCollectorFreshness(string first, string second, string because)
    {
        var peer = new FakePeer(_ => Json(BodyWithCollectors(
            "2026-11-01",
            "2026-11-01",
            0,
            CollectorList(
                Collector(first, 1, "\"2026-11-20T03:04:05.006Z\""),
                Collector(second, 1, "null")))));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.MalformedResponse, failure.Failure);
        Assert.True(failure.Message.Length > 0, because);
    }

    [Theory]
    [InlineData("{\"collectorId\":\"col-a1\",\"generation\":1}")]
    [InlineData("{\"collectorId\":\"col-a1\",\"lastReceivedAt\":null}")]
    [InlineData("{\"collectorId\":\"col-a1\",\"generation\":1,\"lastReceivedAt\":null,\"extra\":1}")]
    [InlineData("{\"collectorId\":\"\",\"generation\":1,\"lastReceivedAt\":null}")]
    [InlineData("{\"collectorId\":\"col a1\",\"generation\":1,\"lastReceivedAt\":null}")]
    [InlineData("{\"collectorId\":\"col-a1\",\"generation\":0,\"lastReceivedAt\":null}")]
    [InlineData("{\"collectorId\":\"col-a1\",\"generation\":-1,\"lastReceivedAt\":null}")]
    [InlineData("{\"collectorId\":\"col-a1\",\"generation\":1.5,\"lastReceivedAt\":null}")]
    [InlineData("{\"collectorId\":\"col-a1\",\"generation\":\"1\",\"lastReceivedAt\":null}")]
    [InlineData("{\"collectorId\":\"col-a1\",\"generation\":9007199254740992,\"lastReceivedAt\":null}")]
    [InlineData("{\"collectorId\":\"col-a1\",\"generation\":1,\"lastReceivedAt\":\"2026-11-20T03:04:05Z\"}")]
    [InlineData("{\"collectorId\":\"col-a1\",\"generation\":1,\"lastReceivedAt\":\"2026-11-20T03:04:05.006+09:00\"}")]
    [InlineData("{\"collectorId\":\"col-a1\",\"generation\":1,\"lastReceivedAt\":\"2026-11-20\"}")]
    [InlineData("{\"collectorId\":\"col-a1\",\"generation\":1,\"lastReceivedAt\":\"2026-11-20T24:00:00.000Z\"}")]
    [InlineData("{\"collectorId\":\"col-a1\",\"generation\":1,\"lastReceivedAt\":12345}")]
    public async Task RejectsCollectorFreshnessEntriesOutsideTheContract(string entry)
    {
        var peer = new FakePeer(_ => Json(
            "{\"from\":\"2026-11-01\",\"to\":\"2026-11-01\",\"days\":[],\"totalTokens\":0,"
            + "\"collectors\":[" + entry + "]}"));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.MalformedResponse, failure.Failure);
    }

    [Fact]
    public async Task RejectsMoreCollectorEntriesThanTheDocumentedBound()
    {
        var entries = string.Join(",", Enumerable
            .Range(0, HttpUsageReadPort.MaxCollectors + 1)
            .Select(index => Collector($"col-{index:d4}", 1, "null")));
        var peer = new FakePeer(_ => Json(
            "{\"from\":\"2026-11-01\",\"to\":\"2026-11-01\",\"days\":[],\"totalTokens\":0,"
            + "\"collectors\":[" + entries + "]}"));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.ResponseTooLarge, failure.Failure);
    }

    [Fact]
    public async Task RejectsARowWhoseCollectorIsMissingFromTheFreshnessList()
    {
        var peer = new FakePeer(_ => Json(BodyWithCollectors(
            "2026-11-01",
            "2026-11-01",
            400,
            CollectorList(Collector("col-a1", 1, "null")),
            Row("2026-11-01", "col-a2", 400))));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.MalformedResponse, failure.Failure);
    }

    [Fact]
    public async Task RejectsAFreshnessEntryForAnotherCollectorWhenCollectorFiltered()
    {
        var entries = Collector("col-a1", 1, "null") + "," + Collector("col-a2", 1, "null");
        var peer = new FakePeer(_ => Json(
            "{\"from\":\"2026-11-01\",\"to\":\"2026-11-01\",\"days\":[],\"totalTokens\":0,"
            + "\"collectors\":[" + entries + "]}"));
        using var harness = Harness.Create(peer, Credential());

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => harness.Port.ReadDailyAsync(
            new DateOnly(2026, 11, 1),
            new DateOnly(2026, 11, 1),
            "col-a1",
            CancellationToken.None));

        Assert.Equal(UsageReadFailure.FilterMismatch, failure.Failure);
    }

    [Fact]
    public async Task RejectsADuplicateCollectorsFieldAtTheRoot()
    {
        var peer = new FakePeer(_ => Json(
            "{\"from\":\"2026-11-01\",\"to\":\"2026-11-01\",\"days\":[],\"totalTokens\":0,"
            + "\"collectors\":[],\"collectors\":[]}"));

        var failure = await Assert.ThrowsAsync<UsageReadException>(() => Read(peer));

        Assert.Equal(UsageReadFailure.MalformedResponse, failure.Failure);
    }

    private static async Task<DailyUsage> Read(FakePeer peer, DateOnly? to = null)
    {
        using var harness = Harness.Create(peer, Credential());
        return await harness.Port.ReadDailyAsync(
            new DateOnly(2026, 11, 1),
            to ?? new DateOnly(2026, 11, 1),
            null,
            CancellationToken.None);
    }

    private static UsageCredential Credential() => new(Identity, "synthetic-id.access", "synthetic-secret");

    private static int HeaderValueCount(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.Count() : 0;

    private static string? SingleHeaderOrNull(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private static void AssertSecretIsAbsent(Exception failure, string secret)
    {
        if (secret.Length == 0)
        {
            return;
        }

        Assert.DoesNotContain(secret, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, failure.ToString(), StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A JSON response whose body is streamed, so its read can be observed and bounded.</summary>
    private static HttpResponseMessage JsonWithStream(Stream body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return response;
    }

    private static string Body(string from, string to, long totalTokens, params string[] rows) =>
        "{\"from\":\"" + from + "\",\"to\":\"" + to + "\",\"days\":[" + string.Join(",", rows)
        + "],\"totalTokens\":" + totalTokens.ToString(CultureInfo.InvariantCulture)
        + ",\"collectors\":[" + CollectorList(rows
            .Select(RowCollectorId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => Collector(id, 1, "null"))
            .ToArray()) + "]}";

    /// <summary>Extracts the collector id from a row built by <see cref="Row"/>.</summary>
    private static string RowCollectorId(string row)
    {
        const string Marker = "\"collectorId\":\"";
        var start = row.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length;
        var end = row.IndexOf('"', start);
        return row[start..end];
    }

    private static string BodyWithCollectors(
        string from,
        string to,
        long totalTokens,
        string collectorsJson,
        params string[] rows) =>
        "{\"from\":\"" + from + "\",\"to\":\"" + to + "\",\"days\":[" + string.Join(",", rows)
        + "],\"totalTokens\":" + totalTokens.ToString(CultureInfo.InvariantCulture)
        + ",\"collectors\":[" + collectorsJson + "]}";

    private static string CollectorList(params string[] entries) => string.Join(",", entries);

    /// <summary>One `collectors` entry; the time is passed as raw JSON so `null` is expressible.</summary>
    private static string Collector(string collectorId, long generation, string lastReceivedAtJson) =>
        "{\"collectorId\":\"" + collectorId + "\",\"generation\":"
        + generation.ToString(CultureInfo.InvariantCulture)
        + ",\"lastReceivedAt\":" + lastReceivedAtJson + "}";

    private static string Row(string date, string collectorId, long totalTokens) =>
        "{\"date\":\"" + date + "\",\"collectorId\":\"" + collectorId + "\",\"totalTokens\":"
        + totalTokens.ToString(CultureInfo.InvariantCulture) + "}";

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
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public FakePeer(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request)))
        {
        }

        public FakePeer(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

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
                SingleHeaderOrNull(request, "CF-Access-Client-Id"),
                SingleHeaderOrNull(request, "CF-Access-Client-Secret"),
                SingleHeaderOrNull(request, "Cookie")));

            return _respond(request, cancellationToken);
        }
    }

    private sealed class QueueCredentials(params UsageCredential?[] credentials) : IUsageCredentialProvider
    {
        private readonly Queue<UsageCredential?> _pending = new(credentials);

        public int Calls { get; private set; }

        public ValueTask<UsageCredential?> GetCredentialAsync(CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_pending.Count > 0 ? _pending.Dequeue() : null);
        }
    }

    /// <summary>Boilerplate for a bounded, read-only body stream used as a fake peer body.</summary>
    private abstract class ReadOnlyTestStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Serves a fixed payload only after a delay that honours cancellation.</summary>
    private sealed class DelayedStream(byte[] payload, TimeSpan delay) : ReadOnlyTestStream
    {
        private int _offset;
        private bool _delayed;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_delayed)
            {
                _delayed = true;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            return Copy(buffer.Span);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_delayed)
            {
                _delayed = true;
                Thread.Sleep(delay);
            }

            return Copy(buffer.AsSpan(offset, count));
        }

        private int Copy(Span<byte> destination)
        {
            var count = Math.Min(destination.Length, payload.Length - _offset);
            payload.AsSpan(_offset, count).CopyTo(destination);
            _offset += count;
            return count;
        }
    }

    /// <summary>Fails the first body read with the given transport error.</summary>
    private sealed class ThrowingStream(Exception failure) : ReadOnlyTestStream
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.FromException<int>(failure);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) => Task.FromException<int>(failure);

        public override int Read(byte[] buffer, int offset, int count) => throw failure;
    }

    /// <summary>Serves one partial payload, then fails the next read like a truncated response.</summary>
    private sealed class PrefixThenThrowStream(byte[] prefix) : ReadOnlyTestStream
    {
        private bool _served;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_served)
            {
                return ValueTask.FromException<int>(
                    new IOException("the connection dropped mid-body"));
            }

            _served = true;
            return ValueTask.FromResult(Copy(buffer.Span));
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Copy(Span<byte> destination)
        {
            var count = Math.Min(destination.Length, prefix.Length);
            prefix.AsSpan(0, count).CopyTo(destination);
            return count;
        }
    }

    /// <summary>Never produces a body byte and only completes when cancelled.</summary>
    private sealed class BlockingStream(TaskCompletionSource started) : ReadOnlyTestStream
    {
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NonSeekableStream(byte[] payload) : Stream
    {
        private readonly MemoryStream _inner = new(payload);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

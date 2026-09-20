using System.Net;
using System.Net.Http;
using QuotaSight.Application;
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

    [Fact]
    public async Task GitHub_device_flow_uses_form_encoded_requests_and_returns_successful_token()
    {
        var requests = new List<(string? ContentType, string Body)>();
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            Json(HttpStatusCode.OK, "{\"device_code\":\"device-code\",\"user_code\":\"user-code\",\"verification_uri\":\"https://github.com/login/device\",\"expires_in\":600,\"interval\":0}"),
            Json(HttpStatusCode.OK, "{\"access_token\":\"access-token-secret\"}")
        });
        var handler = new Handler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            requests.Add((request.Content.Headers.ContentType?.MediaType, body));
            return responses.Dequeue();
        });
        var client = new GitHubDeviceFlowClient(new HttpClient(handler), "client-id", delay: new NoDelay(), scope: "read:user read:org");

        var start = await client.StartAsync(default);
        var result = await client.PollAsync(start.Value!, default);

        Assert.True(result.IsSuccess);
        Assert.Equal("access-token-secret", result.Value);
        Assert.Collection(requests,
            request =>
            {
                Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
                Assert.Equal("client_id=client-id&scope=read%3Auser+read%3Aorg", request.Body);
            },
            request =>
            {
                Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
                Assert.Equal("client_id=client-id&device_code=device-code&grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", request.Body);
            });
    }

    [Fact]
    public async Task GitHub_poll_classifies_known_errors_without_exposing_response_data()
    {
        var expected = new Dictionary<string, FetchStatus>
        {
            ["expired_token"] = FetchStatus.TransientFailure,
            ["access_denied"] = FetchStatus.Unauthorized,
            ["incorrect_client_credentials"] = FetchStatus.Unauthorized,
            ["device_flow_disabled"] = FetchStatus.Unsupported,
            ["unsupported_grant_type"] = FetchStatus.Unsupported
        };

        foreach (var pair in expected)
        {
            var error = pair.Key;
            var responseBody = $"{{\"error\":\"{error}\",\"error_description\":\"secret response details\"}}";
            var authorization = new DeviceAuthorizationStart("device-code", "user-code", new Uri("https://github.com/login/device"), DateTimeOffset.UtcNow.AddMinutes(1), TimeSpan.Zero);
            var result = await new GitHubDeviceFlowClient(
                new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, responseBody))),
                "client-id",
                delay: new NoDelay()).PollAsync(authorization, default);

            Assert.Equal(pair.Value, result.Status);
            Assert.Null(result.Value);
            Assert.DoesNotContain(responseBody, result.Error ?? "", StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Copilot_billing_usage_aggregates_all_ai_credit_models_and_preserves_quantities()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync("github", "github-token", default);
        HttpRequestMessage? captured = null;
        const string json = """
            {"timePeriod":{"year":2026,"month":6},"organization":"acme-org","user":"octo-user","usageItems":[
              {"product":"Copilot","sku":"premium-model-a","unitType":"credits","grossQuantity":1.25,"discountQuantity":0.25,"netQuantity":1.0},
              {"product":"Copilot","sku":"premium-model-b","unitType":"credits","grossQuantity":2,"discountQuantity":0,"netQuantity":2},
              {"product":"Copilot","sku":"legacy","unitType":"requests","grossQuantity":99,"discountQuantity":0,"netQuantity":99},
              {"product":"Other","sku":"x","unitType":"credits","grossQuantity":500,"discountQuantity":0,"netQuantity":500}
            ]}
            """;

        var result = await new CopilotBillingUsageAdapter(
            new HttpClient(new Handler(request => { captured = request; return Json(HttpStatusCode.OK, json); })),
            store, "acme-org", "octo-user", new FixedTimeProvider(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero)))
            .FetchAsync("ignored", default);

        Assert.True(result.IsSuccess);
        var snapshot = Assert.Single(result.Value!);
        Assert.Equal(3.25m, snapshot.Used);
        Assert.Equal(3.25m, snapshot.CopilotUsage!.GrossQuantity);
        Assert.Equal(0.25m, snapshot.CopilotUsage.DiscountQuantity);
        Assert.Equal(3m, snapshot.CopilotUsage.NetQuantity);
        Assert.Null(snapshot.Limit);
        Assert.Null(snapshot.ReportedPercent);
        Assert.Equal(QuotaSource.Delayed, snapshot.Source);
        Assert.Equal(QuotaConfidence.Official, snapshot.Confidence);
        Assert.Equal("Bearer github-token", captured!.Headers.Authorization!.ToString());
        Assert.Equal("application/vnd.github+json", captured.Headers.Accept.Single().MediaType);
        Assert.Contains("/organizations/acme-org/settings/billing/ai_credit/usage", captured.RequestUri!.AbsolutePath);
        Assert.Contains("year=2026", captured.RequestUri.Query);
        Assert.Contains("month=6", captured.RequestUri.Query);
        Assert.Contains("user=octo-user", captured.RequestUri.Query);
    }

    [Fact]
    public async Task Copilot_billing_usage_rejects_a_nested_period_that_does_not_match_the_request()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync("github", "github-token", default);
        const string json = """
            {"timePeriod":{"year":2025,"month":6},"organization":"acme-org","user":"octo-user","usageItems":[
              {"product":"Copilot","sku":"premium-model-a","unitType":"credits","grossQuantity":1,"discountQuantity":0,"netQuantity":1}
            ]}
            """;

        var result = await new CopilotBillingUsageAdapter(
            new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, json))),
            store, "acme-org", "octo-user", new FixedTimeProvider(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero)))
            .FetchAsync("ignored", default);

        Assert.Equal(FetchStatus.TransientFailure, result.Status);
    }

    [Fact]
    public async Task Copilot_billing_usage_rejects_a_response_for_a_different_user()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync("github", "github-token", default);
        const string json = """
            {"timePeriod":{"year":2026,"month":6},"organization":"acme-org","user":"someone-else","usageItems":[
              {"product":"Copilot","sku":"premium-model-a","unitType":"credits","grossQuantity":1,"discountQuantity":0,"netQuantity":1}
            ]}
            """;

        var result = await new CopilotBillingUsageAdapter(
            new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, json))),
            store, "acme-org", "octo-user", new FixedTimeProvider(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero)))
            .FetchAsync("ignored", default);

        Assert.Equal(FetchStatus.TransientFailure, result.Status);
    }

    [Fact]
    public async Task Copilot_billing_usage_accepts_optional_month_and_user_when_absent()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync("github", "github-token", default);
        const string json = "{\"timePeriod\":{\"year\":2026},\"organization\":\"acme-org\",\"usageItems\":[]}";
        var result = await new CopilotBillingUsageAdapter(
            new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, json))), store, "acme-org", "octo-user",
            new FixedTimeProvider(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero))).FetchAsync("ignored", default);
        Assert.Equal(FetchStatus.NoData, result.Status);
    }

    [Theory]
    [InlineData("{}")]
    public async Task Copilot_billing_usage_validates_envelope_before_empty_usage_decision(string json)
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync("github", "github-token", default);
        var result = await new CopilotBillingUsageAdapter(
            new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, json))), store, "acme-org", "octo-user",
            new FixedTimeProvider(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero))).FetchAsync("ignored", default);
        Assert.Equal(FetchStatus.TransientFailure, result.Status);
    }

    [Fact]
    public async Task Copilot_billing_usage_rejects_day_and_missing_organization()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync("github", "github-token", default);
        foreach (var json in new[]
        {
            "{\"timePeriod\":{\"year\":2026,\"month\":6,\"day\":1},\"organization\":\"acme-org\",\"usageItems\":[]}",
            "{\"timePeriod\":{\"year\":2026,\"month\":6,\"day\":null},\"organization\":\"acme-org\",\"usageItems\":[]}",
            "{\"timePeriod\":{\"year\":2026,\"month\":6},\"usageItems\":[]}"
        })
        {
            var result = await new CopilotBillingUsageAdapter(
                new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, json))), store, "acme-org", "octo-user",
                new FixedTimeProvider(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero))).FetchAsync("ignored", default);
            Assert.Equal(FetchStatus.TransientFailure, result.Status);
        }
    }

    [Fact]
    public async Task Copilot_billing_usage_ignores_unrecognized_units()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync("github", "github-token", default);
        const string json = "{\"timePeriod\":{\"year\":2026,\"month\":6},\"organization\":\"acme-org\",\"usageItems\":[{\"product\":\"Copilot\",\"unitType\":\"ai_credits\",\"grossQuantity\":1,\"discountQuantity\":0,\"netQuantity\":1}]}";
        var result = await new CopilotBillingUsageAdapter(
            new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, json))), store, "acme-org", "octo-user",
            new FixedTimeProvider(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero))).FetchAsync("ignored", default);
        Assert.Equal(FetchStatus.NoData, result.Status);
    }

    [Fact]
    public async Task Copilot_billing_usage_uses_ttl_and_org_user_identity()
    {
        var now = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);
        var store = new InMemoryCredentialStore();
        await store.SetAsync("github", "github-token", default);
        const string json = "{\"timePeriod\":{\"year\":2026,\"month\":6},\"organization\":\"acme-org\",\"user\":\"octo-user\",\"usageItems\":[{\"product\":\"Copilot\",\"unitType\":\"credits\",\"grossQuantity\":1,\"discountQuantity\":0,\"netQuantity\":1}]}";
        var result = await new CopilotBillingUsageAdapter(new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, json))), store, "acme-org", "octo-user", new FixedTimeProvider(now)).FetchAsync("ignored", default);
        var snapshot = Assert.Single(result.Value!);
        Assert.Equal("acme-org/octo-user", snapshot.Account);
        Assert.Equal(now.AddHours(1), snapshot.FreshUntil);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), snapshot.Window.End);
        Assert.True(snapshot.IsStale(now.AddDays(1)));
    }

    [Fact]
    public async Task Copilot_billing_usage_explicit_token_path_sets_bearer_header()
    {
        HttpRequestMessage? captured = null;
        const string json = "{\"timePeriod\":{\"year\":2026,\"month\":6},\"organization\":\"acme-org\",\"user\":\"octo-user\",\"usageItems\":[{\"product\":\"Copilot\",\"unitType\":\"credits\",\"grossQuantity\":1,\"discountQuantity\":0,\"netQuantity\":1}]}";
        var result = await new CopilotBillingUsageAdapter(
            new HttpClient(new Handler(request => { captured = request; return Json(HttpStatusCode.OK, json); })),
            new InMemoryCredentialStore(), "acme-org", "octo-user", new FixedTimeProvider(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero)))
            .FetchWithTokenAsync("ignored", "explicit-token", default);
        Assert.True(result.IsSuccess);
        Assert.Equal("Bearer explicit-token", captured!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Copilot_billing_usage_maps_sum_overflow_to_transient_failure()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync("github", "github-token", default);
        const string json = "{\"timePeriod\":{\"year\":2026,\"month\":6},\"organization\":\"acme-org\",\"usageItems\":[{\"product\":\"Copilot\",\"unitType\":\"credits\",\"grossQuantity\":79228162514264337593543950335,\"discountQuantity\":0,\"netQuantity\":1},{\"product\":\"Copilot\",\"unitType\":\"credits\",\"grossQuantity\":79228162514264337593543950335,\"discountQuantity\":0,\"netQuantity\":1}]}";
        var result = await new CopilotBillingUsageAdapter(
            new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, json))), store, "acme-org", "octo-user",
            new FixedTimeProvider(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero))).FetchAsync("ignored", default);
        Assert.Equal(FetchStatus.TransientFailure, result.Status);
    }

    [Fact]
    public async Task Copilot_billing_usage_identity_includes_organization_to_separate_accounts()
    {
        var now = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);
        static string Response(string org) => "{\"timePeriod\":{\"year\":2026,\"month\":6},\"organization\":\"" + org + "\",\"user\":\"octo-user\",\"usageItems\":[{\"product\":\"Copilot\",\"unitType\":\"credits\",\"grossQuantity\":1,\"discountQuantity\":0,\"netQuantity\":1}]}";
        var first = new InMemoryCredentialStore();
        var second = new InMemoryCredentialStore();
        await first.SetAsync("github", "token", default);
        await second.SetAsync("github", "token", default);
        var a = await new CopilotBillingUsageAdapter(new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, Response("org-a")))), first, "org-a", "octo-user", new FixedTimeProvider(now)).FetchAsync("ignored", default);
        var b = await new CopilotBillingUsageAdapter(new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, Response("org-b")))), second, "org-b", "octo-user", new FixedTimeProvider(now)).FetchAsync("ignored", default);
        Assert.NotEqual(Assert.Single(a.Value!).Account, Assert.Single(b.Value!).Account);
    }

    [Fact]
    public async Task Copilot_billing_usage_rejects_invalid_target_and_does_not_send_without_configuration()
    {
        var sent = false;
        var store = new InMemoryCredentialStore();
        await store.SetAsync("github", "token", default);
        var adapter = new CopilotBillingUsageAdapter(new HttpClient(new Handler(_ => { sent = true; return Json(HttpStatusCode.OK, "{}"); })), store, "bad/org", "user");

        var invalidOrg = await adapter.FetchAsync("ignored", default);
        Assert.Equal(FetchStatus.Unsupported, invalidOrg.Status);
        Assert.False(sent);

        var noToken = await new CopilotBillingUsageAdapter(new HttpClient(new Handler(_ => { sent = true; return Json(HttpStatusCode.OK, "{}"); })), new InMemoryCredentialStore(), "org", "user").FetchAsync("ignored", default);
        Assert.Equal(FetchStatus.NoData, noToken.Status);
        Assert.False(sent);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-acme")]
    [InlineData("acme-")]
    [InlineData("acme--engineering")]
    [InlineData("acme_engineering")]
    [InlineData("acmé")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Copilot_billing_usage_rejects_every_invalid_github_organization_slug_without_io(string organization)
    {
        var sent = false;
        var store = new InMemoryCredentialStore();
        await store.SetAsync("github", "token", default);
        var adapter = new CopilotBillingUsageAdapter(
            new HttpClient(new Handler(_ => { sent = true; return Json(HttpStatusCode.OK, "{}"); })),
            store, organization, "user");

        var result = await adapter.FetchAsync("ignored", default);

        Assert.Equal(FetchStatus.Unsupported, result.Status);
        Assert.False(sent);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("acme")]
    [InlineData("acme-engineering")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void GitHub_organization_slug_accepts_valid_boundaries(string organization)
    {
        Assert.True(GitHubOrganizationSlug.IsValid(organization));
    }

    [Fact]
    public async Task Copilot_billing_usage_maps_http_failures_without_exposing_token_or_body()
    {
        var store = new InMemoryCredentialStore();
        await store.SetAsync("github", "secret-token", default);
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound, (HttpStatusCode)429, HttpStatusCode.InternalServerError })
        {
            var result = await new CopilotBillingUsageAdapter(new HttpClient(new Handler(_ => Json(status, "secret response body"))), store, "org", "user").FetchAsync("ignored", default);
            Assert.DoesNotContain("secret-token", result.Error ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain("secret response body", result.Error ?? "", StringComparison.Ordinal);
            Assert.Equal(status switch
            {
                HttpStatusCode.Unauthorized => FetchStatus.Unauthorized,
                HttpStatusCode.Forbidden => FetchStatus.Forbidden,
                HttpStatusCode.NotFound => FetchStatus.Unsupported,
                (HttpStatusCode)429 => FetchStatus.RateLimited,
                _ => FetchStatus.TransientFailure
            }, result.Status);
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

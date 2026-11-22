using System.Text;
using System.Net;
using System.Text.Json;
using QuotaSight.Collector;

namespace QuotaSight.Collector.Tests;

public sealed class TokscaleUsageQuotaContractTests
{
    [Fact]
    public void QuotaPreviewEmitsVersionedBasisPointContractAndPreservesFetchedAt()
    {
        const string input = """[{"provider":"Claude","freshness":{"fetched_at":1760000000,"stale":true},"metrics":[{"label":"Session","remaining_percent":57.5,"resets_at":"2026-11-23T00:00:00.729061+00:00"}]}]""";
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var stdout = new MemoryStream();
        using var stderr = new StringWriter();

        var exitCode = CollectorCli.Run(["--quota-preview", "--generation", "7"], stdin, stdout, stderr);

        Assert.Equal(0, exitCode);
        using var json = System.Text.Json.JsonDocument.Parse(stdout.ToArray());
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(7, root.GetProperty("generation").GetInt32());
        Assert.Equal("2025-10-09T08:53:20Z", root.GetProperty("providers")[0].GetProperty("observedAt").GetString());
        var window = root.GetProperty("providers")[0].GetProperty("windows")[0];
        Assert.Equal("session", window.GetProperty("window").GetString());
        Assert.Equal(5750, window.GetProperty("remainingBasisPoints").GetInt32());
        Assert.Equal("2026-11-23T00:00:00Z", window.GetProperty("resetsAt").GetString());
    }

    [Fact]
    public void QuotaPreviewSeparatesAllSevenProvidersAndRequiresGrokOperatorAttestation()
    {
        const string fixture = """
        [
          {"provider":"Claude","freshness":{"fetched_at":1760000000,"stale":false},"metrics":[{"label":"Session","remaining_percent":0,"resets_at":"2026-11-23T00:00:00Z"}]},
          {"provider":"Codex","freshness":null,"metrics":[{"label":"5h","remaining_percent":75,"resets_at":null}]},
          {"provider":"OpenCode Go","freshness":null,"metrics":[{"label":"Rolling","remaining_percent":57.5,"resets_at":null}]},
          {"provider":"Antigravity","account":{"id":"gemini-models","label":"Gemini Models","is_active":true},"freshness":null,"metrics":[{"label":"5h","remaining_percent":41.25,"resets_at":"2026-11-24T01:00:00Z"},{"label":"weekly","remaining_percent":100,"resets_at":null}]},
          {"provider":"Antigravity","account":{"id":"claude-and-gpt-models","label":"Claude and GPT Models","is_active":true},"freshness":null,"metrics":[{"label":"Weekly","remaining_percent":0,"resets_at":null}]},
          {"provider":"Antigravity","account":{"id":"unknown-group","label":"Private Group","is_active":true},"metrics":[{"label":"Weekly","remaining_percent":99,"resets_at":null}]},
          {"provider":"Grok","metrics":[{"label":"Weekly","remaining_percent":20,"resets_at":null},{"label":"Monthly","remaining_percent":0,"resets_at":null},{"label":"Credits","remaining_percent":100,"resets_at":null}]},
          {"provider":"Grok Bot","metrics":[{"label":"Weekly","remaining_percent":99,"resets_at":null}]},
          {"provider":"Copilot","freshness":null,"metrics":[{"label":"Premium","remaining_percent":100,"remaining_label":"unlimited","resets_at":null},{"label":"Chat","remaining_percent":0,"resets_at":"2026-11-23"},{"label":"Completions","remaining_percent":null,"resets_at":"2026-11-23T02:00:00Z"}]}
        ]
        """;
        var bytes = Encoding.UTF8.GetBytes(fixture);
        Assert.InRange(bytes.Length, 1, 4096);

        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();
        using var error = new StringWriter();
        var code = CollectorCli.Run(["--quota-preview", "--generation", "7", "--grok-build-first-party"], input, output, error);

        Assert.Equal(0, code);
        Assert.InRange(output.Length, 1, 4096);
        using var document = System.Text.Json.JsonDocument.Parse(output.ToArray());
        var providers = document.RootElement.GetProperty("providers");
        Assert.Equal(7, providers.GetArrayLength());
        var ids = providers.EnumerateArray().Select(p => p.GetProperty("provider").GetString()).ToArray();
        Assert.Equal(new[] { "claude", "codex", "opencode-go", "antigravity-gemini", "antigravity-claude-gpt", "grok-build", "copilot" }, ids);
        Assert.Equal("five-hour", providers[3].GetProperty("windows")[0].GetProperty("window").GetString());
        Assert.Equal("weekly", providers[3].GetProperty("windows")[1].GetProperty("window").GetString());
        Assert.Equal("available", providers[5].GetProperty("status").GetString());
        var premium = providers[6].GetProperty("windows")[0];
        Assert.Equal("premium", premium.GetProperty("window").GetString());
        Assert.Equal(JsonValueKind.Null, premium.GetProperty("remainingBasisPoints").ValueKind);
        Assert.True(premium.GetProperty("unlimited").GetBoolean());
        var chat = providers[6].GetProperty("windows")[1];
        Assert.Equal("chat", chat.GetProperty("window").GetString());
        Assert.Equal(0, chat.GetProperty("remainingBasisPoints").GetInt32());
        Assert.Equal(JsonValueKind.Null, chat.GetProperty("resetsAt").ValueKind);
        var completions = providers[6].GetProperty("windows")[2];
        Assert.Equal("completions", completions.GetProperty("window").GetString());
        Assert.Equal(JsonValueKind.Null, completions.GetProperty("remainingBasisPoints").ValueKind);
        Assert.Equal("2026-11-23T02:00:00Z", completions.GetProperty("resetsAt").GetString());
        Assert.False(completions.TryGetProperty("unlimited", out _));
        var outputText = Encoding.UTF8.GetString(output.ToArray());
        Assert.DoesNotContain("Private Group", outputText, StringComparison.Ordinal);
        Assert.DoesNotContain("Grok Bot", outputText, StringComparison.Ordinal);
    }

    [Fact]
    public void GrokIsUnavailableWithoutFirstPartyAttestationAndBotIsNeverIncluded()
    {
        const string fixture = """[{"provider":"Grok","metrics":[{"label":"Weekly","remaining_percent":0,"resets_at":null}]},{"provider":"Grok Bot","metrics":[{"label":"Weekly","remaining_percent":0,"resets_at":null}]}]""";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(fixture));
        using var output = new MemoryStream();
        using var error = new StringWriter();

        var code = CollectorCli.Run(["--quota-preview", "--generation", "7"], input, output, error);

        Assert.Equal(0, code);
        using var document = JsonDocument.Parse(output.ToArray());
        var providers = document.RootElement.GetProperty("providers");
        Assert.Single(providers.EnumerateArray());
        Assert.Equal("grok-build", providers[0].GetProperty("provider").GetString());
        Assert.Equal("unavailable", providers[0].GetProperty("status").GetString());
        Assert.Equal("unknown", providers[0].GetProperty("freshness").GetString());
        Assert.Empty(providers[0].GetProperty("windows").EnumerateArray());
    }

    [LinuxFact]
    public void GrokFirstPartyAttestationIsAcceptedByQuotaSend()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(0);
        var handler = new QuotaFakeHandler(HttpStatusCode.OK);
        var client = new HttpClient(handler);
        var context = new CollectorSendContext { HttpClient = client, Credentials = harness.Credentials, StateDirectory = () => harness.StateDirectory };
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("[{\"provider\":\"Grok\",\"metrics\":[{\"label\":\"Weekly\",\"remaining_percent\":25,\"resets_at\":null}]}]"));
        using var output = new MemoryStream();
        using var error = new StringWriter();

        var code = CollectorCli.Run(["--quota-send", "--endpoint", SendHarness.Endpoint, "--collector-id", SendHarness.CollectorId, "--generation", "7", "--grok-build-first-party"], input, output, error, context);

        Assert.Equal(0, code);
        Assert.Single(handler.Bodies);
        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("available", body.RootElement.GetProperty("providers")[0].GetProperty("status").GetString());
        Assert.Equal("weekly", body.RootElement.GetProperty("providers")[0].GetProperty("windows")[0].GetProperty("window").GetString());
        Assert.False(handler.Disposed);
        client.Dispose();
    }

    [Fact]
    public void AntigravityKnownDuplicateGroupIsUnknownAndMismatchedGroupIsOmitted()
    {
        const string fixture = """
        [
          {"provider":"Antigravity","account":{"id":"gemini-models","label":"Gemini Models","is_active":true},"metrics":[{"label":"weekly","remaining_percent":20,"resets_at":null}]},
          {"provider":"Antigravity","account":{"id":"gemini-models","label":"Gemini Models","is_active":true},"metrics":[{"label":"5h","remaining_percent":80,"resets_at":null}]},
          {"provider":"Antigravity","account":{"id":"claude-and-gpt-models","label":"Mismatched name","is_active":true},"metrics":[{"label":"weekly","remaining_percent":99,"resets_at":null}]}
        ]
        """;
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(fixture));
        using var output = new MemoryStream();
        using var error = new StringWriter();

        var code = CollectorCli.Run(["--quota-preview", "--generation", "7"], input, output, error);

        Assert.Equal(0, code);
        using var document = JsonDocument.Parse(output.ToArray());
        var providers = document.RootElement.GetProperty("providers");
        Assert.Single(providers.EnumerateArray());
        Assert.Equal("antigravity-gemini", providers[0].GetProperty("provider").GetString());
        Assert.Equal("unavailable", providers[0].GetProperty("status").GetString());
        Assert.Empty(providers[0].GetProperty("windows").EnumerateArray());
        Assert.DoesNotContain("Mismatched name", Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
    }

    [LinuxFact]
    public void QuotaSendRejectsStateMismatchBeforeReadingCredentialOrSending()
    {
        using var harness = SendHarness.Create();
        harness.WriteStateBytes(CollectorSendState.Create(SendHarness.CollectorId, SendHarness.Generation + 1, 0).ToCanonicalJson());
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("[]"));
        using var output = new MemoryStream();
        using var error = new StringWriter();
        var context = new CollectorSendContext { HttpClient = harness.Client, Credentials = harness.Credentials, StateDirectory = () => harness.StateDirectory };

        var code = CollectorCli.Run(["--quota-send", "--endpoint", SendHarness.Endpoint, "--collector-id", SendHarness.CollectorId, "--generation", "7"], input, output, error, context);

        Assert.Equal(CollectorExitCodes.StateMismatch, code);
        Assert.Equal(0, harness.Credentials.Reads);
        Assert.Empty(harness.Handler.Captured);
    }

    [Fact]
    public void QuotaSendRejectsUnknownDuplicateAndMixedModes()
    {
        foreach (var args in new[]
        {
            new[] { "--quota-preview", "--generation", "7", "--unexpected" },
            new[] { "--quota-send", "--generation", "7", "--generation", "8" },
            new[] { "--quota-preview", "--quota-send", "--generation", "7", "--endpoint", SendHarness.Endpoint, "--collector-id", SendHarness.CollectorId },
        })
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes("[]"));
            using var output = new MemoryStream();
            using var error = new StringWriter();
            Assert.Equal(CollectorExitCodes.Usage, CollectorCli.Run(args, input, output, error));
            Assert.Empty(output.ToArray());
        }
    }

    [LinuxFact]
    public void QuotaSendRetriesWithIdenticalBodyAndKeepsInjectedClientAlive()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(0);
        var stateBefore = harness.ReadStateBytes();
        var handler = new QuotaFakeHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var client = new HttpClient(handler);
        var context = new CollectorSendContext
        {
            HttpClient = client,
            Credentials = harness.Credentials,
            StateDirectory = () => harness.StateDirectory,
            Delay = _ => { },
            NextJitterUnit = () => 0,
        };
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("[]"));
        using var output = new MemoryStream();
        using var error = new StringWriter();

        var code = CollectorCli.Run(["--quota-send", "--endpoint", SendHarness.Endpoint, "--collector-id", SendHarness.CollectorId, "--generation", "7"], input, output, error, context);

        Assert.Equal(0, code);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
        Assert.Equal("PUT", handler.Method);
        Assert.Equal("/v1/collectors/col-test/quota", handler.Path);
        Assert.Equal("synthetic-id", handler.AccessId);
        Assert.Equal("synthetic-secret-value", handler.AccessSecret);
        Assert.Equal(1, harness.Credentials.Reads);
        Assert.Equal(0, output.Length);
        Assert.Equal(stateBefore, harness.ReadStateBytes());
        Assert.False(handler.Disposed);
        client.Dispose();
    }

    [LinuxFact]
    public void QuotaSendDoesNotFollowRedirectOrExposeResponseBody()
    {
        using var harness = SendHarness.Create();
        harness.WriteState(0);
        var handler = new QuotaFakeHandler(HttpStatusCode.Redirect) { ResponseBody = "synthetic-secret-response" };
        var client = new HttpClient(handler);
        var context = new CollectorSendContext { HttpClient = client, Credentials = harness.Credentials, StateDirectory = () => harness.StateDirectory };
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("[]"));
        using var output = new MemoryStream();
        using var error = new StringWriter();

        var code = CollectorCli.Run(["--quota-send", "--endpoint", SendHarness.Endpoint, "--collector-id", SendHarness.CollectorId, "--generation", "7"], input, output, error, context);

        Assert.Equal(CollectorExitCodes.SendRedirect, code);
        Assert.Single(handler.Bodies);
        Assert.DoesNotContain("synthetic-secret-response", error.ToString(), StringComparison.Ordinal);
        Assert.False(handler.Disposed);
        client.Dispose();
    }

    private sealed class QuotaFakeHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _statuses = new(statuses);
        public List<byte[]> Bodies { get; } = [];
        public string? Method { get; private set; }
        public string? Path { get; private set; }
        public string? AccessId { get; private set; }
        public string? AccessSecret { get; private set; }
        public string ResponseBody { get; init; } = string.Empty;
        public bool Disposed { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method.Method;
            Path = request.RequestUri!.AbsolutePath;
            AccessId = request.Headers.GetValues("CF-Access-Client-Id").Single();
            AccessSecret = request.Headers.GetValues("CF-Access-Client-Secret").Single();
            Bodies.Add(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            return new HttpResponseMessage(_statuses.Dequeue()) { Content = new StringContent(ResponseBody) };
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    [Fact]
    public void ReadsOnlyProviderSpecificContractWindowsAndOmitsPrivateFields()
    {
        const string json = """
            [
              {"provider":"Claude","account":{"id":"synthetic-a","label":"private label","is_active":true},"email":"private@example.test","plan":"private plan","credential_source":"private source","freshness":{"fetched_at":1760000000,"stale":false},"metrics":[
                {"label":"Session","remaining_percent":0,"remaining_label":"private free text","resets_at":"2026-11-23T00:00:00Z","used_percent":100},
                {"label":"Weekly","remaining_percent":100,"resets_at":null},
                {"label":"Fable","remaining_percent":13.5,"resets_at":null}
              ]},
              {"provider":"Codex","account":null,"freshness":null,"metrics":[
                {"label":"5h","remaining_percent":0,"resets_at":"2026-11-23T00:00:00Z"},
                {"label":"Weekly","remaining_percent":100,"resets_at":null},
                {"label":"OpenAI API","remaining_percent":42,"resets_at":null}
              ]},
              {"provider":"OpenCode Go","account":null,"plan":"Go","metrics":[
                {"label":"Rolling","remaining_percent":57.5,"remaining_label":"rate-limited","resets_at":null},
                {"label":"Weekly","remaining_percent":50,"remaining_label":"free-text must not escape","resets_at":"2026-11-24T00:00:00Z"},
                {"label":"Monthly","remaining_percent":100,"resets_at":null}
              ]},
              {"provider":"Antigravity","metrics":[{"label":"Rolling","remaining_percent":99,"resets_at":null}]}
            ]
            """;

        var result = TokscaleUsageQuotaContract.Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal(TokscaleUsageQuotaParseStatus.Success, result.Status);
        Assert.Collection(result.Providers,
            claude =>
            {
                Assert.Equal("Claude", claude.Provider);
                Assert.Equal(TokscaleUsageQuotaFreshness.Fresh, claude.Freshness);
                Assert.Collection(claude.Windows,
                    session => AssertWindow(session, "Session", 0, "2026-11-23T00:00:00Z"),
                    weekly => AssertWindow(weekly, "Weekly", 100, null));
            },
            codex =>
            {
                Assert.Equal("Codex", codex.Provider);
                Assert.Equal(TokscaleUsageQuotaFreshness.Unknown, codex.Freshness);
                Assert.Collection(codex.Windows,
                    fiveHours => AssertWindow(fiveHours, "5h", 0, "2026-11-23T00:00:00Z"),
                    weekly => AssertWindow(weekly, "Weekly", 100, null));
            },
            go =>
            {
                Assert.Equal("OpenCode Go", go.Provider);
                Assert.Collection(go.Windows,
                    rolling => AssertWindow(rolling, "Rolling", 57.5, null),
                    weekly => AssertWindow(weekly, "Weekly", 50, "2026-11-24T00:00:00Z"),
                    monthly => AssertWindow(monthly, "Monthly", 100, null));
            });
        var projection = string.Join('|', result.Providers.SelectMany(p => p.Windows.SelectMany(w => new[] { p.Provider, w.Label, w.ResetAt ?? "" })));
        Assert.DoesNotContain("private", projection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.test", projection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fable", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAI API", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("Antigravity", projection, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservesStaleZeroAndHundredAsDistinctFromUnknown()
    {
        const string json = """
            [
              {"provider":"Claude","freshness":{"fetched_at":1760000000,"stale":true},"metrics":[{"label":"Session","remaining_percent":0,"resets_at":"not a date"},{"label":"Weekly","remaining_percent":100,"resets_at":null}]},
              {"provider":"Codex","metrics":[{"label":"Weekly","remaining_percent":null,"resets_at":null}]}
            ]
            """;

        var result = TokscaleUsageQuotaContract.Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal(TokscaleUsageQuotaParseStatus.Success, result.Status);
        Assert.Equal(TokscaleUsageQuotaFreshness.Stale, result.Providers[0].Freshness);
        Assert.Equal(0, result.Providers[0].Windows[0].RemainingPercent);
        Assert.Null(result.Providers[0].Windows[0].ResetAt);
        Assert.Equal(100, result.Providers[0].Windows[1].RemainingPercent);
        Assert.Equal(TokscaleUsageQuotaValueStatus.Unknown, result.Providers[1].Windows[0].ValueStatus);
        Assert.Null(result.Providers[1].Windows[0].RemainingPercent);
    }

    [Fact]
    public void EmptyArrayIsAValidEmptySuccess()
    {
        var result = TokscaleUsageQuotaContract.Parse("[]"u8);

        Assert.Equal(TokscaleUsageQuotaParseStatus.Success, result.Status);
        Assert.Empty(result.Providers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[{")]
    [InlineData("null")]
    public void MalformedOrWrongRootIsRejected(string json)
    {
        var result = TokscaleUsageQuotaContract.Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal(TokscaleUsageQuotaParseStatus.Malformed, result.Status);
        Assert.Empty(result.Providers);
    }

    [Fact]
    public void MultipleAccountsRequireExactlyOneExplicitActiveAccount()
    {
        const string json = """
            [
              {"provider":"Codex","account":{"id":"one","is_active":false},"metrics":[{"label":"Weekly","remaining_percent":10}]},
              {"provider":"Codex","account":{"id":"two","is_active":true},"metrics":[{"label":"Weekly","remaining_percent":20}]}
            ]
            """;

        var result = TokscaleUsageQuotaContract.Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal(TokscaleUsageQuotaParseStatus.Success, result.Status);
        var provider = Assert.Single(result.Providers);
        Assert.Equal("Codex", provider.Provider);
        Assert.Equal(20, Assert.Single(provider.Windows).RemainingPercent);
    }

    [Fact]
    public void AmbiguousAccountsAreUnavailableInsteadOfSummedOrGuessed()
    {
        const string json = """
            [
              {"provider":"Codex","account":{"id":"one","is_active":false},"metrics":[{"label":"Weekly","remaining_percent":10}]},
              {"provider":"Codex","account":{"id":"two","is_active":false},"metrics":[{"label":"Weekly","remaining_percent":20}]}
            ]
            """;

        var result = TokscaleUsageQuotaContract.Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal(TokscaleUsageQuotaParseStatus.Success, result.Status);
        var provider = Assert.Single(result.Providers);
        Assert.Equal(TokscaleUsageQuotaProviderStatus.Unavailable, provider.Status);
        Assert.Empty(provider.Windows);
    }

    [Fact]
    public void RejectsOutOfRangePercentAndOversizedBody()
    {
        var outOfRange = TokscaleUsageQuotaContract.Parse("""[{"provider":"Codex","metrics":[{"label":"Weekly","remaining_percent":100.1}]}]"""u8);
        var oversized = TokscaleUsageQuotaContract.Parse(new byte[TokscaleUsageQuotaContract.MaxInputBytes + 1]);

        Assert.Equal(TokscaleUsageQuotaParseStatus.Malformed, outOfRange.Status);
        Assert.Equal(TokscaleUsageQuotaParseStatus.Malformed, oversized.Status);
    }

    private static void AssertWindow(TokscaleUsageQuotaWindow window, string label, double? remaining, string? reset)
    {
        Assert.Equal(label, window.Label);
        Assert.Equal(remaining, window.RemainingPercent);
        Assert.Equal(remaining is null ? TokscaleUsageQuotaValueStatus.Unknown : TokscaleUsageQuotaValueStatus.Known, window.ValueStatus);
        Assert.Equal(reset, window.ResetAt);
    }
}

using System.Globalization;
using System.Net;
using System.Text.Json;

namespace QuotaSight.Collector;

internal static class TokscaleQuotaCli
{
    private const int MaxWireBytes = 4096;
    private const int MaxAttempts = 3;

    public static int Run(string[] args, Stream stdin, Stream stdout, TextWriter stderr, CollectorSendContext? send)
    {
        if (!TryOptions(args, out var options)) return Usage(stderr, "quota_usage: invalid or duplicate option");
        var preview = options.ContainsKey("--quota-preview");
        var sending = options.ContainsKey("--quota-send");
        if (preview == sending) return Usage(stderr, "quota_usage: select exactly one quota mode");
        if (!options.TryGetValue("--generation", out var generationText)
            || !long.TryParse(generationText, NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            || generation is < 1 or > TokscaleGraphContract.MaxSafeInteger)
            return Usage(stderr, "quota_usage: --generation must be a positive integer");

        options.TryGetValue("--endpoint", out var endpointText);
        options.TryGetValue("--collector-id", out var collectorId);
        if (sending && (endpointText is null || collectorId is null || !CollectorSendState.IsCollectorId(collectorId)))
            return Usage(stderr, "quota_usage: invalid send configuration");
        if (preview && (endpointText is not null || collectorId is not null))
            return Usage(stderr, "quota_usage: send options require --quota-send");
        Uri? origin = null;
        if (sending && (!Uri.TryCreate(endpointText, UriKind.Absolute, out origin) || origin.Scheme != Uri.UriSchemeHttps
            || origin.AbsolutePath is not ("" or "/") || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0))
            return Usage(stderr, "quota_usage: endpoint must be an HTTPS origin");

        // TokScale does not encode whether a Grok row came from official xAI or a custom proxy.
        // This opt-in records the operator's assertion only; it is not automatic verification.
        var grokFirstParty = options.ContainsKey("--grok-build-first-party");
        var input = ReadBounded(stdin, TokscaleUsageQuotaContract.MaxInputBytes);
        if (input is null) return Error(stderr, "quota_input: input exceeds limit");
        var parsed = TokscaleUsageQuotaContract.Parse(input, grokFirstParty);
        if (parsed.Status != TokscaleUsageQuotaParseStatus.Success) return Error(stderr, "quota_input: invalid input");
        var body = Serialize(generation, DateTimeOffset.UtcNow, parsed);
        if (body.Length > MaxWireBytes) return Error(stderr, "quota_input: contract body exceeds limit");
        if (preview) { stdout.Write(body); stdout.Flush(); return 0; }
        return Send(origin!, collectorId!, generation, body, send, stderr);
    }

    private static bool TryOptions(string[] args, out Dictionary<string, string?> options)
    {
        options = new Dictionary<string, string?>(StringComparer.Ordinal);
        var valueFlags = new HashSet<string>(["--generation", "--endpoint", "--collector-id"], StringComparer.Ordinal);
        var modeFlags = new HashSet<string>(["--quota-preview", "--quota-send"], StringComparer.Ordinal);
        var booleanFlags = new HashSet<string>(["--grok-build-first-party"], StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            var eq = raw.IndexOf('=');
            var name = eq < 0 ? raw : raw[..eq];
            if (options.ContainsKey(name)) return false;
            if (modeFlags.Contains(name))
            {
                if (eq >= 0) return false;
                options.Add(name, null);
                continue;
            }
            if (booleanFlags.Contains(name))
            {
                if (eq >= 0) return false;
                options.Add(name, null);
                continue;
            }
            if (!valueFlags.Contains(name)) return false;
            string value;
            if (eq >= 0) value = raw[(eq + 1)..];
            else
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) return false;
                value = args[i];
            }
            if (string.IsNullOrWhiteSpace(value)) return false;
            options.Add(name, value);
        }
        return true;
    }

    private static byte[] Serialize(long generation, DateTimeOffset collectedAt, TokscaleUsageQuotaParseResult parsed)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteNumber("generation", generation);
            writer.WriteString("collectedAt", collectedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            writer.WriteStartArray("providers");
            foreach (var provider in parsed.Providers)
            {
                writer.WriteStartObject();
                writer.WriteString("provider", provider.ProviderId ?? provider.Provider switch { "Claude" => "claude", "Codex" => "codex", _ => "opencode-go" });
                writer.WriteString("status", provider.Status == TokscaleUsageQuotaProviderStatus.Available ? "available" : "unavailable");
                writer.WriteString("freshness", provider.Freshness switch { TokscaleUsageQuotaFreshness.Fresh => "fresh", TokscaleUsageQuotaFreshness.Stale => "cached", _ => "unknown" });
                if (provider.ObservedAt is { } observed) writer.WriteString("observedAt", new DateTimeOffset(DateTime.SpecifyKind(observed, DateTimeKind.Utc)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                else writer.WriteNull("observedAt");
                writer.WriteStartArray("windows");
                foreach (var window in provider.Windows)
                {
                    writer.WriteStartObject();
                    writer.WriteString("window", (provider.ProviderId, window.Label) switch
                    {
                        (_, "Session") => "session",
                        (_, "5h") => "five-hour",
                        (_, "Rolling") => "rolling",
                        (_, "weekly") => "weekly",
                        (_, "Weekly") => "weekly",
                        (_, "Monthly") => "monthly",
                        ("copilot", "Premium") => "premium",
                        ("copilot", "Chat") => "chat",
                        ("copilot", "Completions") => "completions",
                        _ => throw new InvalidOperationException("Unmapped quota window."),
                    });
                    if (window.RemainingPercent is { } pct) writer.WriteNumber("remainingBasisPoints", (int)Math.Round(pct * 100, MidpointRounding.AwayFromZero)); else writer.WriteNull("remainingBasisPoints");
                    if (window.Unlimited) writer.WriteBoolean("unlimited", true);
                    if (window.ResetAt is { } reset) writer.WriteString("resetsAt", reset); else writer.WriteNull("resetsAt");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static int Send(Uri origin, string id, long generation, byte[] body, CollectorSendContext? context, TextWriter stderr)
    {
        if (!OperatingSystem.IsLinux() || !SystemdStateDirectory.IsSupportedPlatform)
            return StateFailure(stderr, CollectorStateFailure.UnsupportedPlatform);
        if (!CollectorStateStore.TryOpen(new SystemdStateDirectory(static () => true, context?.StateDirectory ?? SystemdStateDirectory.FromEnvironment), out var store, out var openFailure))
            return StateFailure(stderr, openFailure);
        using (store)
        {
            if (!store!.TryLock(out var lease)) return StateFailure(stderr, CollectorStateFailure.StateLocked);
            using (lease)
            {
                var state = store.Read(out var readFailure);
                if (state is null) return StateFailure(stderr, readFailure);
                if (!state.Matches(id, generation)) return StateMismatch(stderr);
                if (state.IsBlocked) return Error(stderr, "state_blocked: resolve the existing send conflict first", CollectorExitCodes.StateBlocked);
            }
        }

        var credentials = (context?.Credentials ?? new SystemdLoadCredentialSource()).Read();
        if (!credentials.Succeeded || credentials.Credential is null) return Error(stderr, "credential_unavailable", CollectorExitCodes.CredentialUnavailable);
        var client = context?.HttpClient;
        var ownsClient = client is null;
        client ??= CollectorSnapshotTransport.CreateClient();
        try
        {
            if (CollectorSnapshotTransport.HasForbiddenDefaultHeader(client)) return Usage(stderr, "quota_send: invalid client configuration");
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(origin, $"/v1/collectors/{id}/quota"));
                    request.Content = new ByteArrayContent(body);
                    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    request.Headers.TryAddWithoutValidation("CF-Access-Client-Id", credentials.Credential.ClientId);
                    request.Headers.TryAddWithoutValidation("CF-Access-Client-Secret", credentials.Credential.ClientSecret);
                    using var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                    if ((int)response.StatusCode is >= 300 and < 400) return Error(stderr, "quota_send: redirect refused", CollectorExitCodes.SendRedirect);
                    if ((int)response.StatusCode >= 500)
                    {
                        if (attempt < MaxAttempts) { (context?.Delay ?? Thread.Sleep)(CollectorSnapshotTransport.Backoff(attempt, context?.NextJitterUnit?.Invoke() ?? 0d)); continue; }
                        return Error(stderr, "quota_send: server unavailable", CollectorExitCodes.SendUnavailable);
                    }
                    if (!response.IsSuccessStatusCode) return Error(stderr, "quota_send: request rejected", CollectorExitCodes.SendRejected);
                    return 0;
                }
                catch (TaskCanceledException)
                {
                    if (attempt < MaxAttempts) { (context?.Delay ?? Thread.Sleep)(CollectorSnapshotTransport.Backoff(attempt, context?.NextJitterUnit?.Invoke() ?? 0d)); continue; }
                    return Error(stderr, "quota_send: timeout", CollectorExitCodes.SendTimeout);
                }
                catch (HttpRequestException)
                {
                    if (attempt < MaxAttempts) { (context?.Delay ?? Thread.Sleep)(CollectorSnapshotTransport.Backoff(attempt, context?.NextJitterUnit?.Invoke() ?? 0d)); continue; }
                    return Error(stderr, "quota_send: transport failure", CollectorExitCodes.SendTransport);
                }
            }
            return Error(stderr, "quota_send: transport failure", CollectorExitCodes.SendTransport);
        }
        finally { if (ownsClient) client.Dispose(); }
    }

    private static byte[]? ReadBounded(Stream input, int max)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true) { var n = input.Read(buffer); if (n == 0) break; if (output.Length + n > max) return null; output.Write(buffer, 0, n); }
        return output.ToArray();
    }

    private static int StateMismatch(TextWriter stderr) => Error(stderr, "state_mismatch: registered collector or generation differs", CollectorExitCodes.StateMismatch);
    private static int StateFailure(TextWriter stderr, CollectorStateFailure failure) => Error(stderr, "state_unavailable: " + CollectorStateFailureText.Describe(failure), CollectorExitCodes.StateUnavailable);
    private static int Usage(TextWriter stderr, string message) => Error(stderr, message, CollectorExitCodes.Usage);
    private static int Error(TextWriter stderr, string message, int code = CollectorExitCodes.Rejected) { stderr.WriteLine(message); return code; }
}

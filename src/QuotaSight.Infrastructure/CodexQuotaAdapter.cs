using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaSight.Application;
using QuotaSight.Core;

namespace QuotaSight.Infrastructure;

public sealed record CodexQuotaWindowDto([property: JsonPropertyName("used_percent")] decimal? UsedPercent, [property: JsonPropertyName("reset_at")] long? ResetAt, [property: JsonPropertyName("limit_window_seconds")] long? LimitWindowSeconds);
public sealed record CodexRateLimitDto([property: JsonPropertyName("primary_window")] CodexQuotaWindowDto? PrimaryWindow, [property: JsonPropertyName("secondary_window")] CodexQuotaWindowDto? SecondaryWindow);
public sealed record CodexUsageDto([property: JsonPropertyName("plan_type")] string? PlanType, [property: JsonPropertyName("rate_limit")] CodexRateLimitDto? RateLimit);
[JsonSerializable(typeof(CodexUsageDto))]
internal partial class CodexQuotaJsonContext : JsonSerializerContext;

public sealed class CodexQuotaAdapter(HttpClient client, TimeProvider? clock = null) : IQuotaAdapter
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    public ProviderKind Provider => ProviderKind.ChatGpt;
    public ValueTask<FetchResult<IReadOnlyList<QuotaSnapshot>>> FetchAsync(string account, CancellationToken ct) => FetchTokenAsync(account, account, ct);
    public async ValueTask<FetchResult<IReadOnlyList<QuotaSnapshot>>> FetchTokenAsync(string accessToken, string account, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken); request.Headers.Accept.ParseAdd("application/json"); request.Headers.UserAgent.ParseAdd("codex-cli");
            if (TryGetAccountId(accessToken, out var accountId)) request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(FetchStatus.Unauthorized);
            if (response.StatusCode == HttpStatusCode.Forbidden) return new(FetchStatus.Forbidden);
            if ((int)response.StatusCode == 429) return new(FetchStatus.RateLimited, RetryAfter: RetryAfter(response));
            if ((int)response.StatusCode >= 500 || !response.IsSuccessStatusCode) return new(FetchStatus.TransientFailure);
            var dto = await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(ct), CodexQuotaJsonContext.Default.CodexUsageDto, ct);
            if (dto?.RateLimit is null) return new(FetchStatus.TransientFailure);
            var now = clock.GetUtcNow(); var snapshots = new List<QuotaSnapshot>();
            var primaryValid = Add(snapshots, dto.RateLimit.PrimaryWindow, "primary", account, now); var secondaryValid = Add(snapshots, dto.RateLimit.SecondaryWindow, "secondary", account, now);
            if (!primaryValid && !secondaryValid) return new(FetchStatus.TransientFailure);
            return FetchResult<IReadOnlyList<QuotaSnapshot>>.Success(snapshots);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(FetchStatus.TransientFailure); }
        catch (HttpRequestException) { return new(FetchStatus.TransientFailure); }
        catch (JsonException) { return new(FetchStatus.TransientFailure); }
        catch (ArgumentOutOfRangeException) { return new(FetchStatus.TransientFailure); }
    }
    private static bool Add(List<QuotaSnapshot> output, CodexQuotaWindowDto? dto, string name, string account, DateTimeOffset now)
    {
        if (dto is null) return false;
        if (dto.UsedPercent is null || dto.UsedPercent < 0 || dto.ResetAt is null || dto.LimitWindowSeconds is null || dto.LimitWindowSeconds <= 0) return false;
        try
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(dto.ResetAt.Value); var duration = TimeSpan.FromSeconds(dto.LimitWindowSeconds.Value); var start = reset.Subtract(duration); var minutes = dto.LimitWindowSeconds.Value / 60m;
            var kind = Math.Abs(minutes - 300m) < 1m ? QuotaWindowKind.Rolling : Math.Abs(minutes - 10080m) < 1m ? QuotaWindowKind.Weekly : QuotaWindowKind.Custom;
            output.Add(new(ProviderKind.ChatGpt, account, $"Codex {name}", new(kind, start, reset, duration, reset), null, null, dto.UsedPercent, "percent", now, now, QuotaSource.Experimental, QuotaConfidence.Low, now.AddMinutes(10), "ChatGPT Codex")); return true;
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }
    private static bool TryGetAccountId(string token, out string value)
    {
        value = "";
        try
        {
            var parts = token.Split('.'); if (parts.Length < 2) return false;
            var segment = parts[1]; var raw = Convert.FromBase64String(segment.Replace('-', '+').Replace('_', '/') + new string('=', (4 - segment.Length % 4) % 4)); using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("https://api.openai.com/auth", out var auth) || auth.ValueKind != JsonValueKind.Object || !auth.TryGetProperty("chatgpt_account_id", out var id) || id.ValueKind != JsonValueKind.String) return false;
            value = id.GetString() ?? ""; return !string.IsNullOrWhiteSpace(value);
        }
        catch { return false; }
    }
    private TimeSpan? RetryAfter(HttpResponseMessage response) => response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date ? date - clock.GetUtcNow() : null);
}

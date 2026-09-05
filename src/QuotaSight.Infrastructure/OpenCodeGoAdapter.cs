using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaSight.Application;
using QuotaSight.Core;

namespace QuotaSight.Infrastructure;

public sealed record OpenCodeUsageWindow(
    [property: JsonPropertyName("used")] decimal? Used,
    [property: JsonPropertyName("limit")] decimal? Limit,
    [property: JsonPropertyName("reportedPercent")] decimal? ReportedPercent,
    [property: JsonPropertyName("remaining")] decimal? Remaining,
    [property: JsonPropertyName("unit")] string? Unit,
    [property: JsonPropertyName("resetAt")] DateTimeOffset? ResetAt);

public sealed record OpenCodeUsageResponse(
    [property: JsonPropertyName("rollingUsage")] OpenCodeUsageWindow? RollingUsage,
    [property: JsonPropertyName("weeklyUsage")] OpenCodeUsageWindow? WeeklyUsage,
    [property: JsonPropertyName("monthlyUsage")] OpenCodeUsageWindow? MonthlyUsage);

[JsonSerializable(typeof(OpenCodeUsageResponse))]
internal partial class OpenCodeJsonContext : JsonSerializerContext;

public sealed class OpenCodeGoAdapter : IQuotaAdapter
{
    private readonly HttpClient client;
    private readonly string apiKey;
    private readonly Uri endpoint;
    private readonly TimeProvider timeProvider;

    public OpenCodeGoAdapter(HttpClient client, string apiKey)
        : this(client, apiKey, new Uri("https://opencode.ai/zen/go/v1/usage"), TimeProvider.System) { }

    public OpenCodeGoAdapter(HttpClient client, string apiKey, Uri endpoint, TimeProvider timeProvider)
    {
        this.client = client;
        this.apiKey = apiKey;
        this.endpoint = endpoint;
        this.timeProvider = timeProvider;
    }

    public ProviderKind Provider => ProviderKind.OpenCode;

    public async ValueTask<FetchResult<IReadOnlyList<QuotaSnapshot>>> FetchAsync(string account, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(FetchStatus.Unsupported);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return new(FetchStatus.Unauthorized);
        if (response.StatusCode == HttpStatusCode.Forbidden) return new(FetchStatus.Forbidden);
        if ((int)response.StatusCode == 429) return new(FetchStatus.RateLimited, RetryAfter: GetRetryAfter(response));
        if (!response.IsSuccessStatusCode) return new(FetchStatus.TransientFailure);

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var dto = await JsonSerializer.DeserializeAsync(stream, OpenCodeJsonContext.Default.OpenCodeUsageResponse, cancellationToken);
            if (dto is null) return new(FetchStatus.TransientFailure, Error: "Empty usage response.");
            var now = timeProvider.GetUtcNow();
            var snapshots = new List<QuotaSnapshot>();
            AddSnapshot(snapshots, account, "rolling", dto.RollingUsage, now, QuotaWindowKind.Rolling);
            AddSnapshot(snapshots, account, "weekly", dto.WeeklyUsage, now, QuotaWindowKind.Weekly);
            AddSnapshot(snapshots, account, "monthly", dto.MonthlyUsage, now, QuotaWindowKind.Monthly);
            return snapshots.Count == 0 ? new(FetchStatus.TransientFailure, Error: "Usage response contained no windows.") : FetchResult<IReadOnlyList<QuotaSnapshot>>.Success(snapshots);
        }
        catch (JsonException exception)
        {
            return new(FetchStatus.TransientFailure, Error: exception.Message);
        }
    }

    private void AddSnapshot(List<QuotaSnapshot> snapshots, string account, string metric, OpenCodeUsageWindow? usage, DateTimeOffset now, QuotaWindowKind kind)
    {
        if (usage is null) return;
        var freshUntil = now.AddMinutes(10);
        snapshots.Add(new(Provider, account, metric, new(kind, now, usage.ResetAt ?? DateTimeOffset.MaxValue, ResetAt: usage.ResetAt), usage.Used, usage.Limit,
            usage.ReportedPercent, usage.Unit ?? "requests", now, now, QuotaSource.Official, QuotaConfidence.Official, freshUntil, account));
    }

    private TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta) return delta;
        if (retry?.Date is { } date) return date - timeProvider.GetUtcNow();
        return null;
    }
}

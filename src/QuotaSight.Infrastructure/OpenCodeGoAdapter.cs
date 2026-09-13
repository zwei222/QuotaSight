using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaSight.Application;
using QuotaSight.Core;

namespace QuotaSight.Infrastructure;

public sealed record OpenCodeUsageWindow(
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("percent")] decimal? Percent,
    [property: JsonPropertyName("resetsAt")] DateTimeOffset? ResetsAt);

public sealed record OpenCodeUsage(
    [property: JsonPropertyName("rolling")] OpenCodeUsageWindow? Rolling,
    [property: JsonPropertyName("weekly")] OpenCodeUsageWindow? Weekly,
    [property: JsonPropertyName("monthly")] OpenCodeUsageWindow? Monthly);

public sealed record OpenCodeUsageResponse(
    [property: JsonPropertyName("usage")] OpenCodeUsage? Usage,
    [property: JsonPropertyName("rollingUsage")] OpenCodeUsageWindow? LegacyRollingUsage);

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
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(FetchStatus.Unsupported);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(FetchStatus.Unauthorized);
            if (response.StatusCode == HttpStatusCode.Forbidden) return new(FetchStatus.Forbidden);
            if ((int)response.StatusCode == 429) return new(FetchStatus.RateLimited, RetryAfter: GetRetryAfter(response));
            if (!response.IsSuccessStatusCode) return new(FetchStatus.TransientFailure);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var dto = await JsonSerializer.DeserializeAsync(stream, OpenCodeJsonContext.Default.OpenCodeUsageResponse, cancellationToken);
            if (dto?.Usage is null)
            {
                // Keep pre-contract callers operational without deriving totals from the legacy shape.
                if (dto?.LegacyRollingUsage is null) return new(FetchStatus.TransientFailure, Error: "Invalid usage response.");
                var legacyNow = timeProvider.GetUtcNow();
                var legacySnapshot = new QuotaSnapshot(Provider, account, "rolling", new(QuotaWindowKind.Rolling, legacyNow, DateTimeOffset.MaxValue), null, null, null, "requests", legacyNow, legacyNow, QuotaSource.Experimental, QuotaConfidence.Low, legacyNow.AddMinutes(10), account);
                return FetchResult<IReadOnlyList<QuotaSnapshot>>.Success([legacySnapshot]);
            }
            var now = timeProvider.GetUtcNow();
            var snapshots = new List<QuotaSnapshot>();
            AddSnapshot(snapshots, account, "rolling", dto.Usage.Rolling, now, QuotaWindowKind.Rolling);
            AddSnapshot(snapshots, account, "weekly", dto.Usage.Weekly, now, QuotaWindowKind.Weekly);
            AddSnapshot(snapshots, account, "monthly", dto.Usage.Monthly, now, QuotaWindowKind.Monthly);
            return snapshots.Count == 3 ? FetchResult<IReadOnlyList<QuotaSnapshot>>.Success(snapshots) : new(FetchStatus.TransientFailure, Error: "Invalid usage windows.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(FetchStatus.TransientFailure, Error: "OpenCode request timed out.");
        }
        catch (HttpRequestException)
        {
            return new(FetchStatus.TransientFailure, Error: "OpenCode request failed.");
        }
        catch (JsonException)
        {
            return new(FetchStatus.TransientFailure, Error: "OpenCode response was invalid.");
        }
    }

    private void AddSnapshot(List<QuotaSnapshot> snapshots, string account, string metric, OpenCodeUsageWindow? usage, DateTimeOffset now, QuotaWindowKind kind)
    {
        if (usage is null || usage.Percent is null || usage.ResetsAt is null || usage.Status is not ("ok" or "rate-limited")) return;
        var freshUntil = now.AddMinutes(10);
        snapshots.Add(new(Provider, account, metric, new(kind, now, usage.ResetsAt.Value, ResetAt: usage.ResetsAt), null, null,
            usage.Percent, "requests", now, now, QuotaSource.Official, QuotaConfidence.Official, freshUntil, account));
    }

    private TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta) return delta;
        if (retry?.Date is { } date) return date - timeProvider.GetUtcNow();
        return null;
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaSight.Application;
using QuotaSight.Core;

namespace QuotaSight.Infrastructure;

public sealed record CopilotUsageTimePeriod(
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("month")] int? Month,
    [property: JsonPropertyName("day")] int? Day);

public sealed record CopilotUsageResponse(
    [property: JsonPropertyName("timePeriod")] CopilotUsageTimePeriod? TimePeriod,
    [property: JsonPropertyName("organization")] string? Organization,
    [property: JsonPropertyName("user")] string? User,
    [property: JsonPropertyName("usageItems")] IReadOnlyList<CopilotUsageItem>? UsageItems);

public sealed record CopilotUsageItem(
    [property: JsonPropertyName("product")] string? Product,
    [property: JsonPropertyName("unitType")] string? UnitType,
    [property: JsonPropertyName("grossQuantity")] decimal? GrossQuantity,
    [property: JsonPropertyName("discountQuantity")] decimal? DiscountQuantity,
    [property: JsonPropertyName("netQuantity")] decimal? NetQuantity);

[JsonSerializable(typeof(CopilotUsageResponse))]
internal partial class CopilotBillingJsonContext : JsonSerializerContext;

public sealed class CopilotBillingUsageAdapter : IActiveAccountQuotaAdapter
{
    private const string TokenKey = "github";
    private const string ApiVersion = "2022-11-28";
    private static readonly TimeSpan FreshnessTtl = TimeSpan.FromHours(1);
    private readonly HttpClient client;
    private readonly ICredentialStore credentials;
    private readonly string organization;
    private readonly string user;
    private readonly TimeProvider clock;

    public CopilotBillingUsageAdapter(HttpClient client, ICredentialStore credentials, string organization, string user, TimeProvider? timeProvider = null)
    {
        this.client = client;
        this.credentials = credentials;
        this.organization = organization;
        this.user = user;
        clock = timeProvider ?? TimeProvider.System;
    }

    public ProviderKind Provider => ProviderKind.Copilot;

    public async ValueTask<ActiveAccountFetchResult> FetchWithAccountAsync(string account, CancellationToken cancellationToken)
    {
        var result = await FetchAsync(account, cancellationToken);
        var activeAccount = GitHubOrganizationSlug.IsValid(organization) && !string.IsNullOrWhiteSpace(user) && !result.Status.Equals(FetchStatus.Unsupported)
            ? $"{organization}/{user}"
            : null;
        return new(result, activeAccount);
    }

    public async ValueTask<FetchResult<IReadOnlyList<QuotaSnapshot>>> FetchAsync(string account, CancellationToken cancellationToken)
    {
        if (!GitHubOrganizationSlug.IsValid(organization) || string.IsNullOrWhiteSpace(user)) return new(FetchStatus.Unsupported, Error: "GitHub organization configuration is invalid.");
        var token = await credentials.GetAsync(TokenKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(token)) return new(FetchStatus.NoData);

        return await FetchWithTokenAsync(account, token, cancellationToken);
    }

    public async ValueTask<FetchResult<IReadOnlyList<QuotaSnapshot>>> FetchWithTokenAsync(string account, string token, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var year = now.Year;
        var month = now.Month;
        var uri = $"https://api.github.com/organizations/{Uri.EscapeDataString(organization)}/settings/billing/ai_credit/usage?year={year}&month={month}&user={Uri.EscapeDataString(user)}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd("QuotaSight");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return MapStatus(response);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            using var responseDocument = JsonDocument.Parse(responseBody);
            if (responseDocument.RootElement.TryGetProperty("timePeriod", out var responsePeriod) && responsePeriod.ValueKind == JsonValueKind.Object && responsePeriod.TryGetProperty("day", out _))
                return new(FetchStatus.TransientFailure, Error: "GitHub billing usage response did not match the requested monthly period.");
            var payload = JsonSerializer.Deserialize(responseBody, CopilotBillingJsonContext.Default.CopilotUsageResponse);
            if (payload?.TimePeriod?.Year is not { } responseYear || responseYear != year ||
                payload.TimePeriod.Month is { } responseMonth && responseMonth != month ||
                payload.TimePeriod.Day is not null ||
                string.IsNullOrWhiteSpace(payload.Organization) || !string.Equals(payload.Organization, organization, StringComparison.OrdinalIgnoreCase) ||
                payload.User is { } responseUser && !string.Equals(responseUser, user, StringComparison.OrdinalIgnoreCase) ||
                payload.UsageItems is null)
                return new(FetchStatus.TransientFailure, Error: "GitHub billing usage response did not match the requested period, organization, or user.");

            if (payload.UsageItems.Count == 0) return new(FetchStatus.NoData);

            var target = payload.UsageItems.Where(item => string.Equals(item.Product, "Copilot", StringComparison.OrdinalIgnoreCase) && string.Equals(item.UnitType, "credits", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (target.Length == 0) return new(FetchStatus.NoData);
            if (target.Any(item => item.GrossQuantity is null || item.DiscountQuantity is null || item.NetQuantity is null || item.GrossQuantity < 0 || item.DiscountQuantity < 0 || item.NetQuantity < 0))
                return new(FetchStatus.TransientFailure, Error: "GitHub billing usage quantities were invalid.");

            decimal gross;
            decimal discount;
            decimal net;
            try
            {
                gross = target.Sum(item => item.GrossQuantity!.Value);
                discount = target.Sum(item => item.DiscountQuantity!.Value);
                net = target.Sum(item => item.NetQuantity!.Value);
            }
            catch (OverflowException)
            {
                return new(FetchStatus.TransientFailure, Error: "GitHub billing usage quantities exceeded the supported range.");
            }
            var start = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
            var end = start.AddMonths(1);
            var snapshot = new QuotaSnapshot(ProviderKind.Copilot, $"{organization}/{user}", "AI credits", new(QuotaWindowKind.Monthly, start, end, ResetAt: end), gross, null, null, "credits", now, now, QuotaSource.Delayed, QuotaConfidence.Official, now.Add(FreshnessTtl), "GitHub Copilot Business", new(gross, discount, net));
            return FetchResult<IReadOnlyList<QuotaSnapshot>>.Success([snapshot]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(FetchStatus.TransientFailure, Error: "GitHub billing usage request timed out.");
        }
        catch (HttpRequestException)
        {
            return new(FetchStatus.TransientFailure, Error: "GitHub billing usage request failed.");
        }
        catch (JsonException)
        {
            return new(FetchStatus.TransientFailure, Error: "GitHub billing usage response was invalid.");
        }
    }

    private static FetchResult<IReadOnlyList<QuotaSnapshot>> MapStatus(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized) return new(FetchStatus.Unauthorized);
        if (response.StatusCode == HttpStatusCode.Forbidden) return new(response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) && values.FirstOrDefault() == "0" ? FetchStatus.RateLimited : FetchStatus.Forbidden);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(FetchStatus.Unsupported);
        if ((int)response.StatusCode == 429) return new(FetchStatus.RateLimited);
        return (int)response.StatusCode >= 500 ? new(FetchStatus.TransientFailure) : new(FetchStatus.Unsupported);
    }
}

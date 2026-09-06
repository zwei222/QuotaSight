using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaSight.Application;
using QuotaSight.Core;

namespace QuotaSight.Infrastructure;

public sealed record DeviceCodeRequest([property: JsonPropertyName("client_id")] string ClientId, [property: JsonPropertyName("scope")] string Scope);
public sealed record TokenRequest([property: JsonPropertyName("client_id")] string ClientId, [property: JsonPropertyName("device_code")] string DeviceCode, [property: JsonPropertyName("grant_type")] string GrantType);
public sealed record DeviceAuthorizationStart(string DeviceCode, string UserCode, Uri VerificationUri, DateTimeOffset ExpiresAt, TimeSpan Interval);
public sealed record DeviceCodeResponse([property: JsonPropertyName("device_code")] string DeviceCode, [property: JsonPropertyName("user_code")] string UserCode, [property: JsonPropertyName("verification_uri")] string VerificationUri, [property: JsonPropertyName("expires_in")] int ExpiresIn, [property: JsonPropertyName("interval")] int Interval);
public sealed record DeviceTokenResponse([property: JsonPropertyName("access_token")] string? AccessToken, [property: JsonPropertyName("error")] string? Error);

public interface IDeviceFlowDelay
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class TimeProviderDeviceFlowDelay(TimeProvider timeProvider) : IDeviceFlowDelay
{
    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => new(Task.Delay(delay, timeProvider, cancellationToken));
}

[JsonSerializable(typeof(DeviceCodeRequest))]
[JsonSerializable(typeof(TokenRequest))]
[JsonSerializable(typeof(DeviceCodeResponse))]
[JsonSerializable(typeof(DeviceTokenResponse))]
internal partial class GitHubJsonContext : JsonSerializerContext;

public sealed class GitHubDeviceFlowClient : IGitHubAuthenticator
{
    private const string GrantType = "urn:ietf:params:oauth:grant-type:device_code";
    private readonly HttpClient client;
    private readonly string clientId;
    private readonly TimeProvider timeProvider;
    private readonly IDeviceFlowDelay delay;
    private readonly string scope;
    private DeviceAuthorizationStart? started;

    public GitHubDeviceFlowClient(HttpClient client, string clientId, TimeProvider? timeProvider = null, IDeviceFlowDelay? delay = null, string scope = "read:user read:org")
    {
        this.client = client;
        this.clientId = clientId;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.delay = delay ?? new TimeProviderDeviceFlowDelay(this.timeProvider);
        this.scope = scope;
    }

    public async ValueTask<FetchResult<DeviceAuthorizationStart>> StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return new(FetchStatus.Unsupported, Error: "GitHub client ID is not configured.");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code");
            request.Headers.Accept.ParseAdd("application/json");
            request.Content = JsonContent.Create(new DeviceCodeRequest(clientId, scope), GitHubJsonContext.Default.DeviceCodeRequest);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return new(FetchStatus.TransientFailure);
            var details = await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(cancellationToken), GitHubJsonContext.Default.DeviceCodeResponse, cancellationToken);
            if (details is null || string.IsNullOrWhiteSpace(details.DeviceCode) || string.IsNullOrWhiteSpace(details.UserCode) || details.ExpiresIn <= 0 || details.Interval < 0 || !Uri.TryCreate(details.VerificationUri, UriKind.Absolute, out var verificationUri)) return new(FetchStatus.TransientFailure);
            started = new(details.DeviceCode, details.UserCode, verificationUri, timeProvider.GetUtcNow().AddSeconds(details.ExpiresIn), TimeSpan.FromSeconds(Math.Max(5, details.Interval)));
            return FetchResult<DeviceAuthorizationStart>.Success(started);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(FetchStatus.TransientFailure, Error: "GitHub device request timed out.");
        }
        catch (HttpRequestException)
        {
            return new(FetchStatus.TransientFailure, Error: "GitHub device request failed.");
        }
        catch (JsonException)
        {
            return new(FetchStatus.TransientFailure, Error: "GitHub device response was invalid.");
        }
    }

    public async ValueTask<FetchResult<string>> PollAsync(DeviceAuthorizationStart authorization, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return new(FetchStatus.Unsupported);
        var interval = authorization.Interval;
        while (timeProvider.GetUtcNow() < authorization.ExpiresAt)
        {
            try
            {
                await delay.DelayAsync(interval, cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
                request.Headers.Accept.ParseAdd("application/json");
                request.Content = JsonContent.Create(new TokenRequest(clientId, authorization.DeviceCode, GrantType), GitHubJsonContext.Default.TokenRequest);
                using var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) return new(FetchStatus.TransientFailure);
                var result = await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(cancellationToken), GitHubJsonContext.Default.DeviceTokenResponse, cancellationToken);
                if (!string.IsNullOrWhiteSpace(result?.AccessToken)) return FetchResult<string>.Success(result.AccessToken);
                if (result?.Error == "authorization_pending") continue;
                if (result?.Error == "slow_down") { interval += TimeSpan.FromSeconds(5); continue; }
                if (result?.Error == "expired_token") return new(FetchStatus.TransientFailure, Error: "GitHub device authorization expired.");
                if (result?.Error == "access_denied") return new(FetchStatus.Unauthorized, Error: "GitHub device authorization denied.");
                return new(FetchStatus.TransientFailure, Error: "GitHub token response was invalid.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(FetchStatus.TransientFailure, Error: "GitHub token request timed out.");
            }
            catch (HttpRequestException)
            {
                return new(FetchStatus.TransientFailure, Error: "GitHub token request failed.");
            }
            catch (JsonException)
            {
                return new(FetchStatus.TransientFailure, Error: "GitHub token response was invalid.");
            }
        }
        return new(FetchStatus.TransientFailure, Error: "GitHub device authorization expired.");
    }

    public async ValueTask<FetchResult<string>> AuthenticateAsync(CancellationToken cancellationToken)
    {
        var start = await StartAsync(cancellationToken);
        return start.IsSuccess ? await PollAsync(start.Value!, cancellationToken) : new(start.Status, RetryAfter: start.RetryAfter, Error: start.Error);
    }
}

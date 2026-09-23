using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaSight.Application;
using QuotaSight.Core;

namespace QuotaSight.Infrastructure;

public sealed record DeviceCodeRequest([property: JsonPropertyName("client_id")] string ClientId);
public sealed record TokenRequest([property: JsonPropertyName("client_id")] string ClientId, [property: JsonPropertyName("device_code")] string DeviceCode, [property: JsonPropertyName("grant_type")] string GrantType);
public sealed record DeviceAuthorizationStart(string DeviceCode, string UserCode, Uri VerificationUri, DateTimeOffset ExpiresAt, TimeSpan Interval, string? ClientId = null);
public sealed record DeviceCodeResponse([property: JsonPropertyName("device_code")] string DeviceCode, [property: JsonPropertyName("user_code")] string UserCode, [property: JsonPropertyName("verification_uri")] string VerificationUri, [property: JsonPropertyName("expires_in")] int ExpiresIn, [property: JsonPropertyName("interval")] int Interval);

public sealed record GitHubTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int? ExpiresIn,
    [property: JsonPropertyName("refresh_token_expires_in")] int? RefreshTokenExpiresIn,
    [property: JsonPropertyName("error")] string? Error)
{
    public override string ToString() => $"{nameof(GitHubTokenResponse)} {{ AccessToken = [REDACTED], RefreshToken = [REDACTED], ExpiresIn = {ExpiresIn}, RefreshTokenExpiresIn = {RefreshTokenExpiresIn}, Error = {(Error is null ? "null" : "[REDACTED]")} }}";
}

public interface IDeviceFlowDelay
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class TimeProviderDeviceFlowDelay(TimeProvider timeProvider) : IDeviceFlowDelay
{
    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => new(Task.Delay(delay, timeProvider, cancellationToken));
}

[JsonSerializable(typeof(DeviceCodeResponse))]
[JsonSerializable(typeof(GitHubTokenResponse))]
internal partial class GitHubJsonContext : JsonSerializerContext;

public sealed class GitHubDeviceFlowClient : IGitHubAuthenticator
{
    private const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";
    private const string RefreshGrantType = "refresh_token";
    private readonly HttpClient client;
    private readonly string clientId;
    private readonly TimeProvider timeProvider;
    private readonly IDeviceFlowDelay delay;
    private DeviceAuthorizationStart? started;

    public GitHubDeviceFlowClient(HttpClient client, string clientId, TimeProvider? timeProvider = null, IDeviceFlowDelay? delay = null)
    {
        this.client = client;
        this.clientId = clientId;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.delay = delay ?? new TimeProviderDeviceFlowDelay(this.timeProvider);
    }

    public async ValueTask<FetchResult<DeviceAuthorizationStart>> StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return new(FetchStatus.Unsupported, Error: "GitHub client ID is not configured.");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code");
            request.Headers.Accept.ParseAdd("application/json");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = clientId });
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return new(FetchStatus.TransientFailure);
            var details = await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(cancellationToken), GitHubJsonContext.Default.DeviceCodeResponse, cancellationToken);
            if (details is null || string.IsNullOrWhiteSpace(details.DeviceCode) || string.IsNullOrWhiteSpace(details.UserCode) || details.ExpiresIn <= 0 || details.Interval < 0 || !Uri.TryCreate(details.VerificationUri, UriKind.Absolute, out var verificationUri)) return new(FetchStatus.TransientFailure);
            started = new(details.DeviceCode, details.UserCode, verificationUri, timeProvider.GetUtcNow().AddSeconds(details.ExpiresIn), TimeSpan.FromSeconds(Math.Max(5, details.Interval)), clientId);
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

    // Compatibility contract: existing callers receive only the access token.
    public async ValueTask<FetchResult<string>> PollAsync(DeviceAuthorizationStart authorization, CancellationToken cancellationToken)
    {
        var result = await PollTokensAsync(authorization, cancellationToken);
        return result.IsSuccess
            ? FetchResult<string>.Success(result.Value!.AccessToken)
            : new(result.Status, RetryAfter: result.RetryAfter, Error: result.Error);
    }

    public async ValueTask<FetchResult<GitHubUserTokens>> PollTokensAsync(DeviceAuthorizationStart authorization, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return new(FetchStatus.Unsupported);
        if (authorization.ClientId is not null && !string.Equals(authorization.ClientId, clientId, StringComparison.Ordinal)) return new(FetchStatus.ConfigurationError, Error: "GitHub client ID changed during device authorization.");
        var interval = authorization.Interval;
        while (timeProvider.GetUtcNow() < authorization.ExpiresAt)
        {
            try
            {
                await delay.DelayAsync(interval, cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
                request.Headers.Accept.ParseAdd("application/json");
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["device_code"] = authorization.DeviceCode,
                    ["grant_type"] = DeviceGrantType
                });
                using var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) return new(FetchStatus.TransientFailure);
                var result = await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(cancellationToken), GitHubJsonContext.Default.GitHubTokenResponse, cancellationToken);
                if (!string.IsNullOrWhiteSpace(result?.AccessToken))
                    return FetchResult<GitHubUserTokens>.Success(new(result.AccessToken, result.RefreshToken, result.ExpiresIn, result.RefreshTokenExpiresIn));
                if (result?.Error == "authorization_pending") continue;
                if (result?.Error == "slow_down")
                {
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                }
                return MapDeviceError(result?.Error);
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

    public async ValueTask<FetchResult<GitHubUserTokens>> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return new(FetchStatus.Unsupported);
        if (string.IsNullOrWhiteSpace(refreshToken)) return new(FetchStatus.Unauthorized, Error: "GitHub refresh token is invalid.");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
            request.Headers.Accept.ParseAdd("application/json");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = RefreshGrantType,
                ["refresh_token"] = refreshToken
            });
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return new(MapHttpFailure(response.StatusCode));
            var result = await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(cancellationToken), GitHubJsonContext.Default.GitHubTokenResponse, cancellationToken);
            if (result is null) return new(FetchStatus.TransientFailure, Error: "GitHub refresh response was invalid.");
            if (!string.IsNullOrWhiteSpace(result.Error)) return MapRefreshError(result.Error);
            if (string.IsNullOrWhiteSpace(result.AccessToken) || string.IsNullOrWhiteSpace(result.RefreshToken) || result.ExpiresIn is not > 0 || result.RefreshTokenExpiresIn is not > 0)
                return new(FetchStatus.TransientFailure, Error: "GitHub refresh response was incomplete.");
            return FetchResult<GitHubUserTokens>.Success(new(result.AccessToken, result.RefreshToken, result.ExpiresIn, result.RefreshTokenExpiresIn));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(FetchStatus.TransientFailure, Error: "GitHub refresh request timed out.");
        }
        catch (HttpRequestException)
        {
            return new(FetchStatus.TransientFailure, Error: "GitHub refresh request failed.");
        }
        catch (JsonException)
        {
            return new(FetchStatus.TransientFailure, Error: "GitHub refresh response was invalid.");
        }
    }

    public async ValueTask<FetchResult<string>> AuthenticateAsync(CancellationToken cancellationToken)
    {
        var start = await StartAsync(cancellationToken);
        return start.IsSuccess ? await PollAsync(start.Value!, cancellationToken) : new(start.Status, RetryAfter: start.RetryAfter, Error: start.Error);
    }

    private static FetchResult<GitHubUserTokens> MapDeviceError(string? error) => error switch
    {
        "expired_token" => new(FetchStatus.TransientFailure, Error: "GitHub device authorization expired."),
        "access_denied" => new(FetchStatus.Unauthorized, Error: "GitHub device authorization denied."),
        "incorrect_client_credentials" => new(FetchStatus.Unauthorized, Error: "GitHub client credentials were rejected."),
        "device_flow_disabled" => new(FetchStatus.Unsupported, Error: "GitHub device authorization is disabled."),
        "unsupported_grant_type" => new(FetchStatus.Unsupported, Error: "GitHub device authorization grant is unsupported."),
        _ => new(FetchStatus.TransientFailure, Error: "GitHub token response was invalid.")
    };

    private static FetchStatus MapHttpFailure(System.Net.HttpStatusCode status) => status switch
    {
        System.Net.HttpStatusCode.Unauthorized => FetchStatus.Unauthorized,
        System.Net.HttpStatusCode.Forbidden => FetchStatus.Forbidden,
        (System.Net.HttpStatusCode)429 => FetchStatus.RateLimited,
        _ => FetchStatus.TransientFailure
    };

    private static FetchResult<GitHubUserTokens> MapRefreshError(string error) => error switch
    {
        "bad_refresh_token" or "incorrect_client_credentials" or "access_denied" => new(FetchStatus.Unauthorized, Error: "GitHub refresh token was rejected."),
        "unsupported_grant_type" or "device_flow_disabled" => new(FetchStatus.Unsupported, Error: "GitHub token refresh is unsupported."),
        _ => new(FetchStatus.TransientFailure, Error: "GitHub token refresh failed.")
    };
}

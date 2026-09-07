using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaSight.Application;
using QuotaSight.Core;

namespace QuotaSight.Infrastructure;

public sealed record CodexDeviceAuthorization(string UserCode, string DeviceAuthId, Uri VerificationUri, DateTimeOffset ExpiresAt, TimeSpan Interval)
{
    internal long SessionGeneration { get; init; }
}
internal sealed record CodexTokenBundle([property: JsonPropertyName("access_token")] string AccessToken, [property: JsonPropertyName("refresh_token")] string RefreshToken, [property: JsonPropertyName("id_token")] string? IdToken, [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
public sealed record CodexClientRequest([property: JsonPropertyName("client_id")] string ClientId);
public sealed record CodexDeviceTokenRequest([property: JsonPropertyName("device_auth_id")] string DeviceAuthId, [property: JsonPropertyName("user_code")] string UserCode);
public sealed record CodexDeviceCodeResponse([property: JsonPropertyName("user_code")] string? UserCode, [property: JsonPropertyName("device_auth_id")] string? DeviceAuthId, [property: JsonPropertyName("interval")] int? Interval);
public sealed record CodexDeviceTokenResponse([property: JsonPropertyName("authorization_code")] string? AuthorizationCode, [property: JsonPropertyName("code_verifier")] string? CodeVerifier);
public sealed record CodexOAuthTokenResponse([property: JsonPropertyName("access_token")] string? AccessToken, [property: JsonPropertyName("refresh_token")] string? RefreshToken, [property: JsonPropertyName("id_token")] string? IdToken, [property: JsonPropertyName("expires_in")] int? ExpiresIn);

[JsonSerializable(typeof(CodexClientRequest))]
[JsonSerializable(typeof(CodexDeviceTokenRequest))]
[JsonSerializable(typeof(CodexDeviceCodeResponse))]
[JsonSerializable(typeof(CodexDeviceTokenResponse))]
[JsonSerializable(typeof(CodexOAuthTokenResponse))]
[JsonSerializable(typeof(CodexTokenBundle))]
internal partial class CodexJsonContext : JsonSerializerContext;

public sealed class CodexOAuthClient
{
    public const string CredentialKey = "OpenAI Codex OAuth";
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private static readonly Uri DeviceCodeEndpoint = new("https://auth.openai.com/api/accounts/deviceauth/usercode");
    private static readonly Uri DeviceTokenEndpoint = new("https://auth.openai.com/api/accounts/deviceauth/token");
    private static readonly Uri OAuthTokenEndpoint = new("https://auth.openai.com/oauth/token");
    private static readonly Uri VerificationEndpoint = new("https://auth.openai.com/codex/device");
    private static readonly Uri RedirectEndpoint = new("https://auth.openai.com/deviceauth/callback");
    private readonly HttpClient client; private readonly ICredentialStore store; private readonly TimeProvider clock;
    private readonly SemaphoreSlim sessionGate = new(1, 1);
    private long generation;
    public CodexOAuthClient(HttpClient client, ICredentialStore store, TimeProvider? clock = null) { this.client = client; this.store = store; this.clock = clock ?? TimeProvider.System; }
    public CredentialStoreAvailability CredentialAvailability => store.Availability;
    internal ICredentialStore Store => store;
    internal SemaphoreSlim SessionGate => sessionGate;
    internal long Generation => Volatile.Read(ref generation);
    internal void InvalidateSession() => Interlocked.Increment(ref generation);

    public async ValueTask<FetchResult<CodexDeviceAuthorization>> StartAsync(CancellationToken ct)
    {
        var observedGeneration = Generation;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, DeviceCodeEndpoint) { Content = JsonContent.Create(new CodexClientRequest(ClientId), CodexJsonContext.Default.CodexClientRequest) };
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(FetchStatus.Unauthorized);
            if (response.StatusCode == HttpStatusCode.Forbidden) return new(FetchStatus.Forbidden);
            if ((int)response.StatusCode == 429) return new(FetchStatus.RateLimited, RetryAfter: RetryAfter(response));
            if (!response.IsSuccessStatusCode) return new(FetchStatus.TransientFailure);
            var dto = await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(ct), CodexJsonContext.Default.CodexDeviceCodeResponse, ct);
            if (dto is null || string.IsNullOrWhiteSpace(dto.UserCode) || string.IsNullOrWhiteSpace(dto.DeviceAuthId)) return new(FetchStatus.TransientFailure);
            if (observedGeneration != Generation) return new(FetchStatus.Unauthorized);
            var interval = Math.Max(3, dto.Interval ?? 5);
            return FetchResult<CodexDeviceAuthorization>.Success(new(dto.UserCode, dto.DeviceAuthId, VerificationEndpoint, clock.GetUtcNow().AddMinutes(15), TimeSpan.FromSeconds(interval)) { SessionGeneration = observedGeneration });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(FetchStatus.TransientFailure); }
        catch (HttpRequestException) { return new(FetchStatus.TransientFailure); }
        catch (JsonException) { return new(FetchStatus.TransientFailure); }
    }

    public async ValueTask<FetchResult<bool>> PollAndStoreAsync(CodexDeviceAuthorization authorization, CancellationToken ct)
    {
        if (authorization.ExpiresAt <= clock.GetUtcNow()) return new(FetchStatus.TransientFailure);
        var observedGeneration = authorization.SessionGeneration;
        await sessionGate.WaitAsync(ct);
        try
        {
            if (observedGeneration != Generation) return new(FetchStatus.Unauthorized);
            using var request = new HttpRequestMessage(HttpMethod.Post, DeviceTokenEndpoint) { Content = JsonContent.Create(new CodexDeviceTokenRequest(authorization.DeviceAuthId, authorization.UserCode), CodexJsonContext.Default.CodexDeviceTokenRequest) };
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(FetchStatus.Unauthorized);
            if (response.StatusCode == HttpStatusCode.Forbidden) return new(FetchStatus.Forbidden);
            if ((int)response.StatusCode == 429) return new(FetchStatus.RateLimited, RetryAfter: RetryAfter(response));
            if (response.StatusCode == HttpStatusCode.NotFound) return new(FetchStatus.TransientFailure, RetryAfter: authorization.Interval);
            if (!response.IsSuccessStatusCode) return new(FetchStatus.TransientFailure);
            var device = await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(ct), CodexJsonContext.Default.CodexDeviceTokenResponse, ct);
            if (device is null || string.IsNullOrWhiteSpace(device.AuthorizationCode) || string.IsNullOrWhiteSpace(device.CodeVerifier)) return new(FetchStatus.TransientFailure);
            var exchanged = await ExchangeAsync(device, ct); if (!exchanged.IsSuccess) return new(exchanged.Status, RetryAfter: exchanged.RetryAfter);
            if (observedGeneration != Generation) return new(FetchStatus.Unauthorized);
            try
            {
                await store.SetAsync(CredentialKey, JsonSerializer.Serialize(exchanged.Value, CodexJsonContext.Default.CodexTokenBundle), ct);
                if (observedGeneration != Generation)
                {
                    await store.RemoveAsync(CredentialKey, ct);
                    return new(FetchStatus.Unauthorized);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(FetchStatus.TransientFailure); }
            catch (Exception) when (!ct.IsCancellationRequested) { return new(FetchStatus.TransientFailure); }
            return FetchResult<bool>.Success(true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(FetchStatus.TransientFailure); }
        catch (HttpRequestException) { return new(FetchStatus.TransientFailure); }
        catch (JsonException) { return new(FetchStatus.TransientFailure); }
        finally { sessionGate.Release(); }
    }

    private async ValueTask<FetchResult<CodexTokenBundle>> ExchangeAsync(CodexDeviceTokenResponse device, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, OAuthTokenEndpoint) { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = device.AuthorizationCode!, ["redirect_uri"] = RedirectEndpoint.ToString(), ["client_id"] = ClientId, ["code_verifier"] = device.CodeVerifier! }) };
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(FetchStatus.Unauthorized);
            if (response.StatusCode == HttpStatusCode.Forbidden) return new(FetchStatus.Forbidden);
            if ((int)response.StatusCode == 429) return new(FetchStatus.RateLimited, RetryAfter: RetryAfter(response));
            if ((int)response.StatusCode >= 500) return new(FetchStatus.TransientFailure);
            if (!response.IsSuccessStatusCode) return new(FetchStatus.TransientFailure);
            var dto = await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(ct), CodexJsonContext.Default.CodexOAuthTokenResponse, ct);
            if (dto is null || string.IsNullOrWhiteSpace(dto.AccessToken) || string.IsNullOrWhiteSpace(dto.RefreshToken) || dto.ExpiresIn is null or <= 0) return new(FetchStatus.TransientFailure);
            return FetchResult<CodexTokenBundle>.Success(new(dto.AccessToken, dto.RefreshToken, dto.IdToken, clock.GetUtcNow().AddSeconds(dto.ExpiresIn.Value)));
        }
        catch (HttpRequestException) { return new(FetchStatus.TransientFailure); }
        catch (JsonException) { return new(FetchStatus.TransientFailure); }
    }
    internal TimeSpan? RetryAfter(HttpResponseMessage response) => response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date ? date - clock.GetUtcNow() : null);
}

public sealed class CodexSessionManager
{
    private readonly CodexOAuthClient oauth; private readonly HttpClient client; private readonly TimeProvider clock;
    public CodexSessionManager(CodexOAuthClient oauth, HttpClient client, TimeProvider? clock = null) { this.oauth = oauth; this.client = client; this.clock = clock ?? TimeProvider.System; }
    public CredentialStoreAvailability CredentialAvailability => oauth.CredentialAvailability;
    public async ValueTask<bool> HasCredentialAsync(CancellationToken ct)
    {
        try { return !string.IsNullOrWhiteSpace(await oauth.Store.GetAsync(CodexOAuthClient.CredentialKey, ct)); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }
    public ValueTask<FetchResult<CodexDeviceAuthorization>> StartAsync(CancellationToken ct) => oauth.StartAsync(ct);
    public ValueTask<FetchResult<bool>> PollAndStoreAsync(CodexDeviceAuthorization auth, CancellationToken ct) => oauth.PollAndStoreAsync(auth, ct);
    public async ValueTask LogoutAsync(CancellationToken ct)
    {
        oauth.InvalidateSession();
        await oauth.SessionGate.WaitAsync(ct);
        try { await oauth.Store.RemoveAsync(CodexOAuthClient.CredentialKey, ct); }
        finally { oauth.SessionGate.Release(); }
    }
    public async ValueTask<FetchResult<IReadOnlyList<QuotaSnapshot>>> FetchAsync(CancellationToken ct)
    {
        var observedGeneration = oauth.Generation;
        await oauth.SessionGate.WaitAsync(ct);
        try
        {
            var store = oauth.Store; var json = await store.GetAsync(CodexOAuthClient.CredentialKey, ct); if (json is null) return new(FetchStatus.Unauthorized);
            CodexTokenBundle? bundle; try { bundle = ParseBundle(json); } catch (JsonException) { return new(FetchStatus.TransientFailure); }
            if (bundle is null || string.IsNullOrWhiteSpace(bundle.AccessToken) || string.IsNullOrWhiteSpace(bundle.RefreshToken)) return new(FetchStatus.TransientFailure);
            if (bundle.ExpiresAt <= clock.GetUtcNow().AddMinutes(2)) { var refreshed = await RefreshAsync(bundle, store, ct, observedGeneration); if (!refreshed.IsSuccess) return new(refreshed.Status, RetryAfter: refreshed.RetryAfter); bundle = refreshed.Value; }
            if (observedGeneration != oauth.Generation) return new(FetchStatus.Unauthorized);
            return await new CodexQuotaAdapter(client, clock).FetchTokenAsync(bundle!.AccessToken, "ChatGPT", ct);
        }
        finally { oauth.SessionGate.Release(); }
    }
    private async ValueTask<FetchResult<CodexTokenBundle>> RefreshAsync(CodexTokenBundle old, ICredentialStore store, CancellationToken ct, long observedGeneration)
    {
        try
        {
            var currentJson = await store.GetAsync(CodexOAuthClient.CredentialKey, ct);
            if (currentJson is null) return new(FetchStatus.Unauthorized);
            CodexTokenBundle current; try { current = ParseBundle(currentJson) ?? throw new InvalidOperationException(); } catch (JsonException) { return new(FetchStatus.TransientFailure); }
            if (current.ExpiresAt > clock.GetUtcNow().AddMinutes(2)) return FetchResult<CodexTokenBundle>.Success(current);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.openai.com/oauth/token") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = current.RefreshToken, ["client_id"] = CodexOAuthClient.ClientId }) };
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.BadRequest) return new(FetchStatus.Unauthorized);
            if ((int)response.StatusCode == 429) return new(FetchStatus.RateLimited, RetryAfter: response.Headers.RetryAfter?.Delta);
            if ((int)response.StatusCode >= 500) return new(FetchStatus.TransientFailure);
            if (!response.IsSuccessStatusCode) return new(FetchStatus.TransientFailure);
            var dto = await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(ct), CodexJsonContext.Default.CodexOAuthTokenResponse, ct);
            if (dto is null || string.IsNullOrWhiteSpace(dto.AccessToken) || dto.ExpiresIn is null or <= 0) return new(FetchStatus.TransientFailure);
            var updated = new CodexTokenBundle(dto.AccessToken, string.IsNullOrWhiteSpace(dto.RefreshToken) ? current.RefreshToken : dto.RefreshToken, dto.IdToken ?? current.IdToken, clock.GetUtcNow().AddSeconds(dto.ExpiresIn.Value));
            if (observedGeneration != oauth.Generation) return new(FetchStatus.Unauthorized);
            await store.SetAsync(CodexOAuthClient.CredentialKey, JsonSerializer.Serialize(updated, CodexJsonContext.Default.CodexTokenBundle), ct);
            if (observedGeneration != oauth.Generation)
            {
                await store.RemoveAsync(CodexOAuthClient.CredentialKey, ct);
                return new(FetchStatus.Unauthorized);
            }
            return FetchResult<CodexTokenBundle>.Success(updated);
        }
        catch (HttpRequestException) { return new(FetchStatus.TransientFailure); }
        catch (JsonException) { return new(FetchStatus.TransientFailure); }
        catch (InvalidOperationException) { return new(FetchStatus.TransientFailure); }
    }

    private static CodexTokenBundle? ParseBundle(string json)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        var access = Property(root, "access_token", "AccessToken"); var refresh = Property(root, "refresh_token", "RefreshToken"); var expires = Property(root, "expires_at", "ExpiresAt");
        if (access is null || refresh is null || expires is null) return null;
        var id = Property(root, "id_token", "IdToken");
        return new(access.Value.GetString() ?? "", refresh.Value.GetString() ?? "", id is { ValueKind: not JsonValueKind.Null } ? id.Value.GetString() : null, expires.Value.GetDateTimeOffset());
    }

    private static JsonElement? Property(JsonElement root, string first, string second)
    {
        if (root.TryGetProperty(first, out var value)) return value;
        if (root.TryGetProperty(second, out value)) return value;
        return null;
    }
}

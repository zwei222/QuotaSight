using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuotaSight.Infrastructure;

public sealed class GitHubUserTokens(string accessToken, string? refreshToken, int? expiresIn, int? refreshTokenExpiresIn)
{
    public string AccessToken { get; } = accessToken;
    public string? RefreshToken { get; } = refreshToken;
    public int? ExpiresIn { get; } = expiresIn;
    public int? RefreshTokenExpiresIn { get; } = refreshTokenExpiresIn;

    public bool CanCreateBundle => !string.IsNullOrWhiteSpace(AccessToken)
        && !string.IsNullOrWhiteSpace(RefreshToken)
        && ExpiresIn is > 0
        && RefreshTokenExpiresIn is > 0;

    public override string ToString() => $"{nameof(GitHubUserTokens)} {{ AccessToken = [REDACTED], RefreshToken = [REDACTED], ExpiresIn = {ExpiresIn}, RefreshTokenExpiresIn = {RefreshTokenExpiresIn} }}";

    public static string SerializeBundle(string clientId, GitHubUserTokens tokens, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (string.IsNullOrWhiteSpace(clientId) || !tokens.CanCreateBundle)
            throw new ArgumentException("A complete token set and client ID are required to create a v2 bundle.");

        var bundle = new GitHubUserTokenBundleData(
            2,
            clientId,
            tokens.AccessToken,
            tokens.RefreshToken!,
            now.ToUniversalTime().AddSeconds(tokens.ExpiresIn!.Value),
            now.ToUniversalTime().AddSeconds(tokens.RefreshTokenExpiresIn!.Value));
        return BundlePrefix + JsonSerializer.Serialize(bundle, GitHubUserTokensJsonContext.Default.GitHubUserTokenBundleData);
    }

    public static GitHubStoredTokenValue ParseStoredValue(string? value)
    {
        if (value is null) return new(GitHubStoredTokenKind.Invalid, null);
        if (!value.StartsWith(BundlePrefix, StringComparison.Ordinal))
            return string.IsNullOrWhiteSpace(value)
                ? new(GitHubStoredTokenKind.Invalid, null)
                : new(GitHubStoredTokenKind.LegacyAccessToken, null);

        try
        {
            var bundle = JsonSerializer.Deserialize(value[BundlePrefix.Length..], GitHubUserTokensJsonContext.Default.GitHubUserTokenBundleData);
            if (bundle is null || bundle.Version != 2 || string.IsNullOrWhiteSpace(bundle.ClientId)
                || string.IsNullOrWhiteSpace(bundle.AccessToken) || string.IsNullOrWhiteSpace(bundle.RefreshToken)
                || bundle.AccessTokenExpiresAt.Offset != TimeSpan.Zero || bundle.RefreshTokenExpiresAt.Offset != TimeSpan.Zero
                || bundle.AccessTokenExpiresAt == default || bundle.RefreshTokenExpiresAt == default)
                return new(GitHubStoredTokenKind.InvalidV2Bundle, null);

            var safeBundle = new GitHubUserTokenBundle(bundle.ClientId, bundle.AccessToken, bundle.RefreshToken, bundle.AccessTokenExpiresAt, bundle.RefreshTokenExpiresAt);
            return new(GitHubStoredTokenKind.V2Bundle, safeBundle);
        }
        catch (JsonException)
        {
            return new(GitHubStoredTokenKind.InvalidV2Bundle, null);
        }
    }

    private const string BundlePrefix = "github-user-token:v2:";
}

public sealed record GitHubUserTokenBundle(string ClientId, string AccessToken, string RefreshToken, DateTimeOffset AccessTokenExpiresAt, DateTimeOffset RefreshTokenExpiresAt)
{
    public override string ToString() => $"{nameof(GitHubUserTokenBundle)} {{ ClientId = {ClientId}, AccessToken = [REDACTED], RefreshToken = [REDACTED], AccessTokenExpiresAt = {AccessTokenExpiresAt:O}, RefreshTokenExpiresAt = {RefreshTokenExpiresAt:O} }}";
}

public enum GitHubStoredTokenKind
{
    Invalid,
    LegacyAccessToken,
    V2Bundle,
    InvalidV2Bundle
}

public sealed record GitHubStoredTokenValue(GitHubStoredTokenKind Kind, GitHubUserTokenBundle? Bundle);

internal sealed record GitHubUserTokenBundleData(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("access_token_expires_at")] DateTimeOffset AccessTokenExpiresAt,
    [property: JsonPropertyName("refresh_token_expires_at")] DateTimeOffset RefreshTokenExpiresAt)
{
    public override string ToString() => $"{nameof(GitHubUserTokenBundleData)} {{ Version = {Version}, ClientId = {ClientId}, AccessToken = [REDACTED], RefreshToken = [REDACTED], AccessTokenExpiresAt = {AccessTokenExpiresAt:O}, RefreshTokenExpiresAt = {RefreshTokenExpiresAt:O} }}";
}

[JsonSerializable(typeof(GitHubUserTokenBundleData))]
internal partial class GitHubUserTokensJsonContext : JsonSerializerContext;

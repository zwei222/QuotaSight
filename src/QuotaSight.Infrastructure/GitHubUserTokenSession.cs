using System.Runtime.CompilerServices;
using QuotaSight.Application;
using QuotaSight.Core;

namespace QuotaSight.Infrastructure;

public sealed class GitHubUserTokenSession
{
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly ConditionalWeakTable<ICredentialStore, SemaphoreSlim> StoreGates = new();
    private readonly ICredentialStore credentialStore;
    private readonly Func<string> clientId;
    private readonly Func<string, string, CancellationToken, ValueTask<FetchResult<GitHubUserTokens>>> refresh;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim refreshGate;

    public GitHubUserTokenSession(ICredentialStore credentialStore, Func<string> clientId, HttpClient client, TimeProvider? timeProvider = null)
        : this(credentialStore, clientId, (id, token, cancellationToken) => new GitHubDeviceFlowClient(client, id).RefreshAsync(token, cancellationToken), timeProvider)
    {
    }

    public GitHubUserTokenSession(ICredentialStore credentialStore, Func<string> clientId, Func<string, CancellationToken, ValueTask<FetchResult<GitHubUserTokens>>> refresh, TimeProvider? timeProvider = null)
        : this(credentialStore, clientId, (_, token, cancellationToken) => refresh(token, cancellationToken), timeProvider)
    {
    }

    private GitHubUserTokenSession(ICredentialStore credentialStore, Func<string> clientId, Func<string, string, CancellationToken, ValueTask<FetchResult<GitHubUserTokens>>> refresh, TimeProvider? timeProvider)
    {
        this.credentialStore = credentialStore;
        this.clientId = clientId;
        this.refresh = refresh;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        refreshGate = StoreGates.GetValue(credentialStore, _ => new SemaphoreSlim(1, 1));
    }

    public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var result = await GetAccessTokenResultAsync(cancellationToken, forceRefresh);
        return result.IsSuccess ? result.Value : null;
    }

    public async ValueTask<FetchResult<string>> GetAccessTokenResultAsync(CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var initialClientId = clientId();
        var stored = await credentialStore.GetAsync("github", cancellationToken);
        var parsed = GitHubUserTokens.ParseStoredValue(stored);
        if (parsed.Kind == GitHubStoredTokenKind.LegacyAccessToken)
            return forceRefresh ? new(FetchStatus.Unauthorized) : FetchResult<string>.Success(stored!);
        if (parsed.Kind != GitHubStoredTokenKind.V2Bundle || parsed.Bundle is not { } initial) return new(FetchStatus.Unauthorized);
        if (!string.Equals(initial.ClientId, initialClientId, StringComparison.Ordinal)) return new(FetchStatus.ConfigurationError);
        if (!forceRefresh && initial.AccessTokenExpiresAt > timeProvider.GetUtcNow().Add(RefreshWindow)) return FetchResult<string>.Success(initial.AccessToken);
        if (initial.RefreshTokenExpiresAt <= timeProvider.GetUtcNow()) return new(FetchStatus.Unauthorized);

        IDisposable interprocessLock;
        try { interprocessLock = await GitHubCredentialInterprocessLock.AcquireAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException or PlatformNotSupportedException or NotSupportedException)
        {
            return new(FetchStatus.TransientFailure, Error: "GitHub credential coordination is unavailable.");
        }

        using (interprocessLock)
        {
            await refreshGate.WaitAsync(cancellationToken);
            try
            {
                var latestStored = await credentialStore.GetAsync("github", cancellationToken);
                var latest = GitHubUserTokens.ParseStoredValue(latestStored);
                if (latest.Kind != GitHubStoredTokenKind.V2Bundle || latest.Bundle is not { } current) return new(FetchStatus.Unauthorized);
                if (!string.Equals(current.ClientId, initialClientId, StringComparison.Ordinal) || !string.Equals(clientId(), initialClientId, StringComparison.Ordinal)) return new(FetchStatus.ConfigurationError);
                if (forceRefresh && current.AccessToken != initial.AccessToken) return FetchResult<string>.Success(current.AccessToken);
                if (!forceRefresh && current.AccessTokenExpiresAt > timeProvider.GetUtcNow().Add(RefreshWindow)) return FetchResult<string>.Success(current.AccessToken);
                if (current.RefreshTokenExpiresAt <= timeProvider.GetUtcNow()) return new(FetchStatus.Unauthorized);

                cancellationToken.ThrowIfCancellationRequested();
                FetchResult<GitHubUserTokens> result;
                using (var timeout = new CancellationTokenSource(OperationTimeout)) result = await refresh(initialClientId, current.RefreshToken, timeout.Token);
                if (result.IsSuccess && result.Value is not { CanCreateBundle: true })
                    return new(FetchStatus.Unauthorized, Error: "GitHub token refresh did not return a complete token set.");
                if (!result.IsSuccess || result.Value is not { CanCreateBundle: true } tokens)
                {
                    if (result.Status == FetchStatus.Unauthorized)
                    {
                        var reread = GitHubUserTokens.ParseStoredValue(await credentialStore.GetAsync("github", CancellationToken.None));
                        if (reread.Kind == GitHubStoredTokenKind.V2Bundle && reread.Bundle is { } updated && updated.AccessToken != current.AccessToken && updated.ClientId == initialClientId) return FetchResult<string>.Success(updated.AccessToken);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    return new(result.Status, RetryAfter: result.RetryAfter, Error: result.Error);
                }

                // Re-read and compare the exact credential snapshot before writing rotated credentials.
                var beforeSave = GitHubUserTokens.ParseStoredValue(await credentialStore.GetAsync("github", CancellationToken.None));
                if (beforeSave.Kind != GitHubStoredTokenKind.V2Bundle || beforeSave.Bundle is not { } unchanged
                    || unchanged.ClientId != current.ClientId || unchanged.AccessToken != current.AccessToken || unchanged.RefreshToken != current.RefreshToken
                    || clientId() != initialClientId)
                    return new(FetchStatus.ConfigurationError, Error: "GitHub credentials changed during token refresh.");
                var serialized = GitHubUserTokens.SerializeBundle(initialClientId, tokens, timeProvider.GetUtcNow());
                using (var saveTimeout = new CancellationTokenSource(OperationTimeout)) await credentialStore.SetAsync("github", serialized, saveTimeout.Token);
                cancellationToken.ThrowIfCancellationRequested();
                return FetchResult<string>.Success(tokens.AccessToken);
            }
            finally { refreshGate.Release(); }
        }
    }

    public ValueTask<FetchResult<bool>> SaveDeviceFlowTokensAsync(GitHubUserTokens tokens, CancellationToken cancellationToken) => SaveDeviceFlowTokensAsync(tokens, cancellationToken, clientId());

    public async ValueTask<FetchResult<bool>> SaveDeviceFlowTokensAsync(GitHubUserTokens tokens, CancellationToken cancellationToken, string? startedClientId)
    {
        IDisposable interprocessLock;
        try { interprocessLock = await GitHubCredentialInterprocessLock.AcquireAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException or PlatformNotSupportedException or NotSupportedException)
        {
            return new(FetchStatus.TransientFailure, Error: "GitHub credential coordination is unavailable.");
        }

        using (interprocessLock)
        {
            var currentClientId = clientId();
            if (startedClientId is not null && !string.Equals(startedClientId, currentClientId, StringComparison.Ordinal)) return new(FetchStatus.ConfigurationError);
            await refreshGate.WaitAsync(cancellationToken);
            try
            {
                currentClientId = clientId();
                if (startedClientId is not null && !string.Equals(startedClientId, currentClientId, StringComparison.Ordinal)) return new(FetchStatus.ConfigurationError);
                var value = tokens.CanCreateBundle ? GitHubUserTokens.SerializeBundle(currentClientId, tokens, timeProvider.GetUtcNow()) : tokens.AccessToken;
                await credentialStore.SetAsync("github", value, cancellationToken);
                return FetchResult<bool>.Success(true);
            }
            finally { refreshGate.Release(); }
        }
    }
}

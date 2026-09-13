using QuotaSight.Application;
using QuotaSight.Core;

namespace QuotaSight.Infrastructure;

// Fetch-only: history persistence is owned by the UI's HistoryState storage lane so that
// provider refreshes never write storage concurrently with history mutations.
public sealed class CredentialBackedQuotaApplication : IQuotaApplication
{
    private readonly Func<string, IQuotaAdapter> adapterFactory;
    private readonly ICredentialStore credentials;
    private readonly CodexSessionManager? codexSessionManager;
    private readonly TimeSpan providerTimeout;

    public CredentialBackedQuotaApplication(
        Func<string, IQuotaAdapter> adapterFactory,
        ICredentialStore credentials,
        CodexSessionManager? codexSessionManager = null,
        TimeSpan? providerTimeout = null)
    {
        this.adapterFactory = adapterFactory;
        this.credentials = credentials;
        this.codexSessionManager = codexSessionManager;
        this.providerTimeout = providerTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        var openCodeTask = FetchOpenCodeAsync(cancellationToken).AsTask();
        var codexTask = FetchCodexAsync(cancellationToken).AsTask();
        await Task.WhenAll(openCodeTask, codexTask);
        var openCode = await openCodeTask;
        var codex = await codexTask;
        var results = new[] { openCode, codex };
        var snapshots = results.SelectMany(result => result.Snapshots).ToArray();
        var failures = results.SelectMany(result => result.Failures).ToArray();
        return new(snapshots, failures);
    }

    private async ValueTask<QuotaRefreshResult> FetchOpenCodeAsync(CancellationToken cancellationToken)
    {
        using var providerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        providerCancellation.CancelAfter(providerTimeout);
        var providerToken = providerCancellation.Token;
        try
        {
            var key = await credentials.GetAsync("OpenCode Go", providerToken);
            if (string.IsNullOrWhiteSpace(key)) return Empty(ProviderKind.OpenCode);

            var result = await adapterFactory(key).FetchAsync("OpenCode Go", providerToken);
            return ToRefreshResult(ProviderKind.OpenCode, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(ProviderKind.OpenCode, FetchStatus.TransientFailure);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(ProviderKind.OpenCode, FetchStatus.TransientFailure);
        }
    }

    private async ValueTask<QuotaRefreshResult> FetchCodexAsync(CancellationToken cancellationToken)
    {
        if (codexSessionManager is null) return Empty(ProviderKind.ChatGpt);

        using var providerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        providerCancellation.CancelAfter(providerTimeout);
        var providerToken = providerCancellation.Token;
        try
        {
            if (!await codexSessionManager.HasCredentialAsync(providerToken)) return Empty(ProviderKind.ChatGpt);
            var result = await codexSessionManager.FetchAsync(providerToken);
            return ToRefreshResult(ProviderKind.ChatGpt, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(ProviderKind.ChatGpt, FetchStatus.TransientFailure);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(ProviderKind.ChatGpt, FetchStatus.TransientFailure);
        }
    }

    private static QuotaRefreshResult Empty(ProviderKind provider) => new([], []);

    private static QuotaRefreshResult Failure(ProviderKind provider, FetchStatus status, TimeSpan? retryAfter = null) =>
        new([], [new(provider, status, retryAfter)]);

    private static QuotaRefreshResult ToRefreshResult(ProviderKind provider, FetchResult<IReadOnlyList<QuotaSnapshot>> result) =>
        result.IsSuccess
            ? new(result.Value ?? [], [])
            : Failure(provider, result.Status, result.RetryAfter);
}

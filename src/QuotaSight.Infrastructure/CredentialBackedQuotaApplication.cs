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
    private readonly IQuotaAdapter? copilotAdapter;
    private readonly TimeSpan providerTimeout;

    public CredentialBackedQuotaApplication(
        Func<string, IQuotaAdapter> adapterFactory,
        ICredentialStore credentials,
        CodexSessionManager? codexSessionManager = null,
        TimeSpan? providerTimeout = null,
        IQuotaAdapter? copilotAdapter = null)
    {
        this.adapterFactory = adapterFactory;
        this.credentials = credentials;
        this.codexSessionManager = codexSessionManager;
        this.copilotAdapter = copilotAdapter;
        this.providerTimeout = providerTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        var openCodeTask = FetchOpenCodeAsync(cancellationToken).AsTask();
        var codexTask = FetchCodexAsync(cancellationToken).AsTask();
        var copilotTask = FetchCopilotAsync(cancellationToken).AsTask();
        await Task.WhenAll(openCodeTask, codexTask, copilotTask);
        var openCode = await openCodeTask;
        var codex = await codexTask;
        var copilot = await copilotTask;
        var results = new[] { openCode, codex, copilot };
        var snapshots = results.SelectMany(result => result.Snapshots).ToArray();
        var failures = results.SelectMany(result => result.Failures).ToArray();
        var noDataProviders = results.SelectMany(result => result.NoDataProviders).ToHashSet();
        var activeAccounts = results.SelectMany(result => result.ActiveAccounts).ToDictionary(pair => pair.Key, pair => pair.Value);
        return new(snapshots, failures, noDataProviders, activeAccounts);
    }

    private async ValueTask<QuotaRefreshResult> FetchCopilotAsync(CancellationToken cancellationToken)
    {
        if (copilotAdapter is null) return Empty(ProviderKind.Copilot);
        using var providerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        providerCancellation.CancelAfter(providerTimeout);
        try
        {
            if (copilotAdapter is IActiveAccountQuotaAdapter activeAccountAdapter)
            {
                var result = await activeAccountAdapter.FetchWithAccountAsync("GitHub Copilot", providerCancellation.Token);
                return ToRefreshResult(ProviderKind.Copilot, result.Result, result.ActiveAccount);
            }

            var fallbackResult = await copilotAdapter.FetchAsync("GitHub Copilot", providerCancellation.Token);
            return ToRefreshResult(ProviderKind.Copilot, fallbackResult);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(ProviderKind.Copilot, FetchStatus.TransientFailure);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(ProviderKind.Copilot, FetchStatus.TransientFailure);
        }
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

    private static QuotaRefreshResult ToRefreshResult(ProviderKind provider, FetchResult<IReadOnlyList<QuotaSnapshot>> result, string? activeAccount = null) =>
        result.IsSuccess
            ? new(result.Value ?? [], [], activeAccounts: ActiveAccounts(provider, activeAccount ?? result.Value?.FirstOrDefault()?.Account))
            : result.Status == FetchStatus.NoData
                ? new([], [], new HashSet<ProviderKind> { provider }, ActiveAccounts(provider, activeAccount))
                : new([], [new(provider, result.Status, result.RetryAfter)], activeAccounts: ActiveAccounts(provider, activeAccount));

    private static IReadOnlyDictionary<ProviderKind, string> ActiveAccounts(ProviderKind provider, string? activeAccount) =>
        string.IsNullOrWhiteSpace(activeAccount) ? new Dictionary<ProviderKind, string>() : new Dictionary<ProviderKind, string> { [provider] = activeAccount };
}

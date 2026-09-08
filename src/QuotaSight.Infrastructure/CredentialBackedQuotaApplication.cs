using QuotaSight.Application;
using QuotaSight.Core;

namespace QuotaSight.Infrastructure;

public sealed class CredentialBackedQuotaApplication : IQuotaApplication
{
    private readonly Func<string, IQuotaAdapter> adapterFactory;
    private readonly ICredentialStore credentials;
    private readonly IQuotaHistory history;
    private readonly CodexSessionManager? codexSessionManager;
    private readonly TimeSpan providerTimeout;

    public CredentialBackedQuotaApplication(
        Func<string, IQuotaAdapter> adapterFactory,
        ICredentialStore credentials,
        IQuotaHistory history)
        : this(adapterFactory, credentials, history, null, null)
    {
    }

    public CredentialBackedQuotaApplication(
        Func<string, IQuotaAdapter> adapterFactory,
        ICredentialStore credentials,
        IQuotaHistory history,
        TimeProvider? timeProvider)
        : this(adapterFactory, credentials, history, timeProvider, null)
    {
    }

    public CredentialBackedQuotaApplication(
        Func<string, IQuotaAdapter> adapterFactory,
        ICredentialStore credentials,
        IQuotaHistory history,
        CodexSessionManager? codexSessionManager)
        : this(adapterFactory, credentials, history, null, codexSessionManager)
    {
    }

    public CredentialBackedQuotaApplication(
        Func<string, IQuotaAdapter> adapterFactory,
        ICredentialStore credentials,
        IQuotaHistory history,
        TimeProvider? timeProvider,
        CodexSessionManager? codexSessionManager,
        TimeSpan? providerTimeout = null)
    {
        this.adapterFactory = adapterFactory;
        this.credentials = credentials;
        this.history = history;
        this.codexSessionManager = codexSessionManager;
        _ = timeProvider;
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
        if (snapshots.Length == 0) return new(snapshots, failures);

        await history.AppendAsync(snapshots, cancellationToken);
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

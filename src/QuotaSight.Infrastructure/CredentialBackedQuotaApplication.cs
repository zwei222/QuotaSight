using QuotaSight.Application;
using QuotaSight.Core;

namespace QuotaSight.Infrastructure;

public sealed class CredentialBackedQuotaApplication : IQuotaApplication
{
    private readonly Func<string, IQuotaAdapter> adapterFactory;
    private readonly ICredentialStore credentials;
    private readonly IQuotaHistory history;
    private readonly CodexSessionManager? codexSessionManager;

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
        CodexSessionManager? codexSessionManager)
    {
        this.adapterFactory = adapterFactory;
        this.credentials = credentials;
        this.history = history;
        this.codexSessionManager = codexSessionManager;
        _ = timeProvider;
    }

    public async ValueTask<IReadOnlyList<QuotaSnapshot>> RefreshAsync(CancellationToken cancellationToken)
    {
        var openCodeTask = FetchOpenCodeAsync(cancellationToken).AsTask();
        var codexTask = FetchCodexAsync(cancellationToken).AsTask();
        var results = await Task.WhenAll(openCodeTask, codexTask);
        var snapshots = results[0].Concat(results[1]).ToArray();
        if (snapshots.Length == 0) return [];

        await history.AppendAsync(snapshots, cancellationToken);
        return snapshots;
    }

    private async ValueTask<IReadOnlyList<QuotaSnapshot>> FetchOpenCodeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var key = await credentials.GetAsync("OpenCode Go", cancellationToken);
            if (string.IsNullOrWhiteSpace(key)) return [];

            var result = await adapterFactory(key).FetchAsync("OpenCode Go", cancellationToken);
            return result.IsSuccess && result.Value is { Count: > 0 } ? result.Value : [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    private async ValueTask<IReadOnlyList<QuotaSnapshot>> FetchCodexAsync(CancellationToken cancellationToken)
    {
        if (codexSessionManager is null) return [];

        try
        {
            var result = await codexSessionManager.FetchAsync(cancellationToken);
            return result.IsSuccess && result.Value is { Count: > 0 } ? result.Value : [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }
}

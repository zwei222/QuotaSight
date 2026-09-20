using QuotaSight.Core;

namespace QuotaSight.Application;

public interface IQuotaAdapter { ProviderKind Provider { get; } ValueTask<FetchResult<IReadOnlyList<QuotaSnapshot>>> FetchAsync(string account, CancellationToken cancellationToken); }
public sealed record ActiveAccountFetchResult(FetchResult<IReadOnlyList<QuotaSnapshot>> Result, string? ActiveAccount = null);
public interface IActiveAccountQuotaAdapter : IQuotaAdapter
{
    ValueTask<ActiveAccountFetchResult> FetchWithAccountAsync(string account, CancellationToken cancellationToken);
}
public interface IManualQuotaService { ValueTask<QuotaSnapshot> SetAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken); }
public interface ICredentialStore { CredentialStoreAvailability Availability { get; } ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken); ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken); ValueTask RemoveAsync(string account, CancellationToken cancellationToken); }
public enum CredentialStoreAvailability { SecureStore, SessionOnly, InMemoryFallback, Unavailable, Locked }
public sealed record QuotaHistoryEntry(Guid EventId, QuotaSnapshot Snapshot);
public interface IQuotaHistory { ValueTask AppendAsync(IReadOnlyList<QuotaSnapshot> snapshots, CancellationToken cancellationToken); ValueTask<IReadOnlyList<QuotaSnapshot>> ReadAsync(DateOnly day, CancellationToken cancellationToken); ValueTask<IReadOnlyList<QuotaHistoryEntry>> ReadEventsAsync(DateOnly day, CancellationToken cancellationToken); ValueTask DeleteEventAsync(Guid eventId, CancellationToken cancellationToken); ValueTask PruneAsync(DateOnly before, CancellationToken cancellationToken); ValueTask DeleteAsync(DateOnly? day, CancellationToken cancellationToken); ValueTask ExportJsonAsync(Stream output, CancellationToken cancellationToken); ValueTask ExportCsvAsync(Stream output, CancellationToken cancellationToken); }
public interface IGitHubAuthenticator { ValueTask<FetchResult<string>> AuthenticateAsync(CancellationToken cancellationToken); }
public interface IRefreshScheduler { ValueTask RunAsync(Func<CancellationToken, ValueTask> refresh, CancellationToken cancellationToken); }
public interface INotificationSink { ValueTask NotifyAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken); }
public sealed record ProviderFailure(ProviderKind Provider, FetchStatus Status, TimeSpan? RetryAfter = null);
public sealed record QuotaRefreshResult
{
    public QuotaRefreshResult(IReadOnlyList<QuotaSnapshot> snapshots, IReadOnlyList<ProviderFailure> failures, IReadOnlySet<ProviderKind>? noDataProviders = null, IReadOnlyDictionary<ProviderKind, string>? activeAccounts = null)
    {
        Snapshots = snapshots;
        Failures = failures;
        NoDataProviders = noDataProviders ?? new HashSet<ProviderKind>();
        ActiveAccounts = activeAccounts ?? new Dictionary<ProviderKind, string>();
    }

    public IReadOnlyList<QuotaSnapshot> Snapshots { get; }
    public IReadOnlyList<ProviderFailure> Failures { get; }
    public IReadOnlySet<ProviderKind> NoDataProviders { get; }
    public IReadOnlyDictionary<ProviderKind, string> ActiveAccounts { get; }
}
public interface IQuotaApplication { ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken); }
public sealed record NotificationKey(ProviderKind Provider, string Account, string Metric, QuotaWindowKind Window, decimal Threshold);

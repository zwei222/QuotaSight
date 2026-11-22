namespace QuotaSight.Application;

public interface ICredentialStore { CredentialStoreAvailability Availability { get; } ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken); ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken); ValueTask RemoveAsync(string account, CancellationToken cancellationToken); }
public enum CredentialStoreAvailability { SecureStore, SessionOnly, InMemoryFallback, Unavailable, Locked }
public interface IRefreshScheduler { ValueTask RunAsync(Func<CancellationToken, ValueTask> refresh, CancellationToken cancellationToken); }

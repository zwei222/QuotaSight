namespace QuotaSight.Application;

public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    public CredentialStoreAvailability Availability => CredentialStoreAvailability.SessionOnly;
    public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken) => ValueTask.FromResult(values.GetValueOrDefault(account));
    public ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken) { values[account] = secret; return ValueTask.CompletedTask; }
    public ValueTask RemoveAsync(string account, CancellationToken cancellationToken) { values.Remove(account); return ValueTask.CompletedTask; }
}

public static class SecureCredentialStoreFactory
{
    public static ICredentialStore Create() => OperatingSystem.IsWindows()
        ? new WindowsCredentialStore(new WindowsCredentialApi())
        : new UnavailableCredentialStore();

    private sealed class UnavailableCredentialStore : ICredentialStore
    {
        public CredentialStoreAvailability Availability => CredentialStoreAvailability.Unavailable;
        public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
        public ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken) => ValueTask.FromException(new PlatformNotSupportedException("No safe AOT credential backend is available."));
        public ValueTask RemoveAsync(string account, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}

using QuotaSight.Core;

namespace QuotaSight.Application;

public sealed class FallbackCredentialStore(ICredentialStore primary, InMemoryCredentialStore fallback) : ICredentialStore
{
    public CredentialStoreAvailability Availability => primary.Availability is CredentialStoreAvailability.Unavailable or CredentialStoreAvailability.Locked ? primary.Availability : CredentialStoreAvailability.SecureStore;
    public async ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken)
        => await primary.GetAsync(account, cancellationToken) ?? await fallback.GetAsync(account, cancellationToken);
    public async ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken)
    {
        try { await primary.SetAsync(account, secret, cancellationToken); }
        catch (Exception) { await fallback.SetAsync(account, secret, cancellationToken); return; }
        if (primary.Availability is CredentialStoreAvailability.Unavailable or CredentialStoreAvailability.Locked)
            await fallback.SetAsync(account, secret, cancellationToken);
    }
    public async ValueTask RemoveAsync(string account, CancellationToken cancellationToken)
    {
        try { await primary.RemoveAsync(account, cancellationToken); } finally { await fallback.RemoveAsync(account, cancellationToken); }
    }
}

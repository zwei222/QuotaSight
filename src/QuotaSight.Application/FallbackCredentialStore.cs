using QuotaSight.Core;

namespace QuotaSight.Application;

public sealed class FallbackCredentialStore(ICredentialStore primary, ICredentialStore fallback) : ICredentialStore
{
    public CredentialStoreAvailability Availability => primary.Availability is CredentialStoreAvailability.Unavailable or CredentialStoreAvailability.Locked ? primary.Availability : CredentialStoreAvailability.SecureStore;
    public async ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken)
        => await fallback.GetAsync(account, cancellationToken) ?? await primary.GetAsync(account, cancellationToken);
    public async ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken)
    {
        try { await primary.SetAsync(account, secret, cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await fallback.SetAsync(account, secret, cancellationToken);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            await fallback.SetAsync(account, secret, cancellationToken);
            return;
        }
        if (primary.Availability is CredentialStoreAvailability.Unavailable or CredentialStoreAvailability.Locked)
        {
            await fallback.SetAsync(account, secret, cancellationToken);
            return;
        }
        await fallback.RemoveAsync(account, cancellationToken);
    }
    public async ValueTask RemoveAsync(string account, CancellationToken cancellationToken)
    {
        Exception? primaryError = null;
        try { await primary.RemoveAsync(account, cancellationToken); }
        catch (Exception error) { primaryError = error; }
        try { await fallback.RemoveAsync(account, cancellationToken); }
        catch when (primaryError is not null) { }
        if (primaryError is not null) throw primaryError;
    }
}

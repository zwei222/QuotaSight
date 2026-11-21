using System.Runtime.ExceptionServices;
using QuotaSight.Application;

namespace QuotaSight.UI;

/// <summary>
/// Connection state of the usage API as the settings page may show it. It is derived from
/// the stored endpoint and the credential store availability, never from a live request.
/// </summary>
public enum UsageConnectionState
{
    /// <summary>No usable endpoint or no credential pair is stored.</summary>
    Unconfigured,

    /// <summary>The endpoint and a credential pair are stored in a secure credential store.</summary>
    Configured,

    /// <summary>The endpoint and a credential pair are usable, but the pair lives in this session only.</summary>
    SessionOnly,

    /// <summary>The credential store is unavailable or locked, so nothing can be read or saved.</summary>
    StoreUnavailable,

    /// <summary>The stored endpoint does not satisfy the endpoint rules.</summary>
    Invalid,
}

/// <summary>
/// Classified reason for a refused connection change. It is a code, never a sentence: the
/// copy for it lives in <see cref="UiCopy"/> so no user input can be echoed back.
/// </summary>
public enum UsageConnectionError
{
    /// <summary>Nothing failed.</summary>
    None,

    /// <summary>The endpoint was empty.</summary>
    Missing,

    /// <summary>The endpoint is not an absolute URI.</summary>
    Malformed,

    /// <summary>The endpoint does not use https. Cleartext is refused even on loopback.</summary>
    NotHttps,

    /// <summary>The endpoint carries user information.</summary>
    UserInformation,

    /// <summary>The endpoint carries a query.</summary>
    Query,

    /// <summary>The endpoint carries a fragment.</summary>
    Fragment,

    /// <summary>The endpoint carries a path.</summary>
    Path,

    /// <summary>The endpoint has no host name.</summary>
    HostMissing,

    /// <summary>The endpoint is longer than <see cref="UsageEndpoint.MaxLength"/>.</summary>
    TooLong,

    /// <summary>The endpoint contains whitespace or control characters.</summary>
    InvalidCharacters,

    /// <summary>The client id or the client secret cannot be sent as a header token.</summary>
    InvalidCredential,

    /// <summary>The credential store refused or failed the write, so nothing was saved.</summary>
    CredentialStoreUnavailable,
}

/// <summary>Outcome of a connection save attempt.</summary>
public sealed record UsageConnectionSaveResult(bool Succeeded, UsageConnectionError Error, UsageConnectionState State)
{
    /// <summary>A save that stored both the endpoint and the credential pair.</summary>
    public static UsageConnectionSaveResult Saved(UsageConnectionState state) => new(true, UsageConnectionError.None, state);

    /// <summary>A save that changed nothing, or that rolled back everything it wrote.</summary>
    public static UsageConnectionSaveResult Failed(UsageConnectionError error, UsageConnectionState state) => new(false, error, state);
}

/// <summary>
/// The single place that decides whether a usage API endpoint may be stored and used.
///
/// The rules match the collector's own <c>--endpoint</c> validation: https only (cleartext
/// is refused even on loopback), no user information, no query, no fragment, an empty or
/// <c>/</c> path, a required host name, no whitespace or control characters, a bounded
/// length, and a normalized origin without a trailing slash. The code is deliberately not
/// shared with the collector: each side keeps its own tested copy.
/// </summary>
public static class UsageEndpoint
{
    /// <summary>Longest accepted endpoint text, including the scheme.</summary>
    public const int MaxLength = 256;

    /// <summary>
    /// Validates and normalizes an endpoint into <c>scheme://host[:port]</c>.
    /// </summary>
    /// <param name="raw">Text exactly as entered by the user.</param>
    /// <param name="endpoint">Normalized origin, or an empty string when the input is refused.</param>
    /// <param name="error">Classified reason the input was refused, or <see cref="UsageConnectionError.None"/>.</param>
    /// <returns><c>true</c> when the endpoint may be stored and used.</returns>
    public static bool TryNormalize(string? raw, out string endpoint, out UsageConnectionError error)
    {
        endpoint = string.Empty;
        error = UsageConnectionError.None;

        // Surrounding whitespace is what a text box hands over; anything inside is refused.
        var candidate = raw?.Trim();
        if (string.IsNullOrEmpty(candidate))
        {
            error = UsageConnectionError.Missing;
            return false;
        }

        if (candidate.Length > MaxLength)
        {
            error = UsageConnectionError.TooLong;
            return false;
        }

        foreach (var character in candidate)
        {
            // Refused before parsing: a control character could split a header or a request.
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                error = UsageConnectionError.InvalidCharacters;
                return false;
            }
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            error = UsageConnectionError.Malformed;
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            error = UsageConnectionError.NotHttps;
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = UsageConnectionError.UserInformation;
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query))
        {
            error = UsageConnectionError.Query;
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            error = UsageConnectionError.Fragment;
            return false;
        }

        if (uri.AbsolutePath is not ("" or "/"))
        {
            error = UsageConnectionError.Path;
            return false;
        }

        if (string.IsNullOrEmpty(uri.IdnHost))
        {
            error = UsageConnectionError.HostMissing;
            return false;
        }

        endpoint = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}

/// <summary>Result of writing the credential pair to the credential store.</summary>
public enum UsageCredentialSaveOutcome
{
    /// <summary>Both values were stored in the secure credential store.</summary>
    Saved,

    /// <summary>Both values were stored, but only for this session.</summary>
    SessionOnly,

    /// <summary>The values cannot be sent as header tokens, so nothing was written.</summary>
    Invalid,

    /// <summary>The store is unavailable or locked, so nothing was written.</summary>
    StoreUnavailable,

    /// <summary>The store failed the write; whatever was written has been removed again.</summary>
    WriteFailed,
}

/// <summary>
/// Reads the usage read credential from <see cref="ICredentialStore"/> and is the only place
/// that knows the two account names.
///
/// The client id and the client secret are both treated as internal values: neither is ever
/// part of a message, a log line or a settings file. A pair is offered only when both values
/// are present and both satisfy <see cref="UsageCredential"/>; a partial or malformed pair is
/// answered with <c>null</c> and never with an exception.
/// </summary>
public sealed class StoreUsageCredentialProvider(ICredentialStore store) : IUsageCredentialProvider
{
    /// <summary>Account name of the usage read client id inside the credential store.</summary>
    public const string ClientIdAccount = "usage-access-client-id";

    /// <summary>Account name of the usage read client secret inside the credential store.</summary>
    public const string ClientSecretAccount = "usage-access-client-secret";

    /// <summary>
    /// Stable, non-secret identity this pair belongs to. Ports created for the usage API are
    /// scoped to it, so a credential stored for another purpose is never sent.
    /// </summary>
    public const string IdentityId = "usage-access";

    /// <summary>Current availability of the backing store; it is refreshed by the store itself.</summary>
    public CredentialStoreAvailability Availability => store.Availability;

    /// <summary>True when both values are present and valid.</summary>
    public async ValueTask<bool> HasCredentialAsync(CancellationToken cancellationToken) =>
        await GetCredentialAsync(cancellationToken).ConfigureAwait(false) is not null;

    /// <inheritdoc />
    public async ValueTask<UsageCredential?> GetCredentialAsync(CancellationToken cancellationToken)
    {
        string? clientId;
        string? clientSecret;
        try
        {
            clientId = await store.GetAsync(ClientIdAccount, cancellationToken).ConfigureAwait(false);
            clientSecret = await store.GetAsync(ClientSecretAccount, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A store that cannot answer is reported as "no credential", never as a failure
            // that would carry store text towards the UI.
            return null;
        }

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            return null;
        }

        try
        {
            return new UsageCredential(IdentityId, clientId, clientSecret);
        }
        catch (ArgumentException)
        {
            // Validation belongs to UsageCredential; a refused pair is simply not available.
            return null;
        }
    }

    /// <summary>
    /// Reads the stored pair so a caller can roll a failed save back. The values are internal:
    /// they must not be shown, logged or written anywhere.
    /// </summary>
    public async ValueTask<(string? ClientId, string? ClientSecret)> ReadStoredPairForRollbackAsync(CancellationToken cancellationToken)
    {
        var clientId = await store.GetAsync(ClientIdAccount, cancellationToken).ConfigureAwait(false);
        var clientSecret = await store.GetAsync(ClientSecretAccount, cancellationToken).ConfigureAwait(false);
        return (clientId, clientSecret);
    }

    /// <summary>
    /// Validates and stores the pair. A store that is unavailable or locked refuses the write
    /// instead of falling back to a plaintext file; a store that keeps values in memory for
    /// this session only is accepted and reported as <see cref="UsageCredentialSaveOutcome.SessionOnly"/>.
    /// </summary>
    public async ValueTask<UsageCredentialSaveOutcome> SaveAsync(string clientId, string clientSecret, CancellationToken cancellationToken)
    {
        try
        {
            _ = new UsageCredential(IdentityId, clientId, clientSecret);
        }
        catch (ArgumentException)
        {
            return UsageCredentialSaveOutcome.Invalid;
        }

        if (store.Availability is CredentialStoreAvailability.Unavailable or CredentialStoreAvailability.Locked)
        {
            return UsageCredentialSaveOutcome.StoreUnavailable;
        }

        try
        {
            await store.SetAsync(ClientIdAccount, clientId, cancellationToken).ConfigureAwait(false);
            await store.SetAsync(ClientSecretAccount, clientSecret, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RemoveQuietlyAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            // Never leave half a pair behind: a client id without its secret is useless and
            // would only make the next read look configured.
            await RemoveQuietlyAsync(CancellationToken.None).ConfigureAwait(false);
            return UsageCredentialSaveOutcome.WriteFailed;
        }

        // Availability is read after the write: a store that had to fall back to memory is
        // exactly the case the caller must show as "this session only".
        return store.Availability is CredentialStoreAvailability.SecureStore
            ? UsageCredentialSaveOutcome.Saved
            : UsageCredentialSaveOutcome.SessionOnly;
    }

    /// <summary>Removes both values. A failure to remove one still tries the other.</summary>
    public async ValueTask RemoveAsync(CancellationToken cancellationToken)
    {
        ExceptionDispatchInfo? failure = null;
        try
        {
            await store.RemoveAsync(ClientIdAccount, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            failure = ExceptionDispatchInfo.Capture(error);
        }

        try
        {
            await store.RemoveAsync(ClientSecretAccount, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            failure ??= ExceptionDispatchInfo.Capture(error);
        }

        failure?.Throw();
    }

    private async ValueTask RemoveQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RemoveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: the caller already reports the write as failed.
        }
    }
}

/// <summary>
/// One read port whose target (origin and credential provider) can be replaced at runtime,
/// over a single long-lived <see cref="HttpClient"/>.
///
/// A reconfigure swaps a reference; it never disposes the transport. That is what removes
/// the race the previous ownership model had to fence: an in-flight read keeps the port it
/// started with (and a client that stays alive), it completes under those settings, and a
/// later read starts under the new ones. The client is created once for the application
/// (or supplied by the caller) and is never disposed here, so nothing can be used after
/// disposal and no memory fence is needed between a read and a swap.
/// </summary>
public sealed class ReconfigurableUsageReadPort : IUsageReadPort, IDisposable
{
    /// <summary>Slack over one read's own deadline, so the port's deadline is what fires first.</summary>
    private static readonly TimeSpan ClientTimeout =
        HttpUsageReadPort.DefaultRequestDeadline + TimeSpan.FromSeconds(15);

    /// <summary>
    /// The client shared by every configuration for the lifetime of the process. It carries
    /// no default header, follows no redirect and keeps no cookie (see
    /// <see cref="UsageReadTransport"/>), and it is deliberately never disposed: replacing
    /// a configuration must not invalidate a read that is still running.
    /// </summary>
    private static readonly HttpClient SharedClient = UsageReadTransport.CreateClient(ClientTimeout);

    private readonly HttpClient client;
    private Configuration configuration = Configuration.Unconfigured;
    private int disposed;

    /// <summary>
    /// Creates the adapter over <paramref name="client"/>, the transport every configuration
    /// uses. When it is omitted the process-wide client is used and stays owned by this type;
    /// a supplied client stays owned by the caller.
    /// </summary>
    public ReconfigurableUsageReadPort(HttpClient? client = null) => this.client = client ?? SharedClient;

    /// <summary>True when a real port is installed.</summary>
    public bool IsConfigured => Volatile.Read(ref configuration).IsConfigured;

    /// <summary>Fixed request endpoint of the installed port, when there is one.</summary>
    public Uri? Endpoint => Volatile.Read(ref configuration).Endpoint;

    /// <summary>
    /// Installs <paramref name="port"/> for subsequent reads. The port owns whatever client it
    /// was built with; this adapter never disposes it, so the caller keeps the lifetime.
    /// </summary>
    public void Configure(IUsageReadPort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Volatile.Write(ref configuration, new Configuration(port));
    }

    /// <summary>
    /// Installs a port for <paramref name="origin"/> over this adapter's own client. This is
    /// the production path: the credential pair is read once per request by
    /// <paramref name="credentials"/>, never captured in the request.
    /// </summary>
    public void Configure(Uri origin, string identityId, IUsageCredentialProvider credentials)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(credentials);
        Configure(new HttpUsageReadPort(client, origin, identityId, credentials));
    }

    /// <summary>Returns the port to the unconfigured state.</summary>
    public void Reset() => Configure(new UnconfiguredUsageReadPort());

    /// <inheritdoc />
    public Task<DailyUsage> ReadDailyAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken)
    {
        // One reference read decides the whole call: a reconfigure that happens now cannot
        // move this read to another origin, and nothing it replaces is disposed underneath it.
        var snapshot = Volatile.Read(ref configuration);
        return snapshot.Port.ReadDailyAsync(from, to, collectorId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<UsageBreakdown> ReadBreakdownAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken)
    {
        var snapshot = Volatile.Read(ref configuration);
        return snapshot.Port.ReadBreakdownAsync(from, to, collectorId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Only the installed reference is dropped. No client is disposed, because none belongs to
    /// a configuration: the shared client lives for the process and an injected one belongs to
    /// its caller.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref configuration, Configuration.Unconfigured);
    }

    private bool IsDisposed => Volatile.Read(ref disposed) != 0;

    private sealed record Configuration(IUsageReadPort Port)
    {
        public static Configuration Unconfigured { get; } = new(new UnconfiguredUsageReadPort());

        public bool IsConfigured => Port is not UnconfiguredUsageReadPort;

        public Uri? Endpoint => (Port as HttpUsageReadPort)?.Endpoint;
    }
}

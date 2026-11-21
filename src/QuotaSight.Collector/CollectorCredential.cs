using System.Diagnostics;

namespace QuotaSight.Collector;

/// <summary>
/// Cloudflare Access service-token pair for one send. Text and debugger views
/// omit both values.
/// </summary>
[DebuggerDisplay("CollectorAccessCredential")]
public sealed class CollectorAccessCredential
{
    /// <summary>Creates a credential after rejecting values that cannot be header tokens.</summary>
    public CollectorAccessCredential(string clientId, string clientSecret)
    {
        if (!IsToken(clientId, SystemdLoadCredentialSource.MaxClientIdBytes))
        {
            throw new ArgumentException("A client id must be a non-empty printable ASCII token without whitespace.", nameof(clientId));
        }

        if (!IsToken(clientSecret, SystemdLoadCredentialSource.MaxClientSecretBytes))
        {
            throw new ArgumentException("A client secret must be a non-empty printable ASCII token without whitespace.", nameof(clientSecret));
        }

        ClientId = clientId;
        ClientSecret = clientSecret;
    }

    /// <summary>Access service-token client id. Never log this value.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string ClientId { get; }

    /// <summary>Access service-token client secret. Never log this value.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string ClientSecret { get; }

    /// <summary>Deliberately omits the client id and the client secret.</summary>
    public override string ToString() => nameof(CollectorAccessCredential);

    private static bool IsToken(string? value, int maxLength)
    {
        if (value is null || value.Length is 0 || value.Length > maxLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is <= ' ' or >= '\u007f')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Why a systemd credential read could not produce a token pair.</summary>
public enum CollectorCredentialFailure
{
    /// <summary>A credential was read.</summary>
    None = 0,

    /// <summary>This source is Linux-only.</summary>
    UnsupportedPlatform,

    /// <summary><c>CREDENTIALS_DIRECTORY</c> is unset or blank.</summary>
    DirectoryUnset,

    /// <summary>The directory is not a fully qualified path.</summary>
    DirectoryNotAbsolute,

    /// <summary>The directory does not exist.</summary>
    DirectoryMissing,

    /// <summary>The directory is a symbolic link and is not followed.</summary>
    DirectorySymlink,

    /// <summary>The client id file is absent.</summary>
    ClientIdMissing,

    /// <summary>The client secret file is absent.</summary>
    ClientSecretMissing,

    /// <summary>The client id file is a symbolic link and is not followed.</summary>
    ClientIdSymlink,

    /// <summary>The client secret file is a symbolic link and is not followed.</summary>
    ClientSecretSymlink,

    /// <summary>The client id exceeds the byte bound.</summary>
    ClientIdTooLarge,

    /// <summary>The client secret exceeds the byte bound.</summary>
    ClientSecretTooLarge,

    /// <summary>The client id is empty after stripping one trailing line feed.</summary>
    ClientIdEmpty,

    /// <summary>The client secret is empty after stripping one trailing line feed.</summary>
    ClientSecretEmpty,

    /// <summary>The client id is not a printable ASCII token.</summary>
    ClientIdRejected,

    /// <summary>The client secret is not a printable ASCII token.</summary>
    ClientSecretRejected,

    /// <summary>The credential file could not be read.</summary>
    Unreadable,
}

/// <summary>
/// Outcome of a credential read. <see cref="ToString"/> never includes the
/// tokens or the directory path.
/// </summary>
public sealed class CollectorCredentialResult
{
    private CollectorCredentialResult(CollectorAccessCredential? credential, CollectorCredentialFailure failure)
    {
        Credential = credential;
        Failure = failure;
    }

    /// <summary>Token pair, present only on success.</summary>
    public CollectorAccessCredential? Credential { get; }

    /// <summary>Reason class. <see cref="CollectorCredentialFailure.None"/> on success.</summary>
    public CollectorCredentialFailure Failure { get; }

    /// <summary>True when a token pair was read.</summary>
    public bool Succeeded => Credential is not null && Failure == CollectorCredentialFailure.None;

    /// <summary>Successful read.</summary>
    public static CollectorCredentialResult Success(CollectorAccessCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return new CollectorCredentialResult(credential, CollectorCredentialFailure.None);
    }

    /// <summary>Typed failure. The message is the reason class only.</summary>
    public static CollectorCredentialResult Failed(CollectorCredentialFailure failure)
    {
        if (failure == CollectorCredentialFailure.None)
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }

        return new CollectorCredentialResult(null, failure);
    }

    /// <inheritdoc />
    public override string ToString() =>
        Succeeded ? "CollectorCredentialResult:Success" : $"CollectorCredentialResult:{Failure}";
}

/// <summary>
/// Supplies the Access service-token pair for one send. Implementations must
/// not fall back to arguments, environment variables or plaintext files.
/// </summary>
public interface ICollectorCredentialSource
{
    /// <summary>Reads the token pair, or a typed failure. Never throws for an unavailable credential.</summary>
    CollectorCredentialResult Read();
}

/// <summary>
/// Test and production injection for an explicit send. A null client means the
/// CLI creates the production handler; tests always inject a fake handler.
/// </summary>
public sealed class CollectorSendContext
{
    /// <summary>Injected client. Not disposed by the CLI.</summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>Credential source. Production uses systemd LoadCredential when this is null.</summary>
    public ICollectorCredentialSource? Credentials { get; init; }

    /// <summary>Backoff wait. Tests record the span and return immediately.</summary>
    public Action<TimeSpan>? Delay { get; init; }

    /// <summary>Jitter unit in [0, 1). Production uses a random unit.</summary>
    public Func<double>? NextJitterUnit { get; init; }
}

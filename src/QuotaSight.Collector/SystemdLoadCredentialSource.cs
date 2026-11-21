using System.Text;

namespace QuotaSight.Collector;

/// <summary>
/// Reads Cloudflare Access service tokens from systemd <c>LoadCredential</c>
/// files. There is no plaintext, argument or environment-variable fallback.
/// Symbolic links are rejected rather than followed. Non-Linux hosts are unsupported.
/// </summary>
public sealed class SystemdLoadCredentialSource : ICollectorCredentialSource
{
    /// <summary>Environment variable systemd sets to the credential directory.</summary>
    public const string DirectoryVariableName = "CREDENTIALS_DIRECTORY";

    /// <summary>Fixed client id file name. Not taken from input.</summary>
    public const string ClientIdFileName = "access-client-id";

    /// <summary>Fixed client secret file name. Not taken from input.</summary>
    public const string ClientSecretFileName = "access-client-secret";

    /// <summary>Maximum client id size in bytes, after stripping one trailing line feed.</summary>
    public const int MaxClientIdBytes = 256;

    /// <summary>Maximum client secret size in bytes, after stripping one trailing line feed.</summary>
    public const int MaxClientSecretBytes = 512;

    private readonly Func<bool> _isLinux;
    private readonly Func<string?> _directory;

    /// <summary>Production source: Linux only, directory from <see cref="DirectoryVariableName"/>.</summary>
    public SystemdLoadCredentialSource()
        : this(static () => OperatingSystem.IsLinux(), static () => Environment.GetEnvironmentVariable(DirectoryVariableName))
    {
    }

    /// <summary>Test seam. The directory function is the only path input.</summary>
    public SystemdLoadCredentialSource(Func<bool> isLinux, Func<string?> directory)
    {
        ArgumentNullException.ThrowIfNull(isLinux);
        ArgumentNullException.ThrowIfNull(directory);
        _isLinux = isLinux;
        _directory = directory;
    }

    /// <inheritdoc />
    public CollectorCredentialResult Read()
    {
        if (!_isLinux())
        {
            return CollectorCredentialResult.Failed(CollectorCredentialFailure.UnsupportedPlatform);
        }

        string? directory;
        try
        {
            directory = _directory();
        }
        catch (Exception)
        {
            return CollectorCredentialResult.Failed(CollectorCredentialFailure.Unreadable);
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            return CollectorCredentialResult.Failed(CollectorCredentialFailure.DirectoryUnset);
        }

        if (!Path.IsPathFullyQualified(directory))
        {
            return CollectorCredentialResult.Failed(CollectorCredentialFailure.DirectoryNotAbsolute);
        }

        if (IsSymlink(directory))
        {
            return CollectorCredentialResult.Failed(CollectorCredentialFailure.DirectorySymlink);
        }

        try
        {
            if (!Directory.Exists(directory))
            {
                return CollectorCredentialResult.Failed(CollectorCredentialFailure.DirectoryMissing);
            }
        }
        catch (Exception)
        {
            return CollectorCredentialResult.Failed(CollectorCredentialFailure.Unreadable);
        }

        var id = ReadToken(directory, ClientIdFileName, MaxClientIdBytes, isId: true);
        if (id.Failure != CollectorCredentialFailure.None)
        {
            return CollectorCredentialResult.Failed(id.Failure);
        }

        var secret = ReadToken(directory, ClientSecretFileName, MaxClientSecretBytes, isId: false);
        if (secret.Failure != CollectorCredentialFailure.None)
        {
            return CollectorCredentialResult.Failed(secret.Failure);
        }

        try
        {
            return CollectorCredentialResult.Success(new CollectorAccessCredential(id.Value!, secret.Value!));
        }
        catch (ArgumentException)
        {
            return CollectorCredentialResult.Failed(CollectorCredentialFailure.Unreadable);
        }
    }

    /// <summary>Omits the directory path and any token.</summary>
    public override string ToString() => nameof(SystemdLoadCredentialSource);

    private static bool IsSymlink(string path)
    {
        try
        {
            if (File.ResolveLinkTarget(path, returnFinalTarget: false) is not null)
            {
                return true;
            }

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }

            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static TokenRead ReadToken(string directory, string fileName, int limit, bool isId)
    {
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(directory, fileName));
            var expected = Path.Combine(Path.GetFullPath(directory), fileName);
            if (!string.Equals(full, expected, StringComparison.Ordinal))
            {
                return TokenRead.Fail(CollectorCredentialFailure.Unreadable);
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return TokenRead.Fail(CollectorCredentialFailure.Unreadable);
        }

        if (IsSymlink(full))
        {
            return TokenRead.Fail(isId
                ? CollectorCredentialFailure.ClientIdSymlink
                : CollectorCredentialFailure.ClientSecretSymlink);
        }

        try
        {
            var attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return TokenRead.Fail(isId
                    ? CollectorCredentialFailure.ClientIdSymlink
                    : CollectorCredentialFailure.ClientSecretSymlink);
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                return TokenRead.Fail(CollectorCredentialFailure.Unreadable);
            }
        }
        catch (FileNotFoundException)
        {
            return TokenRead.Fail(isId
                ? CollectorCredentialFailure.ClientIdMissing
                : CollectorCredentialFailure.ClientSecretMissing);
        }
        catch (DirectoryNotFoundException)
        {
            return TokenRead.Fail(CollectorCredentialFailure.DirectoryMissing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TokenRead.Fail(CollectorCredentialFailure.Unreadable);
        }

        try
        {
            using var stream = new FileStream(
                full,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.SequentialScan);
            var buffer = new byte[limit + 2];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == buffer.Length)
            {
                return TokenRead.Fail(isId
                    ? CollectorCredentialFailure.ClientIdTooLarge
                    : CollectorCredentialFailure.ClientSecretTooLarge);
            }

            var raw = buffer.AsSpan(0, read);
            if (!raw.IsEmpty && raw[^1] == (byte)'\n')
            {
                raw = raw[..^1];
            }

            if (raw.Length > limit)
            {
                return TokenRead.Fail(isId
                    ? CollectorCredentialFailure.ClientIdTooLarge
                    : CollectorCredentialFailure.ClientSecretTooLarge);
            }

            if (raw.Length == 0)
            {
                return TokenRead.Fail(isId
                    ? CollectorCredentialFailure.ClientIdEmpty
                    : CollectorCredentialFailure.ClientSecretEmpty);
            }

            foreach (var byteValue in raw)
            {
                if (byteValue <= (byte)' ' || byteValue >= 0x7F)
                {
                    return TokenRead.Fail(isId
                        ? CollectorCredentialFailure.ClientIdRejected
                        : CollectorCredentialFailure.ClientSecretRejected);
                }
            }

            return TokenRead.Ok(Encoding.ASCII.GetString(raw));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TokenRead.Fail(CollectorCredentialFailure.Unreadable);
        }
    }

    private readonly struct TokenRead
    {
        private TokenRead(CollectorCredentialFailure failure, string? value)
        {
            Failure = failure;
            Value = value;
        }

        public CollectorCredentialFailure Failure { get; }

        public string? Value { get; }

        public static TokenRead Fail(CollectorCredentialFailure failure) => new(failure, null);

        public static TokenRead Ok(string value) => new(CollectorCredentialFailure.None, value);
    }
}

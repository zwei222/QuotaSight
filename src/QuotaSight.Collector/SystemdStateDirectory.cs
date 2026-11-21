using System.Runtime.InteropServices;

namespace QuotaSight.Collector;

/// <summary>
/// The one directory the collector keeps its send state in, taken from
/// <c>STATE_DIRECTORY</c> (set by systemd <c>StateDirectory=</c>). It is never
/// taken from an argument or a settings file.
///
/// Validation is deliberately strict and happens before anything is read or
/// written: Linux x86-64 only, an absolute path in its own normalized form, a
/// real directory (never a symbolic link), owned by the effective user, with no
/// access for anybody else (0700). Failure reasons are fixed words; a path is
/// never part of one.
/// </summary>
public sealed class SystemdStateDirectory
{
    /// <summary>Environment variable that names the state directory.</summary>
    public const string DirectoryVariableName = "STATE_DIRECTORY";

    private const int NoEntry = 2;
    private const int NotADirectory = 20;

    private readonly Func<bool> _supported;
    private readonly Func<string?> _directory;

    /// <summary>Production source: <c>STATE_DIRECTORY</c> on Linux x86-64.</summary>
    public SystemdStateDirectory()
        : this(static () => IsSupportedPlatform, FromEnvironment)
    {
    }

    /// <summary>
    /// The only supported host: this slice reads <c>struct stat</c> with the
    /// x86-64 layout, so another architecture is refused rather than
    /// misinterpreted.
    /// </summary>
    public static bool IsSupportedPlatform =>
        OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>Test seam. The directory function is the only path input.</summary>
    public SystemdStateDirectory(Func<bool> supported, Func<string?> directory)
    {
        ArgumentNullException.ThrowIfNull(supported);
        ArgumentNullException.ThrowIfNull(directory);
        _supported = supported;
        _directory = directory;
    }

    /// <summary>Reads <c>STATE_DIRECTORY</c> without normalizing it.</summary>
    public static string? FromEnvironment() => Environment.GetEnvironmentVariable(DirectoryVariableName);

    /// <summary>Validates the directory and returns the reason class, or <see cref="CollectorStateFailure.None"/>.</summary>
    public CollectorStateFailure Validate() => Check(out _);

    /// <summary>Validates the directory and yields it unchanged, so callers never re-derive a path.</summary>
    public bool TryValidate(out string? directory) => Check(out directory) == CollectorStateFailure.None;

    /// <summary>Validates the directory and reports the reason class.</summary>
    public bool TryValidate(out string? directory, out CollectorStateFailure failure)
    {
        failure = Check(out directory);
        return failure == CollectorStateFailure.None;
    }

    /// <summary>Omits the directory path.</summary>
    public override string ToString() => nameof(SystemdStateDirectory);

    private CollectorStateFailure Check(out string? directory)
    {
        directory = null;
        if (!_supported())
        {
            return CollectorStateFailure.UnsupportedPlatform;
        }

        string? raw;
        try
        {
            raw = _directory();
        }
        catch (Exception)
        {
            return CollectorStateFailure.StateUnreadable;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return CollectorStateFailure.DirectoryUnset;
        }

        if (raw.Contains(':', StringComparison.Ordinal))
        {
            return CollectorStateFailure.DirectoryMultiple;
        }

        if (!Path.IsPathFullyQualified(raw))
        {
            return CollectorStateFailure.DirectoryNotAbsolute;
        }

        if (raw.Length > 1 && raw.EndsWith('/'))
        {
            // A trailing separator makes lstat follow a symbolic link, and it is
            // not a normalized form of the path.
            return CollectorStateFailure.DirectoryNotNormalized;
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(raw);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return CollectorStateFailure.DirectoryNotNormalized;
        }

        if (!string.Equals(normalized, raw, StringComparison.Ordinal))
        {
            return CollectorStateFailure.DirectoryNotNormalized;
        }

        switch (CollectorUnixNative.LStat(raw, out var stat))
        {
            case NativeCall.Unsupported:
                return CollectorStateFailure.UnsupportedPlatform;
            case NativeCall.Failed:
                var error = Marshal.GetLastPInvokeError();
                return error switch
                {
                    NoEntry => CollectorStateFailure.DirectoryMissing,
                    NotADirectory => CollectorStateFailure.DirectoryNotDirectory,
                    _ => CollectorStateFailure.StateUnreadable,
                };
        }

        if (stat.Type == CollectorUnixNative.SymlinkType)
        {
            return CollectorStateFailure.DirectorySymlink;
        }

        if (stat.Type != CollectorUnixNative.DirectoryType)
        {
            return CollectorStateFailure.DirectoryNotDirectory;
        }

        if (CollectorUnixNative.GetEffectiveUid(out var userId) != NativeCall.Ok)
        {
            return CollectorStateFailure.UnsupportedPlatform;
        }

        if (stat.UserId != userId)
        {
            return CollectorStateFailure.DirectoryWrongOwner;
        }

        if (stat.Permissions != CollectorUnixNative.DirectoryMode)
        {
            return CollectorStateFailure.DirectoryWrongMode;
        }

        directory = raw;
        return CollectorStateFailure.None;
    }
}

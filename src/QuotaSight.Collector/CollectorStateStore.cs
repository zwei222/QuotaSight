using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace QuotaSight.Collector;

/// <summary>
/// The state directory's three fixed files: the state document, its temporary
/// sibling and the lock. Nothing else is created.
///
/// A write is atomic and durable: a stale temporary file of ours is removed, the
/// temporary file is created with <c>O_CREAT|O_EXCL|O_NOFOLLOW</c>, forced to
/// 0600 with <c>fchmod</c> (so the umask cannot widen it), re-checked with
/// <c>fstat</c> (regular file, one link, our owner), written, fsynced, renamed
/// over the state document, and the directory is fsynced so the rename cannot be
/// lost.
///
/// The state document and the lock are opened with <c>O_NOFOLLOW</c> — the
/// document also with <c>O_NONBLOCK</c> so a FIFO cannot hang the tool — and
/// re-checked with <c>fstat</c> before a byte is read.
/// </summary>
public sealed class CollectorStateStore : IDisposable
{
    /// <summary>Fixed state document name.</summary>
    public const string StateFileName = "send-state.json";

    /// <summary>Fixed temporary sibling used for the atomic replace.</summary>
    public const string TempFileName = "send-state.json.tmp";

    /// <summary>Fixed lock file name.</summary>
    public const string LockFileName = "send.lock";

    /// <summary>Largest accepted state document.</summary>
    public const int MaxStateBytes = 4 * 1024;

    /// <summary>How long a send waits for another collector process.</summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(25);
    private const int NoEntry = 2;

    private CollectorStateStore(string directory)
    {
        DirectoryPath = directory;
        StatePath = Path.Combine(directory, StateFileName);
        TempPath = Path.Combine(directory, TempFileName);
        LockPath = Path.Combine(directory, LockFileName);
    }

    /// <summary>Validated state directory.</summary>
    public string DirectoryPath { get; }

    /// <summary>State document path.</summary>
    public string StatePath { get; }

    /// <summary>Temporary sibling path.</summary>
    public string TempPath { get; }

    /// <summary>Lock file path.</summary>
    public string LockPath { get; }

    /// <summary>Validates the directory and creates the handle set. Creates nothing.</summary>
    public static bool TryOpen(SystemdStateDirectory source, out CollectorStateStore? store, out CollectorStateFailure failure)
    {
        ArgumentNullException.ThrowIfNull(source);
        store = null;
        if (!source.TryValidate(out var directory, out failure))
        {
            return false;
        }

        store = new CollectorStateStore(directory!);
        failure = CollectorStateFailure.None;
        return true;
    }

    /// <summary>Reads the state document, or a typed failure. Never throws for a missing or unsafe file.</summary>
    public CollectorSendState? Read(out CollectorStateFailure failure)
    {
        var descriptor = CollectorUnixNative.Open(
            StatePath,
            CollectorUnixNative.OpenReadOnly | CollectorUnixNative.OpenNoFollow
                | CollectorUnixNative.OpenNonBlock | CollectorUnixNative.OpenCloseOnExec,
            0);
        if (descriptor < 0)
        {
            failure = Marshal.GetLastPInvokeError() == NoEntry
                ? CollectorStateFailure.StateMissing
                : CollectorStateFailure.StateUnreadable;
            return null;
        }

        using var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        if (CollectorUnixNative.FStat(handle, out var stat) != NativeCall.Ok
            || stat.Type != CollectorUnixNative.RegularFileType
            || stat.UserId != EffectiveUid()
            || stat.Permissions != CollectorUnixNative.FileMode)
        {
            failure = CollectorStateFailure.StateUnreadable;
            return null;
        }

        if (stat.Size > MaxStateBytes)
        {
            failure = CollectorStateFailure.StateTooLarge;
            return null;
        }

        if (stat.Size <= 0)
        {
            failure = CollectorStateFailure.Empty;
            return null;
        }

        // One byte of headroom so the bound is detected from the bytes read, not
        // only from the size reported by fstat.
        var buffer = new byte[MaxStateBytes + 1];
        var total = 0;
        try
        {
            using var stream = OpenStream(handle, FileAccess.Read);
            while (total < buffer.Length)
            {
                var read = stream.Read(buffer, total, buffer.Length - total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failure = CollectorStateFailure.StateUnreadable;
            return null;
        }

        if (total > MaxStateBytes)
        {
            failure = CollectorStateFailure.StateTooLarge;
            return null;
        }

        return CollectorSendState.TryParse(buffer.AsSpan(0, total), out var state, out failure) ? state : null;
    }

    /// <summary>Replaces the state document atomically and durably.</summary>
    public bool Write(CollectorSendState state, out CollectorStateFailure failure)
    {
        ArgumentNullException.ThrowIfNull(state);
        var bytes = state.ToCanonicalJson();

        if (!PrepareTemporaryPath())
        {
            failure = CollectorStateFailure.StateWriteFailed;
            return false;
        }

        var descriptor = CollectorUnixNative.Open(
            TempPath,
            CollectorUnixNative.OpenWriteOnly | CollectorUnixNative.OpenCreate
                | CollectorUnixNative.OpenExclusive | CollectorUnixNative.OpenNoFollow
                | CollectorUnixNative.OpenCloseOnExec,
            CollectorUnixNative.FileMode);
        if (descriptor < 0)
        {
            failure = CollectorStateFailure.StateWriteFailed;
            return false;
        }

        using (var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true))
        {
            // The mode is set on the descriptor, so the process umask can neither
            // widen nor narrow it.
            if (CollectorUnixNative.SetPermissions(handle, CollectorUnixNative.FileMode) != NativeCall.Ok)
            {
                failure = CollectorStateFailure.StateWriteFailed;
                return false;
            }

            if (CollectorUnixNative.FStat(handle, out var stat) != NativeCall.Ok
                || stat.Type != CollectorUnixNative.RegularFileType
                || stat.LinkCount != 1
                || stat.UserId != EffectiveUid()
                || stat.Permissions != CollectorUnixNative.FileMode)
            {
                failure = CollectorStateFailure.StateWriteFailed;
                return false;
            }

            try
            {
                using var stream = OpenStream(handle, FileAccess.Write);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                if (CollectorUnixNative.FlushToDisk(stream.SafeFileHandle) != NativeCall.Ok)
                {
                    failure = CollectorStateFailure.StateWriteFailed;
                    return false;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failure = CollectorStateFailure.StateWriteFailed;
                return false;
            }
        }

        if (CollectorUnixNative.Rename(TempPath, StatePath) != NativeCall.Ok)
        {
            failure = CollectorStateFailure.StateWriteFailed;
            return false;
        }

        if (CollectorUnixNative.FlushDirectory(DirectoryPath) != NativeCall.Ok)
        {
            failure = CollectorStateFailure.StateWriteFailed;
            return false;
        }

        failure = CollectorStateFailure.None;
        return true;
    }

    /// <summary>
    /// Takes the inter-process lock, waiting up to <see cref="LockTimeout"/>.
    /// <c>FileStream.Lock</c> is a per-process lock on Linux, so a second process
    /// is what this excludes; a second handle in this process is not.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public bool TryLock(out IDisposable? lease)
    {
        lease = null;
        var descriptor = CollectorUnixNative.Open(
            LockPath,
            CollectorUnixNative.OpenReadWrite | CollectorUnixNative.OpenCreate
                | CollectorUnixNative.OpenNoFollow | CollectorUnixNative.OpenCloseOnExec,
            CollectorUnixNative.FileMode);
        if (descriptor < 0)
        {
            return false;
        }

        var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        FileStream stream;
        try
        {
            stream = OpenStream(handle, FileAccess.ReadWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            handle.Dispose();
            return false;
        }

        try
        {
            if (CollectorUnixNative.FStat(handle, out var stat) != NativeCall.Ok
                || stat.Type != CollectorUnixNative.RegularFileType
                || stat.UserId != EffectiveUid()
                || stat.Permissions != CollectorUnixNative.FileMode)
            {
                stream.Dispose();
                return false;
            }

            var started = Stopwatch.GetTimestamp();
            while (true)
            {
                try
                {
                    stream.Lock(0, 1);
                    lease = new LockLease(stream);
                    return true;
                }
                catch (IOException)
                {
                    if (Stopwatch.GetElapsedTime(started) >= LockTimeout)
                    {
                        stream.Dispose();
                        return false;
                    }

                    Thread.Sleep(RetryInterval);
                }
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Takes the lock or throws. Used by the state commands and by tests.</summary>
    [SupportedOSPlatform("linux")]
    public IDisposable Lock() =>
        TryLock(out var lease)
            ? lease!
            : throw new TimeoutException("The collector state lock is held by another process.");

    /// <summary>Omits the directory path.</summary>
    public override string ToString() => nameof(CollectorStateStore);

    /// <inheritdoc />
    public void Dispose()
    {
        // The store holds no handle of its own: every handle is scoped to the
        // call that opened it, and the lock lease owns the lock stream.
    }

    /// <summary>
    /// Removes a leftover temporary file this user created, and refuses anything
    /// else at that path (a FIFO, a directory, another owner, a symbolic link).
    /// A crash between create and rename leaves such a file behind; removing it is
    /// the documented recovery, and it is safe because it is ours and private.
    /// </summary>
    private bool PrepareTemporaryPath()
    {
        switch (CollectorUnixNative.LStat(TempPath, out var stat))
        {
            case NativeCall.Unsupported:
                return false;
            case NativeCall.Failed:
                return Marshal.GetLastPInvokeError() == NoEntry;
            default:
                if (stat.Type != CollectorUnixNative.RegularFileType
                    || stat.LinkCount != 1
                    || stat.UserId != EffectiveUid())
                {
                    return false;
                }

                return CollectorUnixNative.Remove(TempPath) == NativeCall.Ok;
        }
    }

    /// <summary>Wraps an owned descriptor, disposing it if the stream cannot be created.</summary>
    private static FileStream OpenStream(SafeFileHandle handle, FileAccess access)
    {
        try
        {
            return new FileStream(handle, access, bufferSize: 1, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static uint EffectiveUid()
    {
        CollectorUnixNative.GetEffectiveUid(out var userId);
        return userId;
    }

    [SupportedOSPlatform("linux")]
    private sealed class LockLease(FileStream stream) : IDisposable
    {
        private FileStream? _held = stream;

        public void Dispose()
        {
            var held = Interlocked.Exchange(ref _held, null);
            if (held is null)
            {
                return;
            }

            try
            {
                held.Unlock(0, 1);
            }
            catch (IOException)
            {
                // The lock is released when the stream is closed as well; a
                // failed explicit unlock cannot leave it held.
            }
            finally
            {
                held.Dispose();
            }
        }
    }
}

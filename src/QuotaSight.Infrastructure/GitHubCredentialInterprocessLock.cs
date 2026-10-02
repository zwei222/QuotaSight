using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("QuotaSight.Tests")]

namespace QuotaSight.Infrastructure;

internal static class GitHubCredentialInterprocessLock
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(50);
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);

    internal static string GetLinuxLockPath(uint effectiveUid) =>
        $"/var/tmp/quotasight-{effectiveUid}/QuotaSight/github-credential.lock";

    private static readonly UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static uint GetEffectiveUid() => geteuid();

    private static bool IsSecureDirectory(string path, uint owner, UnixFileMode mode) =>
        TryGetUnixStat(path, followLinks: false, out var stat) &&
        (stat.Mode & FileTypeMask) == DirectoryType && stat.UserId == owner &&
        (stat.Mode & PermissionMask) == (uint)mode;

    [SupportedOSPlatform("linux")]
    private static void EnsurePrivateDirectory(string path, uint owner)
    {
        if (!TryGetUnixStat(path, followLinks: false, out _) && mkdir(path, (uint)PrivateDirectoryMode) != 0 &&
            !TryGetUnixStat(path, followLinks: false, out _))
            throw new IOException("GitHub credential lock directory could not be created safely.");
        EnsureSecureDirectory(path, owner, PrivateDirectoryMode);
    }

    private static void EnsureSecureDirectory(string path, uint owner, UnixFileMode mode)
    {
        if (!IsSecureDirectory(path, owner, mode)) throw new IOException("GitHub credential lock directory is unsafe.");
    }

    private static FileStream OpenSecureLinuxLock(string path)
    {
        const int readWrite = 2, nonBlocking = 0x800, create = 0x40, closeOnExec = 0x80000, noFollow = 0x20000;
        var fd = open(path, readWrite | nonBlocking | create | closeOnExec | noFollow, (uint)PrivateFileMode);
        if (fd < 0) throw new IOException("GitHub credential lock file could not be opened safely.");
        var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        if (fstat(handle, out var stat) != 0 || (stat.Mode & FileTypeMask) != RegularFileType ||
            stat.UserId != GetEffectiveUid() || (stat.Mode & PermissionMask) != (uint)PrivateFileMode)
        {
            handle.Dispose();
            throw new IOException("GitHub credential lock file is unsafe.");
        }
        return new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);
    }

    private static bool TryGetUnixStat(string path, bool followLinks, out LinuxStat stat)
    {
        var result = followLinks ? stat_path(path, out stat) : lstat(path, out stat);
        return result == 0;
    }

    private const uint FileTypeMask = 0xF000;
    private const uint DirectoryType = 0x4000;
    private const uint RegularFileType = 0x8000;
    private const uint PermissionMask = 0xFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        public ulong Device, Inode, LinkCount;
        public uint Mode, UserId, GroupId;
        private int padding;
        public ulong SpecialDevice;
        public long Size, BlockSize, Blocks;
        private long accessTime, accessTimeNanos, modifyTime, modifyTimeNanos, changeTime, changeTimeNanos;
        private long reserved0, reserved1, reserved2;
    }

    [DllImport("libc", EntryPoint = "geteuid", SetLastError = true)] private static extern uint geteuid();
    [DllImport("libc", EntryPoint = "stat", SetLastError = true)] private static extern int stat_path(string path, out LinuxStat stat);
    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)] private static extern int lstat(string path, out LinuxStat stat);
    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)] private static extern int mkdir(string path, uint mode);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int open(string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)] private static extern int fstat(SafeFileHandle fd, out LinuxStat stat);

    public static async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("GitHub credential coordination is unsupported on this platform.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LockTimeout);
        try
        {
            await ProcessGate.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("GitHub credential lock acquisition timed out.");
        }

        FileStream? stream = null;
        try
        {
            string directory;
            if (OperatingSystem.IsWindows())
            {
                var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localData)) throw new IOException("GitHub credential lock location is unavailable.");
                directory = Path.Combine(localData, "QuotaSight");
                Directory.CreateDirectory(directory);
            }
            else if (OperatingSystem.IsLinux())
            {
                var uid = GetEffectiveUid();
                var lockPath = GetLinuxLockPath(uid);
                directory = Path.GetDirectoryName(lockPath)!;
                EnsureSecureDirectory("/var/tmp", 0, UnixFileMode.StickyBit | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
                EnsurePrivateDirectory($"/var/tmp/quotasight-{uid}", uid);
                EnsurePrivateDirectory(directory, uid);
            }
            else
            {
                throw new PlatformNotSupportedException("GitHub credential coordination is unsupported on this platform.");
            }

            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.ReadWrite,
                BufferSize = 1
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = PrivateFileMode;
                stream = OpenSecureLinuxLock(Path.Combine(directory, "github-credential.lock"));
            }
            else
                stream = new FileStream(Path.Combine(directory, "github-credential.lock"), options);
            var timer = Stopwatch.StartNew();
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try
                {
                    var openedStream = stream ?? throw new IOException("GitHub credential lock could not be opened.");
                    if (OperatingSystem.IsWindows()) openedStream.Lock(0, 1);
                    else if (OperatingSystem.IsLinux()) openedStream.Lock(0, 1);
                    else throw new PlatformNotSupportedException("GitHub credential coordination is unsupported on this platform.");
                    var lease = new LockLease(openedStream);
                    stream = null;
                    return lease;
                }
                catch (IOException) when (timer.Elapsed < LockTimeout)
                {
                    try
                    {
                        await Task.Delay(RetryInterval, timeout.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException("GitHub credential lock acquisition timed out.");
                    }
                }
                catch
                {
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stream?.Dispose();
            ProcessGate.Release();
            throw new TimeoutException("GitHub credential lock acquisition timed out.");
        }
        catch
        {
            stream?.Dispose();
            ProcessGate.Release();
            throw;
        }
    }

    private sealed class LockLease(FileStream stream) : IDisposable
    {
        private FileStream? heldStream = stream;

        public void Dispose()
        {
            var held = Interlocked.Exchange(ref heldStream, null);
            if (held is null) return;
            try
            {
                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) held.Unlock(0, 1);
            }
            finally
            {
                held.Dispose();
                ProcessGate.Release();
            }
        }
    }
}

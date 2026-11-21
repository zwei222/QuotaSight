using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("QuotaSight.Collector.Tests")]

namespace QuotaSight.Collector;

/// <summary>How one native call ended.</summary>
internal enum NativeCall
{
    /// <summary>The call succeeded.</summary>
    Ok,

    /// <summary>The call was made and failed; the reason is in <c>errno</c>.</summary>
    Failed,

    /// <summary>This host does not expose the library or the symbol, so the feature is unsupported.</summary>
    Unsupported,
}

/// <summary>
/// The Linux calls the state directory needs, declared with
/// <see cref="LibraryImportAttribute"/> so the marshalling code is generated at
/// build time and survives Native AOT trimming.
///
/// The flag values and the <c>struct stat</c> layout below are the Linux x86-64
/// ones; arm64 values differ, which is why the state directory is refused on
/// that architecture instead of being read at the wrong offsets.
///
/// A host without glibc 2.33 or without the symbol must not crash the tool: a
/// missing library or entry point becomes <see cref="NativeCall.Unsupported"/>,
/// never an unhandled exception.
/// </summary>
internal static partial class CollectorUnixNative
{
    internal const int OpenReadOnly = 0;
    internal const int OpenWriteOnly = 1;
    internal const int OpenReadWrite = 2;

    /// <summary><c>O_CREAT</c>.</summary>
    internal const int OpenCreate = 0x40;

    /// <summary><c>O_EXCL</c>: fail if the path already exists, even as a symlink.</summary>
    internal const int OpenExclusive = 0x80;

    /// <summary><c>O_TRUNC</c>.</summary>
    internal const int OpenTruncate = 0x200;

    /// <summary><c>O_NONBLOCK</c>: a FIFO is opened instead of blocking on it.</summary>
    internal const int OpenNonBlock = 0x800;

    /// <summary><c>O_DIRECTORY</c>.</summary>
    internal const int OpenDirectory = 0x10000;

    /// <summary><c>O_NOFOLLOW</c>: a symbolic link is an error, not a target.</summary>
    internal const int OpenNoFollow = 0x20000;

    /// <summary><c>O_CLOEXEC</c>.</summary>
    internal const int OpenCloseOnExec = 0x80000;

    internal const uint FileTypeMask = 0xF000;
    internal const uint DirectoryType = 0x4000;
    internal const uint RegularFileType = 0x8000;
    internal const uint SymlinkType = 0xA000;
    internal const uint PermissionMask = 0xFFF;

    /// <summary>The permission bits this tool accepts (0700 for a directory, 0600 for a file).</summary>
    internal const uint DirectoryMode = 0x1C0;
    internal const uint FileMode = 0x180;

    /// <summary>Runs a native call, mapping a missing library or symbol to unsupported.</summary>
    internal static NativeCall Guard(Func<bool> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        try
        {
            return call() ? NativeCall.Ok : NativeCall.Failed;
        }
        catch (DllNotFoundException)
        {
            return NativeCall.Unsupported;
        }
        catch (EntryPointNotFoundException)
        {
            return NativeCall.Unsupported;
        }
    }

    internal static NativeCall GetEffectiveUid(out uint userId)
    {
        uint value = 0;
        var call = Guard(() =>
        {
            value = geteuid();
            return true;
        });
        userId = value;
        return call;
    }

    /// <summary><c>lstat</c>: never follows a symbolic link.</summary>
    internal static NativeCall LStat(string path, out LinuxStat stat)
    {
        LinuxStat value = default;
        var call = Guard(() => lstat(path, out value) == 0);
        stat = value;
        return call;
    }

    /// <summary>Opens a path with the given flags, or returns a negative value.</summary>
    internal static int Open(string path, int flags, uint mode) => open(path, flags, mode);

    internal static NativeCall FStat(SafeFileHandle handle, out LinuxStat stat)
    {
        LinuxStat value = default;
        var call = Guard(() => fstat(handle, out value) == 0);
        stat = value;
        return call;
    }

    /// <summary><c>fchmod</c>: sets the mode of an open file, independent of the umask.</summary>
    internal static NativeCall SetPermissions(SafeFileHandle handle, uint mode) =>
        Guard(() => fchmod(handle, mode) == 0);

    internal static NativeCall FlushToDisk(SafeFileHandle handle) => Guard(() => fsync(handle) == 0);

    internal static NativeCall Remove(string path) => Guard(() => unlink(path) == 0);

    internal static NativeCall Rename(string from, string to) => Guard(() => rename(from, to) == 0);

    /// <summary>Runs an action with a directory descriptor used only for <c>fsync</c>.</summary>
    internal static NativeCall FlushDirectory(string path)
    {
        var descriptor = open(path, OpenReadOnly | OpenDirectory | OpenCloseOnExec, 0);
        if (descriptor < 0)
        {
            return NativeCall.Failed;
        }

        using var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        return FlushToDisk(handle);
    }

    [LibraryImport("libc", EntryPoint = "geteuid", SetLastError = true)]
    private static partial uint geteuid();

    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int lstat(string path, out LinuxStat stat);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int open(string path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int fstat(SafeFileHandle descriptor, out LinuxStat stat);

    [LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static partial int fchmod(SafeFileHandle descriptor, uint mode);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int fsync(SafeFileHandle descriptor);

    [LibraryImport("libc", EntryPoint = "unlink", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int unlink(string path);

    [LibraryImport("libc", EntryPoint = "rename", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int rename(string from, string to);

    /// <summary>
    /// <c>struct stat</c> as Linux x86-64 defines it. The whole structure is
    /// declared because <c>lstat</c> and <c>fstat</c> write all of it; only type,
    /// mode, owner, link count and size are read.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LinuxStat
    {
        public ulong Device;
        public ulong Inode;
        public ulong LinkCount;
        public uint Mode;
        public uint UserId;
        public uint GroupId;
        private int padding;
        public ulong SpecialDevice;
        public long Size;
        public long BlockSize;
        public long Blocks;
        private long accessTime;
        private long accessTimeNanos;
        private long modifyTime;
        private long modifyTimeNanos;
        private long changeTime;
        private long changeTimeNanos;
        private long reserved0;
        private long reserved1;
        private long reserved2;

        public readonly uint Type => Mode & FileTypeMask;

        public readonly uint Permissions => Mode & PermissionMask;
    }
}

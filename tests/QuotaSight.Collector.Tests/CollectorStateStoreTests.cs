using System.Runtime.Versioning;

namespace QuotaSight.Collector.Tests;

/// <summary>
/// The store owns three fixed files in the state directory: the state document,
/// its temporary sibling and the lock. Everything is validated before it is
/// used, and a write is atomic (temporary file, fsync, rename, directory fsync).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class CollectorStateStoreTests
{
    private const string CollectorId = "col-store";

    private static void MakeFifo(string path)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("mkfifo") { UseShellExecute = false };
        startInfo.ArgumentList.Add(path);
        using var process = System.Diagnostics.Process.Start(startInfo);
        Assert.NotNull(process);
        Assert.True(process!.WaitForExit(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, process.ExitCode);
    }

    private static void UnblockFifo(string path)
    {
        try
        {
            using var unblock = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the assertion below reports the real problem.
        }
    }

    private static CollectorSendState Sample(long lastRevision = 2) =>
        CollectorSendState.Create(CollectorId, 7, lastRevision);

    private static CollectorStateStore Open(string directory)
    {
        Assert.True(CollectorStateStore.TryOpen(new SystemdStateDirectory(static () => true, () => directory), out var store, out var failure),
            $"store did not open: {failure}");
        return store!;
    }

    [LinuxFact]
    public void FileNamesAreFixed()
    {
        Assert.Equal("send-state.json", CollectorStateStore.StateFileName);
        Assert.Equal("send-state.json.tmp", CollectorStateStore.TempFileName);
        Assert.Equal("send.lock", CollectorStateStore.LockFileName);
        Assert.Equal(4 * 1024, CollectorStateStore.MaxStateBytes);
    }

    [LinuxFact]
    public void OpenRejectsADirectoryThatIsNotPrivate()
    {
        var directory = SendHarness.NewStateDirectory();
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);

            Assert.False(CollectorStateStore.TryOpen(
                new SystemdStateDirectory(static () => true, () => directory),
                out var store,
                out var failure));
            Assert.Null(store);
            Assert.Equal(CollectorStateFailure.DirectoryWrongMode, failure);
        }
        finally
        {
            File.SetUnixFileMode(directory, SendHarness.PrivateDirectoryMode);
            Directory.Delete(directory, recursive: true);
        }
    }

    [LinuxFact]
    public void MissingStateIsATypedNoState()
    {
        var directory = SendHarness.NewStateDirectory();
        try
        {
            using var store = Open(directory);

            var state = store.Read(out var failure);

            Assert.Null(state);
            Assert.Equal(CollectorStateFailure.StateMissing, failure);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [LinuxFact]
    public void WriteCreatesAPrivateFileAndLeavesNoTemporaryFile()
    {
        var directory = SendHarness.NewStateDirectory();
        try
        {
            using var store = Open(directory);
            using (store.Lock())
            {
                Assert.True(store.Write(Sample(), out var failure), $"write failed: {failure}");
            }

            Assert.True(File.Exists(store.StatePath));
            Assert.False(File.Exists(store.TempPath));
            Assert.Equal(SendHarness.PrivateFileMode, File.GetUnixFileMode(store.StatePath));
            Assert.Equal(Sample().ToCanonicalJson(), File.ReadAllBytes(store.StatePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [LinuxFact]
    public void WriteThenReadRoundTrips()
    {
        var directory = SendHarness.NewStateDirectory();
        try
        {
            using var store = Open(directory);
            using (store.Lock())
            {
                store.Write(Sample(9), out _);
            }

            var state = store.Read(out var failure);

            Assert.Equal(CollectorStateFailure.None, failure);
            Assert.Equal(9, state!.LastRevision);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [LinuxFact]
    public void ReadRejectsAGroupReadableStateFile()
    {
        var directory = SendHarness.NewStateDirectory();
        try
        {
            using var store = Open(directory);
            File.WriteAllBytes(store.StatePath, Sample().ToCanonicalJson());
            File.SetUnixFileMode(store.StatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

            Assert.Null(store.Read(out var failure));
            Assert.Equal(CollectorStateFailure.StateUnreadable, failure);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [LinuxFact]
    public void ReadRejectsASymlinkedStateFile()
    {
        var directory = SendHarness.NewStateDirectory();
        var outside = Path.Combine(Path.GetTempPath(), "qs-state-outside-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            File.WriteAllBytes(outside, Sample().ToCanonicalJson());
            File.SetUnixFileMode(outside, SendHarness.PrivateFileMode);
            using var store = Open(directory);
            File.CreateSymbolicLink(store.StatePath, outside);

            Assert.Null(store.Read(out var failure));
            Assert.Equal(CollectorStateFailure.StateUnreadable, failure);
        }
        finally
        {
            File.Delete(outside);
            Directory.Delete(directory, recursive: true);
        }
    }

    [LinuxFact]
    public void ReadRejectsAnOversizedStateFile()
    {
        var directory = SendHarness.NewStateDirectory();
        try
        {
            using var store = Open(directory);
            var oversized = new byte[CollectorStateStore.MaxStateBytes + 1];
            Array.Fill(oversized, (byte)' ');
            File.WriteAllBytes(store.StatePath, oversized);
            File.SetUnixFileMode(store.StatePath, SendHarness.PrivateFileMode);

            Assert.Null(store.Read(out var failure));
            Assert.Equal(CollectorStateFailure.StateTooLarge, failure);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [LinuxFact]
    public void ReadRejectsAHandEditedStateFile()
    {
        var directory = SendHarness.NewStateDirectory();
        try
        {
            using var store = Open(directory);
            File.WriteAllBytes(store.StatePath, "{\"stateVersion\":1}"u8.ToArray());
            File.SetUnixFileMode(store.StatePath, SendHarness.PrivateFileMode);

            Assert.Null(store.Read(out var failure));
            Assert.Equal(CollectorStateFailure.StateNotCanonical, failure);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [LinuxFact]
    public void AStateFileThatIsAFifoIsRefusedInsteadOfBlocking()
    {
        var directory = SendHarness.NewStateDirectory();
        try
        {
            using var store = Open(directory);
            MakeFifo(store.StatePath);

            var read = Task.Run(() =>
            {
                var state = store.Read(out var failure);
                return (State: state, Failure: failure);
            });

            if (!read.Wait(TimeSpan.FromSeconds(15)))
            {
                UnblockFifo(store.StatePath);
                Assert.Fail("reading a FIFO state file blocked");
            }

            Assert.Null(read.Result.State);
            Assert.Equal(CollectorStateFailure.StateUnreadable, read.Result.Failure);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [LinuxFact]
    public void AStateFileAtTheByteBoundIsJudgedOnItsContent()
    {
        var directory = SendHarness.NewStateDirectory();
        try
        {
            using var store = Open(directory);
            var document = "{\"stateVersion\":1}".PadRight(CollectorStateStore.MaxStateBytes, ' ');
            File.WriteAllText(store.StatePath, document);
            SendHarness.SetMode(store.StatePath, SendHarness.PrivateFileMode);

            Assert.Null(store.Read(out var failure));
            Assert.Equal(CollectorStateFailure.StateNotCanonical, failure);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [LinuxFact]
    public void AStateFileOneByteOverTheBoundIsTooLarge()
    {
        var directory = SendHarness.NewStateDirectory();
        try
        {
            using var store = Open(directory);
            var document = "{\"stateVersion\":1}".PadRight(CollectorStateStore.MaxStateBytes + 1, ' ');
            File.WriteAllText(store.StatePath, document);
            SendHarness.SetMode(store.StatePath, SendHarness.PrivateFileMode);

            Assert.Null(store.Read(out var failure));
            Assert.Equal(CollectorStateFailure.StateTooLarge, failure);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [LinuxFact]
    public void AStaleTemporaryFileOfOursIsReplaced()
    {
        var directory = SendHarness.NewStateDirectory();
        try
        {
            using var store = Open(directory);
            File.WriteAllBytes(store.TempPath, "stale"u8.ToArray());
            SendHarness.SetMode(store.TempPath, SendHarness.PrivateFileMode);

            using (store.Lock())
            {
                Assert.True(store.Write(Sample(), out var failure), $"write failed: {failure}");
            }

            Assert.False(File.Exists(store.TempPath));
            Assert.Equal(Sample().ToCanonicalJson(), File.ReadAllBytes(store.StatePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [LinuxFact]
    public void ATemporaryPathThatIsADirectoryIsRefused()
    {
        var directory = SendHarness.NewStateDirectory();
        try
        {
            using var store = Open(directory);
            Directory.CreateDirectory(store.TempPath);

            using (store.Lock())
            {
                Assert.False(store.Write(Sample(), out var failure));
                Assert.Equal(CollectorStateFailure.StateWriteFailed, failure);
            }

            Assert.False(File.Exists(store.StatePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [LinuxFact]
    public void ATemporaryPathThatIsAFifoIsRefusedInsteadOfBlocking()
    {
        var directory = SendHarness.NewStateDirectory();
        try
        {
            using var store = Open(directory);
            MakeFifo(store.TempPath);

            var write = Task.Run(() =>
            {
                var written = store.Write(Sample(), out var failure);
                return (Written: written, Failure: failure);
            });

            if (!write.Wait(TimeSpan.FromSeconds(15)))
            {
                UnblockFifo(store.TempPath);
                Assert.Fail("writing through a FIFO temporary path blocked");
            }

            Assert.False(write.Result.Written);
            Assert.Equal(CollectorStateFailure.StateWriteFailed, write.Result.Failure);
            Assert.False(File.Exists(store.StatePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [LinuxFact]
    public void ASymlinkedTemporaryPathIsRefusedWithoutWritingThroughIt()
    {
        var directory = SendHarness.NewStateDirectory();
        var outside = Path.Combine(Path.GetTempPath(), "qs-state-tmp-outside-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            using var store = Open(directory);
            File.CreateSymbolicLink(store.TempPath, outside);

            using (store.Lock())
            {
                Assert.False(store.Write(Sample(), out var failure));
                Assert.Equal(CollectorStateFailure.StateWriteFailed, failure);
            }

            Assert.False(File.Exists(store.StatePath));
            Assert.False(File.Exists(outside));
        }
        finally
        {
            File.Delete(outside);
            Directory.Delete(directory, recursive: true);
        }
    }

    [LinuxFact]
    public void TheLockCanBeTakenAgainAfterItIsReleased()
    {
        var directory = SendHarness.NewStateDirectory();
        try
        {
            using var store = Open(directory);
            var lease = store.Lock();
            lease.Dispose();

            using var second = store.Lock();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [LinuxFact]
    public void TheLockFileIsPrivate()
    {
        var directory = SendHarness.NewStateDirectory();
        try
        {
            using var store = Open(directory);
            using (store.Lock())
            {
            }

            Assert.True(File.Exists(store.LockPath));
            Assert.Equal(SendHarness.PrivateFileMode, File.GetUnixFileMode(store.LockPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

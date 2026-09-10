using System.Text.Json;
using QuotaSight.Application;

namespace QuotaSight.Infrastructure;

public sealed class AppSettingsStore(string? directory = null)
{
    private static readonly object GatesLock = new();
    private static readonly Dictionary<string, GateEntry> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public string Path { get; } = System.IO.Path.Combine(directory ?? PlatformPaths.ConfigDirectory(), "settings.json");

    public AppSettingsDto Load()
    {
        using var gate = AcquireGate();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        try
        {
            if (!File.Exists(Path)) return new();
            return JsonSerializer.Deserialize(File.ReadAllText(Path), AppSettingsJsonContext.Default.AppSettingsDto) ?? new();
        }
        catch (JsonException)
        {
            if (File.Exists(Path)) File.Copy(Path, Path + ".bak", true);
            return new();
        }
    }

    public async ValueTask<AppSettingsDto> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var gate = (await AcquireGateAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        try
        {
            if (!File.Exists(Path)) return new();
            return JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(Path, cancellationToken).ConfigureAwait(false),
                AppSettingsJsonContext.Default.AppSettingsDto) ?? new();
        }
        catch (JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(Path)) File.Copy(Path, Path + ".bak", true);
            return new();
        }
    }

    public async ValueTask SaveAsync(AppSettingsDto settings, CancellationToken cancellationToken = default)
    {
        await using var gate = (await AcquireGateAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temporaryPath = CreateTemporaryPath();
        try
        {
            var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                await JsonSerializer.SerializeAsync(stream, settings, AppSettingsJsonContext.Default.AppSettingsDto, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, Path, true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    public void Save(AppSettingsDto settings)
    {
        using var gate = AcquireGate();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temporaryPath = CreateTemporaryPath();
        try
        {
            using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.SequentialScan))
            {
                JsonSerializer.Serialize(stream, settings, AppSettingsJsonContext.Default.AppSettingsDto);
                stream.Flush(true);
            }

            File.Move(temporaryPath, Path, true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private string CreateTemporaryPath() => Path + ".tmp-" + Guid.NewGuid().ToString("N");

    private static void TryDeleteTemporaryFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private GateLease AcquireGate()
    {
        var entry = AddGate(PathKey());
        try
        {
            entry.Semaphore.Wait();
            return new GateLease(PathKey(), entry);
        }
        catch
        {
            RemoveGate(PathKey(), entry);
            throw;
        }
    }

    private async ValueTask<GateLease> AcquireGateAsync(CancellationToken cancellationToken)
    {
        var key = PathKey();
        var entry = AddGate(key);
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new GateLease(key, entry);
        }
        catch
        {
            RemoveGate(key, entry);
            throw;
        }
    }

    private string PathKey() => System.IO.Path.GetFullPath(Path);

    private static GateEntry AddGate(string key)
    {
        lock (GatesLock)
        {
            if (!Gates.TryGetValue(key, out var entry)) Gates.Add(key, entry = new GateEntry());
            entry.Users++;
            return entry;
        }
    }

    private static void RemoveGate(string key, GateEntry entry)
    {
        lock (GatesLock)
        {
            if (--entry.Users == 0 && Gates.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                Gates.Remove(key);
        }
    }

    private sealed class GateEntry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
    }

    private sealed class GateLease(string key, GateEntry entry) : IDisposable, IAsyncDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            entry.Semaphore.Release();
            RemoveGate(key, entry);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

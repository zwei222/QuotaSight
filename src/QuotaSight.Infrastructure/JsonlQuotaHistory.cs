using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaSight.Application;
using QuotaSight.Core;

namespace QuotaSight.Infrastructure;

public sealed record PersistedQuotaEvent(
    int SchemaVersion,
    Guid EventId,
    QuotaSnapshot Snapshot);

[JsonSerializable(typeof(PersistedQuotaEvent))]
[JsonSerializable(typeof(List<PersistedQuotaEvent>))]
[JsonSerializable(typeof(JsonlQuotaHistory.ExportQuotaDto))]
internal partial class QuotaJsonContext : JsonSerializerContext;

public sealed class JsonlQuotaHistory : IQuotaHistory, IDisposable
{
    private static readonly ConcurrentDictionary<string, byte> rootsInUse = new(StringComparer.OrdinalIgnoreCase);
    private readonly string root;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly FileStream processLock;
    private int disposed;

    public JsonlQuotaHistory(string root, TimeProvider? timeProvider = null)
    {
        this.root = Path.GetFullPath(root);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(this.root);
        if (!rootsInUse.TryAdd(this.root, 0)) throw new IOException($"Quota history is already open: {this.root}");

        FileStream? lockStream = null;
        try
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Quota history locking requires Windows or Linux.");
            lockStream = new FileStream(Path.Combine(this.root, ".history.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.None);
            // FileStream.Lock is implemented by the supported Windows/Linux runtimes; CA1416 only models Windows.
#pragma warning disable CA1416
            lockStream.Lock(0, 1);
#pragma warning restore CA1416
            processLock = lockStream;
        }
        catch
        {
            lockStream?.Dispose();
            rootsInUse.TryRemove(this.root, out _);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
#pragma warning disable CA1416
            processLock.Unlock(0, 1);
#pragma warning restore CA1416
        }
        processLock.Dispose();
        rootsInUse.TryRemove(root, out _);
        writer.Dispose();
    }

    private string PathFor(DateOnly day) => Path.Combine(root, $"{day:yyyy-MM-dd}.jsonl");

    public async ValueTask AppendAsync(IReadOnlyList<QuotaSnapshot> snapshots, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        var day = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        await writer.WaitAsync(cancellationToken);
        try
        {
            var path = PathFor(day);
            await RepairTailAsync(path, cancellationToken);
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, true);
            foreach (var snapshot in snapshots)
            {
                var item = new PersistedQuotaEvent(1, Guid.NewGuid(), snapshot);
                await JsonSerializer.SerializeAsync(stream, item, QuotaJsonContext.Default.PersistedQuotaEvent, cancellationToken);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
            }
        }
        finally
        {
            writer.Release();
        }
    }

    public async ValueTask<IReadOnlyList<QuotaSnapshot>> ReadAsync(DateOnly day, CancellationToken cancellationToken)
    {
        return (await ReadEventsAsync(day, cancellationToken)).Select(e => e.Snapshot).ToList();
    }

    public async ValueTask<IReadOnlyList<QuotaHistoryEntry>> ReadEventsAsync(DateOnly day, CancellationToken cancellationToken)
    {
        await writer.WaitAsync(cancellationToken);
        try { return await ReadEventsCoreAsync(day, cancellationToken); }
        finally { writer.Release(); }
    }

    private async ValueTask<IReadOnlyList<QuotaHistoryEntry>> ReadEventsCoreAsync(DateOnly day, CancellationToken cancellationToken)
    {
        var path = PathFor(day); if (!File.Exists(path)) return [];
        await RepairTailAsync(path, cancellationToken);
        var lines = await File.ReadAllLinesAsync(path, cancellationToken); var result = new List<QuotaHistoryEntry>();
        for (var index = 0; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index])) continue;
            try { var item = JsonSerializer.Deserialize(lines[index], QuotaJsonContext.Default.PersistedQuotaEvent) ?? throw new InvalidDataException("Empty JSONL event."); result.Add(new(item.EventId, item.Snapshot)); }
            catch (JsonException exception) { throw new InvalidDataException($"Corrupt JSONL at line {index + 1}.", exception); }
        }
        return result;
    }

    public async ValueTask DeleteEventAsync(Guid eventId, CancellationToken cancellationToken)
    {
        await writer.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl"))
            {
                var dayText = Path.GetFileNameWithoutExtension(file);
                if (!DateOnly.TryParseExact(dayText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
                IReadOnlyList<QuotaHistoryEntry> existing;
                try { existing = await ReadEventsCoreAsync(day, cancellationToken); }
                catch (InvalidDataException) { continue; }
                var events = existing.Where(e => e.EventId != eventId).ToList();
                if (events.Count == 0) { if (existing.Count > 0) File.Delete(file); continue; }
                if (events.Count != existing.Count) await RewriteAsync(file, events.Select(e => new PersistedQuotaEvent(1, e.EventId, e.Snapshot)), cancellationToken);
            }
        }
        finally { writer.Release(); }
    }

    private async ValueTask RepairTailAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken); var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
        var complete = bytes.AsSpan(0, lastNewline + 1).ToArray();
        foreach (var line in Encoding.UTF8.GetString(complete).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            try { JsonSerializer.Deserialize(line, QuotaJsonContext.Default.PersistedQuotaEvent); } catch (JsonException ex) { throw new InvalidDataException("Corrupt JSONL before tail.", ex); }
        if (lastNewline + 1 < bytes.Length)
        {
            var tail = Encoding.UTF8.GetString(bytes, lastNewline + 1, bytes.Length - lastNewline - 1);
            try { JsonSerializer.Deserialize(tail, QuotaJsonContext.Default.PersistedQuotaEvent); await File.AppendAllTextAsync(path, "\n", cancellationToken); }
            catch (JsonException ex)
            {
                if (!LooksIncomplete(tail)) throw new InvalidDataException("Corrupt JSONL final event.", ex);
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read, 4096, true); stream.SetLength(lastNewline + 1);
            }
        }
    }

    private static bool LooksIncomplete(string text)
    {
        var trimmed = text.TrimEnd(); if (trimmed.Length == 0) return true;
        var depth = 0; var quoted = false; var escaped = false;
        foreach (var c in trimmed)
        {
            if (escaped) { escaped = false; continue; }
            if (c == '\\' && quoted) { escaped = true; continue; }
            if (c == '"') { quoted = !quoted; continue; }
            if (!quoted && c == '{') depth++;
            if (!quoted && c == '}') depth--;
        }
        return depth > 0 || quoted;
    }

    private static async ValueTask RewriteAsync(string path, IEnumerable<PersistedQuotaEvent> events, CancellationToken cancellationToken)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true)) { foreach (var item in events) { await JsonSerializer.SerializeAsync(stream, item, QuotaJsonContext.Default.PersistedQuotaEvent, cancellationToken); await stream.WriteAsync("\n"u8.ToArray(), cancellationToken); } await stream.FlushAsync(cancellationToken); } File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async ValueTask PruneAsync(DateOnly before, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return;
        await writer.WaitAsync(cancellationToken);
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl"))
            {
                var dayText = Path.GetFileNameWithoutExtension(file);
                if (!DateOnly.TryParseExact(dayText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDay)) continue;
                IReadOnlyList<QuotaHistoryEntry> entries;
                try { entries = await ReadEventsCoreAsync(fileDay, cancellationToken); }
                catch (InvalidDataException) { continue; }
                var retained = entries.Where(item => DateOnly.FromDateTime(item.Snapshot.Observed.UtcDateTime) >= before).ToList();
                if (retained.Count == 0)
                {
                    File.Delete(file);
                }
                else if (retained.Count != entries.Count)
                {
                    await RewriteAsync(file, retained.Select(item => new PersistedQuotaEvent(1, item.EventId, item.Snapshot)), cancellationToken);
                }
            }
        }
        finally { writer.Release(); }
    }

    public async ValueTask DeleteAsync(DateOnly? day, CancellationToken cancellationToken)
    {
        await writer.WaitAsync(cancellationToken);
        try
        {
            if (day is { } selectedDay)
            {
                var path = PathFor(selectedDay);
                if (File.Exists(path)) File.Delete(path);
            }
            else if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.jsonl"))
                {
                    File.Delete(file);
                }
            }
        }
        finally
        {
            writer.Release();
        }
    }

    public async ValueTask ExportJsonAsync(Stream output, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        foreach (var snapshot in await ReadAllAsync(cancellationToken))
        {
            var safe = new ExportQuotaDto(snapshot.DisplayName, snapshot.Provider.ToString(), snapshot.Metric,
                snapshot.EffectivePercent, snapshot.Unit, snapshot.Observed, snapshot.Source.ToString(), snapshot.Confidence.ToString(),
                snapshot.CopilotUsage?.GrossQuantity, snapshot.CopilotUsage?.DiscountQuantity, snapshot.CopilotUsage?.NetQuantity,
                snapshot.Window.Start, snapshot.Window.End);
            await writer.WriteLineAsync(JsonSerializer.Serialize(safe, QuotaJsonContext.Default.ExportQuotaDto));
        }
    }

    public async ValueTask ExportCsvAsync(Stream output, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("displayName,provider,metric,percent,unit,observed,Source,Confidence,grossQuantity,discountQuantity,netQuantity,periodStart,periodEnd");
        foreach (var snapshot in await ReadAllAsync(cancellationToken))
        {
            var values = new[] { snapshot.DisplayName, snapshot.Provider.ToString(), snapshot.Metric,
                snapshot.EffectivePercent?.ToString(CultureInfo.InvariantCulture) ?? "", snapshot.Unit, snapshot.Observed.ToString("O", CultureInfo.InvariantCulture), snapshot.Source.ToString(), snapshot.Confidence.ToString(),
                snapshot.CopilotUsage?.GrossQuantity?.ToString(CultureInfo.InvariantCulture) ?? "",
                snapshot.CopilotUsage?.DiscountQuantity?.ToString(CultureInfo.InvariantCulture) ?? "",
                snapshot.CopilotUsage?.NetQuantity?.ToString(CultureInfo.InvariantCulture) ?? "",
                snapshot.Window.Start.ToString("O", CultureInfo.InvariantCulture), snapshot.Window.End.ToString("O", CultureInfo.InvariantCulture) };
            await writer.WriteLineAsync(string.Join(',', values.Select(EscapeCsv)));
        }
    }

    private async ValueTask<IReadOnlyList<QuotaSnapshot>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var all = new List<QuotaSnapshot>();
        if (!Directory.Exists(root)) return all;
        foreach (var file in Directory.EnumerateFiles(root, "*.jsonl").OrderBy(item => item, StringComparer.Ordinal))
        {
            if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                all.AddRange(await ReadAsync(day, cancellationToken));
            }
        }
        return all;
    }

    private static string EscapeCsv(string value) => value.Any(character => character is ',' or '"' or '\n' or '\r')
        ? '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'
        : value;

    internal sealed record ExportQuotaDto(
        string DisplayName,
        string Provider,
        string Metric,
        decimal? Percent,
        [property: JsonPropertyName("unit")] string Unit,
        [property: JsonPropertyName("observed")] DateTimeOffset Observed,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("confidence")] string Confidence,
        [property: JsonPropertyName("grossQuantity")] decimal? GrossQuantity,
        [property: JsonPropertyName("discountQuantity")] decimal? DiscountQuantity,
        [property: JsonPropertyName("netQuantity")] decimal? NetQuantity,
        [property: JsonPropertyName("periodStart")] DateTimeOffset PeriodStart,
        [property: JsonPropertyName("periodEnd")] DateTimeOffset PeriodEnd);
}

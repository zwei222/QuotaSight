using System.Text.Json;
using QuotaSight.Application;

namespace QuotaSight.Infrastructure;

public sealed class AppSettingsStore(string? directory = null)
{
    public string Path { get; } = System.IO.Path.Combine(directory ?? PlatformPaths.ConfigDirectory(), "settings.json");
    public AppSettingsDto Load()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        try { if (!File.Exists(Path)) return new(); return JsonSerializer.Deserialize(File.ReadAllText(Path), AppSettingsJsonContext.Default.AppSettingsDto) ?? new(); }
        catch (JsonException) { return new(); }
    }
    public async ValueTask<AppSettingsDto> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        try { if (!File.Exists(Path)) return new(); return JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path, cancellationToken), AppSettingsJsonContext.Default.AppSettingsDto) ?? new(); }
        catch (JsonException) { if (File.Exists(Path)) File.Copy(Path, Path + ".bak", true); return new(); }
    }
    public async ValueTask SaveAsync(AppSettingsDto settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        await File.WriteAllTextAsync(Path, JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettingsDto), cancellationToken);
    }
    public void Save(AppSettingsDto settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettingsDto));
    }
}

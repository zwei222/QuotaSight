using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaSight.Application;
using QuotaSight.Core;

namespace QuotaSight.Infrastructure;


public interface ISecretToolProcessRunner
{
    ValueTask<SecretToolResult> RunAsync(IReadOnlyList<string> arguments, string? stdin, CancellationToken cancellationToken);
}
public sealed record SecretToolResult(int ExitCode, string Stdout, string Stderr);
public sealed class SecretToolProcessRunner : ISecretToolProcessRunner
{
    public async ValueTask<SecretToolResult> RunAsync(IReadOnlyList<string> arguments, string? stdin, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("secret-tool") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start) ?? throw new Win32Exception();
            if (stdin is not null) { await process.StandardInput.WriteAsync(stdin); await process.StandardInput.FlushAsync(cancellationToken); process.StandardInput.Close(); }
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new(process.ExitCode, stdout, stderr);
        }
        catch (Win32Exception) { return new(-1, string.Empty, "secret-tool unavailable"); }
    }
}
public sealed class LinuxSecretToolCredentialStore(ISecretToolProcessRunner runner) : ICredentialStore
{
    public CredentialStoreAvailability Availability { get; private set; } = CredentialStoreAvailability.SecureStore;
    public async ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(["lookup", "service", "quotasight", "account", account], null, cancellationToken);
        if (result.ExitCode == -1) { Availability = CredentialStoreAvailability.Unavailable; return null; }
        if (result.ExitCode != 0) { Availability = result.Stderr.Contains("locked", StringComparison.OrdinalIgnoreCase) ? CredentialStoreAvailability.Locked : CredentialStoreAvailability.Unavailable; return null; }
        return result.Stdout.TrimEnd('\r', '\n');
    }
    public async ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(["store", "--label", "QuotaSight", "service", "quotasight", "account", account], secret, cancellationToken);
        if (result.ExitCode != 0) Availability = result.Stderr.Contains("locked", StringComparison.OrdinalIgnoreCase) ? CredentialStoreAvailability.Locked : CredentialStoreAvailability.Unavailable;
    }
    public async ValueTask RemoveAsync(string account, CancellationToken cancellationToken) => await runner.RunAsync(["clear", "service", "quotasight", "account", account], null, cancellationToken);
}

public static class PlatformPaths
{
    public static string ConfigDirectory(string appName = "QuotaSight") => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName)
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), appName);
    public static string DataDirectory(string appName = "QuotaSight") => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName, "data")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), appName);
}

public sealed record AppSettingsDto(string Theme = "System", string Language = "English", int RefreshMinutes = 10, decimal OverallThreshold = 80, bool NotificationsEnabled = true, string GithubOAuthClientId = "", Dictionary<string, decimal>? ProviderThresholds = null);
[JsonSerializable(typeof(AppSettingsDto))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;
public sealed class JsonSettingsStore
{
    private readonly string path;
    public JsonSettingsStore(string? directory = null) { var root = directory ?? PlatformPaths.ConfigDirectory(); Directory.CreateDirectory(root); path = System.IO.Path.Combine(root, "settings.json"); }
    public AppSettingsDto Load()
    {
        try { if (!File.Exists(path)) return new(); return JsonSerializer.Deserialize(File.ReadAllText(path), AppSettingsJsonContext.Default.AppSettingsDto) ?? new(); }
        catch (JsonException) { if (File.Exists(path)) File.Copy(path, path + ".bak", true); return new(); }
    }
    public void Save(AppSettingsDto settings) => File.WriteAllText(path, JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettingsDto));
    public string Path => path;
}

public sealed class ManualQuotaService(IQuotaHistory history) : IManualQuotaService
{
    public async ValueTask<QuotaSnapshot> SetAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken) { await history.AppendAsync([snapshot], cancellationToken); return snapshot; }
}

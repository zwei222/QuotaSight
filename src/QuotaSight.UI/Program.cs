using Avalonia;
using QuotaSight.Infrastructure;
namespace QuotaSight.UI;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--smoke-test", StringComparer.Ordinal))
        {
            Environment.ExitCode = SmokeTest.Run();
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

internal static class SmokeTest
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "quotasight-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new JsonSettingsStore(Path.Combine(root, "config"));
            config.Save(new AppSettingsDto(GithubOAuthClientId: "smoke-client"));
            if (config.Load().GithubOAuthClientId != "smoke-client") return 1;
            var app = CompositionRoot.CreateApplication(Path.Combine(root, "data"));
            if (app is null || !Directory.Exists(Path.Combine(root, "config"))) return 2;
            _ = CompositionRoot.CreateMainViewModel(Path.Combine(root, "view-model-data"));
            return 0;
        }
        catch { return 3; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

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

        if (args.Contains("--notification-probe", StringComparer.Ordinal))
        {
#if WINDOWS
            Microsoft.Windows.AppNotifications.AppNotificationManager? manager = null;
            Environment.ExitCode = NotificationApiProbe.Run(
                Microsoft.Windows.AppNotifications.AppNotificationManager.IsSupported,
                () => manager = Microsoft.Windows.AppNotifications.AppNotificationManager.Default,
                () => manager!.NotificationInvoked += ProbeNotificationInvoked,
                () => manager!.Register(),
                () => manager!.Unregister(),
                () => manager!.UnregisterAll(),
                () => manager!.NotificationInvoked -= ProbeNotificationInvoked,
                Console.Error.WriteLine);
#else
            Environment.ExitCode = NotificationApiProbe.Run(
                () => false,
                () => throw new PlatformNotSupportedException(),
                static () => { },
                static () => { },
                static () => { },
                static () => { },
                static () => { },
                Console.Error.WriteLine);
#endif
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
#if WINDOWS
            WindowsDesktopNotifications.UnregisterIfRegistered();
#endif
        }
    }

#if WINDOWS
    private static void ProbeNotificationInvoked(
        Microsoft.Windows.AppNotifications.AppNotificationManager sender,
        Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs args)
    {
    }
#endif

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
            using var appLifetime = app as IDisposable;
            if (app is null || !Directory.Exists(Path.Combine(root, "config"))) return 2;
            using var viewModel = CompositionRoot.CreateMainViewModel(Path.Combine(root, "view-model-data"));
            return 0;
        }
        catch { return 3; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

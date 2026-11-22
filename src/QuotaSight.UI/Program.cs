#if WINDOWS
using System.Runtime.InteropServices;
#endif
using Avalonia;
using QuotaSight.Infrastructure;
namespace QuotaSight.UI;

public static partial class Program
{
    private const int AttachParentProcess = -1;

    [STAThread]
    public static void Main(string[] args)
    {
        var smokeTest = args.Contains("--smoke-test", StringComparer.Ordinal);
        var notificationProbe = args.Contains("--notification-probe", StringComparer.Ordinal);

        // A Windows GUI subsystem binary owns no console, so the diagnostic entry points borrow the
        // launching terminal's console before they write to it. A normal launch never touches it.
        if (smokeTest || notificationProbe) AttachParentConsole();

        if (smokeTest)
        {
            Environment.ExitCode = SmokeTest.Run();
            return;
        }

        if (notificationProbe)
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

    /// <summary>
    /// Attaches the process to the console of the process that launched it, so the smoke test and
    /// the notification probe keep printing when the executable is built as a Windows GUI binary.
    /// Attaching is best effort: a process that already has a console, or one launched without a
    /// parent console, keeps running and only loses the diagnostic text.
    /// </summary>
    private static void AttachParentConsole()
    {
#if WINDOWS
        try
        {
            _ = AttachConsole(AttachParentProcess);
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (DllNotFoundException)
        {
        }
#endif
    }

#if WINDOWS
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int dwProcessId);

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
            if (!Directory.Exists(Path.Combine(root, "config"))) return 2;
            using var viewModel = CompositionRoot.CreateMainViewModel(Path.Combine(root, "view-model-data"));
            return 0;
        }
        catch { return 3; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

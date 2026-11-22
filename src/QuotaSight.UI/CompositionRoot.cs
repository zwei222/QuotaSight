using QuotaSight.Application;
using QuotaSight.Infrastructure;

namespace QuotaSight.UI;

public static class CompositionRoot
{
    private static ICredentialStore CreateCredentialStore()
    {
        return new FallbackCredentialStore(CreatePrimaryCredentialStore(), new InMemoryCredentialStore());
    }
    private static ICredentialStore CreatePrimaryCredentialStore()
    {
        ICredentialStore primary = OperatingSystem.IsWindows()
            ? WindowsCredentialStore.Create()
            : OperatingSystem.IsLinux()
                ? new LinuxSecretToolCredentialStore(new SecretToolProcessRunner())
                : new InMemoryCredentialStore();
        return primary;
    }

    internal static IDesktopNotificationBackend? CreateDesktopNotificationBackend()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows()) return new WindowsDesktopNotifications();
#endif
        return OperatingSystem.IsLinux() ? new LinuxDesktopNotifications() : null;
    }

    public static MainViewModel CreateMainViewModel(string? dataDirectory = null, HttpClient? httpClient = null, IDesktopNotificationBackend? desktopNotificationBackend = null)
    {
        var client = httpClient ?? new HttpClient();
        var settings = new AppSettingsStore(dataDirectory);
        var credentials = CreateCredentialStore();
        return new MainViewModel(settingsStore: settings, desktopNotificationBackend: desktopNotificationBackend ?? CreateDesktopNotificationBackend(), usageConnectionPort: new ReconfigurableUsageReadPort(), usageCredentialStore: credentials);
    }
}

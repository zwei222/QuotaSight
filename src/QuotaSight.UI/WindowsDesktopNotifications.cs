#if WINDOWS
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
#endif

namespace QuotaSight.UI;

internal static class NotificationApiProbe
{
    public const int SuccessExitCode = 0;
    public const int SupportCheckFailureExitCode = 1;
    public const int UnsupportedExitCode = 2;
    public const int DefaultManagerFailureExitCode = 3;
    public const int SubscribeFailureExitCode = 4;
    public const int RegisterFailureExitCode = 5;
    public const int UnregisterFailureExitCode = 6;
    public const int UnregisterAllFailureExitCode = 7;
    public const int UnsubscribeFailureExitCode = 8;

    public static int Run(
        Func<bool> isSupported,
        Func<object> getDefault,
        Action subscribe,
        Action register,
        Action unregister,
        Action unregisterAll,
        Action unsubscribe,
        Action<string> report)
    {
        bool supported;
        try { supported = isSupported(); }
        catch { report("Notification API support check failed."); return SupportCheckFailureExitCode; }
        if (!supported)
        {
            report("Notification API is unsupported.");
            return UnsupportedExitCode;
        }

        object manager;
        try { manager = getDefault(); }
        catch { report("Notification API manager acquisition failed."); return DefaultManagerFailureExitCode; }

        var result = SuccessExitCode;
        var subscriptionAttempted = false;
        var registrationAttempted = false;
        try
        {
            subscriptionAttempted = true;
            try { subscribe(); }
            catch
            {
                report("Notification API event subscription failed.");
                result = SubscribeFailureExitCode;
            }

            if (result == SuccessExitCode)
            {
                registrationAttempted = true;
                try { register(); }
                catch
                {
                    report("Notification API registration failed.");
                    result = RegisterFailureExitCode;
                }
            }
        }
        finally
        {
            if (registrationAttempted)
            {
                try { unregister(); }
                catch
                {
                    report("Notification API unregistration failed.");
                    if (result == SuccessExitCode) result = UnregisterFailureExitCode;
                }

                try { unregisterAll(); }
                catch
                {
                    report("Notification API unregister-all failed.");
                    if (result == SuccessExitCode) result = UnregisterAllFailureExitCode;
                }
            }

            if (subscriptionAttempted)
            {
                try { unsubscribe(); }
                catch
                {
                    report("Notification API event unsubscription failed.");
                    if (result == SuccessExitCode) result = UnsubscribeFailureExitCode;
                }
            }
        }
        return result;
    }
}

internal sealed class NotificationRegistrationLifecycle(
    Action subscribe,
    Action register,
    Action unregister,
    Action unregisterAll,
    Action unsubscribe)
{
    private readonly object gate = new();
    private bool isRegistered;
    private bool isSubscribed;

    public bool IsRegistered
    {
        get { lock (gate) return isRegistered; }
    }

    public void Register()
    {
        lock (gate)
        {
            if (isRegistered) return;
            if (!isSubscribed)
            {
                subscribe();
                isSubscribed = true;
            }

            try
            {
                register();
                isRegistered = true;
            }
            catch
            {
                TryUnsubscribe();
                throw;
            }
        }
    }

    public void UnregisterIfRegistered()
    {
        lock (gate)
        {
            try
            {
                if (isRegistered)
                {
                    try { unregister(); }
                    catch { }
                    try { unregisterAll(); }
                    catch { }
                }
            }
            finally
            {
                TryUnsubscribe();
                isRegistered = false;
            }
        }
    }

    private void TryUnsubscribe()
    {
        if (!isSubscribed) return;
        try { unsubscribe(); }
        catch { }
        finally { isSubscribed = false; }
    }
}

#if WINDOWS
public sealed class WindowsDesktopNotifications : IDesktopNotificationBackend
{
    private static AppNotificationManager? registeredManager;
    private static readonly NotificationRegistrationLifecycle Registration = new(
        () => registeredManager!.NotificationInvoked += OnNotificationInvoked,
        () => registeredManager!.Register(),
        () => registeredManager!.Unregister(),
        () => registeredManager!.UnregisterAll(),
        () => registeredManager!.NotificationInvoked -= OnNotificationInvoked);

    public static string SanitizeText(string value, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumLength);
        return new string(value.Where(static character => !char.IsControl(character)).Take(maximumLength).ToArray()).Trim();
    }

    public Task<bool> TryNotifyAsync(string title, string reason, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (!AppNotificationManager.IsSupported()) return Task.FromResult(false);

            var manager = AppNotificationManager.Default;
            EnsureRegistered(manager);
            token.ThrowIfCancellationRequested();

            var notification = new AppNotificationBuilder()
                .AddText(SanitizeText(title, 256))
                .AddText(SanitizeText(reason, 1024))
                .BuildNotification();
            token.ThrowIfCancellationRequested();
            manager.Show(notification);
            return Task.FromResult(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public static void UnregisterIfRegistered()
    {
        try { Registration.UnregisterIfRegistered(); }
        catch { }
    }

    private static void EnsureRegistered(AppNotificationManager manager)
    {
        if (Registration.IsRegistered) return;
        registeredManager = manager;
        Registration.Register();
    }

    private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        // Invocation behavior is intentionally unchanged; subscribing before Register is required by the API.
    }
}
#endif

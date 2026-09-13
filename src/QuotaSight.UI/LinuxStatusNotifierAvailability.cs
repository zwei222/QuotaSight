using Avalonia.Controls;
using Tmds.DBus.Protocol;

namespace QuotaSight.UI;

public static class LinuxStatusNotifierAvailability
{
    public const string WatcherName = "org.kde.StatusNotifierWatcher";
    public const string WatcherPath = "/StatusNotifierWatcher";
    public const string WatcherInterface = "org.kde.StatusNotifierWatcher";

    public static MessageBuffer BuildNameHasOwnerMessage(MessageWriter writer)
    {
        writer.WriteMethodCallHeader(Connection.DBusServiceName, Connection.DBusObjectPath, Connection.DBusInterface, "NameHasOwner", "s");
        writer.WriteString(WatcherName);
        return writer.CreateMessage();
    }

    public static MessageBuffer BuildHostRegisteredMessage(MessageWriter writer)
    {
        writer.WriteMethodCallHeader(WatcherName, WatcherPath, "org.freedesktop.DBus.Properties", "Get", "ss");
        writer.WriteString(WatcherInterface);
        writer.WriteString("IsStatusNotifierHostRegistered");
        return writer.CreateMessage();
    }

    public static bool ParseNameHasOwnerBody(Message message) => message.GetBodyReader().ReadBool();
    public static bool ParseHostRegisteredBody(Message message) => message.GetBodyReader().ReadVariantValue().GetBool();
    public static bool ResolveDetection(bool ownerPresent, bool hostRegistered) => ownerPresent && hostRegistered;
    public static bool ResolveOverride(bool? testOverride, bool detected) => testOverride ?? detected;

    public static async Task<bool> DetectAsync(TrayIcon tray, CancellationToken cancellationToken = default)
    {
        if (tray.NativeMenuExporter is null) return false;
        if (OperatingSystem.IsWindows()) return true;
        if (!OperatingSystem.IsLinux()) return false;
        return await IsAvailableAsync(tray, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> IsAvailableAsync(TrayIcon tray, CancellationToken cancellationToken = default)
    {
        if (tray.NativeMenuExporter is null || Address.Session is null) return false;

        using var connection = new Connection(Address.Session);
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            await connection.ConnectAsync().AsTask().WaitAsync(Remaining(deadline), cancellationToken).ConfigureAwait(false);
            var writer = connection.GetMessageWriter();
            var ownerMessage = BuildNameHasOwnerMessage(writer);
            var owner = await connection.CallMethodAsync(ownerMessage, static (Message m, object? _) => ParseNameHasOwnerBody(m))
                .WaitAsync(Remaining(deadline), cancellationToken).ConfigureAwait(false);
            if (!owner) return false;

            var hostMessage = BuildHostRegisteredMessage(connection.GetMessageWriter());
            var host = await connection.CallMethodAsync(hostMessage, static (Message m, object? _) => ParseHostRegisteredBody(m))
                .WaitAsync(Remaining(deadline), cancellationToken).ConfigureAwait(false);
            return ResolveDetection(owner, host);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static TimeSpan Remaining(DateTime deadline) =>
        deadline - DateTime.UtcNow is { } remaining && remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
}

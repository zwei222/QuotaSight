using System.Diagnostics;
using Tmds.DBus.Protocol;

namespace QuotaSight.UI;

public interface IDesktopNotificationBackend
{
    Task<bool> TryNotifyAsync(string title, string reason, CancellationToken token);
}

public sealed class LinuxDesktopNotifications : IDesktopNotificationBackend
{
    public const string NotifySignature = "susssasa{sv}i";
    public const string NotifyDestination = "org.freedesktop.Notifications";
    public const string NotifyObjectPath = "/org/freedesktop/Notifications";
    public const string NotifyInterface = "org.freedesktop.Notifications";
    public const string NotifyMember = "Notify";
    private const string Destination = NotifyDestination;
    private const string ObjectPath = NotifyObjectPath;
    private const string Interface = NotifyInterface;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public static NotifyPayload BuildNotifyPayload(string title, string reason) =>
        new("QuotaSight", 0, string.Empty, Sanitize(title, 256), Sanitize(reason, 1024), [], 5000);

    public static MessageBuffer BuildNotifyMessage(MessageWriter writer, string title, string reason)
    {
        var payload = BuildNotifyPayload(title, reason);
        writer.WriteMethodCallHeader(Destination, ObjectPath, Interface, NotifyMember, NotifySignature);
        writer.WriteString(payload.ApplicationName);
        writer.WriteUInt32(payload.ReplacesId);
        writer.WriteString(payload.Icon);
        writer.WriteString(payload.Summary);
        writer.WriteString(payload.Body);
        writer.WriteArray(payload.Actions);
        var hints = writer.WriteArrayStart(DBusType.DictEntry);
        writer.WriteArrayEnd(hints);
        writer.WriteInt32(payload.ExpireTimeoutMilliseconds);
        return writer.CreateMessage();
    }

    public static bool ParseNotificationId(uint notificationId) => notificationId != 0;

    public static bool ResolveNotificationResponse(string? replySignature, uint? notificationId, Exception? error) =>
        error is null && replySignature == "u" && notificationId is { } id && ParseNotificationId(id);

    public async Task<bool> TryNotifyAsync(string title, string reason, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux()) return false;

        var timeout = Stopwatch.StartNew();
        try
        {
            var address = Address.Session;
            if (address is null) return false;

            using var connection = new Connection(address);
            await connection.ConnectAsync().AsTask().WaitAsync(Remaining(timeout), token).ConfigureAwait(false);
            var request = BuildNotifyMessage(connection.GetMessageWriter(), title, reason);
            var response = await connection.CallMethodAsync(request,
                    static (Message message, object? _) =>
                    {
                        var signature = message.SignatureAsString;
                        uint? notificationId = signature == "u" ? message.GetBodyReader().ReadUInt32() : null;
                        return (signature, notificationId);
                    })
                .WaitAsync(Remaining(timeout), token).ConfigureAwait(false);
            return ResolveNotificationResponse(response.signature, response.notificationId, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ResolveNotificationResponse(null, null, exception);
        }
    }

    private static string Sanitize(string value, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sanitized = new string(value.Where(static character => !char.IsControl(character)).Take(maximumLength).ToArray());
        return sanitized.Trim();
    }

    private static TimeSpan Remaining(Stopwatch stopwatch) =>
        Timeout - stopwatch.Elapsed is { } remaining && remaining > TimeSpan.Zero
            ? remaining
            : TimeSpan.FromMilliseconds(1);
}

public sealed record NotifyPayload(
    string ApplicationName,
    uint ReplacesId,
    string Icon,
    string Summary,
    string Body,
    string[] Actions,
    int ExpireTimeoutMilliseconds);

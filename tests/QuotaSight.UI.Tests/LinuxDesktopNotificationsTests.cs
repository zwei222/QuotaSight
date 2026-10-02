using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class LinuxDesktopNotificationsTests
{
    [Fact]
    public void NotifySignatureAndHeader_MatchFreedesktopNotificationsContract()
    {
        Assert.Equal("org.freedesktop.Notifications", LinuxDesktopNotifications.NotifyDestination);
        Assert.Equal("/org/freedesktop/Notifications", LinuxDesktopNotifications.NotifyObjectPath);
        Assert.Equal("org.freedesktop.Notifications", LinuxDesktopNotifications.NotifyInterface);
        Assert.Equal("Notify", LinuxDesktopNotifications.NotifyMember);
        Assert.Equal("susssasa{sv}i", LinuxDesktopNotifications.NotifySignature);
    }

    [Fact]
    public void BuildNotifyPayload_UsesOnlySanitizedTitleAndReasonWithMinimalFields()
    {
        var payload = LinuxDesktopNotifications.BuildNotifyPayload(string.Concat("Title", '\n'), string.Concat("Reason", '\t', "value"));

        Assert.Equal("QuotaSight", payload.ApplicationName);
        Assert.Equal((uint)0, payload.ReplacesId);
        Assert.Equal(string.Empty, payload.Icon);
        Assert.Equal("Title", payload.Summary);
        Assert.Equal("Reasonvalue", payload.Body);
        Assert.Empty(payload.Actions);
        Assert.Equal(5000, payload.ExpireTimeoutMilliseconds);
    }

    [Fact]
    public void ParseNotificationId_AcceptsOnlyNonzeroId()
    {
        Assert.True(LinuxDesktopNotifications.ParseNotificationId(42));
        Assert.False(LinuxDesktopNotifications.ParseNotificationId(0));
        Assert.True(LinuxDesktopNotifications.ResolveNotificationResponse("u", 42, null));
        Assert.False(LinuxDesktopNotifications.ResolveNotificationResponse("u", 0, null));
        Assert.False(LinuxDesktopNotifications.ResolveNotificationResponse("b", 1, null));
        Assert.False(LinuxDesktopNotifications.ResolveNotificationResponse(null, 42, new InvalidOperationException()));
        Assert.False(LinuxDesktopNotifications.ResolveNotificationResponse(null, null, null));
    }

    [Fact]
    public async Task TryNotifyAsync_PropagatesCallerCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LinuxDesktopNotifications().TryNotifyAsync("Title", "Reason", source.Token));
    }
}

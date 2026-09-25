using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class WindowsDesktopNotificationsTests
{
    [Fact]
    public void Notification_probe_runs_registration_lifecycle_without_showing_and_returns_success()
    {
        var calls = new List<string>();
        var messages = new List<string>();

        var exitCode = NotificationApiProbe.Run(
            () => { calls.Add("supported"); return true; },
            () => { calls.Add("default"); return new object(); },
            () => calls.Add("subscribe"),
            () => calls.Add("register"),
            () => calls.Add("unregister"),
            () => calls.Add("unregister-all"),
            () => calls.Add("unsubscribe"),
            messages.Add);

        Assert.Equal(0, exitCode);
        Assert.Equal(["supported", "default", "subscribe", "register", "unregister", "unregister-all", "unsubscribe"], calls);
        Assert.Empty(messages);
    }

    [Fact]
    public void Notification_probe_returns_unsupported_code_without_getting_default()
    {
        var calls = new List<string>();
        var messages = new List<string>();

        var exitCode = NotificationApiProbe.Run(
            () => { calls.Add("supported"); return false; },
            () => { calls.Add("default"); return new object(); },
            () => calls.Add("subscribe"),
            () => calls.Add("register"),
            () => calls.Add("unregister"),
            () => calls.Add("unregister-all"),
            () => calls.Add("unsubscribe"),
            messages.Add);

        Assert.Equal(NotificationApiProbe.UnsupportedExitCode, exitCode);
        Assert.Equal(["supported"], calls);
        Assert.Equal(["Notification API is unsupported."], messages);
    }

    [Theory]
    [InlineData("supported", NotificationApiProbe.SupportCheckFailureExitCode, "Notification API support check failed.", new[] { "supported" })]
    [InlineData("default", NotificationApiProbe.DefaultManagerFailureExitCode, "Notification API manager acquisition failed.", new[] { "supported", "default" })]
    [InlineData("subscribe", NotificationApiProbe.SubscribeFailureExitCode, "Notification API event subscription failed.", new[] { "supported", "default", "subscribe", "unsubscribe" })]
    [InlineData("register", NotificationApiProbe.RegisterFailureExitCode, "Notification API registration failed.", new[] { "supported", "default", "subscribe", "register", "unregister", "unregister-all", "unsubscribe" })]
    [InlineData("unregister", NotificationApiProbe.UnregisterFailureExitCode, "Notification API unregistration failed.", new[] { "supported", "default", "subscribe", "register", "unregister", "unregister-all", "unsubscribe" })]
    [InlineData("unregister-all", NotificationApiProbe.UnregisterAllFailureExitCode, "Notification API unregister-all failed.", new[] { "supported", "default", "subscribe", "register", "unregister", "unregister-all", "unsubscribe" })]
    [InlineData("unsubscribe", NotificationApiProbe.UnsubscribeFailureExitCode, "Notification API event unsubscription failed.", new[] { "supported", "default", "subscribe", "register", "unregister", "unregister-all", "unsubscribe" })]
    public void Notification_probe_reports_each_failure_safely_and_completes_available_cleanup(
        string failingStage,
        int expectedExitCode,
        string expectedMessage,
        string[] expectedCalls)
    {
        var calls = new List<string>();
        var messages = new List<string>();
        void Stage(string name)
        {
            calls.Add(name);
            if (name == failingStage) throw new InvalidOperationException("secret token and path must not appear");
        }

        var exitCode = NotificationApiProbe.Run(
            () => { Stage("supported"); return true; },
            () => { Stage("default"); return new object(); },
            () => Stage("subscribe"),
            () => Stage("register"),
            () => Stage("unregister"),
            () => Stage("unregister-all"),
            () => Stage("unsubscribe"),
            messages.Add);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Equal(expectedCalls, calls);
        Assert.Equal([expectedMessage], messages);
        Assert.DoesNotContain("secret", string.Join(" ", messages), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registration_lifecycle_subscribes_before_registering_and_cleans_up_once()
    {
        var calls = new List<string>();
        var lifecycle = new NotificationRegistrationLifecycle(
            () => calls.Add("subscribe"),
            () => calls.Add("register"),
            () => calls.Add("unregister"),
            () => calls.Add("unregister-all"),
            () => calls.Add("unsubscribe"));

        lifecycle.Register();
        lifecycle.Register();
        lifecycle.UnregisterIfRegistered();
        lifecycle.UnregisterIfRegistered();

        Assert.Equal(["subscribe", "register", "unregister", "unregister-all", "unsubscribe"], calls);
        Assert.False(lifecycle.IsRegistered);
    }

    [Fact]
    public void Registration_lifecycle_cleanup_is_safe_when_never_registered_or_callbacks_fail()
    {
        var untouchedCalls = new List<string>();
        var untouched = new NotificationRegistrationLifecycle(
            () => untouchedCalls.Add("subscribe"),
            () => untouchedCalls.Add("register"),
            () => untouchedCalls.Add("unregister"),
            () => untouchedCalls.Add("unregister-all"),
            () => untouchedCalls.Add("unsubscribe"));
        untouched.UnregisterIfRegistered();
        Assert.Empty(untouchedCalls);

        var calls = new List<string>();
        var failingCleanup = new NotificationRegistrationLifecycle(
            () => calls.Add("subscribe"),
            () => calls.Add("register"),
            () => { calls.Add("unregister"); throw new InvalidOperationException(); },
            () => calls.Add("unregister-all"),
            () => { calls.Add("unsubscribe"); throw new InvalidOperationException(); });
        failingCleanup.Register();
        failingCleanup.UnregisterIfRegistered();

        Assert.Equal(["subscribe", "register", "unregister", "unregister-all", "unsubscribe"], calls);
        Assert.False(failingCleanup.IsRegistered);
    }

    [Fact]
    public void Composition_keeps_linux_backend_and_returns_no_backend_on_other_platforms()
    {
        var backend = CompositionRoot.CreateDesktopNotificationBackend();
        if (OperatingSystem.IsLinux())
            Assert.IsType<LinuxDesktopNotifications>(backend);
#if WINDOWS
        else if (OperatingSystem.IsWindows())
            Assert.IsType<WindowsDesktopNotifications>(backend);
#endif
        else if (!OperatingSystem.IsWindows())
            Assert.Null(backend);
    }

#if WINDOWS
    [Fact]
    public void Sanitization_removes_control_characters_trims_and_preserves_markup_as_plain_text()
    {
        Assert.Equal("<quota>& use", WindowsDesktopNotifications.SanitizeText("  <quota>\u0001& use\r\n  ", 256));
    }

    [Fact]
    public void Sanitization_limits_text_without_splitting_other_characters()
    {
        Assert.Equal("abc", WindowsDesktopNotifications.SanitizeText("abcdef", 3));
    }
#endif
}

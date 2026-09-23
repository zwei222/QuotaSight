using QuotaSight.Application;
using QuotaSight.Core;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class ThresholdNotificationPresentationTests
{
    [Fact]
    public async Task Scheduled_threshold_notification_remains_visible_and_deduplicates_per_window()
    {
        var snapshot = Snapshot(85);
        var app = new MutableApplication(new QuotaRefreshResult([snapshot], []));
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: app, timeProvider: new TestTimeProvider(snapshot.Fetched), uiDispatcher: new ImmediateUiDispatcher());
        var bannerChanges = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.NotificationBannerText)) bannerChanges++; };

        await vm.RefreshAsync(RefreshOrigin.Scheduled);
        var banner = vm.NotificationBannerText;
        Assert.True(vm.IsNotificationVisible);
        Assert.NotEmpty(banner);

        await vm.RefreshAsync(RefreshOrigin.Scheduled);
        Assert.Equal(banner, vm.NotificationBannerText);
        Assert.Equal(1, bannerChanges);

        app.Result = new QuotaRefreshResult([snapshot with { Used = 90, Window = new(QuotaWindowKind.Weekly, snapshot.Window.Start.AddDays(7), snapshot.Window.End.AddDays(7)) }], []);
        await vm.RefreshAsync(RefreshOrigin.Scheduled);
        Assert.Equal(2, bannerChanges);
    }

    [Fact]
    public async Task Manual_snapshot_threshold_is_presented()
    {
        var backend = new FakeDesktopNotificationBackend();
        using var vm = new MainViewModel(new EmptyDashboardSource(), timeProvider: new TestTimeProvider(Snapshot(85).Fetched), uiDispatcher: new ImmediateUiDispatcher(), desktopNotificationBackend: backend);
        var snapshot = Snapshot(85) with { Source = QuotaSource.Manual, Confidence = QuotaConfidence.Manual };
        await vm.ApplyManualSnapshotAsync(snapshot);
        await vm.ApplyManualSnapshotAsync(snapshot);
        Assert.True(vm.IsNotificationVisible);
        Assert.NotEmpty(vm.NotificationBannerText);
        Assert.Single(backend.Requests);
    }

    [Fact]
    public async Task Partial_failure_keeps_error_precedence_then_restores_delivered_threshold()
    {
        var snapshot = Snapshot(85);
        var app = new MutableApplication(new QuotaRefreshResult([snapshot], [new(ProviderKind.Claude, FetchStatus.TransientFailure)]));
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: app, timeProvider: new TestTimeProvider(snapshot.Fetched), uiDispatcher: new ImmediateUiDispatcher());

        await vm.RefreshAsync();
        Assert.Contains("incomplete", vm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);
        var failureBanner = vm.NotificationBannerText;

        app.Result = new QuotaRefreshResult([snapshot], []);
        await vm.RefreshAsync();
        Assert.NotEqual(failureBanner, vm.NotificationBannerText);
        Assert.NotEmpty(vm.NotificationBannerText);
    }

    [Fact]
    public async Task Disabled_threshold_notifications_do_not_suppress_refresh_errors()
    {
        var snapshot = Snapshot(85);
        var app = new MutableApplication(new QuotaRefreshResult([snapshot], [new(ProviderKind.Claude, FetchStatus.TransientFailure)]));
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: app, timeProvider: new TestTimeProvider(snapshot.Fetched), uiDispatcher: new ImmediateUiDispatcher());
        vm.Settings.NotificationsEnabled = false;

        await vm.RefreshAsync();

        Assert.True(vm.IsNotificationVisible);
        Assert.Contains("incomplete", vm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);

        app.Result = new QuotaRefreshResult([snapshot], []);
        await vm.RefreshAsync();
        Assert.False(vm.IsNotificationVisible);
    }

    [Fact]
    public async Task Threshold_crossing_is_sent_to_backend_once_and_payload_is_localized_and_private()
    {
        var snapshot = Snapshot(85) with { Account = "private-account", DisplayName = "private-name" };
        var backend = new FakeDesktopNotificationBackend();
        var app = new MutableApplication(new QuotaRefreshResult([snapshot], []));
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: app, timeProvider: new TestTimeProvider(snapshot.Fetched), uiDispatcher: new ImmediateUiDispatcher(), desktopNotificationBackend: backend);
        vm.Language = UiLanguage.Japanese;

        await vm.RefreshAsync(RefreshOrigin.Scheduled);
        await vm.RefreshAsync(RefreshOrigin.Scheduled);

        var notification = Assert.Single(backend.Requests);
        Assert.Contains("ChatGPT", notification.Title);
        Assert.Contains("85", notification.Reason);
        Assert.DoesNotContain("private-account", notification.Title + notification.Reason);
        Assert.DoesNotContain("private-name", notification.Title + notification.Reason);
        Assert.True(vm.IsNotificationVisible);
    }

    [Fact]
    public async Task Backend_failure_keeps_in_app_fallback_and_caller_cancellation_is_not_suppressed()
    {
        var snapshot = Snapshot(85);
        var clock = new TestTimeProvider(snapshot.Fetched);
        var backend = new FakeDesktopNotificationBackend { Throw = new InvalidOperationException("unavailable") };
        var app = new MutableApplication(new QuotaRefreshResult([snapshot], []));
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: app, timeProvider: clock, uiDispatcher: new ImmediateUiDispatcher(), desktopNotificationBackend: backend);

        await vm.RefreshAsync(RefreshOrigin.Scheduled);
        Assert.True(vm.IsNotificationVisible);
        Assert.Single(backend.Requests);
        clock.Advance(TimeSpan.FromMinutes(5));
        backend.Throw = null;
        await vm.RefreshAsync(RefreshOrigin.Scheduled);
        Assert.Equal(2, backend.Requests.Count);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var canceledVm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: app, timeProvider: new TestTimeProvider(snapshot.Fetched), uiDispatcher: new ImmediateUiDispatcher(), desktopNotificationBackend: new FakeDesktopNotificationBackend());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledVm.RefreshAsync(RefreshOrigin.Scheduled, cancellation.Token));
    }

    [Fact]
    public async Task Backend_cancellation_after_merge_keeps_refreshed_cards_current()
    {
        var snapshot = Snapshot(85);
        using var cancellation = new CancellationTokenSource();
        var backend = new FakeDesktopNotificationBackend { CancelOnNotify = cancellation };
        var app = new MutableApplication(new QuotaRefreshResult([snapshot], []));
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: app, timeProvider: new TestTimeProvider(snapshot.Fetched), uiDispatcher: new ImmediateUiDispatcher(), desktopNotificationBackend: backend);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.RefreshAsync(RefreshOrigin.Scheduled, cancellation.Token));

        var card = Assert.Single(vm.Cards);
        Assert.Contains(card.Windows, window => window.Snapshot == snapshot);
    }

    [Fact]
    public async Task Failed_backend_is_retried_after_five_minutes_using_time_provider_while_banner_remains_visible()
    {
        var snapshot = Snapshot(85);
        var clock = new TestTimeProvider(snapshot.Fetched);
        var backend = new FakeDesktopNotificationBackend { Result = false };
        var app = new MutableApplication(new QuotaRefreshResult([snapshot], []));
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: app, timeProvider: clock, uiDispatcher: new ImmediateUiDispatcher(), desktopNotificationBackend: backend);

        await vm.RefreshAsync(RefreshOrigin.Scheduled);
        Assert.True(vm.IsNotificationVisible);
        Assert.Single(backend.Requests);
        await vm.RefreshAsync(RefreshOrigin.Scheduled);
        Assert.Single(backend.Requests);

        clock.Advance(TimeSpan.FromMinutes(4));
        await vm.RefreshAsync(RefreshOrigin.Scheduled);
        Assert.Single(backend.Requests);
        clock.Advance(TimeSpan.FromMinutes(1));
        backend.Result = true;
        await vm.RefreshAsync(RefreshOrigin.Scheduled);
        Assert.Equal(2, backend.Requests.Count);

        clock.Advance(TimeSpan.FromMinutes(5));
        await vm.RefreshAsync(RefreshOrigin.Scheduled);
        Assert.Equal(2, backend.Requests.Count);
        Assert.True(vm.IsNotificationVisible);
    }

    [Fact]
    public async Task Disabled_notifications_do_not_call_backend_and_clear_threshold_banner()
    {
        var snapshot = Snapshot(85);
        var backend = new FakeDesktopNotificationBackend();
        var app = new MutableApplication(new QuotaRefreshResult([snapshot], []));
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: app, timeProvider: new TestTimeProvider(snapshot.Fetched), uiDispatcher: new ImmediateUiDispatcher(), desktopNotificationBackend: backend);
        await vm.RefreshAsync();
        Assert.Single(backend.Requests);

        await vm.SetNotificationsAsync(false);
        app.Result = new QuotaRefreshResult([snapshot], []);
        await vm.RefreshAsync();

        Assert.Single(backend.Requests);
        Assert.False(vm.IsNotificationVisible);
    }

    [Fact]
    public async Task Current_below_threshold_window_does_not_restore_stale_threshold_banner()
    {
        var snapshot = Snapshot(85);
        var app = new MutableApplication(new QuotaRefreshResult([snapshot], []));
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: app, timeProvider: new TestTimeProvider(snapshot.Fetched), uiDispatcher: new ImmediateUiDispatcher());
        await vm.RefreshAsync();
        Assert.True(vm.IsNotificationVisible);

        app.Result = new QuotaRefreshResult([snapshot with { Used = 70 }], []);
        await vm.RefreshAsync();

        Assert.False(vm.IsNotificationVisible);
    }

    [Fact]
    public async Task Expired_threshold_banner_is_not_restored()
    {
        var snapshot = Snapshot(85);
        var clock = new TestTimeProvider(snapshot.Fetched);
        var app = new MutableApplication(new QuotaRefreshResult([snapshot], []));
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: app, timeProvider: clock, uiDispatcher: new ImmediateUiDispatcher());
        await vm.RefreshAsync();
        Assert.True(vm.IsNotificationVisible);

        clock.Advance(snapshot.Window.End - clock.GetUtcNow());
        app.Result = new QuotaRefreshResult([snapshot], []);
        await vm.RefreshAsync();

        Assert.False(vm.IsNotificationVisible);
    }

    [Fact]
    public async Task Stale_threshold_banner_is_not_restored_when_clean_refresh_has_no_snapshots()
    {
        var fetched = Snapshot(85);
        var initial = fetched with { FreshUntil = fetched.Fetched.AddMinutes(10), Source = QuotaSource.Manual, Confidence = QuotaConfidence.Manual };
        var clock = new TestTimeProvider(initial.Fetched);
        var app = new MutableApplication(new QuotaRefreshResult([initial], []));
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: app, timeProvider: clock, uiDispatcher: new ImmediateUiDispatcher());
        await vm.RefreshAsync(RefreshOrigin.Scheduled);
        Assert.True(vm.IsNotificationVisible);

        clock.Advance(TimeSpan.FromMinutes(11));
        app.Result = new QuotaRefreshResult([], []);
        await vm.RefreshAsync(RefreshOrigin.Scheduled);

        Assert.False(vm.IsNotificationVisible);
    }

    [Fact]
    public async Task Fresh_rolling_replacement_with_same_reset_retains_banner_after_old_snapshot_ttl()
    {
        var initial = RollingSnapshot(85, DateTimeOffset.Parse("2026-09-23T12:00:00Z"));
        var clock = new TestTimeProvider(initial.Fetched);
        var backend = new FakeDesktopNotificationBackend();
        var app = new MutableApplication(new QuotaRefreshResult([initial], []));
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: app, timeProvider: clock, uiDispatcher: new ImmediateUiDispatcher(), desktopNotificationBackend: backend);
        await vm.RefreshAsync(RefreshOrigin.Scheduled);
        var banner = vm.NotificationBannerText;

        clock.Advance(TimeSpan.FromMinutes(15));
        app.Result = new QuotaRefreshResult([RollingSnapshot(85, clock.GetUtcNow(), initial.Window.ResetAt)], []);
        await vm.RefreshAsync(RefreshOrigin.Scheduled);

        Assert.Equal(banner, vm.NotificationBannerText);
        Assert.True(vm.IsNotificationVisible);
        Assert.Single(backend.Requests);
    }

    [Fact]
    public async Task Shifted_start_replacement_below_threshold_clears_banner()
    {
        var initial = RollingSnapshot(85, DateTimeOffset.Parse("2026-09-23T12:00:00Z"));
        var clock = new TestTimeProvider(initial.Fetched);
        var app = new MutableApplication(new QuotaRefreshResult([initial], []));
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: app, timeProvider: clock, uiDispatcher: new ImmediateUiDispatcher());
        await vm.RefreshAsync();
        Assert.True(vm.IsNotificationVisible);

        clock.Advance(TimeSpan.FromMinutes(1));
        app.Result = new QuotaRefreshResult([RollingSnapshot(70, clock.GetUtcNow())], []);
        await vm.RefreshAsync();

        Assert.False(vm.IsNotificationVisible);
    }

    [Fact]
    public async Task Manual_below_threshold_snapshot_clears_existing_threshold_banner()
    {
        var initial = Snapshot(85);
        var clock = new TestTimeProvider(initial.Fetched);
        using var vm = new MainViewModel(new EmptyDashboardSource(), timeProvider: clock, uiDispatcher: new ImmediateUiDispatcher());
        await vm.ApplyProviderSnapshotsAsync([initial]);
        Assert.True(vm.IsNotificationVisible);

        var manual = initial with { Used = 70, Source = QuotaSource.Manual, Confidence = QuotaConfidence.Manual };
        await vm.ApplyManualSnapshotAsync(manual);

        Assert.False(vm.IsNotificationVisible);
    }

    [Fact]
    public async Task Partial_failure_recovery_with_shifted_start_restores_threshold_title()
    {
        var initial = RollingSnapshot(85, DateTimeOffset.Parse("2026-09-23T12:00:00Z"));
        var clock = new TestTimeProvider(initial.Fetched);
        var app = new MutableApplication(new QuotaRefreshResult([initial], [new(ProviderKind.Claude, FetchStatus.TransientFailure)]));
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: app, timeProvider: clock, uiDispatcher: new ImmediateUiDispatcher());
        await vm.RefreshAsync();
        Assert.Contains("incomplete", vm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);

        clock.Advance(TimeSpan.FromMinutes(1));
        app.Result = new QuotaRefreshResult([RollingSnapshot(85, clock.GetUtcNow(), initial.Window.ResetAt)], []);
        await vm.RefreshAsync();

        Assert.Contains("OpenCode", vm.NotificationBannerText);
        Assert.DoesNotContain("incomplete", vm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_effective_percent_does_not_call_desktop_backend()
    {
        var snapshot = Snapshot(85) with { Used = null, Limit = null, ReportedPercent = null };
        var backend = new FakeDesktopNotificationBackend();
        using var vm = new MainViewModel(new EmptyDashboardSource(), timeProvider: new TestTimeProvider(Snapshot(85).Fetched), uiDispatcher: new ImmediateUiDispatcher(), desktopNotificationBackend: backend);

        await vm.ApplyProviderSnapshotsAsync([snapshot]);

        Assert.Empty(backend.Requests);
    }

    [Fact]
    public void Production_composition_selects_platform_backend_without_calling_it()
    {
        var backend = CompositionRoot.CreateDesktopNotificationBackend();
        if (OperatingSystem.IsLinux()) Assert.IsType<LinuxDesktopNotifications>(backend);
#if WINDOWS
        else if (OperatingSystem.IsWindows()) Assert.IsType<WindowsDesktopNotifications>(backend);
#endif
        else Assert.Null(backend);
    }

    private static QuotaSnapshot Snapshot(decimal percent)
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        return new(ProviderKind.ChatGpt, "acct", "Messages", new(QuotaWindowKind.Weekly, now.AddDays(-1), now.AddDays(6)), percent, 100, null, "percent", now, now, QuotaSource.Official, QuotaConfidence.Official, null, "ChatGPT Plus");
    }

    private static QuotaSnapshot RollingSnapshot(decimal percent, DateTimeOffset now, DateTimeOffset? resetAt = null)
    {
        var reset = resetAt ?? now.AddHours(5);
        return new(ProviderKind.OpenCode, "rolling-account", "Requests",
            new(QuotaWindowKind.Rolling, now, reset, Duration: TimeSpan.FromHours(5), ResetAt: reset),
            percent, 100, null, "percent", now, now, QuotaSource.Official, QuotaConfidence.Official, now.AddMinutes(10), "OpenCode");
    }

    private sealed class MutableApplication(QuotaRefreshResult result) : IQuotaApplication
    {
        public QuotaRefreshResult Result { get; set; } = result;
        public ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result);
    }

    private sealed class FakeDesktopNotificationBackend : IDesktopNotificationBackend
    {
        public List<(string Title, string Reason)> Requests { get; } = [];
        public bool Result { get; set; } = true;
        public Exception? Throw { get; set; }
        public CancellationTokenSource? CancelOnNotify { get; set; }
        public Task<bool> TryNotifyAsync(string title, string reason, CancellationToken token)
        {
            Requests.Add((title, reason));
            if (Throw is { } exception) return Task.FromException<bool>(exception);
            CancelOnNotify?.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }
}

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using QuotaSight.Infrastructure;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// Resident mode, tray and lifecycle contracts, independent of the quota UI that used to
/// sit on the same surfaces: the close policy, the tray menu, the tracked operations and
/// the settings round-trip all behave the same with the observed-usage dashboard.
/// </summary>
public sealed class ResidentModeTests
{
    [Fact]
    public async Task Lifecycle_run_tracks_resident_transition_until_exit_cleanup()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new AppLifecycleCoordinator(() => ValueTask.CompletedTask);
        var transition = coordinator.Run(async _ => await release.Task);
        var exit = coordinator.BeginExitAsync();
        Assert.False(exit.IsCompleted);
        release.SetResult();
        await transition;
        await exit;
    }

    [Fact]
    public async Task Startup_guard_runs_monitor_once_and_exit_stops_the_single_loop()
    {
        var coordinator = new AppLifecycleCoordinator(() => ValueTask.CompletedTask);
        var guard = new AppStartupGuard();
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeCount = 0;
        var monitor = new LinuxStatusNotifierMonitor(() =>
        {
            Interlocked.Increment(ref probeCount);
            probeStarted.SetResult();
            return Task.FromResult(true);
        }, _ => { }, TimeSpan.FromMilliseconds(1));

        async Task Start(CancellationToken token)
        {
            var loop = monitor.RunAsync(token);
            coordinator.Track(loop);
            await Task.CompletedTask;
        }

        await Task.WhenAll(guard.StartOnceAsync(Start, coordinator.CancellationToken), guard.StartOnceAsync(Start, coordinator.CancellationToken));
        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await coordinator.BeginExitAsync().WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var countAtExit = probeCount;
        await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.Equal(1, guard.StartCount);
        Assert.Equal(countAtExit, probeCount);
        Assert.True(coordinator.IsCleanupCompleted);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void External_shutdown_is_cancelled_until_cleanup_completes(bool exiting, bool cleanupCompleted, bool expectedCancel)
    {
        Assert.Equal(expectedCancel, ShutdownPolicy.ShouldCancelExternalRequest(exiting, cleanupCompleted));
    }

    [Fact]
    public async Task External_shutdown_remains_cancelled_until_cleanup_completion()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new AppLifecycleCoordinator(async () => await release.Task);

        var exit = coordinator.BeginExitAsync();
        Assert.True(ShutdownPolicy.ShouldCancelExternalRequest(coordinator.IsExiting, coordinator.IsCleanupCompleted));
        coordinator.MarkExitWithoutCleanup();
        Assert.False(exit.IsCompleted);

        release.SetResult();
        await exit;
        Assert.False(ShutdownPolicy.ShouldCancelExternalRequest(coordinator.IsExiting, coordinator.IsCleanupCompleted));
    }

    [Fact]
    public void Refresh_lifetime_is_detached_before_it_is_cancelled_and_disposed()
    {
        CancellationTokenSource? original = new();
        var detached = AppLifecycleCoordinator.DetachLifetime(ref original);

        Assert.NotNull(detached);
        Assert.Null(original);
        detached!.Cancel();
        detached.Dispose();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Close_policy_requires_effective_resident_mode_and_capability(bool resident, bool capability, bool expected)
    {
        Assert.Equal(expected, ResidentModePolicy.ShouldHideOnClose(resident, capability, exiting: false));
    }

    [Fact]
    public void Tray_primary_click_is_noop_after_disable_or_exit()
    {
        var calls = 0;
        var enabled = true;
        var exiting = false;
        var tray = new TrayController(() => { }, () => calls++, () => { }, () => { }, canPrimary: () => enabled && !exiting);

        tray.PrimaryClick();
        enabled = false;
        tray.PrimaryClick();
        enabled = true;
        exiting = true;
        tray.PrimaryClick();

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Lifecycle_coordinator_drains_tracked_work_and_cleans_once()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupCount = 0;
        var coordinator = new AppLifecycleCoordinator(() => { cleanupCount++; return ValueTask.CompletedTask; });
        coordinator.Track(release.Task);

        var exit = coordinator.BeginExitAsync();
        Assert.False(exit.IsCompleted);
        release.SetResult();
        await exit;
        await coordinator.BeginExitAsync();

        Assert.Equal(1, cleanupCount);
        Assert.True(coordinator.IsExiting);
    }

    [Fact]
    public async Task Linked_refresh_scheduler_stops_before_exit_cleanup_and_second_exit_is_safe()
    {
        var cleanupCount = 0;
        var coordinator = new AppLifecycleCoordinator(() => { cleanupCount++; return ValueTask.CompletedTask; });
        using var refreshLifetime = AppLifecycleCoordinator.CreateLinkedLifetime(coordinator.CancellationToken);
        var scheduler = new RefreshScheduler(TimeSpan.FromMinutes(5));
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var schedulerTask = scheduler.RunAsync(_ =>
        {
            refreshStarted.SetResult();
            return ValueTask.CompletedTask;
        }, refreshLifetime.Token).AsTask();
        coordinator.Track(schedulerTask);

        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await coordinator.BeginExitAsync().WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await coordinator.BeginExitAsync().WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.True(schedulerTask.IsCompleted);
        Assert.Equal(1, cleanupCount);
    }

    [Fact]
    public void Resident_mode_defaults_off_and_legacy_settings_remain_off()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AppSettingsStore(root);
            Directory.CreateDirectory(root);
            File.WriteAllText(store.Path, "{\"Theme\":\"Dark\",\"Language\":\"English\"}");

            Assert.False(new AppSettingsDto().ResidentMode);
            Assert.False(store.Load().ResidentMode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Resident_mode_round_trips_without_affecting_other_settings()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AppSettingsStore(root);
            await store.SaveAsync(new AppSettingsDto(Theme: "Dark", ResidentMode: true), TestContext.Current.CancellationToken);
            var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
            Assert.True(loaded.ResidentMode);
            Assert.Equal("Dark", loaded.Theme);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Settings_state_applies_and_persists_resident_mode()
    {
        var vm = new MainViewModel();
        vm.SetSettings(new AppSettingsDto(ResidentMode: true), persist: false);
        Assert.True(vm.Settings.ResidentMode);
        Assert.True(vm.CurrentSettingsForTests.ResidentMode);
        await vm.SetResidentModeAsync(false, TestContext.Current.CancellationToken);
        Assert.False(vm.Settings.ResidentMode);
    }

    [Fact]
    public void Tray_primary_click_opens_compact_and_context_actions_are_independent()
    {
        var compactShows = 0;
        var fullShows = 0;
        var refreshes = 0;
        var exits = 0;
        var tray = new TrayController(() => fullShows++, () => compactShows++, () => refreshes++, () => exits++);

        tray.PrimaryClick();
        tray.Show();
        tray.Refresh();
        tray.Exit();

        Assert.Equal(1, compactShows);
        Assert.Equal(1, fullShows);
        Assert.Equal(1, refreshes);
        Assert.Equal(1, exits);
    }

    [AvaloniaFact]
    public async Task Refresh_button_operation_is_drained_before_cleanup()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new MainViewModel(usageReadPort: new BlockingBreakdownPort(release));
        var window = new MainWindow(vm);
        var coordinator = new AppLifecycleCoordinator(() => ValueTask.CompletedTask);
        window.OperationRunner = coordinator.Run;
        window.Show();
        try
        {
            window.FindControl<Button>("RefreshButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Yield();
            var exit = coordinator.BeginExitAsync();
            Assert.False(exit.IsCompleted);
            release.SetResult();
            await exit;
        }
        finally { window.Close(); vm.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Compact_window_shares_vm_renders_usage_cards_busy_state_and_hides_on_close()
    {
        var vm = new MainViewModel();
        vm.SetSettings(new AppSettingsDto(TokScaleProviders: ["openai"]), persist: false);
        var window = new CompactQuotaWindow(vm);
        window.Show();
        await Task.Yield();

        Assert.Same(vm, window.DataContext);
        Assert.NotNull(window.FindControl<ItemsControl>("CompactTokScaleCards"));
        Assert.NotNull(window.FindControl<Button>("CompactRefreshButton"));
        Assert.NotNull(window.FindControl<Button>("CompactOpenFullButton"));
        Assert.NotNull(window.FindControl<ProgressBar>("CompactRefreshProgress"));
        Assert.NotNull(window.FindControl<Border>("CompactTokScaleEmptyState"));
        Assert.True(window.FindControl<ItemsControl>("CompactTokScaleCards")!.ItemTemplate is not null);

        window.Close();
        Assert.False(window.IsVisible);
        window.Show();
        Assert.Same(vm, window.DataContext);
        window.Hide();
        vm.Dispose();
    }

    private sealed class BlockingBreakdownPort(TaskCompletionSource release) : IUsageReadPort
    {
        public async Task<UsageBreakdown> ReadBreakdownAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken)
        {
            await release.Task;
            return new(from, to, [], [], []);
        }

        public Task<DailyUsage> ReadDailyAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken) =>
            Task.FromResult(new DailyUsage(from, to, [], 0));
    }
}

using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using QuotaSight.Application;
using QuotaSight.Core;
using QuotaSight.Infrastructure;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

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
        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.BeginExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var countAtExit = probeCount;
        await Task.Delay(20);

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

        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.BeginExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.BeginExitAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(schedulerTask.IsCompleted);
        Assert.Equal(1, cleanupCount);
    }

    [AvaloniaFact]
    public async Task Tracked_codex_poll_blocks_exit_until_poll_finishes()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pollStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new BlockingCodexProvider(pollStarted, release);
        var vm = new MainViewModel(new EmptyDashboardSource());
        var window = new MainWindow(vm, provider);
        var coordinator = new AppLifecycleCoordinator(() => ValueTask.CompletedTask);
        window.OperationRunner = coordinator.Run;
        window.Show();
        try
        {
            vm.OpenProviderFlow();
            vm.SelectProvider(ProviderConnectionChoice.Codex);
            window.FindControl<Button>("CodexStartButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Yield();
            window.FindControl<Button>("CodexPollButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pollStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var exit = coordinator.BeginExitAsync();
            Assert.False(exit.IsCompleted);
            release.SetResult();
            await exit.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { window.Close(); vm.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Tracked_codex_logout_drains_handler_state_continuation_before_cleanup()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CodexAuthorizationState? stateAtCleanup = null;
        var provider = new BlockingCodexLogoutProvider(release);
        var vm = new MainViewModel(new EmptyDashboardSource());
        var window = new MainWindow(vm, provider);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new AppLifecycleCoordinator(async () =>
        {
            stateAtCleanup = vm.CodexState;
            cleanupStarted.SetResult();
            await allowCleanup.Task;
        });
        window.OperationRunner = coordinator.Run;
        window.Show();
        try
        {
            vm.OpenProviderFlow();
            vm.SelectProvider(ProviderConnectionChoice.Codex);
            vm.SetCodexResult(new CodexUiResult(true, CodexAuthorizationState.Connected, "connected"));
            window.FindControl<Button>("CodexLogoutButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var exit = coordinator.BeginExitAsync();
            Assert.False(exit.IsCompleted);
            release.SetResult();
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(CodexAuthorizationState.Disconnected, stateAtCleanup);
            allowCleanup.SetResult();
            await exit.WaitAsync(TimeSpan.FromSeconds(2));


        }
        finally { window.Close(); vm.Dispose(); }
    }

    private sealed class BlockingCodexProvider(TaskCompletionSource started, TaskCompletionSource release) : IProviderUiService
    {
        public ValueTask<ProviderConnectionResult> TestOpenCodeAsync(string key, CancellationToken token) => ValueTask.FromResult(new ProviderConnectionResult(false, "not used"));
        public ValueTask<string> ProbeGitHubCliAsync(CancellationToken token) => ValueTask.FromResult(string.Empty);
        public ValueTask<FetchResult<DeviceAuthorizationStart>> StartGitHubDeviceFlowAsync(CancellationToken token) => ValueTask.FromResult(new FetchResult<DeviceAuthorizationStart>(FetchStatus.Unsupported));
        public ValueTask<FetchResult<string>> PollGitHubDeviceFlowAsync(DeviceAuthorizationStart authorization, CancellationToken token) => ValueTask.FromResult(new FetchResult<string>(FetchStatus.Unsupported));
        public ValueTask<CodexUiResult> StartCodexAsync(CancellationToken token) => ValueTask.FromResult(new CodexUiResult(true, CodexAuthorizationState.AwaitingAuthorization, "started", new("CODE", new Uri("https://example.test"), DateTimeOffset.UtcNow.AddMinutes(5))));
        public async ValueTask<CodexUiResult> PollCodexAsync(CancellationToken token)
        {
            started.SetResult();
            await release.Task;
            return new CodexUiResult(false, CodexAuthorizationState.Pending, "pending");
        }
        public ValueTask<CodexUiResult> LogoutCodexAsync(CancellationToken token) => ValueTask.FromResult(new CodexUiResult(true, CodexAuthorizationState.Disconnected, "logged out"));
        public ValueTask<bool> HasCodexCredentialAsync(CancellationToken token) => ValueTask.FromResult(false);
    }

    private sealed class BlockingCodexLogoutProvider(TaskCompletionSource release) : IProviderUiService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<ProviderConnectionResult> TestOpenCodeAsync(string key, CancellationToken token) => ValueTask.FromResult(new ProviderConnectionResult(false, "not used"));
        public ValueTask<string> ProbeGitHubCliAsync(CancellationToken token) => ValueTask.FromResult(string.Empty);
        public ValueTask<FetchResult<DeviceAuthorizationStart>> StartGitHubDeviceFlowAsync(CancellationToken token) => ValueTask.FromResult(new FetchResult<DeviceAuthorizationStart>(FetchStatus.Unsupported));
        public ValueTask<FetchResult<string>> PollGitHubDeviceFlowAsync(DeviceAuthorizationStart authorization, CancellationToken token) => ValueTask.FromResult(new FetchResult<string>(FetchStatus.Unsupported));
        public ValueTask<CodexUiResult> StartCodexAsync(CancellationToken token) => ValueTask.FromResult(new CodexUiResult(false, CodexAuthorizationState.Disconnected, "not used"));
        public ValueTask<CodexUiResult> PollCodexAsync(CancellationToken token) => ValueTask.FromResult(new CodexUiResult(false, CodexAuthorizationState.Disconnected, "not used"));
        public async ValueTask<CodexUiResult> LogoutCodexAsync(CancellationToken token)
        {
            Started.SetResult();
            await release.Task.ConfigureAwait(false);
            return new CodexUiResult(true, CodexAuthorizationState.Disconnected, "logged out");
        }
        public ValueTask<bool> HasCodexCredentialAsync(CancellationToken token) => ValueTask.FromResult(true);
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
            await store.SaveAsync(new AppSettingsDto(Theme: "Dark", ResidentMode: true));
            var loaded = await store.LoadAsync();
            Assert.True(loaded.ResidentMode);
            Assert.Equal("Dark", loaded.Theme);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Settings_state_applies_and_persists_resident_mode()
    {
        var vm = new MainViewModel(new EmptyDashboardSource());
        vm.SetSettings(new AppSettingsDto(ResidentMode: true), persist: false);
        Assert.True(vm.Settings.ResidentMode);
        Assert.True(vm.CurrentSettingsForTests.ResidentMode);
        await vm.SetResidentModeAsync(false);
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
    public async Task History_delete_cancellation_from_real_button_is_not_unhandled_or_reported_as_failure()
    {
        var history = new BlockingDeleteHistory();
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaHistory: history);
        var entry = new HistoryUiEntry(Guid.NewGuid(), "ChatGPT Plus", "Personal", "Weekly", 42, DateTimeOffset.UtcNow, QuotaSource.Manual, QuotaConfidence.Manual);
        vm.History.Entries.Add(entry);
        vm.Navigate(AppPage.History);
        var window = new MainWindow(vm);
        var coordinator = new AppLifecycleCoordinator(() => ValueTask.CompletedTask);
        window.OperationRunner = coordinator.Run;
        var unhandled = new List<Exception>();
        void OnUnhandled(object? sender, Avalonia.Threading.DispatcherUnhandledExceptionEventArgs args)
        {
            unhandled.Add(args.Exception);
            args.Handled = true;
        }
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += OnUnhandled;
        window.Show();
        try
        {
            var deleteButton = window.GetVisualDescendants().OfType<Button>().Single(button =>
                Avalonia.Automation.AutomationProperties.GetName(button) == "Delete history entry");
            deleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await history.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var exit = coordinator.BeginExitAsync();
            await exit.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(100);

            Assert.Empty(unhandled);
            Assert.DoesNotContain(vm.CopyText.HistoryDeleteFailure, vm.NotificationBannerText, StringComparison.Ordinal);
            Assert.DoesNotContain(vm.CopyText.HistoryDeleteAllFailure, vm.NotificationBannerText, StringComparison.Ordinal);
        }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException -= OnUnhandled;
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task History_delete_all_cancellation_from_real_button_is_not_unhandled_or_reported_as_failure()
    {
        var history = new BlockingDeleteHistory();
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaHistory: history);
        vm.Navigate(AppPage.History);
        var window = new MainWindow(vm);
        var coordinator = new AppLifecycleCoordinator(() => ValueTask.CompletedTask);
        window.OperationRunner = coordinator.Run;
        var unhandled = new List<Exception>();
        void OnUnhandled(object? sender, Avalonia.Threading.DispatcherUnhandledExceptionEventArgs args)
        {
            unhandled.Add(args.Exception);
            args.Handled = true;
        }
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += OnUnhandled;
        window.Show();
        try
        {
            var deleteButton = window.FindControl<WrapPanel>("HistoryActions")!.GetVisualDescendants().OfType<Button>().Single(button =>
                Avalonia.Automation.AutomationProperties.GetName(button) == "Delete all history");
            deleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await history.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var exit = coordinator.BeginExitAsync();
            await exit.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(100);

            Assert.Empty(unhandled);
            Assert.DoesNotContain(vm.CopyText.HistoryDeleteFailure, vm.NotificationBannerText, StringComparison.Ordinal);
            Assert.DoesNotContain(vm.CopyText.HistoryDeleteAllFailure, vm.NotificationBannerText, StringComparison.Ordinal);
        }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException -= OnUnhandled;
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Refresh_button_operation_is_drained_before_cleanup()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: new BlockedRefreshApplication(release));
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
    public async Task History_export_write_respects_exit_cancellation()
    {
        var destination = new BlockingExportDestination("text/csv");
        var vm = new MainViewModel(new EmptyDashboardSource());
        vm.Navigate(AppPage.History);
        vm.History.Entries.Add(new HistoryUiEntry(Guid.NewGuid(), "ChatGPT Plus", "Personal", "Weekly", 42, DateTimeOffset.UtcNow, QuotaSource.Manual, QuotaConfidence.Manual));
        var window = new MainWindow(vm, new UiProviderFacade(), null, destination);
        var coordinator = new AppLifecycleCoordinator(() => ValueTask.CompletedTask);
        window.OperationRunner = coordinator.Run;
        var unhandled = new List<Exception>();
        void OnUnhandled(object? sender, Avalonia.Threading.DispatcherUnhandledExceptionEventArgs args)
        {
            unhandled.Add(args.Exception);
            args.Handled = true;
        }
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += OnUnhandled;
        window.Show();
        try
        {
            window.FindControl<WrapPanel>("HistoryActions")!.GetVisualDescendants().OfType<Button>()
                .Single(button => Avalonia.Automation.AutomationProperties.GetName(button) == "Export history CSV")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await destination.Opened.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var exit = coordinator.BeginExitAsync();
            await exit.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(exit.IsCompletedSuccessfully);
            Assert.Empty(unhandled);
        }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException -= OnUnhandled;
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData("Export CSV", "text/csv")]
    [InlineData("Export JSON", "application/json")]
    public async Task History_export_button_tracks_write_and_async_dispose_until_exit(string buttonName, string contentType)
    {
        var destination = new BlockingExportDestination(contentType);
        var vm = new MainViewModel(new EmptyDashboardSource());
        vm.Navigate(AppPage.History);
        vm.History.Entries.Add(new HistoryUiEntry(Guid.NewGuid(), "ChatGPT Plus", "Personal", "Weekly", 42, DateTimeOffset.UtcNow, QuotaSource.Manual, QuotaConfidence.Manual));
        var window = new MainWindow(vm, new UiProviderFacade(), null, destination);
        var coordinator = new AppLifecycleCoordinator(() => ValueTask.CompletedTask);
        window.OperationRunner = coordinator.Run;
        window.Show();
        try
        {
            window.FindControl<WrapPanel>("HistoryActions")!.GetVisualDescendants().OfType<Button>()
                .Single(button => Avalonia.Automation.AutomationProperties.GetName(button) == (buttonName == "Export CSV" ? "Export history CSV" : "Export history JSON"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await destination.Opened.Task.WaitAsync(TimeSpan.FromSeconds(2));
            // Complete the write before exit so the tracked task is not cancelled.
            destination.AllowWrite.SetResult();
            await destination.Written.Task.WaitAsync(TimeSpan.FromSeconds(2));
            // Now begin exit — the tracked task is blocked on DisposeAsync (AllowDispose).
            var exit = coordinator.BeginExitAsync();
            Assert.False(exit.IsCompleted);
            destination.AllowDispose.SetResult();
            await exit.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(contentType, destination.ContentType);
            Assert.Contains(contentType == "text/csv" ? "Provider,Account,Window" : "\"provider\"", destination.Content);
        }
        finally { window.Close(); vm.Dispose(); }
    }

    private sealed class BlockingExportDestination : IHistoryExportDestination
    {
        private readonly string contentType;
        public BlockingExportDestination(string contentType) { this.contentType = contentType; ContentType = contentType; }
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Content { get; private set; } = string.Empty;
        public string ContentType { get; }
        public Task<IHistoryExportFile?> PickAsync(string suggestedName, string requestedContentType)
        {
            Assert.Equal(contentType, requestedContentType);
            return Task.FromResult<IHistoryExportFile?>(new BlockingExportFile(this));
        }
        private sealed class BlockingExportFile(BlockingExportDestination owner) : IHistoryExportFile
        {
            public Task<Stream> OpenWriteAsync()
            {
                owner.Opened.SetResult();
                return Task.FromResult<Stream>(new BlockingExportStream(owner));
            }
        }
        private sealed class BlockingExportStream(BlockingExportDestination owner) : MemoryStream
        {
            private bool writeCancelled;
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => WriteCoreAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
                => WriteCoreAsync(buffer, cancellationToken);
            public override Task FlushAsync(CancellationToken cancellationToken)
                => FlushCoreAsync(cancellationToken).AsTask();
            private async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
            {
                try
                {
                    await owner.AllowWrite.Task.WaitAsync(cancellationToken);
                    await base.WriteAsync(buffer, cancellationToken);
                    owner.Content = Encoding.UTF8.GetString(ToArray());
                    owner.Written.TrySetResult();
                }
                catch (OperationCanceledException)
                {
                    writeCancelled = true;
                    throw;
                }
            }
            private async ValueTask FlushCoreAsync(CancellationToken cancellationToken)
            {
                // During dispose-after-cancel, StreamWriter calls FlushAsync with
                // CancellationToken.None. Short-circuit to prevent a deadlock when
                // the tracked operation was already cancelled.
                if (writeCancelled || cancellationToken.IsCancellationRequested)
                    return;
                await owner.AllowWrite.Task.WaitAsync(cancellationToken);
                await base.FlushAsync(cancellationToken);
            }
            public override async ValueTask DisposeAsync()
            {
                // In production, disposal completes quickly even after cancellation.
                // Simulate this by not blocking on AllowDispose when write was cancelled.
                if (!writeCancelled)
                    await owner.AllowDispose.Task;
                await base.DisposeAsync();
            }
        }
    }

    private sealed class BlockingDeleteHistory : IQuotaHistory
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask AppendAsync(IReadOnlyList<QuotaSnapshot> snapshots, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<QuotaSnapshot>> ReadAsync(DateOnly day, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<QuotaSnapshot>>([]);
        public ValueTask<IReadOnlyList<QuotaHistoryEntry>> ReadEventsAsync(DateOnly day, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<QuotaHistoryEntry>>([]);
        public async ValueTask DeleteEventAsync(Guid eventId, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public ValueTask PruneAsync(DateOnly before, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async ValueTask DeleteAsync(DateOnly? day, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public ValueTask ExportJsonAsync(Stream output, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ExportCsvAsync(Stream output, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class BlockedRefreshApplication(TaskCompletionSource release) : IQuotaApplication
    {
        public async ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken)
        {
            await release.Task;
            return new([], []);
        }
    }

    [AvaloniaFact]
    public async Task Compact_window_shares_vm_renders_cards_busy_state_and_hides_on_close()
    {
        var vm = new MainViewModel(new DemoDashboardSource());
        var window = new CompactQuotaWindow(vm);
        window.Show();
        await Task.Yield();

        Assert.Same(vm, window.DataContext);
        Assert.NotNull(window.FindControl<ItemsControl>("CompactProviderCards"));
        Assert.NotNull(window.FindControl<Button>("CompactRefreshButton"));
        Assert.NotNull(window.FindControl<Button>("CompactOpenFullButton"));
        Assert.NotNull(window.FindControl<ProgressBar>("CompactRefreshProgress"));
        Assert.True(window.FindControl<ItemsControl>("CompactProviderCards")!.ItemTemplate is not null);

        window.Close();
        Assert.False(window.IsVisible);
        window.Show();
        Assert.Same(vm, window.DataContext);
        window.Hide();
        vm.Dispose();
    }
}

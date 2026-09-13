using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using QuotaSight.Application;
using QuotaSight.Core;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

internal static class ConcurrencyFixtures
{
    public static QuotaSnapshot Snapshot(int value) => new(ProviderKind.OpenCode, $"account-{value % 3}", "usage", new(QuotaWindowKind.Rolling, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1)), value, 100, null, "requests", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, QuotaSource.Official, QuotaConfidence.Official, DateTimeOffset.UtcNow.AddHours(1), "OpenCode Go");
}

internal class InMemoryQuotaHistory(IEnumerable<QuotaSnapshot> snapshots) : IQuotaHistory
{
    public List<QuotaHistoryEntry> Events { get; } = snapshots.Select(s => new QuotaHistoryEntry(Guid.NewGuid(), s)).ToList();
    public bool ReadEventsOffUi { get; private set; }
    public bool AppendOffUi { get; private set; }
    public bool DeleteOffUi { get; private set; }
    public virtual ValueTask AppendAsync(IReadOnlyList<QuotaSnapshot> values, CancellationToken token)
    {
        AppendOffUi = !Dispatcher.UIThread.CheckAccess();
        Events.AddRange(values.Select(s => new QuotaHistoryEntry(Guid.NewGuid(), s)));
        return ValueTask.CompletedTask;
    }
    public ValueTask<IReadOnlyList<QuotaSnapshot>> ReadAsync(DateOnly day, CancellationToken token) => ValueTask.FromResult<IReadOnlyList<QuotaSnapshot>>(Events.Select(e => e.Snapshot).ToList());
    public virtual ValueTask<IReadOnlyList<QuotaHistoryEntry>> ReadEventsAsync(DateOnly day, CancellationToken token)
    {
        ReadEventsOffUi = !Dispatcher.UIThread.CheckAccess();
        return ValueTask.FromResult<IReadOnlyList<QuotaHistoryEntry>>(day == DateOnly.FromDateTime(DateTime.UtcNow) ? Events.ToList() : []);
    }
    public virtual ValueTask DeleteEventAsync(Guid id, CancellationToken token)
    {
        DeleteOffUi = !Dispatcher.UIThread.CheckAccess();
        Events.RemoveAll(e => e.EventId == id);
        return ValueTask.CompletedTask;
    }
    public ValueTask PruneAsync(DateOnly before, CancellationToken token) => ValueTask.CompletedTask;
    public virtual ValueTask DeleteAsync(DateOnly? day, CancellationToken token) { Events.Clear(); return ValueTask.CompletedTask; }
    public ValueTask ExportJsonAsync(Stream output, CancellationToken token) => ValueTask.CompletedTask;
    public ValueTask ExportCsvAsync(Stream output, CancellationToken token) => ValueTask.CompletedTask;
}

public sealed class ConcurrencyContractTests
{
    private static QuotaSnapshot Snapshot(int value) => ConcurrencyFixtures.Snapshot(value);

    // Acceptance A: a blocked storage delete does not stall queries; the delete request completes
    // only after a publication covering its data revision, and exports accepted behind it include
    // every matching row without the deleted entry.
    [Fact]
    public async Task Blocked_delete_lets_queries_publish_and_completes_with_exports_after_covering_publication()
    {
        var history = new GatedMutationHistory([Snapshot(0), Snapshot(1), Snapshot(1), Snapshot(2)]);
        var state = new HistoryState(history, new ImmediateUiDispatcher());
        await state.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));
        state.AccountFilter = "account-1";
        await state.WaitForPublishedAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var target = history.Events.First(e => e.Snapshot.Account == "account-1").EventId;
        var delete = state.DeleteAsync(target);
        await history.MutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var csvTask = state.ExportCsvAsync();
        var jsonTask = state.ExportJsonAsync();

        var localized = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(state.FilteredEntries) && state.FilteredEntries.Count > 0 && state.FilteredEntries.All(entry => entry.WindowDisplay == "ローリング枠")) localized.TrySetResult(true);
        };
        state.SetLanguage(UiLanguage.Japanese);
        await localized.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(delete.IsCompleted);
        Assert.False(csvTask.IsCompleted);
        Assert.False(jsonTask.IsCompleted);

        history.ReleaseMutation.TrySetResult(true);
        Assert.True(await delete.WaitAsync(TimeSpan.FromSeconds(5)));
        var csv = await csvTask.WaitAsync(TimeSpan.FromSeconds(5));
        var json = await jsonTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(state.FilteredEntries);
        Assert.Equal(2, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("account-0", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("account-2", json, StringComparison.Ordinal);
        Assert.Contains("account-1", json, StringComparison.Ordinal);
    }

    // Acceptance A (DeleteAll variant): a blocked DeleteAll does not stall queries and its export
    // reflects the emptied state once the covering publication arrives.
    [Fact]
    public async Task Blocked_delete_all_lets_queries_publish_and_export_reflects_empty_state()
    {
        var history = new GatedMutationHistory([Snapshot(0), Snapshot(1)]);
        var state = new HistoryState(history, new ImmediateUiDispatcher());
        await state.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var deleteAll = state.DeleteAllAsync();
        await history.MutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var csvTask = state.ExportCsvAsync();

        var filtered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(state.FilteredEntries) && state.FilteredEntries.All(entry => entry.Account == "account-1")) filtered.TrySetResult(true);
        };
        state.AccountFilter = "account-1";
        await filtered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(deleteAll.IsCompleted);

        history.ReleaseMutation.TrySetResult(true);
        await deleteAll.WaitAsync(TimeSpan.FromSeconds(5));
        var csv = await csvTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(state.Entries);
        Assert.Single(csv.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    // Acceptance A: an obsolete build finishing late must not complete CRUD or resurrect deleted data.
    [Fact]
    public async Task Obsolete_build_completion_does_not_complete_crud_or_restore_deleted_history()
    {
        var history = new InMemoryQuotaHistory([Snapshot(1)]);
        var scheduler = new BlockingBuildScheduler(blockedCall: 2);
        var state = new HistoryState(history, new ImmediateUiDispatcher(), scheduler);
        await state.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));

        state.AccountFilter = "account-1";
        await scheduler.BlockedBuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deleteAll = state.DeleteAllAsync();
        await deleteAll.WaitAsync(TimeSpan.FromSeconds(5));

        scheduler.ReleaseBlockedBuild.TrySetResult(true);
        await scheduler.BlockedBuildCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.WaitForPublishedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(state.Entries);
        Assert.Empty(state.FilteredEntries);
        Assert.Empty(state.DisplayedEntries);
    }

    [Fact]
    public async Task History_reload_delete_deleteall_append_are_serialized()
    {
        var history = new InMemoryQuotaHistory([Snapshot(1)]);
        var state = new HistoryState(history, new ImmediateUiDispatcher());
        await state.InitializeAsync();
        var reload = state.RefreshAfterPersistAsync();
        var delete = state.DeleteAsync(history.Events[0].EventId);
        var deleteAll = state.DeleteAllAsync();
        var append = state.AppendAndReloadAsync([Snapshot(2)]);
        await Task.WhenAll(reload, delete, deleteAll, append).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await delete);
        Assert.Equal(history.Events.Select(e => e.Snapshot.Account), state.Entries.Select(e => e.Account));
        Assert.Equal(state.FilteredEntries, state.DisplayedEntries);
        Assert.Equal(history.Events.Count, state.Entries.Count);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("delete")]
    [InlineData("append")]
    public async Task History_queue_recovers_after_storage_fault(string operation)
    {
        var history = new FaultOnceHistory(operation);
        var state = new HistoryState(history, new ImmediateUiDispatcher());
        if (operation == "delete") await state.InitializeAsync();
        var id = operation == "delete" ? history.Events[0].EventId : Guid.NewGuid();
        await Assert.ThrowsAnyAsync<Exception>(() => operation switch
        {
            "read" => state.InitializeAsync(),
            "delete" => state.DeleteAsync(id),
            _ => state.AppendAndReloadAsync([Snapshot(1)])
        });
        await state.AppendAndReloadAsync([Snapshot(2)]).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(history.SuccessfulOperationObserved);
    }

    [Fact]
    public async Task DeleteAll_does_not_allow_an_old_projection_to_restore_deleted_history()
    {
        var history = new InMemoryQuotaHistory([Snapshot(1)]);
        var scheduler = new BlockingBuildScheduler(blockedCall: 2);
        var state = new HistoryState(history, new ImmediateUiDispatcher(), scheduler);
        await state.InitializeAsync();

        state.AccountFilter = "account-1";
        await scheduler.BlockedBuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.DeleteAllAsync().WaitAsync(TimeSpan.FromSeconds(5));

        scheduler.ReleaseBlockedBuild.TrySetResult(true);
        await scheduler.BlockedBuildCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.WaitForPublishedAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(state.Entries);
        Assert.Empty(state.FilteredEntries);
        Assert.Empty(state.DisplayedEntries);
    }

    [Fact]
    public async Task Async_export_does_not_observe_new_raw_data_before_its_projection_is_published()
    {
        var history = new InMemoryQuotaHistory([]);
        var dispatcher = new GatedPublishDispatcher();
        var state = new HistoryState(history, dispatcher);
        await state.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));

        dispatcher.HoldNext();
        var replacement = Snapshot(99) with { Account = "new-account" };
        var reload = state.AppendAndReloadAsync([replacement]);
        await dispatcher.Held.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var export = state.ExportCsvAsync();
        await Task.Yield();
        Assert.False(export.IsCompleted);

        dispatcher.Release();
        var csv = await export.WaitAsync(TimeSpan.FromSeconds(5));
        await reload.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("new-account", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("account-1", csv, StringComparison.Ordinal);
    }

    // Acceptance B: an export accepted while a filter is desired must serialize exactly that
    // requested query even when a later filter publishes first; the export never silently
    // substitutes the later query, and the later query still owns the published state.
    [Fact]
    public async Task Async_export_serializes_the_query_accepted_at_request_time()
    {
        var history = new InMemoryQuotaHistory(Enumerable.Range(0, 180).Select(Snapshot).ToList());
        var scheduler = new BlockingBuildScheduler(blockedCall: 2);
        var state = new HistoryState(history, new ImmediateUiDispatcher(), scheduler);
        await state.InitializeAsync();

        state.AccountFilter = "account-1";
        await scheduler.BlockedBuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var csvTask = state.ExportCsvAsync();
        var jsonTask = state.ExportJsonAsync();
        Assert.False(csvTask.IsCompleted);
        Assert.False(jsonTask.IsCompleted);

        state.AccountFilter = "account-2";
        var csv = await csvTask.WaitAsync(TimeSpan.FromSeconds(5));
        var json = await jsonTask.WaitAsync(TimeSpan.FromSeconds(5));

        scheduler.ReleaseBlockedBuild.TrySetResult(true);
        await scheduler.BlockedBuildCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.WaitForPublishedAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var rows = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(rows.Length > HistoryState.PageSize + 1);
        Assert.DoesNotContain("account-0", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("account-2", csv, StringComparison.Ordinal);
        Assert.Contains("account-1", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("account-0", json, StringComparison.Ordinal);
        Assert.DoesNotContain("account-2", json, StringComparison.Ordinal);
        Assert.Contains("account-1", json, StringComparison.Ordinal);
        Assert.All(state.FilteredEntries, entry => Assert.Equal("account-2", entry.Account));
    }

    // Acceptance C: repeated Next uses the desired requested page; back -> filter -> Next is
    // deterministic and overshoot normalizes to the final page.
    [Fact]
    public async Task Paging_commands_use_desired_requested_page_and_normalize_deterministically()
    {
        var history = new InMemoryQuotaHistory(Enumerable.Range(0, 250).Select(i => Snapshot(i) with { Account = "acct" }).ToList());
        var state = new HistoryState(history, new ImmediateUiDispatcher());
        await state.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(5, state.PageCount);

        state.NextPage();
        state.NextPage();
        state.NextPage();
        await state.WaitForPublishedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, state.CurrentPage);
        Assert.Equal(3, state.RequestedPage);

        state.PreviousPage();
        state.AccountFilter = "acct";
        state.NextPage();
        await state.WaitForPublishedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, state.CurrentPage);
        Assert.Equal(1, state.RequestedPage);

        for (var i = 0; i < 10; i++) state.NextPage();
        await state.WaitForPublishedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, state.CurrentPage);
        Assert.Equal(4, state.RequestedPage);
        Assert.False(state.HasNextPage);
        Assert.True(state.HasPreviousPage);
    }

    // Acceptance D: a projection scheduler fault is observable through the waiting request and the
    // queue recovers on the next command.
    [Fact]
    public async Task Projection_scheduler_fault_is_observable_and_queue_recovers()
    {
        var state = new HistoryState(new InMemoryQuotaHistory([Snapshot(1)]), new ImmediateUiDispatcher(), new FaultOnSecondProjectionScheduler());
        await state.InitializeAsync();

        state.AccountFilter = "account-1";
        await Assert.ThrowsAsync<InvalidOperationException>(() => state.WaitForPublishedAsync());

        state.AccountFilter = "All accounts";
        await state.WaitForPublishedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(state.FilteredEntries);
    }

    // Acceptance D: a dispatcher fault surfaces on the waiting request and later commands recover.
    [Fact]
    public async Task Dispatcher_fault_fails_the_waiting_request_and_next_command_recovers()
    {
        var dispatcher = new FaultOnceDispatcher();
        var state = new HistoryState(new InMemoryQuotaHistory([Snapshot(1)]), dispatcher);
        await state.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));

        dispatcher.FaultNext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => state.RefreshAfterPersistAsync());

        await state.AppendAndReloadAsync([Snapshot(2) with { Account = "recovered" }]).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(state.Entries, entry => entry.Account == "recovered");
    }

    // Acceptance D: a notification subscriber exception faults the responsible request, records one
    // safe LoadError through a later publish, and the queue keeps serving commands.
    [Fact]
    public async Task Notification_subscriber_exception_faults_request_records_load_error_and_recovers()
    {
        var state = new HistoryState(new InMemoryQuotaHistory([Snapshot(1)]), new ImmediateUiDispatcher());
        await state.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var thrown = false;
        state.PropertyChanged += (_, e) =>
        {
            if (!thrown && e.PropertyName == nameof(state.DisplayedEntries))
            {
                thrown = true;
                throw new InvalidOperationException("subscriber failed");
            }
        };
        var reload = state.RefreshAfterPersistAsync();
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => reload);
        Assert.Equal("subscriber failed", observed.Message);

        await state.WaitForPublishedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("subscriber failed", state.LoadError);

        await state.AppendAndReloadAsync([Snapshot(2) with { Account = "after-fault" }]).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(state.Entries, entry => entry.Account == "after-fault");
        Assert.Null(state.LoadError);
    }

    // Acceptance E: once Dispose is established, releasing a publish blocked before its commit must
    // not change state or notify, and every pending request cancels.
    [Fact]
    public async Task Dispose_blocks_pending_publish_and_cancels_every_pending_request()
    {
        var history = new InMemoryQuotaHistory([Snapshot(1)]);
        var dispatcher = new GatedPublishDispatcher();
        var state = new HistoryState(history, dispatcher);
        await state.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));

        dispatcher.HoldNext();
        state.AccountFilter = "account-1";
        await dispatcher.Held.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var delete = state.DeleteAsync(history.Events[0].EventId);
        var barrier = state.WaitForPublishedAsync();
        var export = state.ExportCsvAsync();

        var notifications = 0;
        state.PropertyChanged += (_, _) => notifications++;
        var displayedBefore = state.DisplayedEntries;
        state.Dispose();
        dispatcher.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delete.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => barrier.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, notifications);
        Assert.Same(displayedBefore, state.DisplayedEntries);
    }

    // Acceptance F: caller cancellation cancels only that request; the storage chain and a later
    // request complete consistently.
    [Fact]
    public async Task Caller_cancellation_cancels_only_the_waiting_request()
    {
        var history = new GatedMutationHistory([Snapshot(1)]);
        var state = new HistoryState(history, new ImmediateUiDispatcher());
        await state.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var target = history.Events[0].EventId;

        using var cancellation = new CancellationTokenSource();
        var delete = state.DeleteAsync(target, cancellation.Token);
        await history.MutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var append = state.AppendAndReloadAsync([Snapshot(2) with { Account = "later" }]);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delete.WaitAsync(TimeSpan.FromSeconds(5)));

        await append.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(state.Entries, entry => entry.Id == target);
        Assert.Contains(state.Entries, entry => entry.Account == "later");
        var csv = await state.ExportCsvAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("later", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_mixed_origins_single_flight_and_scheduled_skip()
    {
        var application = new GatedApplication(new QuotaRefreshResult([], []));
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: application, uiDispatcher: new ImmediateUiDispatcher());
        var manual = vm.RefreshAsync(RefreshOrigin.Manual);
        await application.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var scheduled = vm.RefreshAsync(RefreshOrigin.Scheduled);
        await Task.WhenAll(scheduled, Task.CompletedTask);
        Assert.Equal(1, application.Calls);
        application.Release.TrySetResult(true);
        await manual;

        application.Reset();
        var scheduledOwner = vm.RefreshAsync(RefreshOrigin.Scheduled);
        await application.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var manualWaiter = vm.RefreshAsync(RefreshOrigin.Manual);
        Assert.False(manualWaiter.IsCompleted);
        application.Release.TrySetResult(true);
        await Task.WhenAll(scheduledOwner, manualWaiter);
        Assert.Equal(2, application.Calls);
        Assert.Equal(1, application.MaximumConcurrentCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Refresh_gate_reacquires_after_cancel_and_exception(bool cancel)
    {
        var application = new FaultThenSuccessApplication(cancel);
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: application, uiDispatcher: new ImmediateUiDispatcher());
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.RefreshAsync(RefreshOrigin.Manual));
        else await vm.RefreshAsync(RefreshOrigin.Manual);
        if (cancel) Assert.True(application.FirstCancellationObserved);
        await vm.RefreshAsync(RefreshOrigin.Manual);
        Assert.Equal(2, application.Calls);
    }

    [Fact]
    public async Task Tray_success_resets_consecutive_failure_count()
    {
        var probes = new Queue<TaskCompletionSource<bool>>();
        var results = new List<bool>();
        var probeNumber = 0;
        using var stop = new CancellationTokenSource();
        foreach (var value in new[] { true, false, false, true, false, false, false, true })
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            probes.Enqueue(gate);
            gate.TrySetResult(value);
        }
        var monitor = new LinuxStatusNotifierMonitor(() =>
        {
            var value = probes.Dequeue().Task;
            if (Interlocked.Increment(ref probeNumber) == 8) stop.Cancel();
            return value;
        }, results.Add, TimeSpan.Zero);
        var run = monitor.RunAsync(stop.Token);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([true, false, true], results);
    }

    internal sealed class GatedMutationHistory(IEnumerable<QuotaSnapshot> snapshots) : InMemoryQuotaHistory(snapshots)
    {
        public TaskCompletionSource<bool> MutationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseMutation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask DeleteEventAsync(Guid id, CancellationToken token)
        {
            MutationStarted.TrySetResult(true);
            await ReleaseMutation.Task.WaitAsync(token);
            await base.DeleteEventAsync(id, token);
        }
        public override async ValueTask DeleteAsync(DateOnly? day, CancellationToken token)
        {
            MutationStarted.TrySetResult(true);
            await ReleaseMutation.Task.WaitAsync(token);
            await base.DeleteAsync(day, token);
        }
    }

    internal sealed class FaultOnceHistory(string failingOperation) : InMemoryQuotaHistory([ConcurrencyFixtures.Snapshot(1)])
    {
        private bool failed;
        public bool SuccessfulOperationObserved { get; private set; }
        private bool ShouldFail(string name) { if (name != failingOperation || failed) return false; failed = true; return true; }
        public override ValueTask<IReadOnlyList<QuotaHistoryEntry>> ReadEventsAsync(DateOnly day, CancellationToken token)
        {
            if (ShouldFail("read")) return ValueTask.FromException<IReadOnlyList<QuotaHistoryEntry>>(new InvalidOperationException("read"));
            SuccessfulOperationObserved = true;
            return base.ReadEventsAsync(day, token);
        }
        public override ValueTask DeleteEventAsync(Guid id, CancellationToken token)
        {
            if (ShouldFail("delete")) return ValueTask.FromException(new InvalidOperationException("delete"));
            SuccessfulOperationObserved = true;
            return base.DeleteEventAsync(id, token);
        }
        public override ValueTask AppendAsync(IReadOnlyList<QuotaSnapshot> values, CancellationToken token)
        {
            if (ShouldFail("append")) return ValueTask.FromException(new InvalidOperationException("append"));
            SuccessfulOperationObserved = true;
            return base.AppendAsync(values, token);
        }
    }

    internal sealed class GatedPublishDispatcher : IUiDispatcher
    {
        private volatile bool hold;
        public TaskCompletionSource<bool> Held { get; private set; } = NewTcs();
        private TaskCompletionSource<bool> release = NewTcs();
        public void HoldNext()
        {
            Held = NewTcs();
            release = NewTcs();
            hold = true;
        }
        public void Release() => release.TrySetResult(true);
        public bool CheckAccess() => true;
        public async Task InvokeAsync(Action action)
        {
            if (hold)
            {
                Held.TrySetResult(true);
                await release.Task;
            }
            action();
        }
        public Task InvokeAsync(Func<Task> action) => action();
        private static TaskCompletionSource<bool> NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class FaultOnceDispatcher : IUiDispatcher
    {
        public bool FaultNext { get; set; }
        public bool CheckAccess() => true;
        public Task InvokeAsync(Action action)
        {
            if (FaultNext)
            {
                FaultNext = false;
                throw new InvalidOperationException("dispatcher fault");
            }
            action();
            return Task.CompletedTask;
        }
        public Task InvokeAsync(Func<Task> action) => action();
    }

    private sealed class GatedApplication(QuotaRefreshResult result) : IQuotaApplication
    {
        private readonly object gate = new();
        public TaskCompletionSource<bool> Started { get; private set; } = NewTcs();
        public TaskCompletionSource<bool> Release { get; private set; } = NewTcs();
        public int Calls { get; private set; }
        public int MaximumConcurrentCalls { get; private set; }
        private int active;
        public async ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken)
        {
            lock (gate) { Calls++; active++; MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, active); }
            Started.TrySetResult(true);
            try { await Release.Task.WaitAsync(cancellationToken); return result; }
            finally { lock (gate) active--; }
        }
        public void Reset() { Started = NewTcs(); Release = NewTcs(); Calls = 0; MaximumConcurrentCalls = 0; }
        private static TaskCompletionSource<bool> NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FaultThenSuccessApplication(bool cancel) : IQuotaApplication
    {
        public int Calls { get; private set; }
        public bool FirstCancellationObserved { get; private set; }
        public ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken token)
        {
            Calls++;
            if (Calls == 1)
            {
                if (cancel) { FirstCancellationObserved = true; return ValueTask.FromCanceled<QuotaRefreshResult>(new CancellationToken(true)); }
                return ValueTask.FromException<QuotaRefreshResult>(new InvalidOperationException("first"));
            }
            return ValueTask.FromResult(new QuotaRefreshResult([], []));
        }
    }

    private sealed class FaultOnSecondProjectionScheduler : IHistoryProjectionScheduler
    {
        private int calls;
        public Task<T> ScheduleAsync<T>(Func<T> work) => Interlocked.Increment(ref calls) == 2 ? Task.FromException<T>(new InvalidOperationException("projection scheduler fault")) : Task.FromResult(work());
    }

    internal sealed class BlockingBuildScheduler(int blockedCall) : IHistoryProjectionScheduler
    {
        private int calls;
        public TaskCompletionSource<bool> BlockedBuildStarted { get; } = NewTcs();
        public TaskCompletionSource<bool> ReleaseBlockedBuild { get; } = NewTcs();
        public TaskCompletionSource<bool> BlockedBuildCompleted { get; } = NewTcs();
        public async Task<T> ScheduleAsync<T>(Func<T> work)
        {
            if (Interlocked.Increment(ref calls) != blockedCall) return work();
            BlockedBuildStarted.TrySetResult(true);
            await ReleaseBlockedBuild.Task;
            try { return work(); }
            finally { BlockedBuildCompleted.TrySetResult(true); }
        }
        private static TaskCompletionSource<bool> NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

// Avalonia-backed concurrency tests live in their own class so that plain [Fact] tests filtered in
// together never share a class with dispatcher-frame tests (avoids Dispatcher.PushFrame on
// unsupported platforms).
public sealed class ConcurrencyContractAvaloniaTests
{
    private static QuotaSnapshot Snapshot(int value) => ConcurrencyFixtures.Snapshot(value);

    // Acceptance G: with the production AvaloniaUiDispatcher and 5000 persisted rows, storage and
    // projection work stays off the UI thread, the UI advances while a worker is blocked, and
    // notifications plus bounded projections arrive on the UI thread.
    [AvaloniaFact]
    public async Task History_pipeline_runs_worker_stages_off_ui_and_publishes_bounded_state_on_ui()
    {
        var history = new InMemoryQuotaHistory(Enumerable.Range(0, 5000).Select(Snapshot).ToList());
        var scheduler = new RecordingProjectionScheduler();
        var state = new HistoryState(history, new AvaloniaUiDispatcher(), scheduler);
        var uiNotifications = new List<bool>();
        state.PropertyChanged += (_, _) => uiNotifications.Add(Dispatcher.UIThread.CheckAccess());

        await state.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(10));

        scheduler.HoldNext();
        state.SetLanguage(UiLanguage.Japanese);
        await scheduler.HeldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var sentinel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => sentinel.TrySetResult(true));
        await sentinel.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.Release();
        await state.WaitForPublishedAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(state.FilteredEntries.Take(10), entry => Assert.Equal("ローリング枠", entry.WindowDisplay));

        Assert.True(await state.DeleteAsync(history.Events[0].EventId).WaitAsync(TimeSpan.FromSeconds(10)));
        await state.AppendAndReloadAsync([Snapshot(7)]).WaitAsync(TimeSpan.FromSeconds(10));

        var export = state.ExportCsvAsync();
        var exportSentinel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => exportSentinel.TrySetResult(true));
        await exportSentinel.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var csv = await export.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(history.ReadEventsOffUi);
        Assert.True(history.AppendOffUi);
        Assert.True(history.DeleteOffUi);
        Assert.True(scheduler.RanBuilds);
        Assert.True(scheduler.AllBuildsOffUi);
        Assert.Equal(5000, state.Entries.Count);
        Assert.Equal(HistoryState.PageSize, state.DisplayedEntries.Count);
        Assert.InRange(state.SparklinePoints.Count, 1, HistoryState.SparklineLimit);
        Assert.Equal(5001, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.NotEmpty(uiNotifications);
        Assert.All(uiNotifications, Assert.True);
        state.Dispose();
    }

    [AvaloniaFact]
    public async Task Scheduled_refresh_all_publish_events_run_on_ui()
    {
        var history = new InMemoryQuotaHistory([Snapshot(90)]);
        var application = new GatedRefreshApplication(new QuotaRefreshResult([Snapshot(91)], []));
        var vm = new MainViewModel(new FixedCardsSource(Snapshot(40)), quotaApplication: application, quotaHistory: history, uiDispatcher: new AvaloniaUiDispatcher());
        var threads = new List<bool>();
        vm.PropertyChanged += (_, _) => threads.Add(Dispatcher.UIThread.CheckAccess());
        vm.Cards.CollectionChanged += (_, _) => threads.Add(Dispatcher.UIThread.CheckAccess());
        vm.History.PropertyChanged += (_, _) => threads.Add(Dispatcher.UIThread.CheckAccess());
        vm.Notify("threshold", "notice");

        var refresh = vm.RefreshAsync(RefreshOrigin.Scheduled);
        await application.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsQuotaDataBusy);
        application.Release.TrySetResult(true);
        await refresh.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotEmpty(vm.Cards);
        Assert.NotEmpty(threads);
        Assert.All(threads, Assert.True);
        vm.Dispose();
    }

    private sealed class FixedCardsSource(QuotaSnapshot snapshot) : IDashboardSource { public bool IsDemo => false; public IReadOnlyList<ProviderCardViewModel> Load() => DashboardAggregation.ToCards([snapshot], DateTimeOffset.UtcNow); }

    private sealed class GatedRefreshApplication(QuotaRefreshResult result) : IQuotaApplication
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class RecordingProjectionScheduler : IHistoryProjectionScheduler
    {
        private volatile bool hold;
        private volatile bool allOffUi = true;
        private volatile bool ran;
        private TaskCompletionSource<bool> release = NewTcs();
        public TaskCompletionSource<bool> HeldStarted { get; private set; } = NewTcs();
        public bool AllBuildsOffUi => allOffUi;
        public bool RanBuilds => ran;
        public void HoldNext()
        {
            HeldStarted = NewTcs();
            release = NewTcs();
            hold = true;
        }
        public void Release()
        {
            hold = false;
            release.TrySetResult(true);
        }
        public async Task<T> ScheduleAsync<T>(Func<T> work)
        {
            var wasHeld = hold;
            if (wasHeld)
            {
                HeldStarted.TrySetResult(true);
                await release.Task;
            }
            return await Task.Run(() =>
            {
                ran = true;
                if (Dispatcher.UIThread.CheckAccess()) allOffUi = false;
                return work();
            });
        }
        private static TaskCompletionSource<bool> NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

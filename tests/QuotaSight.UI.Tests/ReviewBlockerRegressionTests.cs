using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuotaSight.Application;
using QuotaSight.Core;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class ReviewBlockerRegressionTests
{
    private static QuotaSnapshot Snapshot(int value) => ConcurrencyFixtures.Snapshot(value);

    // B1: a stale publish action released after a newer query has been published must not
    // overwrite the newer published snapshot or raise notifications for the stale state.
    [Fact]
    public async Task Stale_publish_released_after_newer_query_cannot_overwrite_published_state()
    {
        var history = new InMemoryQuotaHistory(Enumerable.Range(0, 9).Select(Snapshot).ToList());
        var dispatcher = new HoldNextPublishDispatcher();
        var state = new HistoryState(history, dispatcher);
        await state.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));

        dispatcher.HoldNext();
        state.AccountFilter = "account-1";
        await dispatcher.Held.Task.WaitAsync(TimeSpan.FromSeconds(5));

        state.AccountFilter = "account-2";
        await state.WaitForPublishedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(state.FilteredEntries);
        Assert.All(state.FilteredEntries, entry => Assert.Equal("account-2", entry.Account));

        var notificationsAfterCommit = 0;
        state.PropertyChanged += (_, _) => notificationsAfterCommit++;
        dispatcher.Release();
        await dispatcher.HeldActionExecuted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(state.FilteredEntries, entry => Assert.Equal("account-2", entry.Account));
        Assert.Equal(0, notificationsAfterCommit);
        await state.WaitForPublishedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(state.FilteredEntries, entry => Assert.Equal("account-2", entry.Account));
        state.Dispose();
    }

    // B1: an export accepted at a data revision must serialize exactly that revision's rows
    // even when the revision's own projection never publishes because a later revision
    // (here DeleteAll) supersedes it and publishes first.
    [Fact]
    public async Task Export_serializes_the_exact_accepted_data_revision_when_a_later_revision_publishes_first()
    {
        var history = new InMemoryQuotaHistory([]);
        var scheduler = new ConcurrencyContractTests.BlockingBuildScheduler(blockedCall: 2);
        var state = new HistoryState(history, new ImmediateUiDispatcher(), scheduler);
        await state.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var append = state.AppendAndReloadAsync([Snapshot(1) with { Account = "accepted-row" }]);
        await scheduler.BlockedBuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var jsonTask = state.ExportJsonAsync();
        var csvTask = state.ExportCsvAsync();
        var deleteAll = state.DeleteAllAsync();

        await deleteAll.WaitAsync(TimeSpan.FromSeconds(5));
        var json = await jsonTask.WaitAsync(TimeSpan.FromSeconds(5));
        var csv = await csvTask.WaitAsync(TimeSpan.FromSeconds(5));
        await append.WaitAsync(TimeSpan.FromSeconds(5));

        using (var document = JsonDocument.Parse(json))
        {
            var entry = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("accepted-row", entry.GetProperty("account").GetString());
        }
        var rows = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, rows.Length);
        Assert.Contains("accepted-row", csv, StringComparison.Ordinal);

        scheduler.ReleaseBlockedBuild.TrySetResult(true);
        await scheduler.BlockedBuildCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.WaitForPublishedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(state.Entries);
        Assert.Empty(state.FilteredEntries);
        state.Dispose();
    }

    // B1: an export accepted while its target revision's storage is still running must still
    // serialize that revision's rows once observed, not the later revision that publishes first.
    [Fact]
    public async Task Export_accepted_before_storage_completes_serializes_its_own_revision_not_a_later_one()
    {
        var history = new GatedAppendHistory([]);
        var scheduler = new ConcurrencyContractTests.BlockingBuildScheduler(blockedCall: 2);
        var state = new HistoryState(history, new ImmediateUiDispatcher(), scheduler);
        await state.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var append = state.AppendAndReloadAsync([Snapshot(1) with { Account = "accepted-row" }]);
        await history.AppendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var jsonTask = state.ExportJsonAsync();
        var deleteAll = state.DeleteAllAsync();
        history.ReleaseAppend.TrySetResult(true);

        await deleteAll.WaitAsync(TimeSpan.FromSeconds(5));
        var json = await jsonTask.WaitAsync(TimeSpan.FromSeconds(5));
        await append.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("accepted-row", json, StringComparison.Ordinal);

        scheduler.ReleaseBlockedBuild.TrySetResult(true);
        await scheduler.BlockedBuildCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.WaitForPublishedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(state.Entries);
        state.Dispose();
    }

    // B2: a spontaneously canceled latest projection must still terminate every accepted
    // request, and the queue must recover on the next command.
    [Fact]
    public async Task Latest_build_cancellation_terminates_pending_requests_and_queue_recovers()
    {
        var state = new HistoryState(new InMemoryQuotaHistory([Snapshot(1)]), new ImmediateUiDispatcher(), new CancelOnSecondProjectionScheduler());
        await state.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));

        state.AccountFilter = "account-1";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.WaitForPublishedAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.ExportCsvAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        state.AccountFilter = "All accounts";
        await state.WaitForPublishedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(state.FilteredEntries);
        state.Dispose();
    }

    // B2: after Dispose has completed, every new request terminates as canceled.
    [Fact]
    public async Task Requests_after_dispose_terminate_canceled()
    {
        var history = new InMemoryQuotaHistory([Snapshot(1)]);
        var state = new HistoryState(history, new ImmediateUiDispatcher());
        await state.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));
        state.Dispose();
        await state.PumpCompletedForTests.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.DeleteAsync(history.Events[0].EventId).WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.AppendAndReloadAsync([Snapshot(2)]).WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.ExportCsvAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.WaitForPublishedAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        state.AccountFilter = "account-1";
        state.NextPage();
    }

    // B3: a mutation that reached storage followed by a failed verifying re-read must not
    // present the stale pre-mutation rows as exportable current data.
    [Theory]
    [InlineData("delete")]
    [InlineData("append")]
    public async Task Post_mutation_read_fault_blocks_exports_until_reload_verifies(string operation)
    {
        var history = new MutationThenReadFaultHistory([Snapshot(1), Snapshot(2)]);
        var state = new HistoryState(history, new ImmediateUiDispatcher());
        await state.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var target = history.Events[0].EventId;

        if (operation == "delete")
            await Assert.ThrowsAsync<InvalidOperationException>(() => state.DeleteAsync(target).WaitAsync(TimeSpan.FromSeconds(5)));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => state.AppendAndReloadAsync([Snapshot(3) with { Account = "appended" }]).WaitAsync(TimeSpan.FromSeconds(5)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => state.ExportCsvAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(state.LoadError);

        await state.RefreshAfterPersistAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var csv = await state.ExportCsvAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (operation == "delete") Assert.DoesNotContain(state.Entries, entry => entry.Id == target);
        else Assert.Contains("appended", csv, StringComparison.Ordinal);
        Assert.Null(state.LoadError);
        state.Dispose();
    }

    // B3: cancellation between the storage mutation and the verifying re-read leaves the
    // state unverified; exports fault until a later reload verifies the snapshot.
    [Fact]
    public async Task Post_mutation_read_cancellation_blocks_exports_until_reload_verifies()
    {
        var history = new MutationThenBlockedReadHistory([Snapshot(1), Snapshot(2)]);
        var state = new HistoryState(history, new ImmediateUiDispatcher());
        await state.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var target = history.Events[0].EventId;

        using var cancellation = new CancellationTokenSource();
        var delete = state.DeleteAsync(target, cancellation.Token);
        await history.BlockedReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delete.WaitAsync(TimeSpan.FromSeconds(5)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => state.ExportCsvAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        await state.RefreshAfterPersistAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(state.Entries, entry => entry.Id == target);
        var csv = await state.ExportCsvAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        state.Dispose();
    }

    // B4: a subscriber disposing HistoryState during PropertyChanged must stop the
    // remaining notification names of that publish.
    [Fact]
    public async Task Dispose_during_property_changed_stops_remaining_notifications()
    {
        var state = new HistoryState(new InMemoryQuotaHistory([Snapshot(1)]), new ImmediateUiDispatcher());
        await state.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var disposedDuringNotification = false;
        var notificationsAfterDispose = 0;
        state.PropertyChanged += (_, _) =>
        {
            if (!disposedDuringNotification)
            {
                disposedDuringNotification = true;
                state.Dispose();
                return;
            }
            notificationsAfterDispose++;
        };
        state.SetLanguage(UiLanguage.Japanese);
        await state.PumpCompletedForTests.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(disposedDuringNotification);
        Assert.Equal(0, notificationsAfterDispose);
    }

    // B4: a scheduled refresh completing after MainViewModel.Dispose must not publish any
    // property or collection change.
    [Fact]
    public async Task Disposed_view_model_publishes_nothing_when_scheduled_refresh_completes()
    {
        var history = new InMemoryQuotaHistory([]);
        var application = new GatedSnapshotApplication(new QuotaRefreshResult([Snapshot(50)], []));
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaHistory: history, quotaApplication: application, uiDispatcher: new ImmediateUiDispatcher());

        var refresh = vm.RefreshAsync(RefreshOrigin.Scheduled);
        await application.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Dispose();

        var propertyChanges = 0;
        var collectionChanges = 0;
        vm.PropertyChanged += (_, _) => propertyChanges++;
        vm.Cards.CollectionChanged += (_, _) => collectionChanges++;
        vm.History.PropertyChanged += (_, _) => propertyChanges++;

        application.Release.TrySetResult(true);
        try { await refresh.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }

        Assert.Equal(0, propertyChanges);
        Assert.Equal(0, collectionChanges);
    }

    // B4: a scheduled refresh failure whose dispatcher action is released only after Dispose
    // must not mutate Cards (no CollectionChanged) or raise any property notification.
    [Fact]
    public async Task Scheduled_refresh_failure_action_released_after_dispose_publishes_nothing()
    {
        var dispatcher = new HoldNextPublishDispatcher();
        var vm = new MainViewModel(new DemoDashboardSource(), quotaApplication: new FailingApplication(), uiDispatcher: dispatcher);
        Assert.NotEmpty(vm.Cards);

        dispatcher.HoldNext();
        var refresh = vm.RefreshAsync(RefreshOrigin.Scheduled);
        await dispatcher.Held.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Dispose();

        var propertyChanges = 0;
        var collectionChanges = 0;
        vm.PropertyChanged += (_, _) => propertyChanges++;
        vm.Cards.CollectionChanged += (_, _) => collectionChanges++;
        vm.History.PropertyChanged += (_, _) => propertyChanges++;

        dispatcher.Release();
        await dispatcher.HeldActionExecuted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, collectionChanges);
        Assert.Equal(0, propertyChanges);
    }

    // B5: notification faults whose message changes on every publish must not turn into an
    // unbounded republish loop; one bounded recovery publish records the LoadError.
    [Fact]
    public async Task Notification_faults_with_changing_messages_do_not_republish_unbounded()
    {
        var state = new HistoryState(new InMemoryQuotaHistory([Snapshot(1)]), new ImmediateUiDispatcher());
        await state.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var faults = 0;
        var throwing = true;
        var thirdFault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.PropertyChanged += (_, e) =>
        {
            if (!throwing || e.PropertyName != nameof(state.DisplayedEntries)) return;
            var fault = ++faults;
            if (fault == 3) thirdFault.TrySetResult();
            throw new InvalidOperationException($"subscriber failure {fault}");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => state.RefreshAfterPersistAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<TimeoutException>(() => thirdFault.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(2, faults);

        throwing = false;
        await state.AppendAndReloadAsync([Snapshot(2) with { Account = "after-bounded-fault" }]).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(state.Entries, entry => entry.Account == "after-bounded-fault");
        Assert.Null(state.LoadError);
        state.Dispose();
    }

    // B6: provider refresh results are persisted exactly once through the HistoryState
    // storage lane, not by the provider application writing storage directly.
    [Fact]
    public async Task Provider_refresh_persists_snapshots_once_through_history_state()
    {
        var history = new InMemoryQuotaHistory([]);
        var snapshot = Snapshot(60) with { Account = "provider-owned" };
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaHistory: history, quotaApplication: new SingleResultApplication(new QuotaRefreshResult([snapshot], [])), uiDispatcher: new ImmediateUiDispatcher());

        await vm.RefreshAsync();

        Assert.Single(history.Events, entry => entry.Snapshot.Account == "provider-owned");
        Assert.Contains(vm.History.Entries, entry => entry.Account == "provider-owned");
        vm.Dispose();
    }

    [Fact]
    public async Task No_data_is_not_a_refresh_failure_and_keeps_last_success()
    {
        var successful = Snapshot(42) with { Provider = ProviderKind.Copilot, Account = "org/user" };
        var application = new MutableResultApplication(new QuotaRefreshResult([successful], []));
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: application, uiDispatcher: new ImmediateUiDispatcher());

        await vm.RefreshAsync();
        application.Result = new QuotaRefreshResult([], [], new HashSet<ProviderKind> { ProviderKind.Copilot });
        await vm.RefreshAsync();

        Assert.Equal(PresentationState.Ready, vm.PresentationState);
        Assert.False(vm.IsError);
        Assert.Single(vm.Cards);
        Assert.Contains("no data", vm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("last", vm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);
        vm.Dispose();
    }

    [Fact]
    public async Task Organization_and_user_switch_replaces_only_active_automatic_copilot_card()
    {
        var old = Snapshot(20) with { Provider = ProviderKind.Copilot, Account = "org-a/user-a" };
        var other = Snapshot(30) with { Provider = ProviderKind.OpenCode, Account = "other" };
        var application = new MutableResultApplication(new QuotaRefreshResult([old, other], []));
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: application, uiDispatcher: new ImmediateUiDispatcher());

        await vm.RefreshAsync();
        application.Result = new QuotaRefreshResult(
            [old with { Account = "org-b/user-b", Used = 40 }, other with { Used = 35 }],
            [],
            activeAccounts: new Dictionary<ProviderKind, string> { [ProviderKind.Copilot] = "org-b/user-b" });
        await vm.RefreshAsync();

        Assert.DoesNotContain(vm.Cards, card => card.Provider == ProviderKind.Copilot && card.Account == "org-a/user-a");
        Assert.Contains(vm.Cards, card => card.Provider == ProviderKind.Copilot && card.Account == "org-b/user-b");
        Assert.Contains(vm.Cards, card => card.Provider == ProviderKind.OpenCode && card.Account == "other");
        Assert.Equal(PresentationState.Ready, vm.PresentationState);
        Assert.False(vm.IsError);
        Assert.False(vm.IsNotificationVisible);
        vm.Dispose();
    }

    // B8: page-shrinking mutations must never let a HasPreviousPage notification observe a
    // stale previous-page value.
    [Fact]
    public async Task Page_shrink_notifications_never_observe_stale_previous_page()
    {
        var history = new InMemoryQuotaHistory(Enumerable.Range(0, 150).Select(index => Snapshot(index) with { Account = "acct" }).ToList());
        var state = new HistoryState(history, new ImmediateUiDispatcher());
        await state.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));

        state.NextPage();
        state.NextPage();
        await state.WaitForPublishedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, state.CurrentPage);
        Assert.True(state.HasPreviousPage);

        var observed = new List<bool>();
        state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(state.HasPreviousPage)) observed.Add(state.HasPreviousPage); };

        await state.DeleteAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await state.WaitForPublishedAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, state.CurrentPage);
        Assert.Equal(0, state.RequestedPage);
        Assert.False(state.HasPreviousPage);
        Assert.NotEmpty(observed);
        Assert.All(observed, value => Assert.False(value));
        state.Dispose();
    }

    // B9: JSON export must escape every control character and round-trip through a
    // standards-compliant parser.
    [Fact]
    public async Task Json_export_escapes_control_characters_and_round_trips()
    {
        const string account = "tab\tback\bform\fquote\"slash\\solidus/cr\rlf\ncontrolend";
        var history = new InMemoryQuotaHistory([Snapshot(1) with { Account = account }]);
        var state = new HistoryState(history, new ImmediateUiDispatcher());
        await state.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var json = await state.ExportJsonAsync().WaitAsync(TimeSpan.FromSeconds(5));

        using var document = JsonDocument.Parse(json);
        var entry = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(account, entry.GetProperty("account").GetString());
        state.Dispose();
    }

    internal sealed class HoldNextPublishDispatcher : IUiDispatcher
    {
        private TaskCompletionSource<bool> release = NewTcs();
        private int holding;
        public TaskCompletionSource<bool> Held { get; private set; } = NewTcs();
        public TaskCompletionSource<bool> HeldActionExecuted { get; private set; } = NewTcs();
        public void HoldNext()
        {
            Held = NewTcs();
            HeldActionExecuted = NewTcs();
            release = NewTcs();
            Interlocked.Exchange(ref holding, 1);
        }
        public void Release() => release.TrySetResult(true);
        public bool CheckAccess() => true;
        public async Task InvokeAsync(Action action)
        {
            if (Interlocked.Exchange(ref holding, 0) == 1)
            {
                Held.TrySetResult(true);
                await release.Task;
                action();
                HeldActionExecuted.TrySetResult(true);
                return;
            }
            action();
        }
        public Task InvokeAsync(Func<Task> action) => action();
        private static TaskCompletionSource<bool> NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CancelOnSecondProjectionScheduler : IHistoryProjectionScheduler
    {
        private int calls;
        public Task<T> ScheduleAsync<T>(Func<T> work) => Interlocked.Increment(ref calls) == 2
            ? Task.FromCanceled<T>(new CancellationToken(true))
            : Task.FromResult(work());
    }

    internal sealed class GatedAppendHistory(IEnumerable<QuotaSnapshot> snapshots) : InMemoryQuotaHistory(snapshots)
    {
        public TaskCompletionSource<bool> AppendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseAppend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask AppendAsync(IReadOnlyList<QuotaSnapshot> values, CancellationToken token)
        {
            AppendStarted.TrySetResult(true);
            await ReleaseAppend.Task.WaitAsync(token);
            await base.AppendAsync(values, token);
        }
    }

    internal sealed class MutationThenReadFaultHistory(IEnumerable<QuotaSnapshot> snapshots) : InMemoryQuotaHistory(snapshots)
    {
        private bool faultNextRead;
        public override async ValueTask AppendAsync(IReadOnlyList<QuotaSnapshot> values, CancellationToken token)
        {
            await base.AppendAsync(values, token);
            faultNextRead = true;
        }
        public override async ValueTask DeleteEventAsync(Guid id, CancellationToken token)
        {
            await base.DeleteEventAsync(id, token);
            faultNextRead = true;
        }
        public override ValueTask<IReadOnlyList<QuotaHistoryEntry>> ReadEventsAsync(DateOnly day, CancellationToken token)
        {
            if (faultNextRead)
            {
                faultNextRead = false;
                return ValueTask.FromException<IReadOnlyList<QuotaHistoryEntry>>(new InvalidOperationException("verifying re-read failed"));
            }
            return base.ReadEventsAsync(day, token);
        }
    }

    internal sealed class MutationThenBlockedReadHistory(IEnumerable<QuotaSnapshot> snapshots) : InMemoryQuotaHistory(snapshots)
    {
        private bool blockNextRead;
        public TaskCompletionSource BlockedReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask DeleteEventAsync(Guid id, CancellationToken token)
        {
            await base.DeleteEventAsync(id, token);
            blockNextRead = true;
        }
        public override async ValueTask<IReadOnlyList<QuotaHistoryEntry>> ReadEventsAsync(DateOnly day, CancellationToken token)
        {
            if (blockNextRead)
            {
                blockNextRead = false;
                BlockedReadStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return await base.ReadEventsAsync(day, token);
        }
    }

    internal sealed class GatedSnapshotApplication(QuotaRefreshResult result) : IQuotaApplication
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

    internal sealed class SingleResultApplication(QuotaRefreshResult result) : IQuotaApplication
    {
        public ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken) => ValueTask.FromResult(result);
    }

    internal sealed class MutableResultApplication(QuotaRefreshResult result) : IQuotaApplication
    {
        public QuotaRefreshResult Result { get; set; } = result;
        public ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result);
    }

    internal sealed class FailingApplication : IQuotaApplication
    {
        public ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken) => ValueTask.FromException<QuotaRefreshResult>(new InvalidOperationException("refresh failed"));
    }
}

public sealed class ReviewBlockerAvaloniaRegressionTests
{
    private static QuotaSnapshot Snapshot(int value) => ConcurrencyFixtures.Snapshot(value);

    // B6: a manual refresh must run the provider fetch (including its synchronous prefix)
    // off the UI thread while busy state and applied results stay on the UI thread.
    [AvaloniaFact]
    public async Task Manual_refresh_runs_provider_fetch_off_ui_and_applies_on_ui()
    {
        var history = new InMemoryQuotaHistory([]);
        var application = new ThreadRecordingApplication(new QuotaRefreshResult([Snapshot(45)], []));
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaHistory: history, quotaApplication: application, uiDispatcher: new AvaloniaUiDispatcher());
        var notificationThreads = new List<bool>();
        vm.PropertyChanged += (_, _) => notificationThreads.Add(Dispatcher.UIThread.CheckAccess());
        vm.Cards.CollectionChanged += (_, _) => notificationThreads.Add(Dispatcher.UIThread.CheckAccess());

        var refresh = vm.RefreshAsync(RefreshOrigin.Manual);
        Assert.True(vm.IsQuotaDataBusy);
        await refresh.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(application.SynchronousPrefixOffUi);
        Assert.False(vm.IsQuotaDataBusy);
        Assert.NotEmpty(vm.Cards);
        Assert.NotEmpty(notificationThreads);
        Assert.All(notificationThreads, Assert.True);
        vm.Dispose();
    }

    // B6: initialization runs prune and the 30-day history read/aggregation off the UI
    // thread while UI projections and notifications stay on the UI thread.
    [AvaloniaFact]
    public async Task Initialization_runs_prune_and_history_reads_off_ui()
    {
        var history = new ThreadRecordingHistory(Snapshot(70));
        var vm = new MainViewModel(new EmptyDashboardSource(), quotaHistory: history, uiDispatcher: new AvaloniaUiDispatcher());
        var notificationThreads = new List<bool>();
        vm.PropertyChanged += (_, _) => notificationThreads.Add(Dispatcher.UIThread.CheckAccess());
        vm.Cards.CollectionChanged += (_, _) => notificationThreads.Add(Dispatcher.UIThread.CheckAccess());

        var initialization = vm.InitializeAsync();
        Assert.True(vm.IsQuotaDataBusy);
        await initialization.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(history.PruneOffUi);
        Assert.True(history.ReadOffUi);
        Assert.False(vm.IsQuotaDataBusy);
        Assert.NotEmpty(vm.Cards);
        Assert.NotEmpty(notificationThreads);
        Assert.All(notificationThreads, Assert.True);
        vm.Dispose();
    }

    // B7: app exit must complete while an OS export picker is unresolved; a late picker
    // result must cause no writes and no notifications after cleanup.
    [AvaloniaFact]
    public async Task App_exit_completes_while_export_picker_is_unresolved_and_late_result_is_ignored()
    {
        var destination = new UnresolvedPickerDestination();
        var vm = new MainViewModel(new EmptyDashboardSource());
        vm.Navigate(AppPage.History);
        var window = new MainWindow(vm, new UiProviderFacade(), null, destination);
        var coordinator = new AppLifecycleCoordinator(() => { vm.Dispose(); return ValueTask.CompletedTask; });
        window.OperationRunner = coordinator.Run;
        window.Show();
        try
        {
            window.FindControl<WrapPanel>("HistoryActions")!.GetVisualDescendants().OfType<Button>()
                .Single(button => Avalonia.Automation.AutomationProperties.GetName(button) == "Export history CSV")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await destination.PickRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await coordinator.BeginExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(coordinator.IsCleanupCompleted);

            var bannerBefore = vm.NotificationBannerText;
            destination.PickResult.TrySetResult(destination.File);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            Assert.Equal(0, destination.File.OpenCount);
            Assert.Equal(bannerBefore, vm.NotificationBannerText);
        }
        finally { window.Close(); }
    }

    private sealed class ThreadRecordingApplication(QuotaRefreshResult result) : IQuotaApplication
    {
        public bool SynchronousPrefixOffUi { get; private set; }
        public async ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken)
        {
            SynchronousPrefixOffUi = !Dispatcher.UIThread.CheckAccess();
            await Task.Yield();
            return result;
        }
    }

    private sealed class ThreadRecordingHistory(QuotaSnapshot snapshot) : IQuotaHistory
    {
        public bool PruneOffUi { get; private set; }
        public bool ReadOffUi { get; private set; }
        public ValueTask AppendAsync(IReadOnlyList<QuotaSnapshot> snapshots, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<QuotaSnapshot>> ReadAsync(DateOnly day, CancellationToken cancellationToken)
        {
            ReadOffUi = !Dispatcher.UIThread.CheckAccess();
            return ValueTask.FromResult<IReadOnlyList<QuotaSnapshot>>(day == DateOnly.FromDateTime(DateTime.UtcNow) ? [snapshot] : []);
        }
        public ValueTask<IReadOnlyList<QuotaHistoryEntry>> ReadEventsAsync(DateOnly day, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<QuotaHistoryEntry>>(day == DateOnly.FromDateTime(DateTime.UtcNow) ? [new QuotaHistoryEntry(Guid.NewGuid(), snapshot)] : []);
        public ValueTask DeleteEventAsync(Guid eventId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask PruneAsync(DateOnly before, CancellationToken cancellationToken)
        {
            PruneOffUi = !Dispatcher.UIThread.CheckAccess();
            return ValueTask.CompletedTask;
        }
        public ValueTask DeleteAsync(DateOnly? day, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ExportJsonAsync(Stream output, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ExportCsvAsync(Stream output, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class UnresolvedPickerDestination : IHistoryExportDestination
    {
        public TaskCompletionSource PickRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IHistoryExportFile?> PickResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RecordingExportFile File { get; } = new();
        public Task<IHistoryExportFile?> PickAsync(string suggestedName, string contentType)
        {
            PickRequested.TrySetResult();
            return PickResult.Task;
        }

        public sealed class RecordingExportFile : IHistoryExportFile
        {
            private int openCount;
            public int OpenCount => Volatile.Read(ref openCount);
            public Task<Stream> OpenWriteAsync()
            {
                Interlocked.Increment(ref openCount);
                return Task.FromResult<Stream>(new MemoryStream());
            }
        }
    }
}

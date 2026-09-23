using QuotaSight.Application;
using QuotaSight.Core;
using Xunit;

namespace QuotaSight.Tests;

public sealed class NotificationDeduplicatorWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartChangesWithinSameResetCycleNotifyOnceAndNewCycleNotifiesAgain()
    {
        var sink = new RecordingNotificationSink();
        var deduplicator = new NotificationDeduplicator(sink);
        var resetAt = Now.AddDays(7);

        await deduplicator.ConsiderAsync(Snapshot(start: Now.AddHours(-1), fetched: Now, percent: 82, resetAt: resetAt), 80, Now, CancellationToken.None);
        await deduplicator.ConsiderAsync(Snapshot(start: Now, fetched: Now.AddMinutes(10), percent: 85, resetAt: resetAt), 80, Now, CancellationToken.None);
        await deduplicator.ConsiderAsync(Snapshot(start: Now.AddMinutes(10), fetched: Now.AddMinutes(20), percent: 91, resetAt: resetAt), 80, Now, CancellationToken.None);

        Assert.Single(sink.Snapshots);

        var nextResetAt = resetAt.AddDays(7);
        await deduplicator.ConsiderAsync(Snapshot(start: resetAt, fetched: resetAt, percent: 82, resetAt: nextResetAt), 80, resetAt, CancellationToken.None);

        Assert.Equal(2, sink.Snapshots.Count);
    }

    [Fact]
    public async Task Sliding_reset_boundary_within_current_cycle_does_not_notify_again()
    {
        var sink = new RecordingNotificationSink();
        var deduplicator = new NotificationDeduplicator(sink);
        var resetAt = Now.AddHours(1);

        await deduplicator.ConsiderAsync(Snapshot(Now, Now, 85, resetAt), 80, Now, CancellationToken.None);
        await deduplicator.ConsiderAsync(Snapshot(Now.AddMinutes(10), Now.AddMinutes(10), 86, resetAt.AddMinutes(30)), 80, Now.AddMinutes(10), CancellationToken.None);
        await deduplicator.ConsiderAsync(Snapshot(Now.AddMinutes(20), Now.AddMinutes(20), 87, resetAt.AddMinutes(60)), 80, Now.AddMinutes(20), CancellationToken.None);

        Assert.Single(sink.Snapshots);
    }

    [Fact]
    public async Task EndIsCycleFallbackAndIdentityKeepsProviderAccountMetricAndWindowKindDistinct()
    {
        var sink = new RecordingNotificationSink();
        var deduplicator = new NotificationDeduplicator(sink);
        var end = Now.AddDays(1);

        await deduplicator.ConsiderAsync(Snapshot(ProviderKind.OpenCode, "account-a", "requests", QuotaWindowKind.Daily, Now.AddMinutes(-1), Now, 81, end), 80, Now, CancellationToken.None);
        await deduplicator.ConsiderAsync(Snapshot(ProviderKind.OpenCode, "account-a", "requests", QuotaWindowKind.Daily, Now, Now.AddMinutes(10), 89, end), 80, Now, CancellationToken.None);
        await deduplicator.ConsiderAsync(Snapshot(ProviderKind.Copilot, "account-a", "requests", QuotaWindowKind.Daily, Now, Now, 81, end), 80, Now, CancellationToken.None);
        await deduplicator.ConsiderAsync(Snapshot(ProviderKind.OpenCode, "account-b", "requests", QuotaWindowKind.Daily, Now, Now, 81, end), 80, Now, CancellationToken.None);
        await deduplicator.ConsiderAsync(Snapshot(ProviderKind.OpenCode, "account-a", "credits", QuotaWindowKind.Daily, Now, Now, 81, end), 80, Now, CancellationToken.None);
        await deduplicator.ConsiderAsync(Snapshot(ProviderKind.OpenCode, "account-a", "requests", QuotaWindowKind.Weekly, Now, Now, 81, end), 80, Now, CancellationToken.None);
        await deduplicator.ConsiderAsync(Snapshot(ProviderKind.OpenCode, "account-a", "requests", QuotaWindowKind.Daily, end, end, 81, end.AddDays(1)), 80, end, CancellationToken.None);

        Assert.Equal(6, sink.Snapshots.Count);
    }

    [Fact]
    public async Task FallingBelowThenCrossingThresholdCanNotifyAgainWithinSameCycle()
    {
        var sink = new RecordingNotificationSink();
        var deduplicator = new NotificationDeduplicator(sink);
        var resetAt = Now.AddDays(7);

        await deduplicator.ConsiderAsync(Snapshot(start: Now.AddHours(-1), fetched: Now, percent: 85, resetAt: resetAt), 80, Now, CancellationToken.None);
        await deduplicator.ConsiderAsync(Snapshot(start: Now, fetched: Now.AddMinutes(10), percent: 75, resetAt: resetAt), 80, Now, CancellationToken.None);
        await deduplicator.ConsiderAsync(Snapshot(start: Now.AddMinutes(10), fetched: Now.AddMinutes(20), percent: 85, resetAt: resetAt), 80, Now, CancellationToken.None);

        Assert.Equal(2, sink.Snapshots.Count);
    }

    [Fact]
    public async Task Failed_delivery_retries_only_after_five_minutes_even_while_above_threshold()
    {
        var sink = new RecordingNotificationSink(false, true);
        var deduplicator = new NotificationDeduplicator(sink);
        var snapshot = Snapshot(Now, Now, 85, Now.AddDays(7));

        await deduplicator.ConsiderAsync(snapshot, 80, Now, CancellationToken.None);
        await deduplicator.ConsiderAsync(snapshot, 80, Now.AddMinutes(4), CancellationToken.None);
        Assert.Single(sink.Snapshots);
        await deduplicator.ConsiderAsync(snapshot, 80, Now.AddMinutes(5), CancellationToken.None);
        await deduplicator.ConsiderAsync(snapshot, 80, Now.AddMinutes(10), CancellationToken.None);

        Assert.Equal(2, sink.Snapshots.Count);
    }

    [Fact]
    public async Task Accepted_retry_is_not_sent_again_and_below_then_above_resets_cycle_delivery()
    {
        var sink = new RecordingNotificationSink(false, true, true);
        var deduplicator = new NotificationDeduplicator(sink);
        var resetAt = Now.AddDays(7);

        await deduplicator.ConsiderAsync(Snapshot(Now, Now, 85, resetAt), 80, Now, CancellationToken.None);
        await deduplicator.ConsiderAsync(Snapshot(Now, Now, 85, resetAt), 80, Now.AddMinutes(5), CancellationToken.None);
        await deduplicator.ConsiderAsync(Snapshot(Now, Now, 85, resetAt), 80, Now.AddMinutes(10), CancellationToken.None);
        await deduplicator.ConsiderAsync(Snapshot(Now, Now, 70, resetAt), 80, Now.AddMinutes(11), CancellationToken.None);
        await deduplicator.ConsiderAsync(Snapshot(Now, Now, 85, resetAt), 80, Now.AddMinutes(12), CancellationToken.None);
        await deduplicator.ConsiderAsync(Snapshot(resetAt, resetAt, 85, resetAt.AddDays(7)), 80, resetAt, CancellationToken.None);

        Assert.Equal(4, sink.Snapshots.Count);
    }

    [Fact]
    public async Task Thrown_failure_can_retry_and_cancellation_does_not_record_delivery()
    {
        var sink = new RecordingNotificationSink { Throw = new InvalidOperationException("backend unavailable") };
        var deduplicator = new NotificationDeduplicator(sink);
        var snapshot = Snapshot(Now, Now, 85, Now.AddDays(7));

        await Assert.ThrowsAsync<InvalidOperationException>(() => deduplicator.ConsiderAsync(snapshot, 80, Now, CancellationToken.None).AsTask());
        sink.Throw = null;
        await deduplicator.ConsiderAsync(snapshot, 80, Now.AddMinutes(1), CancellationToken.None);
        Assert.Single(sink.Snapshots);

        var secondSink = new RecordingNotificationSink();
        var secondDeduplicator = new NotificationDeduplicator(secondSink);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondDeduplicator.ConsiderAsync(snapshot, 80, Now, cancellation.Token).AsTask());
        await secondDeduplicator.ConsiderAsync(snapshot, 80, Now.AddMinutes(1), CancellationToken.None);
        Assert.Single(secondSink.Snapshots);
    }

    [Fact]
    public async Task Disabled_tracking_records_threshold_drop_so_reenabled_crossing_notifies()
    {
        var sink = new RecordingNotificationSink();
        var deduplicator = new NotificationDeduplicator(sink);
        var resetAt = Now.AddDays(7);

        await deduplicator.ConsiderAsync(Snapshot(Now, Now, 85, resetAt), 80, Now, CancellationToken.None, notificationsEnabled: false);
        await deduplicator.ConsiderAsync(Snapshot(Now.AddMinutes(1), Now.AddMinutes(1), 70, resetAt), 80, Now.AddMinutes(1), CancellationToken.None, notificationsEnabled: false);
        await deduplicator.ConsiderAsync(Snapshot(Now.AddMinutes(2), Now.AddMinutes(2), 85, resetAt), 80, Now.AddMinutes(2), CancellationToken.None, notificationsEnabled: true);

        Assert.Single(sink.Snapshots);
    }

    private static QuotaSnapshot Snapshot(
        DateTimeOffset start,
        DateTimeOffset fetched,
        decimal percent,
        DateTimeOffset resetAt) => Snapshot(
            ProviderKind.OpenCode, "account-a", "requests", QuotaWindowKind.Weekly, start, fetched, percent, resetAt, resetAt);

    private static QuotaSnapshot Snapshot(
        ProviderKind provider,
        string account,
        string metric,
        QuotaWindowKind kind,
        DateTimeOffset start,
        DateTimeOffset fetched,
        decimal percent,
        DateTimeOffset end,
        DateTimeOffset? resetAt = null) => new(
            provider,
            account,
            metric,
            new QuotaWindow(kind, start, end, ResetAt: resetAt),
            null,
            null,
            percent,
            "%",
            fetched,
            fetched,
            QuotaSource.Official,
            QuotaConfidence.Official,
            end.AddHours(1));

    private sealed class RecordingNotificationSink(params bool[] results) : INotificationSink
    {
        private int callCount;
        public List<QuotaSnapshot> Snapshots { get; } = [];
        public Exception? Throw { get; set; }

        public ValueTask<bool> NotifyAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Throw is { } exception) return ValueTask.FromException<bool>(exception);
            Snapshots.Add(snapshot);
            return ValueTask.FromResult(results.Length == 0 || results[Math.Min(callCount++, results.Length - 1)]);
        }
    }
}

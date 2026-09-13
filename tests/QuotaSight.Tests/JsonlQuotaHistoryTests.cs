using QuotaSight.Core;
using QuotaSight.Infrastructure;

namespace QuotaSight.Tests;

public sealed class JsonlQuotaHistoryTests
{
    [Fact]
    public void Same_root_instances_are_exclusive_for_their_lifetime()
    {
        var root = CreateRoot();
        try
        {
            using var first = new JsonlQuotaHistory(root);

            Assert.ThrowsAny<IOException>(() => new JsonlQuotaHistory(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Delete_event_skips_corrupt_other_days_and_preserves_remaining_events()
    {
        var root = CreateRoot();
        try
        {
            var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
            var history = new JsonlQuotaHistory(root, new FixedTimeProvider(now));
            var snapshots = new[] { Snapshot(now, "first"), Snapshot(now, "second") };
            await history.AppendAsync(snapshots, default);
            var events = await history.ReadEventsAsync(new DateOnly(2026, 9, 5), default);
            var corruptPath = Path.Combine(root, "2026-09-04.jsonl");
            await File.WriteAllTextAsync(corruptPath, "not-json\n");
            var corruptContents = await File.ReadAllTextAsync(corruptPath);

            await history.DeleteEventAsync(events[0].EventId, default);
            history.Dispose();

            using var reloaded = new JsonlQuotaHistory(root, new FixedTimeProvider(now));
            var remaining = await reloaded.ReadEventsAsync(new DateOnly(2026, 9, 5), default);
            Assert.Single(remaining);
            Assert.Equal("second", remaining[0].Snapshot.Account);
            Assert.Equal(corruptContents, await File.ReadAllTextAsync(corruptPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Delete_all_removes_only_jsonl_files_and_preserves_process_lock()
    {
        var root = CreateRoot();
        try
        {
            var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
            using var history = new JsonlQuotaHistory(root, new FixedTimeProvider(now));
            await history.AppendAsync([Snapshot(now, "first")], default);

            await history.DeleteAsync(null, default);

            Assert.False(File.Exists(Path.Combine(root, "2026-09-05.jsonl")));
            Assert.True(File.Exists(Path.Combine(root, ".history.lock")));
            Assert.ThrowsAny<IOException>(() => new JsonlQuotaHistory(root, new FixedTimeProvider(now)));
            Assert.Empty(await history.ReadEventsAsync(new DateOnly(2026, 9, 5), default));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static string CreateRoot() => Path.Combine(Path.GetTempPath(), "quotasight-tests-" + Guid.NewGuid().ToString("N"));

    private static QuotaSnapshot Snapshot(DateTimeOffset now, string account) =>
        new(ProviderKind.ChatGpt, account, "Messages", new(QuotaWindowKind.Weekly, now.AddDays(-1), now.AddDays(1)), 40, 100, null, "%", now, now, QuotaSource.Manual, QuotaConfidence.Manual, null);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

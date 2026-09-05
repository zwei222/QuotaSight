using QuotaSight.Core;

namespace QuotaSight.Tests;

public sealed class CoreTests
{
    [Fact]
    public void Percentages_preserve_overage_and_unknown_limit()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var bounded = new QuotaSnapshot(ProviderKind.OpenCode, "a", "m", new QuotaWindow(QuotaWindowKind.Rolling, now.AddHours(-1), now.AddHours(1)), 150, 100, null, "unit", now, now, QuotaSource.Official, QuotaConfidence.Official, now.AddMinutes(1));
        Assert.Equal(150, bounded.UsedPercent);
        Assert.Equal(0, bounded.RemainingPercent);
        Assert.Equal(50, bounded.OveragePercent);
        var unknown = bounded with { Limit = null, Used = 10 };
        Assert.Null(unknown.UsedPercent);
        Assert.Null(unknown.RemainingPercent);
    }

    [Fact]
    public void Stale_and_window_selection_are_deterministic()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var fresh = new QuotaWindow(QuotaWindowKind.Daily, now.AddHours(-1), now.AddHours(23));
        var stale = new QuotaSnapshot(ProviderKind.OpenCode, "a", "m", new QuotaWindow(QuotaWindowKind.Monthly, now.AddDays(-30), now.AddDays(-1)), 1, 100, null, "u", now.AddDays(-2), now.AddDays(-2), QuotaSource.Experimental, QuotaConfidence.Estimated, now.AddMinutes(-1));
        Assert.True(stale.IsStale(now));
        var candidates = new[] { stale with { Window = fresh, Used = 80, Limit = 100 }, stale with { Used = 95, Limit = 100, Window = new QuotaWindow(QuotaWindowKind.Weekly, now.AddDays(-1), now.AddDays(6)) } };
        Assert.Equal(QuotaWindowKind.Weekly, QuotaSnapshot.MostConstrained(candidates)!.Window.Kind);
    }
}

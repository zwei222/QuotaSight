using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// Presentation contract of the attributed usage breakdown: the local aggregation of
/// parts, the state machine the usage page shows, and the notes for unclassified,
/// partial and over-attributed coverage. All screens are driven through a scripted
/// read port and a fixed clock; nothing here touches the network.
/// </summary>
public sealed class UsageBreakdownPresentationTests
{
    private static readonly DateOnly From = new(2026, 11, 1);
    private static readonly DateOnly To = new(2026, 11, 7);

    // ---------- local aggregation ----------

    [Fact]
    public void ClientGroupsKeepTheDocumentedOrderAndSumTheirParts()
    {
        var parts = new[]
        {
            Part("claude", "anthropic", "m1", 10, 20, 5, 3, 2),
            Part("claude", "anthropic", "m2", 5, 0, 0, 0, 0),
            Part("codex", "openai", "m3", 1, 0, 0, 0, 0),
        };

        var groups = UsageBreakdownAggregation.GroupParts(parts, UsageBreakdownDimension.Client);

        Assert.Collection(
            groups,
            group =>
            {
                Assert.Equal("claude", group.Key);
                Assert.Equal(45, group.Total);
                Assert.Equal(2, group.Parts.Count);
            },
            group =>
            {
                Assert.Equal("codex", group.Key);
                Assert.Equal(1, group.Total);
            });
    }

    [Fact]
    public void ProviderGroupsKeepCommaJoinedProvidersUnsplit()
    {
        var parts = new[] { Part("claude", "anthropic,google", "m1", 1, 0, 0, 0, 0) };

        var groups = UsageBreakdownAggregation.GroupParts(parts, UsageBreakdownDimension.Provider);

        var group = Assert.Single(groups);
        Assert.Equal("anthropic,google", group.Key);
        Assert.Equal("anthropic,google", group.DisplayName);
    }

    [Fact]
    public void UnknownKeysAreTheirOwnGroupWithTheLocalizedUnknownName()
    {
        var parts = new[] { Part("", "", "", 7, 0, 0, 0, 0) };

        var japanese = UsageBreakdownAggregation.GroupParts(parts, UsageBreakdownDimension.Client, UiLanguage.Japanese);
        Assert.Equal("（不明）", Assert.Single(japanese).DisplayName);

        var english = UsageBreakdownAggregation.GroupParts(parts, UsageBreakdownDimension.Client, UiLanguage.English);
        Assert.Equal("(unknown)", Assert.Single(english).DisplayName);
    }

    [Fact]
    public void KnownClientIdsGetTheirDisplayNameAndUnknownIdsAreShownAsIs()
    {
        var parts = new[] { Part("claude", "", "", 1, 0, 0, 0, 0), Part("not-a-known-client", "", "", 2, 0, 0, 0, 0) };

        var groups = UsageBreakdownAggregation.GroupParts(parts, UsageBreakdownDimension.Client);

        Assert.Equal("Claude Code", groups[0].DisplayName);
        Assert.Equal("not-a-known-client", groups[1].DisplayName);
    }

    [Fact]
    public void UnclassifiedTokensAreTheSumOfThePositiveRemainders()
    {
        var coverage = new[]
        {
            new UsageCoverage(new DateOnly(2026, 11, 1), "col-a", 100, 70),
            new UsageCoverage(new DateOnly(2026, 11, 2), "col-a", 50, 50),
            new UsageCoverage(new DateOnly(2026, 11, 3), "col-b", 20, 25), // over-attributed: never negative
        };

        Assert.Equal(30, UsageBreakdownAggregation.UnclassifiedTokens(coverage));
    }

    [Fact]
    public void OverAttributedRowsAreListedExactlyWithoutRounding()
    {
        var coverage = new[]
        {
            new UsageCoverage(new DateOnly(2026, 11, 1), "col-a", 100, 70),
            new UsageCoverage(new DateOnly(2026, 11, 2), "col-b", 20, 25),
        };

        var over = UsageBreakdownAggregation.OverAttributedRows(coverage);

        var row = Assert.Single(over);
        Assert.Equal(new DateOnly(2026, 11, 2), row.Date);
        Assert.Equal(25, row.AttributedTokens);
        Assert.Equal(20, row.TotalTokens);
    }

    [Fact]
    public void PartialAttributionMeansSomeUsageDaysHaveNoBreakdownAndSomeDo()
    {
        var partial = new[]
        {
            new UsageCoverage(new DateOnly(2026, 11, 1), "col-a", 100, 100),
            new UsageCoverage(new DateOnly(2026, 11, 2), "col-a", 50, 0),
        };
        var allAttributed = new[] { new UsageCoverage(new DateOnly(2026, 11, 1), "col-a", 100, 100) };
        var noneAttributed = new[] { new UsageCoverage(new DateOnly(2026, 11, 1), "col-a", 100, 0) };

        Assert.True(UsageBreakdownAggregation.IsPartiallyAttributed(partial));
        Assert.False(UsageBreakdownAggregation.IsPartiallyAttributed(allAttributed));
        Assert.False(UsageBreakdownAggregation.IsPartiallyAttributed(noneAttributed));
        Assert.False(UsageBreakdownAggregation.IsPartiallyAttributed([]));
    }

    // ---------- usage page state ----------

    [AvaloniaFact]
    public async Task UnconfiguredUsageHidesTheBreakdownSurface()
    {
        using var viewModel = new MainViewModel(new EmptyDashboardSource());
        viewModel.NavigateToUsage();
        await viewModel.UsageLoadTask;
        await viewModel.BreakdownLoadTask;

        Assert.Equal("Unconfigured", viewModel.BreakdownState);
        Assert.False(viewModel.IsBreakdownSurfaceConfigured);
        Assert.False(viewModel.IsBreakdownNoteVisible);
    }

    [AvaloniaFact]
    public async Task ALoadedBreakdownShowsRowsAndTheFixedObservationNote()
    {
        var breakdown = Breakdown(
            [Part("claude", "anthropic", "m1", 10, 20, 5, 3, 2)],
            [Cov(40, 40)],
            [Col("col-a", 1, "2026-11-02T00:00:00.000Z")]);
        using var viewModel = Create(breakdown);
        viewModel.NavigateToUsage();
        await viewModel.UsageLoadTask;
        await viewModel.BreakdownLoadTask;

        Assert.Equal("Ready", viewModel.BreakdownState);
        Assert.True(viewModel.IsBreakdownNoteVisible);
        Assert.Contains("TokScale", viewModel.BreakdownNoteText, StringComparison.Ordinal);
        Assert.True(viewModel.IsBreakdownRowsVisible);
        var row = Assert.Single(viewModel.BreakdownRows);
        Assert.Equal("Claude Code", row.DisplayName);
        Assert.Equal(40, row.Total);
        Assert.Equal("100", row.ShareText);
    }

    [AvaloniaFact]
    public async Task SwitchingDimensionRegroupsRowsWithoutAnotherRead()
    {
        var breakdown = Breakdown(
            [
                Part("claude", "anthropic", "m1", 10, 20, 5, 3, 2),
                Part("claude", "google", "m1", 1, 0, 0, 0, 0),
            ],
            [Cov(41, 41)],
            [Col("col-a", 1, null)]);
        using var viewModel = Create(breakdown);
        viewModel.NavigateToUsage();
        await viewModel.UsageLoadTask;
        await viewModel.BreakdownLoadTask;

        Assert.Single(viewModel.BreakdownRows);

        viewModel.SelectedBreakdownDimension = UsageBreakdownDimension.Provider;

        Assert.Equal(2, viewModel.BreakdownRows.Count);
        Assert.Equal("anthropic", viewModel.BreakdownRows[0].DisplayName);
        Assert.Equal("google", viewModel.BreakdownRows[1].DisplayName);
    }

    [AvaloniaFact]
    public async Task ServerWithoutBreakdownSupportIsANamedState()
    {
        using var viewModel = Create(UsageReadFailure.NotFound);
        viewModel.NavigateToUsage();
        await viewModel.UsageLoadTask;
        await viewModel.BreakdownLoadTask;

        Assert.Equal("Unsupported", viewModel.BreakdownState);
        Assert.Equal(
            new UiCopy(UiLanguage.English).UsageBreakdownState("Unsupported"),
            viewModel.UsageBreakdownStateText);
        Assert.False(viewModel.IsBreakdownRowsVisible);
    }

    [AvaloniaFact]
    public async Task AnUnknownCollectorIsADistinctFailureWithGuidance()
    {
        using var viewModel = Create(UsageReadFailure.CollectorNotFound);
        viewModel.NavigateToUsage();
        await viewModel.UsageLoadTask;
        await viewModel.BreakdownLoadTask;

        Assert.Equal("Error", viewModel.BreakdownState);
        Assert.Equal(UsageFailureKind.CollectorNotFound, viewModel.BreakdownFailure);
        Assert.Contains("collector", viewModel.BreakdownFailureText, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task AnEmptyBreakdownStatesThatThereIsNothingAttributed()
    {
        var breakdown = Breakdown([], [Cov(0, 0)], [Col("col-a", 1, null)]);
        using var viewModel = Create(breakdown);
        viewModel.NavigateToUsage();
        await viewModel.UsageLoadTask;
        await viewModel.BreakdownLoadTask;

        Assert.Equal("Empty", viewModel.BreakdownState);
        Assert.False(viewModel.IsBreakdownRowsVisible);
    }

    [AvaloniaFact]
    public async Task PartialAttributionAndUnclassifiedAndOverAttributionGetTheirOwnNotes()
    {
        var breakdown = Breakdown(
            [
                Part("claude", "anthropic", "m1", 10, 20, 5, 3, 2),
                Part("codex", "openai", "m2", 0, 5, 0, 0, 0),
            ],
            [Cov(40, 40), Cov(50, 55, "2026-11-02"), Cov(30, 0, "2026-11-03")],
            [Col("col-a", 1, null)]);
        using var viewModel = Create(breakdown);
        viewModel.NavigateToUsage();
        await viewModel.UsageLoadTask;
        await viewModel.BreakdownLoadTask;

        Assert.Equal("Ready", viewModel.BreakdownState);
        Assert.True(viewModel.IsBreakdownPartialVisible);
        Assert.True(viewModel.IsBreakdownUnclassifiedVisible);
        Assert.Equal(30, viewModel.BreakdownUnclassifiedTokens);
        Assert.True(viewModel.IsBreakdownOverAttributedVisible);
        Assert.Contains("55", viewModel.BreakdownOverAttributionText, StringComparison.Ordinal);
        Assert.Contains("50", viewModel.BreakdownOverAttributionText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task ADailyFailureDoesNotStaleTheBreakdownAndViceVersa()
    {
        var port = new ScriptedDualReadPort();
        using var viewModel = new MainViewModel(
            new EmptyDashboardSource(),
            uiDispatcher: new ImmediateUiDispatcher(),
            usageReadPort: port);
        port.Daily.Enqueue(() => new DailyUsage(From, To, [], 0));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "", "", 5, 0, 0, 0, 0)], [Cov(5, 5)], [Col("col-a", 1, null)]));
        viewModel.NavigateToUsage();
        await viewModel.UsageLoadTask;
        await viewModel.BreakdownLoadTask;

        Assert.Equal("Ready", viewModel.BreakdownState);

        // The daily read fails, the breakdown read succeeds: the breakdown stays Ready.
        port.Daily.Enqueue(() => throw new UsageReadException(UsageReadFailure.TransportFailure, "offline"));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "", "", 6, 0, 0, 0, 0)], [Cov(6, 6)], [Col("col-a", 1, null)]));
        await viewModel.RefreshUsageAsync();
        await viewModel.BreakdownLoadTask;
        Assert.True(viewModel.IsUsageStale);
        Assert.Equal("Ready", viewModel.BreakdownState);
        Assert.Equal(6, Assert.Single(viewModel.BreakdownRows).Total);

        // The breakdown read fails, the daily read succeeds: the daily stays Ready and
        // the breakdown keeps its previous values, marked stale.
        port.Daily.Enqueue(() => new DailyUsage(From, To, [new(To, "col-a", 9)], 9));
        port.Breakdown.Enqueue(() => throw new UsageReadException(UsageReadFailure.Timeout, "slow"));
        await viewModel.RefreshUsageAsync();
        await viewModel.BreakdownLoadTask;
        Assert.Equal("Ready", viewModel.UsageState);
        Assert.Equal("Stale", viewModel.BreakdownState);
        Assert.True(viewModel.IsBreakdownStale);
        Assert.Single(viewModel.BreakdownRows);
    }

    [AvaloniaFact]
    public async Task ALoadingBreakdownShowsProgressAndSuppressesSwitching()
    {
        var port = new ScriptedDualReadPort();
        using var viewModel = new MainViewModel(
            new EmptyDashboardSource(),
            uiDispatcher: new ImmediateUiDispatcher(),
            usageReadPort: port);
        port.Daily.Enqueue(() => new DailyUsage(From, To, [], 0));
        var loading = port.GateBreakdown();
        viewModel.NavigateToUsage();
        var pending = viewModel.BreakdownLoadTask;
        await viewModel.UsageLoadTask;

        Assert.Equal("Loading", viewModel.BreakdownState);
        Assert.True(viewModel.IsUsageBreakdownLoading);
        Assert.False(viewModel.CanSwitchBreakdownDimension);

        port.ReleaseBreakdown(Breakdown([], [], [Col("col-a", 1, null)]));
        await loading;
        await pending;
        Assert.Equal("Empty", viewModel.BreakdownState);
        Assert.True(viewModel.CanSwitchBreakdownDimension);
    }

    // ---------- helpers ----------

    private static MainViewModel Create(UsageBreakdown breakdown, UiLanguage language = UiLanguage.English) =>
        Create(new FixedDualReadPort(breakdown), language);

    private static MainViewModel Create(UsageReadFailure failure, UiLanguage language = UiLanguage.English) =>
        Create(new FixedFailingReadPort(failure), language);

    private static MainViewModel Create(IUsageReadPort port, UiLanguage language)
    {
        var viewModel = new MainViewModel(
            new EmptyDashboardSource(),
            uiDispatcher: new ImmediateUiDispatcher(),
            usageReadPort: port);
        viewModel.Language = language;
        return viewModel;
    }

    private static UsageBreakdown Breakdown(UsagePart[] parts, UsageCoverage[] coverage, UsageCollectorReceipt[] collectors) =>
        new(From, To, parts, coverage, collectors);

    private static UsagePart Part(string client, string provider, string model, params long[] components)
    {
        long total = 0;
        foreach (var component in components) total += component;
        return new UsagePart(client, provider, model, components[0], components[1], components[2], components[3], components[4], total);
    }

    private static UsageCoverage Cov(long totalTokens, long attributedTokens, string date = "2026-11-01", string collectorId = "col-a") =>
        new(DateOnly.Parse(date, CultureInfo.InvariantCulture), collectorId, totalTokens, attributedTokens);

    private static UsageCollectorReceipt Col(string collectorId, long generation, string? lastReceivedAt) =>
        new(collectorId, generation, lastReceivedAt is null ? null : DateTimeOffset.Parse(lastReceivedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));

    private sealed class FixedDualReadPort(UsageBreakdown breakdown) : IUsageReadPort
    {
        public Task<DailyUsage> ReadDailyAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken) =>
            Task.FromResult(new DailyUsage(from, to, [], 0));

        public Task<UsageBreakdown> ReadBreakdownAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken) =>
            Task.FromResult(breakdown);
    }

    private sealed class FixedFailingReadPort(UsageReadFailure failure) : IUsageReadPort
    {
        public Task<DailyUsage> ReadDailyAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken) =>
            Task.FromResult(new DailyUsage(from, to, [], 0));

        public Task<UsageBreakdown> ReadBreakdownAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken) =>
            Task.FromException<UsageBreakdown>(new UsageReadException(failure, "classified failure"));
    }

    private sealed class ScriptedDualReadPort : IUsageReadPort
    {
        public Queue<Func<DailyUsage>> Daily { get; } = new();
        public Queue<Func<UsageBreakdown>> Breakdown { get; } = new();
        private TaskCompletionSource<UsageBreakdown>? gate;

        public Task<DailyUsage> ReadDailyAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken)
        {
            var next = Daily.Dequeue();
            try
            {
                return Task.FromResult(next());
            }
            catch (Exception failure)
            {
                return Task.FromException<DailyUsage>(failure);
            }
        }

        public Task<UsageBreakdown> ReadBreakdownAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken)
        {
            if (gate is not null) return gate.Task;
            var next = Breakdown.Dequeue();
            try
            {
                return Task.FromResult(next());
            }
            catch (Exception failure)
            {
                return Task.FromException<UsageBreakdown>(failure);
            }
        }

        public Task<UsageBreakdown> GateBreakdown()
        {
            gate = new TaskCompletionSource<UsageBreakdown>(TaskCreationOptions.RunContinuationsAsynchronously);
            return gate.Task;
        }

        public void ReleaseBreakdown(UsageBreakdown next) => gate!.TrySetResult(next);
    }
}

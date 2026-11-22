using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using QuotaSight.Infrastructure;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// The dashboard's "observed usage (TokScale)" section: small per-client cards fed by the
/// breakdown read port, shown only while a usage API is configured, refreshed on a longer
/// interval than the usage page, and never dressed up as a remaining-quota gauge.
/// </summary>
public sealed class TokScaleDashboardTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 22);

    // ---------- hidden when unconfigured ----------

    [AvaloniaFact]
    public async Task WithoutAUsageConnectionTheSectionIsAbsent()
    {
        using var viewModel = new MainViewModel(new EmptyDashboardSource(), timeProvider: new FixedNow(Now));
        await viewModel.InitializeAsync();

        Assert.False(viewModel.IsTokScaleSectionVisible);
        Assert.Empty(viewModel.TokScaleCards);
    }

    [AvaloniaFact]
    public async Task WhenTheConnectionIsRemovedTheSectionIsDroppedAgain()
    {
        var port = new ScriptedDualReadPort();
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "", "", 10, 0, 0, 0, 0)], [Cov(10, 10)], [Col("col-a", 1, Now.AddHours(-1))]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "", "", 60, 0, 0, 0, 0)], [Cov(60, 60)], [Col("col-a", 1, Now.AddHours(-1))]));
        using var viewModel = Create(port);
        await viewModel.InitializeAsync();
        Assert.True(viewModel.IsTokScaleSectionVisible);

        await viewModel.ClearUsageConnectionAsync();

        Assert.False(viewModel.IsTokScaleSectionVisible);
    }

    // ---------- cards ----------

    [AvaloniaFact]
    public async Task DetectedClientsGetOneSmallCardEachWithTodayAndWeekTotals()
    {
        var port = new ScriptedDualReadPort();
        port.Breakdown.Enqueue(() => Breakdown(
            [Part("claude", "", "", 10, 0, 0, 0, 0), Part("codex", "", "", 4, 0, 0, 0, 0)],
            [Cov(14, 14)],
            [Col("col-a", 1, Now.AddHours(-1))]));
        port.Breakdown.Enqueue(() => Breakdown(
            [Part("codex", "", "", 120, 0, 0, 0, 0), Part("claude", "", "", 80, 0, 0, 0, 0)],
            [Cov(200, 200), Cov(0, 0, "2026-11-16")],
            [Col("col-a", 1, Now.AddHours(-1))]));
        using var viewModel = Create(port);
        await viewModel.InitializeAsync();
        await viewModel.TokScaleLoadTask;

        Assert.Equal(2, viewModel.TokScaleCards.Count);
        // Sorted by the week's total, descending.
        Assert.Equal("codex", viewModel.TokScaleCards[0].ClientId);
        Assert.Equal("120", viewModel.TokScaleCards[0].WeekText);
        Assert.Equal("4", viewModel.TokScaleCards[0].TodayText);
        Assert.Equal("Claude Code", viewModel.TokScaleCards[1].DisplayName);
        Assert.Equal("80", viewModel.TokScaleCards[1].WeekText);
        Assert.Equal("10", viewModel.TokScaleCards[1].TodayText);
    }

    [AvaloniaFact]
    public async Task TheSectionCarriesTheObservationBadgeAndFreshness()
    {
        var port = new ScriptedDualReadPort();
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "", "", 1, 0, 0, 0, 0)], [Cov(1, 1)], [Col("col-a", 1, Now.AddHours(-2))]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "", "", 2, 0, 0, 0, 0)], [Cov(2, 2)], [Col("col-a", 1, Now.AddHours(-2))]));
        using var viewModel = Create(port);
        viewModel.Language = UiLanguage.Japanese;
        await viewModel.InitializeAsync();
        await viewModel.TokScaleLoadTask;

        Assert.Contains("観測", viewModel.TokScaleBadgeText, StringComparison.Ordinal);
        Assert.Contains("推定", viewModel.TokScaleBadgeText, StringComparison.Ordinal);
        Assert.Contains("他端末", viewModel.TokScaleBadgeText, StringComparison.Ordinal);
        Assert.Contains("2026-11-22 10:00", viewModel.TokScaleFreshnessText, StringComparison.Ordinal);
        Assert.False(viewModel.IsTokScaleLoading);
    }

    [AvaloniaFact]
    public async Task AnEmptyDetectionStatesThatNoClientWasSeen()
    {
        var port = new ScriptedDualReadPort();
        port.Breakdown.Enqueue(() => Breakdown([], [Cov(0, 0)], [Col("col-a", 1, null)]));
        port.Breakdown.Enqueue(() => Breakdown([], [Cov(0, 0), Cov(0, 0, "2026-11-16")], [Col("col-a", 1, null)]));
        using var viewModel = Create(port);
        await viewModel.InitializeAsync();
        await viewModel.TokScaleLoadTask;

        Assert.True(viewModel.IsTokScaleSectionVisible);
        Assert.Empty(viewModel.TokScaleCards);
        Assert.Equal("Empty", viewModel.TokScaleState);
    }

    // ---------- refresh interval ----------

    [AvaloniaFact]
    public async Task NavigatingBackWithinTheIntervalDoesNotReadAgain()
    {
        var port = new ScriptedDualReadPort();
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "", "", 1, 0, 0, 0, 0)], [Cov(1, 1)], [Col("col-a", 1, Now.AddHours(-1))]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "", "", 2, 0, 0, 0, 0)], [Cov(2, 2)], [Col("col-a", 1, Now.AddHours(-1))]));
        using var viewModel = Create(port);
        await viewModel.InitializeAsync();
        await viewModel.TokScaleLoadTask;
        Assert.Equal(2, port.BreakdownCalls);

        // Leaving for the usage page costs one breakdown read of its own; returning to
        // the dashboard within the interval reads nothing more for the section.
        viewModel.NavigateToUsage();
        await viewModel.BreakdownLoadTask;
        viewModel.Navigate(AppPage.Dashboard);
        await viewModel.TokScaleLoadTask;
        Assert.Equal(3, port.BreakdownCalls);
        Assert.Equal("1", Assert.Single(viewModel.TokScaleCards).TodayText);

        // After the named interval the section reads again on navigation.
        port.Clock.Advance(MainViewModel.TokScaleRefreshInterval + TimeSpan.FromMinutes(1));
        viewModel.NavigateToUsage();
        await viewModel.BreakdownLoadTask;
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "", "", 2, 0, 0, 0, 0)], [Cov(2, 2)], [Col("col-a", 1, Now.AddHours(-1))]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "", "", 3, 0, 0, 0, 0)], [Cov(3, 3)], [Col("col-a", 1, Now.AddHours(-1))]));
        viewModel.Navigate(AppPage.Settings);
        viewModel.Navigate(AppPage.Dashboard);
        await viewModel.TokScaleLoadTask;
        Assert.Equal(6, port.BreakdownCalls);
        Assert.Equal("2", Assert.Single(viewModel.TokScaleCards).TodayText);
    }

    [AvaloniaFact]
    public async Task AFailedRefreshKeepsThePreviousCardsAndMarksThemStale()
    {
        var port = new ScriptedDualReadPort();
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "", "", 1, 0, 0, 0, 0)], [Cov(1, 1)], [Col("col-a", 1, Now.AddHours(-1))]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "", "", 2, 0, 0, 0, 0)], [Cov(2, 2)], [Col("col-a", 1, Now.AddHours(-1))]));
        using var viewModel = Create(port);
        await viewModel.InitializeAsync();
        await viewModel.TokScaleLoadTask;

        port.Clock.Advance(MainViewModel.TokScaleRefreshInterval + TimeSpan.FromMinutes(1));
        port.Breakdown.Enqueue(() => throw new UsageReadException(UsageReadFailure.Timeout, "slow"));
        port.Breakdown.Enqueue(() => throw new UsageReadException(UsageReadFailure.Timeout, "slow"));
        viewModel.Navigate(AppPage.Settings);
        viewModel.Navigate(AppPage.Dashboard);
        await viewModel.TokScaleLoadTask;

        Assert.Equal("Stale", viewModel.TokScaleState);
        var card = Assert.Single(viewModel.TokScaleCards);
        Assert.Equal("1", card.TodayText);
    }

    // ---------- on the real window ----------

    [AvaloniaFact]
    public async Task TheSectionRendersPerClientCardsWithTheBadgeOnTheWindow()
    {
        var port = new ScriptedDualReadPort();
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "", "", 10, 0, 0, 0, 0)], [Cov(10, 10)], [Col("col-a", 1, Now.AddHours(-1))]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "", "", 80, 0, 0, 0, 0)], [Cov(80, 80)], [Col("col-a", 1, Now.AddHours(-1))]));
        using var viewModel = Create(port);
        viewModel.Language = UiLanguage.Japanese;
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            await viewModel.InitializeAsync();
            await viewModel.TokScaleLoadTask;
            window.UpdateLayout();

            var section = window.FindControl<Border>("TokScaleSection")!;
            Assert.True(section.IsVisible);
            var cards = window.FindControl<ItemsControl>("TokScaleCards")!;
            Assert.Single(viewModel.TokScaleCards);
            Assert.Contains("観測使用量（TokScale）", viewModel.TokScaleTitleText, StringComparison.Ordinal);
            Assert.Contains("今日 10", viewModel.TokScaleCards[0].SummaryText, StringComparison.Ordinal);
            Assert.Contains("7日 80", viewModel.TokScaleCards[0].SummaryText, StringComparison.Ordinal);
            // An observation, never a quota: no progress bar while data is on screen.
            Assert.False(window.FindControl<ProgressBar>("TokScaleLoadingBar")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheSectionRelocalizesOnTheWindowWhenTheLanguageChanges()
    {
        var port = new ScriptedDualReadPort();
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "", "", 10, 0, 0, 0, 0)], [Cov(10, 10)], [Col("col-a", 1, Now.AddHours(-1))]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "", "", 80, 0, 0, 0, 0)], [Cov(80, 80)], [Col("col-a", 1, Now.AddHours(-1))]));
        using var viewModel = Create(port);
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            await viewModel.InitializeAsync();
            await viewModel.TokScaleLoadTask;
            window.UpdateLayout();

            var title = window.FindControl<TextBlock>("TokScaleTitle")!;
            var badge = window.FindControl<TextBlock>("TokScaleBadge")!;
            Assert.Contains("Observed usage (TokScale)", title.Text, StringComparison.Ordinal);
            Assert.Contains("other devices", badge.Text, StringComparison.Ordinal);
            Assert.Contains("Today 10", viewModel.TokScaleCards.Single().SummaryText, StringComparison.Ordinal);

            await viewModel.SetLanguageAsync(UiLanguage.Japanese);
            window.UpdateLayout();

            // Title, badge and the stored per-client cards all follow the switch; the
            // stored numbers never change, only their labels do.
            Assert.Contains("観測使用量（TokScale）", title.Text, StringComparison.Ordinal);
            Assert.Contains("他端末", badge.Text, StringComparison.Ordinal);
            Assert.Contains("今日 10", viewModel.TokScaleCards.Single().SummaryText, StringComparison.Ordinal);
            Assert.Contains("7日 80", viewModel.TokScaleCards.Single().SummaryText, StringComparison.Ordinal);
            Assert.Equal(2, port.BreakdownCalls);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WithoutAUsageConnectionTheSectionIsCollapsedOnTheWindow()
    {
        using var viewModel = new MainViewModel(new EmptyDashboardSource(), timeProvider: new FixedNow(Now));
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            await viewModel.InitializeAsync();
            window.UpdateLayout();
            Assert.False(window.FindControl<Border>("TokScaleSection")!.IsVisible);
        }
        finally { window.Close(); }
    }

    // ---------- usage-only cards for catalog clients ----------

    [AvaloniaFact]
    public async Task AnAddedClientWithoutObservationsGetsANotObservedCard()
    {
        var port = new ScriptedDualReadPort();
        using var viewModel = Create(port, TemporarySettingsStore());
        await viewModel.InitializeAsync();

        await viewModel.AddTokScaleClientAsync("cursor");
        await viewModel.TokScaleLoadTask;

        Assert.True(viewModel.IsTokScaleSectionVisible);
        var card = Assert.Single(viewModel.TokScaleCards);
        Assert.Equal("cursor", card.ClientId);
        Assert.Equal("Cursor IDE", card.DisplayName);
        Assert.False(card.Observed);
        Assert.Equal("0", card.TodayText);
        Assert.Equal("0", card.WeekText);
        Assert.Contains("Not observed yet", card.SummaryText, StringComparison.Ordinal);
        // The card is an observation surface, never a quota: the state stays Ready.
        Assert.Equal("Ready", viewModel.TokScaleState);
    }

    [AvaloniaFact]
    public async Task TheTrackedClientListPersistsThroughTheSettingsStore()
    {
        var store = TemporarySettingsStore();
        var port = new ScriptedDualReadPort();
        using var viewModel = Create(port, store);
        await viewModel.InitializeAsync();

        await viewModel.AddTokScaleClientAsync("cursor");
        await viewModel.AddTokScaleClientAsync("kiro");
        await viewModel.AddTokScaleClientAsync("cursor"); // Adding twice stores it once.

        var saved = await store.LoadAsync();
        Assert.Equal(["cursor", "kiro"], saved.TokScaleClients ?? []);

        // A fresh session over the same store rebuilds the tracked cards.
        port.Breakdown.Enqueue(() => Breakdown([], [Cov(0, 0)], [Col("col-a", 1, null)]));
        port.Breakdown.Enqueue(() => Breakdown([], [Cov(0, 0)], [Col("col-a", 1, null)]));
        using var reloaded = Create(port, store);
        await reloaded.InitializeAsync();
        await reloaded.TokScaleLoadTask;

        Assert.Equal(2, reloaded.TokScaleCards.Count);
        Assert.All(reloaded.TokScaleCards, card => Assert.False(card.Observed));
    }

    [AvaloniaFact]
    public async Task RemovingTheTrackedClientDropsItsCardAgain()
    {
        var port = new ScriptedDualReadPort();
        var store = TemporarySettingsStore();
        using var viewModel = Create(port, store);
        await viewModel.InitializeAsync();

        await viewModel.AddTokScaleClientAsync("cursor");
        await viewModel.TokScaleLoadTask;
        Assert.Single(viewModel.TokScaleCards);

        await viewModel.RemoveTokScaleClientAsync("cursor");

        Assert.Empty(viewModel.TokScaleCards);
        var saved = await store.LoadAsync();
        Assert.Equal([], saved.TokScaleClients ?? []);
    }

    [AvaloniaFact]
    public async Task ObservedClientsKeepTheirDataCardsNextToTheTrackedOnes()
    {
        var port = new ScriptedDualReadPort();
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "", "", 10, 0, 0, 0, 0)], [Cov(10, 10)], [Col("col-a", 1, Now.AddHours(-1))]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "", "", 80, 0, 0, 0, 0)], [Cov(80, 80)], [Col("col-a", 1, Now.AddHours(-1))]));
        using var viewModel = Create(port, TemporarySettingsStore());
        await viewModel.InitializeAsync();
        await viewModel.TokScaleLoadTask;

        await viewModel.AddTokScaleClientAsync("amp");

        Assert.Equal(2, viewModel.TokScaleCards.Count);
        var observed = viewModel.TokScaleCards.Single(card => card.Observed);
        Assert.Equal("claude", observed.ClientId);
        Assert.Equal("80", observed.WeekText);
        var tracked = viewModel.TokScaleCards.Single(card => !card.Observed);
        Assert.Equal("amp", tracked.ClientId);
    }

    // ---------- the add-provider flow on the real window ----------

    [AvaloniaFact]
    public async Task PickingACatalogClientOpensTheUsageOnlyPanelAndAddingShowsTheRemoveAction()
    {
        var port = new ScriptedDualReadPort();
        using var viewModel = Create(port, TemporarySettingsStore());
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            await window.InitializeAsync();
            window.UpdateLayout();

            viewModel.OpenProviderFlow();
            window.FindControl<ComboBox>("ProviderPicker")!.SelectedItem =
                viewModel.ProviderChoices.Single(choice => choice.TokScaleClientId == "cursor");
            window.UpdateLayout();

            Assert.True(viewModel.IsTokScaleProviderPanelVisible);
            Assert.False(viewModel.IsManualProviderPanelVisible);
            Assert.False(viewModel.IsCodexProviderPanelVisible);
            var panel = window.FindControl<Border>("TokScalePanel")!;
            Assert.True(panel.IsVisible);
            Assert.Contains("Cursor IDE", viewModel.TokScaleFlowTitleText, StringComparison.Ordinal);
            Assert.False(viewModel.IsTokScaleClientTracked);
            Assert.True(window.FindControl<Button>("TokScaleAddButton")!.IsVisible);
            Assert.False(window.FindControl<Button>("TokScaleRemoveButton")!.IsVisible);

            await viewModel.AddTokScaleClientAsync("cursor");
            window.UpdateLayout();

            Assert.True(viewModel.IsTokScaleClientTracked);
            Assert.True(window.FindControl<Button>("TokScaleRemoveButton")!.IsVisible);
            Assert.False(window.FindControl<Button>("TokScaleAddButton")!.IsVisible);
        }
        finally { window.Close(); }
    }

    // ---------- helpers ----------

    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    private static MainViewModel Create(ScriptedDualReadPort port, AppSettingsStore? settingsStore = null)
    {
        var viewModel = new MainViewModel(
            new EmptyDashboardSource(),
            timeProvider: port.Clock,
            uiDispatcher: new ImmediateUiDispatcher(),
            settingsStore: settingsStore,
            usageReadPort: port,
            localTimeZone: Tokyo);
        return viewModel;
    }

    /// <summary>A settings store rooted in a throwaway directory; no user setting is touched.</summary>
    private static AppSettingsStore TemporarySettingsStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "quotasight-tests", Guid.NewGuid().ToString("N"));
        return new AppSettingsStore(root);
    }

    private static UsageBreakdown Breakdown(UsagePart[] parts, UsageCoverage[] coverage, UsageCollectorReceipt[] collectors) =>
        new(Today, Today, parts, coverage, collectors);

    private static UsagePart Part(string client, string provider, string model, params long[] components)
    {
        long total = 0;
        foreach (var component in components) total += component;
        return new UsagePart(client, provider, model, components[0], components[1], components[2], components[3], components[4], total);
    }

    private static UsageCoverage Cov(long totalTokens, long attributedTokens, string date = "2026-11-22", string collectorId = "col-a") =>
        new(DateOnly.Parse(date, CultureInfo.InvariantCulture), collectorId, totalTokens, attributedTokens);

    private static UsageCollectorReceipt Col(string collectorId, long generation, DateTimeOffset? lastReceivedAt) =>
        new(collectorId, generation, lastReceivedAt);

    private sealed class ScriptedDualReadPort : IUsageReadPort
    {
        public MutableClock Clock { get; } = new(Now);
        public Queue<Func<UsageBreakdown>> Breakdown { get; } = new();
        public int BreakdownCalls;

        public Task<DailyUsage> ReadDailyAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken) =>
            Task.FromResult(new DailyUsage(from, to, [], 0));

        public Task<UsageBreakdown> ReadBreakdownAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref BreakdownCalls);
            // An unqueued read is an empty answer: the port never invents values, but the
            // tests only script the reads they assert on.
            if (!Breakdown.TryDequeue(out var next))
            {
                return Task.FromResult(new UsageBreakdown(from, to, [], [], []));
            }

            try
            {
                return Task.FromResult(next());
            }
            catch (Exception failure)
            {
                return Task.FromException<UsageBreakdown>(failure);
            }
        }
    }

    private sealed class FixedNow(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan amount) => current += amount;
    }
}

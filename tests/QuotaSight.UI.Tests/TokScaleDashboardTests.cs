using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using QuotaSight.Infrastructure;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// The dashboard's "observed usage (TokScale)" section: small per-provider cards fed by
/// the breakdown read port, visible even without a usage connection (an empty state with a
/// settings shortcut takes its place), refreshed on a longer interval than the usage page,
/// and never dressed up as a remaining-quota gauge.
/// </summary>
public sealed class TokScaleDashboardTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 22);

    // ---------- visible even without a usage connection ----------

    [AvaloniaFact]
    public async Task WithoutAUsageConnectionTheSectionStaysVisibleWithAnEmptyState()
    {
        using var viewModel = new MainViewModel(new EmptyDashboardSource(), timeProvider: new FixedNow(Now));
        await viewModel.InitializeAsync();
        await viewModel.TokScaleLoadTask;

        Assert.True(viewModel.IsTokScaleSectionVisible);
        Assert.Equal("Unconfigured", viewModel.TokScaleState);
        Assert.True(viewModel.IsTokScaleUnconfigured);
        Assert.Empty(viewModel.TokScaleCards);
        Assert.True(viewModel.IsTokScaleEmptyStateVisible);
        // The status sentence names the missing connection, and the empty body's next
        // step is to set it up.
        Assert.Contains("usage API", viewModel.TokScaleStateText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("usage API", viewModel.TokScaleEmptyBodyText, StringComparison.OrdinalIgnoreCase);
        // Nothing was read and nothing may be claimed: no freshness, no observation data.
        Assert.False(viewModel.IsTokScaleFreshnessVisible);
    }

    [AvaloniaFact]
    public async Task AnAddedClientShowsItsNotObservedCardWithoutAUsageConnection()
    {
        using var viewModel = new MainViewModel(
            new EmptyDashboardSource(), timeProvider: new FixedNow(Now), settingsStore: TemporarySettingsStore());
        await viewModel.InitializeAsync();
        await viewModel.TokScaleLoadTask;

        await viewModel.AddTokScaleProviderAsync("openai");
        await viewModel.TokScaleLoadTask;

        Assert.True(viewModel.IsTokScaleSectionVisible);
        Assert.Equal("Unconfigured", viewModel.TokScaleState);
        var card = Assert.Single(viewModel.TokScaleCards);
        Assert.Equal("openai", card.ProviderKey);
        Assert.False(card.Observed);
        Assert.Contains("Not observed yet", card.SummaryText, StringComparison.Ordinal);
        // The cards do not hide that the connection is missing: the status line stays.
        Assert.True(viewModel.IsTokScaleStatusVisible);
        Assert.Contains("usage API", viewModel.TokScaleStateText, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task RemovingTheConnectionDropsTheObservedDataButKeepsTheSectionAndTrackedCards()
    {
        var port = new ScriptedDualReadPort();
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "", 10, 0, 0, 0, 0)], [Cov(10, 10)], [Col("col-a", 1, Now.AddHours(-1))]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "", 60, 0, 0, 0, 0)], [Cov(60, 60)], [Col("col-a", 1, Now.AddHours(-1))]));
        using var viewModel = Create(port, TemporarySettingsStore());
        await viewModel.InitializeAsync();
        await viewModel.TokScaleLoadTask;
        await viewModel.AddTokScaleProviderAsync("openai");
        Assert.True(viewModel.IsTokScaleSectionVisible);
        Assert.Equal(2, viewModel.TokScaleCards.Count);

        await viewModel.ClearUsageConnectionAsync();

        Assert.True(viewModel.IsTokScaleSectionVisible);
        Assert.Equal("Unconfigured", viewModel.TokScaleState);
        // The observed data card is gone with the credential it was read with; the added
        // card survives as a "not observed yet" card and the section itself stays put.
        var card = Assert.Single(viewModel.TokScaleCards);
        Assert.Equal("openai", card.ProviderKey);
        Assert.False(card.Observed);
    }

    // ---------- cards ----------

    [AvaloniaFact]
    public async Task DetectedClientsGetOneSmallCardEachWithTodayAndWeekTotals()
    {
        var port = new ScriptedDualReadPort();
        port.Breakdown.Enqueue(() => Breakdown(
            [Part("claude", "anthropic", "", 10, 0, 0, 0, 0), Part("codex", "openai", "", 4, 0, 0, 0, 0)],
            [Cov(14, 14)],
            [Col("col-a", 1, Now.AddHours(-1))]));
        port.Breakdown.Enqueue(() => Breakdown(
            [Part("codex", "openai", "", 120, 0, 0, 0, 0), Part("claude", "anthropic", "", 80, 0, 0, 0, 0)],
            [Cov(200, 200), Cov(0, 0, "2026-11-16")],
            [Col("col-a", 1, Now.AddHours(-1))]));
        using var viewModel = Create(port);
        await viewModel.InitializeAsync();
        await viewModel.TokScaleLoadTask;

        Assert.Equal(2, viewModel.TokScaleCards.Count);
        // Sorted by the week's total, descending.
        Assert.Equal("openai", viewModel.TokScaleCards[0].ProviderKey);
        Assert.Equal("120", viewModel.TokScaleCards[0].WeekText);
        Assert.Equal("4", viewModel.TokScaleCards[0].TodayText);
        Assert.Equal("Anthropic", viewModel.TokScaleCards[1].DisplayName);
        Assert.Equal("80", viewModel.TokScaleCards[1].WeekText);
        Assert.Equal("10", viewModel.TokScaleCards[1].TodayText);
    }

    [AvaloniaFact]
    public async Task TheSectionCarriesTheObservationBadgeAndFreshness()
    {
        var port = new ScriptedDualReadPort();
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "", 1, 0, 0, 0, 0)], [Cov(1, 1)], [Col("col-a", 1, Now.AddHours(-2))]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "", 2, 0, 0, 0, 0)], [Cov(2, 2)], [Col("col-a", 1, Now.AddHours(-2))]));
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
        // Connected but without data: the empty state names the next step (add a provider
        // or wait for a collector), and there is no settings shortcut to push.
        Assert.True(viewModel.IsTokScaleEmptyStateVisible);
        Assert.False(viewModel.IsTokScaleUnconfigured);
        Assert.Contains("provider", viewModel.TokScaleEmptyBodyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("collector", viewModel.TokScaleEmptyBodyText, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- refresh interval ----------

    [AvaloniaFact]
    public async Task NavigatingBackWithinTheIntervalDoesNotReadAgain()
    {
        var port = new ScriptedDualReadPort();
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "", 1, 0, 0, 0, 0)], [Cov(1, 1)], [Col("col-a", 1, Now.AddHours(-1))]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "", 2, 0, 0, 0, 0)], [Cov(2, 2)], [Col("col-a", 1, Now.AddHours(-1))]));
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
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "", 2, 0, 0, 0, 0)], [Cov(2, 2)], [Col("col-a", 1, Now.AddHours(-1))]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "", 3, 0, 0, 0, 0)], [Cov(3, 3)], [Col("col-a", 1, Now.AddHours(-1))]));
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
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "", 1, 0, 0, 0, 0)], [Cov(1, 1)], [Col("col-a", 1, Now.AddHours(-1))]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "", 2, 0, 0, 0, 0)], [Cov(2, 2)], [Col("col-a", 1, Now.AddHours(-1))]));
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
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "", 10, 0, 0, 0, 0)], [Cov(10, 10)], [Col("col-a", 1, Now.AddHours(-1))]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "", 80, 0, 0, 0, 0)], [Cov(80, 80)], [Col("col-a", 1, Now.AddHours(-1))]));
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
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "", 10, 0, 0, 0, 0)], [Cov(10, 10)], [Col("col-a", 1, Now.AddHours(-1))]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "", 80, 0, 0, 0, 0)], [Cov(80, 80)], [Col("col-a", 1, Now.AddHours(-1))]));
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

            // Title, badge and the stored per-provider cards all follow the switch; the
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
    public async Task WithoutAUsageConnectionTheSectionShowsTheEmptyStateAndASettingsShortcut()
    {
        using var viewModel = new MainViewModel(new EmptyDashboardSource(), timeProvider: new FixedNow(Now));
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            await viewModel.InitializeAsync();
            await viewModel.TokScaleLoadTask;
            window.UpdateLayout();

            Assert.True(window.FindControl<Border>("TokScaleSection")!.IsVisible);
            Assert.True(window.FindControl<StackPanel>("TokScaleEmptyState")!.IsVisible);
            var openSettings = window.FindControl<Button>("TokScaleOpenSettingsButton")!;
            Assert.True(openSettings.IsVisible);

            openSettings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(AppPage.Settings, viewModel.CurrentPage);
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

        await viewModel.AddTokScaleProviderAsync("openai");
        await viewModel.TokScaleLoadTask;

        Assert.True(viewModel.IsTokScaleSectionVisible);
        var card = Assert.Single(viewModel.TokScaleCards);
        Assert.Equal("openai", card.ProviderKey);
        Assert.Equal("OpenAI", card.DisplayName);
        Assert.False(card.Observed);
        Assert.Equal("0", card.TodayText);
        Assert.Equal("0", card.WeekText);
        Assert.Contains("Not observed yet", card.SummaryText, StringComparison.Ordinal);
        // The card is an observation surface, never a quota: the state stays Ready.
        Assert.Equal("Ready", viewModel.TokScaleState);
    }

    [AvaloniaFact]
    public async Task TheTrackedProviderListPersistsThroughTheSettingsStore()
    {
        var store = TemporarySettingsStore();
        var port = new ScriptedDualReadPort();
        using var viewModel = Create(port, store);
        await viewModel.InitializeAsync();

        await viewModel.AddTokScaleProviderAsync("openai");
        await viewModel.AddTokScaleProviderAsync("deepseek");
        await viewModel.AddTokScaleProviderAsync("openai"); // Adding twice stores it once.

        var saved = await store.LoadAsync();
        Assert.Equal(["openai", "deepseek"], saved.TokScaleProviders ?? []);

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
    public async Task ATrackedProviderWithoutAttributionGetsItsNotObservedCard()
    {
        var port = new ScriptedDualReadPort();
        using var viewModel = Create(port, TemporarySettingsStore());
        await viewModel.InitializeAsync();

        await viewModel.AddTokScaleProviderAsync("deepseek");
        await viewModel.TokScaleLoadTask;

        Assert.True(viewModel.IsTokScaleSectionVisible);
        var card = Assert.Single(viewModel.TokScaleCards);
        Assert.Equal("deepseek", card.ProviderKey);
        Assert.Equal("DeepSeek", card.DisplayName);
        Assert.False(card.Observed);
        Assert.Equal("0", card.TodayText);
        Assert.Equal("0", card.WeekText);
        Assert.Contains("Not observed yet", card.SummaryText, StringComparison.Ordinal);
        // The card is an observation surface, never a quota: the state stays Ready.
        Assert.Equal("Ready", viewModel.TokScaleState);
    }

    [AvaloniaFact]
    public async Task RemovingTheTrackedProviderDropsItsCardAgain()
    {
        var port = new ScriptedDualReadPort();
        var store = TemporarySettingsStore();
        using var viewModel = Create(port, store);
        await viewModel.InitializeAsync();

        await viewModel.AddTokScaleProviderAsync("openai");
        await viewModel.TokScaleLoadTask;
        Assert.Single(viewModel.TokScaleCards);

        await viewModel.RemoveTokScaleProviderAsync("openai");

        Assert.Empty(viewModel.TokScaleCards);
        var saved = await store.LoadAsync();
        Assert.Equal([], saved.TokScaleProviders ?? []);
    }

    [AvaloniaFact]
    public async Task ObservedProvidersKeepTheirDataCardsNextToTheTrackedOnes()
    {
        var port = new ScriptedDualReadPort();
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "", 10, 0, 0, 0, 0)], [Cov(10, 10)], [Col("col-a", 1, Now.AddHours(-1))]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "", 80, 0, 0, 0, 0)], [Cov(80, 80)], [Col("col-a", 1, Now.AddHours(-1))]));
        using var viewModel = Create(port, TemporarySettingsStore());
        await viewModel.InitializeAsync();
        await viewModel.TokScaleLoadTask;

        await viewModel.AddTokScaleProviderAsync("nous");

        Assert.Equal(2, viewModel.TokScaleCards.Count);
        var observed = viewModel.TokScaleCards.Single(card => card.Observed);
        Assert.Equal("anthropic", observed.ProviderKey);
        Assert.Equal("80", observed.WeekText);
        var tracked = viewModel.TokScaleCards.Single(card => !card.Observed);
        Assert.Equal("nous", tracked.ProviderKey);
    }

    // ---------- settings compatibility ----------

    [AvaloniaFact]
    public async Task AStoredClientListIsKeptInSettingsButNeverBecomesCards()
    {
        // A settings file written before provider cards carries tracked client ids. They
        // are read and preserved verbatim — destroying a user's saved list would be
        // data loss — but clients are no longer a card source, and an observed provider
        // becomes a card without any list entry.
        var store = TemporarySettingsStore();
        await store.SaveAsync(new AppSettingsDto(TokScaleClients: ["antigravity-cli", "grok"]));
        var port = new ScriptedDualReadPort();
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "", 10, 0, 0, 0, 0)], [Cov(10, 10)], [Col("col-a", 1, Now.AddHours(-1))]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "", 80, 0, 0, 0, 0)], [Cov(80, 80)], [Col("col-a", 1, Now.AddHours(-1))]));
        using var viewModel = Create(port, store);
        await viewModel.InitializeAsync();
        await viewModel.TokScaleLoadTask;

        // The legacy ids never turn into cards, and nothing else was added.
        var card = Assert.Single(viewModel.TokScaleCards);
        Assert.Equal("anthropic", card.ProviderKey);

        // A later save keeps the legacy list untouched next to the new field.
        await viewModel.AddTokScaleProviderAsync("openai");
        var saved = await store.LoadAsync();
        Assert.Equal(["antigravity-cli", "grok"], saved.TokScaleClients ?? []);
        Assert.Equal(["openai"], saved.TokScaleProviders ?? []);
    }

    // ---------- the add-provider flow on the real window ----------

    [AvaloniaFact]
    public async Task PickingACatalogProviderOpensTheUsageOnlyPanelAndAddingShowsTheRemoveAction()
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
                viewModel.ProviderChoices.Single(choice => choice.TokScaleProviderId == "openai");
            window.UpdateLayout();

            Assert.True(viewModel.IsTokScaleProviderPanelVisible);
            Assert.False(viewModel.IsManualProviderPanelVisible);
            Assert.False(viewModel.IsCodexProviderPanelVisible);
            var panel = window.FindControl<Border>("TokScalePanel")!;
            Assert.True(panel.IsVisible);
            Assert.Contains("OpenAI", viewModel.TokScaleFlowTitleText, StringComparison.Ordinal);
            Assert.False(viewModel.IsTokScaleProviderTracked);
            Assert.True(window.FindControl<Button>("TokScaleAddButton")!.IsVisible);
            Assert.False(window.FindControl<Button>("TokScaleRemoveButton")!.IsVisible);

            await viewModel.AddTokScaleProviderAsync("openai");
            window.UpdateLayout();

            Assert.True(viewModel.IsTokScaleProviderTracked);
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

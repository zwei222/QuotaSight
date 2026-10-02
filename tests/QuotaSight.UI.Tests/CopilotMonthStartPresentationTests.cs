using QuotaSight.Application;
using QuotaSight.Core;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class CopilotMonthStartPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 15, 0, TimeSpan.Zero);
    private static readonly CopilotRequestedPeriod October = new(2026, 10, "org", "user");

    [Fact]
    public async Task October_no_data_does_not_project_September_measurement_as_current()
    {
        var september = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var snapshot = CopilotSnapshot("org/user", september, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        using var viewModel = CreateViewModel(new CopilotCardSource(snapshot), new NoDataApplication(Result(noData: [ProviderKind.Copilot], account: "org/user", period: October)));

        await viewModel.RefreshAsync();

        var card = Assert.Single(viewModel.Cards);
        Assert.Equal("October 2026 · UTC", card.Windows.Single().WindowName);
        Assert.Contains("No usage details", card.Windows.Single().StatusText, StringComparison.Ordinal);
        Assert.Empty(card.Windows.Single().PercentText);
        Assert.False(card.Windows.Single().IsGaugeVisible);
        Assert.False(card.Windows.Single().IsQuantityCompositionVisible);
        Assert.DoesNotContain("Stale", card.StateText, StringComparison.Ordinal);
        Assert.Equal("org/user", card.Account);
        Assert.True(card.Windows.Single().IsCopilotNoData);
        Assert.DoesNotContain("1900", card.Windows.Single().StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(viewModel.Cards, item => item.Account == "org · user");
    }

    [Fact]
    public async Task No_data_details_and_accessible_label_remain_localized_after_language_round_trip()
    {
        using var viewModel = CreateViewModel(new EmptyDashboardSource(), new NoDataApplication(Result(noData: [ProviderKind.Copilot], account: "org/user", period: October)));

        await viewModel.RefreshAsync();

        var card = Assert.Single(viewModel.Cards);
        var row = Assert.Single(card.Windows);
        var englishDetails = new UiCopy(UiLanguage.English).CopilotNoDataDetails;
        Assert.Equal(englishDetails, row.StatusText);
        Assert.Equal($"GitHub Copilot, org/user. {new UiCopy(UiLanguage.English).CopilotNoDataAutomation("October 2026 · UTC")}", card.AccessibleLabel);
        Assert.Contains("usage unavailable", row.StatusText, StringComparison.Ordinal);
        Assert.False(row.IsGaugeVisible);
        Assert.True(row.IsCopilotNoData);

        viewModel.Language = UiLanguage.Japanese;
        card = Assert.Single(viewModel.Cards);
        row = Assert.Single(card.Windows);
        Assert.Equal(new UiCopy(UiLanguage.Japanese).CopilotNoDataDetails, row.StatusText);
        Assert.Equal($"GitHub Copilot, org/user. {new UiCopy(UiLanguage.Japanese).CopilotNoDataAutomation("2026年10月 · UTC")}", card.AccessibleLabel);
        Assert.Contains("使用量不明", row.StatusText, StringComparison.Ordinal);

        viewModel.Language = UiLanguage.English;
        card = Assert.Single(viewModel.Cards);
        row = Assert.Single(card.Windows);
        Assert.Equal(englishDetails, row.StatusText);
        Assert.Equal($"GitHub Copilot, org/user. {new UiCopy(UiLanguage.English).CopilotNoDataAutomation("October 2026 · UTC")}", card.AccessibleLabel);
        Assert.Equal("October 2026 · UTC", row.WindowName);
        Assert.False(row.IsGaugeVisible);
        Assert.True(row.IsCopilotNoData);
        Assert.Single(viewModel.Cards);
        Assert.Single(card.Windows);
    }

    [Fact]
    public async Task Valid_copilot_no_data_projects_alongside_another_provider_failure()
    {
        var result = Result(noData: [ProviderKind.Copilot], failures: [new(ProviderKind.OpenCode, FetchStatus.TransientFailure)], account: "org/user", period: October);
        using var viewModel = CreateViewModel(new EmptyDashboardSource(), new NoDataApplication(result));

        await viewModel.RefreshAsync();

        AssertCopilotPlaceholder(viewModel, "org/user", "October 2026 · UTC");
        Assert.Equal(PresentationState.Error, viewModel.PresentationState);
        Assert.Contains("OpenCode", viewModel.NotificationBannerText, StringComparison.Ordinal);
        Assert.DoesNotContain("Copilot", viewModel.NotificationBannerText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copilot_specific_failure_prevents_no_data_placeholder_projection()
    {
        var result = Result(noData: [ProviderKind.Copilot], failures: [new(ProviderKind.Copilot, FetchStatus.TransientFailure)], account: "org/user", period: October);
        using var viewModel = CreateViewModel(new EmptyDashboardSource(), new NoDataApplication(result));

        await viewModel.RefreshAsync();

        Assert.Empty(viewModel.Cards);
        Assert.Equal(PresentationState.Error, viewModel.PresentationState);
        Assert.Contains("Copilot", viewModel.NotificationBannerText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copilot_and_open_code_no_data_projects_copilot_but_warns_only_for_open_code()
    {
        var result = Result(noData: [ProviderKind.Copilot, ProviderKind.OpenCode], account: "org/user", period: October);
        using var viewModel = CreateViewModel(new EmptyDashboardSource(), new NoDataApplication(result));

        await viewModel.RefreshAsync();

        AssertCopilotPlaceholder(viewModel, "org/user", "October 2026 · UTC");
        Assert.Contains("OpenCode", viewModel.NotificationBannerText, StringComparison.Ordinal);
        Assert.DoesNotContain("Copilot", viewModel.NotificationBannerText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "org/user")]
    [InlineData(10, "other/user")]
    public async Task Invalid_copilot_context_is_warned_and_not_projected(int month, string activeAccount)
    {
        var period = new CopilotRequestedPeriod(2026, month, "org", "user");
        var result = Result(noData: [ProviderKind.Copilot], account: activeAccount, period: period);
        using var viewModel = CreateViewModel(new EmptyDashboardSource(), new NoDataApplication(result));

        await viewModel.RefreshAsync();

        Assert.Empty(viewModel.Cards);
        Assert.Contains("Copilot", viewModel.NotificationBannerText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Context_free_copilot_no_data_keeps_previous_value_semantics()
    {
        var snapshot = CopilotSnapshot("org/user", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), Now.AddDays(-1));
        using var viewModel = CreateViewModel(new CopilotCardSource(snapshot), new NoDataApplication(Result(noData: [ProviderKind.Copilot])));

        await viewModel.RefreshAsync();

        var card = Assert.Single(viewModel.Cards);
        Assert.Contains(card.Windows, row => row.Snapshot is not null && !row.IsCopilotNoData);
        Assert.Contains("Copilot", viewModel.NotificationBannerText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replaced_no_data_account_is_removed_when_new_account_has_no_data()
    {
        var app = new SequenceApplication(
            Result(noData: [ProviderKind.Copilot], account: "org/A", period: new(2026, 10, "org", "A")),
            Result(noData: [ProviderKind.Copilot], account: "org/B", period: new(2026, 10, "org", "B")));
        using var viewModel = CreateViewModel(new EmptyDashboardSource(), app);

        await viewModel.RefreshAsync();
        await viewModel.RefreshAsync();

        var card = Assert.Single(viewModel.Cards);
        Assert.Equal("org/B", card.Account);
        Assert.Equal(new CopilotRequestedPeriod(2026, 10, "org", "B"), card.CopilotNoDataPeriod);
        Assert.DoesNotContain(viewModel.Cards, item => item.Account == "org/A");
    }

    [Fact]
    public async Task Replaced_no_data_account_is_removed_when_new_account_returns_success()
    {
        var app = new SequenceApplication(
            Result(noData: [ProviderKind.Copilot], account: "org/A", period: new(2026, 10, "org", "A")),
            Result(snapshots: [CopilotSnapshot("org/B", Now.AddDays(-1), Now)]));
        using var viewModel = CreateViewModel(new EmptyDashboardSource(), app);

        await viewModel.RefreshAsync();
        await viewModel.RefreshAsync();

        var card = Assert.Single(viewModel.Cards);
        Assert.Equal("org/B", card.Account);
        Assert.Null(card.CopilotNoDataPeriod);
        Assert.Contains(card.Windows, row => row.Snapshot is { Source: QuotaSource.Delayed });
        Assert.DoesNotContain(viewModel.Cards, item => item.Account == "org/A");
    }

    [Fact]
    public async Task Replacing_no_data_account_preserves_manual_window_and_clears_placeholder_status()
    {
        var manual = new QuotaSnapshot(ProviderKind.Copilot, "org/A", "Manual", new(QuotaWindowKind.Weekly, Now.AddDays(-7), Now.AddDays(7)), 40, 100, null, "%", Now, Now, QuotaSource.Manual, QuotaConfidence.Manual, null, "GitHub Copilot");
        var app = new SequenceApplication(
            Result(noData: [ProviderKind.Copilot], account: "org/A", period: new(2026, 10, "org", "A")),
            Result(noData: [ProviderKind.Copilot], account: "org/B", period: new(2026, 10, "org", "B")));
        using var viewModel = CreateViewModel(new CopilotCardSource(manual), app);

        await viewModel.RefreshAsync();
        await viewModel.RefreshAsync();

        var manualCard = Assert.Single(viewModel.Cards, card => card.Account == "org/A");
        Assert.Null(manualCard.CopilotNoDataPeriod);
        Assert.DoesNotContain(manualCard.Windows, row => row.IsCopilotNoData);
        Assert.Contains(manualCard.Windows, row => row.Snapshot?.Source == QuotaSource.Manual);
        Assert.Contains(viewModel.Cards, card => card.Account == "org/B" && card.IsCopilotNoData);
    }

    [Fact]
    public async Task No_data_then_success_for_same_identity_restores_quantity_and_connected_state_in_each_language()
    {
        var snapshot = CopilotSnapshot("org/user", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        using var viewModel = CreateViewModel(
            new EmptyDashboardSource(),
            new SequenceApplication(
                Result(noData: [ProviderKind.Copilot], account: "org/user", period: October),
                Result(snapshots: [snapshot])));

        await viewModel.RefreshAsync();
        await viewModel.RefreshAsync();

        var card = Assert.Single(viewModel.Cards);
        Assert.Equal("org/user", card.Account);
        Assert.Null(card.CopilotNoDataPeriod);
        Assert.Equal("Connected", card.StateText);
        var row = Assert.Single(card.Windows);
        Assert.True(row.IsQuantityOnly);
        Assert.False(row.IsCopilotNoData);
        Assert.False(row.IsGaugeVisible);
        Assert.Contains("Gross", row.QuantityGrossText, StringComparison.Ordinal);
        Assert.Equal(PresentationState.Ready, viewModel.PresentationState);

        viewModel.Language = UiLanguage.Japanese;
        card = Assert.Single(viewModel.Cards);
        row = Assert.Single(card.Windows);
        Assert.Null(card.CopilotNoDataPeriod);
        Assert.Equal("接続済み", card.StateText);
        Assert.True(row.IsQuantityOnly);
        Assert.False(row.IsCopilotNoData);
        Assert.False(row.IsGaugeVisible);
        Assert.Contains("総量", row.QuantityGrossText, StringComparison.Ordinal);
        Assert.Equal(PresentationState.Ready, viewModel.PresentationState);

        viewModel.Language = UiLanguage.English;
        card = Assert.Single(viewModel.Cards);
        row = Assert.Single(card.Windows);
        Assert.Null(card.CopilotNoDataPeriod);
        Assert.Equal("Connected", card.StateText);
        Assert.True(row.IsQuantityOnly);
        Assert.False(row.IsCopilotNoData);
        Assert.False(row.IsGaugeVisible);
        Assert.Contains("Gross", row.QuantityGrossText, StringComparison.Ordinal);
        Assert.Equal(PresentationState.Ready, viewModel.PresentationState);
    }

    private static MainViewModel CreateViewModel(IDashboardSource source, IQuotaApplication app) => new(source, quotaApplication: app, timeProvider: new FixedTimeProvider(Now));

    private static QuotaRefreshResult Result(
        IReadOnlyList<QuotaSnapshot>? snapshots = null,
        IReadOnlyList<ProviderFailure>? failures = null,
        ProviderKind[]? noData = null,
        string? account = null,
        CopilotRequestedPeriod? period = null)
    {
        IReadOnlyDictionary<ProviderKind, string> accounts = account is null ? new Dictionary<ProviderKind, string>() : new Dictionary<ProviderKind, string> { [ProviderKind.Copilot] = account };
        return new(snapshots ?? [], failures ?? [], noData is null ? [] : noData.ToHashSet(), accounts, period);
    }

    private static QuotaSnapshot CopilotSnapshot(string account, DateTimeOffset start, DateTimeOffset end) =>
        new(ProviderKind.Copilot, account, "AI credits", new(QuotaWindowKind.Monthly, start, end), null, null, null, "credits", end.AddMinutes(-1), end.AddMinutes(-1), QuotaSource.Delayed, QuotaConfidence.High, null, "GitHub Copilot")
        { CopilotUsage = new CopilotUsageBreakdown(1900m, 1900m, 0m) };

    private static void AssertCopilotPlaceholder(MainViewModel viewModel, string account, string month)
    {
        var card = Assert.Single(viewModel.Cards);
        Assert.Equal(account, card.Account);
        Assert.Equal(month, card.Windows.Single().WindowName);
        Assert.True(card.Windows.Single().IsCopilotNoData);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CopilotCardSource(QuotaSnapshot snapshot) : IDashboardSource
    {
        public bool IsDemo => false;
        public IReadOnlyList<ProviderCardViewModel> Load() => DashboardAggregation.ToCards([snapshot], snapshot.Fetched, UiLanguage.English);
    }

    private sealed class NoDataApplication(QuotaRefreshResult result) : IQuotaApplication
    {
        public ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken) => ValueTask.FromResult(result);
    }

    private sealed class SequenceApplication(params QuotaRefreshResult[] results) : IQuotaApplication
    {
        private int index;
        public ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken) => ValueTask.FromResult(results[Math.Min(index++, results.Length - 1)]);
    }
}

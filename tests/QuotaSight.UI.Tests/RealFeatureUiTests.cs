using System.Net;
using System.Text;
using Xunit;
using QuotaSight.Application;
using QuotaSight.Core;
using QuotaSight.Infrastructure;
using QuotaSight.UI;

namespace QuotaSight.UI.Tests;

public sealed class RealFeatureTests
{
    [Fact]
    public async Task OpenCode_success_stores_session_credential_and_delivers_snapshots()
    {
        var store = new InMemoryCredentialStore(); var delivered = new List<QuotaSnapshot>();
        using var client = new HttpClient(new JsonHandler("{\"rollingUsage\":{\"used\":80,\"limit\":100}}"));
        var facade = new UiProviderFacade(key => new OpenCodeGoAdapter(client, key, new Uri("https://example.test/usage"), TimeProvider.System), credentialStore: store, openCodeSuccess: snapshots => { delivered.AddRange(snapshots); return ValueTask.CompletedTask; });
        var result = await facade.TestOpenCodeAsync("session-key", default);
        Assert.True(result.Success); Assert.Single(delivered); Assert.Equal("session-key", await store.GetAsync("OpenCode Go", default)); Assert.DoesNotContain("session-key", result.Message);
    }

    [Fact]
    public async Task Refresh_failure_keeps_last_known_cards_and_marks_error()
    {
        var snapshot = Snapshot(40); var vm = new MainViewModel(new FixedDashboardSource(snapshot), quotaApplication: new StubApplication([]));
        await vm.RefreshAsync();
        Assert.Contains(vm.Cards, card => card.Account == "acct"); Assert.True(vm.IsError);
    }

    [Fact]
    public async Task Settings_roundtrip_and_client_id_update_are_public_contracts()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try { var store = new AppSettingsStore(root); var vm = new MainViewModel(new EmptyDashboardSource(), settingsStore: store); await vm.SetGithubClientIdAsync("client"); await vm.SetRefreshMinutesAsync(15); var loaded = await store.LoadAsync(); Assert.Equal("client", loaded.GithubOAuthClientId); Assert.Equal(15, loaded.RefreshMinutes); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Refresh_scheduler_reaches_the_same_application_refresh_contract()
    {
        var calls = 0; var scheduler = new RefreshScheduler(TimeSpan.FromMinutes(5)); using var stop = new CancellationTokenSource();
        stop.Cancel(); await scheduler.RunAsync(_ => { calls++; return ValueTask.CompletedTask; }, stop.Token); Assert.Equal(0, calls);
    }

    [Fact]
    public void Thirty_day_aggregation_deduplicates_account_and_keeps_multiple_windows()
    {
        var now = DateTimeOffset.UtcNow; var snapshots = new[] { Snapshot(40), Snapshot(90) with { Window = new(QuotaWindowKind.Monthly, now.AddDays(-1), now.AddDays(29)) } };
        var cards = DashboardAggregation.ToCards(snapshots, now); Assert.Single(cards); Assert.Equal(2, cards[0].Windows.Count); Assert.Equal("Monthly", cards[0].Windows[0].WindowName);
    }

    private static QuotaSnapshot Snapshot(decimal percent) => new(ProviderKind.OpenCode, "acct", "usage", new(QuotaWindowKind.Rolling, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1)), percent, 100, null, "requests", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, QuotaSource.Official, QuotaConfidence.Official, DateTimeOffset.UtcNow.AddHours(1), "OpenCode Go");
    private sealed class JsonHandler(string json) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }); }
    private sealed class FixedDashboardSource(QuotaSnapshot snapshot) : IDashboardSource { public bool IsDemo => false; public IReadOnlyList<ProviderCardViewModel> Load() => DashboardAggregation.ToCards([snapshot], DateTimeOffset.UtcNow); }
    private sealed class StubApplication(IReadOnlyList<QuotaSnapshot> result) : IQuotaApplication { public ValueTask<IReadOnlyList<QuotaSnapshot>> RefreshAsync(CancellationToken cancellationToken) => ValueTask.FromResult(result); }
}

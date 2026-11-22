using System.Net;
using System.Text;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class QuotaRingsHttpPortTests
{
    private static readonly DateTimeOffset Fixed = DateTimeOffset.Parse("2026-11-22T01:30:00Z");

    [Fact]
    public void ProviderProjectionCreatesOneCardWithShortAndLongQuotaWindows()
    {
        var quota = new LatestQuota(DateTimeOffset.Parse("2026-11-22T01:00:00Z"), [
            new QuotaProviderValue(QuotaProvider.Claude, QuotaProviderStatus.Available, QuotaFreshness.Fresh, DateTimeOffset.Parse("2026-11-22T00:59:00Z"), [
                new(QuotaWindow.Session, 3800, DateTimeOffset.Parse("2026-11-22T06:00:00Z")),
                new(QuotaWindow.Weekly, 7400, DateTimeOffset.Parse("2026-11-29T00:00:00Z"))])]);

        var cards = QuotaCardProjection.Project(quota, japanese: true, Fixed);

        var card = Assert.Single(cards);
        Assert.Equal("Claude", card.ProviderText);
        Assert.Equal("38%", card.CenterText);
        Assert.Equal(2, card.Windows.Count);
        Assert.Equal(QuotaWindow.Session, card.Windows[0].Window);
        Assert.Equal(QuotaWindow.Weekly, card.Windows[1].Window);
        Assert.Equal("週", card.Windows[1].WindowText);
        Assert.Equal(3800, card.RingValues[0]);
        Assert.Equal(7400, card.RingValues[1]);
    }

    [Fact]
    public void ProjectionKeepsTrueZeroSeparateFromUnknownAndLimitsRingToThreeWindows()
    {
        var quota = new LatestQuota(DateTimeOffset.Parse("2026-11-22T01:00:00Z"), [
            new QuotaProviderValue(QuotaProvider.Codex, QuotaProviderStatus.Available, QuotaFreshness.Cached, null, [
                new(QuotaWindow.Session, 0, null), new(QuotaWindow.FiveHour, null, null),
                new(QuotaWindow.Rolling, 5000, null), new(QuotaWindow.Weekly, 8000, null), new(QuotaWindow.Monthly, 9000, null)])]);
        var card = Assert.Single(QuotaCardProjection.Project(quota, japanese: false, Fixed));
        Assert.Equal(3, card.Windows.Count);
        Assert.Equal("0%", card.CenterText);
        Assert.Equal(0, card.RingValues[0]);
        Assert.Null(card.RingValues[1]);
        Assert.Equal("—", card.Windows[1].RemainingText);
        Assert.True(card.IsStale);
        Assert.Equal("Cached", card.Windows[0].FreshnessText);
        var fresh = quota with { Providers = [quota.Providers[0] with { Freshness = QuotaFreshness.Fresh, ObservedAt = DateTimeOffset.UtcNow, Windows = [new(QuotaWindow.Session, 5000, DateTimeOffset.UtcNow.AddHours(3))] }] };
        var failedRefreshCard = Assert.Single(QuotaCardProjection.Project(fresh, japanese: false, forceStale: true));
        Assert.True(failedRefreshCard.IsStale);
        Assert.Equal("Stale", failedRefreshCard.FreshnessText);
    }

    [Fact]
    public async Task GetLatestQuotaRecognizesAllSevenDataGroupsAndCopilotUnlimited()
    {
        const string json = """{"snapshots":[{"collectedAt":"2026-11-22T01:00:00Z","providers":[{"provider":"claude","status":"available","freshness":"fresh","observedAt":"2026-11-22T00:59:00Z","windows":[{"window":"session","remainingBasisPoints":5000,"resetsAt":"2026-11-22T06:00:00Z"}]},{"provider":"codex","status":"available","freshness":"fresh","observedAt":"2026-11-22T00:59:00Z","windows":[{"window":"five-hour","remainingBasisPoints":0,"resetsAt":"2026-11-22T06:00:00Z"}]},{"provider":"opencode-go","status":"available","freshness":"fresh","observedAt":"2026-11-22T00:59:00Z","windows":[{"window":"rolling","remainingBasisPoints":null,"resetsAt":null}]},{"provider":"antigravity-gemini","status":"available","freshness":"fresh","observedAt":"2026-11-22T00:59:00Z","windows":[{"window":"five-hour","remainingBasisPoints":7500,"resetsAt":"2026-11-22T06:00:00Z"},{"window":"weekly","remainingBasisPoints":6000,"resetsAt":"2026-11-29T00:00:00Z"}]},{"provider":"antigravity-claude-gpt","status":"available","freshness":"fresh","observedAt":"2026-11-22T00:59:00Z","windows":[{"window":"five-hour","remainingBasisPoints":6500,"resetsAt":"2026-11-22T06:00:00Z"},{"window":"weekly","remainingBasisPoints":5500,"resetsAt":"2026-11-29T00:00:00Z"}]},{"provider":"grok-build","status":"available","freshness":"fresh","observedAt":"2026-11-22T00:59:00Z","windows":[{"window":"weekly","remainingBasisPoints":4000,"resetsAt":"2026-11-29T00:00:00Z"},{"window":"monthly","remainingBasisPoints":8500,"resetsAt":"2026-12-01T00:00:00Z"}]},{"provider":"copilot","status":"available","freshness":"fresh","observedAt":"2026-11-22T00:59:00Z","windows":[{"window":"premium","remainingBasisPoints":null,"unlimited":true,"resetsAt":null},{"window":"chat","remainingBasisPoints":5000,"resetsAt":"2026-11-23T00:00:00Z"},{"window":"completions","remainingBasisPoints":0,"resetsAt":null}]}]}]}""";
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
        var port = new HttpQuotaReadPort(client, new Uri("https://usage.test"), "owner", new TestCredentialProvider());
        var result = await port.ReadLatestAsync(CancellationToken.None);
        Assert.Equal(7, result.Providers.Count);
        Assert.Equal("Antigravity · Gemini models", QuotaCardProjection.Project(result, false, Fixed).Single(card => card.ProviderText.Contains("Gemini", StringComparison.Ordinal)).ProviderText);
        var copilot = Assert.Single(QuotaCardProjection.Project(result, false, Fixed), card => card.Provider == QuotaProvider.Copilot);
        Assert.Equal("Unlimited", copilot.Windows[0].RemainingText);
        Assert.True(copilot.Windows[0].Unlimited);
        Assert.Equal(2, copilot.RingValues.Count);
        Assert.Equal(5000, copilot.RingValues[0]);
        Assert.Equal(0, copilot.RingValues[1]);
    }

    [Fact]
    public async Task UnlimitedFlagIsRejectedForProvidersOtherThanCopilot()
    {
        const string json = """{"snapshots":[{"collectedAt":"2026-11-22T01:00:00Z","providers":[{"provider":"claude","status":"available","freshness":"fresh","observedAt":null,"windows":[{"window":"session","remainingBasisPoints":null,"unlimited":true,"resetsAt":null}]}]}]}""";
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
        var port = new HttpQuotaReadPort(client, new Uri("https://usage.test"), "owner", new TestCredentialProvider());
        await Assert.ThrowsAsync<InvalidOperationException>(() => port.ReadLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LatestQuotaPreservesKnownZeroUnknownAndWindowIdentity()
    {
        const string json = """{"snapshots":[{"collectedAt":"2026-11-22T01:00:00Z","providers":[{"provider":"claude","status":"available","freshness":"fresh","observedAt":"2026-11-22T00:59:00Z","windows":[{"window":"session","remainingBasisPoints":0,"resetsAt":"2026-11-22T06:00:00Z"},{"window":"weekly","remainingBasisPoints":null,"resetsAt":null}]}]}]}""";
        var handler = new StubHandler(request =>
        {
            Assert.Equal("https://usage.test/v1/quota/latest", request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
        using var client = new HttpClient(handler);
        var port = new HttpQuotaReadPort(client, new Uri("https://usage.test"), "owner", new TestCredentialProvider());

        var result = await port.ReadLatestAsync(CancellationToken.None);

        var claude = Assert.Single(result.Providers);
        Assert.Equal(QuotaProvider.Claude, claude.Provider);
        Assert.Equal(0, claude.Windows[0].RemainingBasisPoints);
        Assert.Null(claude.Windows[1].RemainingBasisPoints);
        Assert.Equal(QuotaWindow.Session, claude.Windows[0].Window);
        Assert.Equal(QuotaFreshness.Fresh, claude.Freshness);
    }

    [Fact]
    public async Task SameProviderInSeveralSnapshotsIsShownAsMultipleSourcesNotSilentlyResolved()
    {
        const string json = """{"snapshots":[{"collectedAt":"2026-11-22T01:00:00Z","providers":[{"provider":"claude","status":"available","freshness":"fresh","observedAt":null,"windows":[{"window":"session","remainingBasisPoints":1000,"resetsAt":null}]},{"provider":"codex","status":"available","freshness":"fresh","observedAt":null,"windows":[{"window":"five-hour","remainingBasisPoints":2000,"resetsAt":null}]}]},{"collectedAt":"2026-11-22T02:00:00Z","providers":[{"provider":"claude","status":"available","freshness":"fresh","observedAt":null,"windows":[{"window":"session","remainingBasisPoints":9000,"resetsAt":null}]}]}]}""";
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
        var port = new HttpQuotaReadPort(client, new Uri("https://usage.test"), "owner", new TestCredentialProvider());
        var result = await port.ReadLatestAsync(CancellationToken.None);
        Assert.Equal(2, result.Providers.Count);
        var claude = result.Providers.Single(p => p.Provider == QuotaProvider.Claude);
        Assert.True(claude.MultipleSources);
        Assert.Empty(claude.Windows);
        Assert.False(result.Providers.Single(p => p.Provider == QuotaProvider.Codex).MultipleSources);
        var en = QuotaCardProjection.Project(result, false, Fixed).Single(c => c.Provider == QuotaProvider.Claude);
        Assert.Equal("Multiple sources", en.FreshnessText);
        Assert.Equal("—", en.CenterText);
        Assert.Empty(en.RingValues);
        Assert.Equal("複数の送信元", QuotaCardProjection.Project(result, true).Single(c => c.Provider == QuotaProvider.Claude).FreshnessText);
    }

    [Fact]
    public void WindowPastItsResetTimeIsNeverTheCurrentPrimaryValue()
    {
        var now = DateTimeOffset.Parse("2026-11-22T10:00:00Z");
        var quota = new LatestQuota(now, [new QuotaProviderValue(QuotaProvider.Claude, QuotaProviderStatus.Available, QuotaFreshness.Fresh, now, [
            new(QuotaWindow.Session, 4000, now.AddMinutes(-1)), new(QuotaWindow.Weekly, 7000, now.AddDays(1))])]);
        var card = Assert.Single(QuotaCardProjection.Project(quota, false, now));
        Assert.Equal("—", card.CenterText);
        Assert.Equal("—", card.Windows[0].RemainingText);
        Assert.Null(card.RingValues[0]);
        Assert.Equal("Past reset · awaiting refresh", card.Windows[0].FreshnessText);
        Assert.Equal(7000, card.RingValues[1]);
        Assert.Equal("70%", card.Windows[1].RemainingText);
    }

    [Fact]
    public void UnlimitedOnlyCopilotShowsUnlimitedAndNoZeroOrFullRing()
    {
        var now = DateTimeOffset.Parse("2026-11-22T10:00:00Z");
        var quota = new LatestQuota(now, [new QuotaProviderValue(QuotaProvider.Copilot, QuotaProviderStatus.Available, QuotaFreshness.Fresh, now, [new(QuotaWindow.Premium, null, null, true)])]);
        var card = Assert.Single(QuotaCardProjection.Project(quota, true, now));
        Assert.Equal("無制限", card.CenterText);
        Assert.Empty(card.RingValues);
        Assert.True(card.IsUnlimitedOnly);
        Assert.Equal("Unlimited", Assert.Single(QuotaCardProjection.Project(quota, false, now)).Windows[0].RemainingText);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request));
    }

    private sealed class TestCredentialProvider : IUsageCredentialProvider
    {
        public ValueTask<UsageCredential?> GetCredentialAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<UsageCredential?>(new UsageCredential("owner", "synthetic-id", "synthetic-secret"));
    }
}
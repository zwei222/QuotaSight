using System.Net;
using System.Text;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class QuotaRingsHttpPortTests
{
    private static readonly DateTimeOffset Fixed = DateTimeOffset.Parse("2026-11-22T01:30:00Z");

    private static QuotaProviderValue Claude(QuotaFreshness freshness, DateTimeOffset? observed, DateTimeOffset? collected = null) =>
        new(QuotaProvider.Claude, QuotaProviderStatus.Available, freshness, observed, [new(QuotaWindow.Session, 4200, null)], false, collected);

    [Fact]
    public void MissingObservationFallsBackToCollectionTimeLabelledAsCollected()
    {
        var now = DateTimeOffset.Parse("2026-11-23T00:30:00Z");
        var quota = new LatestQuota(now.AddMinutes(-7), [Claude(QuotaFreshness.Unknown, null)]);
        var card = Assert.Single(QuotaCardProjection.Project(quota, japanese: true, now));
        Assert.Equal("収集 7分前", card.FreshnessText);
        Assert.Equal("Collected 7m ago", Assert.Single(QuotaCardProjection.Project(quota, japanese: false, now)).FreshnessText);
        Assert.Equal("42%", card.CenterText);
        Assert.True(card.IsStale);
    }

    [Fact]
    public void ProviderCollectionTimeWinsOverSectionCollectionTime()
    {
        var now = DateTimeOffset.Parse("2026-11-23T00:30:00Z");
        var quota = new LatestQuota(now.AddMinutes(-1), [Claude(QuotaFreshness.Unknown, null, now.AddMinutes(-20))]);
        Assert.Equal("収集 20分前", Assert.Single(QuotaCardProjection.Project(quota, japanese: true, now)).FreshnessText);
    }

    [Fact]
    public void ObservationTimeStillWinsAndIsNotLabelledAsCollected()
    {
        var now = DateTimeOffset.Parse("2026-11-23T00:30:00Z");
        var quota = new LatestQuota(now.AddMinutes(-1), [Claude(QuotaFreshness.Fresh, now.AddMinutes(-9), now.AddMinutes(-1))]);
        Assert.Equal("9分前", Assert.Single(QuotaCardProjection.Project(quota, japanese: true, now)).FreshnessText);
    }

    [Fact]
    public void CollectionTimeOlderThanTwelveHoursWarnsAndCachedKeepsItsRule()
    {
        var now = DateTimeOffset.Parse("2026-11-23T00:30:00Z");
        var old = Assert.Single(QuotaCardProjection.Project(new LatestQuota(now.AddHours(-13), [Claude(QuotaFreshness.Unknown, null)]), japanese: true, now));
        Assert.Equal("収集 13時間前の値", old.FreshnessText);
        Assert.True(old.IsWarning);
        var cached = Assert.Single(QuotaCardProjection.Project(new LatestQuota(now.AddMinutes(-3), [Claude(QuotaFreshness.Cached, null)]), japanese: true, now));
        Assert.Equal("キャッシュ · 収集 3分前", cached.FreshnessText);
        Assert.False(cached.IsWarning);
    }

    [Fact]
    public void NeitherTimeKeepsUnknownText()
    {
        var now = DateTimeOffset.Parse("2026-11-23T00:30:00Z");
        var card = Assert.Single(QuotaCardProjection.Project(new LatestQuota(DateTimeOffset.MinValue, [Claude(QuotaFreshness.Unknown, null)]), japanese: true, now));
        Assert.Equal("取得時刻不明", card.FreshnessText);
    }

    [Fact]
    public async Task ParserAttachesEachSnapshotCollectionTimeToItsProviders()
    {
        const string json = """{"snapshots":[{"collectedAt":"2026-11-22T01:00:00Z","providers":[{"provider":"claude","status":"available","freshness":"unknown","observedAt":null,"windows":[{"window":"session","remainingBasisPoints":1000,"resetsAt":null}]}]},{"collectedAt":"2026-11-22T02:00:00Z","providers":[{"provider":"codex","status":"available","freshness":"unknown","observedAt":null,"windows":[{"window":"five-hour","remainingBasisPoints":2000,"resetsAt":null}]}]}]}""";
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
        var port = new HttpQuotaReadPort(client, new Uri("https://usage.test"), "owner", new TestCredentialProvider());
        var quota = await port.ReadLatestAsync(CancellationToken.None);
        Assert.Equal(DateTimeOffset.Parse("2026-11-22T01:00:00Z"), quota.Providers.Single(p => p.Provider == QuotaProvider.Claude).CollectedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-22T02:00:00Z"), quota.Providers.Single(p => p.Provider == QuotaProvider.Codex).CollectedAt);
    }

    [Fact]
    public void CenterAndHighlightUseTheSmallestValidFiniteWindow()
    {
        var now = DateTimeOffset.Parse("2026-11-23T00:04:00Z");
        var quota = new LatestQuota(now, [new QuotaProviderValue(QuotaProvider.OpenCodeGo, QuotaProviderStatus.Available, QuotaFreshness.Cached, now.AddMinutes(-18), [
            new(QuotaWindow.Rolling, 10000, now.AddHours(1)),
            new(QuotaWindow.Weekly, 0, now.AddHours(2)),
            new(QuotaWindow.Monthly, 4300, now.AddDays(11))])]);

        var card = Assert.Single(QuotaCardProjection.Project(quota, japanese: true, now));

        Assert.Equal("0%", card.CenterText);
        Assert.Equal("週", card.CenterPeriodText);
        Assert.Equal("キャッシュ · 18分前", card.FreshnessText);
        Assert.Equal("↻ " + now.AddHours(2).ToLocalTime().ToString("M/d HH:mm", System.Globalization.CultureInfo.InvariantCulture), card.Windows.Single(window => window.Window == QuotaWindow.Weekly).ResetText);
    }

    [Fact]
    public void UnlimitedOnlyCenterHasNoQuotaAndFiniteZeroRemainsDistinctFromUnknown()
    {
        var now = DateTimeOffset.Parse("2026-11-23T00:04:00Z");
        var copilot = new LatestQuota(DateTimeOffset.MinValue, [new QuotaProviderValue(QuotaProvider.Copilot, QuotaProviderStatus.Available, QuotaFreshness.Unknown, null, [
            new(QuotaWindow.Premium, null, null, true), new(QuotaWindow.Chat, null, null, true), new(QuotaWindow.Completions, null, null, true)])]);
        var card = Assert.Single(QuotaCardProjection.Project(copilot, japanese: true, now));
        Assert.Equal("無制限", card.CenterText);
        Assert.True(card.IsUnlimitedOnly);
        Assert.Equal("取得時刻不明", card.FreshnessText);
        Assert.All(card.Windows, window => Assert.Equal(string.Empty, window.ResetText));

        var zeroUnknown = new LatestQuota(now, [new QuotaProviderValue(QuotaProvider.Codex, QuotaProviderStatus.Available, QuotaFreshness.Fresh, now, [
            new(QuotaWindow.Session, 0, now.AddHours(1)), new(QuotaWindow.Weekly, null, null)])]);
        var codex = Assert.Single(QuotaCardProjection.Project(zeroUnknown, japanese: true, now));
        Assert.Equal("0%", codex.CenterText);
        Assert.Equal(0, codex.RingValues[0]);
        Assert.Null(codex.RingValues[1]);
        Assert.Equal("0%", codex.Windows[0].RemainingText);
        Assert.Equal("—", codex.Windows[1].RemainingText);
    }

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
    public async Task MissingNullPlaceholdersFromOtherCollectorsAreNotCountedAsSources()
    {
        // quota.test.ts "isolates the retained value by owner and by collector": two
        // collectors publish two snapshots. A collector that does not serve a provider
        // marks it missing with no value; such a marker is not a reading and must never
        // turn the provider into "multiple sources". A provider read by both collectors
        // still is one, and a provider seen by neither read still gets its own card.
        const string json = """
            {"schemaVersion":2,"snapshots":[
              {"collectedAt":"2026-11-22T02:00:00Z","providers":[
                {"provider":"claude","latestStatus":"missing","value":null},
                {"provider":"codex","latestStatus":"available","value":{"source":"latest","collectedAt":"2026-11-22T01:59:00Z","observedAt":"2026-11-22T01:58:00Z","freshness":"fresh","windows":[{"window":"weekly","remainingBasisPoints":7000,"resetsAt":"2026-11-22T09:00:00Z"}]}},
                {"provider":"opencode-go","latestStatus":"available","value":{"source":"latest","collectedAt":"2026-11-22T01:59:00Z","observedAt":"2026-11-22T01:58:00Z","freshness":"fresh","windows":[{"window":"rolling","remainingBasisPoints":9000,"resetsAt":null}]}},
                {"provider":"antigravity-gemini","latestStatus":"missing","value":null},
                {"provider":"antigravity-claude-gpt","latestStatus":"missing","value":null},
                {"provider":"grok-build","latestStatus":"missing","value":null},
                {"provider":"copilot","latestStatus":"missing","value":null}]},
              {"collectedAt":"2026-11-22T01:00:00Z","providers":[
                {"provider":"claude","latestStatus":"available","value":{"source":"latest","collectedAt":"2026-11-22T00:59:00Z","observedAt":"2026-11-22T00:58:00Z","freshness":"fresh","windows":[{"window":"session","remainingBasisPoints":5000,"resetsAt":"2026-11-22T06:00:00Z"}]}},
                {"provider":"codex","latestStatus":"available","value":{"source":"latest","collectedAt":"2026-11-22T00:59:00Z","observedAt":"2026-11-22T00:58:00Z","freshness":"fresh","windows":[{"window":"weekly","remainingBasisPoints":7500,"resetsAt":"2026-11-22T09:00:00Z"}]}},
                {"provider":"opencode-go","latestStatus":"missing","value":null},
                {"provider":"antigravity-gemini","latestStatus":"available","value":{"source":"latest","collectedAt":"2026-11-22T00:59:00Z","observedAt":"2026-11-22T00:58:00Z","freshness":"fresh","windows":[{"window":"five-hour","remainingBasisPoints":7500,"resetsAt":"2026-11-22T06:00:00Z"},{"window":"weekly","remainingBasisPoints":6000,"resetsAt":"2026-11-29T00:00:00Z"}]}},
                {"provider":"antigravity-claude-gpt","latestStatus":"missing","value":null},
                {"provider":"grok-build","latestStatus":"available","value":{"source":"latest","collectedAt":"2026-11-22T00:59:00Z","observedAt":"2026-11-22T00:58:00Z","freshness":"fresh","windows":[{"window":"weekly","remainingBasisPoints":4000,"resetsAt":"2026-11-29T00:00:00Z"},{"window":"monthly","remainingBasisPoints":8500,"resetsAt":"2026-12-01T00:00:00Z"}]}},
                {"provider":"copilot","latestStatus":"available","value":{"source":"latest","collectedAt":"2026-11-22T00:59:00Z","observedAt":"2026-11-22T00:58:00Z","freshness":"fresh","windows":[{"window":"premium","remainingBasisPoints":null,"unlimited":true,"resetsAt":null},{"window":"chat","remainingBasisPoints":5000,"resetsAt":"2026-11-23T00:00:00Z"},{"window":"completions","remainingBasisPoints":0,"resetsAt":null}]}}]}
            ]}
            """;
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
        var port = new HttpQuotaReadPort(client, new Uri("https://usage.test"), "owner", new TestCredentialProvider());
        var result = await port.ReadLatestAsync(CancellationToken.None);

        // Wire: every group keeps one entry, placeholders collapsed onto the real reads.
        Assert.Equal(7, result.Providers.Count);
        var claude = result.Providers.Single(p => p.Provider == QuotaProvider.Claude);
        Assert.False(claude.MultipleSources);
        var claudeWindow = Assert.Single(claude.Windows);
        Assert.Equal(5000, claudeWindow.RemainingBasisPoints);
        Assert.Equal(DateTimeOffset.Parse("2026-11-22T00:59:00Z"), claude.CollectedAt);
        Assert.True(result.Providers.Single(p => p.Provider == QuotaProvider.Codex).MultipleSources);
        var neverRead = result.Providers.Single(p => p.Provider == QuotaProvider.AntigravityClaudeGpt);
        Assert.False(neverRead.MultipleSources);
        Assert.Equal(QuotaProviderStatus.Missing, neverRead.Status);
        Assert.Empty(neverRead.Windows);

        var cards = QuotaCardProjection.Project(result, japanese: true, Fixed);
        Assert.Equal(7, cards.Count);
        var claudeCard = Assert.Single(cards, c => c.Provider == QuotaProvider.Claude);
        Assert.False(claudeCard.IsUnavailable);
        Assert.Equal("50%", claudeCard.CenterText);
        Assert.NotEqual("複数の送信元", claudeCard.FreshnessText);
        var codexCard = Assert.Single(cards, c => c.Provider == QuotaProvider.Codex);
        Assert.True(codexCard.IsUnavailable);
        Assert.Equal("複数の送信元", codexCard.FreshnessText);
        Assert.Equal("—", codexCard.CenterText);
        Assert.False(Assert.Single(cards, c => c.Provider == QuotaProvider.OpenCodeGo).IsUnavailable);
        Assert.False(Assert.Single(cards, c => c.Provider == QuotaProvider.AntigravityGemini).IsUnavailable);
        var neverReadCard = Assert.Single(cards, c => c.Provider == QuotaProvider.AntigravityClaudeGpt);
        Assert.Equal("値をまだ\n取得していません", neverReadCard.UnavailableMessage);
        Assert.Equal("未取得", neverReadCard.FreshnessText);
        Assert.False(Assert.Single(cards, c => c.Provider == QuotaProvider.GrokBuild).IsUnavailable);
        var copilotCard = Assert.Single(cards, c => c.Provider == QuotaProvider.Copilot);
        Assert.False(copilotCard.IsUnavailable);
        Assert.Equal("0%", copilotCard.CenterText); // the smallest valid finite window (completions) is the primary
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
            Assert.Equal("https://usage.test/v1/quota/latest?schema=2", request.RequestUri!.AbsoluteUri);
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
        Assert.Equal("70%", card.CenterText); // the expired window is skipped; the smallest VALID window is shown
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
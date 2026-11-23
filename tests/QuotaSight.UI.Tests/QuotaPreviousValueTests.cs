using System.Globalization;
using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using QuotaSight.Infrastructure;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// The schema=2 quota wire contract and the "previous value" display: a value the
/// collector kept from an earlier successful read is shown, but never as the
/// current value, and its time is labelled so a collection time is never called
/// an observation time.
/// </summary>
public sealed class QuotaPreviousValueTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-23T01:10:00Z");

    private static string Stamp(DateTimeOffset at) => at.ToLocalTime().ToString("M/d HH:mm", CultureInfo.InvariantCulture);

    private const string Schema2Mixed = """
        {"schemaVersion":2,"snapshots":[{"collectedAt":"2026-11-23T01:00:00Z","providers":[
          {"provider":"claude","latestStatus":"available","value":{"source":"latest","collectedAt":"2026-11-23T00:59:00Z","observedAt":"2026-11-23T00:58:00Z","freshness":"fresh","windows":[{"window":"session","remainingBasisPoints":4200,"resetsAt":"2026-11-23T06:00:00Z"}]}},
          {"provider":"codex","latestStatus":"available","value":{"source":"previous","collectedAt":"2026-11-22T13:40:00Z","observedAt":"2026-11-22T13:39:00Z","freshness":"cached","windows":[{"window":"five-hour","remainingBasisPoints":3200,"resetsAt":null}]}},
          {"provider":"grok-build","latestStatus":"available","value":{"source":"previous","collectedAt":"2026-11-22T09:00:00Z","observedAt":null,"freshness":"unknown","windows":[{"window":"weekly","remainingBasisPoints":5500,"resetsAt":null}]}},
          {"provider":"opencode-go","latestStatus":"available","value":{"source":"previous","collectedAt":null,"observedAt":null,"freshness":"unknown","windows":[{"window":"rolling","remainingBasisPoints":7100,"resetsAt":null}]}},
          {"provider":"copilot","latestStatus":"missing","value":null}
        ]}]}
        """;

    private static HttpClient Stub(string json) => new(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));

    private static HttpQuotaReadPort Port(HttpClient client) => new(client, new Uri("https://usage.test"), "owner", new TestCredentialProvider());

    // ---------- wire: schema=2 ----------

    [Fact]
    public async Task RequestsSchema2AndParsesTheMixedFixture()
    {
        string? requested = null;
        var handler = new StubHandler(request =>
        {
            requested = request.RequestUri!.AbsoluteUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Schema2Mixed, Encoding.UTF8, "application/json") };
        });
        using var client = new HttpClient(handler);
        var quota = await Port(client).ReadLatestAsync(CancellationToken.None);

        Assert.Equal("https://usage.test/v1/quota/latest?schema=2", requested);
        Assert.Equal(DateTimeOffset.Parse("2026-11-23T01:00:00Z"), quota.CollectedAt);
        Assert.Equal(5, quota.Providers.Count);

        var claude = quota.Providers.Single(p => p.Provider == QuotaProvider.Claude);
        Assert.False(claude.IsPrevious);
        Assert.Equal(QuotaFreshness.Fresh, claude.Freshness);
        Assert.Equal(DateTimeOffset.Parse("2026-11-23T00:59:00Z"), claude.CollectedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-23T00:58:00Z"), claude.ObservedAt);

        var codex = quota.Providers.Single(p => p.Provider == QuotaProvider.Codex);
        Assert.True(codex.IsPrevious);
        Assert.Equal(DateTimeOffset.Parse("2026-11-22T13:40:00Z"), codex.CollectedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-22T13:39:00Z"), codex.ObservedAt);
        Assert.Equal(3200, Assert.Single(codex.Windows).RemainingBasisPoints);

        var grok = quota.Providers.Single(p => p.Provider == QuotaProvider.GrokBuild);
        Assert.True(grok.IsPrevious);
        Assert.Null(grok.ObservedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-22T09:00:00Z"), grok.CollectedAt);

        var opencode = quota.Providers.Single(p => p.Provider == QuotaProvider.OpenCodeGo);
        Assert.True(opencode.IsPrevious);
        Assert.Null(opencode.CollectedAt);
        Assert.Null(opencode.ObservedAt);

        var copilot = quota.Providers.Single(p => p.Provider == QuotaProvider.Copilot);
        Assert.Equal(QuotaProviderStatus.Missing, copilot.Status);
        Assert.Empty(copilot.Windows);
    }

    [Fact]
    public async Task OldWorkerThatIgnoresSchema2AndAnswersStrictV1IsStillAccepted()
    {
        const string json = """{"snapshots":[{"collectedAt":"2026-11-22T01:00:00Z","providers":[{"provider":"claude","status":"available","freshness":"fresh","observedAt":"2026-11-22T00:59:00Z","windows":[{"window":"session","remainingBasisPoints":5000,"resetsAt":"2026-11-22T06:00:00Z"}]}]}]}""";
        string? requested = null;
        var handler = new StubHandler(request =>
        {
            requested = request.RequestUri!.AbsoluteUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
        using var client = new HttpClient(handler);
        var quota = await Port(client).ReadLatestAsync(CancellationToken.None);

        Assert.Equal("https://usage.test/v1/quota/latest?schema=2", requested);
        var claude = Assert.Single(quota.Providers);
        Assert.False(claude.IsPrevious);
        Assert.Equal(DateTimeOffset.Parse("2026-11-22T01:00:00Z"), claude.CollectedAt);
    }

    [Theory]
    [InlineData("""{"schemaVersion":3,"snapshots":[{"collectedAt":"2026-11-23T01:00:00Z","providers":[]}]}""")]
    [InlineData("""{"schemaVersion":2,"snapshots":[{"collectedAt":"2026-11-23T01:00:00Z","providers":[{"provider":"claude","latestStatus":"available","value":{"source":"newest","collectedAt":null,"observedAt":null,"freshness":"fresh","windows":[]}}]}]}""")]
    [InlineData("""{"schemaVersion":2,"snapshots":[{"collectedAt":"2026-11-23T01:00:00Z","providers":[{"provider":"claude","value":null}]}]}""")]
    [InlineData("""{"snapshots":[{"collectedAt":"2026-11-23T01:00:00Z","providers":[{"provider":"claude","status":"available","freshness":"fresh","observedAt":null,"windows":[]}],"schemaVersion":2}]}""")]
    public async Task DeviationsFromTheSchema2ContractAreRefused(string json)
    {
        using var client = Stub(json);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Port(client).ReadLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UnavailableWithNoValueKeepsItsOwnCardCopy()
    {
        const string json = """{"schemaVersion":2,"snapshots":[{"collectedAt":"2026-11-23T01:00:00Z","providers":[{"provider":"claude","latestStatus":"unavailable","value":null}]}]}""";
        using var client = Stub(json);
        var quota = await Port(client).ReadLatestAsync(CancellationToken.None);
        var card = Assert.Single(QuotaCardProjection.Project(quota, japanese: true, Now));
        Assert.True(card.IsUnavailable);
        Assert.Equal("残量を\n取得できませんでした", card.UnavailableMessage);
        Assert.Equal("Couldn't read\nthe quota", Assert.Single(QuotaCardProjection.Project(quota, japanese: false, Now)).UnavailableMessage);
    }

    [Fact]
    public async Task MissingLatestReadStillServesTheRetainedPreviousValue()
    {
        const string json = """{"schemaVersion":2,"snapshots":[{"collectedAt":"2026-11-23T01:00:00Z","providers":[{"provider":"claude","latestStatus":"missing","value":{"source":"previous","collectedAt":"2026-11-22T13:40:00Z","observedAt":"2026-11-22T13:39:00Z","freshness":"cached","windows":[{"window":"session","remainingBasisPoints":3200,"resetsAt":null}]}}]}]}""";
        using var client = Stub(json);
        var quota = await Port(client).ReadLatestAsync(CancellationToken.None);
        var card = Assert.Single(QuotaCardProjection.Project(quota, japanese: true, Now));
        Assert.False(card.IsUnavailable);
        Assert.Equal($"前回値 · 最終取得 {Stamp(DateTimeOffset.Parse("2026-11-22T13:39:00Z"))}", card.FreshnessText);
        Assert.Equal("—", Assert.Single(card.Windows).RemainingText);
        Assert.Equal("リセット時刻不明", Assert.Single(card.Windows).ResetText);
        Assert.Equal("前回 32%", Assert.Single(card.Windows).PreviousText);
    }

    [Fact]
    public async Task AvailableLatestStatusWithNoValueIsNotYetCollectedNotAnError()
    {
        const string json = """{"schemaVersion":2,"snapshots":[{"collectedAt":"2026-11-23T01:00:00Z","providers":[{"provider":"claude","latestStatus":"available","value":null}]}]}""";
        using var client = Stub(json);
        var quota = await Port(client).ReadLatestAsync(CancellationToken.None);
        var card = Assert.Single(QuotaCardProjection.Project(quota, japanese: true, Now));
        Assert.True(card.IsUnavailable);
        Assert.Equal("値をまだ\n取得していません", card.UnavailableMessage);
        Assert.Equal("未取得", card.FreshnessText);
    }

    // ---------- projection: previous values ----------

    private static LatestQuota MixedQuota => new(
        DateTimeOffset.Parse("2026-11-23T01:00:00Z"),
        [
            new(QuotaProvider.Claude, QuotaProviderStatus.Available, QuotaFreshness.Fresh, Now.AddMinutes(-2), [new(QuotaWindow.Session, 4200, Now.AddHours(5))]),
            new(QuotaProvider.Codex, QuotaProviderStatus.Available, QuotaFreshness.Cached, DateTimeOffset.Parse("2026-11-22T13:39:00Z"), [new(QuotaWindow.FiveHour, 3200, null)], false, DateTimeOffset.Parse("2026-11-22T13:40:00Z"), true, true),
            new(QuotaProvider.GrokBuild, QuotaProviderStatus.Available, QuotaFreshness.Unknown, null, [new(QuotaWindow.Weekly, 5500, null)], false, DateTimeOffset.Parse("2026-11-22T09:00:00Z"), true, true),
            new(QuotaProvider.OpenCodeGo, QuotaProviderStatus.Available, QuotaFreshness.Unknown, null, [new(QuotaWindow.Rolling, 7100, null)], false, null, true, true),
            new(QuotaProvider.Copilot, QuotaProviderStatus.Available, QuotaFreshness.Cached, DateTimeOffset.Parse("2026-11-22T22:39:00Z"), [new(QuotaWindow.Premium, 6200, null)], false, DateTimeOffset.Parse("2026-11-22T22:40:00Z"), true, true),
        ]);

    [Fact]
    public void PreviousValueWithObservationTimeIsLabelledPreviousAndFetched()
    {
        var card = Assert.Single(QuotaCardProjection.Project(MixedQuota, japanese: true, Now), c => c.Provider == QuotaProvider.Codex);
        Assert.Equal($"前回値 · 最終取得 {Stamp(DateTimeOffset.Parse("2026-11-22T13:39:00Z"))}", card.FreshnessText);
        Assert.Equal($"Previous · fetched {Stamp(DateTimeOffset.Parse("2026-11-22T13:39:00Z"))}", Assert.Single(QuotaCardProjection.Project(MixedQuota, japanese: false, Now), c => c.Provider == QuotaProvider.Codex).FreshnessText);
        Assert.True(card.IsWarning);
        Assert.True(card.IsStale);
    }

    [Fact]
    public void PreviousValueWithoutObservationTimeLabelsTheCollectionTime()
    {
        var card = Assert.Single(QuotaCardProjection.Project(MixedQuota, japanese: true, Now), c => c.Provider == QuotaProvider.GrokBuild);
        Assert.Equal($"前回値 · 最終収集 {Stamp(DateTimeOffset.Parse("2026-11-22T09:00:00Z"))}", card.FreshnessText);
        Assert.Equal($"Previous · collected {Stamp(DateTimeOffset.Parse("2026-11-22T09:00:00Z"))}", Assert.Single(QuotaCardProjection.Project(MixedQuota, japanese: false, Now), c => c.Provider == QuotaProvider.GrokBuild).FreshnessText);
    }

    [Fact]
    public void PreviousValueWithoutAnyTimeStaysUnknownAndNeverBorrowsTheSnapshotTime()
    {
        var card = Assert.Single(QuotaCardProjection.Project(MixedQuota, japanese: true, Now), c => c.Provider == QuotaProvider.OpenCodeGo);
        Assert.Equal("前回値 · 取得時刻不明", card.FreshnessText);
        Assert.Equal("Previous · time unknown", Assert.Single(QuotaCardProjection.Project(MixedQuota, japanese: false, Now), c => c.Provider == QuotaProvider.OpenCodeGo).FreshnessText);
    }

    [Fact]
    public void LatestValueKeepsTheExistingFreshnessLanguage()
    {
        var card = Assert.Single(QuotaCardProjection.Project(MixedQuota, japanese: true, Now), c => c.Provider == QuotaProvider.Claude);
        Assert.Equal("2分前", card.FreshnessText);
        Assert.False(card.IsWarning);
    }

    [Fact]
    public void PreviousWindowWithUnknownResetIsNeverTheCurrentValueButShowsThePercentage()
    {
        var card = Assert.Single(QuotaCardProjection.Project(MixedQuota, japanese: true, Now), c => c.Provider == QuotaProvider.Codex);
        var window = Assert.Single(card.Windows);
        Assert.Equal("—", window.RemainingText);
        Assert.Equal("リセット時刻不明", window.ResetText);
        Assert.Equal("前回 32%", window.PreviousText);
        Assert.True(window.HasPreviousText);
        Assert.Null(card.RingValues.Single());
        Assert.Equal("—", card.CenterText);
        Assert.Equal(string.Empty, card.CenterPeriodText);
        Assert.Equal("muted", window.ValueTone);
        Assert.True(card.IsStale);

        var en = Assert.Single(QuotaCardProjection.Project(MixedQuota, japanese: false, Now), c => c.Provider == QuotaProvider.Codex);
        Assert.Equal("Reset unknown", en.Windows.Single().ResetText);
        Assert.Equal("was 32%", en.Windows.Single().PreviousText);
    }

    [Fact]
    public void EveryWindowPastItsResetKeepsTheCenterDashAndNoPeriodLabel()
    {
        var quota = new LatestQuota(Now, [new QuotaProviderValue(QuotaProvider.Claude, QuotaProviderStatus.Available, QuotaFreshness.Fresh, Now, [
            new(QuotaWindow.Session, 4000, Now.AddMinutes(-1)), new(QuotaWindow.Weekly, 7000, Now.AddMinutes(-2))])]);
        var card = Assert.Single(QuotaCardProjection.Project(quota, japanese: true, Now));
        Assert.Equal("—", card.CenterText); // the centre keeps the honest dash when nothing is current
        Assert.Equal(string.Empty, card.CenterPeriodText);
        Assert.All(card.Windows, window => Assert.Equal("—", window.RemainingText));
        Assert.Equal("前回 40%", card.Windows[0].PreviousText);
        Assert.Equal("前回 70%", card.Windows[1].PreviousText);
    }

    [Fact]
    public void ExpiredFiniteWindowKeepsTheCurrentValueADashAndShowsThePreviousPercentageAsReference()
    {
        var quota = new LatestQuota(Now, [new QuotaProviderValue(QuotaProvider.Claude, QuotaProviderStatus.Available, QuotaFreshness.Fresh, Now, [
            new(QuotaWindow.Session, 4000, Now.AddMinutes(-1)), new(QuotaWindow.Weekly, 7000, Now.AddDays(1))])]);
        var card = Assert.Single(QuotaCardProjection.Project(quota, japanese: true, Now));
        var expired = card.Windows[0];
        Assert.Equal("—", expired.RemainingText);
        Assert.Equal("リセット済み", expired.ResetText);
        Assert.Equal("前回 40%", expired.PreviousText);
        Assert.Equal("Past reset", Assert.Single(QuotaCardProjection.Project(quota, japanese: false, Now)).Windows[0].ResetText);
        Assert.Equal("was 40%", Assert.Single(QuotaCardProjection.Project(quota, japanese: false, Now)).Windows[0].PreviousText);
        Assert.Null(card.RingValues[0]);
        Assert.Equal(7000, card.RingValues[1]);
        Assert.Equal("70%", card.CenterText);
    }

    [Fact]
    public void ExpiredWindowWithoutABasisPointKeepsTheAwaitingRefreshCopy()
    {
        var quota = new LatestQuota(Now, [new QuotaProviderValue(QuotaProvider.Claude, QuotaProviderStatus.Available, QuotaFreshness.Fresh, Now, [new(QuotaWindow.Session, null, Now.AddMinutes(-1))])]);
        var window = Assert.Single(Assert.Single(QuotaCardProjection.Project(quota, japanese: true, Now)).Windows);
        Assert.Equal("—", window.RemainingText);
        Assert.Equal("リセット済み・更新待ち", window.ResetText);
    }

    [Fact]
    public void PreviousWindowWithAFutureResetStillShowsItsPercentage()
    {
        var quota = new LatestQuota(Now, [new QuotaProviderValue(QuotaProvider.Claude, QuotaProviderStatus.Available, QuotaFreshness.Cached, Now.AddMinutes(-90), [new(QuotaWindow.Session, 4800, Now.AddHours(2))], false, null, true, true)]);
        var card = Assert.Single(QuotaCardProjection.Project(quota, japanese: true, Now));
        var window = Assert.Single(card.Windows);
        Assert.Equal("48%", window.RemainingText);
        Assert.False(window.IsReference);
        Assert.Equal(4800, Assert.Single(card.RingValues));
        Assert.Equal("48%", card.CenterText);
        Assert.StartsWith("前回値 · ", card.FreshnessText);
    }

    [Fact]
    public void MissingLatestStatusIsDistinctFromAnUnsuccessfulRead()
    {
        var quota = new LatestQuota(Now, [new QuotaProviderValue(QuotaProvider.Copilot, QuotaProviderStatus.Missing, QuotaFreshness.Unknown, null, [], false, null, false, true)]);
        var card = Assert.Single(QuotaCardProjection.Project(quota, japanese: true, Now));
        Assert.True(card.IsUnavailable);
        Assert.Equal("値をまだ\n取得していません", card.UnavailableMessage);
        Assert.Equal("未取得", card.FreshnessText);
        var en = Assert.Single(QuotaCardProjection.Project(quota, japanese: false, Now));
        Assert.Equal("No value\ncollected yet", en.UnavailableMessage);
        Assert.Equal("Not collected yet", en.FreshnessText);
    }

    [Fact]
    public void MultipleSourcesAndSelectionStillWin()
    {
        var quota = new LatestQuota(Now, [
            new QuotaProviderValue(QuotaProvider.Claude, QuotaProviderStatus.Unavailable, QuotaFreshness.Unknown, null, [], true),
            new(QuotaProvider.Codex, QuotaProviderStatus.Available, QuotaFreshness.Fresh, Now.AddMinutes(-1), [new(QuotaWindow.FiveHour, 3200, null)], false, null, true, true)]);
        var cards = QuotaCardProjection.Project(quota, japanese: true, Now);
        Assert.Equal("複数の送信元", cards[0].FreshnessText);
        Assert.StartsWith("前回値 · ", cards[1].FreshnessText);
    }

    // ---------- headless UI: the label reaches the screens ----------

    [AvaloniaFact]
    public async Task MainScreenShowsThePreviousValueLabelWithoutBreakingTheCard()
    {
        using var vm = new MainViewModel(timeProvider: new FixedTime(Now), quotaReadPort: new FixedPort(MixedQuota));
        vm.SetSettings(new AppSettingsDto(Language: "Japanese", QuotaProviders: ["claude", "codex", "copilot", "grok-build", "opencode-go"]), persist: false);
        var window = new MainWindow(vm) { Width = 1100, Height = 800 };
        try
        {
            window.Show();
            await vm.InitializeAsync(TestContext.Current.CancellationToken);
            window.UpdateLayout();
            var codexCard = vm.QuotaCards.Single(card => card.Provider == QuotaProvider.Codex);
            var tiles = window.GetVisualDescendants().OfType<Border>().Where(border => border.DataContext is QuotaProviderCard && border.Classes.Contains("tile")).ToArray();
            Assert.Equal(5, tiles.Length);
            Assert.All(tiles, tile => Assert.Equal(158, (int)Math.Round(tile.Bounds.Height)));
            var badge = window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.IsEffectivelyVisible && text.DataContext is QuotaProviderCard { Provider: QuotaProvider.Codex } bound && text.Text == bound.FreshnessBadgeText);
            Assert.True(badge.Bounds.Height <= 24, "the previous-value badge stays on one line");
            // Width below its own cap proves the badge was not ellipsized: the badge fits as drawn.
            Assert.True(badge.Bounds.Width is > 20 and < 80, $"the previous-value badge fits without trimming ({badge.Bounds.Width}px)");
            var time = window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.IsEffectivelyVisible && text.DataContext is QuotaProviderCard { Provider: QuotaProvider.Codex } bound && text.Text == bound.FreshnessTimeText);
            Assert.True(time.Bounds.Height <= 16, "the previous-value time stays on one line");
            Assert.True(time.Bounds.Width is > 60 and < 320, $"the explicit previous-value time fits without trimming ({time.Bounds.Width}px)");
            var codexTile = tiles.Single(tile => ((QuotaProviderCard)tile.DataContext!).Provider == QuotaProvider.Codex);
            var dash = codexTile.GetVisualDescendants().OfType<TextBlock>().Single(text => text.IsEffectivelyVisible && text.DataContext is QuotaCardWindow { IsReference: true } && text.Text == "—");
            Assert.True(dash.IsEffectivelyVisible);
            var previous = codexTile.GetVisualDescendants().OfType<TextBlock>().Single(text => text.IsEffectivelyVisible && text.DataContext is QuotaCardWindow { IsReference: true } boundWindow && text.Text == boundWindow.PreviousText);
            Assert.True(previous.IsEffectivelyVisible, "the previous percentage stays readable beside the dash");
            Assert.False(previous.Classes.Contains("normal") || previous.Classes.Contains("attention") || previous.Classes.Contains("danger"), "the previous percentage must not borrow the current value's tone");
            var reset = codexTile.GetVisualDescendants().OfType<TextBlock>().Single(text => text.IsEffectivelyVisible && text.DataContext is QuotaCardWindow { IsReference: true } boundWindow && text.Text == boundWindow.ResetText);
            Assert.True(reset.IsEffectivelyVisible);
        }
        finally { window.Close(); vm.Dispose(); }
    }

    [AvaloniaFact]
    public async Task CompactWindowShowsThePreviousValueLabelWithoutBreakingTheCard()
    {
        using var vm = new MainViewModel(timeProvider: new FixedTime(Now), quotaReadPort: new FixedPort(MixedQuota));
        vm.SetSettings(new AppSettingsDto(Language: "Japanese", QuotaProviders: ["claude", "codex", "copilot", "grok-build", "opencode-go"]), persist: false);
        var window = new CompactQuotaWindow(vm);
        try
        {
            window.Show();
            await vm.InitializeAsync(TestContext.Current.CancellationToken);
            window.UpdateLayout();
            var codexCard = vm.QuotaCards.Single(card => card.Provider == QuotaProvider.Codex);
            var tiles = window.GetVisualDescendants().OfType<Border>().Where(border => border.DataContext is QuotaProviderCard && border.Classes.Contains("tokscale")).ToArray();
            Assert.Equal(5, tiles.Length);
            Assert.All(tiles, tile => Assert.Equal(118, (int)Math.Round(tile.Bounds.Height)));
            var compactLabel = window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.IsEffectivelyVisible && text.DataContext is QuotaProviderCard { Provider: QuotaProvider.Codex } bound && text.Text == bound.FreshnessText);
            Assert.True(compactLabel.Bounds.Height <= 20, "the compact previous-value label stays on one line");
            Assert.True(compactLabel.Bounds.Width is > 100 and < 320, $"the compact previous-value label fits without trimming ({compactLabel.Bounds.Width}px)");
            var compactTile = tiles.Single(tile => ((QuotaProviderCard)tile.DataContext!).Provider == QuotaProvider.Codex);
            var compactPrevious = compactTile.GetVisualDescendants().OfType<TextBlock>().Single(text => text.IsEffectivelyVisible && text.DataContext is QuotaCardWindow { IsReference: true } boundWindow && text.Text == boundWindow.PreviousText);
            Assert.True(compactPrevious.IsEffectivelyVisible, "the compact previous percentage stays readable beside the dash");
            Assert.False(compactPrevious.Classes.Contains("normal") || compactPrevious.Classes.Contains("attention") || compactPrevious.Classes.Contains("danger"), "the previous percentage must not borrow the current value's tone");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == codexCard.Windows.Single().ResetText);
        }
        finally { window.Close(); vm.Dispose(); }
    }

    [AvaloniaFact]
    public async Task PreviousValueScreensAreCapturedForEvidence()
    {
        var dir = Environment.GetEnvironmentVariable("QUOTASIGHT_UI_PNG_DIR");
        using var vm = new MainViewModel(timeProvider: new FixedTime(Now), quotaReadPort: new FixedPort(MixedQuota));
        vm.SetSettings(new AppSettingsDto(Theme: "Dark", Language: "Japanese", QuotaProviders: ["claude", "codex", "copilot", "grok-build", "opencode-go"]), persist: false);
        var main = new MainWindow(vm) { Width = 1100, Height = 800 };
        var compact = new CompactQuotaWindow(vm);
        try
        {
            main.Show();
            compact.Show();
            await vm.InitializeAsync(TestContext.Current.CancellationToken);
            main.UpdateLayout();
            compact.UpdateLayout();
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
                using var mainBitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(1100, 800));
                mainBitmap.Render(main);
                mainBitmap.Save(Path.Combine(dir, "previous-value-main-1100.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                using var compactBitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(410, 700));
                compactBitmap.Render(compact);
                compactBitmap.Save(Path.Combine(dir, "previous-value-compact-410.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally { compact.Close(); main.Close(); vm.Dispose(); }
    }

    // ---------- truncation: names, times and previous percentages stay readable ----------

    private static readonly QuotaProvider[] TruncationProviders =
        [QuotaProvider.Claude, QuotaProvider.Codex, QuotaProvider.Copilot, QuotaProvider.GrokBuild, QuotaProvider.OpenCodeGo];

    [AvaloniaFact]
    public async Task MainWindowKeepsNamesFreshnessAndResetLinesReadableAt1100()
    {
        await AssertMainWindowReadable(1100, japanese: true);
    }

    [AvaloniaFact]
    public async Task MainWindowKeepsNamesFreshnessAndResetLinesReadableAt420()
    {
        await AssertMainWindowReadable(420, japanese: true);
    }

    [AvaloniaFact]
    public async Task CompactWindowKeepsNamesFreshnessAndResetLinesReadable()
    {
        await AssertCompactWindowReadable(japanese: true);
    }

    [AvaloniaFact]
    public async Task EnglishWindowsKeepNamesFreshnessAndResetLinesReadable()
    {
        await AssertMainWindowReadable(1100, japanese: false);
        await AssertCompactWindowReadable(japanese: false);
    }

    private static void AssertUntrimmed(TextBlock block, Panel host, Func<(double font, FontWeight weight, string text)> twin, string what, double tileWidth)
    {
        var (font, weight, text) = twin();
        var needs = DesiredWidthTwin(host, text, font, weight, block.FontFamily);
        Assert.True(block.TextLayout.TextLines.Count == 1 && block.Bounds.Width >= needs - 1,
            $"{what} '{text}' is cut: drawn {block.Bounds.Width:0.#}px, needs {needs:0.#}px (tile {tileWidth:0.#}px)");
    }

    private static double DesiredWidthTwin(Panel host, string text, double font, FontWeight weight, FontFamily family)
    {
        var twin = new TextBlock { Text = text, FontSize = font, FontWeight = weight, FontFamily = family };
        host.Children.Add(twin);
        twin.Measure(new Size(10000, 100));
        var desired = twin.DesiredSize.Width;
        host.Children.Remove(twin);
        return desired;
    }

    private static async Task AssertMainWindowReadable(int width, bool japanese)
    {
        using var vm = new MainViewModel(timeProvider: new FixedTime(Now), quotaReadPort: new FixedPort(MixedQuota));
        vm.SetSettings(new AppSettingsDto(Language: japanese ? "Japanese" : "English", QuotaProviders: ["claude", "codex", "copilot", "grok-build", "opencode-go"]), persist: false);
        var window = new MainWindow(vm) { Width = width, Height = 800 };
        try
        {
            window.Show();
            await vm.InitializeAsync(TestContext.Current.CancellationToken);
            window.UpdateLayout();
            var tiles = window.GetVisualDescendants().OfType<Border>().Where(border => border.DataContext is QuotaProviderCard && border.Classes.Contains("tile")).ToArray();
            Assert.Equal(5, tiles.Length);
            Assert.All(tiles, tile => Assert.Equal(158, (int)Math.Round(tile.Bounds.Height)));
            var texts = window.GetVisualDescendants().OfType<TextBlock>().ToArray();
            var host = (Panel)window.Content!;
            foreach (var provider in TruncationProviders)
            {
                var card = vm.QuotaCards.Single(c => c.Provider == provider);
                var tile = tiles.Single(t => ((QuotaProviderCard)t.DataContext!).Provider == provider);
                var name = texts.FirstOrDefault(t => t.IsEffectivelyVisible && t.DataContext is QuotaProviderCard { Provider: var p } && p == provider && t.Text == card.ProviderText);
                if (name is null)
                {
                    var seen = string.Join(" | ", texts.Where(t => t.DataContext is QuotaProviderCard { Provider: var q } && q == provider).Select(t => $"'{t.Text}' visible={t.IsEffectivelyVisible}"));
                    Assert.Fail($"no visible name block for {provider}: [{seen}]");
                }
                AssertUntrimmed(name, host, () => (15, FontWeight.SemiBold, card.ProviderText), $"{card.ProviderText} name", tile.Bounds.Width);
                if (card.HasFreshnessTime)
                {
                    var badge = texts.Single(t => t.IsEffectivelyVisible && t.DataContext is QuotaProviderCard { Provider: var bp } && bp == provider && t.Text == card.FreshnessBadgeText);
                    AssertUntrimmed(badge, host, () => (11, FontWeight.Normal, card.FreshnessBadgeText), "badge", tile.Bounds.Width);
                    var time = texts.Single(t => t.IsEffectivelyVisible && t.DataContext is QuotaProviderCard { Provider: var tp } && tp == provider && t.Text == card.FreshnessTimeText);
                    AssertUntrimmed(time, host, () => (11, FontWeight.Normal, card.FreshnessTimeText), "time", tile.Bounds.Width);
                }
                else
                {
                    var freshness = texts.Single(t => t.IsEffectivelyVisible && t.DataContext is QuotaProviderCard { Provider: var fp } && fp == provider && t.Text == card.FreshnessText);
                    AssertUntrimmed(freshness, host, () => (11, FontWeight.Normal, card.FreshnessText), "freshness", tile.Bounds.Width);
                }
                foreach (var windowRow in card.Windows)
                {
                    // The period name must be read in full: no ellipsis may eat into
                    // プレミアム or ローリング just because the previous percentage
                    // also shares the row's two lines.
                    var label = texts.Single(t => t.IsEffectivelyVisible && t.DataContext is QuotaCardWindow { Window: var lw } && lw == windowRow.Window && t.Text == windowRow.WindowText);
                    AssertUntrimmed(label, host, () => (12, windowRow.IsEmphasized ? FontWeight.SemiBold : FontWeight.Normal, windowRow.WindowText), $"{provider} {windowRow.WindowText} period name", tile.Bounds.Width);
                    if (windowRow.HasPreviousText)
                    {
                        var previousShare = texts.Single(t => t.IsEffectivelyVisible && t.DataContext is QuotaCardWindow { Window: var pw2 } && pw2 == windowRow.Window && t.Text == windowRow.PreviousText);
                        AssertUntrimmed(previousShare, host, () => (11, FontWeight.Normal, windowRow.PreviousText), $"{provider} previous '{windowRow.PreviousText}'", tile.Bounds.Width);
                    }
                }
                foreach (var windowRow in card.Windows.Where(row => row.ResetText.Length > 0))
                {
                    var dash = texts.Single(t => t.IsEffectivelyVisible && t.DataContext is QuotaCardWindow { Window: var dw } && dw == windowRow.Window && t.Text == windowRow.RemainingText);
                    Assert.True(dash.IsEffectivelyVisible, $"the value '{windowRow.RemainingText}' must stay visible");
                    if (windowRow.HasPreviousText)
                    {
                        var previous = texts.Single(t => t.IsEffectivelyVisible && t.DataContext is QuotaCardWindow { Window: var pw } && pw == windowRow.Window && t.Text == windowRow.PreviousText);
                        Assert.True(previous.Classes.Contains("quota-previous") && !previous.Classes.Contains("normal") && !previous.Classes.Contains("attention") && !previous.Classes.Contains("danger"), $"the previous percentage '{windowRow.PreviousText}' must not borrow the current value's tone");
                    }
                    var reset = texts.Single(t => t.IsEffectivelyVisible && t.DataContext is QuotaCardWindow { Window: var rw } && rw == windowRow.Window && t.Text == windowRow.ResetText);
                    AssertUntrimmed(reset, host, () => (11, FontWeight.Normal, windowRow.ResetText), "reset", tile.Bounds.Width);
                }
                // A value that is no longer current keeps the honest dash in the ring's
                // centre: the retained percentage is never shown as if it were current.
                if (card.Windows.Count > 0 && card.Windows.All(window => window.IsReference || window.Expired))
                {
                    var centerDash = tile.GetVisualDescendants().OfType<TextBlock>().Single(t => t.IsEffectivelyVisible && t.DataContext is QuotaProviderCard { Provider: var cpc } && cpc == provider && t.Text == card.CenterText && t.Classes.Contains("quota-center"));
                    Assert.Equal("—", centerDash.Text);
                }
            }
        }
        finally { window.Close(); vm.Dispose(); }
    }

    private static async Task AssertCompactWindowReadable(bool japanese)
    {
        using var vm = new MainViewModel(timeProvider: new FixedTime(Now), quotaReadPort: new FixedPort(MixedQuota));
        vm.SetSettings(new AppSettingsDto(Language: japanese ? "Japanese" : "English", QuotaProviders: ["claude", "codex", "copilot", "grok-build", "opencode-go"]), persist: false);
        var window = new CompactQuotaWindow(vm);
        try
        {
            window.Show();
            await vm.InitializeAsync(TestContext.Current.CancellationToken);
            window.UpdateLayout();
            var tiles = window.GetVisualDescendants().OfType<Border>().Where(border => border.DataContext is QuotaProviderCard && border.Classes.Contains("tokscale")).ToArray();
            Assert.Equal(5, tiles.Length);
            Assert.All(tiles, tile => Assert.Equal(118, (int)Math.Round(tile.Bounds.Height)));
            var texts = window.GetVisualDescendants().OfType<TextBlock>().ToArray();
            var host = (Panel)window.Content!;
            foreach (var provider in TruncationProviders)
            {
                var card = vm.QuotaCards.Single(c => c.Provider == provider);
                var tile = tiles.Single(t => ((QuotaProviderCard)t.DataContext!).Provider == provider);
                var name = texts.FirstOrDefault(t => t.IsEffectivelyVisible && t.DataContext is QuotaProviderCard { Provider: var p } && p == provider && t.Text == card.ProviderText);
                if (name is null)
                {
                    var seen = string.Join(" | ", texts.Where(t => t.DataContext is QuotaProviderCard { Provider: var q } && q == provider).Select(t => $"'{t.Text}' visible={t.IsEffectivelyVisible}"));
                    Assert.Fail($"no visible name block for {provider}: [{seen}]");
                }
                AssertUntrimmed(name, host, () => (13, FontWeight.SemiBold, card.ProviderText), $"{card.ProviderText} name", tile.Bounds.Width);
                var freshness = texts.Single(t => t.IsEffectivelyVisible && t.DataContext is QuotaProviderCard { Provider: var fp } && fp == provider && t.Text == card.FreshnessText);
                AssertUntrimmed(freshness, host, () => (11, FontWeight.Normal, card.FreshnessText), "freshness", tile.Bounds.Width);
                foreach (var windowRow in card.Windows.Where(row => row.ResetText.Length > 0))
                {
                    var dash = texts.Single(t => t.IsEffectivelyVisible && t.DataContext is QuotaCardWindow { Window: var dw } && dw == windowRow.Window && t.Text == windowRow.RemainingText);
                    Assert.True(dash.IsEffectivelyVisible, $"the value '{windowRow.RemainingText}' must stay visible");
                    if (windowRow.HasPreviousText)
                    {
                        var previous = texts.Single(t => t.IsEffectivelyVisible && t.DataContext is QuotaCardWindow { Window: var pw } && pw == windowRow.Window && t.Text == windowRow.PreviousText);
                        Assert.True(previous.Classes.Contains("quota-previous") && !previous.Classes.Contains("normal") && !previous.Classes.Contains("attention") && !previous.Classes.Contains("danger"), $"the previous percentage '{windowRow.PreviousText}' must not borrow the current value's tone");
                    }
                    var reset = texts.Single(t => t.IsEffectivelyVisible && t.DataContext is QuotaCardWindow { Window: var rw } && rw == windowRow.Window && t.Text == windowRow.ResetText);
                    AssertUntrimmed(reset, host, () => (11, FontWeight.Normal, windowRow.ResetText), "reset", tile.Bounds.Width);
                }
            }
        }
        finally { window.Close(); vm.Dispose(); }
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

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedPort(LatestQuota quota) : IQuotaReadPort
    {
        public Task<LatestQuota> ReadLatestAsync(CancellationToken cancellationToken) => Task.FromResult(quota);
    }
}

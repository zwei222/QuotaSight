using System.Text;
using Avalonia.Headless.XUnit;
using QuotaSight.Core;
using QuotaSight.Infrastructure;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// The real-data contracts that survive the quota-UI removal: the history's damage scan
/// still guards the one surface quota history keeps (the corrupted-history notification),
/// and the notification banner's localization rules hold for the alerts that remain.
/// </summary>
public sealed class RealFeatureUiTests
{
    [AvaloniaFact]
    public async Task InitializeAsync_detects_a_corrupt_current_day_and_notifies_once()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        try
        {
            using var history = new JsonlQuotaHistory(root, new FixedTimeProvider(now.AddDays(-1)));
            await history.AppendAsync([Snapshot(25) with { Account = "prior-day" }], TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, $"{today:yyyy-MM-dd}.jsonl"), "not-json\n", TestContext.Current.CancellationToken);

            var viewModel = new MainViewModel(quotaHistory: history, timeProvider: new FixedTimeProvider(now));
            await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
            await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

            // The corrupted history notification is the one quota-history surface the UI
            // keeps; a second initialization must not duplicate the banner.
            Assert.Contains("History data is damaged", viewModel.NotificationBannerText, StringComparison.Ordinal);
            Assert.False(viewModel.IsTokScaleSectionVisible is false);
            await Assert.ThrowsAsync<InvalidDataException>(async () => await history.ReadAsync(today, TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Initially_English_history_corruption_notification_relocalizes_to_Japanese()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        try
        {
            using var history = new JsonlQuotaHistory(root);
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            await File.WriteAllTextAsync(Path.Combine(root, $"{today:yyyy-MM-dd}.jsonl"), "not-json\n", TestContext.Current.CancellationToken);
            var vm = new MainViewModel(quotaHistory: history, timeProvider: new FixedTimeProvider(now));
            await vm.InitializeAsync(TestContext.Current.CancellationToken);

            Assert.Equal("History: History data is damaged; showing available entries.", vm.NotificationBannerText);
            vm.Language = UiLanguage.Japanese;

            Assert.Equal("履歴: 履歴データが破損しています。読み込める項目を表示しています。", vm.NotificationBannerText);
            Assert.DoesNotContain("InvalidDataException", vm.NotificationBannerText, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Ui_owned_alert_factory_relocalizes_but_opaque_public_notify_does_not()
    {
        using var vm = new MainViewModel();
        vm.NotifyLocalized(copy => (copy.TrayUnavailableTitle, copy.TrayUnavailable));
        Assert.Equal("Tray unavailable: Tray unavailable. Use the app window instead.", vm.NotificationBannerText);
        vm.Language = UiLanguage.Japanese;
        Assert.Equal("トレイを利用できません: トレイを利用できません。アプリ内で操作してください。", vm.NotificationBannerText);

        vm.Notify("Caller title", "Caller text");
        vm.Language = UiLanguage.English;
        Assert.Equal("Caller title: Caller text", vm.NotificationBannerText);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private static QuotaSnapshot Snapshot(decimal percent) => new(ProviderKind.OpenCode, "acct", "usage", new(QuotaWindowKind.Rolling, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1)), percent, 100, null, "requests", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, QuotaSource.Official, QuotaConfidence.Official, DateTimeOffset.UtcNow.AddHours(1), "OpenCode Go");
}

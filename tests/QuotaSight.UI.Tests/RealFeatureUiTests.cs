using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// The notification banner is the one alert surface the UI keeps, and its localization rules
/// hold for the alerts that remain.
/// </summary>
public sealed class RealFeatureUiTests
{
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
}

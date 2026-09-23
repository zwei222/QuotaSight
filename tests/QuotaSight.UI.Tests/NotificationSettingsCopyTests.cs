using QuotaSight.Core;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class NotificationSettingsCopyTests
{
    [Fact]
    public void Delivery_description_is_localized_and_matches_the_platform_fallback_contract()
    {
        var japanese = new UiCopy(UiLanguage.Japanese).NotificationDeliveryDescription;
        var english = new UiCopy(UiLanguage.English).NotificationDeliveryDescription;

        Assert.Contains("アプリ内", japanese);
        Assert.Contains("しきい値", japanese);
        Assert.Contains("in-app", english);
        Assert.Contains("threshold", english, StringComparison.OrdinalIgnoreCase);

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("Windows", japanese);
            Assert.Contains("Windows", english);
            Assert.Contains("バナー", japanese);
            Assert.Contains("banner", english, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("WindowsのOS通知を試み", japanese);
            Assert.Contains("Windows system notification", english, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("表示されない場合", japanese);
            Assert.Contains("suppress", english, StringComparison.OrdinalIgnoreCase);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Contains("Linux", japanese);
            Assert.Contains("Linux", english);
            Assert.Contains("デスクトップ通知", japanese);
            Assert.Contains("desktop notification", english, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("アプリの実行中", japanese);
            Assert.Contains("アプリ内にも表示", japanese);
            Assert.Contains("while running", english);
            Assert.Contains("also shows an in-app banner", english);
        }
    }

    [Fact]
    public void Threshold_helper_copy_does_not_claim_per_account_editing_and_excludes_copilot_credits()
    {
        var japanese = new UiCopy(UiLanguage.Japanese).ProviderOverrides;
        var english = new UiCopy(UiLanguage.English).ProviderOverrides;

        Assert.Contains("Copilot", japanese);
        Assert.Contains("Copilot", english);
        Assert.Contains("percentage-based", english);
        Assert.DoesNotContain("アカウントごとに", japanese);
        Assert.DoesNotContain("per account", english, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Settings_markup_places_localized_delivery_guidance_near_checkbox_and_threshold()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "QuotaSight.UI", "Views", "MainWindow.axaml"));
        Assert.Contains("Text=\"{Binding CopyText.NotificationDeliveryDescription}\"", xaml);
        Assert.Contains("Text=\"{Binding CopyText.ProviderOverrides}\"", xaml);
        Assert.Contains("Classes=\"muted\"", xaml);
    }
}

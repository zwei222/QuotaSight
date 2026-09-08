using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class LocalizationCopyTests
{
    [Fact]
    public void Integration_copy_describes_credential_storage_and_device_flow_requirements_in_both_languages()
    {
        var japanese = new UiCopy(UiLanguage.Japanese);
        var english = new UiCopy(UiLanguage.English);

        Assert.Contains("安全な資格情報ストアを利用できる場合は保存", japanese.OpenCodeDescription);
        Assert.Contains("利用できない場合はセッション中のみ保持", japanese.OpenCodeDescription);
        Assert.Contains("表示・記録しません", japanese.OpenCodeDescription);
        Assert.Contains("secure credential store when available", english.OpenCodeDescription);
        Assert.Contains("otherwise it is kept for this session only", english.OpenCodeDescription);
        Assert.Contains("never displayed or logged", english.OpenCodeDescription);

        Assert.Contains("デバイス認証には実行時に設定したGitHub OAuth Client IDが必要", japanese.CopilotDescription);
        Assert.Contains("クライアントシークレットは不要", japanese.CopilotDescription);
        Assert.Contains("ghの状態確認にはClient IDは不要", japanese.CopilotDescription);
        Assert.Contains("組織の利用枠を取得できない場合は手動入力", japanese.CopilotDescription);
        Assert.Contains("Device flow requires a runtime-configured GitHub OAuth Client ID", english.CopilotDescription);
        Assert.Contains("no client secret", english.CopilotDescription);
        Assert.Contains("gh status does not require a Client ID", english.CopilotDescription);
        Assert.Contains("Organization quota may be unavailable, so manual fallback is supported", english.CopilotDescription);
    }

    [Fact]
    public void Exception_notifications_have_localized_safe_copy()
    {
        var japanese = new UiCopy(UiLanguage.Japanese);
        var english = new UiCopy(UiLanguage.English);

        Assert.Equal("履歴", japanese.HistoryNotificationTitle);
        Assert.Equal("この履歴を削除できませんでした。", japanese.HistoryDeleteFailure);
        Assert.Equal("履歴データが破損しています。読み込める項目を表示しています。", japanese.HistoryCorrupt);
        Assert.Equal("トレイ", japanese.TrayUnavailableTitle);
        Assert.Equal("トレイを利用できません。アプリ内で操作してください。", japanese.TrayUnavailable);
        Assert.Equal("History", english.HistoryNotificationTitle);
        Assert.Equal("Unable to delete this history entry.", english.HistoryDeleteFailure);
        Assert.Equal("History data is damaged; showing available entries.", english.HistoryCorrupt);
        Assert.Equal("Tray unavailable", english.TrayUnavailableTitle);
        Assert.Equal("Tray unavailable. Use the app window instead.", english.TrayUnavailable);
    }
}

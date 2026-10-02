using QuotaSight.UI;
using QuotaSight.Core;
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

        Assert.Contains("デバイス認証には実行時に設定したGitHub App Client IDが必要", japanese.CopilotDescription);
        Assert.Contains("クライアントシークレットは不要", japanese.CopilotDescription);
        Assert.Contains("ghの状態確認にはClient IDは不要", japanese.CopilotDescription);
        Assert.Contains("組織の利用状況を取得できない場合は手動入力", japanese.CopilotDescription);
        Assert.Contains("Device flow requires a runtime-configured GitHub App Client ID", english.CopilotDescription);
        Assert.Contains("no client secret", english.CopilotDescription);
        Assert.Contains("gh status does not require a Client ID", english.CopilotDescription);
        Assert.Contains("Organization quota may be unavailable, so manual fallback is supported", english.CopilotDescription);
    }

    [Fact]
    public void Privacy_footer_says_no_telemetry_and_explains_provider_authentication_in_both_languages()
    {
        var japanese = new UiCopy(UiLanguage.Japanese);
        var english = new UiCopy(UiLanguage.English);

        Assert.Equal("テレメトリーは送信しません。認証情報は連携先への認証に使用します。", japanese.NoSecretsLeave);
        Assert.Equal("No telemetry is sent. Credentials are used to authenticate with providers.", english.NoSecretsLeave);
        Assert.DoesNotContain("leave this app", english.NoSecretsLeave, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("このセッション中のみ保持", japanese.OpenCodeStatus(new ProviderConnectionResult(true, string.Empty, FetchStatus.Success)));
    }

    [Fact]
    public void Exception_notifications_have_localized_safe_copy()
    {
        var japanese = new UiCopy(UiLanguage.Japanese);
        var english = new UiCopy(UiLanguage.English);

        Assert.Equal("履歴", japanese.HistoryNotificationTitle);
        Assert.Equal("この履歴を削除できませんでした。", japanese.HistoryDeleteFailure);
        Assert.Equal("履歴データが破損しています。読み込める項目を表示しています。", japanese.HistoryCorrupt);
        Assert.Equal("トレイを利用できません", japanese.TrayUnavailableTitle);
        Assert.Equal("トレイを利用できません。アプリ内で操作してください。", japanese.TrayUnavailable);
        Assert.Equal("History", english.HistoryNotificationTitle);
        Assert.Equal("Unable to delete this history entry.", english.HistoryDeleteFailure);
        Assert.Equal("History data is damaged; showing available entries.", english.HistoryCorrupt);
        Assert.Equal("Tray unavailable", english.TrayUnavailableTitle);
        Assert.Equal("Tray unavailable. Use the app window instead.", english.TrayUnavailable);
    }

    [Fact]
    public void Missing_provider_refresh_copy_names_the_provider_without_guessing_the_cause()
    {
        var japanese = new UiCopy(UiLanguage.Japanese);
        var english = new UiCopy(UiLanguage.English);
        var missing = new[] { ProviderKind.ChatGpt, ProviderKind.OpenCode };

        Assert.Equal("今回はCodex、OpenCode Goのデータを取得できませんでした。接続状態を確認してください。", japanese.RefreshMissingProviders(missing));
        Assert.Equal("Codex and OpenCode Go data could not be retrieved this time. Check the connection.", english.RefreshMissingProviders(missing));
        Assert.DoesNotContain("credential", english.RefreshMissingProviders(missing), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("資格情報", japanese.RefreshMissingProviders(missing), StringComparison.Ordinal);
    }

    [Fact]
    public void Codex_display_name_is_distinct_from_chatgpt()
    {
        Assert.Equal("ChatGPT Codex", new UiCopy(UiLanguage.English).ProviderName(ProviderKind.ChatGpt));
        Assert.Equal("ChatGPT Codex", new UiCopy(UiLanguage.Japanese).ProviderName(ProviderKind.ChatGpt));
    }

    [Fact]
    public void Copilot_threshold_copy_says_shared_gross_credits_are_not_a_personal_quota()
    {
        Assert.Equal("通知しきい値は使用率を計算できる利用枠に適用されます。CopilotのAIクレジット総量には個人別の上限がないため、対象外です。",
            new UiCopy(UiLanguage.Japanese).ProviderOverrides);
    }
}

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

        Assert.Contains("device flowには実行時に設定したGitHub OAuth Client IDが必要", japanese.CopilotDescription);
        Assert.Contains("Client Secretは不要", japanese.CopilotDescription);
        Assert.Contains("gh statusにはClient IDは不要", japanese.CopilotDescription);
        Assert.Contains("組織クォータを取得できない場合は手動入力", japanese.CopilotDescription);
        Assert.Contains("Device flow requires a runtime-configured GitHub OAuth Client ID", english.CopilotDescription);
        Assert.Contains("no client secret", english.CopilotDescription);
        Assert.Contains("gh status does not require a Client ID", english.CopilotDescription);
        Assert.Contains("Organization quota may be unavailable, so manual fallback is supported", english.CopilotDescription);
    }
}

using QuotaSight.Application;
using QuotaSight.Core;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class CopilotStatusPresentationTests
{
    [Theory]
    [InlineData(UiLanguage.Japanese, "設定画面でGitHub Organizationを設定してください。")]
    [InlineData(UiLanguage.English, "Set the GitHub Organization in Settings")]
    public async Task Configuration_error_uses_safe_ui_copy_without_provider_details(UiLanguage language, string expected)
    {
        var uiDispatcher = new ImmediateUiDispatcher();
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: new FailureApplication(FetchStatus.ConfigurationError), uiDispatcher: uiDispatcher);
        vm.Language = language;

        await vm.RefreshAsync();

        Assert.Contains(expected, vm.NotificationBannerText, StringComparison.Ordinal);
        Assert.DoesNotContain("token", vm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("response", vm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(UiLanguage.Japanese, "Billing Usageを取得できません", "Organization名", "アクセス権", "対応状況")]
    [InlineData(UiLanguage.English, "Unable to retrieve Billing Usage", "organization name", "access permissions", "supported")]
    public async Task Copilot_unsupported_failure_guides_organization_access_and_availability(
        UiLanguage language, string expectedMessage, string organizationHint, string accessHint, string availabilityHint)
    {
        var uiDispatcher = new ImmediateUiDispatcher();
        foreach (var (status, expected) in new[]
        {
            (FetchStatus.Unauthorized, language == UiLanguage.Japanese ? "GitHub認証が必要です" : "GitHub authentication is required"),
            (FetchStatus.Forbidden, language == UiLanguage.Japanese ? "Billing権限が不足" : "Billing permission is insufficient"),
            (FetchStatus.RateLimited, language == UiLanguage.Japanese ? "レート制限" : "rate-limited")
        })
        {
            using var vm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: new FailureApplication(status), uiDispatcher: uiDispatcher);
            vm.Language = language;

            await vm.RefreshAsync();

            Assert.Contains(expected, vm.NotificationBannerText, StringComparison.Ordinal);
        }

        using var unsupportedVm = new MainViewModel(new EmptyDashboardSource(), quotaApplication: new FailureApplication(FetchStatus.Unsupported), uiDispatcher: uiDispatcher);
        unsupportedVm.Language = language;

        await unsupportedVm.RefreshAsync();

        Assert.Contains(expectedMessage, unsupportedVm.NotificationBannerText, StringComparison.Ordinal);
        Assert.Contains(organizationHint, unsupportedVm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(accessHint, unsupportedVm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(availabilityHint, unsupportedVm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(language == UiLanguage.Japanese ? "APIには対応していません" : "API is unsupported", unsupportedVm.NotificationBannerText, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FailureApplication(FetchStatus status) : IQuotaApplication
    {
        public ValueTask<QuotaRefreshResult> RefreshAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new QuotaRefreshResult([], [new(ProviderKind.Copilot, status)]));
    }
}

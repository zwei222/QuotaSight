using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using QuotaSight.Infrastructure;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class CopilotOrganizationSettingsTests
{
    [Fact]
    public void Organization_is_loaded_and_round_tripped_without_being_confused_with_client_id()
    {
        var vm = new MainViewModel(new EmptyDashboardSource());
        try
        {
            vm.SetSettings(new AppSettingsDto(GithubOAuthClientId: "client-123", GithubOrganization: "acme-engineering"), persist: false);

            Assert.Equal("client-123", vm.Settings.GithubOAuthClientId);
            Assert.Equal("acme-engineering", vm.Settings.GithubOrganization);
            Assert.Equal("acme-engineering", vm.CurrentSettingsForTests.GithubOrganization);
        }
        finally { vm.Dispose(); }
    }

    [AvaloniaFact]
    public void Settings_page_has_a_distinct_non_secret_organization_input()
    {
        var window = new MainWindow(new MainViewModel(new EmptyDashboardSource()));
        window.Show();
        try
        {
            var organization = window.FindControl<TextBox>("GithubOrganizationBox");
            Assert.NotNull(organization);
            Assert.NotEqual("GithubClientIdBox", organization!.Name);
            Assert.Contains("organization", AutomationProperties.GetName(organization), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not a secret", window.ViewModel.CopyText.GithubOrganization, StringComparison.OrdinalIgnoreCase);
        }
        finally { window.Close(); window.ViewModel.Dispose(); }
    }

    [Fact]
    public void Organization_hint_tells_users_to_refresh_after_saving()
    {
        var copy = new UiCopy(UiLanguage.Japanese);

        Assert.Contains("保存後", copy.GithubOrganizationHint, StringComparison.Ordinal);
        Assert.Contains("変更時に自動保存", copy.GithubOrganizationHint, StringComparison.Ordinal);
        Assert.Contains("更新", copy.GithubOrganizationHint, StringComparison.Ordinal);
        Assert.Contains("auto-saved", new UiCopy(UiLanguage.English).GithubOrganizationHint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("After saving", new UiCopy(UiLanguage.English).GithubOrganizationHint, StringComparison.Ordinal);
        Assert.Contains("refresh", new UiCopy(UiLanguage.English).GithubOrganizationHint, StringComparison.OrdinalIgnoreCase);
    }
}

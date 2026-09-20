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
}

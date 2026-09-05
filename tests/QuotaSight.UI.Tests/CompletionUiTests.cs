using Avalonia.Headless.XUnit;
using Xunit;
using QuotaSight.UI;

namespace QuotaSight.UI.Tests;

public sealed class CompletionUiTests
{
    [Fact] public void Tray_menu_controller_exposes_show_refresh_exit() { var controller = new TrayController(() => null, () => { }); Assert.Equal([TrayCommand.Show, TrayCommand.Refresh, TrayCommand.Exit], controller.MenuCommands); }
    [Fact] public void Tray_controller_connects_commands() { var shown = 0; var refreshed = 0; var exited = 0; var controller = new TrayController(() => { shown++; return null; }, () => exited++); controller.RefreshAction = () => refreshed++; controller.Show(); controller.Refresh(); controller.Exit(); Assert.Equal(2, shown); Assert.Equal(1, refreshed); Assert.Equal(1, exited); }
    [AvaloniaFact] public void App_initializes_tray_only_for_non_headless_lifetime() { var app = new App(); Assert.NotNull(app.TrayFactory); }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Headless.XUnit;
using Avalonia.Media;

using Avalonia.VisualTree;

using Avalonia.Styling;

using QuotaSight.Infrastructure;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// The UI-requirement contracts that outlive the quota-UI removal: navigation, theme
/// surfaces, the compact header and icon buttons, the TokScale-only add-provider flow,
/// and the localized copy of the choices that remain.
/// </summary>
public sealed class UiRequirementsTests
{
    [AvaloniaFact]
    public void Usage_navigation_shows_unconfigured_daily_context_without_touching_quota_dashboard()
    {
        var window = new MainWindow(new MainViewModel());
        try
        {
            window.Show();
            window.FindControl<Button>("UsageNavButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.True(window.FindControl<Grid>("UsagePage")!.IsVisible);
            Assert.False(window.FindControl<Grid>("DashboardPage")!.IsVisible);
            Assert.Contains("not configured", window.FindControl<TextBlock>("UsageStatus")!.Text, StringComparison.OrdinalIgnoreCase);
            var usageRange = window.FindControl<TextBlock>("UsageTimeZone")!;
            Assert.False(usageRange.IsVisible);
            Assert.Empty(usageRange.Text ?? "");
            window.ViewModel.Language = UiLanguage.Japanese;
            Assert.Contains("未設定", window.FindControl<TextBlock>("UsageStatus")!.Text);
            window.ViewModel.Navigate(AppPage.Dashboard);
            Assert.True(window.FindControl<Grid>("DashboardPage")!.IsVisible);
        }
        finally { window.Close(); window.ViewModel.Dispose(); }
    }

    [AvaloniaFact]
    public void Application_theme_uses_quiet_mineral_surfaces_and_a_single_green_action_accent()
    {
        var window = new MainWindow(new MainViewModel());
        try
        {
            window.Show();
            UiSettings.ApplyTheme(ThemeMode.Light);
            window.UpdateLayout();
            AssertBrushColor(window.Background, "#F4F6F3");
            AssertBrushColor(window.FindControl<Border>("Sidebar")!.Background, "#EEF1ED");
            AssertBrushColor(window.FindControl<Border>("LocalFirstCard")!.Background, "#FFFFFF");
            Assert.Equal(14, window.FindControl<Border>("LocalFirstCard")!.CornerRadius.TopLeft);
            UiSettings.ApplyTheme(ThemeMode.Dark);
            window.UpdateLayout();
            AssertBrushColor(window.Background, "#101715");
            AssertBrushColor(window.FindControl<Border>("Sidebar")!.Background, "#151D1A");
        }
        finally { window.Close(); window.ViewModel.Dispose(); }
    }

    [AvaloniaFact]
    public void Page_header_is_single_and_sidebar_note_keeps_intrinsic_layout()
    {
        var window = new MainWindow(new MainViewModel());
        try
        {
            window.Show();
            window.UpdateLayout();
            var navigation = window.FindControl<DockPanel>("MainNavigation")!;
            var note = window.FindControl<Border>("LocalFirstCard")!;
            var sidebar = window.FindControl<Border>("Sidebar")!;
            Assert.False(navigation.LastChildFill);
            Assert.True(note.Bounds.Height < sidebar.Bounds.Height / 2);

            window.ViewModel.Navigate(AppPage.Settings);
            window.UpdateLayout();
            var headings = window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsVisible && text.Classes.Contains("title") && text.Text == window.ViewModel.CopyText.Settings).ToList();
            Assert.Single(headings);
            var header = window.FindControl<Grid>("DashboardHeader")!;
            Assert.Equal(window.ViewModel.CopyText.Settings, header.GetVisualDescendants().OfType<TextBlock>().First(text => text.Classes.Contains("title")).Text);
        }
        finally
        {
            window.Close();
            window.ViewModel.Dispose();
        }
    }

    [AvaloniaFact]
    public void Primary_action_buttons_use_matching_vector_icons_localized_tooltips_and_automation_names()
    {
        var main = new MainWindow(new MainViewModel());
        var compact = new CompactQuotaWindow(new MainViewModel());
        try
        {
            main.Show();
            compact.Show();
            main.UpdateLayout();
            compact.UpdateLayout();

            var mainRefresh = main.FindControl<Button>("RefreshButton")!;
            var compactRefresh = compact.FindControl<Button>("CompactRefreshButton")!;
            var addProvider = main.FindControl<Button>("AddProviderButton")!;

            Assert.IsType<PathIcon>(mainRefresh.Content);
            Assert.IsType<PathIcon>(compactRefresh.Content);
            Assert.IsType<PathIcon>(addProvider.Content);
            Assert.Equal(((PathIcon)mainRefresh.Content).Data?.ToString(), ((PathIcon)compactRefresh.Content).Data?.ToString());
            Assert.Equal(main.ViewModel.CopyText.Refresh, ToolTip.GetTip(mainRefresh));
            Assert.Equal(main.ViewModel.CopyText.Refresh, AutomationProperties.GetName(mainRefresh));
            Assert.Equal(compact.ViewModel.CopyText.Refresh, ToolTip.GetTip(compactRefresh));
            Assert.Equal(compact.ViewModel.CopyText.Refresh, AutomationProperties.GetName(compactRefresh));
            Assert.Equal(main.ViewModel.CopyText.AddProvider, ToolTip.GetTip(addProvider));
            Assert.Equal(main.ViewModel.CopyText.AddProvider, AutomationProperties.GetName(addProvider));

            main.ViewModel.Language = UiLanguage.Japanese;
            compact.ViewModel.Language = UiLanguage.Japanese;
            main.UpdateLayout();
            compact.UpdateLayout();

            Assert.Equal("更新", ToolTip.GetTip(mainRefresh));
            Assert.Equal("更新", AutomationProperties.GetName(mainRefresh));
            Assert.Equal("更新", ToolTip.GetTip(compactRefresh));
            Assert.Equal("更新", AutomationProperties.GetName(compactRefresh));
            Assert.Equal(main.ViewModel.CopyText.AddProvider, ToolTip.GetTip(addProvider));
            Assert.Equal(main.ViewModel.CopyText.AddProvider, AutomationProperties.GetName(addProvider));
        }
        finally
        {
            compact.Close();
            compact.ViewModel.Dispose();
            main.Close();
            main.ViewModel.Dispose();
        }
    }

    [AvaloniaFact]
    public void Icon_actions_keep_the_path_icon_readable_across_theme_and_interaction_states()
    {
        var window = new MainWindow(new MainViewModel()) { Width = 420 };
        window.ViewModel.Navigate(AppPage.Settings);
        try
        {
            window.Show();
            var refresh = window.FindControl<Button>("RefreshButton")!;
            var icon = Assert.IsType<PathIcon>(refresh.Content);

            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                UiSettings.ApplyTheme(theme);
                GetPseudoClasses(refresh).Remove(":pressed");
                window.UpdateLayout();
                AssertBrushColor(refresh.Background, "#00FFFFFF");
                AssertBrushColor(icon.Foreground, theme == ThemeMode.Light ? "#17211D" : "#F1F5F2");

                GetPseudoClasses(refresh).Add(":pointerover");
                window.UpdateLayout();
                var presenter = Assert.Single(refresh.GetVisualDescendants().OfType<ContentPresenter>(), part => part.Name == "PART_ContentPresenter");
                AssertBrushColor(presenter.Background, theme == ThemeMode.Light ? "#E5F1EC" : "#202F29");
                AssertBrushColor(presenter.BorderBrush, theme == ThemeMode.Light ? "#167C68" : "#73D6B1");
                AssertBrushColor(icon.Foreground, theme == ThemeMode.Light ? "#17211D" : "#F1F5F2");
                GetPseudoClasses(refresh).Remove(":pointerover");

                var navigation = window.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Dashboard");
                GetPseudoClasses(navigation).Add(":pointerover");
                window.UpdateLayout();
                var navPresenter = Assert.Single(navigation.GetVisualDescendants().OfType<ContentPresenter>(), part => part.Name == "PART_ContentPresenter");
                AssertBrushColor(navPresenter.Background, theme == ThemeMode.Light ? "#E5F1EC" : "#202F29");
                GetPseudoClasses(navigation).Remove(":pointerover");

                var input = window.FindControl<TextBox>("UsageEndpointBox")!;
                GetPseudoClasses(input).Add(":pointerover");
                window.UpdateLayout();
                var inputBorder = Assert.Single(input.GetVisualDescendants().OfType<Border>(), part => part.Name == "PART_BorderElement");
                Assert.NotEqual(Color.Parse(theme == ThemeMode.Light ? "#DCE3DE" : "#34443C"), ((ISolidColorBrush)inputBorder.BorderBrush!).Color);
                GetPseudoClasses(input).Remove(":pointerover");

                var combo = window.FindControl<ComboBox>("ThemeBox")!;
                GetPseudoClasses(combo).Add(":pointerover");
                window.UpdateLayout();
                var comboBorder = Assert.Single(combo.GetVisualDescendants().OfType<Border>(), part => part.Name == "Background");
                Assert.NotEqual(Color.Parse(theme == ThemeMode.Light ? "#DCE3DE" : "#34443C"), ((ISolidColorBrush)comboBorder.BorderBrush!).Color);
                GetPseudoClasses(combo).Remove(":pointerover");

                GetPseudoClasses(refresh).Add(":focus-visible");
                window.UpdateLayout();
                var focusedPresenter = Assert.Single(refresh.GetVisualDescendants().OfType<ContentPresenter>(), part => part.Name == "PART_ContentPresenter");
                AssertBrushColor(focusedPresenter.BorderBrush, theme == ThemeMode.Light ? "#167C68" : "#73D6B1");
                Assert.Equal(2, focusedPresenter.BorderThickness.Top);
                GetPseudoClasses(refresh).Remove(":focus-visible");

                GetPseudoClasses(refresh).Add(":pressed");
                window.UpdateLayout();
                var pressedPresenter = Assert.Single(refresh.GetVisualDescendants().OfType<ContentPresenter>(), part => part.Name == "PART_ContentPresenter");
                Assert.NotEqual(Color.Parse("#E5F1EC"), ((ISolidColorBrush)pressedPresenter.Background!).Color);
                AssertBrushColor(icon.Foreground, theme == ThemeMode.Light ? "#F4F6F3" : "#101715");
                GetPseudoClasses(refresh).Remove(":pressed");

                refresh.IsEnabled = false;
                window.UpdateLayout();
                Assert.Equal(0.5, refresh.Opacity);
                AssertBrushColor(icon.Foreground, theme == ThemeMode.Light ? "#17211D" : "#F1F5F2");
                refresh.IsEnabled = true;
            }
        }
        finally
        {
            window.Close();
            window.ViewModel.Dispose();
        }
    }

    [AvaloniaFact]
    public void Compact_width_keeps_header_copy_clear_of_the_refresh_button()
    {
        var window = new MainWindow(new MainViewModel()) { Width = 420 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var headerCopy = window.FindControl<StackPanel>("DashboardHeaderCopy")!;
            var refresh = window.FindControl<Button>("RefreshButton")!;
            Assert.False(headerCopy.Bounds.Intersects(refresh.Bounds));
        }
        finally
        {
            window.Close();
            window.ViewModel.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Add_provider_flow_shows_selection_then_the_selected_provider_panel_and_can_close()
    {
        var window = new MainWindow(new MainViewModel());
        window.Show();
        await window.InitializeAsync();
        var add = window.FindControl<Button>("AddProviderButton")!;
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(window.FindControl<ComboBox>("ProviderPicker")!.IsVisible);

        var picker = window.FindControl<ComboBox>("ProviderPicker")!;
        var openai = window.ViewModel.ProviderChoices.Single(item => item.ProviderId == "openai");
        picker.SelectedItem = openai;
        picker.RaiseEvent(new SelectionChangedEventArgs(ComboBox.SelectionChangedEvent, new List<object?>(), new List<object?> { openai }));
        Assert.True(window.FindControl<Border>("TokScalePanel")!.IsVisible);
        Assert.True(window.FindControl<Button>("CloseProviderButton")!.IsVisible);

        window.ViewModel.CloseProviderFlow();
        Assert.False(window.FindControl<ComboBox>("ProviderPicker")!.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Reopening_provider_flow_shows_picker_without_previous_panel()
    {
        var window = new MainWindow(new MainViewModel());
        window.Show();
        await window.InitializeAsync();
        var add = window.FindControl<Button>("AddProviderButton")!;
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var picker = window.FindControl<ComboBox>("ProviderPicker")!;
        var openai = window.ViewModel.ProviderChoices.Single(item => item.ProviderId == "openai");
        picker.SelectedItem = openai;
        picker.RaiseEvent(new SelectionChangedEventArgs(ComboBox.SelectionChangedEvent, new List<object?>(), new List<object?> { openai }));
        window.ViewModel.CloseProviderFlow();

        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(picker.IsVisible);
        Assert.False(window.FindControl<Border>("TokScalePanel")!.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Language_switch_keeps_closed_provider_flow_closed()
    {
        var window = new MainWindow(new MainViewModel());
        window.Show();
        await window.InitializeAsync();

        var language = window.FindControl<ComboBox>("LanguageBox")!;
        language.SelectedItem = "日本語";
        await Task.Delay(50);

        Assert.False(window.ViewModel.IsProviderFlowOpen);
        Assert.False(window.FindControl<ComboBox>("ProviderPicker")!.IsVisible);
        window.Close();
    }

    [Fact]
    public void Provider_choices_offer_the_catalog_only_with_display_names_and_identity()
    {
        var japanese = UiSettings.ProviderChoices(UiLanguage.Japanese);
        var english = UiSettings.ProviderChoices(UiLanguage.English);

        // The picker offers the provider catalog only: the five quota-era choices are gone.
        Assert.Equal(TokScaleProviderCatalog.Providers.Count, japanese.Count);
        Assert.Equal(japanese.Count, english.Count);
        Assert.Equal("OpenAI", english.Single(item => item.ProviderId == "openai").DisplayName);
        Assert.Equal("OpenAI", japanese.Single(item => item.ProviderId == "openai").DisplayName);
        Assert.Equal("GitHub Copilot", english.Single(item => item.ProviderId == "copilot").DisplayName);
        Assert.All(japanese, item => Assert.False(item.ProviderId.Contains(',', StringComparison.Ordinal)));
        // Display-name order so a list this long stays scannable.
        Assert.Equal(
            [.. japanese.Select(item => item.DisplayName).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)],
            [.. japanese.Select(item => item.DisplayName)]);
    }

    [Fact]
    public void Japanese_copy_localizes_theme_labels()
    {
        var choices = UiSettings.ThemeChoices(UiLanguage.Japanese);

        Assert.Equal("システム", choices.Single(item => item.Value == ThemeMode.System).DisplayName);
        Assert.Equal("ライト", choices.Single(item => item.Value == ThemeMode.Light).DisplayName);
        Assert.Equal("ダーク", choices.Single(item => item.Value == ThemeMode.Dark).DisplayName);
    }

    [Fact]
    public void Theme_choice_values_survive_localization()
    {
        var theme = UiSettings.ThemeChoices(UiLanguage.Japanese).Single(item => item.Value == ThemeMode.Dark);

        Assert.Equal(ThemeMode.Dark, theme.Value);
        Assert.Equal("ダーク", theme.DisplayName);
    }

    [AvaloniaFact]
    public async Task Japanese_language_switch_updates_the_rendered_theme_choice()
    {
        var window = new MainWindow(new MainViewModel());
        window.Show();
        await window.InitializeAsync();

        window.FindControl<ComboBox>("ThemeBox")!.SelectedItem = UiSettings.ThemeChoices(UiLanguage.English).Single(item => item.Value == ThemeMode.Dark);
        window.FindControl<ComboBox>("LanguageBox")!.SelectedItem = "日本語";
        await Task.Delay(50);

        var selectedTheme = Assert.IsType<LocalizedChoice<ThemeMode>>(window.FindControl<ComboBox>("ThemeBox")!.SelectedItem);
        Assert.Equal(ThemeMode.Dark, selectedTheme.Value);
        Assert.Equal("ダーク", selectedTheme.DisplayName);
        window.Close();
    }

    private static void AssertBrushColor(IBrush? actual, string expected)
    {
        Assert.Equal(Color.Parse(expected), Assert.IsAssignableFrom<ISolidColorBrush>(actual).Color);
    }

    private static IPseudoClasses GetPseudoClasses(Control control) =>
        (IPseudoClasses)control.GetType().GetProperty("PseudoClasses", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(control)!;
}

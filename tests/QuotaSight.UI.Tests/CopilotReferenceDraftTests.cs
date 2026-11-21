using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using QuotaSight.Core;
using QuotaSight.Infrastructure;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class CopilotReferenceDraftTests
{
    [AvaloniaFact]
    public async Task Language_switch_preserves_reference_draft_with_or_without_saved_value()
    {
        foreach (var saved in new decimal?[] { 1900m, null })
        {
            using var vm = new MainViewModel(new EmptyDashboardSource());
            vm.SetSettings(new AppSettingsDto(CopilotReferenceCredits: saved), persist: false);
            vm.Navigate(AppPage.Settings);
            var window = new MainWindow(vm);
            window.Show();
            await window.InitializeAsync();
            var reference = window.FindControl<TextBox>("CopilotReferenceCreditsBox")!;
            var language = window.FindControl<ComboBox>("LanguageBox")!;
            reference.Text = "2000";

            language.SelectedIndex = 1;
            await Task.Yield();
            language.SelectedIndex = 0;
            await Task.Yield();

            Assert.Equal("2000", reference.Text);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Successful_save_canonicalizes_submitted_text()
    {
        using var vm = new MainViewModel(new EmptyDashboardSource());
        vm.Navigate(AppPage.Settings);
        var window = new MainWindow(vm) { OperationRunner = operation => operation(CancellationToken.None) };
        window.Show();
        await window.InitializeAsync();
        var reference = window.FindControl<TextBox>("CopilotReferenceCreditsBox")!;
        reference.Text = "02000";
        window.FindControl<Button>("CopilotReferenceSaveButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Task.Yield();

        Assert.Equal(2000m, vm.Settings.CopilotReferenceCredits);
        Assert.Equal("2000", reference.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Invalid_reference_message_does_not_remain_in_previous_language()
    {
        using var vm = new MainViewModel(new EmptyDashboardSource());
        vm.Navigate(AppPage.Settings);
        var window = new MainWindow(vm) { OperationRunner = operation => operation(CancellationToken.None) };
        window.Show();
        await window.InitializeAsync();
        var reference = window.FindControl<TextBox>("CopilotReferenceCreditsBox")!;
        reference.Text = "bad";
        var save = window.FindControl<Button>("CopilotReferenceSaveButton")!;
        save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Task.Yield();
        var validation = window.FindControl<TextBlock>("CopilotReferenceValidationText")!;
        Assert.NotEmpty(validation.Text ?? string.Empty);

        window.FindControl<ComboBox>("LanguageBox")!.SelectedIndex = 1;
        await Task.Yield();

        Assert.Empty(validation.Text ?? string.Empty);
        Assert.Equal("bad", reference.Text);
        window.Close();
    }
}

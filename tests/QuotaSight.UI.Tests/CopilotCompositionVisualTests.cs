using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using QuotaSight.Core;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class CopilotCompositionVisualTests
{
    [AvaloniaFact]
    public void Main_and_compact_templates_expose_the_same_quantity_composition_semantics_without_a_percentage_gauge()
    {
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new QuotaSnapshot(ProviderKind.Copilot, "octocat", "AI credits", new(QuotaWindowKind.Monthly, now.AddDays(-3), now.AddDays(28)), null, null, null, "credits", now, now, QuotaSource.Delayed, QuotaConfidence.Official, now.AddDays(28), "GitHub Copilot Business", new(1950, 1200, 750));
        using var mainVm = new MainViewModel(new FixedSource(snapshot));
        using var compactVm = new MainViewModel(new FixedSource(snapshot));
        var main = new MainWindow(mainVm);
        var compact = new CompactQuotaWindow(compactVm);
        try
        {
            main.Show();
            compact.Show();
            AssertBar(main, "CopilotCompositionBar");
            AssertBar(compact, "CompactCopilotCompositionBar");
            Assert.Equal(mainVm.Cards.Single().Windows.Single().CompositionBarLabel, compactVm.Cards.Single().Windows.Single().CompositionBarLabel);

            mainVm.Language = UiLanguage.Japanese;
            compactVm.Language = UiLanguage.Japanese;
            main.UpdateLayout();
            compact.UpdateLayout();
            Assert.Equal("含まれる分", mainVm.Cards.Single().Windows.Single().CompositionIncludedLabel.Split(' ')[0]);
            Assert.Equal("追加分", compactVm.Cards.Single().Windows.Single().CompositionAdditionalLabel.Split(' ')[0]);
        }
        finally
        {
            main.Close();
            compact.Close();
        }
    }

    private static void AssertBar(Window window, string name)
    {
        var bar = window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == name);
        Assert.True(bar.IsVisible);
        Assert.Equal(window.DataContext is MainViewModel vm ? vm.Cards.Single().Windows.Single().CompositionBarLabel : string.Empty, AutomationProperties.GetName(bar));
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<ProgressBar>(), progress => progress.IsVisible && progress.DataContext is QuotaRowViewModel { IsQuantityOnly: true });
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text?.Contains("Included", StringComparison.Ordinal) == true);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text?.Contains("Additional", StringComparison.Ordinal) == true);
        var compositionLabel = window.DataContext is MainViewModel viewModel ? viewModel.Cards.Single().Windows.Single().CompositionBarLabel : string.Empty;
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text == compositionLabel);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text?.Contains("Total:", StringComparison.Ordinal) == true);
    }

    private sealed class FixedSource(QuotaSnapshot snapshot) : IDashboardSource
    {
        public bool IsDemo => false;
        public IReadOnlyList<ProviderCardViewModel> Load() => DashboardAggregation.ToCards([snapshot], snapshot.Fetched);
    }
}

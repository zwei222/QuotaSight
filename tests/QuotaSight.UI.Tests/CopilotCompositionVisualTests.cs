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
    [AvaloniaTheory]
    [InlineData(UiLanguage.English, 1950, "Gross composition only; this is not a percentage gauge")]
    [InlineData(UiLanguage.Japanese, 1950, "総量の内訳を表示しています（使用率ゲージではありません）")]
    [InlineData(UiLanguage.English, 0, "Gross composition only; this is not a percentage gauge")]
    [InlineData(UiLanguage.Japanese, 0, "総量の内訳を表示しています（使用率ゲージではありません）")]
    public void Main_and_compact_templates_hide_obsolete_quantity_status_without_a_gauge(UiLanguage language, decimal gross, string explanation)
    {
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new QuotaSnapshot(ProviderKind.Copilot, "octocat", "AI credits", new(QuotaWindowKind.Monthly, now.AddDays(-3), now.AddDays(28)), null, null, null, "credits", now, now, QuotaSource.Delayed, QuotaConfidence.Official, now.AddDays(28), "GitHub Copilot Business", new(gross, 0, gross));
        using var mainVm = new MainViewModel(new FixedSource(snapshot));
        using var compactVm = new MainViewModel(new FixedSource(snapshot));
        var main = new MainWindow(mainVm);
        var compact = new CompactQuotaWindow(compactVm);
        try
        {
            main.Show();
            compact.Show();
            mainVm.Language = language;
            compactVm.Language = language;
            main.UpdateLayout();
            compact.UpdateLayout();

            AssertQuantityExplanation(main, mainVm.Cards.Single().Windows.Single(), explanation);
            AssertQuantityExplanation(compact, compactVm.Cards.Single().Windows.Single(), explanation);
        }
        finally
        {
            main.Close();
            compact.Close();
        }
    }

    [AvaloniaFact]
    public void Main_and_compact_templates_relegate_composition_to_text_without_a_duplicate_gross_bar()
    {
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new QuotaSnapshot(ProviderKind.Copilot, "octocat", "AI credits", new(QuotaWindowKind.Monthly, now.AddDays(-3), now.AddDays(28)), null, null, null, "credits", now, now, QuotaSource.Delayed, QuotaConfidence.Official, now.AddDays(28), "GitHub Copilot Business", new(1950, 1200, 750));
        using var mainVm = new MainViewModel(new FixedSource(snapshot));
        using var compactVm = new MainViewModel(new FixedSource(snapshot));
        var main = new MainWindow(mainVm);
        var compact = new CompactQuotaWindow(compactVm);
        try
        {
            main.Show(); compact.Show();
            Assert.DoesNotContain(main.GetVisualDescendants().OfType<Border>(), border => border.Name == "CopilotCompositionBar");
            Assert.DoesNotContain(compact.GetVisualDescendants().OfType<Border>(), border => border.Name == "CompactCopilotCompositionBar");
            Assert.Contains(main.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text?.Contains("Included", StringComparison.Ordinal) == true);
            Assert.Contains(main.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text?.Contains("Additional", StringComparison.Ordinal) == true);
            Assert.Contains(compact.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text?.Contains("Included", StringComparison.Ordinal) == true);
            Assert.Contains(compact.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text?.Contains("Additional", StringComparison.Ordinal) == true);
            Assert.DoesNotContain(main.GetVisualDescendants().OfType<ProgressBar>(), bar => bar.IsVisible && bar.Value == 100);
            Assert.DoesNotContain(compact.GetVisualDescendants().OfType<ProgressBar>(), bar => bar.IsVisible && bar.Value == 100);
            mainVm.Language = UiLanguage.Japanese; compactVm.Language = UiLanguage.Japanese;
            Assert.Contains("含まれる分", mainVm.Cards.Single().Windows.Single().CompositionIncludedLabel);
            Assert.Contains("追加分", compactVm.Cards.Single().Windows.Single().CompositionAdditionalLabel);
        }
        finally { main.Close(); compact.Close(); }
    }

    private static void AssertQuantityExplanation(Window window, QuotaRowViewModel row, string explanation)
    {
        Assert.True(row.IsQuantityOnly);
        Assert.False(row.IsGaugeVisible);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text == explanation);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text == row.QuantityGrossText);
    }

    private sealed class FixedSource(QuotaSnapshot snapshot) : IDashboardSource
    {
        public bool IsDemo => false;
        public IReadOnlyList<ProviderCardViewModel> Load() => DashboardAggregation.ToCards([snapshot], snapshot.Fetched);
    }
}

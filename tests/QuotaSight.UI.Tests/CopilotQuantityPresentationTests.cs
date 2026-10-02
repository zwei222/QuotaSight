using QuotaSight.Core;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class CopilotQuantityPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(UiLanguage.English, "Gross 1,950 credits", "Included 1,200 credits", "Additional 750 credits")]
    [InlineData(UiLanguage.Japanese, "総量 1,950 クレジット", "含まれる分 1,200 クレジット", "追加分 750 クレジット")]
    public void Copilot_usage_is_quantity_only_and_exposes_gross_discount_net(UiLanguage language, string gross, string discount, string net)
    {
        var snapshot = new QuotaSnapshot(ProviderKind.Copilot, "octocat", "AI credits", new(QuotaWindowKind.Monthly, Now.AddDays(-3), Now.AddDays(28)), 1950, null, null, "credits", Now, Now, QuotaSource.Delayed, QuotaConfidence.Official, Now.AddDays(28), "GitHub Copilot Business", new(1950, 1200, 750));

        var row = QuotaPresentationFormatter.Format(snapshot, Now, language);

        Assert.True(row.IsQuantityOnly);
        Assert.Equal(0, row.VisualPercent);
        Assert.Null(row.UsedPercent);
        Assert.Contains(gross, row.QuantityGrossText);
        Assert.Contains(discount, row.QuantityDiscountText);
        Assert.Contains(net, row.QuantityNetText);
        Assert.DoesNotContain("remaining", row.PercentText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("over", row.StatusText, StringComparison.OrdinalIgnoreCase);
        if (language == UiLanguage.Japanese)
        {
            Assert.Contains("追加分", row.CompositionBarLabel);
            Assert.DoesNotContain("総量: 総量", row.CompositionBarLabel);
            Assert.DoesNotContain("Gross", row.CompositionBarLabel);
            Assert.Contains("含まれる分", row.CompositionIncludedLabel);
            Assert.Contains("クレジット", row.CompositionIncludedLabel);
            Assert.Contains("追加", row.CompositionAdditionalLabel);
            Assert.Contains("クレジット", row.CompositionAdditionalLabel);
            Assert.Equal(1, row.CompositionIncludedLabel.Split("含まれる分").Length - 1);
            Assert.Equal(1, row.CompositionAdditionalLabel.Split("追加分").Length - 1);
            Assert.DoesNotContain("含まれる分", row.CompositionIncludedLabel[(row.CompositionIncludedLabel.IndexOf('·') + 1)..]);
            Assert.DoesNotContain("追加分", row.CompositionAdditionalLabel[(row.CompositionAdditionalLabel.IndexOf('·') + 1)..]);
        }
    }

    [Fact]
    public void Copilot_composition_uses_gross_as_the_visual_total_and_exposes_auditable_ratios()
    {
        var snapshot = new QuotaSnapshot(ProviderKind.Copilot, "octocat", "AI credits", new(QuotaWindowKind.Monthly, Now.AddDays(-3), Now.AddDays(28)), 1950, null, null, "credits", Now, Now, QuotaSource.Delayed, QuotaConfidence.Official, Now.AddDays(28), "GitHub Copilot Business", new(1950, 1200, 750));

        var row = QuotaPresentationFormatter.Format(snapshot, Now);

        Assert.True(row.IsQuantityCompositionVisible);
        Assert.Equal(1200m / 1950m, row.IncludedCompositionRatio, 6);
        Assert.Equal(750m / 1950m, row.AdditionalCompositionRatio, 6);
        Assert.Equal(1m, row.CompositionRatioTotal);
        Assert.Contains("Gross composition", row.CompositionBarLabel, StringComparison.Ordinal);
        Assert.Contains("Included", row.CompositionBarLabel, StringComparison.Ordinal);
        Assert.Contains("Additional", row.CompositionBarLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("remaining", row.CompositionBarLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("limit", row.CompositionBarLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Total: 1,950 credits.", row.CompositionBarLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("Total: Gross", row.CompositionBarLabel, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0, 0, false, 0, 0)]
    [InlineData(10, 0, 0, true, 0, 0)]
    [InlineData(10, 0, 10, true, 0, 1)]
    [InlineData(10, 10, 0, true, 1, 0)]
    [InlineData(10, 2, 3, true, 0.2, 0.3)]
    [InlineData(10, 6, 8, true, 0.6, 0.4)]
    [InlineData(10, 20, 30, true, 1, 0)]
    [InlineData(10, 20, 10, true, 1, 0)]
    [InlineData(10, -2, 3, true, 0, 0.3)]
    [InlineData(-10, 20, 10, false, 0, 0)]
    public void Copilot_composition_safely_handles_zero_negative_and_inconsistent_values(decimal gross, decimal discount, decimal net, bool visible, double included, double additional)
    {
        var snapshot = new QuotaSnapshot(ProviderKind.Copilot, "octocat", "AI credits", new(QuotaWindowKind.Monthly, Now.AddDays(-3), Now.AddDays(28)), null, null, null, "credits", Now, Now, QuotaSource.Delayed, QuotaConfidence.Official, Now.AddDays(28), "GitHub Copilot Business", new(gross, discount, net));

        var row = QuotaPresentationFormatter.Format(snapshot, Now);

        Assert.Equal(visible, row.IsQuantityCompositionVisible);
        Assert.Equal((decimal)included, row.IncludedCompositionRatio, 6);
        Assert.Equal((decimal)additional, row.AdditionalCompositionRatio, 6);
        Assert.Equal(row.IncludedCompositionRatio + row.AdditionalCompositionRatio, row.CompositionRatioTotal);
        Assert.DoesNotContain("NaN", row.CompositionBarLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Copilot_composition_does_not_add_large_decimal_components()
    {
        var snapshot = new QuotaSnapshot(ProviderKind.Copilot, "octocat", "AI credits", new(QuotaWindowKind.Monthly, Now.AddDays(-3), Now.AddDays(28)), null, null, null, "credits", Now, Now, QuotaSource.Delayed, QuotaConfidence.Official, Now.AddDays(28), "GitHub Copilot Business", new(decimal.MaxValue, decimal.MaxValue, decimal.MaxValue));

        var row = QuotaPresentationFormatter.Format(snapshot, Now);

        Assert.True(row.IsQuantityCompositionVisible);
        Assert.Equal(1m, row.IncludedCompositionRatio);
        Assert.Equal(0m, row.AdditionalCompositionRatio);
        Assert.InRange(row.CompositionRatioTotal, 0m, 1m);
        Assert.DoesNotContain("NaN", row.CompositionBarLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Infinity", row.CompositionBarLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Copilot_composition_does_not_overflow_when_tiny_gross_is_combined_with_maximum_components()
    {
        var snapshot = new QuotaSnapshot(ProviderKind.Copilot, "octocat", "AI credits", new(QuotaWindowKind.Monthly, Now.AddDays(-3), Now.AddDays(28)), null, null, null, "credits", Now, Now, QuotaSource.Delayed, QuotaConfidence.Official, Now.AddDays(28), "GitHub Copilot Business", new(0.0000000000000000000000000001m, decimal.MaxValue, decimal.MaxValue));

        var row = QuotaPresentationFormatter.Format(snapshot, Now);

        Assert.Equal(1m, row.IncludedCompositionRatio);
        Assert.Equal(0m, row.AdditionalCompositionRatio);
        Assert.InRange(row.CompositionRatioTotal, 0m, 1m);
    }
}

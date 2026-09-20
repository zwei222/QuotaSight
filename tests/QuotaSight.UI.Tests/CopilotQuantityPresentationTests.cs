using QuotaSight.Core;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class CopilotQuantityPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(UiLanguage.English, "1,950 credits used", "1,200 included", "750 additional")]
    [InlineData(UiLanguage.Japanese, "1,950 クレジットを使用", "1,200 含まれる分", "750 追加課金対象")]
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
    }
}

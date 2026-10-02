using System.Globalization;
using QuotaSight.Core;
using QuotaSight.UI;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class CopilotHistoryExportTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Credits_are_exported_but_do_not_enter_percentage_sparkline()
    {
        var history = new HistoryState(new InMemoryQuotaHistory([
            new QuotaSnapshot(ProviderKind.Copilot, "octocat", "AI credits", new(QuotaWindowKind.Monthly, Now.AddDays(-1), Now.AddDays(30)), 1900, null, null, "credits", Now, Now, QuotaSource.Delayed, QuotaConfidence.Official, Now.AddDays(30), "GitHub Copilot Business", new(1900, 1200, 700)),
            new QuotaSnapshot(ProviderKind.ChatGpt, "me", "Messages", new(QuotaWindowKind.Weekly, Now.AddDays(-1), Now.AddDays(6)), 50, 100, 50, "percent", Now, Now, QuotaSource.Manual, QuotaConfidence.Manual, null)
        ]), new ImmediateUiDispatcher());
        using (history)
        {
            await history.InitializeAsync();
            var json = await history.ExportJsonAsync();
            var csv = await history.ExportCsvAsync();

            Assert.Contains("grossQuantity", json);
            Assert.Contains("discountQuantity", json);
            Assert.Contains("netQuantity", json);
            Assert.Contains("credits", json);
            Assert.Contains("GrossQuantity", csv);
            Assert.DoesNotContain(1900d, history.SparklinePoints);
            Assert.Contains(50d, history.SparklinePoints);
        }
    }

    [Fact]
    public async Task Csv_uses_invariant_full_precision_for_quantity_values()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var history = new HistoryState(new InMemoryQuotaHistory([
                new QuotaSnapshot(ProviderKind.Copilot, "octocat", "AI credits", new(QuotaWindowKind.Monthly, Now.AddDays(-1), Now.AddDays(30)), 3.251m, null, null, "credits", Now, Now, QuotaSource.Delayed, QuotaConfidence.Official, Now.AddDays(30), "GitHub Copilot Business", new(3.251m, 0.251m, 3m))
            ]), new ImmediateUiDispatcher());
            using (history)
            {
                await history.InitializeAsync();
                var csv = await history.ExportCsvAsync();
                var header = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Split(',');
                var row = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1].Split(',');
                Assert.Equal(header.Length, row.Length);
                Assert.Equal("3.251", row[4]);
                Assert.Equal("0.251", row[5]);
                Assert.DoesNotContain("3,251", csv, StringComparison.Ordinal);
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [AvaloniaFact]
    public async Task History_window_shows_quantity_rows_without_percentage_text()
    {
        var history = new InMemoryQuotaHistory([
            new QuotaSnapshot(ProviderKind.Copilot, "octocat", "AI credits", new(QuotaWindowKind.Monthly, Now.AddDays(-1), Now.AddDays(30)), 3, null, null, "credits", Now, Now, QuotaSource.Delayed, QuotaConfidence.Official, Now.AddDays(30), "GitHub Copilot Business", new(3.251m, 0.251m, 3m))
        ]);
        using var vm = new MainViewModel(new EmptyDashboardSource(), quotaHistory: history);
        await vm.InitializeAsync();
        vm.Navigate(AppPage.History);
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            var historyList = window.FindControl<ListBox>("HistoryEntriesList")!;
            var visible = historyList.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsVisible).Select(text => text.Text ?? string.Empty).ToArray();
            Assert.Contains("Gross: 3.251", visible);
            Assert.Contains("Included: 0.251", visible);
            Assert.Contains("Net: 3", visible);
            Assert.Contains("Unit: credits", visible);
            Assert.DoesNotContain(visible, text => text.Contains("%", StringComparison.Ordinal));
        }
        finally { window.Close(); }
    }
}

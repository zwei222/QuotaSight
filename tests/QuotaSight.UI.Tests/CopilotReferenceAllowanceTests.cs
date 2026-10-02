using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using QuotaSight.Infrastructure;
using QuotaSight.Core;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class CopilotReferenceAllowanceTests
{
    [Theory]
    [InlineData(300d)]
    [InlineData(0d)]
    [InlineData(1900d)]
    [InlineData(3800d)]
    public async Task Gross_usage_percent_is_visible_against_manual_reference(double actual)
    {
        var expectedPercent = ((decimal)actual * 100m / 1900m).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        using var vm = new MainViewModel(new EmptyDashboardSource());
        await vm.SetCopilotReferenceAsync("1900");
        await vm.ApplyProviderSnapshotsAsync([CopilotSnapshot((decimal)actual)]);
        var card = Assert.Single(vm.Cards);
        Assert.Contains($"Usage vs manual reference: {expectedPercent}%", card.CopilotUsagePercentText);
        Assert.Contains("not", card.CopilotUsagePercentText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_gross_and_unset_reference_never_invent_a_percent()
    {
        using var vm = new MainViewModel(new EmptyDashboardSource());
        await vm.ApplyProviderSnapshotsAsync([CopilotSnapshot(null)]);
        var card = Assert.Single(vm.Cards);
        Assert.DoesNotContain("%", card.CopilotUsagePercentText);
        await vm.SetCopilotReferenceAsync("1900");
        Assert.DoesNotContain("%", vm.Cards[0].CopilotUsagePercentText);
        await vm.ApplyProviderSnapshotsAsync([CopilotSnapshot(725m)]);
        Assert.Contains("%", vm.Cards[0].CopilotUsagePercentText);
        await vm.SetCopilotReferenceAsync("");
        Assert.DoesNotContain("%", vm.Cards[0].CopilotUsagePercentText);
    }

    [AvaloniaFact]
    public async Task Percent_and_disclaimer_render_in_main_and_compact_windows_and_relocalize()
    {
        using var vm = new MainViewModel(new EmptyDashboardSource());
        await vm.SetCopilotReferenceAsync("1900");
        await vm.ApplyProviderSnapshotsAsync([CopilotSnapshot(300m)]);
        var main = new MainWindow(vm);
        var compact = new CompactQuotaWindow(vm);
        main.Show(); compact.Show();
        Assert.Contains(main.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text?.Contains("15.8%") == true);
        Assert.Contains(main.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text?.Contains("Not a personal balance or plan limit.") == true);
        Assert.Contains(compact.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text?.Contains("15.8%") == true);
        Assert.Contains(compact.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text?.Contains("Not a personal balance or plan limit.") == true);
        vm.Language = UiLanguage.Japanese;
        Assert.Contains("参考枠に対する使用率", vm.Cards[0].CopilotUsagePercentText);
        Assert.Contains("個人", vm.Cards[0].CopilotUsagePercentText);
        main.Close(); compact.Close();
    }

    private static QuotaSnapshot CopilotSnapshot(decimal? gross) => new(ProviderKind.Copilot, "acme/alex", "AI credits", new(QuotaWindowKind.Monthly, DateTimeOffset.UtcNow.AddDays(-4), DateTimeOffset.UtcNow.AddDays(26)), null, null, null, "credits", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, QuotaSource.Delayed, QuotaConfidence.Reported, null, "GitHub Copilot", new CopilotUsageBreakdown(gross, 500m, 225m));

    [AvaloniaFact]
    public async Task Configured_reference_projects_actual_gross_to_main_and_compact_cards()
    {
        var vm = new MainViewModel(new EmptyDashboardSource());
        Assert.True(await vm.SetCopilotReferenceAsync("1900"));
        var snapshot = CopilotSnapshot(725m);
        await vm.ApplyProviderSnapshotsAsync([snapshot]);
        var card = Assert.Single(vm.Cards);
        Assert.Equal(1900m, card.ManualReferenceCredits);
        Assert.Contains("725", card.CopilotReferenceText);
        Assert.Contains("1,900", card.CopilotReferenceText);
        Assert.Contains("not", card.CopilotReferenceText, StringComparison.OrdinalIgnoreCase);
        vm.Language = UiLanguage.Japanese;
        Assert.Contains("使用済み", vm.Cards[0].CopilotReferenceText);
        Assert.Contains("個人", vm.Cards[0].CopilotReferenceText);
        vm.Language = UiLanguage.English;
        Assert.True(await vm.SetCopilotReferenceAsync(""));
        Assert.Null(vm.Cards[0].ManualReferenceCredits);
        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task Settings_entry_is_live_and_reference_text_renders_in_both_templates()
    {
        var vm = new MainViewModel(new EmptyDashboardSource());
        await vm.SetCopilotReferenceAsync("1900");
        vm.SetSettings(new AppSettingsDto(CopilotReferenceCredits: 1900m), persist: false);
        await vm.ApplyProviderSnapshotsAsync([CopilotSnapshot(725m)]);
        vm.Navigate(AppPage.Settings);
        var window = new MainWindow(vm);
        window.Show();
        await window.InitializeAsync();
        var box = window.FindControl<TextBox>("CopilotReferenceCreditsBox");
        Assert.NotNull(box);
        Assert.Equal("1900", box!.Text);
        Assert.NotNull(window.FindControl<Button>("CopilotReferenceSaveButton"));
        vm.Navigate(AppPage.Dashboard);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text?.Contains("manual reference", StringComparison.OrdinalIgnoreCase) == true);
        var compact = new CompactQuotaWindow(vm);
        compact.Show();
        Assert.Contains(compact.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text?.Contains("manual reference", StringComparison.OrdinalIgnoreCase) == true);
        window.Close(); compact.Close(); vm.Dispose();
    }

    [Theory]
    [InlineData("1900", true)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("1.2", true)]
    [InlineData("bad", false)]
    public void Manual_reference_requires_positive_decimal(string input, bool expected)
    {
        var settings = new SettingsState();
        Assert.Equal(expected, settings.TrySetCopilotReference(input));
    }

    [Fact]
    public async Task Reference_roundtrips_without_becoming_a_quota_limit()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AppSettingsStore(root);
            using var vm = new MainViewModel(new EmptyDashboardSource(), settingsStore: store);
            Assert.Null(vm.Settings.CopilotReferenceCredits);
            Assert.True(await vm.SetCopilotReferenceAsync("1900"));
            var restarted = new MainViewModel(new EmptyDashboardSource(), settingsStore: store);
            await restarted.InitializeAsync();
            Assert.Equal(1900m, restarted.Settings.CopilotReferenceCredits);
            Assert.Equal(1900m, (await store.LoadAsync()).CopilotReferenceCredits);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Canceled_reference_save_leaves_live_cards_and_disk_unchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AppSettingsStore(root);
            await store.SaveAsync(new AppSettingsDto(CopilotReferenceCredits: 1900m));
            using var vm = new MainViewModel(new EmptyDashboardSource(), settingsStore: store);
            vm.SetSettings(await store.LoadAsync(), persist: false);
            await vm.ApplyProviderSnapshotsAsync([CopilotSnapshot(725m)]);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.SetCopilotReferenceAsync("2000", canceled.Token));
            Assert.Equal(1900m, vm.Settings.CopilotReferenceCredits);
            Assert.Equal(1900m, Assert.Single(vm.Cards).ManualReferenceCredits);
            Assert.Equal(1900m, (await store.LoadAsync()).CopilotReferenceCredits);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Failed_reference_save_leaves_live_settings_and_cards_unchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        File.WriteAllText(root, "not a directory");
        try
        {
            using var vm = new MainViewModel(new EmptyDashboardSource(), settingsStore: new AppSettingsStore(root));
            vm.SetSettings(new AppSettingsDto(CopilotReferenceCredits: 1900m), persist: false);
            await vm.ApplyProviderSnapshotsAsync([CopilotSnapshot(725m)]);
            await Assert.ThrowsAnyAsync<IOException>(() => vm.SetCopilotReferenceAsync("2000"));
            Assert.Equal(1900m, vm.Settings.CopilotReferenceCredits);
            Assert.Equal(1900m, Assert.Single(vm.Cards).ManualReferenceCredits);
            Assert.Equal("not a directory", File.ReadAllText(root));
        }
        finally { File.Delete(root); }
    }

    [Fact]
    public async Task Overlapping_reference_saves_leave_live_and_persisted_values_consistent()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AppSettingsStore(root);
            using var vm = new MainViewModel(new EmptyDashboardSource(), settingsStore: store);
            await vm.ApplyProviderSnapshotsAsync([CopilotSnapshot(725m)]);
            var first = vm.SetCopilotReferenceAsync("2000");
            var second = vm.SetCopilotReferenceAsync("2100");
            Assert.True(await first);
            Assert.True(await second);
            var persisted = (await store.LoadAsync()).CopilotReferenceCredits;
            Assert.Equal(persisted, vm.Settings.CopilotReferenceCredits);
            Assert.Equal(persisted, Assert.Single(vm.Cards).ManualReferenceCredits);
            Assert.Contains(persisted, new decimal?[] { 2000m, 2100m });
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

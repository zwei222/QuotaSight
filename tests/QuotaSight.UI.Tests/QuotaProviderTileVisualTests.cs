using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using QuotaSight.Infrastructure;
using QuotaSight.UI;
using QuotaSight.UI.Controls;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class QuotaProviderTileVisualTests
{
    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow;

    private static (string Name, QuotaProviderValue[] Providers)[] Services() =>
    [
        ("claude", [P(QuotaProvider.Claude, W(QuotaWindow.Session, 3800, 5), W(QuotaWindow.Weekly, 7400, 150))]),
        ("codex", [P(QuotaProvider.Codex, W(QuotaWindow.FiveHour, 0, 4), W(QuotaWindow.Weekly, null, null))]),
        ("opencode-go", [P(QuotaProvider.OpenCodeGo, W(QuotaWindow.Rolling, 6100, 3), W(QuotaWindow.Monthly, 8800, 400))]),
        ("antigravity", [P(QuotaProvider.AntigravityGemini, W(QuotaWindow.FiveHour, 7500, 4), W(QuotaWindow.Weekly, 6000, 120)), P(QuotaProvider.AntigravityClaudeGpt, W(QuotaWindow.FiveHour, 6500, 4), W(QuotaWindow.Weekly, 5500, 120))]),
        ("grok-build", [P(QuotaProvider.GrokBuild, W(QuotaWindow.Weekly, 4000, 100), W(QuotaWindow.Monthly, 8500, 500))]),
        ("copilot", [P(QuotaProvider.Copilot, new QuotaWindowValue(QuotaWindow.Premium, null, null, true), W(QuotaWindow.Chat, 5000, 20), W(QuotaWindow.Completions, 0, null))]),
    ];

    private static QuotaWindowValue W(QuotaWindow window, int? bp, int? hours) => new(window, bp, hours is { } h ? Base.AddHours(h) : null);
    private static QuotaProviderValue P(QuotaProvider provider, params QuotaWindowValue[] windows) => new(provider, QuotaProviderStatus.Available, QuotaFreshness.Fresh, Base.AddMinutes(-1), windows);

    [AvaloniaFact]
    public async Task Quota_copy_matches_language_and_updates_live_when_language_changes()
    {
        using var vm = new MainViewModel(quotaReadPort: new SyntheticQuotaPort([P(QuotaProvider.Claude, W(QuotaWindow.Session, 5000, 5))]));
        vm.SetSettings(new AppSettingsDto(Language: "English"), persist: false);
        var window = new MainWindow(vm) { Width = 1100, Height = 800 };
        try
        {
            window.Show();
            await vm.InitializeAsync(TestContext.Current.CancellationToken);
            window.UpdateLayout();
            Assert.Equal("Subscription quota", window.FindControl<TextBlock>("DashboardTitle")!.Text);
            Assert.False(window.FindControl<TextBlock>("DashboardSubtitle")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<Button>("QuotaManageButton")!.IsEffectivelyVisible);
            Assert.DoesNotContain("board", window.FindControl<Border>("QuotaContractsSection")!.Classes);
            Assert.Equal("Add a service to see its remaining quota.", window.FindControl<TextBlock>("QuotaEmptyStateText")!.Text);
            var manage = window.FindControl<Button>("QuotaManageButton")!;
            manage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.FindControl<Border>("QuotaManagePanel")!.IsEffectivelyVisible);
            var picker = window.FindControl<ComboBox>("QuotaAddPicker")!;
            Assert.Contains("Add", picker.PlaceholderText, StringComparison.Ordinal);
            picker.SelectedIndex = 0;
            await Task.Delay(50, TestContext.Current.CancellationToken);
            window.UpdateLayout();
            var remove = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "QuotaRemoveButton");
            Assert.Equal("Hide from dashboard", remove.Content);
            vm.Language = UiLanguage.Japanese;
            window.UpdateLayout();
            Assert.Equal("＋ サービスを追加", picker.PlaceholderText);
            Assert.Equal("サービスを追加すると残量が表示されます。", window.FindControl<TextBlock>("QuotaEmptyStateText")!.Text);
            Assert.Equal("表示から外す", window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "QuotaRemoveButton").Content);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Selected_service_missing_from_response_remains_removable_as_unavailable()
    {
        using var vm = new MainViewModel(quotaReadPort: new SyntheticQuotaPort([P(QuotaProvider.Claude, W(QuotaWindow.Session, 5000, 5))]));
        vm.SetSettings(new AppSettingsDto(Language: "Japanese", QuotaProviders: ["copilot"]), persist: false);
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Single(vm.QuotaCards);
        Assert.Equal("GitHub Copilot", vm.QuotaCards[0].ProviderText);
        Assert.Contains("取得できません", vm.QuotaCards[0].FreshnessText);
        Assert.Empty(vm.QuotaCards[0].Windows);
        await vm.RemoveQuotaProviderAsync("copilot");
        Assert.Empty(vm.QuotaCards);
    }

    [AvaloniaFact]
    public async Task Unavailable_service_renders_text_without_a_ring()
    {
        using var vm = new MainViewModel(quotaReadPort: new SyntheticQuotaPort([P(QuotaProvider.Claude, W(QuotaWindow.Session, 5000, 5))]));
        vm.SetSettings(new AppSettingsDto(Language: "Japanese", QuotaProviders: ["copilot"]), persist: false);
        var window = new MainWindow(vm) { Width = 1100, Height = 800 };
        try
        {
            window.Show();
            await vm.InitializeAsync(TestContext.Current.CancellationToken);
            window.UpdateLayout();
            var card = Assert.Single(vm.QuotaCards);
            Assert.True(card.IsUnavailable);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "取得できません");
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<QuotaRingControl>(), ring => ring.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Dashboard_starts_empty_even_when_quota_api_returns_services()
    {
        using var vm = new MainViewModel(quotaReadPort: new SyntheticQuotaPort(Services().SelectMany(service => service.Providers).ToArray()));
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Empty(vm.QuotaCards);
    }

    [AvaloniaFact]
    public async Task Explicit_antigravity_selection_shows_both_model_groups_without_merging()
    {
        using var vm = new MainViewModel(quotaReadPort: new SyntheticQuotaPort(Services().Single(service => service.Name == "antigravity").Providers));
        vm.SetSettings(new AppSettingsDto(QuotaProviders: ["antigravity"]), persist: false);
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, vm.QuotaCards.Count);
        Assert.Contains(vm.QuotaCards, card => card.Provider == QuotaProvider.AntigravityGemini);
        Assert.Contains(vm.QuotaCards, card => card.Provider == QuotaProvider.AntigravityClaudeGpt);
    }

    [AvaloniaFact]
    public async Task Dashboard_quota_picker_adds_then_removes_one_service_and_tokscale_surface_is_hidden()
    {
        var values = Services().SelectMany(service => service.Providers).ToArray();
        using var vm = new MainViewModel(quotaReadPort: new SyntheticQuotaPort(values));
        var main = new MainWindow(vm) { Width = 420, Height = 800 };
        var compact = new CompactQuotaWindow(vm) { Width = 410, Height = 800 };
        try
        {
            main.Show(); compact.Show();
            await vm.InitializeAsync(TestContext.Current.CancellationToken);
            main.UpdateLayout(); compact.UpdateLayout();
            Assert.True(main.FindControl<Border>("QuotaContractsSection")!.IsEffectivelyVisible);
            Assert.False(main.FindControl<Border>("TokScaleSection")!.IsEffectivelyVisible);
            Assert.False(main.FindControl<Border>("TokScalePanel")!.IsEffectivelyVisible);
            Assert.False(compact.FindControl<ItemsControl>("CompactTokScaleCards")!.IsEffectivelyVisible);
            Assert.False(compact.FindControl<TextBlock>("CompactTokScaleStatus")!.IsEffectivelyVisible);
            Assert.Empty(vm.QuotaCards);
            var manageButton = main.FindControl<Button>("QuotaManageButton")!;
            manageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            main.UpdateLayout();
            var picker = main.FindControl<ComboBox>("QuotaAddPicker")!;
            Assert.True(picker.IsEffectivelyVisible);
            Assert.True(picker.Bounds.Width > 0);
            picker.SelectedIndex = 0;
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Single(vm.QuotaCards);
            var restored = new MainViewModel(quotaReadPort: new SyntheticQuotaPort(values));
            restored.SetSettings(vm.CurrentSettingsForTests, persist: false);
            await restored.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Single(restored.QuotaCards);
            restored.Dispose();
            var remove = main.GetVisualDescendants().OfType<Button>().First(button => button.Name == "QuotaRemoveButton");
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Empty(vm.QuotaCards);
        }
        finally { main.Close(); compact.Close(); }
    }

    [AvaloniaFact]
    public async Task Japanese_quota_dashboard_capture_evidence_is_opt_in()
    {
        await CaptureAndCheck("ja-initial-empty", [], 420, compact: false, japanese: true, dark: true);
        await CaptureAndCheck("ja-claude-only", [P(QuotaProvider.Claude, W(QuotaWindow.Session, 6400, 7), W(QuotaWindow.Weekly, 2800, 60))], 420, compact: false, japanese: true, dark: true);
    }

    [AvaloniaFact]
    public async Task Redesigned_dashboard_evidence_is_opt_in_at_desktop_and_narrow_widths()
    {
        var one = Services()[0].Providers;
        var several = one.Concat(Services()[1].Providers).ToArray();
        foreach (var width in new[] { 1100, 420 })
        {
            await CaptureAndCheck("redesign-empty-ja-dark", [], width, compact: false, japanese: true, dark: true);
            await CaptureAndCheck("redesign-one-ja-dark", one, width, compact: false, japanese: true, dark: true);
            await CaptureAndCheck("redesign-many-ja-dark", several, width, compact: false, japanese: true, dark: true);
            await CaptureAndCheck("redesign-manage-ja-dark", several, width, compact: false, japanese: true, dark: true, managementOpen: true);
        }
    }

    [AvaloniaFact]
    public async Task Production_quota_templates_keep_every_service_card_and_ring_inside_formal_bounds()
    {
        foreach (var (name, providers) in Services())
            foreach (var (width, compact) in new[] { (1100, false), (420, false), (410, true) })
                await CaptureAndCheck(name, providers, width, compact, japanese: false);
        var all = Services().SelectMany(service => service.Providers).ToArray();
        foreach (var (width, compact) in new[] { (1100, false), (420, false), (410, true) })
            await CaptureAndCheck("all-ja", all, width, compact, japanese: true);
    }

    [AvaloniaFact]
    public async Task State_variants_render_inside_bounds_for_all_widths_and_languages()
    {
        var states = new (string Name, QuotaProviderValue[] Providers)[]
        {
            ("state-multiple-sources", [new QuotaProviderValue(QuotaProvider.Claude, QuotaProviderStatus.Unavailable, QuotaFreshness.Unknown, null, [], true), P(QuotaProvider.Codex, W(QuotaWindow.FiveHour, 0, 4), W(QuotaWindow.Weekly, null, null))]),
            ("state-stale", [new QuotaProviderValue(QuotaProvider.Claude, QuotaProviderStatus.Available, QuotaFreshness.Cached, Base.AddHours(-9), [W(QuotaWindow.Session, 3800, 5), W(QuotaWindow.Weekly, 7400, 150)])]),
            ("state-past-reset", [P(QuotaProvider.Claude, W(QuotaWindow.Session, 4000, -1), W(QuotaWindow.Weekly, 7000, 150))]),
            ("state-zero-unknown", [P(QuotaProvider.Codex, W(QuotaWindow.FiveHour, 0, 4), W(QuotaWindow.Weekly, null, null)), P(QuotaProvider.Claude, W(QuotaWindow.Session, null, null), W(QuotaWindow.Weekly, 0, 100))]),
        };
        foreach (var (name, providers) in states)
            foreach (var japanese in new[] { false, true })
                foreach (var (width, compact) in new[] { (1100, false), (420, false), (410, true) })
                    await CaptureAndCheck(name + (japanese ? "-ja" : "-en"), providers, width, compact, japanese);
    }

    [AvaloniaFact]
    public async Task Narrow_headings_and_badges_sit_on_separate_rows_so_the_title_never_collapses()
    {
        using var vm = new MainViewModel(quotaReadPort: new SyntheticQuotaPort(Services()[0].Providers));
        vm.Language = UiLanguage.Japanese;
        var window = new MainWindow(vm) { Width = 420, Height = 740 };
        try
        {
            window.Show();
            await vm.InitializeAsync(TestContext.Current.CancellationToken);
            window.UpdateLayout();
            foreach (var (title, badge) in new[] { ("TokScaleTitle", "TokScaleBadge") })
            {
                var t = window.FindControl<TextBlock>(title)!; var b = window.FindControl<TextBlock>(badge)!;
                if (!t.IsEffectivelyVisible) continue;
                Assert.True(t.Bounds.Width >= 200, $"{title} collapsed to {t.Bounds.Width}px");
                Assert.True(t.Bounds.Height < 60, $"{title} wraps char-by-char (height {t.Bounds.Height})");
                var tp = t.TranslatePoint(default, window)!.Value; var bp = b.TranslatePoint(default, window)!.Value;
                Assert.True(bp.Y >= tp.Y + t.Bounds.Height - 1, "badge must be on its own row below the heading");
            }
        }
        finally { window.Close(); }
    }

    private static async Task CaptureAndCheck(string name, QuotaProviderValue[] providers, int width, bool compact, bool japanese, bool dark = false, bool managementOpen = false)
    {
        using var vm = new MainViewModel(quotaReadPort: new SyntheticQuotaPort(providers));
        vm.SetSettings(new AppSettingsDto(Theme: dark ? "Dark" : "System", Language: japanese ? "Japanese" : "English", QuotaProviders: providers.Select(provider => provider.Provider switch { QuotaProvider.Claude => "claude", QuotaProvider.Codex => "codex", QuotaProvider.OpenCodeGo => "opencode-go", QuotaProvider.AntigravityGemini or QuotaProvider.AntigravityClaudeGpt => "antigravity", QuotaProvider.GrokBuild => "grok-build", _ => "copilot" }).Distinct().ToArray()), persist: false);
        vm.Language = japanese ? UiLanguage.Japanese : UiLanguage.English;
        Window window = compact ? new CompactQuotaWindow(vm) { Width = width, Height = 900 } : new MainWindow(vm) { Width = width, Height = 900 };
        try
        {
            window.Show();
            await vm.InitializeAsync(TestContext.Current.CancellationToken);
            window.UpdateLayout();
            if (managementOpen && !compact)
            {
                var manage = window.FindControl<Button>("QuotaManageButton")!;
                manage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Assert.True(window.FindControl<Border>("QuotaManagePanel")!.IsEffectivelyVisible);
            }
            var panel = compact ? window.FindControl<Border>("CompactQuotaContracts")! : window.FindControl<Border>("QuotaContractsSection")!;
            Assert.True(panel.IsVisible);
            Assert.Equal(providers.Length, vm.QuotaCards.Count);
            var rings = window.GetVisualDescendants().OfType<QuotaRingControl>().Where(ring => ring.IsEffectivelyVisible).ToArray();
            Assert.Equal(providers.Count(provider => provider.Status == QuotaProviderStatus.Available && !provider.MultipleSources), rings.Length);
            Assert.All(rings, ring => Assert.Equal(compact ? 72 : 86, (int)ring.Bounds.Width));
            var cards = window.GetVisualDescendants().OfType<Border>().Where(border => border.DataContext is QuotaProviderCard && border.Classes.Contains(compact ? "tokscale" : "tile")).ToArray();
            Assert.Equal(providers.Length, cards.Length);
            Assert.All(cards, card => { Assert.True(card.Bounds.Width <= panel.Bounds.Width + 1); Assert.True(card.Bounds.Width >= (compact ? 250 : 300) - 1); });
            Assert.All(window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.DataContext is QuotaProviderCard && t.IsEffectivelyVisible && t.Text == ((QuotaProviderCard)t.DataContext!).ProviderText), t => Assert.True(t.Bounds.Width > 80, $"{t.Text} collapsed"));
            Assert.Equal(width, (int)window.Bounds.Width);
            var dir = Environment.GetEnvironmentVariable("QUOTASIGHT_UI_PNG_DIR");
            if (string.IsNullOrWhiteSpace(dir)) return;
            dir = Path.GetFullPath(dir);
            Directory.CreateDirectory(dir);
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, (int)window.Bounds.Height));
            bitmap.Render(window);
            bitmap.Save(Path.Combine(dir, $"quota-{name}-{(managementOpen ? "management-" : string.Empty)}{(compact ? "compact" : width == 420 ? "420" : "normal")}.png"), PngBitmapEncoderOptions.Default);
        }
        finally { window.Close(); }
    }

    private sealed class SyntheticQuotaPort(QuotaProviderValue[] providers) : IQuotaReadPort
    {
        public Task<LatestQuota> ReadLatestAsync(CancellationToken cancellationToken) => Task.FromResult(new LatestQuota(Base, providers));
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
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

    private static async Task CaptureAndCheck(string name, QuotaProviderValue[] providers, int width, bool compact, bool japanese)
    {
        using var vm = new MainViewModel(quotaReadPort: new SyntheticQuotaPort(providers));
        vm.Language = japanese ? UiLanguage.Japanese : UiLanguage.English;
        Window window = compact ? new CompactQuotaWindow(vm) { Width = width, Height = 900 } : new MainWindow(vm) { Width = width, Height = 900 };
        try
        {
            window.Show();
            await vm.InitializeAsync(TestContext.Current.CancellationToken);
            window.UpdateLayout();
            var panel = compact ? window.FindControl<Border>("CompactQuotaContracts")! : window.FindControl<Border>("QuotaContractsSection")!;
            Assert.True(panel.IsVisible);
            Assert.Equal(providers.Length, vm.QuotaCards.Count);
            var rings = window.GetVisualDescendants().OfType<QuotaRingControl>().ToArray();
            Assert.Equal(providers.Length, rings.Length);
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
            bitmap.Save(Path.Combine(dir, $"quota-{name}-{(compact ? "compact" : width == 420 ? "420" : "normal")}.png"), PngBitmapEncoderOptions.Default);
        }
        finally { window.Close(); }
    }

    private sealed class SyntheticQuotaPort(QuotaProviderValue[] providers) : IQuotaReadPort
    {
        public Task<LatestQuota> ReadLatestAsync(CancellationToken cancellationToken) => Task.FromResult(new LatestQuota(Base, providers));
    }
}

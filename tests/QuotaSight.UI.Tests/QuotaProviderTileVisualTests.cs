using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Headless;
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
    internal static readonly DateTimeOffset Base = DateTimeOffset.UtcNow;

    private static (string Name, QuotaProviderValue[] Providers)[] Services() =>
    [
        ("claude", [P(QuotaProvider.Claude, W(QuotaWindow.Session, 3800, 5), W(QuotaWindow.Weekly, 7400, 150))]),
        ("codex", [P(QuotaProvider.Codex, W(QuotaWindow.FiveHour, 0, 4), W(QuotaWindow.Weekly, null, null))]),
        ("opencode-go", [P(QuotaProvider.OpenCodeGo, W(QuotaWindow.Rolling, 6100, 3), W(QuotaWindow.Monthly, 8800, 400))]),
        ("antigravity", [P(QuotaProvider.AntigravityGemini, W(QuotaWindow.FiveHour, 7500, 4), W(QuotaWindow.Weekly, 6000, 120)), P(QuotaProvider.AntigravityClaudeGpt, W(QuotaWindow.FiveHour, 6500, 4), W(QuotaWindow.Weekly, 5500, 120))]),
        ("grok-build", [P(QuotaProvider.GrokBuild, W(QuotaWindow.Weekly, 4000, 100), W(QuotaWindow.Monthly, 8500, 500))]),
        ("copilot", [P(QuotaProvider.Copilot, new QuotaWindowValue(QuotaWindow.Premium, null, null, true), W(QuotaWindow.Chat, 5000, 20), W(QuotaWindow.Completions, 0, null))]),
    ];

    internal static QuotaWindowValue W(QuotaWindow window, int? bp, int? hours) => new(window, bp, hours is { } h ? Base.AddHours(h) : null);
    internal static QuotaProviderValue P(QuotaProvider provider, params QuotaWindowValue[] windows) => new(provider, QuotaProviderStatus.Available, QuotaFreshness.Fresh, Base.AddMinutes(-1), windows);

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
            Assert.Equal("Add the subscriptions whose remaining quota you want to see.", window.FindControl<TextBlock>("QuotaEmptyStateText")!.Text);
            var manage = window.FindControl<Button>("QuotaManageButton")!;
            manage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.FindControl<Border>("QuotaManagePanel")!.IsEffectivelyVisible);
            var addButton = window.GetVisualDescendants().OfType<Button>().First(button => button.Name == "QuotaAddButton");
            Assert.Equal("Add", addButton.Content);
            addButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(50, TestContext.Current.CancellationToken);
            window.UpdateLayout();
            var remove = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "QuotaRemoveButton");
            Assert.Equal("Hide from dashboard", remove.Content);
            vm.Language = UiLanguage.Japanese;
            window.UpdateLayout();
            Assert.Equal("追加", window.GetVisualDescendants().OfType<Button>().First(button => button.Name == "QuotaAddButton").Content);
            Assert.Equal("残量を確認したい契約を追加してください。", window.FindControl<TextBlock>("QuotaEmptyStateText")!.Text);
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
        Assert.Contains("コレクターから届いていません", vm.QuotaCards[0].FreshnessText);
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
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "コレクターから\n届いていません");
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
            var firstAdd = main.GetVisualDescendants().OfType<Button>().First(button => button.Name == "QuotaAddButton");
            Assert.True(firstAdd.IsEffectivelyVisible);
            Assert.True(firstAdd.Bounds.Width > 0);
            firstAdd.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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

    private static async Task<(MainViewModel Vm, MainWindow Window)> OpenSixServiceWindow(int width, bool selectAll)
    {
        var vm = new MainViewModel(timeProvider: new MockFixedTimeProvider(MockNow), quotaReadPort: new SyntheticQuotaPort(SixStateFixture(), MockNow));
        vm.SetSettings(new AppSettingsDto(Theme: "Dark", Language: "Japanese", QuotaProviders: selectAll ? ["codex", "opencode-go", "grok-build", "copilot", "claude", "antigravity"] : []), persist: false);
        var window = new MainWindow(vm) { Width = width, Height = 1019 };
        window.Show();
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        window.UpdateLayout();
        return (vm, window);
    }
    private static void Capture(MainWindow window, string name, int width)
    {
        var dir = Environment.GetEnvironmentVariable("QUOTASIGHT_UI_PNG_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, 1019));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(dir, name), PngBitmapEncoderOptions.Default);
    }
    private static IEnumerable<Button> Named(Visual root, string name) => root.GetVisualDescendants().OfType<Button>().Where(button => button.Name == name && button.IsEffectivelyVisible);

    [AvaloniaFact]
    public async Task Manage_panel_is_closed_at_first_opens_with_focus_closes_with_escape_and_returns_focus()
    {
        var (vm, window) = await OpenSixServiceWindow(1164, true);
        try
        {
            var panel = window.FindControl<Border>("QuotaManagePanel")!;
            var manage = window.FindControl<Button>("QuotaManageButton")!;
            Assert.False(panel.IsVisible);
            manage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.True(panel.IsEffectivelyVisible);
            Assert.True(Named(panel, "QuotaRemoveButton").First().IsFocused, "first Hide button takes focus on open");
            Assert.Equal("表示中（6）", vm.QuotaShownHeaderText);
            Assert.Equal("追加できるサービス（0）", vm.QuotaAddableHeaderText);
            Assert.Equal(new[] { "Codex", "OpenCode Go", "Grok Build", "GitHub Copilot", "Claude", "Antigravity" }, vm.QuotaShownServices.Select(item => item.Name));
            Assert.Equal(720, (int)panel.Bounds.Width);
            var columns = window.FindControl<Grid>("QuotaManageColumns")!;
            Assert.Equal(2, columns.ColumnDefinitions.Count);
            Capture(window, "themis-management-1164.png", 1164);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.UpdateLayout();
            Assert.False(panel.IsVisible);
            Assert.True(manage.IsFocused, "Esc returns focus to the trigger");
        }
        finally { window.Close(); vm.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Manage_panel_hides_and_adds_services_immediately_without_deleting_data_and_antigravity_is_one_selection_two_cards()
    {
        var values = new[] { P(QuotaProvider.Codex, W(QuotaWindow.FiveHour, 5000, 4)), P(QuotaProvider.AntigravityGemini, W(QuotaWindow.FiveHour, 7500, 4)), P(QuotaProvider.AntigravityClaudeGpt, W(QuotaWindow.FiveHour, 6500, 4)) };
        using var vm = new MainViewModel(quotaReadPort: new SyntheticQuotaPort(values));
        vm.SetSettings(new AppSettingsDto(Language: "Japanese", QuotaProviders: ["codex"]), persist: false);
        var window = new MainWindow(vm) { Width = 1164, Height = 900 };
        try
        {
            window.Show();
            await vm.InitializeAsync(TestContext.Current.CancellationToken);
            window.FindControl<Button>("QuotaManageButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.Equal("追加できるサービス（5）", vm.QuotaAddableHeaderText);
            Assert.Contains(vm.QuotaAddableServices, item => item.Id == "antigravity" && item.Sub == "Gemini系とClaude/GPT系の2枚");
            var addAntigravity = Named(window, "QuotaAddButton").Single(button => ((QuotaServiceItem)button.DataContext!).Id == "antigravity");
            addAntigravity.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100, TestContext.Current.CancellationToken); window.UpdateLayout();
            Assert.Equal(new[] { "codex", "antigravity" }, vm.Settings.QuotaProviders);
            Assert.Equal(2, vm.QuotaShownServices.Count);
            Assert.Equal(3, vm.QuotaCards.Count); // one selection, two Antigravity cards
            Assert.Equal(3, window.GetVisualDescendants().OfType<Border>().Count(border => border.DataContext is QuotaProviderCard && border.Classes.Contains("tile")));
            Assert.Equal("表示中（2）", vm.QuotaShownHeaderText);
            Named(window, "QuotaRemoveButton").First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100, TestContext.Current.CancellationToken); window.UpdateLayout();
            Assert.Equal(new[] { "antigravity" }, vm.Settings.QuotaProviders);
            Assert.Equal(2, vm.QuotaCards.Count);
            Assert.NotNull(vm.LatestQuota); // hiding never deletes the collected data
            Assert.Contains(vm.LatestQuota!.Providers, provider => provider.Provider == QuotaProvider.Codex);
            Assert.True(Named(window, "QuotaRemoveButton").Single().IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Empty_state_matches_the_mock_and_its_button_opens_the_panel_on_the_add_list()
    {
        var (vm, window) = await OpenSixServiceWindow(1164, false);
        try
        {
            Assert.Empty(vm.QuotaCards);
            var empty = window.FindControl<Border>("QuotaEmptyState")!;
            Assert.True(empty.IsEffectivelyVisible);
            Assert.Equal("残量を確認したい契約を追加してください。", window.FindControl<TextBlock>("QuotaEmptyStateText")!.Text);
            Assert.Contains(empty.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "表示するサービスがありません");
            Assert.Equal("サービスを追加", Assert.IsType<string>(window.FindControl<Button>("QuotaEmptyAddButton")!.Content));
            Capture(window, "themis-empty-1164.png", 1164);
            window.FindControl<Button>("QuotaEmptyAddButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.True(window.FindControl<Border>("QuotaManagePanel")!.IsEffectivelyVisible);
            Assert.True(Named(window, "QuotaAddButton").First().IsFocused);
            Assert.Equal("表示中（0）", vm.QuotaShownHeaderText);
            Assert.Equal(6, vm.QuotaAddableServices.Count);
        }
        finally { window.Close(); vm.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Manage_panel_stacks_its_two_lists_at_narrow_width_without_overflow()
    {
        var (vm, window) = await OpenSixServiceWindow(420, true);
        try
        {
            window.FindControl<Button>("QuotaManageButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            var panel = window.FindControl<Border>("QuotaManagePanel")!;
            Assert.Single(window.FindControl<Grid>("QuotaManageColumns")!.ColumnDefinitions);
            Assert.True(panel.Bounds.Width <= 420 - 32 + 1);
            Capture(window, "themis-management-420.png", 420);
        }
        finally { window.Close(); vm.Dispose(); }
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

    [Fact]
    public void Antigravity_with_no_readable_model_group_collapses_to_one_plain_card_but_keeps_variants_when_one_reads()
    {
        var failed = QuotaCardProjection.Project(new LatestQuota(Base, [new(QuotaProvider.AntigravityGemini, QuotaProviderStatus.Unavailable, QuotaFreshness.Unknown, null, []), new(QuotaProvider.AntigravityClaudeGpt, QuotaProviderStatus.Unavailable, QuotaFreshness.Unknown, null, [])]), japanese: true, now: Base);
        var single = Assert.Single(failed);
        Assert.Equal("Antigravity", single.ProviderText);
        var mixed = QuotaCardProjection.Project(new LatestQuota(Base, [new(QuotaProvider.AntigravityGemini, QuotaProviderStatus.Unavailable, QuotaFreshness.Unknown, null, []), P(QuotaProvider.AntigravityClaudeGpt, W(QuotaWindow.FiveHour, 5000, 4))]), japanese: true, now: Base);
        Assert.Equal(2, mixed.Count);
        Assert.Contains("Gemini", mixed[0].ProviderText);
    }

    internal static readonly DateTimeOffset MockNow = DateTimeOffset.Parse("2026-11-23T04:04:00+09:00");
    private static DateTimeOffset MockAt(string value) => DateTimeOffset.Parse(value + "+09:00");
    private static QuotaProviderValue MockProvider(QuotaProvider provider, QuotaFreshness freshness, DateTimeOffset? observed, params QuotaWindowValue[] windows) =>
        new(provider, windows.Length == 0 ? QuotaProviderStatus.Unavailable : QuotaProviderStatus.Available, freshness, observed, windows);

    internal static QuotaProviderValue[] SixStateFixture() =>
    [
        // Claude is selected but absent from the response: "not received from the collector".
        MockProvider(QuotaProvider.GrokBuild, QuotaFreshness.Fresh, MockNow.AddHours(-14), new QuotaWindowValue(QuotaWindow.Weekly, 0, MockAt("2026-11-27T20:45:00"))),
        MockProvider(QuotaProvider.AntigravityGemini, QuotaFreshness.Unknown, null),
        MockProvider(QuotaProvider.Copilot, QuotaFreshness.Unknown, null,
            new QuotaWindowValue(QuotaWindow.Premium, null, null, true), new QuotaWindowValue(QuotaWindow.Chat, null, null, true), new QuotaWindowValue(QuotaWindow.Completions, null, null, true)),
        MockProvider(QuotaProvider.OpenCodeGo, QuotaFreshness.Cached, MockNow.AddMinutes(-18),
            new QuotaWindowValue(QuotaWindow.Rolling, 10000, MockAt("2026-11-23T07:41:00")),
            new QuotaWindowValue(QuotaWindow.Weekly, 0, MockAt("2026-11-23T09:00:00")),
            new QuotaWindowValue(QuotaWindow.Monthly, 4300, MockAt("2026-12-04T21:58:00"))),
        MockProvider(QuotaProvider.Codex, QuotaFreshness.Fresh, MockNow.AddMinutes(-4),
            new QuotaWindowValue(QuotaWindow.FiveHour, 4100, MockAt("2026-11-23T05:09:00")),
            new QuotaWindowValue(QuotaWindow.Weekly, 7500, MockAt("2026-11-29T19:08:00")))
    ];

    [AvaloniaFact]
    public async Task Six_service_dashboard_uses_three_equal_158px_tiles_at_desktop_width()
    {
        var fixture = SixStateFixture();
        using var vm = new MainViewModel(timeProvider: new MockFixedTimeProvider(MockNow), quotaReadPort: new SyntheticQuotaPort(fixture, MockNow));
        vm.SetSettings(new AppSettingsDto(Theme: "Dark", Language: "Japanese", QuotaProviders: ["codex", "opencode-go", "grok-build", "copilot", "claude", "antigravity"]), persist: false);
        var window = new MainWindow(vm) { Width = 1164, Height = 1019 };
        try
        {
            window.Show();
            await vm.InitializeAsync(TestContext.Current.CancellationToken);
            window.UpdateLayout();
            var cards = window.GetVisualDescendants().OfType<Border>().Where(border => border.DataContext is QuotaProviderCard && border.Classes.Contains("tile")).ToArray();
            Assert.Equal(6, cards.Length);
            var unavailableCards = cards.Where(card => ((QuotaProviderCard)card.DataContext!).IsUnavailable).ToArray();
            Assert.Equal(2, unavailableCards.Length);
            foreach (var unavailable in unavailableCards)
                Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), text => ReferenceEquals(text.DataContext, unavailable.DataContext) && text.IsEffectivelyVisible && text.Text == ((QuotaProviderCard)unavailable.DataContext!).UnavailableMessage);
            Assert.Equal(new[] { QuotaProvider.Codex, QuotaProvider.OpenCodeGo, QuotaProvider.GrokBuild, QuotaProvider.Copilot, QuotaProvider.Claude, QuotaProvider.AntigravityGemini }, vm.QuotaCards.Select(card => card.Provider));
            Assert.Equal("コレクターから\n届いていません", vm.QuotaCards.Single(card => card.Provider == QuotaProvider.Claude).UnavailableMessage);
            var antigravityCard = vm.QuotaCards.Single(card => card.Provider == QuotaProvider.AntigravityGemini);
            Assert.Equal("Antigravity", antigravityCard.ProviderText);
            Assert.Equal("残量を\n取得できませんでした", antigravityCard.UnavailableMessage);
            Assert.Matches(@"^最終収集 \d\d:\d\d$", window.FindControl<TextBlock>("QuotaCollectedLabel")!.Text);
            Assert.True(window.FindControl<TextBlock>("QuotaCollectedLabel")!.IsEffectivelyVisible);
            // Approved mock: the left bar marks the lowest window only when it was picked out of several valid finite windows.
            Assert.Equal(new[] { QuotaWindow.Weekly }, vm.QuotaCards.Single(card => card.Provider == QuotaProvider.OpenCodeGo).Windows.Where(w => w.IsEmphasized).Select(w => w.Window));
            Assert.Equal(new[] { QuotaWindow.FiveHour }, vm.QuotaCards.Single(card => card.Provider == QuotaProvider.Codex).Windows.Where(w => w.IsEmphasized).Select(w => w.Window));
            Assert.DoesNotContain(vm.QuotaCards.Single(card => card.Provider == QuotaProvider.GrokBuild).Windows, w => w.IsEmphasized);
            Assert.DoesNotContain(vm.QuotaCards.Single(card => card.Provider == QuotaProvider.Copilot).Windows, w => w.IsEmphasized);
            Assert.Equal(2, window.GetVisualDescendants().OfType<Border>().Count(marker => marker.IsEffectivelyVisible && marker.Classes.Contains("quota-marker")));
            var dashed = window.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Where(ellipse => ellipse.IsEffectivelyVisible && ellipse.StrokeDashArray is not null).ToArray();
            Assert.All(dashed, ellipse => { Assert.Equal(6, ellipse.StrokeThickness); Assert.Equal(new[] { 0.5, 0.6667 }, ellipse.StrokeDashArray!.ToArray()); Assert.Equal(.6, ellipse.Opacity); });
            var faint = window.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single(ellipse => ellipse.IsEffectivelyVisible && ellipse.StrokeDashArray is null);
            Assert.Equal(.35, faint.Opacity);
            Assert.Equal(6, faint.StrokeThickness);
            Assert.Equal(24, window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "∞" && text.IsEffectivelyVisible).FontSize);
            var openCode = vm.QuotaCards.Single(card => card.Provider == QuotaProvider.OpenCodeGo);
            Assert.Equal("0%", openCode.CenterText);
            Assert.Equal(QuotaWindow.Weekly, openCode.Windows.Single(window => window.IsPrimary).Window);
            Assert.Equal("キャッシュ · 18分前", openCode.FreshnessText);
            Assert.Equal("0%", openCode.Windows.Single(window => window.Window == QuotaWindow.Weekly).RemainingText);
            var copilot = vm.QuotaCards.Single(card => card.Provider == QuotaProvider.Copilot);
            Assert.True(copilot.IsUnlimitedOnly);
            Assert.All(copilot.Windows, quotaWindow => Assert.Empty(quotaWindow.ResetText));
            var grok = vm.QuotaCards.Single(card => card.Provider == QuotaProvider.GrokBuild);
            Assert.True(grok.IsWarning);
            Assert.Equal("14時間前の値", grok.FreshnessText);
            var openCodeCard = cards.Single(card => ((QuotaProviderCard)card.DataContext!).Provider == QuotaProvider.OpenCodeGo);
            Assert.Contains(openCodeCard.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "0%" && text.IsEffectivelyVisible);
            Assert.Contains(openCodeCard.GetVisualDescendants().OfType<Border>(), marker => marker.IsEffectivelyVisible && marker.DataContext is QuotaCardWindow { IsPrimary: true });
            var infinity = cards.Single(card => ((QuotaProviderCard)card.DataContext!).Provider == QuotaProvider.Copilot).GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "∞");
            Assert.True(infinity.IsEffectivelyVisible);
            var first = cards.OrderBy(card => card.TranslatePoint(default, window)!.Value.Y).ThenBy(card => card.TranslatePoint(default, window)!.Value.X).ToArray();
            Assert.All(first, card => Assert.Equal(158, (int)Math.Round(card.Bounds.Height)));
            var row1 = first.Take(3).Select(card => card.TranslatePoint(default, window)!.Value).ToArray();
            var row2 = first.Skip(3).Select(card => card.TranslatePoint(default, window)!.Value).ToArray();
            Assert.True(row1[0].X < row1[1].X && row1[1].X < row1[2].X);
            Assert.Equal(row1.Select(point => point.X), row2.Select(point => point.X));
            Assert.All(first.Skip(1), card => Assert.InRange(Math.Abs(card.Bounds.Width - first[0].Bounds.Width), 0, .5));
            Assert.All(first.Take(2).Zip(first.Skip(1), (left, right) => right.TranslatePoint(default, window)!.Value.X - left.TranslatePoint(default, window)!.Value.X - left.Bounds.Width), gap => Assert.InRange(Math.Abs(gap - 12), 0, 1));
            Assert.InRange(Math.Abs((row1[2].X + first[2].Bounds.Width) - (row1[0].X + first[0].Bounds.Width + first[1].Bounds.Width + first[2].Bounds.Width + 24)), 0, 1.1);
            var ringPositions = window.GetVisualDescendants().OfType<QuotaRingControl>().Where(ring => ring.IsEffectivelyVisible).Select(ring => ring.TranslatePoint(default, window)!.Value).ToArray();
            Assert.Equal(4, ringPositions.Length);
            Assert.Equal(2, window.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Count(ellipse => ellipse.IsEffectivelyVisible && ellipse.StrokeDashArray is not null));
            Assert.All(first.Where(card => !((QuotaProviderCard)card.DataContext!).IsUnavailable), card =>
            {
                var cardTopLeft = card.TranslatePoint(default, window)!.Value;
                var ring = card.GetVisualDescendants().OfType<QuotaRingControl>().Single();
                var ringTopLeft = ring.TranslatePoint(default, window)!.Value;
                Assert.InRange(Math.Abs(ringTopLeft.X - cardTopLeft.X - 17), 0, 1);
                Assert.InRange(Math.Abs(ringTopLeft.Y - cardTopLeft.Y - 53), 0, 1);
            });
            var pngDir = Environment.GetEnvironmentVariable("QUOTASIGHT_UI_PNG_DIR");
            if (!string.IsNullOrWhiteSpace(pngDir))
            {
                Directory.CreateDirectory(pngDir);
                using var bitmap = new RenderTargetBitmap(new PixelSize(1164, 1019));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(pngDir, "themis-six-state-1164.png"), PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Six_service_dashboard_uses_one_column_at_narrow_width_and_captures_fixture()
    {
        var fixture = SixStateFixture();
        using var vm = new MainViewModel(timeProvider: new MockFixedTimeProvider(MockNow), quotaReadPort: new SyntheticQuotaPort(fixture, MockNow));
        vm.SetSettings(new AppSettingsDto(Theme: "Dark", Language: "Japanese", QuotaProviders: ["codex", "opencode-go", "grok-build", "copilot", "claude", "antigravity"]), persist: false);
        var window = new MainWindow(vm) { Width = 420, Height = 1019 };
        try
        {
            window.Show();
            await vm.InitializeAsync(TestContext.Current.CancellationToken);
            window.UpdateLayout();
            var cards = window.GetVisualDescendants().OfType<Border>().Where(border => border.DataContext is QuotaProviderCard && border.Classes.Contains("tile")).ToArray();
            Assert.Equal(6, cards.Length);
            Assert.All(cards, card => Assert.Equal(158, (int)Math.Round(card.Bounds.Height)));
            var ordered = cards.OrderBy(card => card.TranslatePoint(default, window)!.Value.Y).ToArray();
            Assert.All(ordered.Skip(1).Zip(ordered, (next, current) => next.TranslatePoint(default, window)!.Value.Y - current.TranslatePoint(default, window)!.Value.Y), gap => Assert.True(gap >= 158));
            var pngDir = Environment.GetEnvironmentVariable("QUOTASIGHT_UI_PNG_DIR");
            if (!string.IsNullOrWhiteSpace(pngDir))
            {
                Directory.CreateDirectory(pngDir);
                using var bitmap = new RenderTargetBitmap(new PixelSize(420, 1019));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(pngDir, "themis-six-state-420.png"), PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
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
            Control panel = compact ? window.FindControl<StackPanel>("CompactQuotaContracts")! : window.FindControl<Border>("QuotaContractsSection")!;
            Assert.True(panel.IsVisible);
            Assert.Equal(providers.Length, vm.QuotaCards.Count);
            var rings = window.GetVisualDescendants().OfType<QuotaRingControl>().Where(ring => ring.IsEffectivelyVisible).ToArray();
            Assert.Equal(providers.Count(provider => provider.Status == QuotaProviderStatus.Available && !provider.MultipleSources), rings.Length);
            Assert.All(rings, ring => Assert.Equal(compact ? 64 : 88, (int)ring.Bounds.Width));
            var cards = window.GetVisualDescendants().OfType<Border>().Where(border => border.DataContext is QuotaProviderCard && border.Classes.Contains(compact ? "tokscale" : "tile")).ToArray();
            Assert.Equal(providers.Length, cards.Length);
            Assert.All(cards, card => { Assert.True(card.Bounds.Width <= panel.Bounds.Width + 1); Assert.True(card.Bounds.Width >= (compact ? 250 : 260) - 1); });
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

    internal sealed class MockFixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    internal sealed class SyntheticQuotaPort(QuotaProviderValue[] providers, DateTimeOffset? collectedAt = null) : IQuotaReadPort
    {
        public Task<LatestQuota> ReadLatestAsync(CancellationToken cancellationToken) => Task.FromResult(new LatestQuota(collectedAt ?? Base, providers));
    }
}

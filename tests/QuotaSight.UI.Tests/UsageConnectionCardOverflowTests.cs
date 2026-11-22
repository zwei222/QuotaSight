using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using QuotaSight.Application;
using QuotaSight.Infrastructure;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// Narrow-window layout tests for the Usage connection card. Every banner line, credential
/// pill and error sentence must stay inside the card's inner border instead of running off
/// the right edge: at 520px, in both languages, in every connection state, and with a
/// deliberately wide font. Bounds alone cannot prove this — a text element can sit inside
/// the card and still clip its own glyphs — so the widest rendered text line is also
/// measured against its element.
/// </summary>
public sealed class UsageConnectionCardOverflowTests
{
    private const double WindowWidth = 520;
    private const double Tolerance = 0.5;
    private const double PillSpacing = 8;
    private const string Endpoint = "https://usage.example.test";
    private const string NonHttpsEndpoint = "http://usage.example.test";

    [AvaloniaFact]
    public void The_banner_is_a_star_text_column_and_the_pills_are_a_wrap_panel()
    {
        using var scope = Scope.Create(out _);
        var window = scope.Window;

        var banner = window.FindControl<Grid>("UsageStatusBannerGrid")!;
        var columns = banner.ColumnDefinitions;
        Assert.Equal(2, columns.Count);
        Assert.Equal(GridUnitType.Auto, columns[0].Width.GridUnitType);
        Assert.Equal(GridUnitType.Star, columns[1].Width.GridUnitType);

        // While no icon is shown the status copy stays flush with the banner padding.
        var status = window.FindControl<TextBlock>("UsageConnectionStatusText")!;
        Assert.Equal(TextWrapping.Wrap, status.TextWrapping);
        Assert.Equal(0, status.Bounds.Left, 1);

        var pills = window.FindControl<WrapPanel>("UsageCredentialPills")!;
        Assert.Equal(PillSpacing, pills.ItemSpacing);
        Assert.All(pills.Children.OfType<Border>().SelectMany(border => border.GetVisualDescendants().OfType<TextBlock>()),
            text => Assert.Equal(TextWrapping.Wrap, text.TextWrapping));

        AssertEveryTextStaysInsideTheCard(window);

        window.ViewModel.Language = UiLanguage.Japanese;
        window.UpdateLayout();
        AssertEveryTextStaysInsideTheCard(window);
    }

    [AvaloniaFact]
    public async Task The_configured_state_keeps_its_copy_inside_the_card_in_both_languages()
    {
        using var scope = Scope.Create(out _);
        var window = scope.Window;
        await SaveConnectionAsync(window, scope.Store, Endpoint, CredentialStoreAvailability.SecureStore);

        AssertStatusBannerState(window, "configured");
        AssertEveryTextStaysInsideTheCard(window);

        window.ViewModel.Language = UiLanguage.Japanese;
        window.UpdateLayout();
        AssertStatusBannerState(window, "configured");
        AssertEveryTextStaysInsideTheCard(window);
    }

    [AvaloniaFact]
    public async Task The_session_only_state_keeps_its_copy_inside_the_card_in_both_languages()
    {
        using var scope = Scope.Create(out _);
        var window = scope.Window;
        await SaveConnectionAsync(window, scope.Store, Endpoint, CredentialStoreAvailability.SessionOnly);

        AssertStatusBannerState(window, "session-only");
        Assert.True(window.FindControl<PathIcon>("UsageStatusSessionIcon")!.IsVisible);
        AssertEveryTextStaysInsideTheCard(window);

        window.ViewModel.Language = UiLanguage.Japanese;
        window.UpdateLayout();
        AssertEveryTextStaysInsideTheCard(window);
    }

    [AvaloniaFact]
    public async Task The_store_unavailable_state_wraps_the_banner_and_pills_inside_the_card()
    {
        using var scope = Scope.Create(out _);
        var window = scope.Window;
        await SaveConnectionAsync(window, scope.Store, Endpoint, CredentialStoreAvailability.Unavailable);

        AssertStatusBannerState(window, "store-unavailable");
        Assert.True(window.FindControl<PathIcon>("UsageStatusStoreIcon")!.IsVisible);
        window.UpdateLayout();

        // The icon is a fixed 16px column plus an 8px gap; the text takes the remaining width.
        var status = window.FindControl<TextBlock>("UsageConnectionStatusText")!;
        Assert.Equal(24, status.Bounds.Left, 1);
        AssertEveryTextStaysInsideTheCard(window);

        // A wide font must not resurrect the clipping: the banner wraps onto several lines
        // instead of keeping one long clipped line, and the pills wrap onto another row.
        window.FindControl<Border>("UsageConnectionCard")!.SetValue(TextElement.FontFamilyProperty, new FontFamily("DejaVu Serif"));
        window.UpdateLayout();
        Assert.True(status.TextLayout.TextLines.Count > 1,
            $"The store-unavailable banner must wrap at {WindowWidth:0}px with a wide font; it stayed on {status.TextLayout.TextLines.Count} line(s).");
        AssertStatusIconOnTheFirstLine(window, status);
        AssertEveryTextStaysInsideTheCard(window);

        AssertPillsWrapWhenTheLineRunsOut(window);

        window.ViewModel.Language = UiLanguage.Japanese;
        window.UpdateLayout();
        AssertEveryTextStaysInsideTheCard(window);
    }

    [AvaloniaFact]
    public async Task An_invalid_endpoint_error_stays_inside_the_card_in_both_languages()
    {
        using var scope = Scope.Create(out var store);
        var window = scope.Window;

        // A stored pair with a stored non-https endpoint puts the card into the invalid state.
        store.Seed(StoreUsageCredentialProvider.ClientIdAccount, "synthetic-ui-client-id");
        store.Seed(StoreUsageCredentialProvider.ClientSecretAccount, "synthetic-ui-client-secret");
        window.ViewModel.Settings.UsageApiEndpoint = NonHttpsEndpoint;
        await window.ViewModel.InitializeAsync();
        await WaitUntilAsync(() => window.ViewModel.UsageConnectionState == UsageConnectionState.Invalid);

        AssertStatusBannerState(window, "invalid");
        Assert.True(window.FindControl<PathIcon>("UsageStatusInvalidIcon")!.IsVisible);
        Assert.Equal(
            new UiCopy(UiLanguage.English).UsageConnectionStatus(UsageConnectionState.Invalid),
            window.ViewModel.UsageConnectionStatusText);
        var validation = window.FindControl<TextBlock>("UsageConnectionValidationText")!;
        Assert.True(validation.IsVisible);
        AssertEveryTextStaysInsideTheCard(window);

        window.ViewModel.Language = UiLanguage.Japanese;
        window.UpdateLayout();
        AssertEveryTextStaysInsideTheCard(window);
    }

    [AvaloniaFact]
    public async Task The_saving_state_keeps_the_busy_copy_inside_the_card()
    {
        using var scope = Scope.Create(out var store);
        var window = scope.Window;
        FillConnection(window, Endpoint);

        window.FindControl<Button>("UsageConnectionSaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var busy = window.FindControl<TextBlock>("UsageConnectionBusyText")!;
        Assert.True(busy.IsVisible);
        AssertEveryTextStaysInsideTheCard(window);

        window.ViewModel.Language = UiLanguage.Japanese;
        window.UpdateLayout();
        AssertEveryTextStaysInsideTheCard(window);

        store.Release();
        await WaitUntilAsync(() => !window.FindControl<StackPanel>("UsageConnectionBusyPanel")!.IsVisible);
        Assert.Equal(UsageConnectionState.Configured, window.ViewModel.UsageConnectionState);
    }

    private static async Task SaveConnectionAsync(MainWindow window, FakeCredentialStore store, string endpoint, CredentialStoreAvailability availability)
    {
        store.Availability = availability;
        FillConnection(window, endpoint);
        window.FindControl<Button>("UsageConnectionSaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => !window.FindControl<StackPanel>("UsageConnectionBusyPanel")!.IsVisible);
        Assert.NotEqual(UsageConnectionState.Invalid, window.ViewModel.UsageConnectionState);
    }

    private static void FillConnection(MainWindow window, string endpoint)
    {
        window.FindControl<TextBox>("UsageEndpointBox")!.Text = endpoint;
        window.FindControl<TextBox>("UsageClientIdBox")!.Text = "synthetic-ui-client-id";
        window.FindControl<TextBox>("UsageClientSecretBox")!.Text = "synthetic-ui-client-secret";
    }

    private static void AssertStatusBannerState(MainWindow window, string expectedClass)
    {
        var panel = window.FindControl<Border>("UsageConnectionStatusPanel")!;
        Assert.Contains(expectedClass, panel.Classes);
    }

    // Same first-line standard as the usage page banners: the icon sits inside the first
    // text line's box and centered on it, without depending on a few pixels of slack that
    // flip with font metrics (DejaVu Sans renders a 16.3px first line at 14px).
    private static void AssertStatusIconOnTheFirstLine(MainWindow window, TextBlock status)
    {
        var banner = window.FindControl<Grid>("UsageStatusBannerGrid")!;
        var icon = banner.GetVisualDescendants().OfType<PathIcon>().First(child => child.IsVisible);
        var iconTop = icon.TranslatePoint(default, banner)!.Value.Y;
        var textTop = status.TranslatePoint(default, banner)!.Value.Y;
        var firstLineHeight = status.TextLayout.TextLines[0].Height;
        Assert.True(iconTop >= textTop,
            $"The status icon floats above the first text line by {textTop - iconTop:0.#}px.");
        Assert.True(iconTop + icon.Bounds.Height <= textTop + firstLineHeight,
            $"The status icon ends {iconTop + icon.Bounds.Height - textTop:0.#}px into the text, beyond the first line ({firstLineHeight:0.#}px).");
        Assert.True(Math.Abs(iconTop + icon.Bounds.Height / 2 - (textTop + firstLineHeight / 2)) <= 3,
            "The status icon is not centered on the first text line.");
    }

    private static void AssertEveryTextStaysInsideTheCard(MainWindow window)
    {
        window.UpdateLayout();
        var card = window.FindControl<Border>("UsageConnectionCard")!;
        var innerRight = card.Bounds.Width - card.BorderThickness.Right - card.Padding.Right;
        Assert.True(innerRight > 100, $"The card is unexpectedly narrow: inner edge at {innerRight:0.#}px.");

        foreach (var block in card.GetVisualDescendants().OfType<TextBlock>()
                     .Where(block => block.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(block.Text)))
        {
            var rightInCard = block.TranslatePoint(new Point(block.Bounds.Width, block.Bounds.Height), card)?.X
                ?? throw new InvalidOperationException($"'{block.Text}' is not attached to the card visual tree.");
            Assert.True(rightInCard <= innerRight + Tolerance,
                $"'{block.Text}' ends at {rightInCard:0.#}px but the card's inner edge is {innerRight:0.#}px; the text runs past the card.");

            var lines = block.TextLayout.TextLines;
            Assert.True(lines.Count > 0, $"'{block.Text}' has no measured text lines.");
            var widestLine = lines.Max(line => line.Width);
            // A sub-pixel rounding overshoot in the shaped line is invisible; a real overflow
            // (unwrapped text) misses the element width by tens of pixels.
            Assert.True(widestLine <= block.Bounds.Width + 1,
                $"The widest line of '{block.Text}' measures {widestLine:0.#}px but its element is only {block.Bounds.Width:0.#}px wide; the glyphs would clip even though the element fits.");
        }
    }

    private static void AssertPillsWrapWhenTheLineRunsOut(MainWindow window)
    {
        var pills = window.FindControl<WrapPanel>("UsageCredentialPills")!;
        var visiblePills = pills.Children.OfType<Border>().Where(pill => pill.IsEffectivelyVisible).ToList();
        Assert.True(visiblePills.Count > 0, "The credential pills must be laid out.");

        var oneLineWidth = visiblePills.Sum(pill => NaturalPillWidth(pill)) + PillSpacing * (visiblePills.Count - 1);
        if (oneLineWidth <= pills.Bounds.Width) return;

        Assert.True(visiblePills.Any(pill => pill.Bounds.Y > visiblePills[0].Bounds.Y + 1),
            $"The pills need {oneLineWidth:0.#}px on one line but only {pills.Bounds.Width:0.#}px is available; they must wrap onto another row instead of spilling past the card.");
    }

    private static double NaturalPillWidth(Border pill)
    {
        var text = pill.GetVisualDescendants().OfType<TextBlock>().Single(block => !string.IsNullOrWhiteSpace(block.Text));
        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        var layout = new TextLayout(text.Text!, typeface, text.FontSize, Brushes.Black, maxWidth: double.PositiveInfinity);
        return layout.WidthIncludingTrailingWhitespace + pill.Padding.Left + pill.Padding.Right + pill.BorderThickness.Left + pill.BorderThickness.Right;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The expected condition was not reached in time.");
            await Task.Delay(10);
        }
    }

    private sealed class Scope : IDisposable
    {
        private Scope(MainWindow window, FakeCredentialStore store)
        {
            Window = window;
            Store = store;
        }

        public MainWindow Window { get; }
        public FakeCredentialStore Store { get; }

        public static Scope Create(out FakeCredentialStore store)
        {
            store = new FakeCredentialStore();
            var window = new MainWindow(new MainViewModel(new EmptyDashboardSource(), usageCredentialStore: store)) { Width = WindowWidth };
            window.ViewModel.Navigate(AppPage.Settings);
            window.Show();
            window.UpdateLayout();
            return new Scope(window, store);
        }

        public void Dispose()
        {
            Window.Close();
            Window.ViewModel.Dispose();
        }
    }

    private sealed class FakeCredentialStore(bool gated = false) : ICredentialStore
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CredentialStoreAvailability Availability { get; set; } = CredentialStoreAvailability.SecureStore;
        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count => values.Count;

        public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken) =>
            ValueTask.FromResult(values.GetValueOrDefault(account));

        public void Seed(string account, string secret) => values[account] = secret;

        public async ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken)
        {
            SaveStarted.TrySetResult();
            if (gated) await gate.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            values[account] = secret;
        }

        public ValueTask RemoveAsync(string account, CancellationToken cancellationToken)
        {
            values.Remove(account);
            return ValueTask.CompletedTask;
        }

        public void Release() => gate.TrySetResult();
    }
}

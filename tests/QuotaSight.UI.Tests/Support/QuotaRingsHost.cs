using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using QuotaSight.Application;
using QuotaSight.Core;
using QuotaSight.UI;

namespace QuotaSight.UI.Tests.Support;

/// <summary>
/// Test host that builds the synthetic quota-rings window from fixed sample data, a fixed
/// clock, and theme/language chosen through settings, so tests and any external capture
/// harness that links this file render the same window.
///
/// The <c>--usage-state</c> switch additionally builds the settings page with a synthetic
/// usage connection. It only ever uses an in-memory credential store, a synthetic credential
/// pair and a transport that refuses every request, so no real credential store, user setting
/// or network endpoint is read or written.
/// </summary>
public static class QuotaRingsHost
{
    public static readonly DateTimeOffset FixtureNow = DateTimeOffset.Parse("2026-11-20T12:00:00+09:00");

    /// <summary>Acceptable synthetic usage endpoint for the configured states.</summary>
    public const string SyntheticUsageEndpoint = "https://usage.example.test";

    /// <summary>Deliberately invalid synthetic endpoint: cleartext is refused by the rules.</summary>
    public const string SyntheticUsageInvalidEndpoint = "http://usage.example.test";

    /// <summary>Synthetic client id. It is a fixture literal, never a real credential.</summary>
    public const string SyntheticUsageClientId = "synthetic-fixture-client-id";

    /// <summary>Synthetic client secret. It is a fixture literal, never a real credential.</summary>
    public const string SyntheticUsageClientSecret = "synthetic-fixture-client-secret";

    /// <summary>Argument that selects the connection state shown on the settings page.</summary>
    public const string UsageStateArgument = "--usage-state";

    /// <summary>
    /// Argument that scrolls the settings page, once the window has been laid out, so the usage
    /// connection card sits inside the viewport. It is only meaningful together with
    /// <c>--settings</c>, because the card lives on that page.
    /// </summary>
    public const string ScrollUsageArgument = "--scroll-usage";

    /// <summary>Gap kept above a top-aligned card, in device-independent pixels.</summary>
    private const double UsageScrollTopGap = 12;

    /// <summary>Gap kept below a bottom-aligned card, so its last row is not flush with the edge.</summary>
    private const double UsageScrollBottomGap = 16;

    /// <summary>States whose error copy or progress strip at the card bottom is what a capture needs.</summary>
    private static readonly string[] UsageStatesThatPreferTheBottom = ["saving", "invalid-endpoint", "store-unavailable"];

    /// <summary>Connection states the host can build, in the order they are documented.</summary>
    public static readonly IReadOnlyList<string> UsageStates =
        ["unconfigured", "configured", "session-only", "store-unavailable", "invalid-endpoint", "saving"];

    public static (Window Window, MainViewModel ViewModel) Create(string[] args)
    {
        var host = CreateHost(args);
        return (host.Window, host.ViewModel);
    }

    /// <summary>
    /// Same window as <see cref="Create"/>, plus the synthetic store so a test can inspect what
    /// was written and whether a save is still in flight.
    /// </summary>
    public static FixtureHost CreateHost(string[] args)
    {
        var widget = args.Contains("--widget", StringComparer.OrdinalIgnoreCase);
        var light = args.Contains("--light", StringComparer.OrdinalIgnoreCase);
        var narrow = args.Contains("--narrow", StringComparer.OrdinalIgnoreCase);
        var settings = args.Contains("--settings", StringComparer.OrdinalIgnoreCase);
        var scrollUsage = args.Contains(ScrollUsageArgument, StringComparer.OrdinalIgnoreCase);
        var usageState = ReadUsageState(args);
        if (settings && widget)
            throw new InvalidOperationException("--settings is only available on the main window; --widget has no settings page.");
        if (scrollUsage && !settings)
            throw new InvalidOperationException(
                $"{ScrollUsageArgument} requires --settings: the usage connection card only exists on the settings page.");

        // Theme must be passed explicitly through settings: SetSettings applies dto.Theme and a
        // default "System" value would override the --dark variant on the host.
        var theme = light ? "Light" : "Dark";
        var store = usageState is null
            ? null
            : new SyntheticUsageCredentialStore(AvailabilityFor(usageState), freezeSave: usageState == "saving");
        var viewModel = new MainViewModel(new SyntheticDashboardSource(),
            timeProvider: new FixedTimeProvider(FixtureNow),
            usageCredentialStore: store,
            usageHttpClientFactory: store is null ? null : CreateSyntheticHttpClient);
        viewModel.SetSettings(viewModel.CurrentSettingsForTests with { Theme = theme, CopilotReferenceCredits = 1500m }, persist: false);
        if (args.Contains("--ja", StringComparer.OrdinalIgnoreCase))
            viewModel.Language = UiLanguage.Japanese;
        Window window = widget
            ? new CompactQuotaWindow(viewModel) { Width = 410, Height = 620 }
            : new MainWindow(viewModel) { Width = narrow ? 520 : 1100, Height = 740 };
        if (usageState is not null)
            ApplyUsageState(window, usageState);
        if (settings)
            viewModel.Navigate(AppPage.Settings);
        if (scrollUsage)
        {
            // Layout is only complete after the window has been shown, and the fixture may be
            // launched unattended, so scroll from a Loaded-priority post instead of asking the
            // capture harness to press Tab. The handler stays subscribed for re-shows.
            window.Opened += (_, _) => Dispatcher.UIThread.Post(
                () => ScrollUsageCardIntoView(window, usageState),
                DispatcherPriority.Loaded);
        }
        return new FixtureHost(window, viewModel, store, usageState);
    }

    /// <summary>
    /// Scrolls the settings page so the usage connection card is fully inside the viewport of the
    /// page's own scroll viewer. The card top is aligned near the viewport top; for the states
    /// whose error copy or progress strip sits at the bottom of the card the bottom edge wins
    /// instead, so those rows stay visible even when the card is taller than the viewport.
    /// Nothing here moves focus, so it works without a single Tab press.
    /// </summary>
    public static void ScrollUsageCardIntoView(Window window, string? usageState = null)
    {
        if (window is not MainWindow main)
            throw new InvalidOperationException("The usage connection card only exists on the main window.");

        var card = main.FindControl<Border>("UsageConnectionCard")
            ?? throw new InvalidOperationException("The main window has no UsageConnectionCard to reveal.");
        var scroll = main.FindControl<ScrollViewer>("ContentScroll")
            ?? throw new InvalidOperationException("The main window has no ContentScroll viewer to scroll.");

        // Bounds are all zero until a layout pass has run, and a post can arrive before the first
        // one on a slow launch; laying out here makes the measurement below meaningful.
        main.UpdateLayout();
        if (card.Bounds.Height <= 0 || scroll.Viewport.Height <= 0)
            return;

        if (card.TranslatePoint(new Point(0, 0), scroll) is not { } origin)
            return;

        var viewport = scroll.Viewport.Height;
        var offset = scroll.Offset.Y;
        if (UsageStatesThatPreferTheBottom.Contains(usageState, StringComparer.Ordinal))
            offset += origin.Y + card.Bounds.Height - (viewport - UsageScrollBottomGap);
        else
            offset += origin.Y - UsageScrollTopGap;

        var maximum = Math.Max(0, scroll.Extent.Height - viewport);
        var clamped = Math.Clamp(offset, 0, maximum);
        if (Math.Abs(clamped - scroll.Offset.Y) > 0.01)
            scroll.Offset = new Vector(scroll.Offset.X, clamped);
    }

    private static void ApplyUsageState(Window window, string usageState)
    {
        // "unconfigured" is the view model's own starting point: a store with nothing in it.
        if (usageState == "unconfigured")
            return;

        if (window is not MainWindow main)
            throw new InvalidOperationException("Usage connection states require the main window.");

        var endpoint = usageState == "invalid-endpoint" ? SyntheticUsageInvalidEndpoint : SyntheticUsageEndpoint;
        main.FindControl<TextBox>("UsageEndpointBox")!.Text = endpoint;
        main.FindControl<TextBox>("UsageClientIdBox")!.Text = SyntheticUsageClientId;
        main.FindControl<TextBox>("UsageClientSecretBox")!.Text = SyntheticUsageClientSecret;

        // Saving goes through the real button handler, so the progress panel and the disabled
        // fields a capture is supposed to show come from production code. "saving" uses a store
        // whose write never completes, which leaves that progress state on screen.
        main.FindControl<Button>("UsageConnectionSaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static CredentialStoreAvailability AvailabilityFor(string usageState) => usageState switch
    {
        "unconfigured" or "configured" => CredentialStoreAvailability.SecureStore,
        "session-only" => CredentialStoreAvailability.SessionOnly,
        "store-unavailable" => CredentialStoreAvailability.Unavailable,
        // The invalid endpoint never reaches the store, so any availability is fine here.
        _ => CredentialStoreAvailability.SecureStore,
    };

    private static string? ReadUsageState(string[] args)
    {
        var index = Array.FindIndex(args, argument => string.Equals(argument, UsageStateArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return null;
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{UsageStateArgument} needs a value: {string.Join(", ", UsageStates)}.");

        var value = args[index + 1].Trim().ToLowerInvariant();
        if (!UsageStates.Contains(value, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown {UsageStateArgument} value '{args[index + 1]}'; expected one of: {string.Join(", ", UsageStates)}.");
        return value;
    }

    private static HttpClient CreateSyntheticHttpClient() => new(new OfflineUsageHandler());

    /// <summary>Transport for the synthetic connection. It answers locally and never dials out.</summary>
    private sealed class OfflineUsageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }
}

/// <summary>Window, view model and synthetic store produced by <see cref="QuotaRingsHost.CreateHost"/>.</summary>
/// <param name="UsageState">Requested state, or <c>null</c> when the host was launched without one.</param>
public sealed record FixtureHost(
    Window Window,
    MainViewModel ViewModel,
    SyntheticUsageCredentialStore? UsageCredentialStore,
    string? UsageState);

/// <summary>
/// In-memory credential store for the fixture. <see cref="Availability"/> is chosen by the
/// requested state, and <paramref name="freezeSave"/> makes the first write wait forever so the
/// settings page stays in its saving state for a capture. Nothing outside this object is touched.
/// </summary>
public sealed class SyntheticUsageCredentialStore(CredentialStoreAvailability availability = CredentialStoreAvailability.SecureStore, bool freezeSave = false) : ICredentialStore
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource saveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CredentialStoreAvailability Availability { get; } = availability;

    /// <summary>Completes as soon as a write has been attempted, whether or not it finished.</summary>
    public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Count => values.Count;

    public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken) =>
        ValueTask.FromResult(values.GetValueOrDefault(account));

    public async ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken)
    {
        SaveStarted.TrySetResult();
        if (freezeSave)
            await saveGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        values[account] = secret;
    }

    public ValueTask RemoveAsync(string account, CancellationToken cancellationToken)
    {
        values.Remove(account);
        return ValueTask.CompletedTask;
    }

    /// <summary>Returns the stored value for a test; the fixture never renders it.</summary>
    public string? Peek(string account) => values.GetValueOrDefault(account);
}

public sealed class SyntheticDashboardSource : IDashboardSource
{
    private readonly IReadOnlyList<ProviderCardViewModel> cards;

    public SyntheticDashboardSource()
    {
        var now = QuotaRingsHost.FixtureNow;
        cards =
        [
            Card(ProviderKind.Claude, "Claude Max", "alice", "Connected",
                [Sample(ProviderKind.Claude, "alice", "5h", QuotaWindowKind.Rolling, TimeSpan.FromHours(5), 62, now, now.AddHours(1).AddMinutes(12)),
                 Sample(ProviderKind.Claude, "alice", "Messages", QuotaWindowKind.Weekly, TimeSpan.FromDays(7), 29, now, now.AddDays(3))]),
            Card(ProviderKind.ChatGpt, "ChatGPT Plus", "work", "Connected",
                [Sample(ProviderKind.ChatGpt, "work", "5h", QuotaWindowKind.Rolling, TimeSpan.FromHours(5), 0, now, now.AddMinutes(22)),
                 Sample(ProviderKind.ChatGpt, "work", "Messages", QuotaWindowKind.Weekly, TimeSpan.FromDays(7), 55, now, now.AddDays(2))]),
            Card(ProviderKind.OpenCode, "OpenCode Go", "dev", "Connected",
                [Sample(ProviderKind.OpenCode, "dev", "5h", QuotaWindowKind.Rolling, TimeSpan.FromHours(5), 88, now, now.AddHours(2)),
                 Sample(ProviderKind.OpenCode, "dev", "Requests", QuotaWindowKind.Daily, TimeSpan.FromDays(1), 72, now, now.AddHours(9)),
                 Sample(ProviderKind.OpenCode, "dev", "Requests", QuotaWindowKind.Weekly, TimeSpan.FromDays(7), 40, now, now.AddDays(2)),
                 Sample(ProviderKind.OpenCode, "dev", "Requests", QuotaWindowKind.Monthly, TimeSpan.FromDays(30), 12, now, now.AddDays(10))]),
            Card(ProviderKind.Claude, "Claude Pro", "personal", "Stale · refresh failed",
                [Sample(ProviderKind.Claude, "personal", "5h", QuotaWindowKind.Rolling, TimeSpan.FromHours(5), 75, now.AddHours(-3), now.AddHours(1), now.AddHours(-2)),
                 Sample(ProviderKind.Claude, "personal", "Messages", QuotaWindowKind.Weekly, TimeSpan.FromDays(7), null, now.AddHours(-3), now.AddDays(3), now.AddHours(-2)),
                 Sample(ProviderKind.Claude, "personal", "Messages", QuotaWindowKind.Monthly, TimeSpan.FromDays(30), 64, now.AddHours(-1), now.AddMinutes(-30), now.AddHours(2))]),
            CopilotCard(now)
        ];
    }

    public bool IsDemo => false;

    public IReadOnlyList<ProviderCardViewModel> Load() => cards;

    private static ProviderCardViewModel Card(ProviderKind provider, string name, string account, string state,
        IReadOnlyList<QuotaRowViewModel> rows) =>
        new(provider, name, account, "#73D6B1", state, false, rows) { CurrentLanguage = UiLanguage.English };

    private static QuotaRowViewModel Sample(ProviderKind provider, string account, string metric,
        QuotaWindowKind kind, TimeSpan duration, decimal? remaining, DateTimeOffset fetched,
        DateTimeOffset resetAt, DateTimeOffset? freshUntil = null)
    {
        var used = remaining is { } value ? 100m - value : (decimal?)null;
        var snapshot = new QuotaSnapshot(provider, account, metric,
            new QuotaWindow(kind, fetched.AddDays(-1), fetched.Add(duration), duration, resetAt),
            used, remaining is null ? null : 100m, null, "requests", fetched, fetched,
            QuotaSource.Official, QuotaConfidence.Reported, freshUntil ?? fetched.AddHours(4),
            provider == ProviderKind.Claude ? "Claude" : provider == ProviderKind.ChatGpt ? "ChatGPT" : "OpenCode Go");
        var row = QuotaPresentationFormatter.Format(snapshot, QuotaRingsHost.FixtureNow, UiLanguage.English);
        return kind == QuotaWindowKind.Rolling ? row with { WindowName = metric, Metric = string.Empty } : row;
    }

    private static ProviderCardViewModel CopilotCard(DateTimeOffset now)
    {
        var snapshot = new QuotaSnapshot(ProviderKind.Copilot, "corp", "AI credits",
            new QuotaWindow(QuotaWindowKind.Monthly, now.AddDays(-10), now.AddDays(20), TimeSpan.FromDays(30), now.AddDays(20)),
            null, null, 83, "credits", now, now, QuotaSource.Delayed, QuotaConfidence.Reported,
            now.AddHours(2), "GitHub Copilot Business", new CopilotUsageBreakdown(1240, 900, 340));
        var row = QuotaPresentationFormatter.Format(snapshot, now, UiLanguage.English);
        return new ProviderCardViewModel(ProviderKind.Copilot, "GitHub Copilot Business", "corp",
            "#73D6B1", "Connected · reference values", false, [row])
        {
            CurrentLanguage = UiLanguage.English
        };
    }
}

public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

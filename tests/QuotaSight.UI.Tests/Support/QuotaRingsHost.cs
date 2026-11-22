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
///
/// The <c>--usage-page</c> switch opens the usage page with a scripted in-memory read port and
/// a fixed clock, so every freshness, delayed, stale and failure state can be captured with
/// synthetic daily rows. It never reads a credential, a settings file or a network endpoint.
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
    /// Argument that opens the usage page, loads a synthetic read result and leaves the page in
    /// the requested state. It is only meaningful on the main window and cannot be combined with
    /// <c>--settings</c> or <c>--usage-state</c>, which build a different page.
    /// </summary>
    public const string UsagePageArgument = "--usage-page";

    /// <summary>Usage-page states the host can build, in the order they are documented.</summary>
    public static readonly IReadOnlyList<string> UsagePageStates =
    [
        "ready", "delayed", "stale", "stale-delayed",
        "error-unauthorized", "error-transient", "empty", "loading", "unconfigured"
    ];

    /// <summary>
    /// Display zone for the usage page's freshness line. It is injected so the rendered text does
    /// not depend on the machine's zone, and it is only used by <c>--usage-page</c>.
    /// </summary>
    private static readonly TimeZoneInfo UsagePageZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

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
        var usagePage = ReadUsagePageState(args);
        if (settings && widget)
            throw new InvalidOperationException("--settings is only available on the main window; --widget has no settings page.");
        if (usagePage is not null && widget)
            throw new InvalidOperationException(
                $"{UsagePageArgument} requires the main window; --widget has no usage page.");
        if (usagePage is not null && settings)
            throw new InvalidOperationException(
                $"{UsagePageArgument} cannot be combined with --settings: they open different pages.");
        if (usagePage is not null && usageState is not null)
            throw new InvalidOperationException(
                $"{UsagePageArgument} cannot be combined with {UsageStateArgument}: the connection state and the loaded usage page are set up by different switches.");
        if (scrollUsage && !settings)
            throw new InvalidOperationException(
                $"{ScrollUsageArgument} requires --settings: the usage connection card only exists on the settings page.");

        // Theme must be passed explicitly through settings: SetSettings applies dto.Theme and a
        // default "System" value would override the --dark variant on the host.
        var theme = light ? "Light" : "Dark";
        var store = usageState is null
            ? null
            : new SyntheticUsageCredentialStore(AvailabilityFor(usageState), freezeSave: usageState == "saving");
        var usagePagePort = usagePage is null or "unconfigured" ? null : CreateUsagePageReadPort(usagePage);
        var viewModel = new MainViewModel(
            timeProvider: new FixedTimeProvider(FixtureNow),
            usageReadPort: usagePagePort,
            usageCredentialStore: store,
            usageHttpClientFactory: store is null ? null : CreateSyntheticHttpClient,
            localTimeZone: usagePage is null ? null : UsagePageZone);
        viewModel.SetSettings(viewModel.CurrentSettingsForTests with { Theme = theme }, persist: false);
        if (args.Contains("--ja", StringComparer.OrdinalIgnoreCase))
            viewModel.Language = UiLanguage.Japanese;
        Window window = widget
            ? new CompactQuotaWindow(viewModel) { Width = 410, Height = 620 }
            : new MainWindow(viewModel) { Width = narrow ? 520 : 1100, Height = 740 };
        if (usageState is not null)
            ApplyUsageState(window, usageState);
        if (settings)
            viewModel.Navigate(AppPage.Settings);
        if (usagePage is not null)
            OpenUsagePage(window, usagePage);
        if (scrollUsage)
        {
            // Layout is only complete after the window has been shown, and the fixture may be
            // launched unattended, so scroll from a Loaded-priority post instead of asking the
            // capture harness to press Tab. The handler stays subscribed for re-shows.
            window.Opened += (_, _) => Dispatcher.UIThread.Post(
                () => ScrollUsageCardIntoView(window, usageState),
                DispatcherPriority.Loaded);
        }
        return new FixtureHost(window, viewModel, store, usageState, usagePage, usagePagePort);
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

    /// <summary>
    /// Opens the usage page and drives it into the requested state through production code:
    /// navigating to <see cref="AppPage.Usage"/> starts the page's first read (the page setter owns
    /// that load), and the stale / loading states advance to a second read by raising the real
    /// refresh or retry button's Click, exactly like the settings fixture presses its save button.
    /// No settings page and no Tab press is involved, and the scripted port answers locally.
    /// </summary>
    private static void OpenUsagePage(Window window, string usagePage)
    {
        if (window is not MainWindow main)
            throw new InvalidOperationException($"{UsagePageArgument} requires the main window.");

        // The CurrentPage setter starts the first read when the page becomes Usage; an explicit
        // RefreshUsageAsync here would start a second, unwanted read before the state is set up.
        main.ViewModel.NavigateToUsage();

        // stale: the retry action inside the (still hidden) failure panel; loading: the page's
        // own refresh button. Both handlers go through the production click path.
        var secondRead = usagePage switch
        {
            "stale" or "stale-delayed" => "UsageRetryButton",
            "loading" => "UsageRefreshButton",
            _ => null,
        };
        if (secondRead is null)
            return;

        var button = main.FindControl<Button>(secondRead)
            ?? throw new InvalidOperationException($"The main window has no {secondRead} to press.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>
    /// Builds the scripted read sequence for a usage-page state. Every value is synthetic: the
    /// rows come from <see cref="UsagePageSample"/>, the failures are classified
    /// <see cref="UsageReadException"/> instances, and the loading state's second read waits on an
    /// in-memory gate. Nothing dials out.
    /// </summary>
    private static ScriptedUsagePageReadPort? CreateUsagePageReadPort(string usagePage) => usagePage switch
    {
        "ready" => new([() => UsagePageSample(FixtureNow.AddHours(-1))]),
        "delayed" => new([() => UsagePageSample(FixtureNow.AddHours(-13))]),
        "stale" => new(
        [
            () => UsagePageSample(FixtureNow.AddHours(-1)),
            () => throw new UsageReadException(UsageReadFailure.Transient, "synthetic transient failure"),
        ]),
        "stale-delayed" => new(
        [
            () => UsagePageSample(FixtureNow.AddHours(-13)),
            () => throw new UsageReadException(UsageReadFailure.Transient, "synthetic transient failure"),
        ]),
        "error-unauthorized" => new([() => throw new UsageReadException(UsageReadFailure.Unauthorized, "synthetic rejection")]),
        "error-transient" => new([() => throw new UsageReadException(UsageReadFailure.Transient, "synthetic transient failure")]),
        "empty" => new([() => new DailyUsage(DateOnly.FromDateTime(FixtureNow.DateTime).AddDays(-29), DateOnly.FromDateTime(FixtureNow.DateTime), [], 0)]),
        "loading" => new([() => UsagePageSample(FixtureNow.AddHours(-1))], gateAfterwards: true),
        _ => null,
    };

    /// <summary>
    /// Five days of synthetic daily rows ending at the fixture's evaluation day, with a total that
    /// is their exact sum and one collector receipt at <paramref name="receivedAt"/>. Two collectors
    /// are named so the freshness line describes a real per-collector surface rather than a single
    /// fabricated instant.
    /// </summary>
    private static DailyUsage UsagePageSample(DateTimeOffset receivedAt)
    {
        var to = DateOnly.FromDateTime(FixtureNow.DateTime);
        var days = new List<UsageDay>
        {
            new(to.AddDays(-4), "collector-a", 184_200),
            new(to.AddDays(-3), "collector-a", 201_600),
            new(to.AddDays(-2), "collector-b", 158_400),
            new(to.AddDays(-1), "collector-a", 213_000),
            new(to, "collector-b", 176_500),
        };
        return new DailyUsage(to.AddDays(-29), to, days, days.Sum(day => day.TotalTokens),
        [
            new UsageCollectorReceipt("collector-a", 1, receivedAt),
            new UsageCollectorReceipt("collector-b", 1, receivedAt),
        ]);
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

    private static string? ReadUsagePageState(string[] args)
    {
        var index = Array.FindIndex(args, argument => string.Equals(argument, UsagePageArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return null;
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{UsagePageArgument} needs a value: {string.Join(", ", UsagePageStates)}.");

        var value = args[index + 1].Trim().ToLowerInvariant();
        if (!UsagePageStates.Contains(value, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown {UsagePageArgument} value '{args[index + 1]}'; expected one of: {string.Join(", ", UsagePageStates)}.");
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
/// <param name="UsageState">Requested connection state, or <c>null</c> when the host was launched without one.</param>
/// <param name="UsagePageState">Requested usage-page state, or <c>null</c> when <c>--usage-page</c> was not given.</param>
/// <param name="UsagePageReadPort">
/// The scripted port behind the usage page, or <c>null</c> for the unconfigured/absent states.
/// A test can read its call count and release the gate the loading state waits on.
/// </param>
public sealed record FixtureHost(
    Window Window,
    MainViewModel ViewModel,
    SyntheticUsageCredentialStore? UsageCredentialStore,
    string? UsageState,
    string? UsagePageState = null,
    ScriptedUsagePageReadPort? UsagePageReadPort = null);

/// <summary>
/// In-memory read port for the usage page. Each call answers with the next scripted outcome; a
/// throwing outcome becomes a faulted task, exactly like the production port reports a classified
/// failure. With <paramref name="gateAfterwards"/> the call after the scripted outcomes waits on an
/// in-memory gate, so a capture can show the loading state that keeps the previous values.
/// </summary>
public sealed class ScriptedUsagePageReadPort(IReadOnlyList<Func<DailyUsage>> outcomes, bool gateAfterwards = false) : IUsageReadPort
{
    private readonly TaskCompletionSource<DailyUsage> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int calls;

    /// <summary>How many reads the page has requested so far.</summary>
    public int Calls => Volatile.Read(ref calls);

    /// <summary>Completes the gated read, if one is waiting. Returns false when it already completed.</summary>
    public bool Release(Func<DailyUsage> next) => gate.TrySetResult(next());

    public Task<DailyUsage> ReadDailyAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken)
    {
        var index = Interlocked.Increment(ref calls) - 1;
        if (gateAfterwards && index >= outcomes.Count)
            return gate.Task;

        try
        {
            return Task.FromResult(outcomes[Math.Min(index, outcomes.Count - 1)]());
        }
        catch (Exception failure)
        {
            return Task.FromException<DailyUsage>(failure);
        }
    }

    public Task<UsageBreakdown> ReadBreakdownAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken) =>
        Task.FromException<UsageBreakdown>(new InvalidOperationException("Not used by this test."));
}


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

public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

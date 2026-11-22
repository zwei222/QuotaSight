using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

using Avalonia.Styling;

using Avalonia.Threading;
using QuotaSight.Application;
using QuotaSight.Infrastructure;

[assembly: InternalsVisibleTo("QuotaSight.UI.Tests")]

namespace QuotaSight.UI;

public enum AppPage { Dashboard, Usage, Settings }
public enum ThemeMode { System, Light, Dark }
public enum UiLanguage { English, Japanese }
public sealed record LocalizedChoice<T>(T Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}
/// <summary>One entry of the provider catalog, offered by the add-provider picker.</summary>
public sealed record ProviderChoiceViewModel(string ProviderId, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public interface IUiDispatcher
{
    bool CheckAccess();
    Task InvokeAsync(Action action);
    Task InvokeAsync(Func<Task> action);
}
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    // Avalonia 12 wires CheckAccess() to the thread that first touched Dispatcher.UIThread.
    // Without an initialized Avalonia application (plain unit-test hosts), that dispatcher is
    // lazily created and never pumped, so queued UI work would hang forever; treat every
    // thread as the UI thread there - the behavior Avalonia 11's NullDispatcherImpl provided.
    public bool CheckAccess() => Avalonia.Application.Current is null || Dispatcher.UIThread.CheckAccess();
    public Task InvokeAsync(Action action) => CheckAccess() ? InvokeInline(action) : Dispatcher.UIThread.InvokeAsync(action).GetTask();
    public Task InvokeAsync(Func<Task> action) => CheckAccess() ? action() : Dispatcher.UIThread.InvokeAsync(action);
    private static Task InvokeInline(Action action) { action(); return Task.CompletedTask; }
}
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;
    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    public Task InvokeAsync(Func<Task> action) => action();
}
public interface IQuotaWorkScheduler
{
    Task<T> RunAsync<T>(Func<Task<T>> work);
}
public sealed class TaskRunQuotaWorkScheduler : IQuotaWorkScheduler
{
    public Task<T> RunAsync<T>(Func<Task<T>> work) => Task.Run(work);
}

public interface IInAppNotificationService { string BannerText { get; } void Notify(string title, string reason); void NotifyLocalized(Func<UiCopy, (string Title, string Reason)> copy); }
public sealed class InAppNotificationService : IInAppNotificationService, INotifyPropertyChanged
{
    private readonly IDesktopNotificationBackend? desktopNotificationBackend;
    private Func<UiCopy, (string Title, string Reason)>? localizedBanner;
    private enum BannerKind { None, Opaque, Localized }
    public UiLanguage Language { get => language; set { if (language == value) return; language = value; if (visibleKind == BannerKind.Localized) SetLocalizedBanner(); } }
    private UiLanguage language = UiLanguage.English;
    private BannerKind visibleKind;
    public string BannerText { get; private set; } = string.Empty;
    public event PropertyChangedEventHandler? PropertyChanged;
    public InAppNotificationService(IDesktopNotificationBackend? desktopNotificationBackend = null) => this.desktopNotificationBackend = desktopNotificationBackend;
    // Arbitrary caller-provided strings are intentionally opaque; only known UI-owned alerts use localized payloads.
    public void Notify(string title, string reason) { localizedBanner = null; visibleKind = BannerKind.Opaque; SetBannerText($"{title}: {reason}"); }
    public void NotifyLocalized(Func<UiCopy, (string Title, string Reason)> copy) { localizedBanner = copy; visibleKind = BannerKind.Localized; SetLocalizedBanner(); }
    private void SetLocalizedBanner() { var (title, reason) = localizedBanner!(new UiCopy(Language)); SetBannerText($"{title}: {reason}"); }
    public void Clear() { localizedBanner = null; visibleKind = BannerKind.None; SetBannerText(string.Empty); }
    private void SetBannerText(string text)
    {
        if (BannerText == text) return;
        BannerText = text;
        PropertyChanged?.Invoke(this, new(nameof(BannerText)));
    }
}

public sealed class SettingsState
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public UiLanguage Language { get; set; } = UiLanguage.English;
    public int RefreshMinutes { get; private set; } = 10;
    public bool NotificationsEnabled { get; set; } = true;
    public bool ResidentMode { get; set; }
    public string UsageApiEndpoint { get; set; } = string.Empty;
    /// <summary>Legacy tracked client ids from before provider cards. Read and preserved verbatim; never a card source.</summary>
    public IReadOnlyList<string> TokScaleClients { get; set; } = [];
    /// <summary>Catalog provider ids the user added; their cards are usage-only observations.</summary>
    public IReadOnlyList<string> TokScaleProviders { get; set; } = [];
    public bool TrySetRefreshMinutes(int value) => UiSettings.IsRefreshIntervalValid(TimeSpan.FromMinutes(value)) && (RefreshMinutes = value) > 0;
}

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ReconfigurableUsageReadPort usageReadPort;
    private readonly StoreUsageCredentialProvider? usageCredentials;
    private readonly Func<string, TimeZoneInfo> timeZoneResolver;
    private readonly TimeZoneInfo localTimeZone;
    private string usageApiEndpoint = string.Empty;
    private bool hasUsageCredential;
    private UsageConnectionState usageConnectionState = UsageConnectionState.Unconfigured;
    private UsageConnectionError usageConnectionError = UsageConnectionError.None;
    private bool usageConnectionBusy;
    private bool usageConnectionRemoving;
    private CancellationTokenSource? usageRequest;
    private long usageRequestId;
    private string usageState = "Unconfigured";
    private string usageTotal = string.Empty;
    private IReadOnlyList<UsageDay> usageRows = [];
    private IReadOnlyList<UsageCollectorReceipt> usageCollectors = [];
    private UsageFailureKind usageFailure = UsageFailureKind.None;
    private bool isUsageStale;
    private bool hasUsageValues;
    private Task usageLoadTask = Task.CompletedTask;
    private AppPage currentPage = AppPage.Dashboard;
    private UiLanguage language = UiLanguage.English;
    public SettingsState Settings { get; } = new();

    public UiCopy CopyText => new(Language);
    public string NavDashboardText => CopyText.Dashboard;
    public string NavUsageText => CopyText.NavUsage;
    public string NavSettingsText => CopyText.Settings;
    public string RefreshText => CopyText.Refresh;
    public bool CanRefresh => !IsTokScaleLoading;
    public bool IsDashboardVisible => CurrentPage == AppPage.Dashboard;
    public bool IsSettingsVisible => CurrentPage == AppPage.Settings;
    public string HeaderTitle => CurrentPage switch { AppPage.Usage => NavUsageText, AppPage.Settings => CopyText.Settings, _ => CopyText.Dashboard };
    public bool IsUsageVisible => CurrentPage == AppPage.Usage;
    public string UsageStateText => new UiCopy(Language).UsageState(usageState);
    /// <summary>The daily read's raw state, for tests that pin the state machine itself.</summary>
    public string UsageState => usageState;
    public string UsageStatusText => UsageStateText;
    public IReadOnlyList<UsageDay> UsageRows => usageRows;
    public string UsageTotalText => usageTotal;
    public bool IsUsageTotalVisible => usageRows.Count > 0
        && (usageState is "Ready" or "Stale" || (usageState == "Loading" && hasUsageValues));
    public string UsageRangeText => usageState is "Unconfigured" or "Error"
        ? string.Empty
        : $"{DisplayedRangeFrom:yyyy-MM-dd} – {DisplayedRangeTo:yyyy-MM-dd} · Asia/Tokyo";
    public bool IsUsageRangeVisible => usageState is not ("Unconfigured" or "Error");
    public bool IsUsageLoading => usageState == "Loading";
    public bool CanRefreshUsage => !IsUsageLoading;

    /// <summary>
    /// How old the data on screen is, in the user's local time zone. It is the oldest
    /// reception time across the collectors the read covered, never the newest, because
    /// the daily total is only as fresh as the collector that reported least recently.
    /// Empty when no successful read has produced collectors to describe.
    /// </summary>
    public string UsageFreshnessText
    {
        get
        {
            if (!hasUsageValues || usageCollectors.Count == 0)
            {
                return string.Empty;
            }

            var copy = new UiCopy(Language);
            var oldest = OldestReceivedAt();
            if (oldest is not { } receivedAt)
            {
                return copy.UsageFreshnessNeverReceived;
            }

            var local = TimeZoneInfo.ConvertTime(receivedAt, localTimeZone);
            return usageCollectors.Any(entry => entry.LastReceivedAt is null)
                ? copy.UsageFreshnessLastReceivedWithMissing(local)
                : copy.UsageFreshnessLastReceived(local);
        }
    }

    /// <summary>True when the freshness line has something to say.</summary>
    public bool IsUsageFreshnessVisible => UsageFreshnessText.Length > 0;

    /// <summary>
    /// True when the oldest reception time is older than <see cref="UsageDelayThreshold"/>.
    /// The data is still shown, but it is never presented as current.
    /// </summary>
    public bool IsUsageDelayed
    {
        get
        {
            if (!hasUsageValues || OldestReceivedAt() is not { } receivedAt)
            {
                return false;
            }

            return timeProvider.GetUtcNow() - receivedAt > UsageDelayThreshold;
        }
    }

    /// <summary>Fixed "delayed data" sentence, or empty when the data is not delayed.</summary>
    public string UsageDelayedText => IsUsageDelayed ? new UiCopy(Language).UsageDelayed : string.Empty;

    /// <summary>Classified reason the last read failed, if it did.</summary>
    public UsageFailureKind UsageFailure => usageFailure;

    /// <summary>Fixed, localized sentence for <see cref="UsageFailure"/>; it echoes no response detail.</summary>
    public string UsageFailureText => new UiCopy(Language).UsageFailure(usageFailure);

    /// <summary>
    /// True when the values on screen come from an earlier successful read and the latest
    /// refresh failed. The values are kept and shown; they are never silently replaced by
    /// an error state or by zeros.
    /// </summary>
    public bool IsUsageStale => isUsageStale;

    public Task UsageLoadTask => usageLoadTask;

    /// <summary>
    /// Age at which daily data is reported as delayed rather than current. Collectors run
    /// every one to six hours, so twelve hours is twice the slowest normal interval.
    /// Named so the threshold is testable instead of being a literal in the getter.
    /// </summary>
    public static TimeSpan UsageDelayThreshold => TimeSpan.FromHours(12);

    private DateOnly usageFrom, usageTo;
    private DateOnly usageLoadedFrom, usageLoadedTo;

    /// <summary>Range the retained values belong to, or the range being requested before any value exists.</summary>
    private DateOnly DisplayedRangeFrom => hasUsageValues ? usageLoadedFrom : usageFrom;
    private DateOnly DisplayedRangeTo => hasUsageValues ? usageLoadedTo : usageTo;

    private DateTimeOffset? OldestReceivedAt()
    {
        DateTimeOffset? oldest = null;
        foreach (var entry in usageCollectors)
        {
            if (entry.LastReceivedAt is { } receivedAt && (oldest is null || receivedAt < oldest))
            {
                oldest = receivedAt;
            }
        }

        return oldest;
    }
    public void NavigateToUsage() { CurrentPage = AppPage.Usage; OnPropertyChanged(nameof(NavUsageText)); }
    public Task RefreshUsageAsync() { if (CurrentPage == AppPage.Usage) { usageLoadTask = LoadUsageAsync(); breakdownLoadTask = LoadBreakdownAsync(); } return usageLoadTask; }
    private async Task LoadUsageAsync()
    {
        var id = Interlocked.Increment(ref usageRequestId);
        usageRequest?.Cancel(); usageRequest?.Dispose();
        usageRequest = new CancellationTokenSource(); var token = usageRequest.Token;

        if (!usageReadPort.IsConfigured)
        {
            // Nothing to read: drop the surface rather than leave values from a
            // connection that no longer exists.
            ClearUsageValues();
            usageState = "Unconfigured";
            NotifyUsageSurface();
            return;
        }

        // A load keeps the previous rows, total and freshness on screen. Clearing them
        // here would make a failed refresh look like a period with no usage, which is a
        // different and false statement.
        usageState = "Loading";
        try
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timeZoneResolver("Asia/Tokyo")).DateTime);
            usageFrom = today.AddDays(-29); usageTo = today;
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // No usable zone database (tzdata/ICU missing): stop in a fixed state. Neither
            // the zone id nor the exception message may reach the UI.
            ApplyUsageFailure(id, UsageFailureKind.Transient);
            return;
        }

        NotifyUsageSurface();
        try
        {
            var result = await usageReadPort.ReadDailyAsync(usageFrom, usageTo, null, token);
            if (token.IsCancellationRequested || id != Volatile.Read(ref usageRequestId) || CurrentPage != AppPage.Usage) return;
            usageRows = result.Days;
            usageTotal = $"{result.TotalTokens:N0}";
            usageCollectors = result.Collectors;
            usageLoadedFrom = usageFrom;
            usageLoadedTo = usageTo;
            hasUsageValues = true;
            usageFailure = UsageFailureKind.None;
            isUsageStale = false;
            usageState = result.Days.Count == 0 ? "Empty" : "Ready";
            NotifyUsageSurface();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (UsageReadException failure) { ApplyUsageFailure(id, MapUsageFailure(failure.Failure)); }
        catch { ApplyUsageFailure(id, UsageFailureKind.Transient); }
    }

    /// <summary>
    /// Records a failed read. With values on screen the previous rows, total and freshness
    /// stay and the surface is marked stale; without them the surface is the plain error
    /// state. The kind is always exposed, and it never carries response text.
    /// </summary>
    private void ApplyUsageFailure(long id, UsageFailureKind kind)
    {
        if (id != Volatile.Read(ref usageRequestId) || CurrentPage != AppPage.Usage) return;
        usageFailure = kind;
        if (hasUsageValues)
        {
            isUsageStale = true;
            usageState = "Stale";
        }
        else
        {
            isUsageStale = false;
            usageState = "Error";
        }

        NotifyUsageSurface();
    }

    private void ClearUsageValues()
    {
        usageRows = [];
        usageTotal = string.Empty;
        usageCollectors = [];
        usageFailure = UsageFailureKind.None;
        isUsageStale = false;
        hasUsageValues = false;
    }

    private void NotifyUsageSurface()
    {
        OnPropertyChanged(nameof(UsageRangeText));
        OnPropertyChanged(nameof(IsUsageRangeVisible));
        OnPropertyChanged(nameof(UsageRows));
        OnPropertyChanged(nameof(UsageTotalText));
        OnPropertyChanged(nameof(IsUsageTotalVisible));
        OnPropertyChanged(nameof(UsageStateText));
        OnPropertyChanged(nameof(UsageStatusText));
        OnPropertyChanged(nameof(CanRefreshUsage));
        OnPropertyChanged(nameof(IsUsageLoading));
        OnPropertyChanged(nameof(UsageFailure));
        OnPropertyChanged(nameof(UsageFailureText));
        OnPropertyChanged(nameof(IsUsageStale));
        OnPropertyChanged(nameof(UsageFreshnessText));
        OnPropertyChanged(nameof(IsUsageFreshnessVisible));
        OnPropertyChanged(nameof(IsUsageDelayed));
        OnPropertyChanged(nameof(UsageDelayedText));
    }

    /// <summary>
    /// Collapses the port's fine-grained failure into the kinds the screen may name. Every
    /// branch is a fixed classification; no message, status code, URI or identity is read.
    /// </summary>
    private static UsageFailureKind MapUsageFailure(UsageReadFailure failure) => failure switch
    {
        UsageReadFailure.Unauthorized => UsageFailureKind.Unauthorized,
        UsageReadFailure.Forbidden => UsageFailureKind.Forbidden,
        UsageReadFailure.NotFound => UsageFailureKind.NotFound,
        UsageReadFailure.RedirectRejected => UsageFailureKind.Redirected,
        UsageReadFailure.CredentialUnavailable or UsageReadFailure.IdentityMismatch => UsageFailureKind.CredentialMissing,
        UsageReadFailure.TransportFailure or UsageReadFailure.Timeout
            or UsageReadFailure.AuthUnavailable or UsageReadFailure.Transient => UsageFailureKind.Transient,
        _ => UsageFailureKind.InvalidResponse,
    };

    /// <summary>
    /// Collapses a breakdown port failure. A generic 404 is not a generic error here: on
    /// the breakdown endpoint it means the server does not serve the breakdown yet, and
    /// it gets its own state instead of the retry language. `collector_not_found` keeps
    /// its own kind so the settings detour can be offered.
    /// </summary>
    private static UsageFailureKind MapBreakdownFailure(UsageReadFailure failure) => failure switch
    {
        UsageReadFailure.NotFound => UsageFailureKind.BreakdownUnsupported,
        UsageReadFailure.CollectorNotFound => UsageFailureKind.CollectorNotFound,
        _ => MapUsageFailure(failure),
    };

    // ---------- Usage breakdown (TokScale attribution) ----------
    //
    // A second, independent read over the same connection. Its failure states are kept
    // apart from the daily read's on purpose: the daily totals can keep working while the
    // breakdown is unsupported or failing, and neither read may stale the other.

    private string breakdownState = "Unconfigured";
    private UsageFailureKind breakdownFailure = UsageFailureKind.None;
    private bool isBreakdownStale;
    private bool hasBreakdownValues;
    private UsageBreakdown? breakdown;
    private UsageBreakdownDimension breakdownDimension = UsageBreakdownDimension.Client;
    private CancellationTokenSource? breakdownRequest;
    private long breakdownRequestId;
    private Task breakdownLoadTask = Task.CompletedTask;

    public Task BreakdownLoadTask => breakdownLoadTask;

    /// <summary>The breakdown surface's own state, separate from the daily read's state.</summary>
    public string BreakdownState => breakdownState;

    /// <summary>True while the surface holds anything at all (configured and past Unconfigured).</summary>
    public bool IsBreakdownSurfaceConfigured => breakdownState != "Unconfigured";

    public bool IsUsageBreakdownLoading => breakdownState == "Loading";

    /// <summary>Switching the grouping key is suppressed while a read is in flight.</summary>
    public bool CanSwitchBreakdownDimension => breakdownState != "Loading";

    public string UsageBreakdownStateText => new UiCopy(Language).UsageBreakdownState(breakdownState);

    public UsageFailureKind BreakdownFailure => breakdownFailure;

    public string BreakdownFailureText => new UiCopy(Language).UsageFailure(breakdownFailure);

    /// <summary>True when the breakdown values on screen come from an earlier successful read.</summary>
    public bool IsBreakdownStale => isBreakdownStale;

    /// <summary>Fixed TokScale observation disclaimer shown with any breakdown values.</summary>
    public string BreakdownNoteText => CopyText.UsageBreakdownNote;

    public bool IsBreakdownNoteVisible => breakdownState is not ("Unconfigured" or "Unsupported");

    public UsageBreakdownDimension SelectedBreakdownDimension
    {
        get => breakdownDimension;
        set
        {
            if (Set(ref breakdownDimension, value))
            {
                OnPropertyChanged(nameof(BreakdownRows));
                OnPropertyChanged(nameof(IsBreakdownRowsVisible));
            }
        }
    }

    public IReadOnlyList<LocalizedChoice<UsageBreakdownDimension>> BreakdownDimensionChoices =>
        Enum.GetValues<UsageBreakdownDimension>()
            .Select(dimension => new LocalizedChoice<UsageBreakdownDimension>(dimension, CopyText.UsageBreakdownDimensionName(dimension)))
            .ToList();

    /// <summary>Groups of the loaded breakdown under the selected key, or an empty list before any read.</summary>
    public IReadOnlyList<BreakdownGroup> BreakdownRows => hasBreakdownValues && breakdown is { } value
        ? UsageBreakdownAggregation.GroupParts(value.Parts, breakdownDimension, Language)
        : [];

    public bool IsBreakdownRowsVisible => BreakdownRows.Count > 0;

    /// <summary>Σ max(0, day total − attributed) over the loaded coverage, exactly.</summary>
    public long BreakdownUnclassifiedTokens => hasBreakdownValues && breakdown is { } value
        ? UsageBreakdownAggregation.UnclassifiedTokens(value.Coverage)
        : 0;

    public bool IsBreakdownUnclassifiedVisible => hasBreakdownValues && BreakdownUnclassifiedTokens > 0;

    public string BreakdownUnclassifiedText => CopyText.UsageBreakdownUnclassified(BreakdownUnclassifiedTokens);

    /// <summary>True when the range has both attributed days and usage days without any.</summary>
    public bool IsBreakdownPartialVisible => hasBreakdownValues && breakdown is { } value
        && UsageBreakdownAggregation.IsPartiallyAttributed(value.Coverage);

    public string BreakdownPartialText => CopyText.UsageBreakdownPartial;

    /// <summary>True when any coverage row attributes more than its day total received.</summary>
    public bool IsBreakdownOverAttributedVisible => hasBreakdownValues
        && breakdown is { } value
        && UsageBreakdownAggregation.OverAttributedRows(value.Coverage).Count > 0;

    /// <summary>
    /// One exact, unrounded line per over-attributed coverage row. The raw numbers are
    /// shown because a summary would hide a measurement defect.
    /// </summary>
    public string BreakdownOverAttributionText => hasBreakdownValues && breakdown is { } value
        ? string.Join(Environment.NewLine, UsageBreakdownAggregation.OverAttributedRows(value.Coverage)
            .Select(row => CopyText.UsageBreakdownOverAttributionRow(
                row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), row.AttributedTokens, row.TotalTokens)))
        : string.Empty;

    private async Task LoadBreakdownAsync()
    {
        var id = Interlocked.Increment(ref breakdownRequestId);
        breakdownRequest?.Cancel(); breakdownRequest?.Dispose();
        breakdownRequest = new CancellationTokenSource(); var token = breakdownRequest.Token;

        if (!usageReadPort.IsConfigured)
        {
            // Nothing to read: drop the surface rather than leave values from a
            // connection that no longer exists.
            breakdown = null;
            hasBreakdownValues = false;
            breakdownFailure = UsageFailureKind.None;
            isBreakdownStale = false;
            breakdownState = "Unconfigured";
            NotifyBreakdownSurface();
            return;
        }

        // A load keeps the previous groups on screen, exactly like the daily read does;
        // clearing them would make a failed refresh look like "no breakdown".
        breakdownState = "Loading";
        NotifyBreakdownSurface();
        try
        {
            DateOnly from, to;
            try
            {
                var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timeZoneResolver("Asia/Tokyo")).DateTime);
                from = today.AddDays(-29); to = today;
            }
            catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // No usable zone database (tzdata/ICU missing): stop in a fixed state.
                ApplyBreakdownFailure(id, UsageFailureKind.Transient);
                return;
            }

            var result = await usageReadPort.ReadBreakdownAsync(from, to, null, token);
            if (token.IsCancellationRequested || id != Volatile.Read(ref breakdownRequestId) || CurrentPage != AppPage.Usage) return;
            breakdown = result;
            hasBreakdownValues = true;
            breakdownFailure = UsageFailureKind.None;
            isBreakdownStale = false;
            breakdownState = result.Parts.Count == 0 ? "Empty" : "Ready";
            NotifyBreakdownSurface();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (UsageReadException failure) { ApplyBreakdownFailure(id, MapBreakdownFailure(failure.Failure)); }
        catch { ApplyBreakdownFailure(id, UsageFailureKind.Transient); }
    }

    private void ApplyBreakdownFailure(long id, UsageFailureKind kind)
    {
        if (id != Volatile.Read(ref breakdownRequestId) || CurrentPage != AppPage.Usage) return;
        breakdownFailure = kind;
        if (hasBreakdownValues)
        {
            isBreakdownStale = true;
            breakdownState = "Stale";
        }
        else
        {
            isBreakdownStale = false;
            breakdownState = kind == UsageFailureKind.BreakdownUnsupported ? "Unsupported" : "Error";
        }

        NotifyBreakdownSurface();
    }

    private void NotifyBreakdownSurface()
    {
        OnPropertyChanged(nameof(BreakdownState));
        OnPropertyChanged(nameof(IsBreakdownSurfaceConfigured));
        OnPropertyChanged(nameof(IsUsageBreakdownLoading));
        OnPropertyChanged(nameof(CanSwitchBreakdownDimension));
        OnPropertyChanged(nameof(UsageBreakdownStateText));
        OnPropertyChanged(nameof(BreakdownFailure));
        OnPropertyChanged(nameof(BreakdownFailureText));
        OnPropertyChanged(nameof(IsBreakdownStale));
        OnPropertyChanged(nameof(IsBreakdownNoteVisible));
        OnPropertyChanged(nameof(BreakdownNoteText));
        OnPropertyChanged(nameof(BreakdownRows));
        OnPropertyChanged(nameof(IsBreakdownRowsVisible));
        OnPropertyChanged(nameof(BreakdownUnclassifiedTokens));
        OnPropertyChanged(nameof(IsBreakdownUnclassifiedVisible));
        OnPropertyChanged(nameof(BreakdownUnclassifiedText));
        OnPropertyChanged(nameof(IsBreakdownPartialVisible));
        OnPropertyChanged(nameof(BreakdownPartialText));
        OnPropertyChanged(nameof(IsBreakdownOverAttributedVisible));
        OnPropertyChanged(nameof(BreakdownOverAttributionText));
    }

    // ---------- Dashboard: observed usage (TokScale) ----------
    //
    // A third read lane, deliberately the slowest: two breakdown reads (today, last seven
    // days) per interval, never per page interaction. No ring, no gauge, no threshold —
    // TokScale observes tokens, it knows nothing about a remaining quota.

    private string tokScaleState = "Hidden";
    private IReadOnlyList<TokScaleUsageCard> tokScaleCards = [];
    private IReadOnlyList<string> trackedTokScaleProviders = [];
    private bool hasTokScaleValues;
    private UsageBreakdown? tokScaleTodayBreakdown;
    private UsageBreakdown? tokScaleWeekBreakdown;
    private IReadOnlyList<UsageCollectorReceipt> tokScaleCollectors = [];
    private DateTimeOffset? tokScaleLoadedAt;
    private UsageFailureKind tokScaleFailure = UsageFailureKind.None;
    private bool isTokScaleStale;
    private int tokScaleObservedProviderCount;
    private CancellationTokenSource? tokScaleRequest;
    private long tokScaleRequestId;
    private Task tokScaleLoadTask = Task.CompletedTask;

    /// <summary>
    /// Minimum age of a successful section load before navigation or a scheduled refresh
    /// reads again. Named so the interval is testable instead of being a literal.
    /// </summary>
    public static TimeSpan TokScaleRefreshInterval => TimeSpan.FromMinutes(60);

    public Task TokScaleLoadTask => tokScaleLoadTask;

    public string TokScaleState => tokScaleState;

    /// <summary>
    /// The section exists once it has been looked at; a missing usage connection shows an
    /// empty state with a settings shortcut instead of hiding the added cards.
    /// </summary>
    public bool IsTokScaleSectionVisible => tokScaleState != "Hidden";

    public bool IsTokScaleLoading => tokScaleState == "Loading";

    public bool IsTokScaleStale => isTokScaleStale;

    /// <summary>True while no usage API connection exists: no read is attempted.</summary>
    public bool IsTokScaleUnconfigured => tokScaleState == "Unconfigured";

    /// <summary>
    /// The empty state replaces the cards when nothing is selected: a title and a body
    /// that name the next step for this exact situation.
    /// </summary>
    public bool IsTokScaleEmptyStateVisible => !IsTokScaleCardsVisible && tokScaleState is "Unconfigured" or "Empty";

    public string TokScaleEmptyTitleText => CopyText.TokScaleEmptyTitle;

    public string TokScaleEmptyBodyText => IsTokScaleUnconfigured
        ? CopyText.TokScaleEmptyBodyUnconfigured
        : tokScaleObservedProviderCount > 0
            ? CopyText.TokScaleEmptyBodyWithObservations(tokScaleObservedProviderCount)
            : CopyText.TokScaleEmptyBody;

    public string TokScaleOpenSettingsText => CopyText.TokScaleOpenSettings;

    public string TokScaleStateText => new UiCopy(Language).TokScaleState(tokScaleState);

    public string TokScaleFailureText => new UiCopy(Language).UsageFailure(tokScaleFailure);

    public IReadOnlyList<TokScaleUsageCard> TokScaleCards => tokScaleCards;

    public bool IsTokScaleCardsVisible => tokScaleCards.Count > 0;

    /// <summary>The state sentence shows whenever the section has no cards to show, or
    /// whenever the connection is missing — the cards never hide that.</summary>
    public bool IsTokScaleStatusVisible =>
        (!IsTokScaleCardsVisible && !IsTokScaleEmptyStateVisible) || tokScaleState == "Unconfigured";

    public string TokScaleTitleText => CopyText.TokScaleSectionTitle;

    public string TokScaleBadgeText => CopyText.TokScaleBadge;

    public string TokScaleTodayLabel => CopyText.TokScaleToday;

    public string TokScaleWeekLabel => CopyText.TokScaleWeek;

    /// <summary>
    /// Freshness of the section: the oldest reception time across the collectors the
    /// last weekly read covered, formatted in the user's local zone. Empty before any read.
    /// </summary>
    public string TokScaleFreshnessText
    {
        get
        {
            if (!hasTokScaleValues || tokScaleCollectors.Count == 0)
            {
                return string.Empty;
            }

            var copy = new UiCopy(Language);
            DateTimeOffset? oldest = null;
            foreach (var entry in tokScaleCollectors)
            {
                if (entry.LastReceivedAt is { } receivedAt && (oldest is null || receivedAt < oldest))
                {
                    oldest = receivedAt;
                }
            }

            if (oldest is not { } received)
            {
                return copy.UsageFreshnessNeverReceived;
            }

            var local = TimeZoneInfo.ConvertTime(received, localTimeZone);
            return tokScaleCollectors.Any(entry => entry.LastReceivedAt is null)
                ? copy.UsageFreshnessLastReceivedWithMissing(local)
                : copy.UsageFreshnessLastReceived(local);
        }
    }

    public bool IsTokScaleFreshnessVisible => TokScaleFreshnessText.Length > 0;

    /// <summary>
    /// Reads the section again when it is due. Navigation to the dashboard and the
    /// scheduled refresh both come through here; the interval throttle keeps the read
    /// volume far below the usage page's. Returns the in-flight or completed load.
    /// </summary>
    public Task RefreshTokScaleAsync(bool force = false)
    {
        if (!force && CurrentPage != AppPage.Dashboard)
        {
            return tokScaleLoadTask;
        }

        tokScaleLoadTask = LoadTokScaleAsync(force);
        return tokScaleLoadTask;
    }

    private async Task LoadTokScaleAsync(bool force)
    {
        var id = Interlocked.Increment(ref tokScaleRequestId);
        tokScaleRequest?.Cancel(); tokScaleRequest?.Dispose();
        tokScaleRequest = new CancellationTokenSource(); var token = tokScaleRequest.Token;

        if (!usageReadPort.IsConfigured)
        {
            // No connection yet: the section stays, added providers show their "not
            // observed yet" cards, and no read is attempted, so no observation data can
            // appear and nothing may be claimed as observed.
            tokScaleState = "Unconfigured";
            var empty = EmptyBreakdown();
            tokScaleTodayBreakdown = empty;
            tokScaleWeekBreakdown = empty;
            tokScaleCards = UsageBreakdownAggregation.TokScaleCards(empty, empty, trackedTokScaleProviders, Language);
            tokScaleCollectors = [];
            hasTokScaleValues = false;
            tokScaleFailure = UsageFailureKind.None;
            isTokScaleStale = false;
            tokScaleObservedProviderCount = 0;
            NotifyTokScaleSurface();
            return;
        }

        if (!force && tokScaleLoadedAt is { } loaded && timeProvider.GetUtcNow() - loaded < TokScaleRefreshInterval)
        {
            return;
        }

        tokScaleState = "Loading";
        NotifyTokScaleSurface();
        try
        {
            DateOnly today;
            try
            {
                today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timeZoneResolver("Asia/Tokyo")).DateTime);
            }
            catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                ApplyTokScaleFailure(id, UsageFailureKind.Transient);
                return;
            }

            // Two reads: parts are aggregated over a whole range, so today's per-provider
            // totals need today's read and the seven-day totals need the week's.
            var todayRead = await usageReadPort.ReadBreakdownAsync(today, today, null, token);
            if (token.IsCancellationRequested || id != Volatile.Read(ref tokScaleRequestId)) return;
            var weekRead = await usageReadPort.ReadBreakdownAsync(today.AddDays(-6), today, null, token);
            if (token.IsCancellationRequested || id != Volatile.Read(ref tokScaleRequestId)) return;

            tokScaleCards = UsageBreakdownAggregation.TokScaleCards(todayRead, weekRead, trackedTokScaleProviders, Language);
            tokScaleTodayBreakdown = todayRead;
            tokScaleWeekBreakdown = weekRead;
            tokScaleCollectors = weekRead.Collectors;
            hasTokScaleValues = true;
            tokScaleLoadedAt = timeProvider.GetUtcNow();
            tokScaleFailure = UsageFailureKind.None;
            isTokScaleStale = false;
            tokScaleObservedProviderCount = UsageBreakdownAggregation.ObservedProviderCount(todayRead, weekRead);
            tokScaleState = tokScaleCards.Count == 0 ? "Empty" : "Ready";
            NotifyTokScaleSurface();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (UsageReadException failure) { ApplyTokScaleFailure(id, MapBreakdownFailure(failure.Failure)); }
        catch { ApplyTokScaleFailure(id, UsageFailureKind.Transient); }
    }

    private void ApplyTokScaleFailure(long id, UsageFailureKind kind)
    {
        if (id != Volatile.Read(ref tokScaleRequestId)) return;
        tokScaleFailure = kind;
        if (hasTokScaleValues)
        {
            isTokScaleStale = true;
            tokScaleState = "Stale";
        }
        else
        {
            isTokScaleStale = false;
            tokScaleState = "Error";
        }

        NotifyTokScaleSurface();
    }

    /// <summary>A read with nothing in it, used when no connection exists. It is never displayed.</summary>
    private static UsageBreakdown EmptyBreakdown() => new(default, default, [], [], []);

    /// <summary>Rebuilds the stored cards in the current language: the numbers are the
    /// observation, only their labels change with the switch. The unconfigured state also
    /// carries the empty reads, so the added providers follow the switch there too.</summary>
    private void RelocalizeTokScale()
    {
        if (tokScaleTodayBreakdown is { } today && tokScaleWeekBreakdown is { } week)
        {
            RebuildTokScaleCards(today, week);
        }
    }

    /// <summary>Rebuilds the stored cards over the same two reads with the current
    /// tracked list and language, then keeps the section state honest about the count.</summary>
    private void RebuildTokScaleCards(UsageBreakdown today, UsageBreakdown week)
    {
        tokScaleCards = UsageBreakdownAggregation.TokScaleCards(today, week, trackedTokScaleProviders, Language);
        if (tokScaleState is "Ready" or "Empty")
        {
            tokScaleState = tokScaleCards.Count == 0 ? "Empty" : "Ready";
        }
    }

    private void NotifyTokScaleSurface()
    {
        OnPropertyChanged(nameof(TokScaleState));
        OnPropertyChanged(nameof(IsTokScaleSectionVisible));
        OnPropertyChanged(nameof(IsTokScaleLoading));
        OnPropertyChanged(nameof(IsTokScaleStale));
        OnPropertyChanged(nameof(IsTokScaleUnconfigured));
        OnPropertyChanged(nameof(IsTokScaleEmptyStateVisible));
        OnPropertyChanged(nameof(TokScaleEmptyTitleText));
        OnPropertyChanged(nameof(TokScaleEmptyBodyText));
        OnPropertyChanged(nameof(TokScaleOpenSettingsText));
        OnPropertyChanged(nameof(TokScaleStateText));
        OnPropertyChanged(nameof(TokScaleFailureText));
        OnPropertyChanged(nameof(TokScaleTitleText));
        OnPropertyChanged(nameof(TokScaleBadgeText));
        OnPropertyChanged(nameof(TokScaleTodayLabel));
        OnPropertyChanged(nameof(TokScaleWeekLabel));
        OnPropertyChanged(nameof(TokScaleCards));
        OnPropertyChanged(nameof(IsTokScaleCardsVisible));
        OnPropertyChanged(nameof(IsTokScaleStatusVisible));
        OnPropertyChanged(nameof(TokScaleFreshnessText));
        OnPropertyChanged(nameof(IsTokScaleFreshnessVisible));
        OnPropertyChanged(nameof(CanRefresh));
    }
    public string SubtitleText => CopyText.Subtitle;
    /// <summary>Stored usage API endpoint as an origin, or an empty string. Never a credential.</summary>
    public string UsageApiEndpointText => usageApiEndpoint;

    /// <summary>True when a complete, valid credential pair is stored. The values are never exposed.</summary>
    public bool HasUsageCredential => hasUsageCredential;

    /// <summary>True when reads would actually go to a configured usage API.</summary>
    public bool IsUsageConfigured => usageReadPort.IsConfigured;

    /// <summary>Connection state derived from the stored endpoint and the credential store.</summary>
    public UsageConnectionState UsageConnectionState => usageConnectionState;

    /// <summary>Classified reason the last connection change was refused.</summary>
    public UsageConnectionError UsageConnectionError => usageConnectionError;

    /// <summary>Fixed, localized sentence for <see cref="UsageConnectionState"/>; it names no endpoint or identity.
    /// While a save or removal is in flight it names the running operation instead, so the banner never
    /// contradicts the progress strip below it.</summary>
    public string UsageConnectionStatusText => usageConnectionBusy
        ? usageConnectionRemoving ? CopyText.UsageConnectionRemoving : CopyText.UsageConnectionSaving
        : CopyText.UsageConnectionStatus(usageConnectionState);

    /// <summary>Fixed, localized sentence for <see cref="UsageConnectionError"/>; it echoes no input.</summary>
    public string UsageConnectionErrorText => CopyText.UsageConnectionErrorText(usageConnectionError);

    // State flags the settings card binds to; they let the card color and icon each state
    // without converters, and are refreshed with the rest of the connection surface.
    /// <summary>True when no usable endpoint or credential pair is stored.</summary>
    public bool IsUsageConnectionUnconfigured => usageConnectionState == UsageConnectionState.Unconfigured;

    /// <summary>True when the connection is saved in a secure credential store.</summary>
    public bool IsUsageConnectionConfigured => usageConnectionState == UsageConnectionState.Configured;

    /// <summary>True when the credential pair lives in this session only.</summary>
    public bool IsUsageConnectionSessionOnly => usageConnectionState == UsageConnectionState.SessionOnly;

    /// <summary>True when nothing can be saved because the credential store is unavailable.</summary>
    public bool IsUsageConnectionStoreUnavailable => usageConnectionState == UsageConnectionState.StoreUnavailable;

    /// <summary>True when the stored endpoint violates the endpoint rules.</summary>
    public bool IsUsageConnectionInvalid => usageConnectionState == UsageConnectionState.Invalid;

    /// <summary>True when the last connection change was refused, so fixed copy must show near the fields.</summary>
    public bool HasUsageConnectionError => usageConnectionError is not UsageConnectionError.None;

    /// <summary>True while a usage connection save or removal is still in flight.</summary>
    public bool IsUsageConnectionBusy => usageConnectionBusy;

    /// <summary>
    /// Marks the usage connection operation that the settings page is currently showing its
    /// progress strip for. Clearing it (or starting a different operation) re-renders the
    /// status banner through <see cref="UsageConnectionStatusText"/>.
    /// </summary>
    public void SetUsageConnectionBusy(bool busy, bool removing)
    {
        if (usageConnectionBusy == busy && (!busy || usageConnectionRemoving == removing)) return;
        usageConnectionBusy = busy;
        usageConnectionRemoving = removing;
        OnPropertyChanged(nameof(IsUsageConnectionBusy));
        OnPropertyChanged(nameof(UsageConnectionStatusText));
    }

    /// <summary>Presence-only credential text: whether a pair is stored, never any stored value.</summary>
    public string UsageCredentialStatusText => HasUsageCredential ? CopyText.UsageCredentialSaved : CopyText.UsageCredentialNone;

    /// <summary>
    /// Validates the endpoint, stores the client id and the client secret in the credential
    /// store, persists the endpoint as an ordinary setting, and then points the usage read
    /// port at the new origin. The two secret arguments are used only for the store write and
    /// are never kept in a field, a property, a log message or a ToString.
    ///
    /// Nothing is left half-saved: a store refusal changes nothing, and a settings write
    /// failure restores the credential pair that was stored before the call.
    /// </summary>
    /// <exception cref="IOException">The settings file could not be written; the credential store was rolled back.</exception>
    public async Task<UsageConnectionSaveResult> SaveUsageConnectionAsync(string endpoint, string clientId, string clientSecret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(clientSecret);
        if (!UsageEndpoint.TryNormalize(endpoint, out var normalized, out var validationError))
        {
            return PublishUsageConnectionFailure(validationError, usageConnectionState);
        }

        if (usageCredentials is null)
        {
            return PublishUsageConnectionFailure(UsageConnectionError.CredentialStoreUnavailable, UsageConnectionState.StoreUnavailable);
        }

        await settingsTransactionGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previous = await usageCredentials.ReadStoredPairForRollbackAsync(cancellationToken);
            var outcome = await usageCredentials.SaveAsync(clientId, clientSecret, cancellationToken);
            if (outcome is UsageCredentialSaveOutcome.Invalid)
            {
                return PublishUsageConnectionFailure(UsageConnectionError.InvalidCredential, usageConnectionState);
            }

            if (outcome is UsageCredentialSaveOutcome.StoreUnavailable or UsageCredentialSaveOutcome.WriteFailed)
            {
                await RestoreUsageCredentialPairAsync(previous, CancellationToken.None);
                return PublishUsageConnectionFailure(UsageConnectionError.CredentialStoreUnavailable, UsageConnectionState.StoreUnavailable);
            }

            try
            {
                if (settingsStore is not null)
                {
                    await SaveSettingsAsync(CurrentSettings() with { UsageApiEndpoint = normalized }, cancellationToken);
                }
            }
            catch
            {
                // The endpoint did not become a setting, so the pair must not stay behind either.
                await RestoreUsageCredentialPairAsync(previous, CancellationToken.None);
                throw;
            }

            Settings.UsageApiEndpoint = normalized;
            usageApiEndpoint = normalized;
            hasUsageCredential = true;
            usageConnectionError = UsageConnectionError.None;
            usageConnectionState = outcome is UsageCredentialSaveOutcome.SessionOnly
                ? UsageConnectionState.SessionOnly
                : UsageConnectionState.Configured;
            ConfigureUsageReadPort(normalized);
            PublishUsageConnectionChanged();
            // The page already on screen is refreshed through the existing read path; no new
            // transport or reload route is introduced.
            if (CurrentPage == AppPage.Usage) usageLoadTask = LoadUsageAsync();
            return UsageConnectionSaveResult.Saved(usageConnectionState);
        }
        finally { settingsTransactionGate.Release(); }
    }

    /// <summary>
    /// Removes both credential-store accounts, clears the stored endpoint, and returns the
    /// read port to the unconfigured state. A store that cannot remove is reported through
    /// <see cref="UsageConnectionError"/> instead of leaving the UI claiming success.
    /// </summary>
    public async Task ClearUsageConnectionAsync(CancellationToken cancellationToken = default)
    {
        await settingsTransactionGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var removalFailed = false;
            if (usageCredentials is not null)
            {
                try
                {
                    await usageCredentials.RemoveAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    removalFailed = true;
                }
            }

            if (settingsStore is not null)
            {
                await SaveSettingsAsync(CurrentSettings() with { UsageApiEndpoint = string.Empty }, cancellationToken);
            }

            Settings.UsageApiEndpoint = string.Empty;
            usageApiEndpoint = string.Empty;
            hasUsageCredential = false;
            usageReadPort.Reset();
            usageConnectionState = removalFailed ? UsageConnectionState.StoreUnavailable : UsageConnectionState.Unconfigured;
            usageConnectionError = removalFailed ? UsageConnectionError.CredentialStoreUnavailable : UsageConnectionError.None;
            // Both dependent surfaces drop their values with the connection: no cached
            // breakdown or observed-usage card may outlive the credential it was read with.
            breakdown = null;
            hasBreakdownValues = false;
            breakdownFailure = UsageFailureKind.None;
            isBreakdownStale = false;
            breakdownState = "Unconfigured";
            // Observed values drop with the credential they were read with, but the section
            // itself stays: added providers keep a "not observed yet" card and the empty state
            // points at settings until a connection exists again.
            tokScaleState = "Unconfigured";
            var emptyBreakdown = EmptyBreakdown();
            tokScaleTodayBreakdown = emptyBreakdown;
            tokScaleWeekBreakdown = emptyBreakdown;
            tokScaleCards = UsageBreakdownAggregation.TokScaleCards(emptyBreakdown, emptyBreakdown, trackedTokScaleProviders, Language);
            tokScaleCollectors = [];
            hasTokScaleValues = false;
            tokScaleFailure = UsageFailureKind.None;
            isTokScaleStale = false;
            tokScaleObservedProviderCount = 0;
            PublishUsageConnectionChanged();
            NotifyBreakdownSurface();
            NotifyTokScaleSurface();
        }
        finally { settingsTransactionGate.Release(); }
    }

    private async Task RefreshUsageConnectionAsync(CancellationToken cancellationToken)
    {
        usageApiEndpoint = Settings.UsageApiEndpoint ?? string.Empty;
        var hasEndpoint = !string.IsNullOrWhiteSpace(usageApiEndpoint);
        var valid = UsageEndpoint.TryNormalize(usageApiEndpoint, out var normalized, out var validationError);
        usageConnectionError = valid || !hasEndpoint ? UsageConnectionError.None : validationError;

        if (usageCredentials is null)
        {
            // No credential store was wired: reads can only be configured explicitly.
            hasUsageCredential = false;
            usageConnectionState = hasEndpoint && !valid ? UsageConnectionState.Invalid : UsageConnectionState.Unconfigured;
            PublishUsageConnectionChanged();
            return;
        }

        hasUsageCredential = await usageCredentials.HasCredentialAsync(cancellationToken);
        var availability = usageCredentials.Availability;
        if (!valid)
        {
            usageConnectionState = hasEndpoint ? UsageConnectionState.Invalid : UsageConnectionState.Unconfigured;
            usageReadPort.Reset();
        }
        else if (!hasUsageCredential)
        {
            usageConnectionState = availability is CredentialStoreAvailability.Unavailable or CredentialStoreAvailability.Locked
                ? UsageConnectionState.StoreUnavailable
                : UsageConnectionState.Unconfigured;
            usageReadPort.Reset();
        }
        else
        {
            usageConnectionState = availability is CredentialStoreAvailability.SecureStore
                ? UsageConnectionState.Configured
                : UsageConnectionState.SessionOnly;
            ConfigureUsageReadPort(normalized);
        }

        PublishUsageConnectionChanged();
    }

    private void ConfigureUsageReadPort(string endpoint)
    {
        if (usageCredentials is null) return;
        // The port adapter owns the one long-lived client and swaps only the target, so a
        // save or a removal can never dispose a transport an outstanding read is using.
        usageReadPort.Configure(new Uri(endpoint, UriKind.Absolute), StoreUsageCredentialProvider.IdentityId, usageCredentials);
    }

    private async Task RestoreUsageCredentialPairAsync((string? ClientId, string? ClientSecret) previous, CancellationToken cancellationToken)
    {
        if (usageCredentials is null) return;
        if (previous.ClientId is { Length: > 0 } clientId && previous.ClientSecret is { Length: > 0 } clientSecret)
        {
            await usageCredentials.SaveAsync(clientId, clientSecret, cancellationToken);
            return;
        }

        try
        {
            await usageCredentials.RemoveAsync(cancellationToken);
        }
        catch (Exception)
        {
            // Best effort: the caller is already reporting the failed save.
        }
    }

    private UsageConnectionSaveResult PublishUsageConnectionFailure(UsageConnectionError error, UsageConnectionState state)
    {
        usageConnectionError = error;
        usageConnectionState = state;
        PublishUsageConnectionChanged();
        return UsageConnectionSaveResult.Failed(error, state);
    }

    private void PublishUsageConnectionChanged()
    {
        OnPropertyChanged(nameof(UsageApiEndpointText));
        OnPropertyChanged(nameof(HasUsageCredential));
        OnPropertyChanged(nameof(IsUsageConfigured));
        OnPropertyChanged(nameof(UsageConnectionState));
        OnPropertyChanged(nameof(UsageConnectionError));
        OnPropertyChanged(nameof(UsageConnectionStatusText));
        OnPropertyChanged(nameof(UsageConnectionErrorText));
        OnPropertyChanged(nameof(IsUsageConnectionUnconfigured));
        OnPropertyChanged(nameof(IsUsageConnectionConfigured));
        OnPropertyChanged(nameof(IsUsageConnectionSessionOnly));
        OnPropertyChanged(nameof(IsUsageConnectionStoreUnavailable));
        OnPropertyChanged(nameof(IsUsageConnectionInvalid));
        OnPropertyChanged(nameof(HasUsageConnectionError));
        OnPropertyChanged(nameof(UsageCredentialStatusText));
    }

    // ---------- the add-provider flow (catalog providers only) ----------

    public IReadOnlyList<ProviderChoiceViewModel> ProviderChoices => UiSettings.ProviderChoices(Language);
    private bool providerSelectionMade;
    public bool IsProviderFlowOpen { get; private set; }
    public TokScaleProviderInfo? SelectedTokScaleProvider { get; private set; }
    public bool IsTokScaleProviderPanelVisible => IsProviderFlowOpen && providerSelectionMade && SelectedTokScaleProvider is not null;
    public string TokScaleFlowTitleText => SelectedTokScaleProvider?.DisplayName ?? string.Empty;
    public string TokScaleFlowDescriptionText => CopyText.TokScalePanelDescription;
    public string TokScaleFlowStatusText => SelectedTokScaleProvider is { } provider
        ? IsTokScaleProviderTracked ? CopyText.TokScalePanelTracked : CopyText.TokScalePanelNotTracked
        : string.Empty;
    public bool IsTokScaleProviderTracked => SelectedTokScaleProvider is { } provider
        && trackedTokScaleProviders.Contains(provider.Id, StringComparer.Ordinal);
    public void OpenProviderFlow() { SelectedTokScaleProvider = null; IsProviderFlowOpen = true; providerSelectionMade = false; NotifyProviderFlow(); }
    public void CloseProviderFlow() { SelectedTokScaleProvider = null; IsProviderFlowOpen = false; providerSelectionMade = false; NotifyProviderFlow(); }
    public void SelectTokScaleProvider(string providerId)
    {
        SelectedTokScaleProvider = TokScaleProviderCatalog.Find(providerId);
        if (SelectedTokScaleProvider is null) return;
        providerSelectionMade = true;
        IsProviderFlowOpen = true;
        NotifyProviderFlow();
    }
    public Task AddTokScaleProviderAsync(string providerId, CancellationToken cancellationToken = default) => SetTokScaleProviderTrackedAsync(providerId, track: true, cancellationToken);
    public Task RemoveTokScaleProviderAsync(string providerId, CancellationToken cancellationToken = default) => SetTokScaleProviderTrackedAsync(providerId, track: false, cancellationToken);
    private Task SetTokScaleProviderTrackedAsync(string providerId, bool track, CancellationToken cancellationToken)
    {
        if (TokScaleProviderCatalog.Find(providerId) is null || IsDisposed) return Task.CompletedTask;
        return UpdateSettingsAsync(
            settings =>
            {
                var next = TokScaleTrackedList(settings.TokScaleProviders, providerId, track);
                return (settings with { TokScaleProviders = next }, true, true);
            },
            () => PublishTokScaleTrackedList(track, providerId),
            cancellationToken);
    }
    private static string[] TokScaleTrackedList(IEnumerable<string>? current, string providerId, bool track)
    {
        // Add keeps the existing order and appends only when absent: re-adding the same
        // provider must not reshuffle the saved list.
        if (track && (current ?? []).Contains(providerId, StringComparer.Ordinal))
        {
            return [.. current!];
        }

        var kept = (current ?? []).Where(id => !string.Equals(id, providerId, StringComparison.Ordinal)).ToList();
        if (track) kept.Add(providerId);
        return [.. kept];
    }
    private void PublishTokScaleTrackedList(bool track, string providerId)
    {
        trackedTokScaleProviders = TokScaleTrackedList(trackedTokScaleProviders, providerId, track);
        Settings.TokScaleProviders = trackedTokScaleProviders;
        // The unconfigured state also carries the empty reads, so adding a provider
        // builds its "not observed yet" card even before any connection exists.
        if (tokScaleTodayBreakdown is { } today && tokScaleWeekBreakdown is { } week)
        {
            RebuildTokScaleCards(today, week);
        }

        NotifyTokScaleSurface();
        NotifyProviderFlow();
    }
    private void NotifyProviderFlow()
    {
        foreach (var name in new[] { nameof(IsProviderFlowOpen), nameof(IsTokScaleProviderPanelVisible), nameof(TokScaleFlowTitleText), nameof(TokScaleFlowDescriptionText), nameof(TokScaleFlowStatusText), nameof(IsTokScaleProviderTracked) }) OnPropertyChanged(name);
    }
    public AppPage CurrentPage { get => currentPage; private set { var wasUsage = currentPage == AppPage.Usage; if (wasUsage) { Interlocked.Increment(ref usageRequestId); usageRequest?.Cancel(); } if (Set(ref currentPage, value)) { if (value == AppPage.Usage && !wasUsage) { usageLoadTask = LoadUsageAsync(); breakdownLoadTask = LoadBreakdownAsync(); } else if (value == AppPage.Dashboard && !wasUsage) { _ = RefreshTokScaleAsync(); } } } }
    public UiLanguage Language { get => language; set { if (Set(ref language, value)) { notificationService.Language = value; RelocalizeTokScale(); NotifyLocalizedProperties(); NotifyBreakdownSurface(); NotifyTokScaleSurface(); } } }
    public string LanguageCode => Language == UiLanguage.Japanese ? "日本語" : "English";

    public event PropertyChangedEventHandler? PropertyChanged;
    private readonly AppSettingsStore? settingsStore;
    private readonly TimeProvider timeProvider;
    private readonly InAppNotificationService notificationService;
    private readonly SemaphoreSlim settingsTransactionGate = new(1, 1);
    internal Func<AppSettingsDto, CancellationToken, ValueTask>? SettingsSaveAsyncForTests { get; set; }
    private ValueTask SaveSettingsAsync(AppSettingsDto settings, CancellationToken cancellationToken) =>
        SettingsSaveAsyncForTests?.Invoke(settings, cancellationToken) ?? settingsStore!.SaveAsync(settings, cancellationToken);
    private readonly IUiDispatcher uiDispatcher;
    private readonly IQuotaWorkScheduler workScheduler;
    private int disposedState;

    private bool IsDisposed => Volatile.Read(ref disposedState) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposedState, 1) != 0) return;
        usageRequest?.Cancel(); usageRequest?.Dispose(); usageRequest = null;
        breakdownRequest?.Cancel(); breakdownRequest?.Dispose(); breakdownRequest = null;
        tokScaleRequest?.Cancel(); tokScaleRequest?.Dispose(); tokScaleRequest = null;
        usageReadPort.Dispose();
    }
    public MainViewModel(TimeProvider? timeProvider = null, AppSettingsStore? settingsStore = null, IUiDispatcher? uiDispatcher = null, IQuotaWorkScheduler? workScheduler = null, IDesktopNotificationBackend? desktopNotificationBackend = null, IUsageReadPort? usageReadPort = null, ReconfigurableUsageReadPort? usageConnectionPort = null, ICredentialStore? usageCredentialStore = null, Func<HttpClient>? usageHttpClientFactory = null, Func<string, TimeZoneInfo>? timeZoneResolver = null, TimeZoneInfo? localTimeZone = null)
    {
        this.usageReadPort = usageConnectionPort ?? new ReconfigurableUsageReadPort(usageHttpClientFactory?.Invoke());
        if (usageReadPort is not null) this.usageReadPort.Configure(usageReadPort);
        this.usageCredentials = usageCredentialStore is null ? null : new StoreUsageCredentialProvider(usageCredentialStore);
        this.timeZoneResolver = timeZoneResolver ?? TimeZoneInfo.FindSystemTimeZoneById;
        this.localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.settingsStore = settingsStore;
        this.uiDispatcher = uiDispatcher ?? new AvaloniaUiDispatcher();
        this.workScheduler = workScheduler ?? new TaskRunQuotaWorkScheduler();
        notificationService = new InAppNotificationService(desktopNotificationBackend);
        notificationService.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(NotificationBannerText)); OnPropertyChanged(nameof(IsNotificationVisible)); };
    }
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (settingsStore is not null) SetSettings(await workScheduler.RunAsync(() => settingsStore.LoadAsync(cancellationToken).AsTask()), persist: false);
            await RefreshUsageConnectionAsync(cancellationToken);
        }
        finally
        {
            // The observed-usage section lives on the dashboard, which is the page startup
            // lands on; its interval throttle decides whether a read actually happens.
            // It runs even when startup ends early so the section is not left hidden until
            // the first navigation.
            tokScaleLoadTask = RefreshTokScaleAsync(force: false);
        }
    }
    public string NotificationBannerText => notificationService.BannerText;
    public bool IsNotificationVisible => !string.IsNullOrWhiteSpace(notificationService.BannerText);
    public void Notify(string title, string reason) => notificationService.Notify(title, reason);
    public void NotifyLocalized(Func<UiCopy, (string Title, string Reason)> copy) => notificationService.NotifyLocalized(copy);
    public void SetSettings(AppSettingsDto dto, bool persist = true)
    {
        // SetSettings is synchronous for startup and existing callers. Fail fast rather than
        // blocking a UI thread whose async transaction may need that context to finish.
        if (!settingsTransactionGate.Wait(0))
            throw new InvalidOperationException("Settings cannot be loaded synchronously while a settings transaction is active.");
        try
        {
            Settings.TrySetRefreshMinutes(dto.RefreshMinutes);
            Settings.NotificationsEnabled = dto.NotificationsEnabled;
            Settings.ResidentMode = dto.ResidentMode;
            Settings.UsageApiEndpoint = (dto.UsageApiEndpoint ?? string.Empty).Trim();
            // Only catalog ids are kept: a stale saved id cannot resurrect as a card. The
            // legacy client list is preserved verbatim — it is the user's saved data — but
            // it is no longer a card source.
            Settings.TokScaleClients = dto.TokScaleClients is null
                ? []
                : [.. dto.TokScaleClients.Distinct(StringComparer.Ordinal)];
            Settings.TokScaleProviders = dto.TokScaleProviders is null
                ? []
                : [.. dto.TokScaleProviders.Where(id => TokScaleProviderCatalog.Find(id) is not null).Distinct(StringComparer.Ordinal)];
            trackedTokScaleProviders = Settings.TokScaleProviders;
            Language = dto.Language == "Japanese" ? UiLanguage.Japanese : UiLanguage.English;
            Settings.Theme = Enum.TryParse<ThemeMode>(dto.Theme, true, out var theme) ? theme : ThemeMode.System;
            UiSettings.ApplyTheme(Settings.Theme);
            if (persist) settingsStore?.Save(CurrentSettings());
        }
        finally { settingsTransactionGate.Release(); }
    }
    public AppSettingsDto CurrentSettingsForTests => CurrentSettings();
    private AppSettingsDto CurrentSettings() => new(Settings.Theme.ToString(), Language == UiLanguage.Japanese ? "Japanese" : "English", Settings.RefreshMinutes, Settings.NotificationsEnabled, Settings.ResidentMode, Settings.UsageApiEndpoint, [.. Settings.TokScaleClients], [.. Settings.TokScaleProviders]);
    private async Task<T> UpdateSettingsAsync<T>(Func<AppSettingsDto, (AppSettingsDto Candidate, T Result, bool Persist)> buildCandidate, Action publish, CancellationToken cancellationToken)
    {
        await settingsTransactionGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDisposed) return default!;

            var (candidate, result, persist) = buildCandidate(CurrentSettings());
            if (persist && settingsStore is not null)
                await SaveSettingsAsync(candidate, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (IsDisposed) return result;

            if (persist) publish();
            return result;
        }
        finally { settingsTransactionGate.Release(); }
    }
    public Task SetThemeAsync(ThemeMode value, CancellationToken cancellationToken = default) => UpdateSettingsAsync(settings => (settings with { Theme = value.ToString() }, true, true), () => { Settings.Theme = value; UiSettings.ApplyTheme(value); }, cancellationToken);
    public Task SetLanguageAsync(UiLanguage value, CancellationToken cancellationToken = default) => UpdateSettingsAsync(settings => (settings with { Language = value == UiLanguage.Japanese ? "Japanese" : "English" }, true, true), () => Language = value, cancellationToken);
    public Task SetRefreshMinutesAsync(int value, CancellationToken cancellationToken = default) => UpdateSettingsAsync(settings =>
    {
        var valid = UiSettings.IsRefreshIntervalValid(TimeSpan.FromMinutes(value));
        return (valid ? settings with { RefreshMinutes = value } : settings, valid, valid);
    }, () => Settings.TrySetRefreshMinutes(value), cancellationToken);
    public Task SetResidentModeAsync(bool value, CancellationToken cancellationToken = default) => UpdateSettingsAsync(settings => (settings with { ResidentMode = value }, true, true), () =>
    {
        Settings.ResidentMode = value;
        OnPropertyChanged(nameof(Settings));
    }, cancellationToken);
    public void RestoreResidentMode(bool value) { Settings.ResidentMode = value; OnPropertyChanged(nameof(Settings)); }
    public void Navigate(AppPage page) => CurrentPage = page;

    private void NotifyLocalizedProperties()
    {
        foreach (var name in new[] { nameof(CopyText), nameof(NavDashboardText), nameof(NavUsageText), nameof(NavSettingsText), nameof(UsageStatusText), nameof(RefreshText), nameof(HeaderTitle), nameof(SubtitleText), nameof(UsageConnectionStatusText), nameof(UsageConnectionErrorText), nameof(UsageCredentialStatusText), nameof(UsageFailureText), nameof(UsageFreshnessText), nameof(IsUsageFreshnessVisible), nameof(UsageDelayedText), nameof(IsUsageDelayed), nameof(TokScaleFlowDescriptionText), nameof(TokScaleFlowStatusText), nameof(ProviderChoices), nameof(TokScaleTitleText), nameof(TokScaleBadgeText), nameof(TokScaleEmptyTitleText), nameof(TokScaleEmptyBodyText), nameof(TokScaleOpenSettingsText), nameof(TokScaleStateText), nameof(TokScaleFailureText), nameof(BreakdownNoteText), nameof(BreakdownPartialText), nameof(BreakdownUnclassifiedText), nameof(BreakdownOverAttributionText), nameof(BreakdownFailureText), nameof(UsageBreakdownStateText), nameof(BreakdownDimensionChoices), nameof(NotificationBannerText) }) OnPropertyChanged(name);
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnPropertyChanged(name); if (name == nameof(CurrentPage)) { OnPropertyChanged(nameof(IsDashboardVisible)); OnPropertyChanged(nameof(IsUsageVisible)); OnPropertyChanged(nameof(IsSettingsVisible)); OnPropertyChanged(nameof(HeaderTitle)); } return true; }
    private void OnPropertyChanged(string? name)
    {
        if (IsDisposed) return;
        PropertyChanged?.Invoke(this, new(name));
    }
}

public static class UiSettings
{
    public static IReadOnlyList<string> SupportedLanguages { get; } = ["English", "日本語"];
    public static IReadOnlyList<ProviderChoiceViewModel> ProviderChoices(UiLanguage language) =>
        // The picker offers the catalog only: a provider card is a user selection, and a
        // provider the user never picked never appears on the dashboard.
        TokScaleProviderCatalog.Providers
            .OrderBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(provider => new ProviderChoiceViewModel(provider.Id, provider.DisplayName))
            .ToList();
    public static IReadOnlyList<LocalizedChoice<ThemeMode>> ThemeChoices(UiLanguage language) => Enum.GetValues<ThemeMode>().Select(theme => new LocalizedChoice<ThemeMode>(theme, theme switch { ThemeMode.System => language == UiLanguage.Japanese ? "システム" : "System", ThemeMode.Light => language == UiLanguage.Japanese ? "ライト" : "Light", _ => language == UiLanguage.Japanese ? "ダーク" : "Dark" })).ToList();
    public static bool IsRefreshIntervalValid(TimeSpan interval) => interval >= TimeSpan.FromMinutes(5) && interval <= TimeSpan.FromMinutes(15);
    public static ThemeVariant? AppliedTheme { get; private set; }
    public static void ApplyTheme(ThemeMode mode)
    {
        AppliedTheme = mode switch { ThemeMode.Light => ThemeVariant.Light, ThemeMode.Dark => ThemeVariant.Dark, _ => ThemeVariant.Default };
        try
        {
            if (Avalonia.Application.Current is not null && Dispatcher.UIThread.CheckAccess())
                Avalonia.Application.Current.RequestedThemeVariant = AppliedTheme;
        }
        catch (InvalidOperationException)
        {
            // Headless callers may not own Avalonia's UI thread.
        }
    }
}

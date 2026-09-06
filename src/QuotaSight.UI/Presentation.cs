using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using QuotaSight.Core;
using QuotaSight.Application;
using QuotaSight.Infrastructure;

namespace QuotaSight.UI;

public enum AppPage { Dashboard, History, Settings }
public enum ThemeMode { System, Light, Dark }
public enum UiLanguage { English, Japanese }
public enum PresentationState { Ready, Loading, Error, Offline, Empty }

public sealed record QuotaRowViewModel(string WindowName, string Metric, double VisualPercent, string PercentText, string StatusText, string ResetText, string SourceBadge, string FreshnessText, bool IsStale, string ProgressLabel)
{
    public DateTimeOffset FetchedAt { get; init; }
    public DateTimeOffset WindowEnd { get; init; }
    public DateTimeOffset? FreshUntil { get; init; }
    public DateTimeOffset? ResetAt { get; init; }
}
public sealed record ProviderCardViewModel(ProviderKind Provider, string Name, string Account, string Accent, string StateText, bool IsDemo, IReadOnlyList<QuotaRowViewModel> Windows)
{
    public string AccessibleLabel => $"{Name}, {Account}. {StateText}";
}

public static class QuotaPresentationFormatter
{
    public static QuotaRowViewModel Format(QuotaSnapshot snapshot, DateTimeOffset now)
    {
        var percent = snapshot.EffectivePercent;
        var numeric = percent ?? 0m;
        var visual = (double)Math.Clamp(numeric, 0m, 100m);
        var percentText = percent is null ? "Usage unavailable" : $"{numeric:0.#}% used · {Math.Max(0m, 100m - numeric):0.#}% remaining";
        var status = numeric > 100m ? $"Over limit by {numeric - 100m:0.#}%" : percent is null ? "Waiting for quota data" : numeric >= 80m ? "Approaching limit" : "Within limit";
        var reset = snapshot.Window.ResetAt is { } at ? FormatReset(at, now) : "Reset time unavailable";
        var stale = snapshot.IsStale(now);
        var freshness = stale ? "Stale · last updated " + FormatAge(snapshot.Fetched, now) : "Updated " + FormatAge(snapshot.Fetched, now);
        return new QuotaRowViewModel(snapshot.Window.Kind.ToString(), snapshot.Metric, visual, percentText, status, reset, snapshot.Source.ToString(), freshness, stale, $"{percentText}. {status}. {reset}") { FetchedAt = snapshot.Fetched, WindowEnd = snapshot.Window.End, FreshUntil = snapshot.FreshUntil, ResetAt = snapshot.Window.ResetAt };
    }

    public static string FormatReset(DateTimeOffset resetAt, DateTimeOffset now)
    {
        var local = resetAt.ToLocalTime().ToString("ddd, MMM d · h:mm tt", CultureInfo.InvariantCulture);
        var delta = resetAt - now;
        var relative = delta.TotalSeconds <= 0 ? "ended" : delta.TotalDays >= 1 ? $"in {(int)delta.TotalDays}d {delta.Hours}h" : delta.TotalHours >= 1 ? $"in {(int)delta.TotalHours}h {delta.Minutes}m" : $"in {Math.Max(1, (int)delta.TotalMinutes)}m";
        return $"Resets {local} ({relative})";
    }

    private static string FormatAge(DateTimeOffset fetched, DateTimeOffset now)
    {
        var minutes = Math.Max(0, (int)(now - fetched).TotalMinutes);
        return minutes < 1 ? "just now" : minutes < 60 ? $"{minutes}m ago" : $"{minutes / 60}h ago";
    }
}

public interface IDashboardSource { bool IsDemo { get; } IReadOnlyList<ProviderCardViewModel> Load(); }
public sealed class DemoDashboardSource : IDashboardSource { public bool IsDemo => true; public IReadOnlyList<ProviderCardViewModel> Load() => DemoData.Create(DateTimeOffset.Now); }
public sealed class EmptyDashboardSource : IDashboardSource { public bool IsDemo => false; public IReadOnlyList<ProviderCardViewModel> Load() => []; }
public sealed class PersistentDashboardSource(IQuotaHistory history, TimeProvider? clock = null) : IDashboardSource
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    public bool IsDemo => false;
    public IReadOnlyList<ProviderCardViewModel> Load()
    {
        return [];
    }
    public async ValueTask<IReadOnlyList<ProviderCardViewModel>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var snapshots = new List<QuotaSnapshot>(); var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        for (var i = 0; i < 30; i++) { try { snapshots.AddRange(await history.ReadAsync(today.AddDays(-i), cancellationToken)); } catch (InvalidDataException) { break; } }
        return DashboardAggregation.ToCards(snapshots, clock.GetUtcNow());
    }
}
public static class DashboardAggregation
{
    public static IReadOnlyList<ProviderCardViewModel> ToCards(IEnumerable<QuotaSnapshot> snapshots, DateTimeOffset now)
    {
        return snapshots.GroupBy(s => (s.Provider, s.Account)).Select(group =>
        {
            var windows = group.GroupBy(s => (s.Window.Kind, s.Metric)).Select(w => w.OrderByDescending(s => s.Observed).First()).ToList();
            var representative = QuotaSnapshot.MostConstrained(windows);
            var card = new ProviderCardViewModel(group.Key.Provider, representative?.DisplayName.Length > 0 ? representative.DisplayName : group.Key.Provider.ToString(), group.Key.Account, "#405DE6", representative?.IsStale(now) == true ? "Stale · refresh failed" : "Connected", false,
                windows.OrderByDescending(s => s.EffectivePercent ?? decimal.MinValue).Select(s => QuotaPresentationFormatter.Format(s, now)).ToList());
            return (card, percent: representative?.EffectivePercent ?? decimal.MinValue);
        }).OrderByDescending(item => item.percent).Select(item => item.card).ToList();
    }
}

public static class DemoData
{
    public static List<ProviderCardViewModel> Create(DateTimeOffset now)
    {
        var cards = new[]
        {
            (ProviderKind.ChatGpt, "ChatGPT Plus", "Personal account", "#8B7CFF", 62m, QuotaWindowKind.Weekly, QuotaSource.Manual),
            (ProviderKind.Claude, "Claude Pro", "Personal account", "#E5A66A", 84m, QuotaWindowKind.Rolling, QuotaSource.Manual),
            (ProviderKind.OpenCode, "OpenCode Go", "Workspace · demo", "#5ED6C0", 112m, QuotaWindowKind.Monthly, QuotaSource.Official),
            (ProviderKind.Copilot, "GitHub Copilot Business", "Acme Engineering", "#78A9FF", 38m, QuotaWindowKind.Monthly, QuotaSource.Experimental)
        };
        return cards.Select((card, index) =>
        {
            var first = new QuotaSnapshot(card.Item1, card.Item3, index == 2 ? "Requests" : "Messages", new(card.Item6, now.AddDays(-3), now.AddDays(4), ResetAt: now.AddHours(index == 1 ? 7 : 42)), card.Item5, 100, null, "requests", now.AddMinutes(-index * 7), now.AddMinutes(-index * 7), card.Item7, card.Item7 == QuotaSource.Manual ? QuotaConfidence.Manual : QuotaConfidence.High, index == 1 ? now.AddHours(-1) : now.AddHours(12), card.Item2);
            var second = first with { Metric = "Fast window", Window = new(QuotaWindowKind.Daily, now.AddHours(-12), now.AddHours(12), ResetAt: now.AddHours(12)), Used = Math.Max(1, card.Item5 - 24), Fetched = now.AddMinutes(-15) };
            return new ProviderCardViewModel(card.Item1, card.Item2, card.Item3, card.Item4, index == 1 ? "Needs attention" : index == 2 ? "Over limit" : "Healthy", true, [QuotaPresentationFormatter.Format(first, now), QuotaPresentationFormatter.Format(second, now)]);
        }).ToList();
    }
}

public sealed class SettingsState
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public UiLanguage Language { get; set; } = UiLanguage.English;
    public int RefreshMinutes { get; private set; } = 10;
    public bool NotificationsEnabled { get; set; } = true;
    public decimal OverallThreshold { get; private set; } = 80;
    public Dictionary<ProviderKind, decimal> ProviderOverrides { get; } = [];
    public string GithubOAuthClientId { get; set; } = string.Empty;
    public bool ReduceMotionSupported => true;
    public bool HighContrastSupported => true;
    public bool TrySetRefreshMinutes(int value) => UiSettings.IsRefreshIntervalValid(TimeSpan.FromMinutes(value)) && (RefreshMinutes = value) > 0;
    public bool TrySetThreshold(decimal value, out string error)
    {
        if (value is < 0 or > 100) { error = "Threshold must be between 0 and 100."; return false; }
        OverallThreshold = value; error = string.Empty; return true;
    }
}

public sealed class ManualQuotaEntry
{
    public ProviderKind Provider { get; set; } = ProviderKind.Manual;
    public string AccountDisplayName { get; set; } = string.Empty;
    public QuotaWindowKind Window { get; set; } = QuotaWindowKind.Weekly;
    public string UsedPercentText { get; set; } = string.Empty;
    public DateTime ResetLocal { get; set; } = DateTime.Now.AddDays(7);
    public string ResetLocalText { get; set; } = string.Empty;
    public bool TryApply(out QuotaSnapshot? snapshot)
    {
        snapshot = null;
        if (Provider is ProviderKind.Manual or ProviderKind.OpenCode || string.IsNullOrWhiteSpace(AccountDisplayName) || !decimal.TryParse(UsedPercentText, CultureInfo.InvariantCulture, out var percent) || percent < 0) return false;
        var now = DateTimeOffset.Now;
        var resetText = ResetLocalText;
        DateTimeOffset? reset = string.IsNullOrWhiteSpace(resetText) ? null : DateTime.TryParse(resetText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var resetLocal) ? new DateTimeOffset(resetLocal) : null;
        if (!string.IsNullOrWhiteSpace(resetText) && reset is null) return false;
        snapshot = new(Provider, AccountDisplayName.Trim(), "Messages", new(Window, now, reset ?? DateTimeOffset.MaxValue, ResetAt: reset), percent, 100, null, "percent", now, now, QuotaSource.Manual, QuotaConfidence.Manual, reset);
        return true;
    }
}

public sealed record HistoryUiEntry(Guid Id, string Provider, string Account, string Window, decimal? UsedPercent, DateTimeOffset Observed, QuotaSource Source, QuotaConfidence Confidence);
public interface IHistoryUiService { IReadOnlyList<HistoryUiEntry> Read(); bool Delete(Guid id); void DeleteAll(); string ExportJson(); string ExportCsv(); }
public sealed class HistoryState : IHistoryUiService, INotifyPropertyChanged
{
    private readonly IQuotaHistory? persistentHistory;
    public ObservableCollection<HistoryUiEntry> Entries { get; } = [];
    public string? LoadError { get; private set; }
    private string accountFilter = "All accounts";
    private string windowFilter = "All windows";
    public string AccountFilter { get => accountFilter; set { if (accountFilter == value) return; accountFilter = value; PropertyChanged?.Invoke(this, new(nameof(AccountFilter))); PropertyChanged?.Invoke(this, new(nameof(FilteredEntries))); PropertyChanged?.Invoke(this, new(nameof(SparklinePoints))); } }
    public string WindowFilter { get => windowFilter; set { if (windowFilter == value) return; windowFilter = value; PropertyChanged?.Invoke(this, new(nameof(WindowFilter))); PropertyChanged?.Invoke(this, new(nameof(FilteredEntries))); PropertyChanged?.Invoke(this, new(nameof(SparklinePoints))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<HistoryUiEntry> FilteredEntries => Entries.Where(e => (AccountFilter == "All accounts" || e.Account == AccountFilter) && (WindowFilter == "All windows" || e.Window == WindowFilter)).ToList();
    public IReadOnlyList<double> SparklinePoints => FilteredEntries.OrderBy(e => e.Observed).Where(e => e.UsedPercent is not null).Select(e => (double)e.UsedPercent!.Value).ToList();
    public HistoryState(IQuotaHistory? history = null)
    {
        persistentHistory = history;
        Entries.CollectionChanged += (_, _) => NotifyDerivedProperties();
        if (history is null) for (var i = 0; i < 30; i++) Entries.Add(new(Guid.NewGuid(), i % 2 == 0 ? "ChatGPT Plus" : "Claude Pro", i % 2 == 0 ? "Personal" : "Work", "Weekly", 35 + i % 18, DateTimeOffset.Now.AddDays(-29 + i), QuotaSource.Manual, QuotaConfidence.Manual));
    }
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Entries.Clear();
        LoadError = null;
        for (var i = 0; i < 30; i++)
        {
            var day = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-i));
            try
            {
                foreach (var item in await persistentHistory!.ReadEventsAsync(day, cancellationToken))
                {
                    var snapshot = item.Snapshot;
                    Entries.Add(new(item.EventId, snapshot.DisplayName.Length == 0 ? snapshot.Provider.ToString() : snapshot.DisplayName, snapshot.Account, snapshot.Window.Kind.ToString(), snapshot.EffectivePercent, snapshot.Observed, snapshot.Source, snapshot.Confidence));
                }
            }
            catch (InvalidDataException) { LoadError = "History data is damaged; showing available entries."; }
        }
    }
    public IReadOnlyList<HistoryUiEntry> Read() => Entries;
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) { var entry = Entries.FirstOrDefault(e => e.Id == id); if (entry is null) return false; if (persistentHistory is not null) await persistentHistory.DeleteEventAsync(id, cancellationToken); return Entries.Remove(entry); }
    public async Task DeleteAllAsync(CancellationToken cancellationToken = default) { if (persistentHistory is not null) await persistentHistory.DeleteAsync(null, cancellationToken); Entries.Clear(); }
    public bool Delete(Guid id) { var entry = Entries.FirstOrDefault(e => e.Id == id); return entry is not null && Entries.Remove(entry); }
    public void DeleteAll() => Entries.Clear();
    public async Task RefreshAfterPersistAsync(CancellationToken cancellationToken = default)
    {
        if (persistentHistory is not null) await InitializeAsync(cancellationToken);
    }
    private void NotifyDerivedProperties()
    {
        PropertyChanged?.Invoke(this, new(nameof(Entries)));
        PropertyChanged?.Invoke(this, new(nameof(FilteredEntries)));
        PropertyChanged?.Invoke(this, new(nameof(SparklinePoints)));
    }
    public string ExportJson() => "[" + string.Join(",", FilteredEntries.Select(e => $"{{\"provider\":\"{EscapeJson(e.Provider)}\",\"account\":\"{EscapeJson(e.Account)}\",\"window\":\"{EscapeJson(e.Window)}\",\"usedPercent\":{(e.UsedPercent is { } p ? p.ToString(CultureInfo.InvariantCulture) : "null")},\"observed\":\"{EscapeJson(e.Observed.ToString("O", CultureInfo.InvariantCulture))}\",\"source\":\"{e.Source}\",\"confidence\":\"{e.Confidence}\"}}")) + "]";
    public string ExportCsv() => "Provider,Account,Window,UsedPercent,Observed,Source,Confidence\n" + string.Join("\n", FilteredEntries.Select(e => $"{EscapeCsv(e.Provider)},{EscapeCsv(e.Account)},{EscapeCsv(e.Window)},{(e.UsedPercent?.ToString("0.##") ?? string.Empty)},{e.Observed:O},{e.Source},{e.Confidence}"));
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string EscapeJson(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default: if (character < ' ') builder.Append($"\\u{(int)character:x4}"); else builder.Append(character); break;
            }
        }
        return builder.ToString();
    }
    private static string EscapeCsv(string value) => value.Any(character => character is ',' or '"' or '\r' or '\n') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}

public interface IInAppNotificationService { string BannerText { get; } void Notify(string title, string reason); }
public sealed class InAppNotificationService : IInAppNotificationService, INotificationSink, INotifyPropertyChanged
{
    public string BannerText { get; private set; } = string.Empty;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Notify(string title, string reason) { BannerText = $"{title}: {reason}"; PropertyChanged?.Invoke(this, new(nameof(BannerText))); }
    public ValueTask NotifyAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken) { Notify($"{snapshot.Provider} quota threshold", $"{snapshot.EffectivePercent:0.#}% used"); return ValueTask.CompletedTask; }
}

public sealed record ProviderConnectionResult(bool Success, string Message);
public interface IGitHubClientFactory { GitHubDeviceFlowClient Create(string clientId); }
public sealed class GitHubClientFactory(HttpClient client) : IGitHubClientFactory
{
    public GitHubDeviceFlowClient Create(string clientId) => new(client, clientId);
}
public interface IProviderUiService
{
    ValueTask<ProviderConnectionResult> TestOpenCodeAsync(string sessionApiKey, CancellationToken cancellationToken);
    ValueTask<string> ProbeGitHubCliAsync(CancellationToken cancellationToken);
    ValueTask<FetchResult<DeviceAuthorizationStart>> StartGitHubDeviceFlowAsync(CancellationToken cancellationToken);
    ValueTask<FetchResult<string>> PollGitHubDeviceFlowAsync(DeviceAuthorizationStart authorization, CancellationToken cancellationToken);
}
public sealed class UiProviderFacade : IProviderUiService
{
    private readonly Func<string, IQuotaAdapter> openCodeAdapterFactory;
    private GitHubDeviceFlowClient? github;
    private readonly ICredentialStore? credentialStore;
    private readonly GhCliProbe ghProbe;
    private readonly IGitHubClientFactory? githubFactory;
    private readonly InMemoryCredentialStore sessionCredentials = new();
    private Func<IReadOnlyList<QuotaSnapshot>, ValueTask>? openCodeSuccess;
    public CredentialStoreAvailability CredentialAvailability => credentialStore?.Availability ?? CredentialStoreAvailability.Unavailable;
    public UiProviderFacade(Func<string, IQuotaAdapter>? openCodeAdapterFactory = null, GitHubDeviceFlowClient? github = null, ICredentialStore? credentialStore = null, GhCliProbe? ghProbe = null, IGitHubClientFactory? githubFactory = null, Func<IReadOnlyList<QuotaSnapshot>, ValueTask>? openCodeSuccess = null)
    {
        this.openCodeAdapterFactory = openCodeAdapterFactory ?? (key => new OpenCodeGoAdapter(new HttpClient(), key));
        this.github = github; this.credentialStore = credentialStore; this.ghProbe = ghProbe ?? new GhCliProbe(); this.githubFactory = githubFactory; this.openCodeSuccess = openCodeSuccess;
    }
    public void SetOpenCodeSuccessHandler(Func<IReadOnlyList<QuotaSnapshot>, ValueTask> handler) => openCodeSuccess = handler;
    public async ValueTask<string?> GetStoredOpenCodeKeyAsync(CancellationToken cancellationToken)
    {
        var value = credentialStore is null ? null : await credentialStore.GetAsync("OpenCode Go", cancellationToken);
        return value ?? await sessionCredentials.GetAsync("OpenCode Go", cancellationToken);
    }
    public async ValueTask<ProviderConnectionResult> TestOpenCodeAsync(string sessionApiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionApiKey)) return new(false, "Enter an API key.");
        var result = await openCodeAdapterFactory(sessionApiKey).FetchAsync("OpenCode Go", cancellationToken);
        if (!result.IsSuccess) return new(false, result.Status switch
        {
            FetchStatus.Unauthorized => "OpenCode rejected the API key (401).",
            FetchStatus.Forbidden => "OpenCode denied access (403).",
            FetchStatus.Unsupported => "OpenCode usage endpoint was not found (404).",
            FetchStatus.RateLimited => "OpenCode rate limit reached (429). Try again later.",
            _ => "OpenCode returned a server or malformed response."
        });
        if (credentialStore is not null)
        {
            try { await credentialStore.SetAsync("OpenCode Go", sessionApiKey, cancellationToken); }
            catch (Exception) { await sessionCredentials.SetAsync("OpenCode Go", sessionApiKey, cancellationToken); }
            if (credentialStore.Availability is CredentialStoreAvailability.Unavailable or CredentialStoreAvailability.Locked)
                await sessionCredentials.SetAsync("OpenCode Go", sessionApiKey, cancellationToken);
        }
        else await sessionCredentials.SetAsync("OpenCode Go", sessionApiKey, cancellationToken);
        if (openCodeSuccess is not null && result.Value is not null) await openCodeSuccess(result.Value);
        var name = result.Value?.FirstOrDefault()?.DisplayName;
        var storage = CredentialAvailability is CredentialStoreAvailability.SecureStore ? "secure store" : "session-only fallback";
        return new(true, $"Connected to {name ?? "OpenCode Go"}; credential stored in {storage}.");
    }
    public async ValueTask<string> ProbeGitHubCliAsync(CancellationToken cancellationToken)
    {
        var result = await ghProbe.ProbeAsync(cancellationToken);
        return result.Status switch { GhProbeStatus.AvailableAuthenticated => "Copilot: gh authenticated; quota unavailable.", GhProbeStatus.Unauthorized => "Copilot: Unauthorized.", GhProbeStatus.Missing => "Copilot: gh Missing.", GhProbeStatus.Timeout => "Copilot: gh Timeout.", _ => "Copilot: gh probe error." };
    }
    public void UpdateGitHubClientId(string clientId) { github = githubFactory?.Create(clientId) ?? github; }
    public ValueTask<FetchResult<DeviceAuthorizationStart>> StartGitHubDeviceFlowAsync(CancellationToken cancellationToken) => github is null ? ValueTask.FromResult<FetchResult<DeviceAuthorizationStart>>(new(FetchStatus.Unsupported, Error: "GitHub Client ID is not configured.")) : github.StartAsync(cancellationToken);
    public async ValueTask<FetchResult<string>> PollGitHubDeviceFlowAsync(DeviceAuthorizationStart authorization, CancellationToken cancellationToken)
    {
        if (github is null) return new(FetchStatus.Unsupported, Error: "GitHub Client ID is not configured.");
        var result = await github.PollAsync(authorization, cancellationToken);
        if (result.IsSuccess && result.Value is { } token && credentialStore is not null) await credentialStore.SetAsync("github", token, cancellationToken);
        return result;
    }
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IDashboardSource source;
    private readonly HashSet<string> openCodeAccounts = new(StringComparer.Ordinal);
    private AppPage currentPage = AppPage.Dashboard;
    private UiLanguage language = UiLanguage.English;
    private CredentialStoreAvailability credentialAvailability = CredentialStoreAvailability.Unavailable;
    private PresentationState presentationState = PresentationState.Ready;
    public ObservableCollection<ProviderCardViewModel> Cards { get; } = [];
    public SettingsState Settings { get; } = new();
    public HistoryState History { get; }

    public UiCopy CopyText => new(Language);
    public string NavDashboardText => CopyText.Dashboard;
    public string NavHistoryText => CopyText.History;
    public string NavSettingsText => CopyText.Settings;
    public string RefreshText => CopyText.Refresh;
    public string HeaderTitle => CurrentPage switch { AppPage.History => CopyText.History, AppPage.Settings => CopyText.Settings, _ => CopyText.Dashboard };
    public string SubtitleText => CopyText.Subtitle;
    public string EmptyStateText => CopyText.EmptyTitle;
    public string EmptyStateDescription => CopyText.EmptyDescription;
    public string HistoryDeleteText => CopyText.Delete;
    public string OpenCodeCredentialNotice => credentialAvailability is CredentialStoreAvailability.SecureStore ? CopyText.SecureCredential : CopyText.CredentialUnavailable(credentialAvailability.ToString());
    public void SetCredentialAvailability(CredentialStoreAvailability availability) { credentialAvailability = availability; OnPropertyChanged(nameof(OpenCodeCredentialNotice)); }
    public string PersistentStateForTests => Settings.GithubOAuthClientId;
    public string CopilotNotice => CopyText.CopilotDescription;
    public string CopilotDeviceResult { get; private set; } = "No device flow started.";
    public AppPage CurrentPage { get => currentPage; private set => Set(ref currentPage, value); }
    public UiLanguage Language { get => language; set { if (Set(ref language, value)) NotifyLocalizedProperties(); } }
    public string LanguageCode => Language == UiLanguage.Japanese ? "日本語" : "English";
    public PresentationState PresentationState { get => presentationState; set { if (Set(ref presentationState, value)) { OnPropertyChanged(nameof(IsLoading)); OnPropertyChanged(nameof(IsError)); OnPropertyChanged(nameof(IsOffline)); OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(IsNotificationVisible)); OnPropertyChanged(nameof(NotificationBannerText)); } } }
    public bool IsDashboardVisible => CurrentPage == AppPage.Dashboard;
    public bool IsHistoryVisible => CurrentPage == AppPage.History;
    public bool IsSettingsVisible => CurrentPage == AppPage.Settings;
    public bool IsLoading => PresentationState == PresentationState.Loading;
    public bool IsError => PresentationState == PresentationState.Error;
    public bool IsOffline => PresentationState == PresentationState.Offline;
    public bool IsEmpty => PresentationState == PresentationState.Empty || Cards.Count == 0;

    public bool IsDemo => source.IsDemo;
    public string DemoBanner => IsDemo ? CopyText.DemoBanner : string.Empty;
    public bool HasCards => Cards.Count > 0;
    public bool HasEmptyState => Cards.Count == 0;
    public event PropertyChangedEventHandler? PropertyChanged;
    private readonly IManualQuotaService? manualQuotaService;
    private readonly IQuotaHistory? quotaHistory;
    private readonly AppSettingsStore? settingsStore;
    private readonly TimeProvider timeProvider;
    private readonly IGitHubClientFactory? githubFactory;
    private readonly IQuotaApplication? quotaApplication;
    private readonly InAppNotificationService notificationService = new();
    private readonly NotificationDeduplicator notificationDeduplicator;
    private readonly List<QuotaSnapshot> lastKnownSnapshots = [];
    public MainViewModel(IDashboardSource source, IManualQuotaService? manualQuotaService = null, IQuotaHistory? quotaHistory = null, TimeProvider? timeProvider = null, AppSettingsStore? settingsStore = null, IGitHubClientFactory? githubFactory = null, IQuotaApplication? quotaApplication = null) { this.source = source; this.manualQuotaService = manualQuotaService; this.quotaHistory = quotaHistory; this.timeProvider = timeProvider ?? TimeProvider.System; this.settingsStore = settingsStore; this.githubFactory = githubFactory; this.quotaApplication = quotaApplication; notificationDeduplicator = new NotificationDeduplicator(notificationService); notificationService.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(NotificationBannerText)); OnPropertyChanged(nameof(IsNotificationVisible)); }; History = new HistoryState(quotaHistory); LoadCards(); }
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (settingsStore is not null) SetSettings(await settingsStore.LoadAsync(cancellationToken), persist: false);
        if (quotaHistory is null) return;

        await quotaHistory.PruneAsync(DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime).AddDays(-30), cancellationToken);
        await History.InitializeAsync(cancellationToken);
        if (History.LoadError is not null) notificationService.Notify("History", History.LoadError);

        var snapshots = new List<QuotaSnapshot>();
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        for (var dayOffset = 0; dayOffset < 30; dayOffset++)
        {
            try { snapshots.AddRange(await quotaHistory.ReadAsync(today.AddDays(-dayOffset), cancellationToken)); }
            catch (InvalidDataException) { }
        }

        if (snapshots.Count > 0) lastKnownSnapshots.Clear();
        if (snapshots.Count > 0) lastKnownSnapshots.AddRange(snapshots);
        ReplaceCards(snapshots.Count > 0 ? DashboardAggregation.ToCards(snapshots, timeProvider.GetUtcNow()) : source.Load());
    }
    public string NotificationBannerText => string.IsNullOrWhiteSpace(notificationService.BannerText) && IsError ? CopyText.RefreshError : notificationService.BannerText;
    public bool IsNotificationVisible => !string.IsNullOrWhiteSpace(notificationService.BannerText) || IsError;
    public void Notify(string title, string reason) => notificationService.Notify(title, reason);
    public void SetSettings(AppSettingsDto dto, bool persist = true) { Settings.TrySetRefreshMinutes(dto.RefreshMinutes); Settings.TrySetThreshold(dto.OverallThreshold, out _); Settings.NotificationsEnabled = dto.NotificationsEnabled; Settings.ProviderOverrides.Clear(); if (dto.ProviderThresholds is not null) foreach (var pair in dto.ProviderThresholds) if (Enum.TryParse<ProviderKind>(pair.Key, true, out var provider)) Settings.ProviderOverrides[provider] = pair.Value; Settings.GithubOAuthClientId = dto.GithubOAuthClientId; Language = dto.Language == "Japanese" ? UiLanguage.Japanese : UiLanguage.English; UiSettings.ApplyTheme(Enum.TryParse<ThemeMode>(dto.Theme, true, out var theme) ? theme : ThemeMode.System); githubFactory?.Create(dto.GithubOAuthClientId); if (persist) settingsStore?.Save(CurrentSettings()); }
    private AppSettingsDto CurrentSettings() => new(Settings.Theme.ToString(), Language == UiLanguage.Japanese ? "Japanese" : "English", Settings.RefreshMinutes, Settings.OverallThreshold, Settings.NotificationsEnabled, Settings.GithubOAuthClientId, Settings.ProviderOverrides.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value));
    public async Task SetThemeAsync(ThemeMode value, CancellationToken cancellationToken = default) { Settings.Theme = value; UiSettings.ApplyTheme(value); if (settingsStore is not null) await settingsStore.SaveAsync(CurrentSettings(), cancellationToken); }
    public async Task SetLanguageAsync(UiLanguage value, CancellationToken cancellationToken = default) { Language = value; if (settingsStore is not null) await settingsStore.SaveAsync(CurrentSettings(), cancellationToken); }
    public async Task<bool> SetRefreshMinutesAsync(int value, CancellationToken cancellationToken = default) { if (!Settings.TrySetRefreshMinutes(value)) return false; if (settingsStore is not null) await settingsStore.SaveAsync(CurrentSettings(), cancellationToken); return true; }
    public async Task SetNotificationsAsync(bool value, CancellationToken cancellationToken = default) { Settings.NotificationsEnabled = value; if (settingsStore is not null) await settingsStore.SaveAsync(CurrentSettings(), cancellationToken); }
    public async Task<bool> SetThresholdAsync(decimal value, CancellationToken cancellationToken = default) { if (!Settings.TrySetThreshold(value, out _)) return false; if (settingsStore is not null) await settingsStore.SaveAsync(CurrentSettings(), cancellationToken); return true; }
    public async Task SetGithubClientIdAsync(string value, CancellationToken cancellationToken = default) { Settings.GithubOAuthClientId = value.Trim(); githubFactory?.Create(Settings.GithubOAuthClientId); if (settingsStore is not null) await settingsStore.SaveAsync(CurrentSettings(), cancellationToken); }
    public void Navigate(AppPage page) => CurrentPage = page;
    public void Refresh() => _ = RefreshAsync();
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        PresentationState = PresentationState.Loading;
        try
        {
            if (quotaApplication is null) { LoadCards(); PresentationState = Cards.Count == 0 ? PresentationState.Empty : PresentationState.Ready; return; }
            var snapshots = await quotaApplication.RefreshAsync(cancellationToken);
            if (snapshots.Count > 0)
            {
                lastKnownSnapshots.Clear();
                lastKnownSnapshots.AddRange(snapshots);
                if (Settings.NotificationsEnabled) foreach (var snapshot in snapshots) await notificationDeduplicator.ConsiderAsync(snapshot, Settings.ProviderOverrides.GetValueOrDefault(snapshot.Provider, Settings.OverallThreshold), timeProvider.GetUtcNow(), cancellationToken);
                ReplaceCards(Cards.Where(card => card.Provider != ProviderKind.OpenCode).Concat(DashboardAggregation.ToCards(snapshots, timeProvider.GetUtcNow())).ToList());
                if (quotaHistory is not null) await History.RefreshAfterPersistAsync(cancellationToken);
            }
            else ReevaluateCards(timeProvider.GetUtcNow());
            PresentationState = snapshots.Count == 0 && Cards.Count > 0 ? PresentationState.Error : Cards.Count == 0 ? PresentationState.Empty : PresentationState.Ready;
        }
        catch { ReevaluateCards(timeProvider.GetUtcNow()); PresentationState = PresentationState.Error; }
    }
    public async ValueTask ApplyProviderSnapshotsAsync(IReadOnlyList<QuotaSnapshot> snapshots, CancellationToken cancellationToken = default)
    {
        if (snapshots.Count == 0) return;
        if (quotaHistory is not null) await quotaHistory.AppendAsync(snapshots, cancellationToken);
        await History.RefreshAfterPersistAsync(cancellationToken);
        lastKnownSnapshots.RemoveAll(existing => snapshots.Any(updated => updated.Provider == existing.Provider && updated.Account == existing.Account && updated.Window.Kind == existing.Window.Kind && updated.Metric == existing.Metric));
        lastKnownSnapshots.AddRange(snapshots);
        if (Settings.NotificationsEnabled) foreach (var snapshot in snapshots) await notificationDeduplicator.ConsiderAsync(snapshot, Settings.ProviderOverrides.GetValueOrDefault(snapshot.Provider, Settings.OverallThreshold), timeProvider.GetUtcNow(), cancellationToken);
        ReplaceCards(Cards.Where(card => card.Provider != ProviderKind.OpenCode).Concat(DashboardAggregation.ToCards(snapshots, timeProvider.GetUtcNow())).ToList());
        PresentationState = PresentationState.Ready;
    }
    public string Copy(string key) => Language == UiLanguage.Japanese ? key switch { "Dashboard" => "ダッシュボード", "History" => "履歴", "Settings" => "設定", _ => key } : key;
    public bool TryAddAccount(ProviderKind provider, string account)
    {
        if (provider != ProviderKind.OpenCode) return true;
        if (openCodeAccounts.Count > 0) return false;
        openCodeAccounts.Add(account.Trim());
        return true;
    }
    public async Task ApplyManualSnapshotAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (manualQuotaService is not null) await manualQuotaService.SetAsync(snapshot, cancellationToken);
        if (manualQuotaService is not null) await History.RefreshAfterPersistAsync(cancellationToken);
        lastKnownSnapshots.RemoveAll(existing => existing.Provider == snapshot.Provider && existing.Account == snapshot.Account && existing.Window.Kind == snapshot.Window.Kind && existing.Metric == snapshot.Metric);
        lastKnownSnapshots.Add(snapshot);
        ApplyManualSnapshotUi(snapshot);
    }
    private void ApplyManualSnapshotUi(QuotaSnapshot snapshot)
    {
        var row = QuotaPresentationFormatter.Format(snapshot, DateTimeOffset.Now);
        var existing = Cards.FirstOrDefault(card => card.Provider == snapshot.Provider && card.Account == snapshot.Account);
        if (existing is not null)
        {
            var windows = existing.Windows
                .Where(item => item.WindowName != row.WindowName || item.Metric != row.Metric)
                .Append(row)
                .OrderByDescending(item => item.VisualPercent)
                .ToList();
            Cards[Cards.IndexOf(existing)] = existing with { Windows = windows };
        }
        else
        {
            Cards.Add(new ProviderCardViewModel(snapshot.Provider, snapshot.DisplayName.Length == 0 ? snapshot.Provider.ToString() : snapshot.DisplayName, snapshot.Account, "#405DE6", "Manual", false, [row]));
        }
        OnPropertyChanged(nameof(HasCards)); OnPropertyChanged(nameof(HasEmptyState)); OnPropertyChanged(nameof(IsEmpty));
    }
    public void SetCopilotDeviceResult(string url, string code) { CopilotDeviceResult = Language == UiLanguage.Japanese ? $"認証URL: {url}\nユーザーコード: {code}\n操作: コピー · 開く · 確認" : $"Verification URL: {url}\nUser code: {code}\nActions: Copy · Open · Poll"; OnPropertyChanged(nameof(CopilotDeviceResult)); }
    private void LoadCards()
    {
        Cards.Clear();
        foreach (var card in source.Load()) Cards.Add(card);
        if (Cards.Count == 0 && quotaHistory is not null)
        {
            // Persistent sources are loaded by InitializeAsync without blocking the UI thread.
        }
        OnPropertyChanged(nameof(HasCards)); OnPropertyChanged(nameof(HasEmptyState)); OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(EmptyStateText));
    }
    private void ReplaceCards(IEnumerable<ProviderCardViewModel> cards) { var replacement = cards.ToList(); Cards.Clear(); foreach (var card in replacement) Cards.Add(card); OnPropertyChanged(nameof(HasCards)); OnPropertyChanged(nameof(HasEmptyState)); OnPropertyChanged(nameof(IsEmpty)); }
    private void ReevaluateCards(DateTimeOffset now)
    {
        for (var cardIndex = 0; cardIndex < Cards.Count; cardIndex++)
        {
            var card = Cards[cardIndex];
            Cards[cardIndex] = card with
            {
                Windows = card.Windows.Select(row =>
                {
                    var stale = row.WindowEnd != default && row.WindowEnd <= now || row.FreshUntil is { } expiry && now > expiry;
                    var freshness = stale ? "Stale · last updated " + FormatAge(row.FetchedAt, now) : "Updated " + FormatAge(row.FetchedAt, now);
                    var reset = row.ResetAt is { } resetAt ? QuotaPresentationFormatter.FormatReset(resetAt, now) : row.ResetText;
                    return row with { IsStale = stale, FreshnessText = freshness, ResetText = reset, ProgressLabel = $"{row.PercentText}. {row.StatusText}. {reset}" };
                }).ToList()
            };
        }
    }
    private static string FormatAge(DateTimeOffset fetched, DateTimeOffset now)
    {
        var minutes = Math.Max(0, (int)(now - fetched).TotalMinutes);
        return minutes < 1 ? "just now" : minutes < 60 ? $"{minutes}m ago" : $"{minutes / 60}h ago";
    }
    private void NotifyLocalizedProperties()
    {
        foreach (var name in new[] { nameof(CopyText), nameof(NavDashboardText), nameof(NavHistoryText), nameof(NavSettingsText), nameof(RefreshText), nameof(HeaderTitle), nameof(SubtitleText), nameof(EmptyStateText), nameof(EmptyStateDescription), nameof(HistoryDeleteText), nameof(DemoBanner), nameof(NotificationBannerText), nameof(OpenCodeCredentialNotice), nameof(CopilotNotice), nameof(CopilotDeviceResult) }) OnPropertyChanged(name);
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnPropertyChanged(name); if (name == nameof(CurrentPage)) { OnPropertyChanged(nameof(IsDashboardVisible)); OnPropertyChanged(nameof(IsHistoryVisible)); OnPropertyChanged(nameof(IsSettingsVisible)); } return true; }
    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new(name));
}

public static class UiSettings
{
    public static IReadOnlyList<string> SupportedLanguages { get; } = ["English", "日本語"];
    public static string AutostartStatus => "Unsupported · platform backend not installed";
    public static bool IsRefreshIntervalValid(TimeSpan interval) => interval >= TimeSpan.FromMinutes(5) && interval <= TimeSpan.FromMinutes(15);
    public static ThemeVariant? AppliedTheme { get; private set; }
    public static void ApplyTheme(ThemeMode mode)
    {
        AppliedTheme = mode switch { ThemeMode.Light => ThemeVariant.Light, ThemeMode.Dark => ThemeVariant.Dark, _ => ThemeVariant.Default };
        try
        {
            if (Avalonia.Application.Current is not null && Dispatcher.UIThread.CheckAccess())
            {
                Avalonia.Application.Current.RequestedThemeVariant = AppliedTheme;
                var dark = mode == ThemeMode.Dark;
                SetBrush("SidebarBrush", dark ? "#151A24" : "#F1F3F6");
                SetBrush("CardBrush", dark ? "#202735" : "#FFFFFF");
                SetBrush("BannerBrush", dark ? "#242B48" : "#E8EEFF");
                SetBrush("BorderBrush", dark ? "#3A465A" : "#D8DEE8");
                SetBrush("TextPrimaryBrush", dark ? "#F4F7FB" : "#172033");
                SetBrush("TextMutedBrush", dark ? "#AAB5C4" : "#5E6A7E");
                SetBrush("AccentBrush", dark ? "#8FA2FF" : "#405DE6");
                SetBrush("WarningBrush", dark ? "#F4B46A" : "#B76B16");
            }
        }
        catch (InvalidOperationException)
        {
            // Headless callers may not own Avalonia's UI thread.
        }
    }
    private static void SetBrush(string key, string color)
    {
        try
        {
            if (Avalonia.Application.Current?.Resources is not { } resources) return;
            resources[key] = new SolidColorBrush(Color.Parse(color));
        }
        catch (InvalidOperationException)
        {
            // Theme resources are best-effort when a headless caller has no UI dispatcher.
        }
    }
}

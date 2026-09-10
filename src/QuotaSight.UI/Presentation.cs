using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
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
public enum CodexAuthorizationState { Disconnected, AwaitingAuthorization, Pending, Connected, Error }
public enum ProviderConnectionChoice { ChatGpt, Claude, Codex, OpenCode, Copilot }
public enum CopilotUiState { Idle, Started, Completed, Failed, GhProbe }
public enum UsageBand { Normal, Attention, Danger, OverLimit }
public sealed record LocalizedChoice<T>(T Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}
public sealed record ProviderChoiceViewModel(ProviderConnectionChoice Choice, string DisplayName)
{
    public override string ToString() => DisplayName;
}
public sealed record CodexAuthorizationPrompt(string UserCode, Uri VerificationUri, DateTimeOffset ExpiresAt);
public sealed record CodexUiResult(bool Success, CodexAuthorizationState State, string Message, CodexAuthorizationPrompt? Prompt = null, TimeSpan? RetryAfter = null, FetchStatus Status = FetchStatus.Success);

public sealed record QuotaRowViewModel(string WindowName, string Metric, double VisualPercent, string PercentText, string StatusText, string ResetText, string SourceBadge, string FreshnessText, bool IsStale, string ProgressLabel)
{
    public DateTimeOffset FetchedAt { get; init; }
    public DateTimeOffset WindowEnd { get; init; }
    public DateTimeOffset? FreshUntil { get; init; }
    public DateTimeOffset? ResetAt { get; init; }
    public decimal? UsedPercent { get; init; }
    public QuotaSource Source { get; init; }
    public QuotaWindowKind WindowKind { get; init; }
    public UsageBand Band { get; init; }
    public bool IsAttention => Band == UsageBand.Attention;
    public bool IsDanger => Band == UsageBand.Danger;
    public bool IsOverLimit => Band == UsageBand.OverLimit;
    public QuotaSnapshot? Snapshot { get; init; }
}
public sealed record ProviderCardViewModel(ProviderKind Provider, string Name, string Account, string Accent, string StateText, bool IsDemo, IReadOnlyList<QuotaRowViewModel> Windows)
{
    public string AccessibleLabel => $"{Name}, {Account}. {StateText}";
}

public static class QuotaPresentationFormatter
{
    public static QuotaRowViewModel Format(QuotaSnapshot snapshot, DateTimeOffset now, UiLanguage language = UiLanguage.English)
    {
        var percent = snapshot.EffectivePercent;
        var numeric = percent ?? 0m;
        var visual = (double)Math.Clamp(numeric, 0m, 100m);
        var band = percent is null ? UsageBand.Attention : numeric > 100m ? UsageBand.OverLimit : numeric >= 80m ? UsageBand.Danger : numeric >= 60m ? UsageBand.Attention : UsageBand.Normal;
        var japanese = language == UiLanguage.Japanese;
        var percentText = percent is null ? (japanese ? "使用量を表示できません" : "Usage unavailable") : japanese ? numeric > 100m ? $"使用済み {numeric:0.#}%" : $"使用済み {numeric:0.#}%・残り {100m - numeric:0.#}%" : $"{numeric:0.#}% used · {Math.Max(0m, 100m - numeric):0.#}% remaining";
        var status = percent is null ? (japanese ? "未取得" : "Waiting for quota data · value: —") : japanese ? band switch { UsageBand.OverLimit => $"上限を{numeric - 100m:0.#}%超過", UsageBand.Danger => "上限間近", UsageBand.Attention => "注意", _ => "余裕あり" } : band switch { UsageBand.OverLimit => $"Over limit by {numeric - 100m:0.#}%", UsageBand.Danger => $"Danger · {numeric:0.#}%", UsageBand.Attention => $"Attention · {numeric:0.#}%", _ => $"Normal · {numeric:0.#}%" };
        var reset = snapshot.Window.ResetAt is { } at ? FormatReset(at, now, language) : japanese ? "リセット時刻は不明" : "Reset time unavailable";
        var stale = snapshot.IsStale(now);
        var freshness = stale ? (japanese ? $"データが古い可能性があります（最終更新: {FormatAge(snapshot.Fetched, now, language)}）" : "Stale · last updated " + FormatAge(snapshot.Fetched, now, language)) : (japanese ? $"{FormatAge(snapshot.Fetched, now, language)}に更新" : "Updated " + FormatAge(snapshot.Fetched, now, language));
        var source = japanese ? snapshot.Source switch { QuotaSource.Manual => "手動", QuotaSource.Experimental => "実験的", QuotaSource.Delayed => "遅延", _ => "公式" } : snapshot.Source.ToString();
        return new QuotaRowViewModel(japanese ? LocalizeWindow(snapshot.Window.Kind) : snapshot.Window.Kind.ToString(), LocalizeMetric(snapshot.Metric, snapshot.Window.Kind, language), visual, percentText, status, reset, source, freshness, stale, BuildProgressLabel(percentText, status, reset, language)) { FetchedAt = snapshot.Fetched, WindowEnd = snapshot.Window.End, FreshUntil = snapshot.FreshUntil, ResetAt = snapshot.Window.ResetAt, UsedPercent = percent, Source = snapshot.Source, WindowKind = snapshot.Window.Kind, Band = band, Snapshot = snapshot };
    }

    public static QuotaRowViewModel Relocalize(QuotaRowViewModel row, DateTimeOffset now, UiLanguage language) => row.Snapshot is { } snapshot ? Format(snapshot, now, language) : row;

    public static string FormatReset(DateTimeOffset resetAt, DateTimeOffset now, UiLanguage language = UiLanguage.English)
    {
        var delta = resetAt - now;
        if (language == UiLanguage.Japanese)
        {
            var local = resetAt.ToLocalTime().ToString("M月d日(ddd) HH:mm", CultureInfo.GetCultureInfo("ja-JP"));
            var relative = FormatJapaneseRelative(delta);
            return $"{local}にリセット（{relative}）";
        }

        var englishLocal = resetAt.ToLocalTime().ToString("ddd, MMM d · h:mm tt", CultureInfo.InvariantCulture);
        var englishRelative = delta.TotalSeconds <= 0 ? "ended" : delta.TotalDays >= 1 ? $"in {(int)delta.TotalDays}d {delta.Hours}h" : delta.TotalHours >= 1 ? $"in {(int)delta.TotalHours}h {delta.Minutes}m" : $"in {Math.Max(1, (int)delta.TotalMinutes)}m";
        return $"Resets {englishLocal} ({englishRelative})";
    }

    private static string FormatAge(DateTimeOffset fetched, DateTimeOffset now, UiLanguage language)
    {
        var minutes = Math.Max(0, (int)(now - fetched).TotalMinutes);
        return language == UiLanguage.Japanese ? minutes < 1 ? "たった今" : minutes < 60 ? $"{minutes}分前" : $"{minutes / 60}時間前" : minutes < 1 ? "just now" : minutes < 60 ? $"{minutes}m ago" : $"{minutes / 60}h ago";
    }
    private static string FormatJapaneseRelative(TimeSpan delta) => delta.TotalSeconds <= 0 ? "リセット済み" : delta.TotalDays >= 1 ? $"あと{(int)delta.TotalDays}日{(delta.Hours > 0 ? $"{delta.Hours}時間" : string.Empty)}" : delta.TotalHours >= 1 ? $"あと{(int)delta.TotalHours}時間{(delta.Minutes > 0 ? $"{delta.Minutes}分" : string.Empty)}" : $"あと{Math.Max(1, (int)delta.TotalMinutes)}分";
    public static string LocalizeWindow(QuotaWindowKind kind) => kind switch { QuotaWindowKind.Daily => "日次", QuotaWindowKind.Weekly => "週次", QuotaWindowKind.Monthly => "月次", QuotaWindowKind.Rolling => "ローリング枠", _ => "カスタム" };
    private static string LocalizeMetric(string metric, QuotaWindowKind window, UiLanguage language)
    {
        if (language != UiLanguage.Japanese) return metric;
        if (string.Equals(metric, window.ToString(), StringComparison.OrdinalIgnoreCase) ||
            (window == QuotaWindowKind.Monthly && metric.Equals("monthly", StringComparison.OrdinalIgnoreCase)) ||
            (window == QuotaWindowKind.Weekly && metric.Equals("weekly", StringComparison.OrdinalIgnoreCase)) ||
            (window == QuotaWindowKind.Daily && metric.Equals("daily", StringComparison.OrdinalIgnoreCase)) ||
            (window == QuotaWindowKind.Rolling && metric.Equals("rolling", StringComparison.OrdinalIgnoreCase))) return string.Empty;
        return metric switch { "Messages" => "メッセージ", "Requests" => "リクエスト", "Fast window" => "短時間枠", "monthly" => "月次", "weekly" => "週次", "Codex primary" => "Codex主要枠", "Codex secondary" => "Codex副枠", _ => metric };
    }
    private static string BuildProgressLabel(string percentText, string status, string reset, UiLanguage language) => language == UiLanguage.Japanese ? $"{percentText}。{status}。{reset}" : $"{percentText}. {status}. {reset}";
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
        return DashboardAggregation.ToCards(snapshots, clock.GetUtcNow(), UiLanguage.English);
    }
}
public static class DashboardAggregation
{
    public static IReadOnlyList<ProviderCardViewModel> ToCards(IEnumerable<QuotaSnapshot> snapshots, DateTimeOffset now, UiLanguage language = UiLanguage.English)
    {
        return snapshots.GroupBy(s => (s.Provider, s.Account)).Select(group =>
        {
            var windows = group.GroupBy(s => (s.Window.Kind, s.Metric)).Select(w => w.OrderByDescending(s => s.Observed).First()).ToList();
            var representative = QuotaSnapshot.MostConstrained(windows);
            var japanese = language == UiLanguage.Japanese;
            var name = representative?.DisplayName.Length > 0 ? representative.DisplayName : group.Key.Provider.ToString();
            if (group.Key.Provider == ProviderKind.ChatGpt && representative?.Source != QuotaSource.Manual)
                name = new UiCopy(language).ProviderName(group.Key.Provider);
            var card = new ProviderCardViewModel(group.Key.Provider, name, group.Key.Account, "#405DE6", representative?.IsStale(now) == true ? (japanese ? "更新できませんでした" : "Stale · refresh failed") : (japanese ? "接続済み" : "Connected"), false,
                windows.OrderByDescending(s => s.EffectivePercent ?? decimal.MinValue).Select(s => QuotaPresentationFormatter.Format(s, now, language)).ToList());
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
    public bool ResidentMode { get; set; }
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

public sealed record HistoryUiEntry(Guid Id, string Provider, string Account, string Window, decimal? UsedPercent, DateTimeOffset Observed, QuotaSource Source, QuotaConfidence Confidence)
{
    public string WindowDisplay { get; init; } = Window;
}
public interface IHistoryUiService { IReadOnlyList<HistoryUiEntry> Read(); bool Delete(Guid id); void DeleteAll(); string ExportJson(); string ExportCsv(); }
public sealed class HistoryState : IHistoryUiService, INotifyPropertyChanged
{
    private readonly IQuotaHistory? persistentHistory;
    public ObservableCollection<HistoryUiEntry> Entries { get; } = [];
    public string? LoadError { get; private set; }
    private string accountFilter = "All accounts";
    private string windowFilter = "All windows";
    public UiLanguage Language { get; private set; } = UiLanguage.English;
    public string AccountFilter { get => accountFilter; set { if (accountFilter == value) return; accountFilter = value; PropertyChanged?.Invoke(this, new(nameof(AccountFilter))); PropertyChanged?.Invoke(this, new(nameof(FilteredEntries))); PropertyChanged?.Invoke(this, new(nameof(SparklinePoints))); } }
    public string WindowFilter { get => windowFilter; set { if (windowFilter == value) return; windowFilter = value; PropertyChanged?.Invoke(this, new(nameof(WindowFilter))); PropertyChanged?.Invoke(this, new(nameof(FilteredEntries))); PropertyChanged?.Invoke(this, new(nameof(SparklinePoints))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<HistoryUiEntry> FilteredEntries => Entries.Where(e => (AccountFilter == "All accounts" || e.Account == AccountFilter) && (WindowFilter == "All windows" || e.Window == WindowFilter)).ToList();
    public IReadOnlyList<double> SparklinePoints => FilteredEntries.OrderBy(e => e.Observed).Where(e => e.UsedPercent is not null).Select(e => (double)e.UsedPercent!.Value).ToList();
    public HistoryState(IQuotaHistory? history = null)
    {
        persistentHistory = history;
        Entries.CollectionChanged += (_, _) => NotifyDerivedProperties();
        if (history is null) for (var i = 0; i < 30; i++) Entries.Add(CreateEntry(new(Guid.NewGuid(), i % 2 == 0 ? "ChatGPT Plus" : "Claude Pro", i % 2 == 0 ? "Personal" : "Work", "Weekly", 35 + i % 18, DateTimeOffset.Now.AddDays(-29 + i), QuotaSource.Manual, QuotaConfidence.Manual)));
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
                    Entries.Add(CreateEntry(new(item.EventId, snapshot.DisplayName.Length == 0 ? snapshot.Provider.ToString() : snapshot.DisplayName, snapshot.Account, snapshot.Window.Kind.ToString(), snapshot.EffectivePercent, snapshot.Observed, snapshot.Source, snapshot.Confidence)));
                }
            }
            catch (InvalidDataException) { LoadError = "History data is damaged; showing available entries."; }
        }
    }
    public IReadOnlyList<HistoryUiEntry> Read() => Entries;
    public void SetLanguage(UiLanguage language)
    {
        Language = language;
        for (var index = 0; index < Entries.Count; index++) Entries[index] = CreateEntry(Entries[index]);
        PropertyChanged?.Invoke(this, new(nameof(Entries)));
        PropertyChanged?.Invoke(this, new(nameof(FilteredEntries)));
    }
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
    private HistoryUiEntry CreateEntry(HistoryUiEntry entry) => entry with { WindowDisplay = Language == UiLanguage.Japanese && Enum.TryParse<QuotaWindowKind>(entry.Window, true, out var kind) ? QuotaPresentationFormatter.LocalizeWindow(kind) : entry.Window };
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
    public UiLanguage Language { get; set; } = UiLanguage.English;
    public string BannerText { get; private set; } = string.Empty;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Notify(string title, string reason) { BannerText = $"{title}: {reason}"; PropertyChanged?.Invoke(this, new(nameof(BannerText))); }
    public void Clear() { if (BannerText.Length == 0) return; BannerText = string.Empty; PropertyChanged?.Invoke(this, new(nameof(BannerText))); }
    public ValueTask NotifyAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken) { if (snapshot.EffectivePercent is not { } percent) return ValueTask.CompletedTask; var copy = new UiCopy(Language); Notify(copy.QuotaThresholdTitle(snapshot.Provider), copy.QuotaThresholdReason(percent)); return ValueTask.CompletedTask; }
}

public sealed record ProviderConnectionResult(bool Success, string Message, FetchStatus Status = FetchStatus.Success);
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
    ValueTask<CodexUiResult> StartCodexAsync(CancellationToken cancellationToken);
    ValueTask<CodexUiResult> PollCodexAsync(CancellationToken cancellationToken);
    ValueTask<CodexUiResult> LogoutCodexAsync(CancellationToken cancellationToken);
    ValueTask<bool> HasCodexCredentialAsync(CancellationToken cancellationToken);
}
public sealed class UiProviderFacade : IProviderUiService
{
    private readonly Func<string, IQuotaAdapter> openCodeAdapterFactory;
    private GitHubDeviceFlowClient? github;
    private readonly ICredentialStore? credentialStore;
    private readonly GhCliProbe ghProbe;
    private readonly IGitHubClientFactory? githubFactory;
    private readonly CodexSessionManager? codexSessionManager;
    private readonly TimeProvider timeProvider;
    private CodexDeviceAuthorization? codexAuthorization;
    private DateTimeOffset nextCodexPollAt;
    private readonly InMemoryCredentialStore sessionCredentials = new();
    private Func<IReadOnlyList<QuotaSnapshot>, ValueTask>? openCodeSuccess;
    public CredentialStoreAvailability CredentialAvailability => credentialStore?.Availability ?? CredentialStoreAvailability.Unavailable;
    public UiProviderFacade(Func<string, IQuotaAdapter>? openCodeAdapterFactory = null, GitHubDeviceFlowClient? github = null, ICredentialStore? credentialStore = null, GhCliProbe? ghProbe = null, IGitHubClientFactory? githubFactory = null, Func<IReadOnlyList<QuotaSnapshot>, ValueTask>? openCodeSuccess = null, CodexSessionManager? codexSessionManager = null, TimeProvider? timeProvider = null)
    {
        this.openCodeAdapterFactory = openCodeAdapterFactory ?? (key => new OpenCodeGoAdapter(new HttpClient(), key));
        this.github = github; this.credentialStore = credentialStore; this.ghProbe = ghProbe ?? new GhCliProbe(); this.githubFactory = githubFactory; this.openCodeSuccess = openCodeSuccess; this.codexSessionManager = codexSessionManager; this.timeProvider = timeProvider ?? TimeProvider.System;
    }
    public void SetOpenCodeSuccessHandler(Func<IReadOnlyList<QuotaSnapshot>, ValueTask> handler) => openCodeSuccess = handler;
    public async ValueTask<string?> GetStoredOpenCodeKeyAsync(CancellationToken cancellationToken)
    {
        var value = credentialStore is null ? null : await credentialStore.GetAsync("OpenCode Go", cancellationToken);
        return value ?? await sessionCredentials.GetAsync("OpenCode Go", cancellationToken);
    }
    public async ValueTask<ProviderConnectionResult> TestOpenCodeAsync(string sessionApiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionApiKey)) return new(false, "Enter an API key.", FetchStatus.Unauthorized);
        var result = await openCodeAdapterFactory(sessionApiKey).FetchAsync("OpenCode Go", cancellationToken);
        if (!result.IsSuccess) return result.Status switch
        {
            FetchStatus.Unauthorized => new(false, "OpenCode rejected the API key (401).", FetchStatus.Unauthorized),
            FetchStatus.Forbidden => new(false, "OpenCode denied access (403).", FetchStatus.Forbidden),
            FetchStatus.Unsupported => new(false, "OpenCode usage endpoint was not found (404).", FetchStatus.Unsupported),
            FetchStatus.RateLimited => new(false, "OpenCode rate limit reached (429). Try again later.", FetchStatus.RateLimited),
            _ => new(false, "OpenCode returned a server or malformed response.", FetchStatus.TransientFailure)
        };
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
        return new(true, $"Connected to {name ?? "OpenCode Go"}; credential stored in {storage}.", FetchStatus.Success);
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

    public async ValueTask<CodexUiResult> StartCodexAsync(CancellationToken cancellationToken)
    {
        if (codexSessionManager is null) return new(false, CodexAuthorizationState.Error, "Codex authorization is unavailable.", Status: FetchStatus.Unsupported);
        var result = await codexSessionManager.StartAsync(cancellationToken);
        if (!result.IsSuccess || result.Value is not { } authorization) return CodexFailure(result.Status, result.RetryAfter);
        codexAuthorization = authorization;
        nextCodexPollAt = timeProvider.GetUtcNow();
        return new(true, CodexAuthorizationState.AwaitingAuthorization, "Authorization started.", new(authorization.UserCode, authorization.VerificationUri, authorization.ExpiresAt));
    }

    public async ValueTask<CodexUiResult> PollCodexAsync(CancellationToken cancellationToken)
    {
        if (codexSessionManager is null) return new(false, CodexAuthorizationState.Error, "Codex authorization is unavailable.", Status: FetchStatus.Unsupported);
        if (codexAuthorization is not { } authorization) return new(false, CodexAuthorizationState.Disconnected, "Start Codex authorization first.", Status: FetchStatus.Unsupported);
        var now = timeProvider.GetUtcNow();
        if (now < nextCodexPollAt) return new(false, CodexAuthorizationState.Pending, "Still waiting for authorization. Try again shortly.", new(authorization.UserCode, authorization.VerificationUri, authorization.ExpiresAt), nextCodexPollAt - now, FetchStatus.RateLimited);
        var result = await codexSessionManager.PollAndStoreAsync(authorization, cancellationToken);
        if (result.IsSuccess)
        {
            codexAuthorization = null;
            return new(true, CodexAuthorizationState.Connected, "Codex connected.");
        }
        if (result.RetryAfter is { } retry)
        {
            nextCodexPollAt = timeProvider.GetUtcNow().Add(retry);
            return new(false, CodexAuthorizationState.Pending, "Still waiting for authorization. Try again shortly.", new(authorization.UserCode, authorization.VerificationUri, authorization.ExpiresAt), retry, FetchStatus.RateLimited);
        }
        return CodexFailure(result.Status, result.RetryAfter, authorization);
    }

    public async ValueTask<CodexUiResult> LogoutCodexAsync(CancellationToken cancellationToken)
    {
        codexAuthorization = null;
        if (codexSessionManager is null) return new(false, CodexAuthorizationState.Error, "Codex authorization is unavailable.", Status: FetchStatus.Unsupported);
        try
        {
            await codexSessionManager.LogoutAsync(cancellationToken);
            return new(true, CodexAuthorizationState.Disconnected, "Codex disconnected.");
        }
        catch { return new(false, CodexAuthorizationState.Error, "Unable to disconnect Codex.", Status: FetchStatus.TransientFailure); }
    }
    public ValueTask<bool> HasCodexCredentialAsync(CancellationToken cancellationToken) => codexSessionManager?.HasCredentialAsync(cancellationToken) ?? ValueTask.FromResult(false);

    private static CodexUiResult CodexFailure(FetchStatus status, TimeSpan? retryAfter, CodexDeviceAuthorization? authorization = null) =>
        new(false, CodexAuthorizationState.Error, status switch
        {
            FetchStatus.Unauthorized => "Codex authorization was rejected.",
            FetchStatus.Forbidden => "Codex authorization was not permitted.",
            FetchStatus.RateLimited => "Codex authorization is temporarily rate-limited.",
            _ => "Codex authorization is temporarily unavailable."
        }, authorization is null ? null : new(authorization.UserCode, authorization.VerificationUri, authorization.ExpiresAt), retryAfter, status);
}

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
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
    public string QuotaStatusText => IsQuotaDataBusy ? CopyText.QuotaRefreshing : string.Empty;
    public bool IsQuotaDataBusy => quotaDataBusy;
    public bool CanRefresh => !IsQuotaDataBusy;
    public string HeaderTitle => CurrentPage switch { AppPage.History => CopyText.History, AppPage.Settings => CopyText.Settings, _ => CopyText.Dashboard };
    public string SubtitleText => CopyText.Subtitle;
    public string EmptyStateText => CopyText.EmptyTitle;
    public string EmptyStateDescription => CopyText.EmptyDescription;
    public string HistoryDeleteText => CopyText.Delete;
    public string OpenCodeCredentialNotice => credentialAvailability is CredentialStoreAvailability.SecureStore ? CopyText.SecureCredential : CopyText.CredentialUnavailable(credentialAvailability.ToString());
    public void SetCredentialAvailability(CredentialStoreAvailability availability) { credentialAvailability = availability; OnPropertyChanged(nameof(OpenCodeCredentialNotice)); }
    public string PersistentStateForTests => Settings.GithubOAuthClientId;
    public AppSettingsDto CurrentSettingsForTests => CurrentSettings();
    public string CopilotNotice => CopyText.CopilotDescription;
    public IReadOnlyList<ProviderChoiceViewModel> ProviderChoices => UiSettings.ProviderChoices(Language);
    private CopilotUiState copilotState = CopilotUiState.Idle;
    private string copilotVerificationUrl = string.Empty;
    private string copilotUserCode = string.Empty;
    public string CopilotDeviceResult => copilotState == CopilotUiState.Started
        ? Language == UiLanguage.Japanese
            ? $"認証URL: {copilotVerificationUrl}\nユーザーコード: {copilotUserCode}\n操作: コピー · 開く · 確認"
            : $"Verification URL: {copilotVerificationUrl}\nUser code: {copilotUserCode}\nActions: Copy · Open · Poll"
        : CopyText.CopilotStatus(copilotState);
    private CodexUiResult codexResult = new(false, CodexAuthorizationState.Disconnected, "Codex is not connected.");
    private bool providerSelectionMade;
    private bool hasCodexCredential;
    private bool codexCredentialPresenceKnown;
    public CodexAuthorizationState CodexState => codexResult.State;
    public string CodexStatusText => Language == UiLanguage.Japanese ? CopyText.CodexStatus(codexResult.State, codexResult.Success, codexResult.Status) : codexResult.Message;
    public string CodexUserCode => codexResult.Prompt?.UserCode ?? string.Empty;
    public Uri? CodexVerificationUri => codexResult.Prompt?.VerificationUri;
    public string CodexVerificationUriText => codexResult.Prompt?.VerificationUri.ToString() ?? string.Empty;
    public string CodexExpiresText => codexResult.Prompt is { } prompt ? $"{CopyText.CodexExpires}: {prompt.ExpiresAt.ToLocalTime().ToString(Language == UiLanguage.Japanese ? "g" : "g", Language == UiLanguage.Japanese ? CultureInfo.GetCultureInfo("ja-JP") : CultureInfo.InvariantCulture)}" : string.Empty;
    public bool IsCodexPromptVisible => codexResult.Prompt is not null && codexResult.State is CodexAuthorizationState.AwaitingAuthorization or CodexAuthorizationState.Pending;
    public bool IsCodexConnected => codexResult.State == CodexAuthorizationState.Connected;
    public bool IsCodexDisconnected => codexResult.State == CodexAuthorizationState.Disconnected;
    public bool IsCodexError => codexResult.State == CodexAuthorizationState.Error;
    public bool IsCodexLogoutVisible => hasCodexCredential || IsCodexConnected;
    public void SetCodexCredentialPresence(bool exists) { hasCodexCredential = exists; codexCredentialPresenceKnown = true; OnPropertyChanged(nameof(IsCodexLogoutVisible)); }
    public void SetCodexResult(CodexUiResult result)
    {
        codexResult = result;
        if (result.Success && result.State == CodexAuthorizationState.Connected) { hasCodexCredential = true; codexCredentialPresenceKnown = true; }
        if (result.Success && result.State == CodexAuthorizationState.Disconnected) { hasCodexCredential = false; codexCredentialPresenceKnown = true; }
        foreach (var name in new[] { nameof(CodexState), nameof(CodexStatusText), nameof(CodexUserCode), nameof(CodexVerificationUriText), nameof(CodexExpiresText), nameof(IsCodexPromptVisible), nameof(IsCodexConnected), nameof(IsCodexDisconnected), nameof(IsCodexError), nameof(IsCodexLogoutVisible) }) OnPropertyChanged(name);
    }
    public void SetCodexBrowserStatus(bool opened) { SetCodexResult(codexResult with { Message = opened ? CopyText.CodexBrowserOpened : CopyText.CodexBrowserFailed }); }
    public bool IsProviderFlowOpen { get; private set; }
    public ProviderConnectionChoice SelectedProvider { get; private set; } = ProviderConnectionChoice.ChatGpt;
    public bool IsManualProviderPanelVisible => IsProviderFlowOpen && providerSelectionMade && SelectedProvider is ProviderConnectionChoice.ChatGpt or ProviderConnectionChoice.Claude;
    public bool IsCodexProviderPanelVisible => IsProviderFlowOpen && providerSelectionMade && SelectedProvider == ProviderConnectionChoice.Codex;
    public bool IsOpenCodeProviderPanelVisible => IsProviderFlowOpen && providerSelectionMade && SelectedProvider == ProviderConnectionChoice.OpenCode;
    public bool IsCopilotProviderPanelVisible => IsProviderFlowOpen && providerSelectionMade && SelectedProvider == ProviderConnectionChoice.Copilot;
    public void OpenProviderFlow() { IsProviderFlowOpen = true; providerSelectionMade = false; NotifyProviderFlow(); }
    public void CloseProviderFlow() { IsProviderFlowOpen = false; providerSelectionMade = false; NotifyProviderFlow(); }
    public void SelectProvider(ProviderConnectionChoice provider) { SelectedProvider = provider; providerSelectionMade = true; IsProviderFlowOpen = true; NotifyProviderFlow(); }
    private void NotifyProviderFlow()
    {
        foreach (var name in new[] { nameof(IsProviderFlowOpen), nameof(IsManualProviderPanelVisible), nameof(IsCodexProviderPanelVisible), nameof(IsOpenCodeProviderPanelVisible), nameof(IsCopilotProviderPanelVisible) }) OnPropertyChanged(name);
    }
    public AppPage CurrentPage { get => currentPage; private set => Set(ref currentPage, value); }
    public UiLanguage Language { get => language; set { if (Set(ref language, value)) { notificationService.Language = value; History.SetLanguage(value); RelocalizeCards(); NotifyLocalizedProperties(); } } }
    public string LanguageCode => Language == UiLanguage.Japanese ? "日本語" : "English";
    public PresentationState PresentationState { get => presentationState; set { if (Set(ref presentationState, value)) { OnPropertyChanged(nameof(IsLoading)); OnPropertyChanged(nameof(IsError)); OnPropertyChanged(nameof(IsOffline)); OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(HasEmptyState)); OnPropertyChanged(nameof(IsNotificationVisible)); OnPropertyChanged(nameof(NotificationBannerText)); } } }
    public bool IsDashboardVisible => CurrentPage == AppPage.Dashboard;
    public bool IsHistoryVisible => CurrentPage == AppPage.History;
    public bool IsSettingsVisible => CurrentPage == AppPage.Settings;
    public bool IsLoading => PresentationState == PresentationState.Loading;
    public bool IsError => PresentationState == PresentationState.Error;
    public bool IsOffline => PresentationState == PresentationState.Offline;
    public bool IsEmpty => PresentationState == PresentationState.Empty;

    public bool IsDemo => source.IsDemo;
    public string DemoBanner => IsDemo ? CopyText.DemoBanner : string.Empty;
    public bool HasCards => Cards.Count > 0;
    public bool HasEmptyState => PresentationState == PresentationState.Empty;
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
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private bool quotaDataBusy;

    public void Dispose()
    {
        // RefreshAsync may still be waiting on or holding the gate; leave it undisposed to avoid a race.
        (quotaHistory as IDisposable)?.Dispose();
    }
    public MainViewModel(IDashboardSource source, IManualQuotaService? manualQuotaService = null, IQuotaHistory? quotaHistory = null, TimeProvider? timeProvider = null, AppSettingsStore? settingsStore = null, IGitHubClientFactory? githubFactory = null, IQuotaApplication? quotaApplication = null) { this.source = source; this.manualQuotaService = manualQuotaService; this.quotaHistory = quotaHistory; this.timeProvider = timeProvider ?? TimeProvider.System; this.settingsStore = settingsStore; this.githubFactory = githubFactory; this.quotaApplication = quotaApplication; notificationDeduplicator = new NotificationDeduplicator(notificationService); notificationService.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(NotificationBannerText)); OnPropertyChanged(nameof(IsNotificationVisible)); }; History = new HistoryState(quotaHistory); LoadCards(); }
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        SetQuotaDataBusy(true);
        try
        {
            if (settingsStore is not null) SetSettings(await settingsStore.LoadAsync(cancellationToken), persist: false);
            if (quotaHistory is null) return;

            await quotaHistory.PruneAsync(DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime).AddDays(-30), cancellationToken);
            await History.InitializeAsync(cancellationToken);
            if (History.LoadError is not null) notificationService.Notify(CopyText.HistoryNotificationTitle, CopyText.HistoryCorrupt);

            var snapshots = new List<QuotaSnapshot>();
            var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
            for (var dayOffset = 0; dayOffset < 30; dayOffset++)
            {
                try { snapshots.AddRange(await quotaHistory.ReadAsync(today.AddDays(-dayOffset), cancellationToken)); }
                catch (InvalidDataException) { }
            }

            if (snapshots.Count > 0) lastKnownSnapshots.Clear();
            if (snapshots.Count > 0) lastKnownSnapshots.AddRange(snapshots);
            ReplaceCards(snapshots.Count > 0 ? DashboardAggregation.ToCards(snapshots, timeProvider.GetUtcNow(), Language) : source.Load());
        }
        finally { SetQuotaDataBusy(false); }
    }
    public string NotificationBannerText => string.IsNullOrWhiteSpace(notificationService.BannerText) && IsError ? CopyText.RefreshError : notificationService.BannerText;
    public bool IsNotificationVisible => !string.IsNullOrWhiteSpace(notificationService.BannerText) || IsError;
    public void Notify(string title, string reason) => notificationService.Notify(title, reason);
    public void SetSettings(AppSettingsDto dto, bool persist = true) { Settings.TrySetRefreshMinutes(dto.RefreshMinutes); Settings.TrySetThreshold(dto.OverallThreshold, out _); Settings.NotificationsEnabled = dto.NotificationsEnabled; Settings.ResidentMode = dto.ResidentMode; Settings.ProviderOverrides.Clear(); if (dto.ProviderThresholds is not null) foreach (var pair in dto.ProviderThresholds) if (Enum.TryParse<ProviderKind>(pair.Key, true, out var provider)) Settings.ProviderOverrides[provider] = pair.Value; Settings.GithubOAuthClientId = dto.GithubOAuthClientId; Language = dto.Language == "Japanese" ? UiLanguage.Japanese : UiLanguage.English; Settings.Theme = Enum.TryParse<ThemeMode>(dto.Theme, true, out var theme) ? theme : ThemeMode.System; UiSettings.ApplyTheme(Settings.Theme); githubFactory?.Create(dto.GithubOAuthClientId); if (persist) settingsStore?.Save(CurrentSettings()); }
    private AppSettingsDto CurrentSettings() => new(Settings.Theme.ToString(), Language == UiLanguage.Japanese ? "Japanese" : "English", Settings.RefreshMinutes, Settings.OverallThreshold, Settings.NotificationsEnabled, Settings.GithubOAuthClientId, Settings.ProviderOverrides.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value), Settings.ResidentMode);
    public async Task SetThemeAsync(ThemeMode value, CancellationToken cancellationToken = default) { Settings.Theme = value; UiSettings.ApplyTheme(value); if (settingsStore is not null) await settingsStore.SaveAsync(CurrentSettings(), cancellationToken); }
    public async Task SetLanguageAsync(UiLanguage value, CancellationToken cancellationToken = default) { Language = value; if (settingsStore is not null) await settingsStore.SaveAsync(CurrentSettings(), cancellationToken); }
    public async Task<bool> SetRefreshMinutesAsync(int value, CancellationToken cancellationToken = default) { if (!Settings.TrySetRefreshMinutes(value)) return false; if (settingsStore is not null) await settingsStore.SaveAsync(CurrentSettings(), cancellationToken); return true; }
    public async Task SetNotificationsAsync(bool value, CancellationToken cancellationToken = default) { Settings.NotificationsEnabled = value; if (settingsStore is not null) await settingsStore.SaveAsync(CurrentSettings(), cancellationToken); }
    public async Task SetResidentModeAsync(bool value, CancellationToken cancellationToken = default) { Settings.ResidentMode = value; OnPropertyChanged(nameof(Settings)); if (settingsStore is not null) await settingsStore.SaveAsync(CurrentSettings(), cancellationToken); }
    public void RestoreResidentMode(bool value) { Settings.ResidentMode = value; OnPropertyChanged(nameof(Settings)); }
    public async Task<bool> SetThresholdAsync(decimal value, CancellationToken cancellationToken = default) { if (!Settings.TrySetThreshold(value, out _)) return false; if (settingsStore is not null) await settingsStore.SaveAsync(CurrentSettings(), cancellationToken); return true; }
    public async Task SetGithubClientIdAsync(string value, CancellationToken cancellationToken = default) { Settings.GithubOAuthClientId = value.Trim(); githubFactory?.Create(Settings.GithubOAuthClientId); if (settingsStore is not null) await settingsStore.SaveAsync(CurrentSettings(), cancellationToken); }
    public void Navigate(AppPage page) => CurrentPage = page;
    public void Refresh() => _ = RefreshAsync();
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await refreshGate.WaitAsync(cancellationToken);
        SetQuotaDataBusy(true);
        try
        {
            PresentationState = PresentationState.Loading;
            if (quotaApplication is null) { LoadCards(); PresentationState = Cards.Count == 0 ? PresentationState.Empty : PresentationState.Ready; return; }
            var previousProviderIdentities = ExpectedAutomaticIdentities();
            var refreshResult = await quotaApplication.RefreshAsync(cancellationToken);
            var snapshots = refreshResult.Snapshots;
            var currentProviderIdentities = snapshots
                .Where(snapshot => snapshot.Source != QuotaSource.Manual)
                .Select(snapshot => (snapshot.Provider, snapshot.Account, snapshot.Metric, snapshot.Window.Kind))
                .ToHashSet();
            var missingProviders = previousProviderIdentities
                .Where(identity => !currentProviderIdentities.Contains(identity))
                .Select(identity => identity.Provider)
                .Distinct()
                .ToList();
            var partialFailure = missingProviders.Count > 0;
            if (snapshots.Count > 0)
            {
                MergeLastKnownSnapshots(snapshots);
                if (Settings.NotificationsEnabled) foreach (var snapshot in snapshots) await notificationDeduplicator.ConsiderAsync(snapshot, Settings.ProviderOverrides.GetValueOrDefault(snapshot.Provider, Settings.OverallThreshold), timeProvider.GetUtcNow(), cancellationToken);
                UpsertSnapshotCards(snapshots, timeProvider.GetUtcNow());
                ReevaluateCards(timeProvider.GetUtcNow());
                if (quotaHistory is not null) await History.RefreshAfterPersistAsync(cancellationToken);
                if (refreshResult.Failures.Count > 0)
                    notificationService.Notify(CopyText.RefreshIncompleteTitle, CopyText.RefreshFailures(refreshResult.Failures, mixedResult: true));
                else if (partialFailure)
                    notificationService.Notify(CopyText.RefreshIncompleteTitle, CopyText.RefreshMissingProviders(missingProviders));
                else
                    notificationService.Clear();
                PresentationState = refreshResult.Failures.Count > 0 || partialFailure ? PresentationState.Error : PresentationState.Ready;
            }
            else ReevaluateCards(timeProvider.GetUtcNow());
            if (snapshots.Count == 0)
            {
                if (refreshResult.Failures.Count > 0)
                    notificationService.Notify(CopyText.RefreshIncompleteTitle, CopyText.RefreshFailures(refreshResult.Failures, mixedResult: false));
                else if (partialFailure)
                    notificationService.Notify(CopyText.RefreshIncompleteTitle, CopyText.RefreshMissingProviders(missingProviders));
                else
                    notificationService.Clear();
                PresentationState = refreshResult.Failures.Count > 0 || partialFailure ? PresentationState.Error : Cards.Count > 0 ? PresentationState.Ready : PresentationState.Empty;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { ReevaluateCards(timeProvider.GetUtcNow()); PresentationState = PresentationState.Error; }
        finally { SetQuotaDataBusy(false); refreshGate.Release(); }
    }
    public async ValueTask ApplyProviderSnapshotsAsync(IReadOnlyList<QuotaSnapshot> snapshots, CancellationToken cancellationToken = default)
    {
        if (snapshots.Count == 0) return;
        if (quotaHistory is not null) await quotaHistory.AppendAsync(snapshots, cancellationToken);
        await History.RefreshAfterPersistAsync(cancellationToken);
        MergeLastKnownSnapshots(snapshots);
        if (Settings.NotificationsEnabled) foreach (var snapshot in snapshots) await notificationDeduplicator.ConsiderAsync(snapshot, Settings.ProviderOverrides.GetValueOrDefault(snapshot.Provider, Settings.OverallThreshold), timeProvider.GetUtcNow(), cancellationToken);
        UpsertSnapshotCards(snapshots, timeProvider.GetUtcNow());
        ReevaluateCards(timeProvider.GetUtcNow());
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
        var row = QuotaPresentationFormatter.Format(snapshot, timeProvider.GetUtcNow(), Language);
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
            Cards.Add(new ProviderCardViewModel(snapshot.Provider, snapshot.DisplayName.Length == 0 ? snapshot.Provider.ToString() : snapshot.DisplayName, snapshot.Account, "#405DE6", Language == UiLanguage.Japanese ? "手動" : "Manual", false, [row]));
        }
        OnPropertyChanged(nameof(HasCards)); OnPropertyChanged(nameof(HasEmptyState)); OnPropertyChanged(nameof(IsEmpty));
    }
    private void MergeLastKnownSnapshots(IEnumerable<QuotaSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            lastKnownSnapshots.RemoveAll(existing => IsSameSnapshotIdentity(existing, snapshot));
            lastKnownSnapshots.Add(snapshot);
        }
    }
    private void UpsertSnapshotCards(IEnumerable<QuotaSnapshot> snapshots, DateTimeOffset now)
    {
        foreach (var snapshot in snapshots)
        {
            var row = QuotaPresentationFormatter.Format(snapshot, now, Language);
            var matches = Cards
                .Select((card, index) => (card, index))
                .Where(item => item.card.Provider == snapshot.Provider && item.card.Account == snapshot.Account)
                .ToList();
            if (matches.Count == 0)
            {
                Cards.Add(DashboardAggregation.ToCards([snapshot], now, Language)[0]);
                continue;
            }

            var first = matches[0];
            var windows = matches
                .SelectMany(item => item.card.Windows)
                .Where(existing => existing.WindowName != row.WindowName || existing.Metric != row.Metric)
                .Append(row)
                .GroupBy(existing => (existing.WindowName, existing.Metric))
                .Select(group => group.Last())
                .OrderByDescending(existing => existing.VisualPercent)
                .ToList();
            Cards[first.index] = first.card with { Windows = windows };
            foreach (var duplicate in matches.Skip(1).OrderByDescending(item => item.index)) Cards.RemoveAt(duplicate.index);
        }
        OnPropertyChanged(nameof(HasCards)); OnPropertyChanged(nameof(HasEmptyState)); OnPropertyChanged(nameof(IsEmpty));
    }
    private HashSet<(ProviderKind Provider, string Account, string Metric, QuotaWindowKind Kind)> ExpectedAutomaticIdentities()
    {
        return lastKnownSnapshots
            .GroupBy(snapshot => (snapshot.Provider, snapshot.Account, snapshot.Metric, Kind: snapshot.Window.Kind))
            .Select(group => group.OrderByDescending(snapshot => snapshot.Observed).ThenByDescending(snapshot => snapshot.Fetched).First())
            .Where(snapshot => snapshot.Source != QuotaSource.Manual)
            .Where(snapshot => snapshot.Provider != ProviderKind.ChatGpt || !codexCredentialPresenceKnown || hasCodexCredential)
            .Select(snapshot => (snapshot.Provider, snapshot.Account, snapshot.Metric, Kind: snapshot.Window.Kind))
            .ToHashSet();
    }

    private static bool IsSameSnapshotIdentity(QuotaSnapshot left, QuotaSnapshot right) =>
        left.Provider == right.Provider && left.Account == right.Account && left.Metric == right.Metric && left.Window.Kind == right.Window.Kind;
    public void RemoveCodexCards()
    {
        lastKnownSnapshots.RemoveAll(snapshot => snapshot.Provider == ProviderKind.ChatGpt && snapshot.DisplayName.Contains("Codex", StringComparison.OrdinalIgnoreCase));
        for (var index = Cards.Count - 1; index >= 0; index--)
            if (Cards[index].Provider == ProviderKind.ChatGpt && Cards[index].Name.Contains("Codex", StringComparison.OrdinalIgnoreCase)) Cards.RemoveAt(index);
        OnPropertyChanged(nameof(HasCards)); OnPropertyChanged(nameof(HasEmptyState)); OnPropertyChanged(nameof(IsEmpty));
    }
    public void SetCopilotDeviceResult(string url, string code, CopilotUiState state = CopilotUiState.Started)
    {
        copilotState = state;
        if (state == CopilotUiState.Started && Uri.TryCreate(url, UriKind.Absolute, out var verificationUri) && verificationUri.Scheme is "https" or "http")
        {
            copilotVerificationUrl = verificationUri.ToString();
            copilotUserCode = code;
        }
        else
        {
            copilotVerificationUrl = string.Empty;
            copilotUserCode = string.Empty;
        }
        OnPropertyChanged(nameof(CopilotDeviceResult));
    }
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
            var windows = card.Windows.Select(row =>
            {
                var stale = row.WindowEnd != default && row.WindowEnd <= now || row.FreshUntil is { } expiry && now > expiry;
                var localized = row.Snapshot is null ? row : QuotaPresentationFormatter.Relocalize(row, now, Language);
                return localized with { IsStale = stale, ProgressLabel = Language == UiLanguage.Japanese ? $"{localized.PercentText}。{localized.StatusText}。{localized.ResetText}" : $"{localized.PercentText}. {localized.StatusText}. {localized.ResetText}" };
            }).ToList();
            var state = card.StateText;
            if (windows.Any(row => row.Snapshot is { Source: not QuotaSource.Manual }))
            {
                var hasFreshRow = windows.Any(row => row.Snapshot is { Source: not QuotaSource.Manual } && !row.IsStale);
                state = hasFreshRow ? (Language == UiLanguage.Japanese ? "接続済み" : "Connected") : (Language == UiLanguage.Japanese ? "更新できませんでした" : "Stale · refresh failed");
            }
            Cards[cardIndex] = card with { StateText = state, Windows = windows };
        }
    }
    private static string FormatAge(DateTimeOffset fetched, DateTimeOffset now)
    {
        var minutes = Math.Max(0, (int)(now - fetched).TotalMinutes);
        return minutes < 1 ? "just now" : minutes < 60 ? $"{minutes}m ago" : $"{minutes / 60}h ago";
    }
    private void RelocalizeCards()
    {
        for (var i = 0; i < Cards.Count; i++)
        {
            var card = Cards[i];
            var state = Language == UiLanguage.Japanese ? card.StateText switch { "Healthy" => "正常", "Needs attention" => "注意", "Over limit" => "超過", "Connected" => "接続済み", "Manual" => "手動", _ => card.StateText } : card.StateText switch { "正常" => "Healthy", "注意" => "Needs attention", "超過" => "Over limit", "接続済み" => "Connected", "手動" => "Manual", _ => card.StateText };
            Cards[i] = card with { Account = Language == UiLanguage.Japanese ? card.Account switch { "Personal account" => "個人アカウント", "Workspace · demo" => "ワークスペース · デモ", _ => card.Account } : card.Account switch { "個人アカウント" => "Personal account", "ワークスペース · デモ" => "Workspace · demo", _ => card.Account }, StateText = state, Windows = card.Windows.Select(row => QuotaPresentationFormatter.Relocalize(row, timeProvider.GetUtcNow(), Language)).ToList() };
        }
        OnPropertyChanged(nameof(Cards));
    }

    private void SetQuotaDataBusy(bool value)
    {
        if (quotaDataBusy == value) return;
        quotaDataBusy = value;
        OnPropertyChanged(nameof(IsQuotaDataBusy));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(QuotaStatusText));
    }

    private void NotifyLocalizedProperties()
    {
        foreach (var name in new[] { nameof(CopyText), nameof(NavDashboardText), nameof(NavHistoryText), nameof(NavSettingsText), nameof(RefreshText), nameof(QuotaStatusText), nameof(HeaderTitle), nameof(SubtitleText), nameof(EmptyStateText), nameof(EmptyStateDescription), nameof(HistoryDeleteText), nameof(DemoBanner), nameof(NotificationBannerText), nameof(OpenCodeCredentialNotice), nameof(CopilotNotice), nameof(CopilotDeviceResult), nameof(CodexStatusText), nameof(CodexExpiresText), nameof(ProviderChoices) }) OnPropertyChanged(name);
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnPropertyChanged(name); if (name == nameof(CurrentPage)) { OnPropertyChanged(nameof(IsDashboardVisible)); OnPropertyChanged(nameof(IsHistoryVisible)); OnPropertyChanged(nameof(IsSettingsVisible)); } return true; }
    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new(name));
}

public static class UiSettings
{
    public static IReadOnlyList<string> SupportedLanguages { get; } = ["English", "日本語"];
    public static IReadOnlyList<ProviderChoiceViewModel> ProviderChoices(UiLanguage language) => Enum.GetValues<ProviderConnectionChoice>().Select(choice => new ProviderChoiceViewModel(choice, ProviderDisplayName(choice, language))).ToList();
    private static string ProviderDisplayName(ProviderConnectionChoice choice, UiLanguage language) => choice switch
    {
        ProviderConnectionChoice.ChatGpt => "ChatGPT",
        ProviderConnectionChoice.Claude => "Claude",
        ProviderConnectionChoice.Codex => language == UiLanguage.Japanese ? "OpenAI Codex" : "OpenAI Codex",
        ProviderConnectionChoice.OpenCode => "OpenCode Go",
        ProviderConnectionChoice.Copilot => "GitHub Copilot",
        _ => choice.ToString()
    };
    public static IReadOnlyList<LocalizedChoice<string>> HistoryAccountChoices(UiLanguage language) => [new("All accounts", language == UiLanguage.Japanese ? "すべてのアカウント" : "All accounts"), new("Personal", language == UiLanguage.Japanese ? "個人" : "Personal"), new("Work", language == UiLanguage.Japanese ? "仕事" : "Work")];
    public static IReadOnlyList<LocalizedChoice<string>> HistoryWindowChoices(UiLanguage language) => [new("All windows", language == UiLanguage.Japanese ? "すべての利用枠" : "All windows"), .. Enum.GetValues<QuotaWindowKind>().Select(kind => new LocalizedChoice<string>(kind.ToString(), language == UiLanguage.Japanese ? QuotaPresentationFormatter.LocalizeWindow(kind) : kind.ToString()))];
    public static IReadOnlyList<LocalizedChoice<QuotaWindowKind>> ManualWindowChoices(UiLanguage language) => Enum.GetValues<QuotaWindowKind>().Select(kind => new LocalizedChoice<QuotaWindowKind>(kind, language == UiLanguage.Japanese ? QuotaPresentationFormatter.LocalizeWindow(kind) : kind.ToString())).ToList();
    public static IReadOnlyList<LocalizedChoice<ProviderKind>> ManualProviderChoices(UiLanguage language) => new[] { ProviderKind.ChatGpt, ProviderKind.Claude, ProviderKind.Copilot }.Select(provider => new LocalizedChoice<ProviderKind>(provider, provider switch { ProviderKind.ChatGpt => "ChatGPT", ProviderKind.Copilot => "GitHub Copilot", _ => provider.ToString() })).ToList();
    public static IReadOnlyList<LocalizedChoice<ThemeMode>> ThemeChoices(UiLanguage language) => Enum.GetValues<ThemeMode>().Select(theme => new LocalizedChoice<ThemeMode>(theme, theme switch { ThemeMode.System => language == UiLanguage.Japanese ? "システム" : "System", ThemeMode.Light => language == UiLanguage.Japanese ? "ライト" : "Light", _ => language == UiLanguage.Japanese ? "ダーク" : "Dark" })).ToList();
    public static string AutostartStatus => "Unsupported · platform backend not installed";
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

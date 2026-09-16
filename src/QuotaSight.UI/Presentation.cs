using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Styling;

using Avalonia.Threading;
using QuotaSight.Core;
using QuotaSight.Application;
using QuotaSight.Infrastructure;

[assembly: InternalsVisibleTo("QuotaSight.UI.Tests")]

namespace QuotaSight.UI;

public enum AppPage { Dashboard, History, Settings }
public enum ThemeMode { System, Light, Dark }
public enum UiLanguage { English, Japanese }
public enum PresentationState { Ready, Loading, Error, Offline, Empty }
public enum CodexAuthorizationState { Disconnected, AwaitingAuthorization, Pending, Connected, Error }
public enum ProviderConnectionChoice { ChatGpt, Claude, Codex, OpenCode, Copilot }
public enum CopilotUiState { Idle, Started, Pending, Completed, Denied, Expired, ConfigurationError, DeviceFlowDisabled, Unsupported, Failed, GhProbe }
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
                windows.OrderBy(s => s.Window.End - s.Window.Start)
                    .ThenBy(s => s.Window.Start)
                    .ThenBy(s => s.Window.End)
                    .ThenBy(s => s.Window.Kind)
                    .ThenBy(s => s.Metric, StringComparer.Ordinal)
                    .Select(s => QuotaPresentationFormatter.Format(s, now, language)).ToList());
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
public interface IUiDispatcher
{
    bool CheckAccess();
    Task InvokeAsync(Action action);
    Task InvokeAsync(Func<Task> action);
}
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();
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
public enum RefreshOrigin { Manual, Scheduled }
public interface IQuotaWorkScheduler
{
    Task<T> RunAsync<T>(Func<Task<T>> work);
}
public sealed class TaskRunQuotaWorkScheduler : IQuotaWorkScheduler
{
    public Task<T> RunAsync<T>(Func<Task<T>> work) => Task.Run(work);
}
internal sealed record HistorySnapshotState(
    IReadOnlyList<HistoryUiEntry> Raw,
    string AccountFilter,
    string WindowFilter,
    UiLanguage Language,
    int RequestedPage,
    long DataRevision,
    long QueryVersion,
    IReadOnlyList<HistoryUiEntry> Filtered,
    IReadOnlyList<HistoryUiEntry> Displayed,
    IReadOnlyList<double> Sparkline,
    int PageCount,
    int NormalizedPage,
    string? LoadError,
    bool RawUncertain = false)
{
    public static HistorySnapshotState Empty { get; } = new([], "All accounts", "All windows", UiLanguage.English, 0, 0, 0, [], [], [], 1, 0, null);
}

internal sealed record HistoryDesiredSummary(string AccountFilter, string WindowFilter, UiLanguage Language, int RequestedPage)
{
    public static HistoryDesiredSummary Empty { get; } = new("All accounts", "All windows", UiLanguage.English, 0);
}

internal interface IHistoryProjectionScheduler { Task<T> ScheduleAsync<T>(Func<T> work); }
internal sealed class TaskRunHistoryProjectionScheduler : IHistoryProjectionScheduler
{
    public Task<T> ScheduleAsync<T>(Func<T> work) => Task.Run(work);
}

public sealed class HistoryState : INotifyPropertyChanged, IDisposable
{
    public const int PageSize = 50;
    public const int SparklineLimit = 64;
    private static readonly string[] PublishedPropertyNames = [nameof(Entries), nameof(FilteredEntries), nameof(DisplayedEntries), nameof(SparklinePoints), nameof(LoadError), nameof(PageCount), nameof(CurrentPage), nameof(HasPreviousPage), nameof(HasNextPage), nameof(PageStatus)];
    private readonly IQuotaHistory? persistentHistory;
    private readonly IUiDispatcher uiDispatcher;
    private readonly IHistoryProjectionScheduler projectionScheduler;
    private readonly System.Threading.Channels.Channel<Command> commands = System.Threading.Channels.Channel.CreateUnbounded<Command>(new() { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task pump;
    private readonly object publishGate = new();
    private bool publishClosed;
    private long highestPublishedBuildId;
    private int stopped;

    // Reducer-owned state: mutated only while handling commands on the pump.
    private HistorySnapshotState desired = HistorySnapshotState.Empty;
    private HistorySnapshotState committed = HistorySnapshotState.Empty;
    private long acceptedDataRevision;
    private long buildCounter;
    private long latestBuildId;
    private long committedBuildId;
    private long notificationRecoveryBuildId = -1;
    private Exception? latestBuildFailure;
    private readonly Dictionary<long, CancellationTokenSource> buildWorkers = [];
    private readonly List<CrudRequest> crudRequests = [];
    private readonly List<WaitRequest> waitRequests = [];
    private readonly List<WaitRequest> serializingRequests = [];
    private Task storageTail = Task.CompletedTask;

    // Storage-lane state: touched only inside the serialized storage chain (and the constructor).
    private IReadOnlyList<HistoryUiEntry> memoryRaw = [];

    // Atomically published immutable snapshots read by public getters.
    private HistorySnapshotState published = HistorySnapshotState.Empty;
    private HistoryDesiredSummary desiredSummary = HistoryDesiredSummary.Empty;

    private enum CommandKind { SetAccount, SetWindow, SetLanguage, Previous, Next, Reload, Append, Delete, DeleteAll, Manual, ExportCsv, ExportJson, Barrier, StorageCompleted, BuildCompleted, PublishCompleted, SerializeCompleted }
    private sealed record Command(
        CommandKind Kind,
        string? Text = null,
        UiLanguage Language = UiLanguage.English,
        Guid Id = default,
        IReadOnlyList<QuotaSnapshot>? Snapshots = null,
        Func<CancellationToken, Task>? Persist = null,
        TaskCompletionSource<object?>? Result = null,
        CancellationTokenRegistration Registration = default,
        CancellationToken CancellationToken = default,
        long BuildId = 0,
        long Revision = 0,
        HistorySnapshotState? Built = null,
        IReadOnlyList<HistoryUiEntry>? Entries = null,
        Exception? Error = null,
        bool Canceled = false,
        bool Committed = false,
        bool DeleteResult = false,
        bool MutationApplied = false,
        string? LoadError = null);

    private sealed class CrudRequest
    {
        public required TaskCompletionSource<object?> Completion { get; init; }
        public required CancellationTokenRegistration Registration { get; init; }
        public required long TargetRevision { get; init; }
        public bool DeleteResult { get; set; } = true;
    }

    private sealed class WaitRequest
    {
        public required TaskCompletionSource<object?> Completion { get; init; }
        public required CancellationTokenRegistration Registration { get; init; }
        public required long TargetRevision { get; init; }
        public required long TargetQuery { get; init; }
        public required CommandKind Kind { get; init; }
        // The exact query (filters, language, page) desired when the request was accepted;
        // exports must serialize this query even if a later query publishes first.
        public required HistorySnapshotState DesiredAtAccept { get; init; }
        public CancellationToken CancellationToken { get; init; }
        // The exact rows at the accepted data revision, captured when that revision's storage
        // outcome is observed; exports must serialize these rows even if a later data revision
        // publishes first. Held only until the request settles, faults, or cancels.
        public bool HasAcceptedRaw { get; private set; }
        public IReadOnlyList<HistoryUiEntry> AcceptedRaw { get; private set; } = [];
        public bool AcceptedRawUncertain { get; private set; }
        public string? AcceptedLoadError { get; private set; }
        public void CaptureAcceptedRaw(HistorySnapshotState current)
        {
            AcceptedRaw = current.Raw;
            AcceptedRawUncertain = current.RawUncertain;
            AcceptedLoadError = current.LoadError;
            HasAcceptedRaw = true;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal Task PumpCompletedForTests => pump;

    public HistoryState(IQuotaHistory? history = null, IUiDispatcher? uiDispatcher = null) : this(history, uiDispatcher, null) { }
    internal HistoryState(IQuotaHistory? history, IUiDispatcher? uiDispatcher, IHistoryProjectionScheduler? projectionScheduler)
    {
        persistentHistory = history;
        this.uiDispatcher = uiDispatcher ?? new AvaloniaUiDispatcher();
        this.projectionScheduler = projectionScheduler ?? new TaskRunHistoryProjectionScheduler();
        if (history is null)
        {
            var raw = Enumerable.Range(0, 30).Select(i => new HistoryUiEntry(Guid.NewGuid(), i % 2 == 0 ? "ChatGPT Plus" : "Claude Pro", i % 2 == 0 ? "Personal" : "Work", "Weekly", 35 + i % 18, DateTimeOffset.UtcNow.AddDays(-29 + i), QuotaSource.Manual, QuotaConfidence.Manual)).ToArray();
            memoryRaw = raw;
            desired = desired with { Raw = raw };
            var seeded = Build(desired);
            committed = seeded;
            published = seeded;
        }
        pump = Task.Run(PumpAsync);
    }

    public IReadOnlyList<HistoryUiEntry> Entries => Volatile.Read(ref published).Raw;
    public IReadOnlyList<HistoryUiEntry> FilteredEntries => Volatile.Read(ref published).Filtered;
    public IReadOnlyList<HistoryUiEntry> DisplayedEntries => Volatile.Read(ref published).Displayed;
    public IReadOnlyList<double> SparklinePoints => Volatile.Read(ref published).Sparkline;
    public string? LoadError => Volatile.Read(ref published).LoadError;
    public UiLanguage Language => Volatile.Read(ref desiredSummary).Language;
    public int CurrentPage => Volatile.Read(ref published).NormalizedPage;
    public int RequestedPage => Volatile.Read(ref desiredSummary).RequestedPage;
    public int PageCount => Volatile.Read(ref published).PageCount;
    public bool HasPreviousPage => Volatile.Read(ref published).NormalizedPage > 0;
    public bool HasNextPage { get { var snapshot = Volatile.Read(ref published); return snapshot.NormalizedPage < snapshot.PageCount - 1; } }
    public string PageStatus => $"{CurrentPage + 1} / {PageCount}";

    public string AccountFilter { get => Volatile.Read(ref desiredSummary).AccountFilter; set => Enqueue(new(CommandKind.SetAccount, Text: value)); }
    public string WindowFilter { get => Volatile.Read(ref desiredSummary).WindowFilter; set => Enqueue(new(CommandKind.SetWindow, Text: value)); }
    public void SetLanguage(UiLanguage language) => Enqueue(new(CommandKind.SetLanguage, Language: language));
    public void PreviousPage() => Enqueue(new(CommandKind.Previous));
    public void NextPage() => Enqueue(new(CommandKind.Next));

    public Task InitializeAsync(CancellationToken cancellationToken = default) => EnqueueRequest(new(CommandKind.Reload), cancellationToken);
    public Task RefreshAfterPersistAsync(CancellationToken cancellationToken = default) => InitializeAsync(cancellationToken);
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => (bool)(await EnqueueRequest(new(CommandKind.Delete, Id: id), cancellationToken).ConfigureAwait(false))!;
    public Task DeleteAllAsync(CancellationToken cancellationToken = default) => EnqueueRequest(new(CommandKind.DeleteAll), cancellationToken);
    public Task AppendAndReloadAsync(IReadOnlyList<QuotaSnapshot> snapshots, CancellationToken cancellationToken = default) => EnqueueRequest(new(CommandKind.Append, Snapshots: snapshots), cancellationToken);
    public async Task<string> ExportCsvAsync(CancellationToken cancellationToken = default) => (string)(await EnqueueRequest(new(CommandKind.ExportCsv), cancellationToken).ConfigureAwait(false))!;
    public async Task<string> ExportJsonAsync(CancellationToken cancellationToken = default) => (string)(await EnqueueRequest(new(CommandKind.ExportJson), cancellationToken).ConfigureAwait(false))!;
    internal Task ApplyManualPersistAsync(Func<CancellationToken, Task> persist, CancellationToken cancellationToken = default) => EnqueueRequest(new(CommandKind.Manual, Persist: persist), cancellationToken);
    internal Task WaitForPublishedAsync(CancellationToken cancellationToken = default) => EnqueueRequest(new(CommandKind.Barrier), cancellationToken);

    private Task<object?> EnqueueRequest(Command command, CancellationToken cancellationToken)
    {
        var result = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Volatile.Read(ref stopped) != 0)
        {
            result.TrySetCanceled(cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(true));
            return result.Task;
        }
        var registration = cancellationToken.CanBeCanceled ? cancellationToken.Register(() => result.TrySetCanceled(cancellationToken)) : default;
        if (!commands.Writer.TryWrite(command with { Result = result, Registration = registration, CancellationToken = cancellationToken }))
        {
            registration.Dispose();
            result.TrySetCanceled(new CancellationToken(true));
        }
        return result.Task;
    }

    private void Enqueue(Command command)
    {
        if (Volatile.Read(ref stopped) == 0) commands.Writer.TryWrite(command);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var command in commands.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                try { Handle(command); }
                catch (Exception exception)
                {
                    command.Registration.Dispose();
                    command.Result?.TrySetException(exception);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { ShutdownPending(); }
    }

    private void Handle(Command command)
    {
        switch (command.Kind)
        {
            case CommandKind.SetAccount: ApplyQuery(desired with { AccountFilter = command.Text ?? "All accounts", RequestedPage = 0 }); break;
            case CommandKind.SetWindow: ApplyQuery(desired with { WindowFilter = command.Text ?? "All windows", RequestedPage = 0 }); break;
            case CommandKind.SetLanguage: ApplyQuery(desired with { Language = command.Language }); break;
            case CommandKind.Previous: ApplyQuery(desired with { RequestedPage = Math.Max(0, desired.RequestedPage - 1) }); break;
            case CommandKind.Next: ApplyQuery(desired with { RequestedPage = desired.RequestedPage + 1 }); break;
            case CommandKind.Reload:
            case CommandKind.Append:
            case CommandKind.Delete:
            case CommandKind.DeleteAll:
            case CommandKind.Manual: AcceptStorage(command); break;
            case CommandKind.ExportCsv:
            case CommandKind.ExportJson:
            case CommandKind.Barrier: AcceptWait(command); break;
            case CommandKind.StorageCompleted: CompleteStorage(command); break;
            case CommandKind.BuildCompleted: CompleteBuild(command); break;
            case CommandKind.PublishCompleted: CompletePublish(command); break;
            case CommandKind.SerializeCompleted: CompleteSerialize(command); break;
        }
    }

    private void ApplyQuery(HistorySnapshotState next)
    {
        desired = next with { QueryVersion = desired.QueryVersion + 1 };
        PublishDesiredSummary();
        StartBuild();
    }

    private void PublishDesiredSummary() => Volatile.Write(ref desiredSummary, new HistoryDesiredSummary(desired.AccountFilter, desired.WindowFilter, desired.Language, desired.RequestedPage));

    private void AcceptStorage(Command command)
    {
        var revision = ++acceptedDataRevision;
        if (command.Result is { } completion)
            crudRequests.Add(new CrudRequest { Completion = completion, Registration = command.Registration, TargetRevision = revision });
        var previous = storageTail;
        storageTail = Task.Run(() => RunStorageJobAsync(previous, command.Kind, revision, command.Id, command.Snapshots, command.Persist, command.CancellationToken));
    }

    private async Task RunStorageJobAsync(Task previous, CommandKind kind, long revision, Guid id, IReadOnlyList<QuotaSnapshot>? snapshots, Func<CancellationToken, Task>? persist, CancellationToken callerToken)
    {
        await previous.ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, callerToken);
        var token = linked.Token;
        // Once a mutation reached persistent storage, a later read failure leaves the cached
        // rows unverified; report that so stale data is never exported as current.
        var mutationApplied = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var incoming = snapshots?.ToArray() ?? [];
            if (persist is not null)
            {
                await persist(token).ConfigureAwait(false);
                mutationApplied = true;
            }
            IReadOnlyList<HistoryUiEntry> entries;
            string? loadError = null;
            var deleteResult = true;
            if (persistentHistory is null)
            {
                if (kind == CommandKind.Delete) deleteResult = memoryRaw.Any(entry => entry.Id == id);
                entries = kind switch
                {
                    CommandKind.Append => [.. memoryRaw, .. incoming.Select(snapshot => ToEntry(Guid.NewGuid(), snapshot))],
                    CommandKind.Delete => memoryRaw.Where(entry => entry.Id != id).ToArray(),
                    CommandKind.DeleteAll => [],
                    _ => memoryRaw
                };
                memoryRaw = entries;
            }
            else if (kind == CommandKind.DeleteAll)
            {
                await persistentHistory.DeleteAsync(null, token).ConfigureAwait(false);
                mutationApplied = true;
                entries = [];
            }
            else
            {
                if (kind == CommandKind.Append)
                {
                    await persistentHistory.AppendAsync(incoming, token).ConfigureAwait(false);
                    mutationApplied = true;
                }
                if (kind == CommandKind.Delete)
                {
                    var before = await ReadPersistedEntriesAsync(token).ConfigureAwait(false);
                    deleteResult = before.Entries.Any(entry => entry.Id == id);
                    if (deleteResult)
                    {
                        await persistentHistory.DeleteEventAsync(id, token).ConfigureAwait(false);
                        mutationApplied = true;
                    }
                }
                var read = await ReadPersistedEntriesAsync(token).ConfigureAwait(false);
                entries = read.Entries;
                loadError = read.Error;
            }
            commands.Writer.TryWrite(new(CommandKind.StorageCompleted, Revision: revision, Entries: entries, LoadError: loadError, DeleteResult: deleteResult));
        }
        catch (OperationCanceledException) { commands.Writer.TryWrite(new(CommandKind.StorageCompleted, Revision: revision, Canceled: true, MutationApplied: mutationApplied)); }
        catch (Exception exception) { commands.Writer.TryWrite(new(CommandKind.StorageCompleted, Revision: revision, Error: exception, MutationApplied: mutationApplied)); }
    }

    private void CompleteStorage(Command command)
    {
        var index = crudRequests.FindIndex(request => request.TargetRevision == command.Revision);
        var request = index >= 0 ? crudRequests[index] : null;
        if (command.Error is not null || command.Canceled)
        {
            if (request is not null)
            {
                crudRequests.RemoveAt(index);
                request.Registration.Dispose();
                if (command.Error is not null) request.Completion.TrySetException(command.Error);
                else request.Completion.TrySetCanceled(CancellationToken.None);
            }
            desired = command.MutationApplied
                // Storage did change but the verifying re-read failed: keep showing the last
                // verified rows, but mark them unverified so exports fault until a reload succeeds.
                ? desired with
                {
                    RawUncertain = true,
                    LoadError = command.Error?.Message ?? "History changed but could not be re-read; reload to verify.",
                    DataRevision = Math.Max(desired.DataRevision, command.Revision)
                }
                // The failed operation left the data unchanged, but its revision must still be observed
                // so exports and waiters accepted behind it do not stall forever.
                : desired with { DataRevision = Math.Max(desired.DataRevision, command.Revision), LoadError = command.Error?.Message ?? desired.LoadError };
            foreach (var waiter in waitRequests) CaptureAcceptedRawIfObserved(waiter);
            StartBuild();
            return;
        }
        if (request is not null) request.DeleteResult = command.DeleteResult;
        desired = desired with { Raw = command.Entries ?? [], LoadError = command.LoadError, RawUncertain = false, DataRevision = Math.Max(desired.DataRevision, command.Revision) };
        foreach (var waiter in waitRequests) CaptureAcceptedRawIfObserved(waiter);
        StartBuild();
    }

    private void AcceptWait(Command command)
    {
        var request = new WaitRequest
        {
            Completion = command.Result!,
            Registration = command.Registration,
            TargetRevision = acceptedDataRevision,
            TargetQuery = desired.QueryVersion,
            Kind = command.Kind,
            DesiredAtAccept = desired,
            CancellationToken = command.CancellationToken
        };
        CaptureAcceptedRawIfObserved(request);
        if (committed.DataRevision >= request.TargetRevision && committed.QueryVersion >= request.TargetQuery)
        {
            SettleWait(request, committed);
            return;
        }
        if (latestBuildFailure is { } failure && desired.DataRevision >= request.TargetRevision && desired.QueryVersion >= request.TargetQuery)
        {
            request.Registration.Dispose();
            request.Completion.TrySetException(failure);
            return;
        }
        waitRequests.Add(request);
    }

    private void CaptureAcceptedRawIfObserved(WaitRequest request)
    {
        if (request.Kind == CommandKind.Barrier || request.HasAcceptedRaw || desired.DataRevision < request.TargetRevision) return;
        request.CaptureAcceptedRaw(desired);
    }

    private void SettleWait(WaitRequest request, HistorySnapshotState snapshot)
    {
        if (request.Kind == CommandKind.Barrier)
        {
            request.Registration.Dispose();
            request.Completion.TrySetResult(null);
            return;
        }
        if (request.HasAcceptedRaw ? request.AcceptedRawUncertain : snapshot.RawUncertain)
        {
            // A storage mutation was applied but the verifying re-read failed; refusing the
            // export is the only way to avoid presenting stale rows as current data.
            request.Registration.Dispose();
            request.Completion.TrySetException(new InvalidOperationException((request.HasAcceptedRaw ? request.AcceptedLoadError : snapshot.LoadError) ?? "History changed but could not be re-read; reload to verify."));
            return;
        }
        serializingRequests.Add(request);
        _ = SerializeAsync(request, snapshot);
    }

    private async Task SerializeAsync(WaitRequest request, HistorySnapshotState snapshot)
    {
        try
        {
            var text = await Task.Run(() =>
            {
                // Serialize the exact rows captured at the accepted data revision under the query
                // desired at accept time; the covering snapshot may already belong to a later data
                // or query revision and neither may be substituted.
                var projection = Build(request.DesiredAtAccept with { Raw = request.HasAcceptedRaw ? request.AcceptedRaw : snapshot.Raw });
                return request.Kind == CommandKind.ExportJson ? SerializeJson(projection.Filtered) : SerializeCsv(projection.Filtered);
            }, request.CancellationToken).ConfigureAwait(false);
            commands.Writer.TryWrite(new(CommandKind.SerializeCompleted, Text: text, Result: request.Completion));
        }
        catch (Exception exception)
        {
            commands.Writer.TryWrite(new(CommandKind.SerializeCompleted, Error: exception, Result: request.Completion));
        }
    }

    private void CompleteSerialize(Command command)
    {
        var index = serializingRequests.FindIndex(request => ReferenceEquals(request.Completion, command.Result));
        if (index >= 0)
        {
            serializingRequests[index].Registration.Dispose();
            serializingRequests.RemoveAt(index);
        }
        if (command.Error is not null) command.Result?.TrySetException(command.Error);
        else command.Result?.TrySetResult(command.Text ?? string.Empty);
    }

    private void StartBuild()
    {
        latestBuildFailure = null;
        var buildId = ++buildCounter;
        latestBuildId = buildId;
        foreach (var worker in buildWorkers.Values) worker.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        buildWorkers[buildId] = cts;
        _ = BuildAsync(desired, buildId, cts.Token);
    }

    private async Task BuildAsync(HistorySnapshotState input, long buildId, CancellationToken token)
    {
        try
        {
            var work = projectionScheduler.ScheduleAsync(() => Build(input));
            _ = work.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var built = await work.WaitAsync(token).ConfigureAwait(false);
            commands.Writer.TryWrite(new(CommandKind.BuildCompleted, BuildId: buildId, Built: built));
        }
        catch (OperationCanceledException) { commands.Writer.TryWrite(new(CommandKind.BuildCompleted, BuildId: buildId, Canceled: true)); }
        catch (Exception exception) { commands.Writer.TryWrite(new(CommandKind.BuildCompleted, BuildId: buildId, Error: exception)); }
    }

    private void CompleteBuild(Command command)
    {
        if (buildWorkers.Remove(command.BuildId, out var worker)) worker.Dispose();
        if (command.BuildId != latestBuildId) return;
        if (command.Error is not null)
        {
            latestBuildFailure = command.Error;
            desired = desired with { LoadError = command.Error.Message };
            FailSatisfiable(command.Error, desired.DataRevision, desired.QueryVersion);
            return;
        }
        if (command.Canceled)
        {
            // The latest projection will never publish; no newer build supersedes it, so every
            // request that depends on it must terminate now instead of waiting forever.
            var canceled = new OperationCanceledException("The latest history projection was canceled before publishing.");
            latestBuildFailure = canceled;
            CancelSatisfiable(desired.DataRevision, desired.QueryVersion);
            return;
        }
        _ = PublishAsync(command.Built!, command.BuildId);
    }

    private async Task PublishAsync(HistorySnapshotState snapshot, long buildId)
    {
        try
        {
            await uiDispatcher.InvokeAsync(() =>
            {
                var swapped = false;
                Exception? notifyError = null;
                lock (publishGate)
                {
                    // Freshness check at publish time: a publish action released after a newer
                    // build already published must not overwrite the newer snapshot.
                    if (!publishClosed && buildId > highestPublishedBuildId)
                    {
                        highestPublishedBuildId = buildId;
                        Volatile.Write(ref published, snapshot);
                        swapped = true;
                        notifyError = RaisePublishedNotifications();
                    }
                }
                commands.Writer.TryWrite(new(CommandKind.PublishCompleted, BuildId: buildId, Built: snapshot, Committed: swapped, Canceled: !swapped, Error: notifyError));
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            commands.Writer.TryWrite(new(CommandKind.PublishCompleted, BuildId: buildId, Built: snapshot, Error: exception));
        }
    }

    private Exception? RaisePublishedNotifications()
    {
        Exception? first = null;
        foreach (var name in PublishedPropertyNames)
        {
            // A subscriber may dispose this state while handling a notification; once the
            // publish gate closes no further property names may be raised.
            if (publishClosed) break;
            try { PropertyChanged?.Invoke(this, new(name)); }
            catch (Exception exception) { first ??= exception; }
        }
        return first;
    }

    private void CompletePublish(Command command)
    {
        if (command.Committed)
        {
            // Publish order is enforced by the gate, but completion commands can arrive out of
            // order; never let an older build replace the committed snapshot.
            if (command.BuildId < committedBuildId) return;
            committedBuildId = command.BuildId;
            committed = command.Built!;
            if (command.Error is not null)
            {
                FailSatisfiable(command.Error, committed.DataRevision, committed.QueryVersion);
                if (command.BuildId != notificationRecoveryBuildId)
                {
                    // Surface one safe LoadError through a single recovery publish; if that
                    // recovery publish faults again the loop stops instead of republishing.
                    desired = desired with { LoadError = command.Error.Message, QueryVersion = desired.QueryVersion + 1 };
                    StartBuild();
                    notificationRecoveryBuildId = latestBuildId;
                }
                else desired = desired with { LoadError = command.Error.Message };
                return;
            }
            if (committed.QueryVersion == desired.QueryVersion && desired.RequestedPage != committed.NormalizedPage)
            {
                desired = desired with { RequestedPage = committed.NormalizedPage };
                PublishDesiredSummary();
            }
            SettleSatisfied(committed);
            return;
        }
        if (command.Canceled) return;
        if (command.Error is not null && command.BuildId == latestBuildId && !ReferenceEquals(committed, command.Built))
        {
            latestBuildFailure = command.Error;
            desired = desired with { LoadError = command.Error.Message };
            FailSatisfiable(command.Error, command.Built!.DataRevision, command.Built.QueryVersion);
        }
    }

    private void SettleSatisfied(HistorySnapshotState snapshot)
    {
        for (var i = crudRequests.Count - 1; i >= 0; i--)
        {
            var request = crudRequests[i];
            if (request.Completion.Task.IsCompleted)
            {
                crudRequests.RemoveAt(i);
                request.Registration.Dispose();
                continue;
            }
            if (request.TargetRevision > snapshot.DataRevision) continue;
            crudRequests.RemoveAt(i);
            request.Registration.Dispose();
            request.Completion.TrySetResult(request.DeleteResult);
        }
        for (var i = waitRequests.Count - 1; i >= 0; i--)
        {
            var request = waitRequests[i];
            if (request.Completion.Task.IsCompleted)
            {
                waitRequests.RemoveAt(i);
                request.Registration.Dispose();
                continue;
            }
            if (request.TargetRevision > snapshot.DataRevision || request.TargetQuery > snapshot.QueryVersion) continue;
            waitRequests.RemoveAt(i);
            SettleWait(request, snapshot);
        }
    }

    private void FailSatisfiable(Exception error, long dataRevision, long queryVersion)
    {
        for (var i = crudRequests.Count - 1; i >= 0; i--)
        {
            var request = crudRequests[i];
            if (request.TargetRevision > dataRevision) continue;
            crudRequests.RemoveAt(i);
            request.Registration.Dispose();
            request.Completion.TrySetException(error);
        }
        for (var i = waitRequests.Count - 1; i >= 0; i--)
        {
            var request = waitRequests[i];
            if (request.TargetRevision > dataRevision || request.TargetQuery > queryVersion) continue;
            waitRequests.RemoveAt(i);
            request.Registration.Dispose();
            request.Completion.TrySetException(error);
        }
    }

    private void CancelSatisfiable(long dataRevision, long queryVersion)
    {
        for (var i = crudRequests.Count - 1; i >= 0; i--)
        {
            var request = crudRequests[i];
            if (request.TargetRevision > dataRevision) continue;
            crudRequests.RemoveAt(i);
            request.Registration.Dispose();
            request.Completion.TrySetCanceled(CancellationToken.None);
        }
        for (var i = waitRequests.Count - 1; i >= 0; i--)
        {
            var request = waitRequests[i];
            if (request.TargetRevision > dataRevision || request.TargetQuery > queryVersion) continue;
            waitRequests.RemoveAt(i);
            request.Registration.Dispose();
            request.Completion.TrySetCanceled(CancellationToken.None);
        }
    }

    private void ShutdownPending()
    {
        foreach (var worker in buildWorkers.Values) worker.Cancel();
        buildWorkers.Clear();
        foreach (var request in crudRequests) { request.Registration.Dispose(); request.Completion.TrySetCanceled(lifetime.Token); }
        crudRequests.Clear();
        foreach (var request in waitRequests) { request.Registration.Dispose(); request.Completion.TrySetCanceled(lifetime.Token); }
        waitRequests.Clear();
        foreach (var request in serializingRequests) { request.Registration.Dispose(); request.Completion.TrySetCanceled(lifetime.Token); }
        serializingRequests.Clear();
        while (commands.Reader.TryRead(out var command))
        {
            command.Registration.Dispose();
            command.Result?.TrySetCanceled(lifetime.Token);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        lock (publishGate) { publishClosed = true; }
        // Completing the channel before cancelling makes acceptance linearizable: any TryWrite
        // that succeeded happened before completion, so the pump either handles that command or
        // ShutdownPending drains and cancels it — no request can be orphaned.
        commands.Writer.TryComplete();
        lifetime.Cancel();
    }

    private async Task<(IReadOnlyList<HistoryUiEntry> Entries, string? Error)> ReadPersistedEntriesAsync(CancellationToken token)
    {
        var entries = new List<HistoryUiEntry>();
        string? error = null;
        for (var i = 0; i < 30; i++)
        {
            try
            {
                foreach (var item in await persistentHistory!.ReadEventsAsync(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-i)), token).ConfigureAwait(false))
                    entries.Add(ToEntry(item.EventId, item.Snapshot));
            }
            catch (InvalidDataException) { error = "History data is damaged; showing available entries."; }
        }
        return (entries, error);
    }

    private static HistoryUiEntry ToEntry(Guid id, QuotaSnapshot snapshot) => new(id, snapshot.DisplayName.Length == 0 ? snapshot.Provider.ToString() : snapshot.DisplayName, snapshot.Account, snapshot.Window.Kind.ToString(), snapshot.EffectivePercent, snapshot.Observed, snapshot.Source, snapshot.Confidence);

    private static HistorySnapshotState Build(HistorySnapshotState input)
    {
        var filtered = input.Raw.Where(e => (input.AccountFilter == "All accounts" || e.Account == input.AccountFilter) && (input.WindowFilter == "All windows" || e.Window == input.WindowFilter)).Select(e => e with { WindowDisplay = input.Language == UiLanguage.Japanese && Enum.TryParse<QuotaWindowKind>(e.Window, true, out var kind) ? QuotaPresentationFormatter.LocalizeWindow(kind) : e.Window }).ToArray();
        var pageCount = Math.Max(1, (filtered.Length + PageSize - 1) / PageSize);
        var page = Math.Clamp(input.RequestedPage, 0, pageCount - 1);
        var displayed = filtered.Skip(page * PageSize).Take(PageSize).ToArray();
        var points = filtered.Where(e => e.UsedPercent is not null).OrderBy(e => e.Observed).Select(e => (double)e.UsedPercent!.Value).ToArray();
        IReadOnlyList<double> sparkline = points.Length <= SparklineLimit ? points : Enumerable.Range(0, SparklineLimit).Select(i => points[(int)Math.Round(i * (points.Length - 1d) / (SparklineLimit - 1), MidpointRounding.ToEven)]).ToArray();
        return input with { Filtered = filtered, Displayed = displayed, Sparkline = sparkline, PageCount = pageCount, NormalizedPage = page };
    }

    private static string SerializeJson(IReadOnlyList<HistoryUiEntry> entries)
    {
        // Utf8JsonWriter is AOT-safe (no reflection) and escapes every control character.
        using var buffer = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("provider", entry.Provider);
                writer.WriteString("account", entry.Account);
                writer.WriteString("window", entry.Window);
                if (entry.UsedPercent is { } percent) writer.WriteNumber("usedPercent", percent);
                else writer.WriteNull("usedPercent");
                writer.WriteString("observed", entry.Observed.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("source", entry.Source.ToString());
                writer.WriteString("confidence", entry.Confidence.ToString());
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
    private static string SerializeCsv(IReadOnlyList<HistoryUiEntry> entries) => "Provider,Account,Window,UsedPercent,Observed,Source,Confidence\n" + string.Join("\n", entries.Select(e => $"{EscapeCsv(e.Provider)},{EscapeCsv(e.Account)},{EscapeCsv(e.Window)},{(e.UsedPercent?.ToString("0.##") ?? string.Empty)},{e.Observed:O},{e.Source},{e.Confidence}"));
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
    public string CopilotQuotaNotice => CopyText.CopilotQuotaNotice;
    public string CopilotFlowWaitingText => CopyText.CopilotFlowWaiting;
    public IReadOnlyList<ProviderChoiceViewModel> ProviderChoices => UiSettings.ProviderChoices(Language);
    private CopilotUiState copilotState = CopilotUiState.Idle;
    private string copilotVerificationUrl = string.Empty;
    private string copilotUserCode = string.Empty;
    private string copilotDetail = string.Empty;
    public CopilotUiState CopilotState => copilotState;
    public bool IsCopilotFlowActive { get; private set; }
    public bool CanStartCopilot => !IsCopilotFlowActive;
    public bool CanPollCopilot => !IsCopilotFlowActive && copilotState is CopilotUiState.Started or CopilotUiState.Pending or CopilotUiState.Failed;
    public string CopilotDeviceResult => copilotState is CopilotUiState.Started or CopilotUiState.Pending
        ? Language == UiLanguage.Japanese
            ? $"認証URL: {copilotVerificationUrl}\nユーザーコード: {copilotUserCode}\n{CopyText.CopilotStatus(copilotState)}\n操作: コピー · 開く · 確認"
            : $"Verification URL: {copilotVerificationUrl}\nUser code: {copilotUserCode}\n{CopyText.CopilotStatus(copilotState)}\nActions: Copy · Open · Poll"
        : $"{CopyText.CopilotStatus(copilotState)}{(string.IsNullOrWhiteSpace(copilotDetail) ? string.Empty : $"\n{copilotDetail}")}";
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
    private readonly IUiDispatcher uiDispatcher;
    private readonly IQuotaWorkScheduler workScheduler;
    private int disposedState;

    private bool IsDisposed => Volatile.Read(ref disposedState) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposedState, 1) != 0) return;
        // RefreshAsync may still be waiting on or holding the gate; leave it undisposed to avoid a race.
        History.Dispose();
        (quotaHistory as IDisposable)?.Dispose();
    }
    public MainViewModel(IDashboardSource source, IManualQuotaService? manualQuotaService = null, IQuotaHistory? quotaHistory = null, TimeProvider? timeProvider = null, AppSettingsStore? settingsStore = null, IGitHubClientFactory? githubFactory = null, IQuotaApplication? quotaApplication = null, IUiDispatcher? uiDispatcher = null, IQuotaWorkScheduler? workScheduler = null) { this.source = source; this.manualQuotaService = manualQuotaService; this.quotaHistory = quotaHistory; this.timeProvider = timeProvider ?? TimeProvider.System; this.settingsStore = settingsStore; this.githubFactory = githubFactory; this.quotaApplication = quotaApplication; this.uiDispatcher = uiDispatcher ?? new AvaloniaUiDispatcher(); this.workScheduler = workScheduler ?? new TaskRunQuotaWorkScheduler(); notificationDeduplicator = new NotificationDeduplicator(notificationService); notificationService.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(NotificationBannerText)); OnPropertyChanged(nameof(IsNotificationVisible)); }; History = new HistoryState(quotaHistory, this.uiDispatcher); LoadCards(); }
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        SetQuotaDataBusy(true);
        try
        {
            if (settingsStore is not null) SetSettings(await workScheduler.RunAsync(() => settingsStore.LoadAsync(cancellationToken).AsTask()), persist: false);
            if (quotaHistory is null) return;

            // Prune, the 30-day read, and card aggregation are storage/CPU work: run them on a
            // worker; only the resulting immutable projections are applied on the caller.
            var pruneBefore = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime).AddDays(-30);
            var loaded = await workScheduler.RunAsync(async () =>
            {
                await quotaHistory.PruneAsync(pruneBefore, cancellationToken);
                var snapshots = new List<QuotaSnapshot>();
                var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
                for (var dayOffset = 0; dayOffset < 30; dayOffset++)
                {
                    try { snapshots.AddRange(await quotaHistory.ReadAsync(today.AddDays(-dayOffset), cancellationToken)); }
                    catch (InvalidDataException) { }
                }
                var cards = snapshots.Count > 0 ? DashboardAggregation.ToCards(snapshots, timeProvider.GetUtcNow(), Language) : null;
                return (Snapshots: snapshots, Cards: cards);
            });
            await History.InitializeAsync(cancellationToken);
            if (History.LoadError is not null) notificationService.Notify(CopyText.HistoryNotificationTitle, CopyText.HistoryCorrupt);

            if (loaded.Snapshots.Count > 0)
            {
                lastKnownSnapshots.Clear();
                lastKnownSnapshots.AddRange(loaded.Snapshots);
            }
            ReplaceCards(loaded.Cards ?? source.Load());
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
    public void Refresh() => _ = RefreshAsync(RefreshOrigin.Manual);
    public Task RefreshAsync(CancellationToken cancellationToken = default) => RefreshAsync(RefreshOrigin.Manual, cancellationToken);
    public async Task RefreshAsync(RefreshOrigin origin, CancellationToken cancellationToken = default)
    {
        if (origin == RefreshOrigin.Scheduled && !refreshGate.Wait(0)) return;
        if (origin == RefreshOrigin.Manual) await refreshGate.WaitAsync(cancellationToken);
        if (origin == RefreshOrigin.Manual) SetQuotaDataBusy(true);
        try
        {
            if (origin == RefreshOrigin.Manual) PresentationState = PresentationState.Loading;
            if (quotaApplication is null) { LoadCards(); PresentationState = Cards.Count == 0 ? PresentationState.Empty : PresentationState.Ready; return; }
            var previousProviderIdentities = ExpectedAutomaticIdentities();
            // Provider fetches (including their synchronous prefix) always run on a worker so
            // neither origin can execute network or credential work on the UI thread.
            var refreshResult = await workScheduler.RunAsync(() => quotaApplication.RefreshAsync(cancellationToken).AsTask());
            if (origin == RefreshOrigin.Scheduled)
            {
                await uiDispatcher.InvokeAsync(() => ApplyRefreshResultAsync(refreshResult, previousProviderIdentities, cancellationToken));
                return;
            }
            await ApplyRefreshResultAsync(refreshResult, previousProviderIdentities, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            if (origin == RefreshOrigin.Scheduled)
                await uiDispatcher.InvokeAsync(ApplyRefreshFailureUi);
            else
                ApplyRefreshFailureUi();
        }
        finally { if (origin == RefreshOrigin.Manual) SetQuotaDataBusy(false); refreshGate.Release(); }
    }
    private void ApplyRefreshFailureUi()
    {
        // The dispatcher may run this action after Dispose; recheck before any collection or
        // state mutation, not only before dispatching.
        if (IsDisposed) return;
        ReevaluateCards(timeProvider.GetUtcNow());
        PresentationState = PresentationState.Error;
    }
    private async Task ApplyRefreshResultAsync(QuotaRefreshResult refreshResult, HashSet<(ProviderKind Provider, string Account, string Metric, QuotaWindowKind Kind)> previousProviderIdentities, CancellationToken cancellationToken)
    {
        // An in-flight refresh released after Dispose must not publish state or notifications.
        if (IsDisposed) return;
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
            // HistoryState's storage lane is the single owner of history mutations: provider
            // snapshots are appended here instead of by the provider application.
            if (quotaHistory is not null) await History.AppendAndReloadAsync(snapshots, cancellationToken);
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
        OnPropertyChanged(nameof(PresentationState));
    }
    public async ValueTask ApplyProviderSnapshotsAsync(IReadOnlyList<QuotaSnapshot> snapshots, CancellationToken cancellationToken = default)
    {
        if (snapshots.Count == 0 || IsDisposed) return;
        if (quotaHistory is not null) await History.AppendAndReloadAsync(snapshots, cancellationToken);
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
        if (manualQuotaService is not null)
            await History.ApplyManualPersistAsync(async token => await manualQuotaService.SetAsync(snapshot, token), cancellationToken);
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
        if (IsDisposed) return;
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
    public void SetCopilotDeviceResult(string url, string code, CopilotUiState state = CopilotUiState.Started, string? detail = null)
    {
        copilotState = state;
        copilotDetail = state is CopilotUiState.Started or CopilotUiState.Pending ? string.Empty : detail ?? string.Empty;
        if (state is CopilotUiState.Started or CopilotUiState.Pending && Uri.TryCreate(url, UriKind.Absolute, out var verificationUri) && verificationUri.Scheme is "https" or "http")
        {
            copilotVerificationUrl = verificationUri.ToString();
            copilotUserCode = code;
        }
        else if (state is not CopilotUiState.Pending)
        {
            copilotVerificationUrl = string.Empty;
            copilotUserCode = string.Empty;
        }
        foreach (var name in new[] { nameof(CopilotDeviceResult), nameof(CopilotState), nameof(CanPollCopilot) }) OnPropertyChanged(name);
    }
    public void SetCopilotFlowActive(bool active)
    {
        IsCopilotFlowActive = active;
        foreach (var name in new[] { nameof(IsCopilotFlowActive), nameof(CanStartCopilot), nameof(CanPollCopilot) }) OnPropertyChanged(name);
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
    private void ReplaceCards(IEnumerable<ProviderCardViewModel> cards) { if (IsDisposed) return; var replacement = cards.ToList(); Cards.Clear(); foreach (var card in replacement) Cards.Add(card); OnPropertyChanged(nameof(HasCards)); OnPropertyChanged(nameof(HasEmptyState)); OnPropertyChanged(nameof(IsEmpty)); }
    private void ReevaluateCards(DateTimeOffset now)
    {
        // Cards.CollectionChanged is not covered by the OnPropertyChanged guard; in-flight
        // refresh/initialize completions must never mutate the collection after Dispose.
        if (IsDisposed) return;
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
        foreach (var name in new[] { nameof(CopyText), nameof(NavDashboardText), nameof(NavHistoryText), nameof(NavSettingsText), nameof(RefreshText), nameof(QuotaStatusText), nameof(HeaderTitle), nameof(SubtitleText), nameof(EmptyStateText), nameof(EmptyStateDescription), nameof(HistoryDeleteText), nameof(DemoBanner), nameof(NotificationBannerText), nameof(OpenCodeCredentialNotice), nameof(CopilotNotice), nameof(CopilotQuotaNotice), nameof(CopilotFlowWaitingText), nameof(CopilotDeviceResult), nameof(CodexStatusText), nameof(CodexExpiresText), nameof(ProviderChoices) }) OnPropertyChanged(name);
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnPropertyChanged(name); if (name == nameof(CurrentPage)) { OnPropertyChanged(nameof(IsDashboardVisible)); OnPropertyChanged(nameof(IsHistoryVisible)); OnPropertyChanged(nameof(IsSettingsVisible)); } return true; }
    private void OnPropertyChanged(string? name)
    {
        if (IsDisposed) return;
        PropertyChanged?.Invoke(this, new(name));
    }
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

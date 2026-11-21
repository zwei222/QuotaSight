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

public enum AppPage { Dashboard, Usage, History, Settings }
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
    public bool IsQuantityOnly { get; init; }
    public bool IsResetPending { get; init; }
    public string ResetPendingText { get; init; } = string.Empty;
    public string CompactValueText { get; init; } = string.Empty;
    public string CompactResetText { get; init; } = string.Empty;
    public string CompactPrimaryText => $"{WindowName} {CompactValueText}".TrimEnd();
    public string CompactTilePrimaryText => CompactPrimaryText;
    public string CompactTileResetText
    {
        get
        {
            if (IsQuantityOnly) return string.Empty;
            if (ResetAt is null) return $"· {new UiCopy(CurrentLanguage).CompactUnknownValue}";
            return CompactResetText.Length == 0 ? string.Empty : $"· {CompactResetText}";
        }
    }
    public bool IsCompactResetVisible => IsCompactTileVisible && !HasCompactStateBadge && CompactTileResetText.Length > 0;
    public string CompactTileText => CompactTileResetText.Length == 0 ? CompactTilePrimaryText : $"{CompactTilePrimaryText} {CompactTileResetText}";
    public string FactValueText
    {
        get
        {
            var copy = new UiCopy(CurrentLanguage);
            if (IsQuantityOnly) return QuantityGrossText;
            if (CopilotNoDataPeriod is not null) return WindowName;
            if (UsedPercent is null) return copy.CompactUnknownValue;
            if (Snapshot?.Provider == ProviderKind.Copilot) return copy.CopilotUsageVsReference(UsedPercent.Value);
            return copy.FactRemaining(Math.Max(0m, 100m - UsedPercent.Value));
        }
    }
    public string FactResetText
    {
        get
        {
            var copy = new UiCopy(CurrentLanguage);
            if (IsResetPending) return string.Empty; // the state pill carries "reset pending"; mock keeps the pill word only
            if (ResetAt is null) return copy.CompactUnknownValue;
            return CompactResetText;
        }
    }
    public string PillText
    {
        get
        {
            if (IsQuantityOnly) return string.Empty;
            var copy = new UiCopy(CurrentLanguage);
            if (IsResetPending) return copy.PillResetPending;
            if (UsedPercent is null) return copy.PillUnknown;
            if (UsedPercent > 100m) return copy.PillOver(UsedPercent.Value - 100m);
            if (UsedPercent == 100m) return copy.PillExhausted;
            if (IsStale) return copy.PillStale;
            return Band switch { UsageBand.Danger => copy.PillDanger, UsageBand.Attention => copy.PillAttention, _ => copy.PillNormal };
        }
    }
    public bool HasPill => PillText.Length > 0;
    public bool IsFactVisible => IsPercentVisible || IsQuantityVisible;
    public string QuantityNoteText => IsQuantityOnly ? new UiCopy(CurrentLanguage).CopilotRateDisclaimer : string.Empty;
    public string HeaderFreshnessText { get; init; } = string.Empty;
    public bool IsStackedFactVisible => IsPercentVisible && !IsCompactFactLine;
    public bool IsInlineFactVisible => IsPercentVisible && IsCompactFactLine;
    public bool IsCompactFactLine { get; init; }
    public bool IsPillOver => !IsStale && !IsResetPending && UsedPercent > 100m;
    public bool IsPillBad => !IsStale && !IsResetPending && UsedPercent is not > 100m && (UsedPercent == 100m || Band == UsageBand.Danger);
    public bool IsPillWarn => !IsStale && !IsResetPending && !IsPillBad && UsedPercent is not > 100m && Band == UsageBand.Attention;
    public bool IsPillNeutral => IsStale || IsResetPending || UsedPercent is null;
    public UiLanguage CurrentLanguage { get; init; }
    public string CompactStateBadge
    {
        get
        {
            if (IsQuantityOnly) return string.Empty;
            var copy = new UiCopy(CurrentLanguage);
            if (IsResetPending) return copy.CompactBadgeResetPending;
            if (UsedPercent is null) return copy.CompactBadgeUnknown;
            if (UsedPercent > 100m) return copy.CompactBadgeOver;
            if (UsedPercent == 100m) return copy.CompactExhausted;
            if (IsStale) return copy.CompactBadgeStale;
            return string.Empty;
        }
    }
    public bool HasCompactStateBadge => CompactStateBadge.Length > 0;
    public bool IsCompactBadgeWarning => UsedPercent is > 100m or 100m || IsResetPending;
    public bool IsCompactBadgeMuted => !IsCompactBadgeWarning;
    public bool IsCompactTileVisible => IsPercentVisible || IsCopilotNoData;
    public bool IsGaugeVisible => !IsQuantityOnly && CopilotNoDataPeriod is null && Snapshot?.Provider != ProviderKind.Copilot;
    public bool IsPercentVisible => !IsQuantityOnly && CopilotNoDataPeriod is null;
    public bool IsCurrentPercentVisible => IsPercentVisible && !IsStale;
    public bool IsStalePercentVisible => IsPercentVisible && IsStale;
    public bool IsQuantityVisible => IsQuantityOnly;
    public bool IsQuantityCompositionVisible { get; init; }
    public decimal IncludedCompositionRatio { get; init; }
    public decimal AdditionalCompositionRatio { get; init; }
    public decimal CompositionRatioTotal => IncludedCompositionRatio + AdditionalCompositionRatio;
    public string CompositionBarLabel { get; init; } = string.Empty;
    public string CompositionIncludedLabel { get; init; } = string.Empty;
    public string CompositionAdditionalLabel { get; init; } = string.Empty;
    public string QuantityGrossText { get; init; } = string.Empty;
    public string QuantityDiscountText { get; init; } = string.Empty;
    public string QuantityNetText { get; init; } = string.Empty;
    public bool IsAttention => Band == UsageBand.Attention;
    public bool IsDanger => Band == UsageBand.Danger;
    public bool IsOverLimit => Band == UsageBand.OverLimit;
    public QuotaSnapshot? Snapshot { get; init; }
    public CopilotRequestedPeriod? CopilotNoDataPeriod { get; init; }
    public bool IsCopilotNoData => CopilotNoDataPeriod is not null;
    public bool IsNotCopilotNoData => CopilotNoDataPeriod is null;
    public bool IsStatusVisible => IsNotCopilotNoData && !IsQuantityOnly;
    public bool IsStatusOrNoDataVisible => IsStatusVisible || IsCopilotNoData;
}
public sealed record QuotaRingSegmentViewModel(string WindowName, QuotaWindowKind WindowKind, decimal? RemainingPercent, UsageBand Band, bool IsStale);

public sealed record ProviderCardViewModel(ProviderKind Provider, string Name, string Account, string Accent, string StateText, bool IsDemo, IReadOnlyList<QuotaRowViewModel> Windows)
{
    private IReadOnlyList<QuotaRowViewModel> RingEligibleWindows => Windows
        .Where(row => Provider != ProviderKind.Copilot && row.IsGaugeVisible)
        .OrderBy(row => RingDuration(row) ?? TimeSpan.MaxValue)
        .ThenBy(row => QuotaWindowOrdering.FallbackRank(row.WindowKind))
        .ThenBy(row => row.WindowName, StringComparer.Ordinal)
        .ThenBy(row => row.Metric, StringComparer.Ordinal)
        .ToList();
    private IReadOnlyList<QuotaRowViewModel> RingWindowsToDraw
    {
        get
        {
            var ordered = RingEligibleWindows;
            if (ordered.Count <= 3) return ordered;
            var middle = ordered.Skip(1).Take(ordered.Count - 2).ToList();
            var constrained = middle
                .Where(row => !row.IsStale && !row.IsResetPending && Remaining(row) is not null)
                .OrderByDescending(row => row.Band == UsageBand.OverLimit)
                .ThenBy(row => Remaining(row))
                .ThenBy(row => RingDuration(row) ?? TimeSpan.MaxValue)
                .ThenBy(row => QuotaWindowOrdering.FallbackRank(row.WindowKind))
                .ThenBy(row => row.WindowName, StringComparer.Ordinal)
                .ThenBy(row => row.Metric, StringComparer.Ordinal)
                .FirstOrDefault() ?? middle[0];
            return new[] { ordered[0], constrained, ordered[^1] }
                .OrderBy(row => RingDuration(row) ?? TimeSpan.MaxValue)
                .ThenBy(row => QuotaWindowOrdering.FallbackRank(row.WindowKind))
                .ThenBy(row => row.WindowName, StringComparer.Ordinal)
                .ThenBy(row => row.Metric, StringComparer.Ordinal)
                .ToList();
        }
    }
    private IReadOnlyList<QuotaRingSegmentViewModel> CurrentKnownRingSegments => RingSegments
        .Where(segment => !segment.IsStale && segment.RemainingPercent is not null)
        .OrderBy(segment => segment.RemainingPercent)
        .ThenByDescending(segment => segment.Band == UsageBand.OverLimit)
        .ToList();
    public IReadOnlyList<QuotaRingSegmentViewModel> RingSegments
    {
        get
        {
            if (Provider == ProviderKind.Copilot && IsCopilotReferenceVisible && CopilotNoDataPeriod is null)
                return [new QuotaRingSegmentViewModel(new UiCopy(CurrentLanguage).CreditsPeriod, QuotaWindowKind.Monthly, null, UsageBand.Normal, false)];
            return RingWindowsToDraw
                .Select(row => new QuotaRingSegmentViewModel(row.WindowName, row.WindowKind, row.IsResetPending ? null : Remaining(row), row.Band, row.IsStale))
                .ToList();
        }
    }
    public bool IsMultiWindowFacts => Windows.Count >= 3;
    // The short rate note appears on quantity rows only when the card-level reference note is
    // absent (no manual reference configured), so the two copies never show together.
    public bool IsRateNoteVisible => !IsCopilotReferenceVisible;
    // Mock plan A widget: windows sit side by side in two star-sized columns (left column gets
    // the first window, right the second), so column width never depends on fixed pixel sums.
    public IReadOnlyList<QuotaRowViewModel> WindowsLeft => Windows.Where((_, index) => index % 2 == 0).ToArray();
    public IReadOnlyList<QuotaRowViewModel> WindowsRight => Windows.Where((_, index) => index % 2 == 1).ToArray();
    public int AdditionalRingWindowCount => Math.Max(0, RingEligibleWindows.Count - RingSegments.Count);
    public bool HasRingSegments => RingSegments.Count > 0;
    public bool HasAdditionalRingWindows => AdditionalRingWindowCount > 0;
    public string AdditionalRingWindowText => new UiCopy(CurrentLanguage).AdditionalRingWindows(AdditionalRingWindowCount);
    private bool HasCopilotReferenceCenter => Provider == ProviderKind.Copilot && IsCopilotReferenceVisible && CopilotGrossUsed is >= 0 && ManualReferenceCredits is > 0;
    public string RemainingCenterText => CurrentKnownRingSegments
        .Select(segment => segment.RemainingPercent!.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%")
        .FirstOrDefault() ?? (HasCopilotReferenceCenter ? new UiCopy(CurrentLanguage).CopilotCenterPercent(SafeReferencePercent(CopilotGrossUsed!.Value, ManualReferenceCredits!.Value)) : "—");
    public string RemainingCenterPeriodText => CurrentKnownRingSegments
        .Select(segment => segment.WindowName)
        .FirstOrDefault() ?? (HasCopilotReferenceCenter ? new UiCopy(CurrentLanguage).CreditsPeriod : string.Empty);
    public string ProviderMonogram => Provider switch { ProviderKind.ChatGpt => "G", ProviderKind.Claude => "C", ProviderKind.OpenCode => "O", ProviderKind.Copilot => "⌘", _ => "M" };
    public bool HasKnownRemainingCenter => CurrentKnownRingSegments.Count > 0 || HasCopilotReferenceCenter;
    public string FreshnessText => Windows.Where(row => !row.IsQuantityOnly).OrderByDescending(row => row.FetchedAt).Select(row => row.FreshnessText).FirstOrDefault() ?? string.Empty;
    public string HeaderFreshnessText => Windows
        .OrderByDescending(row => row.FetchedAt)
        .Select(row => row.HeaderFreshnessText)
        .FirstOrDefault(text => text.Length > 0) ?? string.Empty;
    private static decimal? Remaining(QuotaRowViewModel row) => row.UsedPercent is { } used ? Math.Clamp(100m - used, 0m, 100m) : null;
    private static TimeSpan? RingDuration(QuotaRowViewModel row)
    {
        if (row.Snapshot is { } snapshot && snapshot.Window.EffectiveDuration > TimeSpan.Zero) return snapshot.Window.EffectiveDuration;
        return row.WindowKind switch
        {
            QuotaWindowKind.Daily => TimeSpan.FromDays(1),
            QuotaWindowKind.Weekly => TimeSpan.FromDays(7),
            QuotaWindowKind.Monthly => TimeSpan.FromDays(30),
            _ => null
        };
    }
    public string AccessibleLabel => CopilotNoDataPeriod is { } period
        ? $"{Name}, {Account}. {new UiCopy(CurrentLanguage).CopilotNoDataAutomation(new UiCopy(CurrentLanguage).CopilotMonthPeriod(period.Year, period.Month))}"
        : $"{Name}, {Account}. {StateText}";
    public UiLanguage CurrentLanguage { get; init; }
    public CopilotRequestedPeriod? CopilotNoDataPeriod { get; init; }
    public bool IsCopilotNoData => CopilotNoDataPeriod is not null;
    public decimal? ManualReferenceCredits { get; init; }
    public decimal? CopilotGrossUsed { get; init; }
    public bool IsCopilotReferenceVisible => ManualReferenceCredits is > 0;
    public string CopilotReferencePercentText => !IsCopilotReferenceVisible ? string.Empty : new UiCopy(CurrentLanguage).CopilotReferencePercentDisplay(CopilotGrossUsed is >= 0 ? SafeReferencePercent(CopilotGrossUsed.Value, ManualReferenceCredits!.Value) : null);
    public string CopilotReferenceGaugeLabel => !IsCopilotReferenceVisible ? string.Empty : new UiCopy(CurrentLanguage).CopilotUsagePercent(CopilotGrossUsed is >= 0 ? SafeReferencePercent(CopilotGrossUsed.Value, ManualReferenceCredits!.Value) : null);
    public string CopilotUsagePercentText => !IsCopilotReferenceVisible ? string.Empty : new UiCopy(CurrentLanguage).CopilotUsagePercent(CopilotGrossUsed is >= 0 ? SafeReferencePercent(CopilotGrossUsed.Value, ManualReferenceCredits!.Value) : null) + ". " + new UiCopy(CurrentLanguage).CopilotReferenceDisclaimer;
    public double CopilotReferenceVisualPercent => CopilotGrossUsed is >= 0 && ManualReferenceCredits is > 0 ? (double)Math.Clamp(SafeReferencePercent(CopilotGrossUsed.Value, ManualReferenceCredits.Value) ?? 0m, 0m, 100m) : 0d;
    public bool IsCopilotReferenceGaugeVisible => IsCopilotReferenceVisible && CopilotGrossUsed is >= 0 && SafeReferencePercent(CopilotGrossUsed.Value, ManualReferenceCredits!.Value) is not null;
    public string CopilotReferenceBadge => new UiCopy(CurrentLanguage).CopilotManualReferenceBadge;
    public string CopilotReferenceCaption => new UiCopy(CurrentLanguage).CopilotReferenceShortCaption;
    private static decimal? SafeReferencePercent(decimal used, decimal reference)
    {
        if (reference <= 0m || used < 0m) return null;
        try { return checked(used / reference * 100m); }
        catch (OverflowException) { return null; }
    }
    public string CopilotReferenceText => ManualReferenceCredits is not > 0 ? string.Empty : new UiCopy(CurrentLanguage).CopilotReferenceDetail(CopilotGrossUsed, ManualReferenceCredits.Value);
    public string CopilotReferenceDisclaimer => new UiCopy(CurrentLanguage).CopilotReferenceDisclaimer;
    public string CopilotRateDisclaimer => new UiCopy(CurrentLanguage).CopilotRateDisclaimer;
}

public static class QuotaPresentationFormatter
{
    public static QuotaRowViewModel Format(QuotaSnapshot snapshot, DateTimeOffset now, UiLanguage language = UiLanguage.English)
    {
        var japanese = language == UiLanguage.Japanese;
        var reset = snapshot.Window.ResetAt is { } at ? FormatReset(at, now, language) : japanese ? "リセット時刻は不明" : "Reset time unavailable";
        var stale = snapshot.IsStale(now);
        var freshness = stale ? (japanese ? $"データが古い可能性があります（最終更新: {FormatAge(snapshot.Fetched, now, language)}）" : "Stale · last updated " + FormatAge(snapshot.Fetched, now, language)) : (japanese ? $"{FormatAge(snapshot.Fetched, now, language)}に更新" : "Updated " + FormatAge(snapshot.Fetched, now, language));
        var headerFreshness = japanese ? $"更新 {FormatAge(snapshot.Fetched, now, language)}" : $"Updated {FormatAge(snapshot.Fetched, now, language)}";
        var source = japanese ? snapshot.Source switch { QuotaSource.Manual => "手動", QuotaSource.Experimental => "実験的", QuotaSource.Delayed => "遅延", _ => "公式" } : snapshot.Source.ToString();
        if (snapshot.Provider == ProviderKind.Copilot && snapshot.CopilotUsage is { } usage)
        {
            var grossValue = usage.GrossQuantity.GetValueOrDefault();
            var includedValue = Math.Max(0m, usage.DiscountQuantity.GetValueOrDefault());
            var additionalValue = Math.Max(0m, usage.NetQuantity.GetValueOrDefault());
            var compositionVisible = grossValue > 0m;
            var includedRatio = compositionVisible ? SafeCompositionRatio(includedValue, grossValue, 1m) : 0m;
            var additionalRatio = compositionVisible ? SafeCompositionRatio(additionalValue, grossValue, 1m - includedRatio) : 0m;
            var grossValueText = FormatQuantity(usage.GrossQuantity, japanese ? "クレジット" : "credits", japanese);
            var includedValueText = FormatQuantity(usage.DiscountQuantity, japanese ? "クレジット" : "credits", japanese);
            var additionalValueText = FormatQuantity(usage.NetQuantity, japanese ? "クレジット" : "credits", japanese);
            var gross = japanese ? $"総量 {grossValueText}" : $"Gross {grossValueText}";
            var discount = japanese ? $"含まれる分 {includedValueText}" : $"Included {includedValueText}";
            var net = japanese ? $"追加分 {additionalValueText}" : $"Additional {additionalValueText}";
            var period = japanese ? $"集計期間: {snapshot.Window.Start:yyyy年M月d日}〜{snapshot.Window.End:yyyy年M月d日}" : $"Aggregation period: {snapshot.Window.Start:yyyy-MM-dd} – {snapshot.Window.End:yyyy-MM-dd}";
            var retrieved = japanese ? $"取得時刻: {snapshot.Fetched.ToLocalTime():yyyy年M月d日 HH:mm}" : $"Retrieved: {snapshot.Fetched.ToLocalTime():yyyy-MM-dd HH:mm zzz}";
            var delay = japanese ? "反映遅延: 不明" : "Reporting delay: unknown";
            var quantityStatus = japanese ? "総量の内訳を表示しています（使用率ゲージではありません）" : "Gross composition only; this is not a percentage gauge";
            var includedLabel = japanese ? $"含まれる分 {includedRatio:P1} · {includedValueText}" : $"Included {includedRatio:P1} · {includedValueText}";
            var additionalLabel = japanese ? $"追加分 {additionalRatio:P1} · {additionalValueText}" : $"Additional {additionalRatio:P1} · {additionalValueText}";
            var compositionLabel = japanese
                ? $"総量の内訳バー。含まれる分 {includedRatio:P1}、追加分 {additionalRatio:P1}。総量: {grossValueText}。"
                : $"Gross composition bar. Included {includedRatio:P1}, Additional {additionalRatio:P1}. Total: {grossValueText}.";
            var quantityFreshness = $"{freshness} · {period} · {retrieved} · {delay}";
            var quantityProgress = string.Join(language == UiLanguage.Japanese ? "。" : ". ",
                new[] { gross, discount, net, quantityStatus, source, quantityFreshness }.Where(value => !string.IsNullOrWhiteSpace(value)));
            return new QuotaRowViewModel(japanese ? LocalizeWindow(snapshot.Window.Kind) : snapshot.Window.Kind.ToString(), LocalizeMetric(snapshot.Metric, snapshot.Window.Kind, language), 0, string.Empty, quantityStatus, period, source, quantityFreshness, stale, quantityProgress)
            {
                FetchedAt = snapshot.Fetched,
                WindowEnd = snapshot.Window.End,
                FreshUntil = snapshot.FreshUntil,
                ResetAt = snapshot.Window.ResetAt,
                UsedPercent = null,
                Source = snapshot.Source,
                WindowKind = snapshot.Window.Kind,
                Band = UsageBand.Normal,
                Snapshot = snapshot,
                IsQuantityOnly = true,
                IsQuantityCompositionVisible = compositionVisible,
                IncludedCompositionRatio = includedRatio,
                AdditionalCompositionRatio = additionalRatio,
                CompositionBarLabel = compositionLabel,
                CompositionIncludedLabel = includedLabel,
                CompositionAdditionalLabel = additionalLabel,
                QuantityGrossText = gross,
                QuantityDiscountText = discount,
                QuantityNetText = net,
                CompactValueText = gross,
                CompactResetText = FormatCompactReset(snapshot.Window.ResetAt, now, new UiCopy(language)),
                HeaderFreshnessText = language == UiLanguage.Japanese ? $"更新 {FormatAge(snapshot.Fetched, now, language)}" : $"Updated {FormatAge(snapshot.Fetched, now, language)}",
                CurrentLanguage = language
            };
        }
        var percent = snapshot.EffectivePercent;
        var numeric = percent ?? 0m;
        var visual = (double)Math.Clamp(numeric, 0m, 100m);
        var band = percent is null ? UsageBand.Attention : numeric > 100m ? UsageBand.OverLimit : numeric >= 80m ? UsageBand.Danger : numeric >= 60m ? UsageBand.Attention : UsageBand.Normal;
        var isCopilotReferenceRate = snapshot.Provider == ProviderKind.Copilot;
        var percentText = percent is null ? (japanese ? "使用率を表示できません" : "Usage unavailable") : isCopilotReferenceRate ? japanese ? $"使用率 {numeric:0.#}% · {new UiCopy(language).CopilotRateDisclaimer}" : $"{numeric:0.#}% used · {new UiCopy(language).CopilotRateDisclaimer}" : japanese ? numeric > 100m ? $"使用率 {numeric:0.#}%（残り 0%・上限を{numeric - 100m:0.#}%超過）" : $"使用率 {numeric:0.#}%・残り {100m - numeric:0.#}%" : $"{numeric:0.#}% used · {Math.Max(0m, 100m - numeric):0.#}% remaining";
        var status = percent is null ? (japanese ? "データなし・値: —" : "No quota value · value: —") : isCopilotReferenceRate ? new UiCopy(language).CopilotRateDisclaimer : numeric == 100m ? new UiCopy(language).QuotaExhausted : japanese ? band switch { UsageBand.OverLimit => $"上限を{numeric - 100m:0.#}%超過", UsageBand.Danger => "上限間近", UsageBand.Attention => "注意", _ => "余裕あり" } : band switch { UsageBand.OverLimit => $"Over limit by {numeric - 100m:0.#}%", UsageBand.Danger => $"Danger · {numeric:0.#}%", UsageBand.Attention => $"Attention · {numeric:0.#}%", _ => $"Normal · {numeric:0.#}%" };
        var resetPending = snapshot.Window.ResetAt is { } resetAt && resetAt <= now && snapshot.Fetched <= resetAt;
        var copy = new UiCopy(language);
        var displayPercentText = resetPending ? copy.LastObservedBeforeReset(percentText) : stale ? copy.LastObserved(percentText) : percentText;
        var displayStatus = resetPending ? copy.LastObservedBeforeReset(status) : stale ? copy.LastObserved(status) : status;
        var compactValue = isCopilotReferenceRate
            ? percent is null ? copy.CompactUnknownValue : copy.CopilotUsageVsReference(numeric)
            : percent is null ? copy.CompactUnknownValue : copy.CompactQuotaRemaining(Math.Max(0m, 100m - numeric));
        var compactReset = FormatCompactReset(snapshot.Window.ResetAt, now, copy);
        return new QuotaRowViewModel(japanese ? LocalizeWindow(snapshot.Window.Kind) : snapshot.Window.Kind.ToString(), LocalizeMetric(snapshot.Metric, snapshot.Window.Kind, language), visual, displayPercentText, displayStatus, reset, source, freshness, stale, BuildProgressLabel(displayPercentText, displayStatus, reset, freshness, resetPending ? copy.ResetPending : string.Empty, source, language))
        {
            FetchedAt = snapshot.Fetched,
            WindowEnd = snapshot.Window.End,
            FreshUntil = snapshot.FreshUntil,
            ResetAt = snapshot.Window.ResetAt,
            UsedPercent = percent,
            Source = snapshot.Source,
            WindowKind = snapshot.Window.Kind,
            Band = band,
            Snapshot = snapshot,
            IsResetPending = resetPending,
            ResetPendingText = resetPending ? copy.ResetPending : string.Empty,
            CompactValueText = compactValue,
            CompactResetText = compactReset,
            HeaderFreshnessText = headerFreshness,
            CurrentLanguage = language
        };
    }
    private static string FormatQuantity(decimal? value, string label, bool japanese) => value is null ? (japanese ? $"不明 · {label}" : $"Unavailable · {label}") : $"{value.Value.ToString("#,0.##", CultureInfo.InvariantCulture)} {label}";
    private static decimal SafeCompositionRatio(decimal value, decimal gross, decimal upperBound) => value <= 0m || gross <= 0m || upperBound <= 0m ? 0m : value >= gross ? upperBound : Math.Min(value / gross, upperBound);

    public static QuotaRowViewModel Relocalize(QuotaRowViewModel row, DateTimeOffset now, UiLanguage language) => row.Snapshot is { } snapshot ? Format(snapshot, now, language) : row;

    private static string FormatCompactReset(DateTimeOffset? resetAt, DateTimeOffset now, UiCopy copy)
    {
        if (resetAt is null) return copy.CompactResetUnavailable;
        var remaining = resetAt.Value - now;
        if (remaining.TotalSeconds <= 0) return copy.CompactResetPassed;
        if (remaining.TotalDays >= 1) return copy.CompactResetDays((int)remaining.TotalDays);
        if (remaining.TotalHours >= 1) return copy.CompactResetHours((int)remaining.TotalHours, remaining.Minutes);
        return copy.CompactResetMinutes(Math.Max(1, (int)remaining.TotalMinutes));
    }

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
    public static string LocalizeWindow(QuotaWindowKind kind) => kind switch { QuotaWindowKind.Daily => "日次", QuotaWindowKind.Weekly => "週次", QuotaWindowKind.Monthly => "月次", QuotaWindowKind.Rolling => "ローリング", _ => "カスタム" };
    private static string LocalizeMetric(string metric, QuotaWindowKind window, UiLanguage language)
    {
        if (language != UiLanguage.Japanese) return metric;
        if (string.Equals(metric, window.ToString(), StringComparison.OrdinalIgnoreCase) ||
            (window == QuotaWindowKind.Monthly && metric.Equals("monthly", StringComparison.OrdinalIgnoreCase)) ||
            (window == QuotaWindowKind.Weekly && metric.Equals("weekly", StringComparison.OrdinalIgnoreCase)) ||
            (window == QuotaWindowKind.Daily && metric.Equals("daily", StringComparison.OrdinalIgnoreCase)) ||
            (window == QuotaWindowKind.Rolling && metric.Equals("rolling", StringComparison.OrdinalIgnoreCase))) return string.Empty;
        return metric switch { "Messages" => "メッセージ", "Requests" => "リクエスト", "Fast window" => "短時間枠", "monthly" => "月次", "weekly" => "週次", "Codex primary" => "Codexの主要利用枠", "Codex secondary" => "Codexの副利用枠", _ => metric };
    }
    private static string BuildProgressLabel(string percentText, string status, string reset, string freshness, string resetPending, string source, UiLanguage language)
    {
        var separator = language == UiLanguage.Japanese ? "。" : ". ";
        return string.Join(separator, new[] { percentText, status, resetPending, reset, source, freshness }.Where(value => !string.IsNullOrWhiteSpace(value)));
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
        return DashboardAggregation.ToCards(snapshots, clock.GetUtcNow(), UiLanguage.English);
    }
}
public static class QuotaWindowOrdering
{
    public static int FallbackRank(QuotaWindowKind kind) => kind switch
    {
        QuotaWindowKind.Daily => 0,
        QuotaWindowKind.Rolling => 1,
        QuotaWindowKind.Weekly => 2,
        QuotaWindowKind.Monthly => 3,
        QuotaWindowKind.Custom => 4,
        _ => 5
    };

    public static IEnumerable<QuotaSnapshot> OrderSnapshots(IEnumerable<QuotaSnapshot> snapshots) => snapshots
        .OrderBy(snapshot => IsValid(snapshot.Window) ? 0 : 1)
        .ThenBy(snapshot => FallbackRank(snapshot.Window.Kind))
        .ThenBy(snapshot => IsValid(snapshot.Window) ? snapshot.Window.End - snapshot.Window.Start : TimeSpan.MaxValue)
        .ThenBy(snapshot => snapshot.Window.Start)
        .ThenBy(snapshot => snapshot.Window.End)
        .ThenBy(snapshot => snapshot.Metric, StringComparer.Ordinal);

    public static IEnumerable<QuotaRowViewModel> OrderRows(IEnumerable<QuotaRowViewModel> rows) => rows
        .OrderBy(row => row.Snapshot is { } snapshot && IsValid(snapshot.Window) ? 0 : 1)
        .ThenBy(row => FallbackRank(row.WindowKind))
        .ThenBy(row => row.Snapshot is { } snapshot && IsValid(snapshot.Window) ? snapshot.Window.End - snapshot.Window.Start : TimeSpan.MaxValue)
        .ThenBy(row => row.Snapshot?.Window.Start ?? DateTimeOffset.MaxValue)
        .ThenBy(row => row.Snapshot?.Window.End ?? DateTimeOffset.MaxValue)
        .ThenBy(row => row.Metric, StringComparer.Ordinal);

    private static bool IsValid(QuotaWindow window) => window.End > window.Start;
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
                QuotaWindowOrdering.OrderSnapshots(windows)
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
            return new ProviderCardViewModel(card.Item1, card.Item2, card.Item3, card.Item4, index == 1 ? "Needs attention" : index == 2 ? "Over limit" : "Healthy", true, QuotaWindowOrdering.OrderRows([QuotaPresentationFormatter.Format(first, now), QuotaPresentationFormatter.Format(second, now)]).ToList());
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
    public string GithubOrganization { get; set; } = string.Empty;
    public string UsageApiEndpoint { get; set; } = string.Empty;
    public decimal? CopilotReferenceCredits { get; set; }
    public bool TrySetCopilotReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { CopilotReferenceCredits = null; return true; }
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0) return false;
        CopilotReferenceCredits = parsed; return true;
    }
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
    public decimal? GrossQuantity { get; init; }
    public decimal? DiscountQuantity { get; init; }
    public decimal? NetQuantity { get; init; }
    public string Unit { get; init; } = string.Empty;
    public DateTimeOffset? PeriodStart { get; init; }
    public DateTimeOffset? PeriodEnd { get; init; }
    public string QuantityGrossText => GrossQuantity is { } value ? value.ToString("#,0.###", CultureInfo.InvariantCulture) : string.Empty;
    public string QuantityDiscountText => DiscountQuantity is { } value ? value.ToString("#,0.###", CultureInfo.InvariantCulture) : string.Empty;
    public string QuantityNetText => NetQuantity is { } value ? value.ToString("#,0.###", CultureInfo.InvariantCulture) : string.Empty;
    public bool IsQuantity => GrossQuantity is not null || DiscountQuantity is not null || NetQuantity is not null;
    public bool IsPercent => !IsQuantity;
    public string QuantityGrossLabel { get; init; } = "Gross";
    public string QuantityDiscountLabel { get; init; } = "Included";
    public string QuantityNetLabel { get; init; } = "Net";
    public string QuantityUnitLabel { get; init; } = "Unit";
    public string QuantityGrossDisplay => $"{QuantityGrossLabel}: {QuantityGrossText}";
    public string QuantityDiscountDisplay => $"{QuantityDiscountLabel}: {QuantityDiscountText}";
    public string QuantityNetDisplay => $"{QuantityNetLabel}: {QuantityNetText}";
    public string QuantityUnitDisplay => $"{QuantityUnitLabel}: {Unit}";
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
    private readonly TimeProvider clock;
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
    internal HistoryState(IQuotaHistory? history, IUiDispatcher? uiDispatcher, IHistoryProjectionScheduler? projectionScheduler, TimeProvider? clock = null)
    {
        persistentHistory = history;
        this.clock = clock ?? TimeProvider.System;
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
                foreach (var item in await persistentHistory!.ReadEventsAsync(DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.AddDays(-i)), token).ConfigureAwait(false))
                    entries.Add(ToEntry(item.EventId, item.Snapshot));
            }
            catch (InvalidDataException) { error = "History data is damaged; showing available entries."; }
        }
        return (entries, error);
    }

    private static HistoryUiEntry ToEntry(Guid id, QuotaSnapshot snapshot)
    {
        var entry = new HistoryUiEntry(id, snapshot.DisplayName.Length == 0 ? snapshot.Provider.ToString() : snapshot.DisplayName, snapshot.Account, snapshot.Window.Kind.ToString(), snapshot.EffectivePercent, snapshot.Observed, snapshot.Source, snapshot.Confidence)
        {
            Unit = snapshot.Unit,
            PeriodStart = snapshot.Window.Start,
            PeriodEnd = snapshot.Window.End
        };
        return snapshot.CopilotUsage is { } usage ? entry with { GrossQuantity = usage.GrossQuantity, DiscountQuantity = usage.DiscountQuantity, NetQuantity = usage.NetQuantity } : entry;
    }

    private static HistorySnapshotState Build(HistorySnapshotState input)
    {
        var filtered = input.Raw.Where(e => (input.AccountFilter == "All accounts" || e.Account == input.AccountFilter) && (input.WindowFilter == "All windows" || e.Window == input.WindowFilter)).Select(e => e with
        {
            WindowDisplay = input.Language == UiLanguage.Japanese && Enum.TryParse<QuotaWindowKind>(e.Window, true, out var kind) ? QuotaPresentationFormatter.LocalizeWindow(kind) : e.Window,
            QuantityGrossLabel = input.Language == UiLanguage.Japanese ? "総量" : "Gross",
            QuantityDiscountLabel = input.Language == UiLanguage.Japanese ? "含まれる分" : "Included",
            QuantityNetLabel = input.Language == UiLanguage.Japanese ? "正味" : "Net",
            QuantityUnitLabel = input.Language == UiLanguage.Japanese ? "単位" : "Unit"
        }).ToArray();
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
                if (entry.GrossQuantity is { } gross) writer.WriteNumber("grossQuantity", gross); else writer.WriteNull("grossQuantity");
                if (entry.DiscountQuantity is { } discount) writer.WriteNumber("discountQuantity", discount); else writer.WriteNull("discountQuantity");
                if (entry.NetQuantity is { } net) writer.WriteNumber("netQuantity", net); else writer.WriteNull("netQuantity");
                writer.WriteString("unit", entry.Unit);
                if (entry.PeriodStart is { } periodStart) writer.WriteString("periodStart", periodStart.ToString("O", CultureInfo.InvariantCulture));
                if (entry.PeriodEnd is { } periodEnd) writer.WriteString("periodEnd", periodEnd.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("observed", entry.Observed.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("source", entry.Source.ToString());
                writer.WriteString("confidence", entry.Confidence.ToString());
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
    private static string SerializeCsv(IReadOnlyList<HistoryUiEntry> entries)
    {
        if (!entries.Any(entry => entry.GrossQuantity is not null || entry.DiscountQuantity is not null || entry.NetQuantity is not null))
            return "Provider,Account,Window,UsedPercent,Observed,Source,Confidence\n" + string.Join("\n", entries.Select(e => $"{EscapeCsv(e.Provider)},{EscapeCsv(e.Account)},{EscapeCsv(e.Window)},{(e.UsedPercent?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)},{e.Observed.ToString("O", CultureInfo.InvariantCulture)},{e.Source},{e.Confidence}"));
        var rows = entries.Select(e => $"{EscapeCsv(e.Provider)},{EscapeCsv(e.Account)},{EscapeCsv(e.Window)},{(e.UsedPercent?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)},{(e.GrossQuantity?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)},{(e.DiscountQuantity?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)},{(e.NetQuantity?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)},{EscapeCsv(e.Unit)},{e.PeriodStart?.ToString("O", CultureInfo.InvariantCulture)},{e.PeriodEnd?.ToString("O", CultureInfo.InvariantCulture)},{e.Observed.ToString("O", CultureInfo.InvariantCulture)},{e.Source},{e.Confidence}").ToArray();
        return "Provider,Account,Window,UsedPercent,GrossQuantity,DiscountQuantity,NetQuantity,Unit,PeriodStart,PeriodEnd,Observed,Source,Confidence\n" + string.Join("\n", rows);
    }
    private static string EscapeCsv(string value) => value.Any(character => character is ',' or '"' or '\r' or '\n') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
public interface IInAppNotificationService { string BannerText { get; } void Notify(string title, string reason); void NotifyLocalized(Func<UiCopy, (string Title, string Reason)> copy); }
public sealed class InAppNotificationService : IInAppNotificationService, INotificationSink, INotifyPropertyChanged
{
    private readonly IDesktopNotificationBackend? desktopNotificationBackend;
    private string thresholdBannerText = string.Empty;
    private QuotaSnapshot? thresholdSnapshot;
    private decimal? pendingThreshold;
    private decimal thresholdPercent;
    private Func<UiCopy, (string Title, string Reason)>? localizedBanner;
    private enum BannerKind { None, Opaque, Localized, Threshold }
    private enum BannerOwner { Other, Refresh }
    private BannerKind visibleKind;
    private BannerOwner visibleOwner = BannerOwner.Other;
    public UiLanguage Language { get => language; set { if (language == value) return; language = value; if (visibleKind == BannerKind.Localized) SetLocalizedBanner(); else if (visibleKind == BannerKind.Threshold) { RefreshThresholdText(); SetBannerText(thresholdBannerText); } } }
    private UiLanguage language = UiLanguage.English;
    public string BannerText { get; private set; } = string.Empty;
    public event PropertyChangedEventHandler? PropertyChanged;
    public InAppNotificationService(IDesktopNotificationBackend? desktopNotificationBackend = null) => this.desktopNotificationBackend = desktopNotificationBackend;
    // Arbitrary caller-provided strings are intentionally opaque; only known UI-owned alerts use localized payloads.
    public void Notify(string title, string reason) { localizedBanner = null; visibleKind = BannerKind.Opaque; visibleOwner = BannerOwner.Other; SetBannerText($"{title}: {reason}"); }
    public void NotifyLocalized(Func<UiCopy, (string Title, string Reason)> copy) => SetLocalizedBanner(copy, BannerOwner.Other);
    public void NotifyRefreshLocalized(Func<UiCopy, (string Title, string Reason)> copy) => SetLocalizedBanner(copy, BannerOwner.Refresh);
    private void SetLocalizedBanner(Func<UiCopy, (string Title, string Reason)> copy, BannerOwner owner) { localizedBanner = copy; visibleKind = BannerKind.Localized; visibleOwner = owner; SetLocalizedBanner(); }
    private void SetLocalizedBanner() { var (title, reason) = localizedBanner!(new UiCopy(Language)); SetBannerText($"{title}: {reason}"); }
    public void Clear() { localizedBanner = null; thresholdBannerText = string.Empty; thresholdSnapshot = null; visibleKind = BannerKind.None; visibleOwner = BannerOwner.Other; SetBannerText(string.Empty); }
    public void ClearThresholdBanner()
    {
        var wasVisible = visibleKind == BannerKind.Threshold;
        thresholdBannerText = string.Empty;
        thresholdSnapshot = null;
        if (wasVisible) { visibleKind = BannerKind.None; visibleOwner = BannerOwner.Other; SetBannerText(string.Empty); }
    }
    public void ClearRefreshIfOwned()
    {
        if (visibleKind != BannerKind.Localized || visibleOwner != BannerOwner.Refresh) return;
        localizedBanner = null;
        visibleKind = BannerKind.None;
        visibleOwner = BannerOwner.Other;
        SetBannerText(string.Empty);
    }
    public void SetThresholdContext(decimal threshold) => pendingThreshold = threshold;
    public void ReconcileThresholdBanner(IReadOnlyList<QuotaSnapshot> snapshots, DateTimeOffset now, Func<ProviderKind, decimal> thresholdForProvider, bool enabled)
    {
        if (!enabled) { ClearThresholdBanner(); return; }
        if (thresholdSnapshot is not { } current) return;
        var threshold = thresholdForProvider(current.Provider);
        if (thresholdPercent > 0 && thresholdPercent != threshold)
        {
            ClearThresholdBanner();
            return;
        }

        var replacement = snapshots
            .Where(snapshot => snapshot.Provider == current.Provider && snapshot.Account == current.Account && snapshot.Metric == current.Metric && snapshot.Window.Kind == current.Window.Kind)
            .OrderByDescending(snapshot => snapshot.Fetched)
            .FirstOrDefault();
        if (replacement is null)
        {
            if (current.IsStale(now) || current.Window.End <= now) ClearThresholdBanner();
            return;
        }

        if (replacement.IsStale(now) || replacement.Window.End <= now || replacement.EffectivePercent is not { } percent || percent < threshold)
        {
            ClearThresholdBanner();
            return;
        }

        thresholdSnapshot = replacement;
        thresholdPercent = threshold;
        RefreshThresholdText();
        if (visibleKind == BannerKind.Threshold) SetBannerText(thresholdBannerText);
    }
    private void RefreshThresholdText()
    {
        if (thresholdSnapshot?.EffectivePercent is not { } percent) { thresholdBannerText = string.Empty; return; }
        var copy = new UiCopy(Language);
        thresholdBannerText = $"{copy.QuotaThresholdTitle(thresholdSnapshot.Provider)}: {copy.QuotaThresholdReason(percent)}";
    }
    public void RestoreThresholdBanner()
    {
        if (thresholdSnapshot is null)
        {
            if (visibleKind == BannerKind.Threshold)
            {
                visibleKind = BannerKind.None;
                visibleOwner = BannerOwner.Other;
                SetBannerText(string.Empty);
            }
            return;
        }
        if (visibleKind is not (BannerKind.None or BannerKind.Threshold) && !(visibleKind == BannerKind.Localized && visibleOwner == BannerOwner.Refresh)) return;
        RefreshThresholdText();
        visibleKind = BannerKind.Threshold;
        visibleOwner = BannerOwner.Other;
        localizedBanner = null;
        SetBannerText(thresholdBannerText);
    }
    public async ValueTask<bool> NotifyAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.EffectivePercent is not { } percent) return true;
        var copy = new UiCopy(Language);
        var title = copy.QuotaThresholdTitle(snapshot.Provider);
        var reason = copy.QuotaThresholdReason(percent);
        thresholdBannerText = $"{title}: {reason}";
        thresholdSnapshot = snapshot;
        thresholdPercent = pendingThreshold ?? 0;
        pendingThreshold = null;
        localizedBanner = null;
        visibleKind = BannerKind.Threshold;
        visibleOwner = BannerOwner.Other;
        SetBannerText(thresholdBannerText);
        if (desktopNotificationBackend is null) return true;
        try { return await desktopNotificationBackend.TryNotifyAsync(title, reason, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { /* Keep the in-app banner as the delivery fallback. */ return false; }
    }
    private void SetBannerText(string text)
    {
        if (BannerText == text) return;
        BannerText = text;
        PropertyChanged?.Invoke(this, new(nameof(BannerText)));
    }
}

public sealed record ProviderConnectionResult(bool Success, string Message, FetchStatus Status = FetchStatus.Success);
public sealed record GitHubDeviceFlowResult(bool Success, FetchStatus Status, string? Error = null)
{
    public bool IsSuccess => Success;
}
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
    ValueTask<GitHubDeviceFlowResult> PollGitHubDeviceFlowAsync(DeviceAuthorizationStart authorization, CancellationToken cancellationToken);
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
    private readonly GitHubUserTokenSession? githubTokenSession;
    private readonly TimeProvider timeProvider;
    private CodexDeviceAuthorization? codexAuthorization;
    private DateTimeOffset nextCodexPollAt;
    private readonly InMemoryCredentialStore sessionCredentials = new();
    private Func<IReadOnlyList<QuotaSnapshot>, ValueTask>? openCodeSuccess;
    public CredentialStoreAvailability CredentialAvailability => credentialStore?.Availability ?? CredentialStoreAvailability.Unavailable;
    public UiProviderFacade(Func<string, IQuotaAdapter>? openCodeAdapterFactory = null, GitHubDeviceFlowClient? github = null, ICredentialStore? credentialStore = null, GhCliProbe? ghProbe = null, IGitHubClientFactory? githubFactory = null, Func<IReadOnlyList<QuotaSnapshot>, ValueTask>? openCodeSuccess = null, CodexSessionManager? codexSessionManager = null, TimeProvider? timeProvider = null, GitHubUserTokenSession? githubTokenSession = null)
    {
        this.openCodeAdapterFactory = openCodeAdapterFactory ?? (key => new OpenCodeGoAdapter(new HttpClient(), key));
        this.github = github; this.credentialStore = credentialStore; this.ghProbe = ghProbe ?? new GhCliProbe(); this.githubFactory = githubFactory; this.openCodeSuccess = openCodeSuccess; this.codexSessionManager = codexSessionManager; this.timeProvider = timeProvider ?? TimeProvider.System; this.githubTokenSession = githubTokenSession;
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
    public async ValueTask<GitHubDeviceFlowResult> PollGitHubDeviceFlowAsync(DeviceAuthorizationStart authorization, CancellationToken cancellationToken)
    {
        if (github is null) return new(false, FetchStatus.Unsupported, "GitHub Client ID is not configured.");
        var result = await github.PollTokensAsync(authorization, cancellationToken);
        if (result.IsSuccess && result.Value is { } tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (githubTokenSession is not null)
            {
                var saveResult = await githubTokenSession.SaveDeviceFlowTokensAsync(tokens, cancellationToken, authorization.ClientId);
                if (!saveResult.IsSuccess) return new(false, saveResult.Status, saveResult.Error);
            }
            else if (credentialStore is not null)
                await credentialStore.SetAsync("github", tokens.AccessToken, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(true, FetchStatus.Success);
        }
        return new(false, result.Status, result.Error);
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
    private readonly ReconfigurableUsageReadPort usageReadPort;
    private readonly StoreUsageCredentialProvider? usageCredentials;
    private readonly Func<HttpClient>? usageHttpClientFactory;
    private string usageApiEndpoint = string.Empty;
    private bool hasUsageCredential;
    private UsageConnectionState usageConnectionState = UsageConnectionState.Unconfigured;
    private UsageConnectionError usageConnectionError = UsageConnectionError.None;
    private CancellationTokenSource? usageRequest;
    private long usageRequestId;
    private string usageState = "Unconfigured";
    private string usageTotal = string.Empty;
    private IReadOnlyList<UsageDay> usageRows = [];
    private Task usageLoadTask = Task.CompletedTask;
    private readonly HashSet<string> openCodeAccounts = new(StringComparer.Ordinal);
    private AppPage currentPage = AppPage.Dashboard;
    private UiLanguage language = UiLanguage.English;
    private CredentialStoreAvailability credentialAvailability = CredentialStoreAvailability.Unavailable;
    private PresentationState presentationState = PresentationState.Ready;
    public ObservableCollection<ProviderCardViewModel> Cards { get; } = [];
    public string SummaryAccountsText => new UiCopy(Language).SummaryAccounts(Cards.Count);
    private int ReviewableCardCount => Cards.Count(card => card.Windows.Any(row =>
        row.IsStale || row.IsResetPending || row.Band is UsageBand.Danger or UsageBand.OverLimit or UsageBand.Attention));
    public string SummaryReviewText => new UiCopy(Language).SummaryReview(ReviewableCardCount);
    public string SummaryFreshnessText => Cards.SelectMany(card => card.Windows)
        .OrderByDescending(row => row.FetchedAt)
        .Select(row => row.HeaderFreshnessText)
        .FirstOrDefault(text => text.Length > 0) ?? string.Empty;
    public string ConnectedAccountsHead => new UiCopy(Language).ConnectedAccounts;
    public bool HasReviewableCards => ReviewableCardCount > 0;
    public bool HasFreshnessChip => SummaryFreshnessText.Length > 0;
    public SettingsState Settings { get; } = new();
    public HistoryState History { get; }

    public UiCopy CopyText => new(Language);
    public string NavDashboardText => CopyText.Dashboard;
    public string NavUsageText => Language == UiLanguage.Japanese ? "利用量" : "Token usage";
    public string NavHistoryText => CopyText.History;
    public string NavSettingsText => CopyText.Settings;
    public string RefreshText => CopyText.Refresh;
    public string QuotaStatusText => IsQuotaDataBusy ? CopyText.QuotaRefreshing : string.Empty;
    public bool IsQuotaDataBusy => quotaDataBusy;
    public bool CanRefresh => !IsQuotaDataBusy;
    public string HeaderTitle => CurrentPage switch { AppPage.Usage => NavUsageText, AppPage.History => CopyText.History, AppPage.Settings => CopyText.Settings, _ => CopyText.Dashboard };
    public bool IsUsageVisible => CurrentPage == AppPage.Usage;
    public string UsageStateText => new UiCopy(Language).UsageState(usageState);
    public string UsageStatusText => UsageStateText;
    public IReadOnlyList<UsageDay> UsageRows => usageRows;
    public string UsageTotalText => usageTotal;
    public bool IsUsageTotalVisible => usageState == "Ready" && usageRows.Count > 0;
    public string UsageRangeText => usageState == "Unconfigured" ? string.Empty : $"{usageFrom:yyyy-MM-dd} – {usageTo:yyyy-MM-dd} · Asia/Tokyo";
    public bool IsUsageRangeVisible => usageState != "Unconfigured";
    public bool IsUsageLoading => usageState == "Loading";
    public bool CanRefreshUsage => !IsUsageLoading;
    public Task UsageLoadTask => usageLoadTask;
    private DateOnly usageFrom, usageTo;
    public void NavigateToUsage() { CurrentPage = AppPage.Usage; OnPropertyChanged(nameof(NavUsageText)); }
    public Task RefreshUsageAsync() { if (CurrentPage == AppPage.Usage) usageLoadTask = LoadUsageAsync(); return usageLoadTask; }
    private async Task LoadUsageAsync()
    {
        var id = Interlocked.Increment(ref usageRequestId);
        usageRequest?.Cancel(); usageRequest?.Dispose();
        usageRequest = new CancellationTokenSource(); var token = usageRequest.Token;
        usageState = usageReadPort.IsConfigured ? "Loading" : "Unconfigured";
        if (usageState != "Unconfigured")
        {
            usageRows = [];
            usageTotal = string.Empty;
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo")).DateTime);
            usageFrom = today.AddDays(-29); usageTo = today;
            OnPropertyChanged(nameof(UsageRangeText));
            OnPropertyChanged(nameof(UsageRows));
            OnPropertyChanged(nameof(UsageTotalText));
        }
        OnPropertyChanged(nameof(UsageRangeText));
        OnPropertyChanged(nameof(IsUsageRangeVisible));
        OnPropertyChanged(nameof(IsUsageTotalVisible));
        OnPropertyChanged(nameof(UsageStateText)); OnPropertyChanged(nameof(UsageStatusText)); OnPropertyChanged(nameof(CanRefreshUsage)); OnPropertyChanged(nameof(IsUsageLoading));
        if (usageState == "Unconfigured") return;
        try
        {
            var result = await usageReadPort.ReadDailyAsync(usageFrom, usageTo, null, token);
            if (token.IsCancellationRequested || id != Volatile.Read(ref usageRequestId) || CurrentPage != AppPage.Usage) return;
            usageRows = result.Days; usageTotal = $"{result.TotalTokens:N0}"; usageState = result.Days.Count == 0 ? "Empty" : "Ready";
            OnPropertyChanged(nameof(UsageRows)); OnPropertyChanged(nameof(UsageTotalText)); OnPropertyChanged(nameof(IsUsageTotalVisible)); OnPropertyChanged(nameof(UsageStateText)); OnPropertyChanged(nameof(UsageStatusText)); OnPropertyChanged(nameof(CanRefreshUsage)); OnPropertyChanged(nameof(IsUsageLoading));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { if (id == Volatile.Read(ref usageRequestId) && CurrentPage == AppPage.Usage) { usageState = "Error"; OnPropertyChanged(nameof(IsUsageTotalVisible)); OnPropertyChanged(nameof(UsageStateText)); OnPropertyChanged(nameof(UsageStatusText)); OnPropertyChanged(nameof(CanRefreshUsage)); OnPropertyChanged(nameof(IsUsageLoading)); } }
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

    /// <summary>Fixed, localized sentence for <see cref="UsageConnectionState"/>; it names no endpoint or identity.</summary>
    public string UsageConnectionStatusText => CopyText.UsageConnectionStatus(usageConnectionState);

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
            PublishUsageConnectionChanged();
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
        var origin = new Uri(endpoint, UriKind.Absolute);
        var client = usageHttpClientFactory?.Invoke()
            ?? UsageReadTransport.CreateClient(HttpUsageReadPort.DefaultRequestDeadline + TimeSpan.FromSeconds(15));
        try
        {
            usageReadPort.Configure(new HttpUsageReadPort(client, origin, StoreUsageCredentialProvider.IdentityId, usageCredentials), client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
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
    private bool copilotReauthenticationAvailable;
    public bool IsCopilotReauthenticationAvailable => copilotReauthenticationAvailable;
    private bool isCopilotSessionOnly;
    public bool IsCopilotSessionOnly => isCopilotSessionOnly;
    public string CopilotSessionStorageNotice => CopyText.CopilotSessionOnlyStorage;
    public void SetCopilotCredentialAvailability(CredentialStoreAvailability availability, bool authenticationSucceeded)
    {
        isCopilotSessionOnly = authenticationSucceeded && availability != CredentialStoreAvailability.SecureStore;
        OnPropertyChanged(nameof(IsCopilotSessionOnly));
        OnPropertyChanged(nameof(CopilotSessionStorageNotice));
    }
    private void SetCopilotReauthenticationAvailable(bool available)
    {
        if (copilotReauthenticationAvailable == available) return;
        copilotReauthenticationAvailable = available;
        OnPropertyChanged(nameof(IsCopilotReauthenticationAvailable));
    }
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
    public AppPage CurrentPage { get => currentPage; private set { if (currentPage == value) return; var wasUsage = currentPage == AppPage.Usage; if (wasUsage) { Interlocked.Increment(ref usageRequestId); usageRequest?.Cancel(); } if (Set(ref currentPage, value) && value == AppPage.Usage && !wasUsage) usageLoadTask = LoadUsageAsync(); } }
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
    private readonly InAppNotificationService notificationService;
    private readonly NotificationDeduplicator notificationDeduplicator;
    private readonly List<QuotaSnapshot> lastKnownSnapshots = [];
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly SemaphoreSlim settingsTransactionGate = new(1, 1);
    internal Func<AppSettingsDto, CancellationToken, ValueTask>? SettingsSaveAsyncForTests { get; set; }
    private ValueTask SaveSettingsAsync(AppSettingsDto settings, CancellationToken cancellationToken) =>
        SettingsSaveAsyncForTests?.Invoke(settings, cancellationToken) ?? settingsStore!.SaveAsync(settings, cancellationToken);
    private bool quotaDataBusy;
    private readonly IUiDispatcher uiDispatcher;
    private readonly IQuotaWorkScheduler workScheduler;
    private int disposedState;

    private bool IsDisposed => Volatile.Read(ref disposedState) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposedState, 1) != 0) return;
        usageRequest?.Cancel(); usageRequest?.Dispose(); usageRequest = null;
        usageReadPort.Dispose();
        // RefreshAsync may still be waiting on or holding the gate; leave it undisposed to avoid a race.
        History.Dispose();
        (quotaHistory as IDisposable)?.Dispose();
    }
    public MainViewModel(IDashboardSource source, IManualQuotaService? manualQuotaService = null, IQuotaHistory? quotaHistory = null, TimeProvider? timeProvider = null, AppSettingsStore? settingsStore = null, IGitHubClientFactory? githubFactory = null, IQuotaApplication? quotaApplication = null, IUiDispatcher? uiDispatcher = null, IQuotaWorkScheduler? workScheduler = null, IDesktopNotificationBackend? desktopNotificationBackend = null, IUsageReadPort? usageReadPort = null, ReconfigurableUsageReadPort? usageConnectionPort = null, ICredentialStore? usageCredentialStore = null, Func<HttpClient>? usageHttpClientFactory = null) { this.source = source; this.usageReadPort = usageConnectionPort ?? new ReconfigurableUsageReadPort(); if (usageReadPort is not null) this.usageReadPort.Configure(usageReadPort); this.usageCredentials = usageCredentialStore is null ? null : new StoreUsageCredentialProvider(usageCredentialStore); this.usageHttpClientFactory = usageHttpClientFactory; this.manualQuotaService = manualQuotaService; this.quotaHistory = quotaHistory; this.timeProvider = timeProvider ?? TimeProvider.System; this.settingsStore = settingsStore; this.githubFactory = githubFactory; this.quotaApplication = quotaApplication; this.uiDispatcher = uiDispatcher ?? new AvaloniaUiDispatcher(); this.workScheduler = workScheduler ?? new TaskRunQuotaWorkScheduler(); notificationService = new InAppNotificationService(desktopNotificationBackend); notificationDeduplicator = new NotificationDeduplicator(notificationService); notificationService.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(NotificationBannerText)); OnPropertyChanged(nameof(IsNotificationVisible)); }; History = new HistoryState(quotaHistory, this.uiDispatcher, projectionScheduler: null, clock: this.timeProvider); LoadCards(); }
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        SetQuotaDataBusy(true);
        try
        {
            if (settingsStore is not null) SetSettings(await workScheduler.RunAsync(() => settingsStore.LoadAsync(cancellationToken).AsTask()), persist: false);
            await RefreshUsageConnectionAsync(cancellationToken);
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
            if (History.LoadError is not null) notificationService.NotifyLocalized(copy => (copy.HistoryNotificationTitle, copy.HistoryCorrupt));

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
            Settings.TrySetThreshold(dto.OverallThreshold, out _);
            Settings.NotificationsEnabled = dto.NotificationsEnabled;
            if (!Settings.NotificationsEnabled) notificationService.ClearThresholdBanner();
            Settings.ResidentMode = dto.ResidentMode;
            Settings.ProviderOverrides.Clear();
            if (dto.ProviderThresholds is not null)
                foreach (var pair in dto.ProviderThresholds)
                    if (Enum.TryParse<ProviderKind>(pair.Key, true, out var provider)) Settings.ProviderOverrides[provider] = pair.Value;
            Settings.GithubOAuthClientId = dto.GithubOAuthClientId;
            Settings.GithubOrganization = dto.GithubOrganization.Trim();
            Settings.UsageApiEndpoint = (dto.UsageApiEndpoint ?? string.Empty).Trim();
            Settings.CopilotReferenceCredits = dto.CopilotReferenceCredits is > 0 ? dto.CopilotReferenceCredits : null;
            Language = dto.Language == "Japanese" ? UiLanguage.Japanese : UiLanguage.English;
            Settings.Theme = Enum.TryParse<ThemeMode>(dto.Theme, true, out var theme) ? theme : ThemeMode.System;
            UiSettings.ApplyTheme(Settings.Theme);
            githubFactory?.Create(dto.GithubOAuthClientId);
            if (persist) settingsStore?.Save(CurrentSettings());
            RefreshCopilotReferences();
        }
        finally { settingsTransactionGate.Release(); }
    }
    private AppSettingsDto CurrentSettings() => new(Settings.Theme.ToString(), Language == UiLanguage.Japanese ? "Japanese" : "English", Settings.RefreshMinutes, Settings.OverallThreshold, Settings.NotificationsEnabled, Settings.GithubOAuthClientId, Settings.ProviderOverrides.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value), Settings.ResidentMode, Settings.GithubOrganization, Settings.CopilotReferenceCredits, Settings.UsageApiEndpoint);
    public async Task<bool> SetCopilotReferenceAsync(string? value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = new SettingsState();
        if (!candidate.TrySetCopilotReference(value)) return false;
        if (IsDisposed) return false;

        await settingsTransactionGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDisposed) return false;

            var settings = CurrentSettings() with { CopilotReferenceCredits = candidate.CopilotReferenceCredits };
            if (settingsStore is not null)
                await SaveSettingsAsync(settings, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (IsDisposed) return false;

            Settings.CopilotReferenceCredits = candidate.CopilotReferenceCredits;
            RefreshCopilotReferences();
            return true;
        }
        finally { settingsTransactionGate.Release(); }
    }
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
    public Task<bool> SetRefreshMinutesAsync(int value, CancellationToken cancellationToken = default) => UpdateSettingsAsync(settings =>
    {
        var valid = UiSettings.IsRefreshIntervalValid(TimeSpan.FromMinutes(value));
        return (valid ? settings with { RefreshMinutes = value } : settings, valid, valid);
    }, () => Settings.TrySetRefreshMinutes(value), cancellationToken);
    public Task SetNotificationsAsync(bool value, CancellationToken cancellationToken = default) => UpdateSettingsAsync(settings => (settings with { NotificationsEnabled = value }, true, true), () =>
    {
        Settings.NotificationsEnabled = value;
        if (!value) notificationService.ClearThresholdBanner();
    }, cancellationToken);
    public Task SetResidentModeAsync(bool value, CancellationToken cancellationToken = default) => UpdateSettingsAsync(settings => (settings with { ResidentMode = value }, true, true), () =>
    {
        Settings.ResidentMode = value;
        OnPropertyChanged(nameof(Settings));
    }, cancellationToken);
    public void RestoreResidentMode(bool value) { Settings.ResidentMode = value; OnPropertyChanged(nameof(Settings)); }
    public Task<bool> SetThresholdAsync(decimal value, CancellationToken cancellationToken = default) => UpdateSettingsAsync(settings =>
    {
        var valid = value is >= 0 and <= 100;
        return (valid ? settings with { OverallThreshold = value } : settings, valid, valid);
    }, () => Settings.TrySetThreshold(value, out _), cancellationToken);
    public Task SetGithubClientIdAsync(string value, CancellationToken cancellationToken = default) => UpdateSettingsAsync(settings => (settings with { GithubOAuthClientId = value.Trim() }, true, true), () =>
    {
        Settings.GithubOAuthClientId = value.Trim();
        githubFactory?.Create(Settings.GithubOAuthClientId);
    }, cancellationToken);
    public Task SetGithubOrganizationAsync(string value, CancellationToken cancellationToken = default) => UpdateSettingsAsync(settings => (settings with { GithubOrganization = value.Trim() }, true, true), () => Settings.GithubOrganization = value.Trim(), cancellationToken);
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
        SetCopilotReauthenticationAvailable(false);
        ReevaluateCards(timeProvider.GetUtcNow());
        notificationService.NotifyRefreshLocalized(copy => (copy.RefreshIncompleteTitle, copy.RefreshError));
        PresentationState = PresentationState.Error;
    }
    private async Task ApplyRefreshResultAsync(QuotaRefreshResult refreshResult, HashSet<(ProviderKind Provider, string Account, string Metric, QuotaWindowKind Kind)> previousProviderIdentities, CancellationToken cancellationToken)
    {
        // An in-flight refresh released after Dispose must not publish state or notifications.
        if (IsDisposed) return;
        SetCopilotReauthenticationAvailable(refreshResult.Failures.Any(failure => failure.Provider == ProviderKind.Copilot && failure.Status == FetchStatus.Unauthorized));
        var snapshots = refreshResult.Snapshots;
        var currentProviderIdentities = snapshots
            .Where(snapshot => snapshot.Source != QuotaSource.Manual)
            .Select(snapshot => (snapshot.Provider, snapshot.Account, snapshot.Metric, snapshot.Window.Kind))
            .ToHashSet();
        var missingProviders = previousProviderIdentities
            .Where(identity => !currentProviderIdentities.Contains(identity))
            .Where(identity => !IsReplacedIdentity(identity, snapshots, refreshResult.ActiveAccounts))
            .Where(identity => !refreshResult.NoDataProviders.Contains(identity.Provider))
            .Select(identity => identity.Provider)
            .Distinct()
            .ToArray();
        var failures = refreshResult.Failures.ToArray();
        var hasValidatedCopilotNoData = HasValidatedCopilotNoData(refreshResult);
        var noDataProviders = refreshResult.NoDataProviders
            .Where(provider => !hasValidatedCopilotNoData || provider != ProviderKind.Copilot)
            .ToArray();
        var partialFailure = missingProviders.Length > 0;
        RemoveReplacedAutomaticCopilotCards(snapshots, refreshResult.ActiveAccounts);
        if (hasValidatedCopilotNoData)
            ProjectCopilotNoData(refreshResult.RequestedPeriod!);
        var noDataProvidersWithPreviousAutomaticValue = noDataProviders
            .Where(provider => Cards.Any(card => card.Provider == provider && card.Windows.Any(window => window.Snapshot is { Source: not QuotaSource.Manual })))
            .ToHashSet();
        if (snapshots.Count > 0)
        {
            MergeLastKnownSnapshots(snapshots);
            UpsertSnapshotCards(snapshots, timeProvider.GetUtcNow());
            ReevaluateCards(timeProvider.GetUtcNow());
            foreach (var snapshot in snapshots) { var threshold = Settings.ProviderOverrides.GetValueOrDefault(snapshot.Provider, Settings.OverallThreshold); notificationService.SetThresholdContext(threshold); await notificationDeduplicator.ConsiderAsync(snapshot, threshold, timeProvider.GetUtcNow(), cancellationToken, Settings.NotificationsEnabled); }
            // HistoryState's storage lane is the single owner of history mutations: provider
            // snapshots are appended here instead of by the provider application.
            if (quotaHistory is not null) await History.AppendAndReloadAsync(snapshots, cancellationToken);
            if (failures.Length > 0)
            {
                notificationService.NotifyRefreshLocalized(copy => (copy.RefreshIncompleteTitle, copy.RefreshFailures(failures, mixedResult: true)));
            }
            else if (partialFailure)
            {
                notificationService.NotifyRefreshLocalized(copy => (copy.RefreshIncompleteTitle, copy.RefreshMissingProviders(missingProviders)));
            }
            else if (noDataProviders.Length > 0)
            {
                notificationService.NotifyRefreshLocalized(copy => (copy.RefreshIncompleteTitle, copy.RefreshNoData(noDataProviders, noDataProvidersWithPreviousAutomaticValue)));
            }
            else
            {
                notificationService.ReconcileThresholdBanner(snapshots, timeProvider.GetUtcNow(), provider => Settings.ProviderOverrides.GetValueOrDefault(provider, Settings.OverallThreshold), Settings.NotificationsEnabled);
                notificationService.ClearRefreshIfOwned();
                notificationService.RestoreThresholdBanner();
            }
            PresentationState = failures.Length > 0 || partialFailure ? PresentationState.Error : PresentationState.Ready;
        }
        else ReevaluateCards(timeProvider.GetUtcNow());
        if (snapshots.Count == 0)
        {
            if (failures.Length > 0)
            {
                notificationService.NotifyRefreshLocalized(copy => (copy.RefreshIncompleteTitle, copy.RefreshFailures(failures, mixedResult: false)));
            }
            else if (partialFailure)
            {
                notificationService.NotifyRefreshLocalized(copy => (copy.RefreshIncompleteTitle, copy.RefreshMissingProviders(missingProviders)));
            }
            else if (noDataProviders.Length > 0)
            {
                notificationService.NotifyRefreshLocalized(copy => (copy.RefreshIncompleteTitle, copy.RefreshNoData(noDataProviders, noDataProvidersWithPreviousAutomaticValue)));
            }
            else
            {
                notificationService.ReconcileThresholdBanner(snapshots, timeProvider.GetUtcNow(), provider => Settings.ProviderOverrides.GetValueOrDefault(provider, Settings.OverallThreshold), Settings.NotificationsEnabled);
                notificationService.ClearRefreshIfOwned();
                notificationService.RestoreThresholdBanner();
            }
            PresentationState = failures.Length > 0 || partialFailure ? PresentationState.Error : Cards.Count > 0 ? PresentationState.Ready : PresentationState.Empty;
        }
        OnPropertyChanged(nameof(PresentationState));
    }
    public async ValueTask ApplyProviderSnapshotsAsync(IReadOnlyList<QuotaSnapshot> snapshots, CancellationToken cancellationToken = default)
    {
        if (snapshots.Count == 0 || IsDisposed) return;
        if (quotaHistory is not null) await History.AppendAndReloadAsync(snapshots, cancellationToken);
        MergeLastKnownSnapshots(snapshots);
        UpsertSnapshotCards(snapshots, timeProvider.GetUtcNow());
        ReevaluateCards(timeProvider.GetUtcNow());
        foreach (var snapshot in snapshots) { var threshold = Settings.ProviderOverrides.GetValueOrDefault(snapshot.Provider, Settings.OverallThreshold); notificationService.SetThresholdContext(threshold); await notificationDeduplicator.ConsiderAsync(snapshot, threshold, timeProvider.GetUtcNow(), cancellationToken, Settings.NotificationsEnabled); }
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
        var threshold = Settings.ProviderOverrides.GetValueOrDefault(snapshot.Provider, Settings.OverallThreshold);
        notificationService.SetThresholdContext(threshold);
        await notificationDeduplicator.ConsiderAsync(snapshot, threshold, timeProvider.GetUtcNow(), cancellationToken, Settings.NotificationsEnabled);
        notificationService.ReconcileThresholdBanner([snapshot], timeProvider.GetUtcNow(), provider => Settings.ProviderOverrides.GetValueOrDefault(provider, Settings.OverallThreshold), Settings.NotificationsEnabled);
    }
    private void ApplyManualSnapshotUi(QuotaSnapshot snapshot)
    {
        var row = QuotaPresentationFormatter.Format(snapshot, timeProvider.GetUtcNow(), Language);
        var existing = Cards.FirstOrDefault(card => card.Provider == snapshot.Provider && card.Account == snapshot.Account);
        if (existing is not null)
        {
            var windows = QuotaWindowOrdering.OrderRows(existing.Windows
                .Where(item => item.WindowName != row.WindowName || item.Metric != row.Metric)
                .Append(row))
                .ToList();
            Cards[Cards.IndexOf(existing)] = existing with { Windows = windows };
        }
        else
        {
            Cards.Add(new ProviderCardViewModel(snapshot.Provider, snapshot.DisplayName.Length == 0 ? snapshot.Provider.ToString() : snapshot.DisplayName, snapshot.Account, "#405DE6", Language == UiLanguage.Japanese ? "手動" : "Manual", false, [row]));
        }
        OnPropertyChanged(nameof(HasCards)); OnPropertyChanged(nameof(HasEmptyState)); OnPropertyChanged(nameof(IsEmpty));
        RefreshCopilotReferences();
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
            var windows = QuotaWindowOrdering.OrderRows(matches
                .SelectMany(item => item.card.Windows)
                .Where(existing => existing.CopilotNoDataPeriod is null && (existing.WindowName != row.WindowName || existing.Metric != row.Metric))
                .Append(row)
                .GroupBy(existing => (existing.WindowName, existing.Metric))
                .Select(group => group.Last()))
                .ToList();
            Cards[first.index] = first.card with { StateText = Language == UiLanguage.Japanese ? "接続済み" : "Connected", Windows = windows, CopilotNoDataPeriod = null };
            foreach (var duplicate in matches.Skip(1).OrderByDescending(item => item.index)) Cards.RemoveAt(duplicate.index);
        }
        OnPropertyChanged(nameof(HasCards)); OnPropertyChanged(nameof(HasEmptyState)); OnPropertyChanged(nameof(IsEmpty));
        RefreshCopilotReferences();
    }
    private static bool HasValidatedCopilotNoData(QuotaRefreshResult result)
    {
        if (!result.NoDataProviders.Contains(ProviderKind.Copilot) || result.Failures.Any(failure => failure.Provider == ProviderKind.Copilot)) return false;
        if (result.RequestedPeriod is not { } period || period.Year is < 1 or > 9999 || period.Month is < 1 or > 12 ||
            string.IsNullOrWhiteSpace(period.Organization) || string.IsNullOrWhiteSpace(period.User)) return false;
        var account = $"{period.Organization}/{period.User}";
        return result.ActiveAccounts.TryGetValue(ProviderKind.Copilot, out var activeAccount) &&
            string.Equals(activeAccount, account, StringComparison.Ordinal);
    }

    private static bool IsReplacedIdentity((ProviderKind Provider, string Account, string Metric, QuotaWindowKind Kind) identity, IReadOnlyList<QuotaSnapshot> snapshots, IReadOnlyDictionary<ProviderKind, string> activeAccounts)
    {
        if (!activeAccounts.TryGetValue(identity.Provider, out var activeAccount) || string.IsNullOrWhiteSpace(activeAccount) || string.Equals(identity.Account, activeAccount, StringComparison.Ordinal)) return false;
        return snapshots.Any(snapshot => snapshot.Provider == identity.Provider && snapshot.Source != QuotaSource.Manual && string.Equals(snapshot.Account, activeAccount, StringComparison.Ordinal));
    }

    private void RemoveReplacedAutomaticCopilotCards(IReadOnlyList<QuotaSnapshot> snapshots, IReadOnlyDictionary<ProviderKind, string> activeAccounts)
    {
        var observedAccounts = snapshots.Where(snapshot => snapshot.Provider == ProviderKind.Copilot && snapshot.Source != QuotaSource.Manual)
            .Select(snapshot => snapshot.Account).ToHashSet(StringComparer.Ordinal);
        var currentAccount = activeAccounts.GetValueOrDefault(ProviderKind.Copilot);
        if (!string.IsNullOrWhiteSpace(currentAccount)) observedAccounts.Add(currentAccount);
        if (observedAccounts.Count == 0) return;
        var replacedAccounts = lastKnownSnapshots
            .Where(snapshot => snapshot.Provider == ProviderKind.Copilot && snapshot.Source != QuotaSource.Manual)
            .Where(snapshot => !observedAccounts.Contains(snapshot.Account))
            .Select(snapshot => snapshot.Account)
            .Concat(Cards
                .Where(card => card.Provider == ProviderKind.Copilot && card.CopilotNoDataPeriod is not null)
                .Where(card => !observedAccounts.Contains(card.Account))
                .Select(card => card.Account))
            .ToHashSet(StringComparer.Ordinal);
        if (replacedAccounts.Count == 0) return;
        lastKnownSnapshots.RemoveAll(snapshot => snapshot.Provider == ProviderKind.Copilot && snapshot.Source != QuotaSource.Manual && replacedAccounts.Contains(snapshot.Account));
        for (var index = Cards.Count - 1; index >= 0; index--)
        {
            var card = Cards[index];
            if (card.Provider != ProviderKind.Copilot || !replacedAccounts.Contains(card.Account)) continue;
            var retainedWindows = card.Windows.Where(window => window.Snapshot is { Source: QuotaSource.Manual }).ToArray();
            if (retainedWindows.Length == 0) Cards.RemoveAt(index);
            else Cards[index] = card with
            {
                Windows = retainedWindows,
                CopilotNoDataPeriod = null,
                StateText = Language == UiLanguage.Japanese ? "手動" : "Manual"
            };
        }
        OnPropertyChanged(nameof(HasCards));
        OnPropertyChanged(nameof(HasEmptyState));
        OnPropertyChanged(nameof(IsEmpty));
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
    private ProviderCardViewModel WithCopilotReference(ProviderCardViewModel card)
    {
        var updated = card with
        {
            ManualReferenceCredits = card.Provider == ProviderKind.Copilot ? Settings.CopilotReferenceCredits : null,
            CopilotGrossUsed = card.Provider == ProviderKind.Copilot ? card.Windows.Select(row => row.Snapshot?.CopilotUsage?.GrossQuantity).FirstOrDefault(value => value is not null) : null,
            CurrentLanguage = Language
        };
        return updated with { Windows = updated.Windows.Select(row => row with { IsCompactFactLine = updated.Windows.Count >= 3 }).ToList() };
    }
    private void RefreshCopilotReferences()
    {
        for (var i = 0; i < Cards.Count; i++) Cards[i] = WithCopilotReference(Cards[i]);
        OnPropertyChanged(nameof(Cards));
    }
    private void LoadCards()
    {
        Cards.Clear();
        foreach (var card in source.Load()) Cards.Add(WithCopilotReference(card));
        if (Cards.Count == 0 && quotaHistory is not null)
        {
            // Persistent sources are loaded by InitializeAsync without blocking the UI thread.
        }
        OnPropertyChanged(nameof(HasCards)); OnPropertyChanged(nameof(HasEmptyState)); OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(EmptyStateText));
    }
    private void ReplaceCards(IEnumerable<ProviderCardViewModel> cards) { if (IsDisposed) return; var replacement = cards.Select(WithCopilotReference).ToList(); Cards.Clear(); foreach (var card in replacement) Cards.Add(card); OnPropertyChanged(nameof(HasCards)); OnPropertyChanged(nameof(HasEmptyState)); OnPropertyChanged(nameof(IsEmpty)); }
    private void ProjectCopilotNoData(CopilotRequestedPeriod period)
    {
        var account = $"{period.Organization}/{period.User}";
        var copy = new UiCopy(Language);
        var windowName = copy.CopilotMonthPeriod(period.Year, period.Month);
        var status = copy.CopilotNoDataDetails;
        var row = new QuotaRowViewModel(windowName, string.Empty, 0, string.Empty, status, string.Empty, string.Empty, string.Empty, false, status) { WindowKind = QuotaWindowKind.Monthly, CopilotNoDataPeriod = period, CurrentLanguage = Language };
        var card = Cards.FirstOrDefault(item => item.Provider == ProviderKind.Copilot && item.Account == account);
        if (card is null) card = new(ProviderKind.Copilot, copy.ProviderName(ProviderKind.Copilot), account, "#78A9FF", copy.CopilotNoUsageState, false, []);
        var windows = card.Windows.Where(item => item.Snapshot is { Source: QuotaSource.Manual }).Append(row).ToArray();
        var updated = WithCopilotReference(card with { StateText = copy.CopilotNoUsageState, Windows = windows, CopilotNoDataPeriod = period, CurrentLanguage = Language });
        var index = Cards.IndexOf(card);
        if (index < 0) Cards.Add(updated); else Cards[index] = updated;
        OnPropertyChanged(nameof(HasCards));
    }

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
            Cards[cardIndex] = WithCopilotReference(card with { StateText = state, Windows = windows });
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
            var state = card.CopilotNoDataPeriod is not null ? (Language == UiLanguage.Japanese ? "明細なし" : "No usage details") : Language == UiLanguage.Japanese ? card.StateText switch { "Healthy" => "正常", "Needs attention" => "注意", "Over limit" => "超過", "Connected" => "接続済み", "Manual" => "手動", _ => card.StateText } : card.StateText switch { "正常" => "Healthy", "注意" => "Needs attention", "超過" => "Over limit", "接続済み" => "Connected", "手動" => "Manual", _ => card.StateText };
            var windows = card.Windows.Select(row => row.CopilotNoDataPeriod is { } period ? row with { StatusText = new UiCopy(Language).CopilotNoDataDetails, ProgressLabel = new UiCopy(Language).CopilotNoDataDetails, WindowName = new UiCopy(Language).CopilotMonthPeriod(period.Year, period.Month), CurrentLanguage = Language } : QuotaPresentationFormatter.Relocalize(row, timeProvider.GetUtcNow(), Language)).ToList();
            Cards[i] = card with { Account = Language == UiLanguage.Japanese ? card.Account switch { "Personal account" => "個人アカウント", "Workspace · demo" => "ワークスペース · デモ", _ => card.Account } : card.Account switch { "個人アカウント" => "Personal account", "ワークスペース · デモ" => "Workspace · demo", _ => card.Account }, StateText = state, Windows = windows.Select(row => row with { IsCompactFactLine = windows.Count >= 3 }).ToList(), CurrentLanguage = Language };
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
        foreach (var name in new[] { nameof(CopyText), nameof(NavDashboardText), nameof(NavUsageText), nameof(NavHistoryText), nameof(NavSettingsText), nameof(UsageStatusText), nameof(RefreshText), nameof(QuotaStatusText), nameof(HeaderTitle), nameof(SubtitleText), nameof(EmptyStateText), nameof(EmptyStateDescription), nameof(HistoryDeleteText), nameof(DemoBanner), nameof(NotificationBannerText), nameof(OpenCodeCredentialNotice), nameof(CopilotNotice), nameof(CopilotQuotaNotice), nameof(CopilotFlowWaitingText), nameof(CopilotDeviceResult), nameof(CodexStatusText), nameof(CodexExpiresText), nameof(ProviderChoices), nameof(UsageConnectionStatusText), nameof(UsageConnectionErrorText), nameof(UsageCredentialStatusText) }) OnPropertyChanged(name);
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnPropertyChanged(name); if (name == nameof(CurrentPage)) { OnPropertyChanged(nameof(IsDashboardVisible)); OnPropertyChanged(nameof(IsUsageVisible)); OnPropertyChanged(nameof(IsHistoryVisible)); OnPropertyChanged(nameof(IsSettingsVisible)); OnPropertyChanged(nameof(HeaderTitle)); } return true; }
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

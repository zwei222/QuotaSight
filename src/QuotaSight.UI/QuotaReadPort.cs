using System.Globalization;
using System.Net;
using System.Text.Json;

namespace QuotaSight.UI;

public enum QuotaProvider { Claude, Codex, OpenCodeGo, AntigravityGemini, AntigravityClaudeGpt, GrokBuild, Copilot }
public enum QuotaWindow { Session, FiveHour, Rolling, Weekly, Monthly, Premium, Chat, Completions }
public enum QuotaFreshness { Fresh, Cached, Unknown }
public enum QuotaProviderStatus { Available, Unavailable }
public sealed record QuotaWindowValue(QuotaWindow Window, int? RemainingBasisPoints, DateTimeOffset? ResetsAt, bool Unlimited = false);
public sealed record QuotaProviderValue(QuotaProvider Provider, QuotaProviderStatus Status, QuotaFreshness Freshness, DateTimeOffset? ObservedAt, IReadOnlyList<QuotaWindowValue> Windows, bool MultipleSources = false);
public sealed record LatestQuota(DateTimeOffset CollectedAt, IReadOnlyList<QuotaProviderValue> Providers);
public sealed record QuotaCardWindow(QuotaWindow Window, int? RemainingBasisPoints, DateTimeOffset? ResetsAt, bool IsStale, bool Unlimited, string WindowText, string RemainingText, string ResetText, string FreshnessText, bool Expired = false)
{
    public bool IsPrimary { get; init; }
    /// <summary>Approved mock: the lowest valid window is emphasised only when it is picked from several valid finite windows.</summary>
    public bool IsEmphasized { get; init; }
    public bool IsLimited => !Unlimited;
    public double CompactValueFontSize => Unlimited ? 11 : 13;
    public double RowHeight => Unlimited ? 20 : 28;
    public bool IsResetVisible => !Unlimited && ResetText.Length > 0;
    public string ValueTone => Unlimited || RemainingBasisPoints is null || Expired ? "muted" : RemainingBasisPoints <= 1000 ? "danger" : RemainingBasisPoints <= 3000 ? "attention" : "normal";
    public bool IsNormal => ValueTone == "normal";
    public bool IsAttention => ValueTone == "attention";
    public bool IsDanger => ValueTone == "danger";
    public bool IsMuted => ValueTone == "muted";
}
public sealed record QuotaProviderCard(QuotaProvider Provider, string ProviderText, IReadOnlyList<QuotaCardWindow> Windows, string CenterText, string CenterPeriodText, bool IsStale, string FreshnessText)
{
    public bool IsUnavailable { get; init; }
    public bool IsWarning { get; init; }
    public string CenterTone { get; init; } = "muted";
    public bool IsCenterNormal => CenterTone == "normal";
    public bool IsCenterAttention => CenterTone == "attention";
    public bool IsCenterDanger => CenterTone == "danger";
    public bool IsCenterMuted => CenterTone == "muted";
    public string RemoveText { get; init; } = "Remove";
    /// <summary>Two short lines shown in place of the windows when the card has no quota to show.</summary>
    public string UnavailableMessage { get; init; } = string.Empty;
    public IReadOnlyList<int?> RingValues => Windows.Where(window => !window.Unlimited).Select(window => window.Expired ? null : window.RemainingBasisPoints).Take(3).ToArray();
    public bool IsUnlimitedOnly => Windows.Count > 0 && Windows.All(window => window.Unlimited);
    public double MainCenterFontSize => CenterText.Length > 4 ? 12 : CenterText.Length > 3 ? 15 : 16;
    public double CompactCenterFontSize => CenterText.Length > 4 ? 9 : 14;
    public bool HasCenter => Windows.Count > 0;
    public bool IsFiniteCenterVisible => HasCenter && !IsUnlimitedOnly;
}

public static class QuotaCardProjection
{
    private static readonly QuotaWindow[] Order = [QuotaWindow.Session, QuotaWindow.FiveHour, QuotaWindow.Rolling, QuotaWindow.Weekly, QuotaWindow.Monthly, QuotaWindow.Premium, QuotaWindow.Chat, QuotaWindow.Completions];
    public static IReadOnlyList<QuotaProviderCard> Project(LatestQuota quota, bool japanese, DateTimeOffset? now = null, bool forceStale = false)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        var cards = quota.Providers.Select(provider =>
        {
            var multiple = provider.MultipleSources;
            var unavailable = multiple || provider.Status != QuotaProviderStatus.Available;
            var age = provider.ObservedAt is { } observed ? FormatAge(current - observed, japanese) : string.Empty;
            var oldObservation = provider.ObservedAt is { } old && current - old > TimeSpan.FromHours(12);
            var freshnessStale = forceStale || unavailable || provider.Freshness != QuotaFreshness.Fresh || oldObservation;
            var sourceWindows = unavailable ? Array.Empty<QuotaWindowValue>() : provider.Windows.ToArray();
            var windows = sourceWindows.OrderBy(window => Array.IndexOf(Order, window.Window)).Take(3).Select(window =>
            {
                var expired = !window.Unlimited && window.ResetsAt is { } reset && reset <= current;
                var stale = freshnessStale || expired;
                var name = (window.Window, japanese) switch
                {
                    (QuotaWindow.Session, true) => "セッション",
                    (QuotaWindow.FiveHour, true) => "5時間",
                    (QuotaWindow.Rolling, true) => "ローリング",
                    (QuotaWindow.Weekly, true) => "週",
                    (QuotaWindow.Monthly, true) => "月",
                    (QuotaWindow.Premium, true) => "プレミアム",
                    (QuotaWindow.Chat, true) => "チャット",
                    (QuotaWindow.Completions, true) => "補完",
                    (QuotaWindow.Session, false) => "Session",
                    (QuotaWindow.FiveHour, false) => "5-hour",
                    (QuotaWindow.Rolling, false) => "Rolling",
                    (QuotaWindow.Weekly, false) => "Weekly",
                    (QuotaWindow.Monthly, false) => "Monthly",
                    (QuotaWindow.Premium, false) => "Premium",
                    (QuotaWindow.Chat, false) => "Chat",
                    (QuotaWindow.Completions, false) => "Completions",
                    _ => "Monthly"
                };
                var resetText = expired ? (japanese ? "リセット済み・更新待ち" : "Past reset · awaiting refresh") : window.Unlimited ? string.Empty : window.ResetsAt is { } at ? "↻ " + at.ToLocalTime().ToString("M/d HH:mm", CultureInfo.InvariantCulture) : (japanese ? "リセット時刻不明" : "Reset unknown");
                var staleText = !stale ? string.Empty : expired ? (japanese ? "リセット済み・再取得待ち" : "Past reset · awaiting refresh") : provider.Freshness switch { QuotaFreshness.Cached => japanese ? "キャッシュ" : "Cached", QuotaFreshness.Unknown => japanese ? "鮮度不明" : "Unknown freshness", _ => japanese ? "古い／期限切れ" : "Stale / expired" };
                return new QuotaCardWindow(window.Window, window.RemainingBasisPoints, window.ResetsAt, stale, window.Unlimited, name, window.Unlimited ? (japanese ? "無制限" : "Unlimited") : expired ? "—" : window.RemainingBasisPoints is { } bp ? $"{bp / 100m:0.#}%" : "—", resetText, staleText, expired);
            }).ToArray();
            var primary = windows.Where(window => !window.Unlimited && !window.Expired && window.RemainingBasisPoints is not null)
                .OrderBy(window => window.RemainingBasisPoints).FirstOrDefault();
            var validFinite = windows.Count(window => !window.Unlimited && !window.Expired && window.RemainingBasisPoints is not null);
            windows = windows.Select(window => window with { IsPrimary = primary?.Window == window.Window, IsEmphasized = validFinite > 1 && primary?.Window == window.Window }).ToArray();
            var allUnlimited = windows.Length > 0 && windows.All(window => window.Unlimited);
            var center = primary is not null ? primary.RemainingText : allUnlimited ? (japanese ? "無制限" : "Unlimited") : "—";
            var centerPeriod = primary?.WindowText ?? string.Empty;
            var centerTone = primary?.ValueTone ?? "muted";
            var providerText = (provider.Provider, japanese) switch
            {
                (QuotaProvider.Claude, _) => "Claude",
                (QuotaProvider.Codex, _) => "Codex",
                (QuotaProvider.OpenCodeGo, _) => "OpenCode Go",
                (QuotaProvider.AntigravityGemini, true) => "Antigravity（Gemini系）",
                (QuotaProvider.AntigravityGemini, false) => "Antigravity · Gemini models",
                (QuotaProvider.AntigravityClaudeGpt, true) => "Antigravity（Claude/GPT系）",
                (QuotaProvider.AntigravityClaudeGpt, false) => "Antigravity · Claude/GPT models",
                (QuotaProvider.GrokBuild, true) => "Grok Build",
                (QuotaProvider.GrokBuild, false) => "Grok Build",
                (QuotaProvider.Copilot, true) => "GitHub Copilot",
                _ => "GitHub Copilot"
            };
            var freshness = multiple ? (japanese ? "複数の送信元" : "Multiple sources")
                : unavailable ? (japanese ? "取得できませんでした" : "Unavailable")
                : forceStale ? (japanese ? "古いデータ" : "Stale")
                : provider.Freshness == QuotaFreshness.Unknown || provider.ObservedAt is null ? (japanese ? "取得時刻不明" : "Time unknown")
                : oldObservation ? (japanese ? age + "の値" : age + " old")
                : provider.Freshness == QuotaFreshness.Cached ? (japanese ? "キャッシュ · " + age : "Cached · " + age)
                : age;
            var staleCard = unavailable || freshnessStale || windows.Any(window => window.IsStale);
            var warning = forceStale || oldObservation;
            return new QuotaProviderCard(provider.Provider, providerText, windows, center, centerPeriod, staleCard, freshness)
            {
                IsUnavailable = unavailable,
                UnavailableMessage = multiple ? (japanese ? "複数の送信元" : "Multiple sources") : unavailable ? (japanese ? "残量を\n取得できませんでした" : "Couldn't read\nthe quota") : string.Empty,
                IsWarning = warning,
                CenterTone = centerTone
            };
        }).ToList();
        // No Antigravity model group could be read: one card headed just "Antigravity", not two near-identical ones.
        var antigravity = cards.Where(card => card.Provider is QuotaProvider.AntigravityGemini or QuotaProvider.AntigravityClaudeGpt).ToArray();
        if (antigravity.Length > 0 && antigravity.All(card => card.IsUnavailable))
        {
            var merged = antigravity[0] with { Provider = QuotaProvider.AntigravityGemini, ProviderText = "Antigravity" };
            var at = cards.IndexOf(antigravity[0]);
            cards.RemoveAll(card => antigravity.Contains(card));
            cards.Insert(at, merged);
        }
        return cards.ToArray();
    }

    private static string FormatAge(TimeSpan age, bool japanese)
    {
        var minutes = Math.Max(0, (int)age.TotalMinutes);
        if (minutes < 1) return japanese ? "たった今" : "Just now";
        if (minutes < 60) return japanese ? $"{minutes}分前" : $"{minutes}m ago";
        var hours = minutes / 60;
        if (hours < 48) return japanese ? $"{hours}時間前" : $"{hours}h ago";
        var days = hours / 24;
        return japanese ? $"{days}日前" : $"{days}d ago";
    }
}

public interface IQuotaReadPort { Task<LatestQuota> ReadLatestAsync(CancellationToken cancellationToken); }

/// <summary>Read-only adapter for the independent GET /v1/quota/latest contract.</summary>
public sealed class HttpQuotaReadPort(HttpClient client, Uri origin, string identityId, IUsageCredentialProvider credentials) : IQuotaReadPort
{
    private const int MaxBytes = 256 * 1024;
    public async Task<LatestQuota> ReadLatestAsync(CancellationToken cancellationToken)
    {
        if (origin.Scheme != Uri.UriSchemeHttps || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
            throw new InvalidOperationException("Quota endpoint must be an HTTPS origin.");
        var credential = await credentials.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (credential is null || credential.IdentityId != identityId) throw new InvalidOperationException("Quota credential is unavailable.");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, "/v1/quota/latest"));
        request.Headers.TryAddWithoutValidation("CF-Access-Client-Id", credential.ClientId);
        request.Headers.TryAddWithoutValidation("CF-Access-Client-Secret", credential.ClientSecret);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException("Quota read failed.");
        if (response.Content.Headers.ContentType?.MediaType != "application/json" || response.Content.Headers.ContentLength > MaxBytes) throw new InvalidOperationException("Quota response is invalid.");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length > MaxBytes) throw new InvalidOperationException("Quota response is too large.");
        return Parse(bytes);
    }

    private static LatestQuota Parse(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        Exact(root, "snapshots");
        var snapshots = root.GetProperty("snapshots");
        if (snapshots.ValueKind != JsonValueKind.Array || snapshots.GetArrayLength() == 0) throw new InvalidOperationException("Quota response is invalid.");
        var collectedAt = DateTimeOffset.MinValue;
        var found = new Dictionary<QuotaProvider, List<QuotaProviderValue>>();
        var order = new List<QuotaProvider>();
        foreach (var snapshot in snapshots.EnumerateArray())
        {
            Exact(snapshot, "collectedAt", "providers");
            var time = Instant(snapshot.GetProperty("collectedAt"));
            if (time > collectedAt) collectedAt = time;
            foreach (var value in ParseProviders(snapshot.GetProperty("providers")))
            {
                if (!found.TryGetValue(value.Provider, out var list)) { found[value.Provider] = list = []; order.Add(value.Provider); }
                list.Add(value);
            }
        }

        // The same provider arriving from several snapshots has no single truthful value: say so
        // instead of silently adopting the newest one.
        var providers = order.Select(id => found[id].Count == 1 ? found[id][0] : new QuotaProviderValue(id, QuotaProviderStatus.Unavailable, QuotaFreshness.Unknown, null, [], true)).ToList();
        return new(collectedAt, providers);
    }

    private static List<QuotaProviderValue> ParseProviders(JsonElement providerArray)
    {
        if (providerArray.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Quota response is invalid.");
        var providers = new List<QuotaProviderValue>(); var seen = new HashSet<QuotaProvider>();
        foreach (var item in providerArray.EnumerateArray())
        {
            Exact(item, "provider", "status", "freshness", "observedAt", "windows");
            var provider = item.GetProperty("provider").GetString() switch { "claude" => QuotaProvider.Claude, "codex" => QuotaProvider.Codex, "opencode-go" => QuotaProvider.OpenCodeGo, "antigravity-gemini" => QuotaProvider.AntigravityGemini, "antigravity-claude-gpt" => QuotaProvider.AntigravityClaudeGpt, "grok-build" => QuotaProvider.GrokBuild, "copilot" => QuotaProvider.Copilot, _ => throw new InvalidOperationException("Quota response is invalid.") };
            if (!seen.Add(provider)) throw new InvalidOperationException("Quota response is ambiguous.");
            var status = item.GetProperty("status").GetString() switch { "available" => QuotaProviderStatus.Available, "unavailable" => QuotaProviderStatus.Unavailable, _ => throw new InvalidOperationException("Quota response is invalid.") };
            var freshness = item.GetProperty("freshness").GetString() switch { "fresh" => QuotaFreshness.Fresh, "cached" => QuotaFreshness.Cached, "unknown" => QuotaFreshness.Unknown, _ => throw new InvalidOperationException("Quota response is invalid.") };
            var observed = NullableInstant(item.GetProperty("observedAt")); var windows = item.GetProperty("windows");
            if (windows.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Quota response is invalid.");
            var values = new List<QuotaWindowValue>(); var seenWindows = new HashSet<QuotaWindow>();
            foreach (var entry in windows.EnumerateArray())
            {
                ExactWindow(entry, provider == QuotaProvider.Copilot);
                var window = entry.GetProperty("window").GetString() switch { "session" => QuotaWindow.Session, "five-hour" => QuotaWindow.FiveHour, "rolling" => QuotaWindow.Rolling, "weekly" => QuotaWindow.Weekly, "monthly" => QuotaWindow.Monthly, "premium" => QuotaWindow.Premium, "chat" => QuotaWindow.Chat, "completions" => QuotaWindow.Completions, _ => throw new InvalidOperationException("Quota response is invalid.") };
                if (!seenWindows.Add(window)) throw new InvalidOperationException("Quota response is ambiguous.");
                if (!WindowBelongsToProvider(provider, window)) throw new InvalidOperationException("Quota response is invalid.");
                var value = entry.GetProperty("remainingBasisPoints"); int? bp = value.ValueKind == JsonValueKind.Null ? null : value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n is >= 0 and <= 10000 ? n : throw new InvalidOperationException("Quota response is invalid.");
                var unlimited = entry.TryGetProperty("unlimited", out var unlimitedElement);
                if (unlimited && (provider != QuotaProvider.Copilot || unlimitedElement.ValueKind != JsonValueKind.True || bp is not null)) throw new InvalidOperationException("Quota response is invalid.");
                values.Add(new(window, bp, NullableInstant(entry.GetProperty("resetsAt")), unlimited));
            }
            providers.Add(new(provider, status, freshness, observed, values));
        }
        return providers;
    }

    private static bool WindowBelongsToProvider(QuotaProvider provider, QuotaWindow window) => provider switch
    {
        QuotaProvider.AntigravityGemini or QuotaProvider.AntigravityClaudeGpt => window is QuotaWindow.FiveHour or QuotaWindow.Weekly,
        QuotaProvider.GrokBuild => window is QuotaWindow.Weekly or QuotaWindow.Monthly,
        QuotaProvider.Copilot => window is QuotaWindow.Premium or QuotaWindow.Chat or QuotaWindow.Completions,
        _ => window is QuotaWindow.Session or QuotaWindow.FiveHour or QuotaWindow.Rolling or QuotaWindow.Weekly or QuotaWindow.Monthly
    };

    private static void ExactWindow(JsonElement element, bool allowUnlimited)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Quota response is invalid.");
        var names = new HashSet<string>(["window", "remainingBasisPoints", "resetsAt"], StringComparer.Ordinal);
        if (allowUnlimited) names.Add("unlimited");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!names.Contains(property.Name) || !seen.Add(property.Name)) throw new InvalidOperationException("Quota response is invalid.");
        if (!seen.Contains("window") || !seen.Contains("remainingBasisPoints") || !seen.Contains("resetsAt")) throw new InvalidOperationException("Quota response is invalid.");
    }

    private static DateTimeOffset? NullableInstant(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : Instant(value);
    private static DateTimeOffset Instant(JsonElement value) => value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var parsed) && parsed.Offset == TimeSpan.Zero ? parsed : throw new InvalidOperationException("Quota response is invalid.");
    private static void Exact(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Quota response is invalid.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new InvalidOperationException("Quota response is invalid.");
        if (seen.Count != names.Length) throw new InvalidOperationException("Quota response is invalid.");
    }
}
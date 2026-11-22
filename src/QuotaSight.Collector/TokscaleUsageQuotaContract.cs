using System.Globalization;
using System.Text.Json;

namespace QuotaSight.Collector;

/// <summary>Bounded parser for the pinned TokScale usage JSON contract (array of provider outputs).</summary>
public static class TokscaleUsageQuotaContract
{
    public const int MaxInputBytes = 256 * 1024;
    private static readonly string[] SupportedProviders = ["Claude", "Codex", "OpenCode Go"];
    private const int MaxProviders = 64;
    private const int MaxMetricsPerProvider = 128;
    private const int MaxJsonDepth = 8;

    /// <summary>
    /// Parses TokScale usage output. Only source-verified subscription-window labels
    /// are returned; spend/balance fields and all account, plan, credential and free-text
    /// fields are deliberately excluded from the result.
    /// </summary>
    public static TokscaleUsageQuotaParseResult Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty || utf8.Length > MaxInputBytes)
        {
            return TokscaleUsageQuotaParseResult.Malformed;
        }

        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaxJsonDepth,
            });

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return TokscaleUsageQuotaParseResult.Malformed;
            }

            var outputs = document.RootElement;
            if (outputs.GetArrayLength() > MaxProviders)
            {
                return TokscaleUsageQuotaParseResult.Malformed;
            }

            var groups = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
            foreach (var output in outputs.EnumerateArray())
            {
                if (output.ValueKind != JsonValueKind.Object || HasDuplicateProperties(output))
                {
                    return TokscaleUsageQuotaParseResult.Malformed;
                }

                if (!TryGetString(output, "provider", out var provider))
                {
                    return TokscaleUsageQuotaParseResult.Malformed;
                }

                if (!IsSupportedProvider(provider))
                {
                    continue;
                }

                if (!groups.TryGetValue(provider, out var rows))
                {
                    rows = new List<JsonElement>();
                    groups.Add(provider, rows);
                }

                rows.Add(output);
            }

            var providers = new List<TokscaleUsageQuotaProvider>(groups.Count);
            foreach (var provider in SupportedProviders)
            {
                if (!groups.TryGetValue(provider, out var rows))
                {
                    continue;
                }

                var selected = SelectAccount(rows);
                if (selected is null)
                {
                    providers.Add(new TokscaleUsageQuotaProvider(
                        provider,
                        TokscaleUsageQuotaProviderStatus.Unavailable,
                        TokscaleUsageQuotaFreshness.Unknown,
                        Array.Empty<TokscaleUsageQuotaWindow>()));
                    continue;
                }

                if (!TryReadProvider(selected.Value, provider, out var freshness, out var windows))
                {
                    return TokscaleUsageQuotaParseResult.Malformed;
                }

                providers.Add(new TokscaleUsageQuotaProvider(
                    provider,
                    TokscaleUsageQuotaProviderStatus.Available,
                    freshness,
                    windows));
            }

            return TokscaleUsageQuotaParseResult.Success(providers);
        }
        catch (JsonException)
        {
            return TokscaleUsageQuotaParseResult.Malformed;
        }
        catch (InvalidOperationException)
        {
            return TokscaleUsageQuotaParseResult.Malformed;
        }
    }

    private static bool TryReadProvider(
        JsonElement output,
        string provider,
        out TokscaleUsageQuotaFreshness freshness,
        out IReadOnlyList<TokscaleUsageQuotaWindow> windows)
    {
        freshness = TokscaleUsageQuotaFreshness.Unknown;
        windows = Array.Empty<TokscaleUsageQuotaWindow>();

        if (TryGetOptional(output, "freshness", out var freshValue))
        {
            if (freshValue.ValueKind == JsonValueKind.Object)
            {
                if (HasDuplicateProperties(freshValue)
                    || !TryGetBoolean(freshValue, "stale", out var isStale)
                    || !TryGetNumber(freshValue, "fetched_at", out var fetchedAt)
                    || fetchedAt < 0)
                {
                    return false;
                }

                freshness = isStale ? TokscaleUsageQuotaFreshness.Stale : TokscaleUsageQuotaFreshness.Fresh;
            }
            else if (freshValue.ValueKind != JsonValueKind.Null)
            {
                return false;
            }
        }

        if (!TryGetRequired(output, "metrics", out var metrics) || metrics.ValueKind != JsonValueKind.Array
            || metrics.GetArrayLength() > MaxMetricsPerProvider)
        {
            return false;
        }

        var windowsFound = new List<TokscaleUsageQuotaWindow>();
        var seenLabels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var metric in metrics.EnumerateArray())
        {
            if (metric.ValueKind != JsonValueKind.Object || HasDuplicateProperties(metric)
                || !TryGetString(metric, "label", out var label))
            {
                return false;
            }

            if (!IsContractWindow(provider, label))
            {
                continue;
            }

            if (!seenLabels.Add(label)
                || !TryGetRequired(metric, "remaining_percent", out var percentValue)
                || !TryReadPercent(percentValue, out var percent))
            {
                return false;
            }

            string? resetAt = null;
            if (TryGetOptional(metric, "resets_at", out var resetValue)
                && !TryReadReset(resetValue, out resetAt))
            {
                return false;
            }

            windowsFound.Add(new TokscaleUsageQuotaWindow(
                label,
                percent,
                percent is null ? TokscaleUsageQuotaValueStatus.Unknown : TokscaleUsageQuotaValueStatus.Known,
                resetAt));
        }

        windows = windowsFound;
        return true;
    }

    private static JsonElement? SelectAccount(IReadOnlyList<JsonElement> rows)
    {
        if (rows.Count == 1)
        {
            var row = rows[0];
            if (!TryGetOptional(row, "account", out var account) || account.ValueKind == JsonValueKind.Null)
            {
                return row;
            }

            return IsActiveAccount(account) ? row : null;
        }

        JsonElement? active = null;
        foreach (var row in rows)
        {
            if (!TryGetOptional(row, "account", out var account)
                || account.ValueKind != JsonValueKind.Object
                || HasDuplicateProperties(account))
            {
                return null;
            }

            if (IsActiveAccount(account))
            {
                if (active.HasValue)
                {
                    return null;
                }

                active = row;
            }
        }

        return active;
    }

    private static bool IsActiveAccount(JsonElement account) =>
        account.ValueKind == JsonValueKind.Object
        && !HasDuplicateProperties(account)
        && TryGetBoolean(account, "is_active", out var isActive)
        && isActive;

    private static bool IsSupportedProvider(string provider) =>
        provider is "Claude" or "Codex" or "OpenCode Go";

    // Evidence from the pinned TokScale provider modules: Claude emits the
    // account-wide Session/Weekly windows and separate model-scoped rows;
    // Codex derives 5h/Weekly from the window duration (other durations are
    // not allowlisted); OpenCode Go emits Rolling/Weekly/Monthly. Provider
    // scoping prevents Codex's Weekly label or other meter labels being guessed.
    private static bool IsContractWindow(string provider, string label) => provider switch
    {
        "Claude" => label is "Session" or "Weekly",
        "Codex" => label is "5h" or "Weekly",
        "OpenCode Go" => label is "Rolling" or "Weekly" or "Monthly",
        _ => false,
    };

    private static bool TryReadPercent(JsonElement element, out double? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var parsed)
            || !double.IsFinite(parsed) || parsed is < 0 or > 100)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadReset(JsonElement element, out string? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = element.GetString();
        if (string.IsNullOrEmpty(text)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
        {
            return true;
        }

        value = text;
        return true;
    }

    private static bool TryGetString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        if (!TryGetRequired(parent, name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString()!;
        return true;
    }

    private static bool TryGetBoolean(JsonElement parent, string name, out bool value)
    {
        value = false;
        if (!TryGetRequired(parent, name, out var element)
            || element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static bool TryGetNumber(JsonElement parent, string name, out double value)
    {
        value = 0;
        return TryGetRequired(parent, name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value)
            && double.IsFinite(value);
    }

    private static bool TryGetRequired(JsonElement parent, string name, out JsonElement value)
    {
        if (TryGetOptional(parent, name, out value) && value.ValueKind != JsonValueKind.Undefined)
        {
            return true;
        }

        return false;
    }

    private static bool TryGetOptional(JsonElement parent, string name, out JsonElement value)
    {
        foreach (var property in parent.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.Ordinal))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                return true;
            }
        }

        return false;
    }
}

public enum TokscaleUsageQuotaParseStatus { Success, Malformed }
public enum TokscaleUsageQuotaProviderStatus { Available, Unavailable }
public enum TokscaleUsageQuotaFreshness { Fresh, Stale, Unknown }
public enum TokscaleUsageQuotaValueStatus { Known, Unknown }

public sealed record TokscaleUsageQuotaWindow(
    string Label,
    double? RemainingPercent,
    TokscaleUsageQuotaValueStatus ValueStatus,
    string? ResetAt);

public sealed record TokscaleUsageQuotaProvider(
    string Provider,
    TokscaleUsageQuotaProviderStatus Status,
    TokscaleUsageQuotaFreshness Freshness,
    IReadOnlyList<TokscaleUsageQuotaWindow> Windows);

public sealed record TokscaleUsageQuotaParseResult(
    TokscaleUsageQuotaParseStatus Status,
    IReadOnlyList<TokscaleUsageQuotaProvider> Providers)
{
    public static TokscaleUsageQuotaParseResult Malformed { get; } =
        new(TokscaleUsageQuotaParseStatus.Malformed, Array.Empty<TokscaleUsageQuotaProvider>());

    public static TokscaleUsageQuotaParseResult Success(IReadOnlyList<TokscaleUsageQuotaProvider> providers) =>
        new(TokscaleUsageQuotaParseStatus.Success, providers);
}

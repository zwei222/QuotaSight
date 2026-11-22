using System.Globalization;

namespace QuotaSight.UI;

/// <summary>Which key the usage page groups the breakdown parts by.</summary>
public enum UsageBreakdownDimension
{
    Client,
    Provider,
    Model,
}

/// <summary>One part of a group, rendered into localized display lines for the expanded row.</summary>
public sealed record BreakdownPartRow(
    string Model,
    string InputText,
    string OutputText,
    string CacheReadText,
    string CacheWriteText,
    string ReasoningText,
    string TotalText);

/// <summary>
/// One aggregated row of the breakdown card: the parts that share one key, their sum,
/// the display name the UI shows for that key (a known client's product name, the raw
/// key itself, or the localized "unknown" marker for an empty key), and the row's share
/// of the range's attributed total. The share is a composition ratio only — never a
/// quota or a gauge value.
/// </summary>
public sealed record BreakdownGroup(
    string Key,
    string DisplayName,
    long Total,
    IReadOnlyList<UsagePart> Parts,
    long RangeTotal,
    IReadOnlyList<BreakdownPartRow> PartRows)
{
    /// <summary>Plain share of the range total in whole-percent text, for the row's ratio label.</summary>
    public string ShareText => RangeTotal <= 0
        ? "0"
        : Math.Clamp(Total * 100d / RangeTotal, 0d, 100d).ToString("0.#", CultureInfo.InvariantCulture);
}

/// <summary>
/// One small dashboard card: a detected provider with its observed totals, or a
/// provider the user added whose tokens the breakdown has not attributed yet. Tokens
/// are summed across clients and models for one provider; a row several providers
/// served together is one combination card. It is an observation surface only — no
/// limit, no ring and no threshold belongs on it.
/// </summary>
public sealed record TokScaleUsageCard(
    string ProviderKey,
    string DisplayName,
    long TodayTokens,
    long WeekTokens,
    string SummaryText,
    bool Observed = true,
    bool IsMultiProvider = false,
    string MultiProviderText = "")
{
    /// <summary>Today's tokens, formatted with grouping and never rounded.</summary>
    public string TodayText => TodayTokens.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The seven-day total, formatted the same way.</summary>
    public string WeekText => WeekTokens.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>
/// Pure aggregation over a verified breakdown. It never invents values: an empty key is
/// kept as its own group, an unclassified remainder is summed exactly, and an
/// over-attributed day is reported with its raw numbers instead of being corrected.
/// </summary>
public static class UsageBreakdownAggregation
{
    /// <summary>
    /// Groups the parts by one key component. The documented part order (total
    /// descending, ties by key) is preserved, so groups come out in the same order and
    /// a group's parts stay in their documented order.
    /// </summary>
    public static IReadOnlyList<BreakdownGroup> GroupParts(
        IReadOnlyList<UsagePart> parts,
        UsageBreakdownDimension dimension,
        UiLanguage language = UiLanguage.English)
    {
        var copy = new UiCopy(language);
        var groups = new List<BreakdownGroup>();
        var index = new Dictionary<string, (BreakdownGroup Group, int Position)>(StringComparer.Ordinal);
        long rangeTotal = 0;
        foreach (var part in parts) rangeTotal += part.Total;
        foreach (var part in parts)
        {
            var key = KeyOf(part, dimension);
            if (index.TryGetValue(key, out var slot))
            {
                var group = slot.Group with
                {
                    Total = slot.Group.Total + part.Total,
                    Parts = [.. slot.Group.Parts, part],
                    PartRows = [.. slot.Group.PartRows, PartRow(part, copy)],
                };
                index[key] = (group, slot.Position);
                groups[slot.Position] = group;
            }
            else
            {
                var group = new BreakdownGroup(
                    key,
                    DisplayName(key, dimension, copy),
                    part.Total,
                    [part],
                    rangeTotal,
                    [PartRow(part, copy)]);
                index[key] = (group, groups.Count);
                groups.Add(group);
            }
        }

        return groups;
    }

    /// <summary>The total of the range's attribution gaps: Σ max(0, day total − attributed).</summary>
    public static long UnclassifiedTokens(IReadOnlyList<UsageCoverage> coverage)
    {
        long sum = 0;
        foreach (var row in coverage)
        {
            var remainder = row.TotalTokens - row.AttributedTokens;
            if (remainder > 0) sum += remainder;
        }

        return sum;
    }

    /// <summary>Rows whose attribution exceeds the day total, in input order, values intact.</summary>
    public static IReadOnlyList<UsageCoverage> OverAttributedRows(IReadOnlyList<UsageCoverage> coverage)
    {
        List<UsageCoverage>? over = null;
        foreach (var row in coverage)
        {
            if (row.AttributedTokens > row.TotalTokens)
            {
                over ??= [];
                over.Add(row);
            }
        }

        return over ?? [];
    }

    /// <summary>
    /// True when the range both has attributed days and has usage days without any
    /// attribution — "only some days have a breakdown". A range where nothing is
    /// attributed is the empty state, not a partial one.
    /// </summary>
    public static bool IsPartiallyAttributed(IReadOnlyList<UsageCoverage> coverage)
    {
        var attributedDay = false;
        var unattributedUsageDay = false;
        foreach (var row in coverage)
        {
            if (row.AttributedTokens > 0) attributedDay = true;
            if (row.TotalTokens > 0 && row.AttributedTokens == 0) unattributedUsageDay = true;
        }

        return attributedDay && unattributedUsageDay;
    }

    /// <summary>
    /// Builds the dashboard cards from the two reads the section performs (today and the
    /// last seven days), for the providers the user added and only for them: a provider
    /// is observed but never auto-carded, so nothing the user did not select appears.
    /// A card means "usage through that provider": its own rows plus every combination
    /// row (a comma-joined provider key such as <c>deepseek,opencode_go</c>) it is part
    /// of. Combination rows therefore feed several cards, and such a card says so with
    /// the shared-amount notice instead of presenting a total that never overlaps.
    /// A provider seen in either read gets exactly one card; providers the user added
    /// but the breakdown has not attributed anything to yet get a card that says so
    /// instead of looking like a zero reading. An empty provider key is data, not a
    /// provider: it never becomes a card or a selectable choice. Sorted by the
    /// seven-day total descending, ties by provider key.
    /// </summary>
    public static IReadOnlyList<TokScaleUsageCard> TokScaleCards(
        UsageBreakdown today,
        UsageBreakdown week,
        IReadOnlyList<string> trackedProviders,
        UiLanguage language = UiLanguage.English)
    {
        var copy = new UiCopy(language);
        var tracked = new HashSet<string>(trackedProviders ?? [], StringComparer.Ordinal);
        var totals = new Dictionary<string, (long Today, long Week)>(StringComparer.Ordinal);
        var shared = new HashSet<string>(StringComparer.Ordinal);
        AddLane(today.Parts, todayLane: true);
        AddLane(week.Parts, todayLane: false);

        void AddLane(IReadOnlyList<UsagePart> parts, bool todayLane)
        {
            foreach (var part in parts)
            {
                foreach (var provider in ProviderKeysOf(part.Provider))
                {
                    if (!tracked.Contains(provider)) continue;
                    if (!totals.TryGetValue(provider, out var seen)) seen = (Today: 0L, Week: 0L);
                    totals[provider] = todayLane ? (seen.Today + part.Total, seen.Week) : (seen.Today, seen.Week + part.Total);
                    if (IsCombination(part.Provider)) shared.Add(provider);
                }
            }
        }

        // A tracked provider with no attribution on either read is "not observed yet",
        // not a zero-token observation.
        foreach (var id in tracked)
        {
            if (TokScaleProviderCatalog.Find(id) is not null && !totals.ContainsKey(id)) totals[id] = (0, 0);
        }

        return [.. totals
            .Select(pair =>
            {
                // Presence in either read's parts is the observation; absence is "not
                // observed yet", even for a tracked provider. A zero-token part is a real
                // observation, not the not-observed state.
                var observed = today.Parts.Any(part => ProviderKeysOf(part.Provider).Contains(pair.Key))
                    || week.Parts.Any(part => ProviderKeysOf(part.Provider).Contains(pair.Key));
                var includesShared = shared.Contains(pair.Key);
                return new TokScaleUsageCard(
                    pair.Key,
                    TokScaleProviderCatalog.DisplayNameOf(pair.Key),
                    pair.Value.Today,
                    pair.Value.Week,
                    observed
                        ? $"{copy.TokScaleToday} {pair.Value.Today:N0} · {copy.TokScaleWeek} {pair.Value.Week:N0}"
                        : copy.TokScaleNotObserved,
                    observed,
                    includesShared,
                    includesShared ? copy.TokScaleSharedNotice : string.Empty);
            })
            .OrderByDescending(card => card.WeekTokens)
            .ThenBy(card => card.ProviderKey, StringComparer.Ordinal)];
    }

    /// <summary>
    /// How many distinct named providers the two reads observed, whether or not the user
    /// added them. The empty provider key counts as data, never as a provider, so it is
    /// excluded; it may only surface as "something was observed" in the empty state.
    /// </summary>
    public static int ObservedProviderCount(UsageBreakdown today, UsageBreakdown week)
    {
        var providers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in today.Parts) foreach (var provider in ProviderKeysOf(part.Provider)) providers.Add(provider);
        foreach (var part in week.Parts) foreach (var provider in ProviderKeysOf(part.Provider)) providers.Add(provider);
        return providers.Count;
    }

    /// <summary>The providers one part's provider value names: the single id, or every id of a comma-joined combination. The empty key names no provider.</summary>
    private static IReadOnlyList<string> ProviderKeysOf(string provider) =>
        provider.Length == 0 ? [] : provider.Split(',');

    private static bool IsCombination(string provider) => provider.Contains(',');

    private static string KeyOf(UsagePart part, UsageBreakdownDimension dimension) => dimension switch
    {
        UsageBreakdownDimension.Provider => part.Provider,
        UsageBreakdownDimension.Model => part.Model,
        _ => part.Client,
    };

    private static string DisplayName(string key, UsageBreakdownDimension dimension, UiCopy copy) =>
        key.Length == 0
            ? copy.UsageBreakdownUnknown
            : dimension == UsageBreakdownDimension.Client ? copy.TokScaleClientName(key) : key;

    private static BreakdownPartRow PartRow(UsagePart part, UiCopy copy) => new(
        part.Model.Length == 0 ? copy.UsageBreakdownUnknown : part.Model,
        $"{copy.UsageBreakdownComponentInput} {part.Input:N0}",
        $"{copy.UsageBreakdownComponentOutput} {part.Output:N0}",
        $"{copy.UsageBreakdownComponentCacheRead} {part.CacheRead:N0}",
        $"{copy.UsageBreakdownComponentCacheWrite} {part.CacheWrite:N0}",
        $"{copy.UsageBreakdownComponentReasoning} {part.Reasoning:N0}",
        $"{copy.UsageBreakdownTotal} {part.Total:N0}");
}

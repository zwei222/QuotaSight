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
/// One small dashboard card: a detected TokScale client with its observed totals, or a
/// client the user added whose tokens the breakdown has not attributed yet. It is an
/// observation surface only — no limit, no ring and no threshold belongs on it.
/// </summary>
public sealed record TokScaleClientCard(
    string ClientId,
    string DisplayName,
    long TodayTokens,
    long WeekTokens,
    string SummaryText,
    bool Observed = true)
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
    /// last seven days). A client seen in either read gets exactly one card; clients the
    /// user added but the breakdown has not attributed anything to yet get a card that
    /// says so instead of looking like a zero reading. Sorted by the seven-day total
    /// descending, ties by client id.
    /// </summary>
    public static IReadOnlyList<TokScaleClientCard> TokScaleCards(
        UsageBreakdown today,
        UsageBreakdown week,
        IReadOnlyList<string> trackedClients,
        UiLanguage language = UiLanguage.English)
    {
        var copy = new UiCopy(language);
        var cards = new Dictionary<string, (long Today, long Week)>(StringComparer.Ordinal);
        foreach (var part in today.Parts)
        {
            var key = string.IsNullOrEmpty(part.Client) ? string.Empty : part.Client;
            var seen = cards.TryGetValue(key, out var existing) ? existing : (0L, 0L);
            cards[key] = (seen.Item1 + part.Total, seen.Item2);
        }

        foreach (var part in week.Parts)
        {
            var key = string.IsNullOrEmpty(part.Client) ? string.Empty : part.Client;
            var seen = cards.TryGetValue(key, out var existing) ? existing : (0L, 0L);
            cards[key] = (seen.Item1, seen.Item2 + part.Total);
        }

        // A tracked client with no attribution on either read is "not observed yet",
        // not a zero-token observation.
        foreach (var id in trackedClients)
        {
            if (TokScaleClientCatalog.Find(id) is not null && !cards.ContainsKey(id)) cards[id] = (0, 0);
        }

        return [.. cards
            .Select(pair =>
            {
                // Presence in either read's parts is the observation; absence is "not
                // observed yet", even for a tracked client. A zero-token part is a real
                // observation, not the not-observed state.
                var observed = today.Parts.Any(part => part.Client == pair.Key)
                    || week.Parts.Any(part => part.Client == pair.Key);
                return new TokScaleClientCard(
                    pair.Key,
                    DisplayName(pair.Key, UsageBreakdownDimension.Client, copy),
                    pair.Value.Today,
                    pair.Value.Week,
                    observed
                        ? $"{copy.TokScaleToday} {pair.Value.Today:N0} · {copy.TokScaleWeek} {pair.Value.Week:N0}"
                        : copy.TokScaleNotObserved,
                    observed);
            })
            .OrderByDescending(card => card.WeekTokens)
            .ThenBy(card => card.ClientId, StringComparer.Ordinal)];
    }

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

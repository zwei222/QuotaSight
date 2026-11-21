using System.Globalization;
using System.Text.Json;

namespace QuotaSight.Collector;

/// <summary>
/// Bounded, strict reader for the pinned <c>tokscale graph --json</c> document
/// (version 4.23.0) that produces an API wire v1 candidate snapshot.
///
/// Rules, all deliberate:
/// <list type="bullet">
/// <item>Only <c>meta.version</c>, <c>meta.generatedAt</c>, <c>contributions[].date</c>
/// and <c>contributions[].totals.tokens</c> are consumed; every other value is
/// skipped and never echoed.</item>
/// <item>Unknown or repeated keys at the consumed levels are rejected instead of ignored.</item>
/// <item>Days the document does not date are never invented: an absent day has no row,
/// while an explicitly observed <c>0</c> on a dated row is kept.</item>
/// <item>Unsorted input is canonicalized into ascending order; duplicate dates are rejected.</item>
/// <item>Nothing is claimed about completeness; the result is a candidate only.</item>
/// </list>
/// </summary>
public static class TokscaleGraphNormalizer
{
    private const int MaxDepth = 16;

    private static readonly string[] RootKeys = ["meta", "summary", "years", "contributions", "timeMetrics"];
    private static readonly string[] MetaKeys = ["generatedAt", "version", "dateRange"];
    private static readonly string[] ContributionKeys = ["date", "totals", "intensity", "tokenBreakdown", "clients"];
    private static readonly string[] TotalsKeys = ["tokens", "cost", "messages", "costProvenance"];

    /// <summary>Normalizes one bounded upstream document.</summary>
    /// <param name="utf8">UTF-8 bytes of the whole document; no more than the input bound.</param>
    /// <returns>A candidate, a no-dated-rows outcome, or a rejection.</returns>
    public static NormalizationResult Normalize(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > TokscaleGraphContract.MaxInputBytes)
        {
            return NormalizationResult.Rejected(
                "input",
                "too_large",
                $"The upstream document must not exceed {TokscaleGraphContract.MaxInputBytes} bytes.");
        }

        if (utf8.Length == 0)
        {
            return NormalizationResult.Rejected("$", "invalid_json", "The upstream document is empty.");
        }

        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = MaxDepth,
            });

            var builder = new GraphBuilder();
            ReadDocument(ref reader, builder);

            if (!IsWhitespaceOnly(utf8[(int)reader.BytesConsumed..]))
            {
                return NormalizationResult.Rejected(
                    "$",
                    "trailing_content",
                    "The upstream document must contain exactly one JSON value.");
            }

            return builder.Build();
        }
        catch (JsonException)
        {
            return NormalizationResult.Rejected("$", "invalid_json", "The upstream document is not valid JSON within the pinned shape.");
        }
        catch (RejectionException rejection)
        {
            return NormalizationResult.Rejected(rejection.Field, rejection.Reason, rejection.Message);
        }
    }

    private static void ReadDocument(ref Utf8JsonReader reader, GraphBuilder builder)
    {
        if (!reader.Read())
        {
            throw Reject("$", "invalid_json", "The upstream document is empty.");
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw Reject("$", "invalid_body", "The upstream document root must be a JSON object.");
        }

        ReadRootObject(ref reader, builder);
    }

    private static bool IsWhitespaceOnly(ReadOnlySpan<byte> tail)
    {
        foreach (var value in tail)
        {
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'))
            {
                return false;
            }
        }

        return true;
    }

    private static void ReadRootObject(ref Utf8JsonReader reader, GraphBuilder builder)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sawMeta = false;
        var sawContributions = false;

        while (true)
        {
            reader.Read();
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            var name = PropertyName(ref reader);
            if (Array.IndexOf(RootKeys, name) < 0)
            {
                throw Reject(name, "unexpected_field", $"Field {name} is not part of the pinned tokscale-{TokscaleGraphContract.PinnedUpstreamVersion} graph document.");
            }

            if (!seen.Add(name))
            {
                throw Reject(name, "duplicate_key", $"Field {name} is repeated in the document root.");
            }

            reader.Read();
            switch (name)
            {
                case "meta":
                    sawMeta = true;
                    ReadMeta(ref reader, builder);
                    break;
                case "contributions":
                    sawContributions = true;
                    ReadContributions(ref reader, builder);
                    break;
                default:
                    SkipValue(ref reader);
                    break;
            }
        }

        if (!sawMeta)
        {
            throw Reject("meta", "missing_field", "Field meta is required.");
        }

        if (!sawContributions)
        {
            throw Reject("contributions", "missing_field", "Field contributions is required.");
        }
    }

    private static void ReadMeta(ref Utf8JsonReader reader, GraphBuilder builder)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw Reject("meta", "invalid_type", "Field meta must be an object.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sawGeneratedAt = false;
        var sawVersion = false;

        while (true)
        {
            reader.Read();
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            var name = PropertyName(ref reader);
            if (Array.IndexOf(MetaKeys, name) < 0)
            {
                throw Reject($"meta.{name}", "unexpected_field", $"Field meta.{name} is not part of the pinned graph document.");
            }

            if (!seen.Add(name))
            {
                throw Reject($"meta.{name}", "duplicate_key", $"Field meta.{name} is repeated.");
            }

            reader.Read();
            switch (name)
            {
                case "version":
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        throw Reject("meta.version", "invalid_type", "Field meta.version must be a string.");
                    }

                    builder.UpstreamVersion = reader.GetString() ?? string.Empty;
                    sawVersion = true;
                    break;
                case "generatedAt":
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        throw Reject("meta.generatedAt", "invalid_type", "Field meta.generatedAt must be a string.");
                    }

                    var raw = reader.GetString() ?? string.Empty;
                    if (!TryCanonicalizeInstant(raw, out var canonical))
                    {
                        throw Reject("meta.generatedAt", "invalid_instant", "Field meta.generatedAt must be an RFC 3339 instant with an explicit UTC offset.");
                    }

                    builder.CollectedAt = canonical;
                    sawGeneratedAt = true;
                    break;
                default:
                    // meta.dateRange is upstream metadata only; it is never used as the observed range.
                    SkipValue(ref reader);
                    break;
            }
        }

        if (!sawVersion)
        {
            throw Reject("meta.version", "missing_field", "Field meta.version is required.");
        }

        if (!string.Equals(builder.UpstreamVersion, TokscaleGraphContract.PinnedUpstreamVersion, StringComparison.Ordinal))
        {
            throw Reject(
                "meta.version",
                "unsupported_version",
                $"Only tokscale graph version {TokscaleGraphContract.PinnedUpstreamVersion} is supported.");
        }

        if (!sawGeneratedAt)
        {
            throw Reject("meta.generatedAt", "missing_field", "Field meta.generatedAt is required.");
        }
    }

    private static void ReadContributions(ref Utf8JsonReader reader, GraphBuilder builder)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw Reject("contributions", "invalid_type", "Field contributions must be an array.");
        }

        var index = 0;
        while (true)
        {
            reader.Read();
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                break;
            }

            ReadContribution(ref reader, builder, $"contributions[{index}]");
            index++;

            if (index > TokscaleGraphContract.MaxDays)
            {
                throw Reject(
                    "days",
                    "too_many_days",
                    $"A snapshot may contain at most {TokscaleGraphContract.MaxDays} days.");
            }
        }
    }

    private static void ReadContribution(ref Utf8JsonReader reader, GraphBuilder builder, string field)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw Reject(field, "invalid_type", $"{field} must be an object.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? date = null;
        long tokens = 0;
        var sawDate = false;
        var sawTotals = false;

        while (true)
        {
            reader.Read();
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            var name = PropertyName(ref reader);
            if (Array.IndexOf(ContributionKeys, name) < 0)
            {
                throw Reject($"{field}.{name}", "unexpected_field", $"{field}.{name} is not part of the pinned graph document.");
            }

            if (!seen.Add(name))
            {
                throw Reject($"{field}.{name}", "duplicate_key", $"{field}.{name} is repeated.");
            }

            reader.Read();
            switch (name)
            {
                case "date":
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        throw Reject($"{field}.date", "invalid_type", $"{field}.date must be a string.");
                    }

                    var raw = reader.GetString() ?? string.Empty;
                    if (!IsStrictDate(raw))
                    {
                        throw Reject($"{field}.date", "invalid_date", $"{field}.date must be a strict YYYY-MM-DD calendar date.");
                    }

                    date = raw;
                    sawDate = true;
                    break;
                case "totals":
                    tokens = ReadTotals(ref reader, $"{field}.totals");
                    sawTotals = true;
                    break;
                default:
                    // intensity, tokenBreakdown and clients are upstream detail; they are discarded.
                    SkipValue(ref reader);
                    break;
            }
        }

        if (!sawDate)
        {
            throw Reject($"{field}.date", "missing_field", $"{field}.date is required.");
        }

        if (!sawTotals)
        {
            throw Reject($"{field}.totals", "missing_field", $"{field}.totals is required.");
        }

        builder.AddDay(date!, tokens);
    }

    private static long ReadTotals(ref Utf8JsonReader reader, string field)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw Reject(field, "invalid_type", $"{field} must be an object.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sawTokens = false;
        long tokens = 0;

        while (true)
        {
            reader.Read();
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            var name = PropertyName(ref reader);
            if (Array.IndexOf(TotalsKeys, name) < 0)
            {
                throw Reject($"{field}.{name}", "unexpected_field", $"{field}.{name} is not part of the pinned graph document.");
            }

            if (!seen.Add(name))
            {
                throw Reject($"{field}.{name}", "duplicate_key", $"{field}.{name} is repeated.");
            }

            reader.Read();
            if (name == "tokens")
            {
                tokens = ReadTokenCount(ref reader, $"{field}.tokens");
                sawTokens = true;
            }
            else
            {
                // cost, messages and costProvenance are never mapped: this collector emits tokens only.
                SkipValue(ref reader);
            }
        }

        if (!sawTokens)
        {
            throw Reject($"{field}.tokens", "missing_field", $"{field}.tokens is required.");
        }

        return tokens;
    }

    private static long ReadTokenCount(ref Utf8JsonReader reader, string field)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            throw Reject(field, "invalid_type", $"{field} must be a JSON integer.");
        }

        if (!reader.TryGetInt64(out var value))
        {
            var hasFraction = !reader.HasValueSequence && reader.ValueSpan.IndexOfAny((byte)'.', (byte)'e', (byte)'E') >= 0;
            throw hasFraction
                ? Reject(field, "invalid_integer", $"{field} must be an integer, never a fraction or exponent.")
                : Reject(field, "out_of_range", $"{field} is outside the representable range.");
        }

        if (value < 0 || value > TokscaleGraphContract.MaxSafeInteger)
        {
            throw Reject(
                field,
                "out_of_range",
                $"{field} must be between 0 and {TokscaleGraphContract.MaxSafeInteger}.");
        }

        return value;
    }

    private static void SkipValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
        {
            return;
        }

        var depth = reader.CurrentDepth;
        while (reader.Read())
        {
            if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray && reader.CurrentDepth == depth)
            {
                return;
            }
        }

        throw Reject("$", "invalid_json", "The upstream document ended inside a value.");
    }

    private static string PropertyName(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.PropertyName)
        {
            throw Reject("$", "invalid_json", "The upstream document has an unexpected token where a field name was required.");
        }

        return reader.GetString() ?? string.Empty;
    }

    private static bool IsStrictDate(string raw)
    {
        if (raw.Length != 10 || raw[4] != '-' || raw[7] != '-')
        {
            return false;
        }

        if (!IsDigits(raw, 0, 4) || !IsDigits(raw, 5, 2) || !IsDigits(raw, 8, 2))
        {
            return false;
        }

        var year = ParseDigits(raw, 0, 4);
        var month = ParseDigits(raw, 5, 2);
        var day = ParseDigits(raw, 8, 2);
        if (year < TokscaleGraphContract.MinYear || year > TokscaleGraphContract.MaxYear)
        {
            return false;
        }

        return month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month);
    }

    private static bool TryCanonicalizeInstant(string raw, out string canonical)
    {
        canonical = string.Empty;
        if (raw.Length < 20)
        {
            return false;
        }

        if (raw[4] != '-' || raw[7] != '-' || raw[10] != 'T' || raw[13] != ':' || raw[16] != ':')
        {
            return false;
        }

        if (!IsDigits(raw, 0, 4) || !IsDigits(raw, 5, 2) || !IsDigits(raw, 8, 2)
            || !IsDigits(raw, 11, 2) || !IsDigits(raw, 14, 2) || !IsDigits(raw, 17, 2))
        {
            return false;
        }

        var position = 19;
        long fractionTicks = 0;
        if (position < raw.Length && raw[position] == '.')
        {
            position++;
            var start = position;
            while (position < raw.Length && IsDigit(raw[position]))
            {
                position++;
            }

            var digits = position - start;
            if (digits is < 1 or > 9)
            {
                return false;
            }

            var significant = raw.AsSpan(start, Math.Min(digits, 7)).ToString().PadRight(7, '0');
            fractionTicks = long.Parse(significant, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        if (position >= raw.Length)
        {
            return false;
        }

        TimeSpan offset;
        if (raw[position] == 'Z')
        {
            if (position + 1 != raw.Length)
            {
                return false;
            }

            offset = TimeSpan.Zero;
        }
        else if ((raw[position] == '+' || raw[position] == '-')
            && position + 6 == raw.Length
            && raw[position + 3] == ':'
            && IsDigits(raw, position + 1, 2)
            && IsDigits(raw, position + 4, 2))
        {
            var hours = ParseDigits(raw, position + 1, 2);
            var minutes = ParseDigits(raw, position + 4, 2);
            if (hours > 23 || minutes > 59)
            {
                return false;
            }

            offset = new TimeSpan(hours, minutes, 0);
            if (raw[position] == '-')
            {
                offset = offset.Negate();
            }
        }
        else
        {
            return false;
        }

        var year = ParseDigits(raw, 0, 4);
        if (year < TokscaleGraphContract.MinYear || year > TokscaleGraphContract.MaxYear)
        {
            return false;
        }

        DateTime local;
        try
        {
            local = new DateTime(year, ParseDigits(raw, 5, 2), ParseDigits(raw, 8, 2),
                ParseDigits(raw, 11, 2), ParseDigits(raw, 14, 2), ParseDigits(raw, 17, 2), DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        var utc = new DateTimeOffset(local.AddTicks(fractionTicks), offset).UtcDateTime;
        var truncated = new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
        if (truncated.Year < TokscaleGraphContract.MinYear || truncated.Year > TokscaleGraphContract.MaxYear)
        {
            return false;
        }

        canonical = truncated.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        return true;
    }

    private static bool IsDigit(char value) => value is >= '0' and <= '9';

    private static bool IsDigits(string raw, int start, int count)
    {
        for (var index = start; index < start + count; index++)
        {
            if (!IsDigit(raw[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static int ParseDigits(string raw, int start, int count) =>
        int.Parse(raw.AsSpan(start, count), NumberStyles.None, CultureInfo.InvariantCulture);

    private static RejectionException Reject(string field, string reason, string message) =>
        new(field, reason, message);

    private sealed class RejectionException(string field, string reason, string message) : Exception(message)
    {
        internal string Field { get; } = field;

        internal string Reason { get; } = reason;
    }

    private sealed class GraphBuilder
    {
        private readonly List<(string Date, long Tokens)> _days = [];

        internal string? UpstreamVersion { get; set; }

        internal string? CollectedAt { get; set; }

        internal void AddDay(string date, long tokens) => _days.Add((date, tokens));

        internal NormalizationResult Build()
        {
            if (_days.Count == 0)
            {
                return NormalizationResult.NoObservedDays(
                    "no_dated_rows",
                    "The upstream document contained no dated contribution row.");
            }

            var ordered = _days.OrderBy(day => day.Date, StringComparer.Ordinal).ToList();
            for (var index = 1; index < ordered.Count; index++)
            {
                if (string.Equals(ordered[index].Date, ordered[index - 1].Date, StringComparison.Ordinal))
                {
                    return NormalizationResult.Rejected(
                        "contributions",
                        "duplicate_date",
                        $"Day {ordered[index].Date} is listed more than once.");
                }
            }

            if (ordered.Count > TokscaleGraphContract.MaxDays)
            {
                return NormalizationResult.Rejected(
                    "days",
                    "too_many_days",
                    $"A snapshot may contain at most {TokscaleGraphContract.MaxDays} days.");
            }

            var from = ordered[0].Date;
            var to = ordered[^1].Date;
            if (DayNumber(to) - DayNumber(from) > TokscaleGraphContract.MaxDays - 1)
            {
                return NormalizationResult.Rejected(
                    "days",
                    "range_too_long",
                    $"The covered range must not exceed {TokscaleGraphContract.MaxDays} days.");
            }

            var days = ordered.Select(day => new SnapshotDay(day.Date, day.Tokens)).ToArray();
            return NormalizationResult.FromCandidate(new SnapshotCandidate(
                TokscaleGraphContract.SchemaVersion,
                TokscaleGraphContract.PinnedAdapterVersion,
                CollectedAt!,
                TokscaleGraphContract.SupportedBucketTimeZone,
                from,
                to,
                days));
        }

        private static int DayNumber(string date) =>
            DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).DayNumber;
    }
}

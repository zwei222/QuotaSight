using System.Globalization;
using System.Text.Json;

namespace QuotaSight.Collector;

/// <summary>
/// Bounded, strict reader for the pinned <c>tokscale graph</c> document
/// (version 4.23.0) that produces an API wire candidate snapshot.
///
/// Rules, all deliberate:
/// <list type="bullet">
/// <item>Only <c>meta.version</c>, <c>meta.generatedAt</c>, <c>contributions[].date</c>,
/// <c>contributions[].totals.tokens</c> and (for wire v2 only)
/// <c>contributions[].clients[]</c> are consumed; every other value is skipped and
/// never echoed.</item>
/// <item>The requested <see cref="WireSchema"/> decides whether <c>clients[]</c> is
/// read at all. For wire v1 it is skipped exactly as it always was, so a document
/// whose client rows changed shape still produces the daily totals it produced
/// before the breakdown existed.</item>
/// <item>For wire v2, client rows are reduced to the five token components and a
/// normalized (client, provider, model) key. Costs, message counts and anything
/// else upstream carries are never mapped. A day whose client rows cannot be read
/// is sent with its total only and counted, rather than rejecting the whole
/// snapshot: withholding one day's breakdown is a smaller loss than sending
/// nothing, and guessing the rows from a broken shape is not an option.</item>
/// <item>The allow-lists are the observed 4.23.0 output, not a wish list:
/// <c>contributions[].activeTimeMs</c> appears on the days that had a measured
/// session, and <c>contributions[].clients[].costProvenance</c> appears on every
/// client row. Both are read for type and bounds and then discarded, so a real
/// document is neither rejected for carrying them nor able to smuggle a value
/// into the wire through them.</item>
/// <item>A day's breakdown is kept only when it explains the day's total exactly
/// and fits one day's row bound; otherwise that day is sent with its total only and
/// the withheld day is counted.</item>
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
    private static readonly string[] ContributionKeys = ["date", "totals", "intensity", "tokenBreakdown", "clients", "activeTimeMs"];
    private static readonly string[] TotalsKeys = ["tokens", "cost", "messages", "costProvenance"];
    private static readonly string[] ClientKeys = ["client", "modelId", "providerId", "tokens", "cost", "messages", "costProvenance"];
    private static readonly string[] ClientTokenKeys = ["input", "output", "cacheRead", "cacheWrite", "reasoning"];

    /// <summary>
    /// Upper bound on upstream client rows read for one day, before normalization.
    ///
    /// Several raw rows may collapse into one key, so this is deliberately larger
    /// than <see cref="TokscaleGraphContract.MaxPartsPerDay"/>; it exists so a
    /// pathological document is rejected as a bounded read instead of exhausting
    /// memory.
    /// </summary>
    private const int MaxClientRowsPerDay = 512;

    /// <summary>Normalizes one bounded upstream document for wire v1.</summary>
    /// <param name="utf8">UTF-8 bytes of the whole document; no more than the input bound.</param>
    /// <returns>A candidate, a no-dated-rows outcome, or a rejection.</returns>
    public static NormalizationResult Normalize(ReadOnlySpan<byte> utf8) =>
        Normalize(utf8, WireSchema.V1);

    /// <summary>
    /// Normalizes one bounded upstream document for the requested wire version.
    ///
    /// The version only decides whether <c>contributions[].clients[]</c> is read:
    /// wire v1 carries daily totals and skips that field, wire v2 carries the
    /// breakdown as well.
    /// </summary>
    /// <param name="utf8">UTF-8 bytes of the whole document; no more than the input bound.</param>
    /// <param name="schema">Wire version the candidate will be serialized into.</param>
    /// <returns>A candidate, a no-dated-rows outcome, or a rejection.</returns>
    public static NormalizationResult Normalize(ReadOnlySpan<byte> utf8, WireSchema schema)
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
            ReadDocument(ref reader, builder, schema);

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

    private static void ReadDocument(ref Utf8JsonReader reader, GraphBuilder builder, WireSchema schema)
    {
        if (!reader.Read())
        {
            throw Reject("$", "invalid_json", "The upstream document is empty.");
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw Reject("$", "invalid_body", "The upstream document root must be a JSON object.");
        }

        ReadRootObject(ref reader, builder, schema);
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

    private static void ReadRootObject(ref Utf8JsonReader reader, GraphBuilder builder, WireSchema schema)
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
                    ReadContributions(ref reader, builder, schema);
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

    private static void ReadContributions(ref Utf8JsonReader reader, GraphBuilder builder, WireSchema schema)
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

            ReadContribution(ref reader, builder, $"contributions[{index}]", schema);
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

    private static void ReadContribution(ref Utf8JsonReader reader, GraphBuilder builder, string field, WireSchema schema)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw Reject(field, "invalid_type", $"{field} must be an object.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? date = null;
        long tokens = 0;
        List<SnapshotPart>? clients = null;
        var clientsWithheld = false;
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
                case "clients":
                    if (schema == WireSchema.V1)
                    {
                        // Wire v1 has carried this field since before the breakdown
                        // existed and never read past it: the daily totals do not
                        // depend on it, so a changed shape is not a reason to reject
                        // a document v1 would have accepted before.
                        SkipValue(ref reader);
                        break;
                    }

                    clients = ReadClientsOrWithhold(ref reader, $"{field}.clients");
                    clientsWithheld = clients is null;
                    break;
                case "activeTimeMs":
                    // Optional upstream detail: present on the days that had a
                    // measured session and absent on the rest. It is read so a
                    // malformed value is still rejected (this is a pinned shape, not
                    // a permissive reader), and discarded: the collector emits
                    // tokens only. Both wire versions validate it, because the
                    // document either matches the pinned shape or it does not.
                    ReadTokenCount(ref reader, $"{field}.activeTimeMs");
                    break;
                default:
                    // intensity and tokenBreakdown are upstream detail; they are discarded.
                    // The day's tokenBreakdown always equals totals.tokens, so it adds
                    // nothing the client rows do not already say.
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

        builder.AddDay(date!, tokens, clients ?? [], clientsWithheld);
    }

    /// <summary>
    /// Reads one day's client rows, or returns <c>null</c> when they cannot be read.
    ///
    /// A malformed day is withheld rather than fatal for the snapshot, which is the
    /// same rule the rest of this file follows: a day whose breakdown is missing is
    /// still a true daily total, while a rejected snapshot is no data at all. The
    /// reader is left positioned on the end of the `clients` value either way, so
    /// the rest of the day is read normally.
    /// </summary>
    /// <param name="reader">Reader positioned on the start of the value.</param>
    /// <param name="field">Dotted path, used only for the skipped value's bounds.</param>
    private static List<SnapshotPart>? ReadClientsOrWithhold(ref Utf8JsonReader reader, string field)
    {
        var valueDepth = reader.CurrentDepth;
        try
        {
            return ReadClients(ref reader, field);
        }
        catch (RejectionException)
        {
            SkipToEndOfValue(ref reader, valueDepth);
            return null;
        }
    }

    /// <summary>
    /// Consumes the remainder of a value whose start token is at <paramref name="valueDepth"/>.
    ///
    /// Used only after a failed read of that value: the reader may be anywhere
    /// inside it, so this walks forward to the token that closes it. A scalar value
    /// has nothing to consume; a value the reader has already left is left alone.
    /// </summary>
    private static void SkipToEndOfValue(ref Utf8JsonReader reader, int valueDepth)
    {
        if (reader.CurrentDepth == valueDepth
            && reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
        {
            return;
        }

        while (reader.Read())
        {
            if (reader.CurrentDepth == valueDepth
                && reader.TokenType is JsonTokenType.EndArray or JsonTokenType.EndObject)
            {
                return;
            }

            if (reader.CurrentDepth < valueDepth)
            {
                // The value closed without a token at its own depth (a scalar that
                // was the last value of its parent); nothing is left to skip.
                return;
            }
        }

        throw Reject("$", "invalid_json", "The upstream document ended inside a value.");
    }

    /// <summary>
    /// Reads <c>contributions[].clients</c>: one row per (client, model, provider)
    /// tuple as upstream saw it. Every row must name its token components; a row
    /// that does not is rejected rather than treated as zeroes, because a zero would
    /// be indistinguishable from an observed zero.
    /// </summary>
    private static List<SnapshotPart> ReadClients(ref Utf8JsonReader reader, string field)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw Reject(field, "invalid_type", $"{field} must be an array.");
        }

        var rows = new List<SnapshotPart>();
        while (true)
        {
            reader.Read();
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return rows;
            }

            rows.Add(ReadClient(ref reader, $"{field}[{rows.Count}]"));
            if (rows.Count > MaxClientRowsPerDay)
            {
                throw Reject(
                    field,
                    "too_many_clients",
                    $"{field} may contain at most {MaxClientRowsPerDay} rows.");
            }
        }
    }

    private static SnapshotPart ReadClient(ref Utf8JsonReader reader, string field)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw Reject(field, "invalid_type", $"{field} must be an object.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? client = null;
        string? modelId = null;
        string? providerId = null;
        var components = new long[ClientTokenKeys.Length];
        var sawTokens = false;

        while (true)
        {
            reader.Read();
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            var name = PropertyName(ref reader);
            if (Array.IndexOf(ClientKeys, name) < 0)
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
                case "client":
                    client = ReadIdentifierString(ref reader, $"{field}.client");
                    break;
                case "modelId":
                    modelId = ReadIdentifierString(ref reader, $"{field}.modelId");
                    break;
                case "providerId":
                    providerId = ReadIdentifierString(ref reader, $"{field}.providerId");
                    break;
                case "tokens":
                    ReadClientTokens(ref reader, $"{field}.tokens", components);
                    sawTokens = true;
                    break;
                default:
                    // cost, messages and costProvenance are never mapped: this collector emits tokens only.
                    SkipValue(ref reader);
                    break;
            }
        }

        if (!sawTokens)
        {
            throw Reject($"{field}.tokens", "missing_field", $"{field}.tokens is required.");
        }

        // A missing identifier is normalized exactly like a value that failed the
        // pattern: unknown, never guessed from neighbouring rows.
        var key = new SnapshotPartKey(
            PartIdentifiers.NormalizeClient(client),
            PartIdentifiers.NormalizeProvider(providerId),
            PartIdentifiers.NormalizeModel(modelId));

        return new SnapshotPart(key, components[0], components[1], components[2], components[3], components[4]);
    }

    private static string ReadIdentifierString(ref Utf8JsonReader reader, string field)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw Reject(field, "invalid_type", $"{field} must be a string.");
        }

        return reader.GetString() ?? string.Empty;
    }

    private static void ReadClientTokens(ref Utf8JsonReader reader, string field, long[] components)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw Reject(field, "invalid_type", $"{field} must be an object.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            reader.Read();
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            var name = PropertyName(ref reader);
            var slot = Array.IndexOf(ClientTokenKeys, name);
            if (slot < 0)
            {
                throw Reject($"{field}.{name}", "unexpected_field", $"{field}.{name} is not part of the pinned graph document.");
            }

            if (!seen.Add(name))
            {
                throw Reject($"{field}.{name}", "duplicate_key", $"{field}.{name} is repeated.");
            }

            reader.Read();
            components[slot] = ReadTokenCount(ref reader, $"{field}.{name}");
        }

        foreach (var name in ClientTokenKeys)
        {
            if (!seen.Contains(name))
            {
                throw Reject($"{field}.{name}", "missing_field", $"{field}.{name} is required.");
            }
        }
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
        private readonly List<(string Date, long Tokens, List<SnapshotPart> Clients, bool Withheld)> _days = [];

        internal string? UpstreamVersion { get; set; }

        internal string? CollectedAt { get; set; }

        /// <summary>
        /// Records one dated day.
        /// </summary>
        /// <param name="date">Strict date of the row.</param>
        /// <param name="tokens">Observed total for the day.</param>
        /// <param name="clients">Client rows, empty when upstream had none.</param>
        /// <param name="withheld">
        /// True when the client rows existed but could not be read; the day is sent
        /// with its total only and counted as withheld.
        /// </param>
        internal void AddDay(string date, long tokens, List<SnapshotPart> clients, bool withheld) =>
            _days.Add((date, tokens, clients, withheld));

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

            var omitted = 0;
            var days = ordered
                .Select(day =>
                {
                    if (day.Withheld)
                    {
                        // The rows existed but were unreadable: withhold the day's
                        // breakdown and count it, instead of sending nothing.
                        omitted++;
                        return new SnapshotDay(day.Date, day.Tokens);
                    }

                    return new SnapshotDay(day.Date, day.Tokens, BuildParts(day.Tokens, day.Clients, ref omitted));
                })
                .ToArray();

            return NormalizationResult.FromCandidate(new SnapshotCandidate(
                TokscaleGraphContract.SchemaVersion,
                TokscaleGraphContract.PinnedAdapterVersion,
                CollectedAt!,
                TokscaleGraphContract.SupportedBucketTimeZone,
                from,
                to,
                days,
                omitted));
        }

        /// <summary>
        /// Builds one day's breakdown, or <c>null</c> when the day must be sent with
        /// its total only.
        ///
        /// Four rules, all of them "withhold rather than guess":
        /// <list type="bullet">
        /// <item>Upstream reported no client rows for the day — nothing to attribute.</item>
        /// <item>The client rows do not add up to the day total. The API requires the
        /// parts to explain the whole day, and adjusting a component to close the gap
        /// would be inventing a number; the day is therefore sent without parts.</item>
        /// <item>More distinct keys than one day may carry — a truncated list would
        /// silently drop real usage.</item>
        /// <item>A component sum overflows — the row cannot be represented, so the day
        /// is sent with its total only rather than with a wrapped number.</item>
        /// </list>
        /// Rows that normalize to the same key (the same client/model/provider under
        /// two spellings, or several rows for one key) are summed component by
        /// component, which is the only addition the document itself supports.
        /// </summary>
        private static List<SnapshotPart>? BuildParts(long dayTotal, List<SnapshotPart> clients, ref int omitted)
        {
            if (clients.Count == 0)
            {
                return null;
            }

            var sums = new Dictionary<SnapshotPartKey, SnapshotPart>();
            try
            {
                foreach (var client in clients)
                {
                    if (sums.TryGetValue(client.Key, out var existing))
                    {
                        sums[client.Key] = new SnapshotPart(
                            client.Key,
                            checked(existing.Input + client.Input),
                            checked(existing.Output + client.Output),
                            checked(existing.CacheRead + client.CacheRead),
                            checked(existing.CacheWrite + client.CacheWrite),
                            checked(existing.Reasoning + client.Reasoning));
                    }
                    else
                    {
                        sums[client.Key] = client;
                    }
                }
            }
            catch (OverflowException)
            {
                // Two rows for one key that together exceed the representable range
                // cannot be sent as one row, and splitting them would invent keys.
                omitted++;
                return null;
            }

            var parts = sums.Values.OrderBy(part => part.Key).ToList();
            if (parts.Count > TokscaleGraphContract.MaxPartsPerDay)
            {
                omitted++;
                return null;
            }

            long attributed = 0;
            try
            {
                foreach (var part in parts)
                {
                    attributed = checked(attributed + part.Total);
                }
            }
            catch (OverflowException)
            {
                omitted++;
                return null;
            }

            if (attributed != dayTotal)
            {
                omitted++;
                return null;
            }

            return parts;
        }

        private static int DayNumber(string date) =>
            DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).DayNumber;
    }
}

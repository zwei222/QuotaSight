using System.Globalization;
using System.Text;

namespace QuotaSight.Collector;

/// <summary>Which API wire contract a snapshot is serialized into.</summary>
public enum WireSchema
{
    /// <summary>Daily totals only (<c>schemaVersion: 1</c>). The default.</summary>
    V1 = 1,

    /// <summary>Daily totals plus the per-key breakdown (<c>schemaVersion: 2</c>).</summary>
    V2 = 2,
}

/// <summary>
/// Serialized wire bytes plus the number of days whose breakdown was withheld.
/// </summary>
/// <param name="Bytes">UTF-8 JSON with a single trailing <c>\n</c>.</param>
/// <param name="OmittedPartDays">
/// Days that are sent with their total only: those withheld during normalization
/// (client rows that did not explain the day, or too many keys) plus those dropped
/// here to stay inside the key, row and body bounds.
/// </param>
public sealed record WireSnapshot(byte[] Bytes, int OmittedPartDays);

/// <summary>
/// Canonical serializer for the local API wire
/// (<c>src/QuotaSight.Usage.Api/src/schema.ts</c>).
///
/// Only allowlisted fields are ever written, in the API's own key order, with a
/// single trailing newline. Extra upstream text, costs, clients and metadata can
/// therefore never reach stdout.
///
/// Wire v2 adds `keys` (one `[client, provider, model]` triple per normalized key,
/// ascending and de-duplicated) and, per day, an optional `parts` array of
/// `[keyIndex, input, output, cacheRead, cacheWrite, reasoning]` rows in ascending
/// key order. Both are derived here and nowhere else, so the key indices a day
/// refers to always describe the `keys` array that is written beside them.
/// </summary>
public static class SnapshotWire
{
    /// <summary>Serializes one candidate into the default (v1) wire.</summary>
    /// <param name="candidate">Normalized candidate snapshot.</param>
    /// <param name="generation">Operator-supplied collector generation (positive safe integer).</param>
    /// <returns>Compact UTF-8 JSON followed by <c>\n</c>.</returns>
    public static byte[] Serialize(SnapshotCandidate candidate, long generation) =>
        Serialize(candidate, generation, WireSchema.V1).Bytes;

    /// <summary>Serializes one candidate for <c>PUT /v1/collectors/{id}/snapshots/{revision}</c>.</summary>
    /// <param name="candidate">Normalized candidate snapshot.</param>
    /// <param name="generation">Operator-supplied collector generation (positive safe integer).</param>
    /// <param name="schema">Wire contract to emit.</param>
    /// <returns>The wire bytes and how many days had their breakdown withheld.</returns>
    public static WireSnapshot Serialize(SnapshotCandidate candidate, long generation, WireSchema schema)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (generation < 1 || generation > TokscaleGraphContract.MaxSafeInteger)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation),
                generation,
                "generation must be a positive safe integer.");
        }

        if (!string.Equals(candidate.BucketTimeZone, TokscaleGraphContract.SupportedBucketTimeZone, StringComparison.Ordinal))
        {
            throw new ArgumentException("bucketTimeZone must be the API v1 bucket time zone.", nameof(candidate));
        }

        if (candidate.SchemaVersion != TokscaleGraphContract.SchemaVersion)
        {
            throw new ArgumentException("schemaVersion must be the API v1 schema version.", nameof(candidate));
        }

        if (!string.Equals(candidate.AdapterVersion, TokscaleGraphContract.PinnedAdapterVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException("adapterVersion must be the pinned adapter version.", nameof(candidate));
        }

        if (schema == WireSchema.V2)
        {
            return SerializeV2(candidate, generation);
        }

        // Wire v1 never sends a breakdown, so there is nothing withheld to report:
        // the count exists for operators who asked for v2 and did not get all of it.
        return new WireSnapshot(SerializeV1(candidate, generation), 0);
    }

    /// <summary>Decodes wire bytes for comparison and diagnostics.</summary>
    /// <param name="wire">Bytes produced by <see cref="Serialize(SnapshotCandidate, long)"/>.</param>
    public static string Text(ReadOnlySpan<byte> wire) => Encoding.UTF8.GetString(wire);

    private static byte[] SerializeV1(SnapshotCandidate candidate, long generation)
    {
        var builder = new StringBuilder(candidate.Days.Count * 48 + 192);
        builder.Append("{\"schemaVersion\":").Append(TokscaleGraphContract.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        AppendEnvelope(builder, candidate, generation);
        builder.Append(",\"days\":[");
        for (var index = 0; index < candidate.Days.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            var day = candidate.Days[index];
            builder.Append("{\"date\":\"").Append(day.Date).Append("\",\"totalTokens\":")
                .Append(day.TotalTokens.ToString(CultureInfo.InvariantCulture)).Append('}');
        }

        builder.Append("]}\n");

        var wire = Encoding.UTF8.GetBytes(builder.ToString());
        if (wire.Length > TokscaleGraphContract.MaxOutputBytes)
        {
            throw new InvalidOperationException(
                $"The serialized snapshot exceeds the {TokscaleGraphContract.MaxOutputBytes}-byte API body bound.");
        }

        Validate(wire, generation, candidate);
        return wire;
    }

    /// <summary>
    /// Serializes wire v2, reducing the breakdown until it fits.
    ///
    /// Reduction order is fixed and explained: the oldest days lose their breakdown
    /// first. A reader is looking at recency, so losing the breakdown of an old day
    /// costs less than losing it for today, and the day totals themselves are never
    /// dropped — the snapshot always explains every day it covers.
    /// </summary>
    private static WireSnapshot SerializeV2(SnapshotCandidate candidate, long generation)
    {
        var omitted = candidate.OmittedPartDays;
        var retained = new List<List<SnapshotPart>?>(candidate.Days.Count);
        foreach (var day in candidate.Days)
        {
            // The API accepts at most one day's row bound; a candidate that exceeds
            // it would be rejected as a whole, so that day loses its breakdown here.
            if (day.Parts is { Count: > TokscaleGraphContract.MaxPartsPerDay })
            {
                retained.Add(null);
                omitted++;
                continue;
            }

            retained.Add(day.Parts?.ToList());
        }

        while (true)
        {
            var keys = CollectKeys(retained);
            var totalParts = 0;
            foreach (var day in retained)
            {
                totalParts += day?.Count ?? 0;
            }

            var wire = WriteV2(candidate, generation, retained, keys);
            if (wire.Length <= TokscaleGraphContract.MaxPartsBodyBytes
                && keys.Count <= TokscaleGraphContract.MaxKeys
                && totalParts <= TokscaleGraphContract.MaxPartsPerSnapshot)
            {
                if (wire.Length > TokscaleGraphContract.MaxOutputBytes)
                {
                    throw new InvalidOperationException(
                        $"The serialized snapshot exceeds the {TokscaleGraphContract.MaxOutputBytes}-byte API body bound.");
                }

                ValidateV2(wire, generation, candidate, keys.Count, totalParts);
                return new WireSnapshot(wire, omitted);
            }

            var dropped = false;
            for (var index = 0; index < retained.Count; index++)
            {
                if (retained[index] is null)
                {
                    continue;
                }

                retained[index] = null;
                omitted++;
                dropped = true;
                break;
            }

            if (!dropped)
            {
                // No breakdown is left, so only the day totals remain; if those do
                // not fit, the snapshot cannot be sent at all rather than sent short.
                throw new InvalidOperationException(
                    $"The day totals alone exceed the {TokscaleGraphContract.MaxPartsBodyBytes}-byte body budget.");
            }
        }
    }

    private static byte[] WriteV2(
        SnapshotCandidate candidate,
        long generation,
        List<List<SnapshotPart>?> retained,
        List<SnapshotPartKey> keys)
    {
        var indexByKey = new Dictionary<SnapshotPartKey, int>(keys.Count);
        for (var index = 0; index < keys.Count; index++)
        {
            indexByKey[keys[index]] = index;
        }

        var builder = new StringBuilder(256 + keys.Count * 48 + candidate.Days.Count * 64);
        builder.Append("{\"schemaVersion\":").Append(TokscaleGraphContract.SchemaVersionParts.ToString(CultureInfo.InvariantCulture));
        AppendEnvelope(builder, candidate, generation);
        builder.Append(",\"keys\":[");
        for (var index = 0; index < keys.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            var key = keys[index];
            // Identifiers are normalized slugs; the check is a guard against a
            // normalization bug producing a byte that would need JSON escaping.
            AssertPlainIdentifier(key.Client);
            AssertPlainIdentifier(key.Provider);
            AssertPlainIdentifier(key.Model);
            builder.Append("[\"").Append(key.Client).Append("\",\"")
                .Append(key.Provider).Append("\",\"")
                .Append(key.Model).Append("\"]");
        }

        builder.Append("],\"days\":[");
        for (var dayIndex = 0; dayIndex < candidate.Days.Count; dayIndex++)
        {
            if (dayIndex > 0)
            {
                builder.Append(',');
            }

            var day = candidate.Days[dayIndex];
            builder.Append("{\"date\":\"").Append(day.Date).Append("\",\"totalTokens\":")
                .Append(day.TotalTokens.ToString(CultureInfo.InvariantCulture));

            var parts = retained[dayIndex];
            if (parts is not null && parts.Count > 0)
            {
                builder.Append(",\"parts\":[");
                for (var partIndex = 0; partIndex < parts.Count; partIndex++)
                {
                    if (partIndex > 0)
                    {
                        builder.Append(',');
                    }

                    var part = parts[partIndex];
                    builder.Append('[')
                        .Append(indexByKey[part.Key].ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(part.Input.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(part.Output.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(part.CacheRead.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(part.CacheWrite.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(part.Reasoning.ToString(CultureInfo.InvariantCulture)).Append(']');
                }

                builder.Append(']');
            }

            builder.Append('}');
        }

        builder.Append("]}\n");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>
    /// Collects the distinct keys of the retained breakdown, in the ascending order
    /// the API requires. A key that no day references is never emitted, because the
    /// API rejects a snapshot with an unreferenced key.
    /// </summary>
    private static List<SnapshotPartKey> CollectKeys(List<List<SnapshotPart>?> retained)
    {
        var distinct = new SortedSet<SnapshotPartKey>();
        foreach (var day in retained)
        {
            if (day is null)
            {
                continue;
            }

            foreach (var part in day)
            {
                distinct.Add(part.Key);
            }
        }

        return [.. distinct];
    }

    private static void AppendEnvelope(StringBuilder builder, SnapshotCandidate candidate, long generation)
    {
        builder.Append(",\"adapterVersion\":\"").Append(candidate.AdapterVersion).Append('"');
        builder.Append(",\"generation\":").Append(generation.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"collectedAt\":\"").Append(candidate.CollectedAt).Append('"');
        builder.Append(",\"bucketTimeZone\":\"").Append(candidate.BucketTimeZone).Append('"');
        builder.Append(",\"coveredRange\":{\"from\":\"").Append(candidate.CoveredFrom).Append('"');
        builder.Append(",\"to\":\"").Append(candidate.CoveredTo).Append("\"}");
    }

    private static void AssertPlainIdentifier(string value)
    {
        foreach (var character in value)
        {
            if (character is '"' or '\\' || character < 0x20 || character > 0x7e)
            {
                throw new InvalidOperationException(
                    "A normalized identifier contains a character that would need JSON escaping.");
            }
        }
    }

    private static void Validate(byte[] wire, long generation, SnapshotCandidate candidate)
    {
        using var document = System.Text.Json.JsonDocument.Parse(wire);
        var root = document.RootElement;
        var names = root.EnumerateObject().Select(property => property.Name).ToArray();
        string[] expected = ["schemaVersion", "adapterVersion", "generation", "collectedAt", "bucketTimeZone", "coveredRange", "days"];
        if (!names.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The serialized snapshot root does not carry exactly the API v1 fields.");
        }

        if (root.GetProperty("generation").GetInt64() != generation)
        {
            throw new InvalidOperationException("The serialized generation does not match the requested generation.");
        }

        if (root.GetProperty("days").GetArrayLength() != candidate.Days.Count)
        {
            throw new InvalidOperationException("The serialized day count does not match the candidate.");
        }
    }

    private static void ValidateV2(byte[] wire, long generation, SnapshotCandidate candidate, int keyCount, int partCount)
    {
        using var document = System.Text.Json.JsonDocument.Parse(wire);
        var root = document.RootElement;
        var names = root.EnumerateObject().Select(property => property.Name).ToArray();
        string[] expected =
        [
            "schemaVersion",
            "adapterVersion",
            "generation",
            "collectedAt",
            "bucketTimeZone",
            "coveredRange",
            "keys",
            "days",
        ];
        if (!names.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The serialized snapshot root does not carry exactly the API v2 fields.");
        }

        if (root.GetProperty("schemaVersion").GetInt32() != TokscaleGraphContract.SchemaVersionParts)
        {
            throw new InvalidOperationException("The serialized snapshot is not wire v2.");
        }

        if (root.GetProperty("generation").GetInt64() != generation)
        {
            throw new InvalidOperationException("The serialized generation does not match the requested generation.");
        }

        var keys = root.GetProperty("keys");
        if (keys.GetArrayLength() != keyCount)
        {
            throw new InvalidOperationException("The serialized key count does not match the retained breakdown.");
        }

        var days = root.GetProperty("days");
        if (days.GetArrayLength() != candidate.Days.Count)
        {
            throw new InvalidOperationException("The serialized day count does not match the candidate.");
        }

        var written = 0;
        foreach (var day in days.EnumerateArray())
        {
            if (day.TryGetProperty("parts", out var parts))
            {
                written += parts.GetArrayLength();
            }
        }

        if (written != partCount)
        {
            throw new InvalidOperationException("The serialized breakdown row count does not match the retained breakdown.");
        }
    }
}

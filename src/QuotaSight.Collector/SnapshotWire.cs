using System.Globalization;
using System.Text;

namespace QuotaSight.Collector;

/// <summary>
/// Canonical serializer for the local API wire v1
/// (<c>src/QuotaSight.Usage.Api/src/schema.ts</c>).
///
/// Only allowlisted fields are ever written, in the API's own key order, with a
/// single trailing newline. Extra upstream text, costs, clients and metadata can
/// therefore never reach stdout.
/// </summary>
public static class SnapshotWire
{
    /// <summary>Serializes one candidate for <c>PUT /v1/collectors/{id}/snapshots/{revision}</c>.</summary>
    /// <param name="candidate">Normalized candidate snapshot.</param>
    /// <param name="generation">Operator-supplied collector generation (positive safe integer).</param>
    /// <returns>Compact UTF-8 JSON followed by <c>\n</c>.</returns>
    public static byte[] Serialize(SnapshotCandidate candidate, long generation)
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

        var builder = new StringBuilder(candidate.Days.Count * 48 + 192);
        builder.Append("{\"schemaVersion\":").Append(candidate.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"adapterVersion\":\"").Append(candidate.AdapterVersion).Append('"');
        builder.Append(",\"generation\":").Append(generation.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"collectedAt\":\"").Append(candidate.CollectedAt).Append('"');
        builder.Append(",\"bucketTimeZone\":\"").Append(candidate.BucketTimeZone).Append('"');
        builder.Append(",\"coveredRange\":{\"from\":\"").Append(candidate.CoveredFrom).Append('"');
        builder.Append(",\"to\":\"").Append(candidate.CoveredTo).Append("\"}");
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

    /// <summary>Decodes wire bytes for comparison and diagnostics.</summary>
    /// <param name="wire">Bytes produced by <see cref="Serialize"/>.</param>
    public static string Text(ReadOnlySpan<byte> wire) => Encoding.UTF8.GetString(wire);

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
}

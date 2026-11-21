using System.Globalization;
using System.Text;
using System.Text.Json;

namespace QuotaSight.Collector.Tests;

/// <summary>
/// Wire v2, asserted end to end: the same bytes the API test stores, the ordering
/// and reference rules the API enforces, and the reduction the collector performs
/// when a snapshot carries more breakdown than the body budget allows.
/// </summary>
public sealed class SnapshotWireV2Tests
{
    private static SnapshotCandidate PartsCandidate() =>
        Assert.IsType<SnapshotCandidate>(
            TokscaleGraphNormalizer.Normalize(FixtureFiles.Bytes(FixtureFiles.Parts), WireSchema.V2).Candidate);

    [Fact]
    public void EmitsExactlyTheBytesTheApiTestStores()
    {
        var wire = SnapshotWire.Serialize(PartsCandidate(), generation: 1, WireSchema.V2);

        Assert.Equal(Encoding.UTF8.GetString(FixtureFiles.SharedWireV2Golden()), SnapshotWire.Text(wire.Bytes));
        Assert.Equal(0, wire.OmittedPartDays);
    }

    [Fact]
    public void KeysAreAscendingDistinctAndAllReferenced()
    {
        using var document = JsonDocument.Parse(SnapshotWire.Serialize(PartsCandidate(), 1, WireSchema.V2).Bytes);
        var keys = document.RootElement.GetProperty("keys").EnumerateArray().ToList();

        var triples = keys
            .Select(key => key.EnumerateArray().Select(value => value.GetString()!).ToArray())
            .Select(triple => string.Join('\u0000', triple))
            .ToList();
        Assert.Equal(triples.OrderBy(value => value, StringComparer.Ordinal), triples);
        Assert.Equal(triples.Count, triples.Distinct(StringComparer.Ordinal).Count());

        var referenced = new HashSet<int>();
        foreach (var day in document.RootElement.GetProperty("days").EnumerateArray())
        {
            var index = -1;
            foreach (var part in day.GetProperty("parts").EnumerateArray())
            {
                var keyIndex = part[0].GetInt32();
                Assert.InRange(keyIndex, 0, keys.Count - 1);
                Assert.True(keyIndex > index, "part key indices must ascend inside a day");
                index = keyIndex;
                referenced.Add(keyIndex);
            }
        }

        Assert.Equal(keys.Count, referenced.Count);
    }

    [Fact]
    public void EveryDayExplainsItsTotalAndTheBreakdownIsNotRestated()
    {
        using var document = JsonDocument.Parse(SnapshotWire.Serialize(PartsCandidate(), 1, WireSchema.V2).Bytes);

        foreach (var day in document.RootElement.GetProperty("days").EnumerateArray())
        {
            var total = day.GetProperty("totalTokens").GetInt64();
            long sum = 0;
            foreach (var part in day.GetProperty("parts").EnumerateArray())
            {
                Assert.Equal(6, part.GetArrayLength());
                for (var slot = 1; slot < 6; slot++)
                {
                    sum += part[slot].GetInt64();
                }
            }

            Assert.Equal(total, sum);
        }

        var text = Encoding.UTF8.GetString(SnapshotWire.Serialize(PartsCandidate(), 1, WireSchema.V2).Bytes);
        foreach (var forbidden in new[] { "cost", "messages", "costProvenance", "totalCost", "summary" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BodyStaysInsideTheApiBoundAndInsideTheCollectorsOwnBudget()
    {
        var wire = SnapshotWire.Serialize(PartsCandidate(), 1, WireSchema.V2);

        Assert.InRange(wire.Bytes.Length, 1, TokscaleGraphContract.DefaultPartsBodyBytes);
        Assert.InRange(wire.Bytes.Length, 1, TokscaleGraphContract.MaxOutputBytes);
        Assert.Equal((byte)'{', wire.Bytes[0]);
        Assert.Equal((byte)'\n', wire.Bytes[^1]);
    }

    [Fact]
    public void TheDefaultPartsBudgetIs16KiBUnderTheBodyCeiling()
    {
        // Measured on 2026-11-22 in one account: the heaviest v2 write (about
        // 31.9 KiB) sat at a 7.5 ms median with three of eight runs over 10 ms,
        // while the same shape at 16 KiB stayed at a 3.5 ms median and a 7 ms
        // maximum. The default therefore moved to 16 KiB; the accepted range is
        // unchanged, so an operator can still choose anything from 4 to 32 KiB.
        Assert.Equal(16 * 1024, TokscaleGraphContract.DefaultPartsBodyBytes);
        Assert.Equal(4 * 1024, TokscaleGraphContract.MinPartsBudgetBytes);
        Assert.Equal(32 * 1024, TokscaleGraphContract.MaxPartsBudgetBytes);
    }

    [Fact]
    public void ABodyThatFitsUnder30KiBIsReducedToTheDefaultBudget()
    {
        // 8 days x 32 shared keys is 256 rows: inside every row and key bound,
        // and inside the API ceiling, but larger than 16 KiB once the token
        // values are twelve digits. The default budget alone has to reduce it.
        var candidate = Synthetic(days: 8, keysPerDay: 32, sharedKeys: true, tokensPerComponent: 123456789012);

        var wire = SnapshotWire.Serialize(candidate, 1, WireSchema.V2);

        Assert.True(
            wire.Bytes.Length <= 16 * 1024,
            $"the default budget has to bound the body, but it was {wire.Bytes.Length} bytes");
        Assert.True(wire.OmittedPartDays > 0, "the byte budget, not a row or key bound, has to reduce this body");
    }

    /// <summary>
    /// The breakdown budget is an operator decision: the same candidate serialized
    /// with a small budget keeps fewer breakdown days, and never loses a day total.
    /// </summary>
    [Fact]
    public void ASmallerBudgetDropsBreakdownsAndKeepsEveryDayTotal()
    {
        var candidate = Synthetic(days: 20, keysPerDay: 8, sharedKeys: true, tokensPerComponent: 123456789);

        var roomy = SnapshotWire.Serialize(candidate, 1, WireSchema.V2);
        var tight = SnapshotWire.Serialize(
            candidate,
            1,
            WireSchema.V2,
            TokscaleGraphContract.MinPartsBudgetBytes);

        Assert.True(tight.Bytes.Length <= TokscaleGraphContract.MinPartsBudgetBytes);
        Assert.True(tight.Bytes.Length < roomy.Bytes.Length);
        Assert.True(tight.OmittedPartDays > roomy.OmittedPartDays);

        using var document = JsonDocument.Parse(tight.Bytes);
        Assert.Equal(20, document.RootElement.GetProperty("days").GetArrayLength());
        foreach (var day in document.RootElement.GetProperty("days").EnumerateArray())
        {
            Assert.True(day.GetProperty("totalTokens").GetInt64() > 0);
        }
    }

    [Fact]
    public void ABudgetOutsideTheAcceptedRangeIsRefused()
    {
        var candidate = PartsCandidate();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SnapshotWire.Serialize(candidate, 1, WireSchema.V2, TokscaleGraphContract.MinPartsBudgetBytes - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SnapshotWire.Serialize(candidate, 1, WireSchema.V2, TokscaleGraphContract.MaxPartsBudgetBytes + 1));
    }

    [Fact]
    public void WireV1IsUnchangedByABreakdownCandidate()
    {
        var text = SnapshotWire.Text(SnapshotWire.Serialize(PartsCandidate(), 7));

        Assert.DoesNotContain("\"keys\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"parts\"", text, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\":1,", text, StringComparison.Ordinal);
        Assert.Contains("\"days\":[{\"date\":\"2026-11-17\",\"totalTokens\":340}", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The API rejects a snapshot whose `keys` contains an entry no day references,
    /// so every reduced body must still satisfy that.
    /// </summary>
    private static void AssertEveryKeyIsReferenced(byte[] wire)
    {
        using var document = JsonDocument.Parse(wire);
        var keys = document.RootElement.GetProperty("keys").EnumerateArray().Select(key => key[0].GetString() + "\u0000" + key[1].GetString() + "\u0000" + key[2].GetString()).ToList();
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var day in document.RootElement.GetProperty("days").EnumerateArray())
        {
            if (!day.TryGetProperty("parts", out var parts))
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                referenced.Add(keys[part[0].GetInt32()]);
            }
        }

        Assert.Equal(keys.Count, referenced.Count);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(keys.OrderBy(value => value, StringComparer.Ordinal), keys);
    }

    [Fact]
    public void MoreKeysThanTheApiAcceptsDropsTheOldestBreakdowns()
    {
        // 40 days x 4 keys is 160 distinct keys: over the 128-key bound, and each
        // day is within its own per-day bound, so the wire has to reduce.
        var candidate = Synthetic(days: 40, keysPerDay: 4);

        var wire = SnapshotWire.Serialize(candidate, 1, WireSchema.V2);
        using var document = JsonDocument.Parse(wire.Bytes);

        Assert.InRange(document.RootElement.GetProperty("keys").GetArrayLength(), 1, TokscaleGraphContract.MaxKeys);
        Assert.True(wire.OmittedPartDays > 0);
        AssertEveryKeyIsReferenced(wire.Bytes);

        // Day totals are never dropped; only breakdowns are.
        Assert.Equal(40, document.RootElement.GetProperty("days").GetArrayLength());
        var days = document.RootElement.GetProperty("days").EnumerateArray().ToList();
        Assert.False(days[0].TryGetProperty("parts", out _));
        Assert.True(days[^1].TryGetProperty("parts", out _));
    }

    [Fact]
    public void MoreRowsThanTheApiAcceptsDropsTheOldestBreakdowns()
    {
        // 40 days x 32 shared keys is 1280 rows with only 32 keys: over the
        // 1024-row bound and inside the key bound, so the row bound is what bites.
        var candidate = Synthetic(days: 40, keysPerDay: 32, sharedKeys: true);

        var wire = SnapshotWire.Serialize(candidate, 1, WireSchema.V2);
        using var document = JsonDocument.Parse(wire.Bytes);

        var rows = document.RootElement.GetProperty("days").EnumerateArray()
            .Sum(day => day.TryGetProperty("parts", out var parts) ? parts.GetArrayLength() : 0);
        Assert.InRange(rows, 1, TokscaleGraphContract.MaxPartsPerSnapshot);
        Assert.True(wire.OmittedPartDays > 0);
        Assert.Equal(40, document.RootElement.GetProperty("days").GetArrayLength());
        AssertEveryKeyIsReferenced(wire.Bytes);
    }

    [Fact]
    public void ABodyOverTheBudgetDropsBreakdownsFromTheOldestDayUntilItFits()
    {
        // 32 days x 32 shared keys is exactly the row bound, so neither the key
        // count nor the row count can be what reduces the body: twelve-digit token
        // values alone push it past 16 KiB.
        var candidate = Synthetic(days: 32, keysPerDay: 32, sharedKeys: true, tokensPerComponent: 123456789012);

        var wire = SnapshotWire.Serialize(candidate, 1, WireSchema.V2);

        Assert.InRange(wire.Bytes.Length, 1, TokscaleGraphContract.DefaultPartsBodyBytes);
        Assert.True(wire.OmittedPartDays > 0);
        AssertEveryKeyIsReferenced(wire.Bytes);
        using var document = JsonDocument.Parse(wire.Bytes);
        var keptRows = document.RootElement.GetProperty("days").EnumerateArray()
            .Sum(day => day.TryGetProperty("parts", out var parts) ? parts.GetArrayLength() : 0);
        Assert.True(
            keptRows < 32 * 32,
            "the body bound, not the row bound, has to be what reduced this payload");
    }

    [Fact]
    public void ADayWithMoreRowsThanTheApiAcceptsLosesItsBreakdown()
    {
        // A candidate built outside the normalizer can exceed one day's row bound;
        // the wire must not emit a body the API would reject as a whole.
        var parts = Enumerable.Range(0, TokscaleGraphContract.MaxPartsPerDay + 1)
            .Select(index => new SnapshotPart(
                new SnapshotPartKey("client", "", "m" + index.ToString("000", CultureInfo.InvariantCulture)),
                1,
                0,
                0,
                0,
                0))
            .ToList();
        var candidate = new SnapshotCandidate(
            TokscaleGraphContract.SchemaVersion,
            TokscaleGraphContract.PinnedAdapterVersion,
            "2026-11-19T18:49:08.875Z",
            TokscaleGraphContract.SupportedBucketTimeZone,
            "2026-11-17",
            "2026-11-17",
            [new SnapshotDay("2026-11-17", parts.Count, parts)]);

        var wire = SnapshotWire.Serialize(candidate, 1, WireSchema.V2);
        using var document = JsonDocument.Parse(wire.Bytes);

        Assert.Equal(0, document.RootElement.GetProperty("keys").GetArrayLength());
        Assert.False(document.RootElement.GetProperty("days")[0].TryGetProperty("parts", out _));
        Assert.Equal(parts.Count, document.RootElement.GetProperty("days")[0].GetProperty("totalTokens").GetInt64());
        Assert.Equal(1, wire.OmittedPartDays);
    }

    [Fact]
    public void ACandidateWithoutAnyBreakdownStillProducesAValidV2Body()
    {
        var days = Enumerable.Range(0, 3)
            .Select(offset => new SnapshotDay(new DateOnly(2026, 11, 1).AddDays(offset).ToString("yyyy-MM-dd"), 10))
            .ToArray();
        var candidate = new SnapshotCandidate(
            TokscaleGraphContract.SchemaVersion,
            TokscaleGraphContract.PinnedAdapterVersion,
            "2026-11-19T18:49:08.875Z",
            TokscaleGraphContract.SupportedBucketTimeZone,
            days[0].Date,
            days[^1].Date,
            days);

        var wire = SnapshotWire.Serialize(candidate, 1, WireSchema.V2);
        using var document = JsonDocument.Parse(wire.Bytes);

        Assert.Equal(0, document.RootElement.GetProperty("keys").GetArrayLength());
        Assert.Equal(0, wire.OmittedPartDays);
        foreach (var day in document.RootElement.GetProperty("days").EnumerateArray())
        {
            Assert.False(day.TryGetProperty("parts", out _));
        }
    }

    /// <summary>
    /// Builds a candidate whose every day has <paramref name="keysPerDay"/> distinct
    /// keys and a breakdown that adds up, so the only reason to reduce is a wire
    /// bound rather than a normalization refusal.
    /// </summary>
    private static SnapshotCandidate Synthetic(
        int days,
        int keysPerDay,
        bool sharedKeys = false,
        int modelLength = 8,
        long tokensPerComponent = 1)
    {
        var start = new DateOnly(2026, 1, 1);
        var rows = new List<SnapshotDay>();
        for (var dayIndex = 0; dayIndex < days; dayIndex++)
        {
            var date = start.AddDays(dayIndex).ToString("yyyy-MM-dd");
            var parts = new List<SnapshotPart>();
            for (var keyIndex = 0; keyIndex < keysPerDay; keyIndex++)
            {
                // Without shared keys the key is unique per day, so the distinct key
                // count grows with the day count; with shared keys the same row keys
                // repeat every day and only the row count grows.
                var name = sharedKeys
                    ? "m" + keyIndex.ToString("000")
                    : "m" + dayIndex.ToString("00") + "-" + keyIndex.ToString("00");
                var model = name.PadRight(modelLength, 'x');
                parts.Add(new SnapshotPart(
                    new SnapshotPartKey("client", "provider", model),
                    tokensPerComponent,
                    tokensPerComponent,
                    tokensPerComponent,
                    tokensPerComponent,
                    tokensPerComponent));
            }

            rows.Add(new SnapshotDay(date, parts.Sum(part => part.Total), parts));
        }

        return new SnapshotCandidate(
            TokscaleGraphContract.SchemaVersion,
            TokscaleGraphContract.PinnedAdapterVersion,
            "2026-11-19T18:49:08.875Z",
            TokscaleGraphContract.SupportedBucketTimeZone,
            rows[0].Date,
            rows[^1].Date,
            rows);
    }
}

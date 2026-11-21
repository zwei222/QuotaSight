using System.Text;

namespace QuotaSight.Collector.Tests;

internal static class FixtureFiles
{
    internal const string Positive = "graph-4.23.0-positive.json";
    internal const string Parts = "graph-4.23.0-parts.json";
    internal const string Empty = "graph-4.23.0-empty.json";
    internal const string Malformed = "graph-4.23.0-malformed.json";
    internal const string Partial = "graph-4.23.0-partial.json";
    internal const string Unsorted = "graph-4.23.0-unsorted.json";
    internal const string ZeroObserved = "graph-4.23.0-zero-observed.json";
    internal const string VersionUnknown = "graph-4.23.0-version-unknown.json";
    internal const string DuplicateDate = "graph-4.23.0-duplicate-date.json";
    internal const string DuplicateKey = "graph-4.23.0-duplicate-key.json";
    internal const string FractionalTokens = "graph-4.23.0-fractional-tokens.json";
    internal const string OverflowTokens = "graph-4.23.0-overflow-tokens.json";
    internal const string NegativeTokens = "graph-4.23.0-negative-tokens.json";
    internal const string MissingTokens = "graph-4.23.0-missing-tokens.json";
    internal const string TokenString = "graph-4.23.0-token-string.json";
    internal const string BadDate = "graph-4.23.0-bad-date.json";
    internal const string OffsetInstant = "graph-4.23.0-offset-instant.json";
    internal const string UnknownRootKey = "graph-4.23.0-unknown-root-key.json";
    internal const string UnknownMetaKey = "graph-4.23.0-unknown-meta-key.json";
    internal const string TrailingGarbage = "graph-4.23.0-trailing-garbage.json";

    /// <summary>
    /// The wire-v2 body both lanes test against: <c>src/QuotaSight.Usage.Api/test/fixtures</c>.
    ///
    /// It lives on the API side because the API test is the one that stores it, and
    /// this side asserts that its serializer produces exactly these bytes. Locating
    /// the repository by walking up to the solution file keeps the test independent
    /// of the working directory and of build output layout.
    /// </summary>
    internal static byte[] SharedWireV2Golden() =>
        File.ReadAllBytes(RepositoryFile("src", "QuotaSight.Usage.Api", "test", "fixtures", "wire-v2-golden.json"));

    internal static string RepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QuotaSight.slnx")))
            {
                return Path.Combine([directory.FullName, .. parts]);
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The repository root could not be located from the test output directory.");
    }

    internal static byte[] Bytes(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        return File.ReadAllBytes(path);
    }

    internal static string Text(string name) => Encoding.UTF8.GetString(Bytes(name));

    /// <summary>Builds a pinned-shape graph document with the supplied dated rows.</summary>
    internal static string Graph(IEnumerable<(string Date, long Tokens)> rows)
    {
        var builder = new StringBuilder();
        builder.Append("{\n  \"meta\": {\n    \"generatedAt\": \"2026-11-19T18:49:08.875485588+00:00\",\n");
        builder.Append("    \"version\": \"4.23.0\",\n    \"dateRange\": { \"start\": \"\", \"end\": \"\" }\n  },\n");
        builder.Append("  \"summary\": { \"totalTokens\": 0, \"totalCost\": 0.0, \"costProvenance\": { \"kind\": \"unknown\" } },\n");
        builder.Append("  \"years\": [],\n  \"contributions\": [");
        var first = true;
        foreach (var (date, tokens) in rows)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append("\n    { \"date\": \"").Append(date).Append("\", \"totals\": { \"tokens\": ")
                .Append(tokens.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(", \"cost\": 0.0, \"messages\": 1, \"costProvenance\": { \"kind\": \"unknown\" } }, \"intensity\": 1, \"clients\": [] }");
        }

        builder.Append("\n  ],\n  \"timeMetrics\": { \"sessionCount\": 1 }\n}\n");
        return builder.ToString();
    }

    internal static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}

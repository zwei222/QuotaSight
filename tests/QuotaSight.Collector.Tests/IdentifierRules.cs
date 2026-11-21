using System.Text.Json;

namespace QuotaSight.Collector.Tests;

/// <summary>
/// The identifier boundaries the Collector and the Worker share.
///
/// The file is <c>src/QuotaSight.Usage.Api/test/fixtures/identifier-rules.json</c>,
/// and both sides read it: the Worker's own test asserts that an element of the
/// lengths it lists is accepted or rejected, and the Collector's tests assert the
/// same list against its own normalization. One file, two readers, so the two
/// lanes cannot drift to different bounds.
/// </summary>
internal sealed record IdentifierRules(
    int[] ClientAcceptedLengths,
    int[] ClientRejectedLengths,
    int[] ProviderElementAcceptedLengths,
    int[] ProviderElementRejectedLengths,
    string[] ModelFileSuffixes,
    string AcceptedModelForm)
{
    internal static IdentifierRules Load()
    {
        var path = FixtureFiles.RepositoryFile("src", "QuotaSight.Usage.Api", "test", "fixtures", "identifier-rules.json");

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var provider = root.GetProperty("providerElementLengths");
        var client = root.GetProperty("clientLengths");

        return new IdentifierRules(
            ReadInts(client.GetProperty("accepted")),
            ReadInts(client.GetProperty("rejected")),
            ReadInts(provider.GetProperty("accepted")),
            ReadInts(provider.GetProperty("rejected")),
            [.. root.GetProperty("modelFileSuffixes").EnumerateArray().Select(value => value.GetString()!)],
            root.GetProperty("acceptedModelForm").GetString()!);
    }

    private static int[] ReadInts(JsonElement values) =>
        [.. values.EnumerateArray().Select(value => value.GetInt32())];
}

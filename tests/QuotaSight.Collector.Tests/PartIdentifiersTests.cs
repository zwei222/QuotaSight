namespace QuotaSight.Collector.Tests;

/// <summary>
/// The identifier rules, as the API's own definition requires them.
///
/// Each case states what a value means rather than only how it is spelled: casing
/// is folded for all three identifiers, a value that is not an identifier becomes
/// the empty (unknown) identifier, and nothing is ever repaired or truncated.
/// </summary>
public sealed class PartIdentifiersTests
{
    [Theory]
    [InlineData("claude", "claude")]
    [InlineData("a", "a")]
    [InlineData("codex-cli", "codex-cli")]
    [InlineData("a.b_c-d1", "a.b_c-d1")]
    [InlineData("Claude", "claude")]
    [InlineData("CLAUDE", "claude")]
    [InlineData("Codex-CLI", "codex-cli")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("clau de", "")]
    [InlineData("-claude", "")]
    [InlineData(".claude", "")]
    [InlineData("claude/cli", "")]
    [InlineData("claude!", "")]
    [InlineData("claud\u00e9", "")]
    [InlineData("\u0130stanbul", "")]
    public void ClientIsFoldedToAsciiLowerCaseAndAcceptedOnlyAsASlug(string? raw, string expected) =>
        Assert.Equal(expected, PartIdentifiers.NormalizeClient(raw));

    [Fact]
    public void ClientLongerThanTheWireBoundIsUnknown() =>
        Assert.Equal(string.Empty, PartIdentifiers.NormalizeClient(new string('a', 33)));

    /// <summary>
    /// The client length boundary, read from the same shared file the Worker's test
    /// reads, so the two sides cannot disagree about it.
    /// </summary>
    [Fact]
    public void ClientLengthBoundaryMatchesTheSharedRule()
    {
        var rules = IdentifierRules.Load();
        foreach (var length in rules.ClientAcceptedLengths)
        {
            Assert.Equal(new string('a', length), PartIdentifiers.NormalizeClient(new string('a', length)));
        }

        foreach (var length in rules.ClientRejectedLengths)
        {
            Assert.Equal(string.Empty, PartIdentifiers.NormalizeClient(new string('a', length)));
        }
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("openai", "openai")]
    [InlineData("OpenAI", "openai")]
    [InlineData("OpenAI-Azure", "openai-azure")]
    [InlineData("Anthropic", "anthropic")]
    [InlineData(" openai ", "openai")]
    [InlineData("openai,anthropic", "anthropic,openai")]
    [InlineData("Anthropic, openai", "anthropic,openai")]
    [InlineData("openai,openai", "openai")]
    [InlineData("openai, ,anthropic", "anthropic,openai")]
    [InlineData("openai,anthropic,google,azure", "anthropic,azure,google,openai")]
    [InlineData("openai,anthropic,google,azure,meta", "")]
    [InlineData(",,,", "")]
    public void ProviderIsSplitTrimmedLowercasedDeduplicatedAndSorted(string? raw, string expected) =>
        Assert.Equal(expected, PartIdentifiers.NormalizeProvider(raw));

    [Fact]
    public void ProviderLongerThanTheWireBoundIsUnknown() =>
        Assert.Equal(
            string.Empty,
            PartIdentifiers.NormalizeProvider(new string('a', 60) + "," + new string('b', 5)));

    [Theory]
    [InlineData("gpt-5.1", "gpt-5.1")]
    [InlineData("GPT-5.1", "gpt-5.1")]
    [InlineData("claude-sonnet-5-5[1m]", "claude-sonnet-5-5[1m]")]
    [InlineData("Claude-Sonnet-5-5[1M]", "claude-sonnet-5-5[1m]")]
    [InlineData("org/model", "org/model")]
    [InlineData("meta-llama/Llama-3-8B", "meta-llama/llama-3-8b")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ModelIsLowercasedAndKeptWhenItIsAnIdentifier(string? raw, string expected) =>
        Assert.Equal(expected, PartIdentifiers.NormalizeModel(raw));

    [Theory]
    [InlineData("/models/llama-3-8b.gguf")]
    [InlineData("/models/llama")]
    [InlineData("C:\\models\\llama-3.gguf")]
    [InlineData("c:/models/llama-3.gguf")]
    [InlineData("D:models")]
    [InlineData("llama-3-8b.gguf")]
    [InlineData("models/llama-3-8b.gguf")]
    [InlineData("org/sub/model")]
    [InlineData("~/models/llama")]
    [InlineData("\\\\server\\share\\model")]
    public void PathShapedModelIsWithheld(string raw) =>
        Assert.Equal(string.Empty, PartIdentifiers.NormalizeModel(raw));

    /// <summary>
    /// The provider element boundary, from the shared rule file: an element may be
    /// up to 64 characters, and the Worker's own test asserts the same numbers.
    /// </summary>
    [Fact]
    public void ProviderElementBoundaryMatchesTheSharedRule()
    {
        var rules = IdentifierRules.Load();
        foreach (var length in rules.ProviderElementAcceptedLengths)
        {
            Assert.Equal(
                new string('p', length),
                PartIdentifiers.NormalizeProvider(new string('p', length)));
        }

        foreach (var length in rules.ProviderElementRejectedLengths)
        {
            Assert.Equal(string.Empty, PartIdentifiers.NormalizeProvider(new string('p', length)));
        }
    }

    /// <summary>
    /// The local weight-file suffixes, from the same shared rule file the Worker's
    /// test reads: a value ending in one is a file, not a model id.
    /// </summary>
    [Fact]
    public void ModelFileSuffixesArePathsOnBothSides()
    {
        var rules = IdentifierRules.Load();
        foreach (var suffix in rules.ModelFileSuffixes)
        {
            Assert.True(
                PartIdentifiers.IsPathShapedModel("model" + suffix),
                $"model{suffix} must be treated as a file");
            Assert.Equal(string.Empty, PartIdentifiers.NormalizeModel("model" + suffix));
        }

        Assert.Equal(
            rules.AcceptedModelForm,
            PartIdentifiers.NormalizeModel(rules.AcceptedModelForm));
    }

    [Fact]
    public void ModelLongerThanTheWireBoundIsUnknown() =>
        Assert.Equal(string.Empty, PartIdentifiers.NormalizeModel(new string('a', 97)));

    [Theory]
    [InlineData("model")]
    [InlineData("org/model")]
    [InlineData("claude-sonnet-5-5[1m]")]
    public void SingleSlashAndBracketsAreNotPaths(string value) =>
        Assert.False(PartIdentifiers.IsPathShapedModel(value));
}

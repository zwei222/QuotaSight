namespace QuotaSight.UI;

/// <summary>One provider the usage breakdown can attribute tokens to, with its product name.</summary>
public sealed record TokScaleProviderInfo(string Id, string DisplayName);

/// <summary>
/// The provider catalog this build offers, pinned to TokScale 4.23.0. The ids are the
/// lowercase attribution ids the usage breakdown carries in its <c>provider</c>
/// component (single ids only: a row served by several providers is a combination and
/// never a member of this list). The display names are the product names; the ids come
/// from the parsers of that release, e.g. <c>openai</c>, <c>anthropic</c>,
/// <c>xai_oauth</c> and the subscription-SDK id observed on real breakdowns, and from
/// the release's provider identity table (<c>xai</c>, <c>zai</c>, <c>moonshotai</c>,
/// <c>meta_llama</c>, …). The count and uniqueness are pinned by tests, so a TokScale
/// release that adds or removes providers is a deliberate catalog update.
///
/// The catalog is deliberately published in this repository's source: these are model
/// provider ids, not the ids of any agent or client this repository keeps out of it.
/// </summary>
public static class TokScaleProviderCatalog
{
    /// <summary>The TokScale release this catalog mirrors.</summary>
    public const string TokScaleVersion = "4.23.0";

    public static IReadOnlyList<TokScaleProviderInfo> Providers { get; } =
    [
        new("ai21", "AI21"),
        new("anthropic", "Anthropic"),
        new("azure_ai", "Azure AI"),
        new("claude_subscription_directsdk_experimental", "Claude (subscription SDK)"),
        new("copilot", "GitHub Copilot"),
        new("deepseek", "DeepSeek"),
        new("fireworks_ai", "Fireworks AI"),
        new("google", "Google"),
        new("meta_llama", "Meta Llama"),
        new("minimax", "MiniMax"),
        new("mistral", "Mistral AI"),
        new("moonshotai", "Moonshot AI"),
        new("nous", "Nous"),
        new("novita", "Novita"),
        new("openai", "OpenAI"),
        new("opencode_go", "OpenCode Go"),
        new("openrouter", "OpenRouter"),
        new("qwen", "Qwen"),
        new("stepfun", "StepFun"),
        new("together_ai", "Together AI"),
        new("vertex", "Vertex AI"),
        new("xai", "xAI"),
        new("xai_oauth", "xAI (OAuth)"),
        new("zai", "Z.AI"),
    ];

    public static TokScaleProviderInfo? Find(string id) =>
        Providers.FirstOrDefault(provider => provider.Id == id);

    /// <summary>The product name for a provider id, or the raw id when it is unknown.</summary>
    public static string DisplayNameOf(string id) => Find(id)?.DisplayName ?? id;

    /// <summary>
    /// The display name for a breakdown provider value. A single id shows its product
    /// name (or the raw id when unknown); a comma-joined combination of ids — the
    /// collector's normalized "several providers served this row" form — shows the
    /// providers' names joined with " + ", one card for the whole combination. An empty
    /// value is the localized unknown marker.
    /// </summary>
    public static string CardDisplayName(string providerKey, UiCopy copy)
    {
        if (providerKey.Length == 0) return copy.UsageBreakdownUnknown;
        if (!providerKey.Contains(',')) return DisplayNameOf(providerKey);
        return string.Join(" + ", providerKey.Split(',').Select(DisplayNameOf));
    }
}

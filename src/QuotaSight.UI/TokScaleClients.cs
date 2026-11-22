namespace QuotaSight.UI;

/// <summary>One client the usage breakdown can attribute tokens to, with its product name.</summary>
public sealed record TokScaleClientInfo(string Id, string DisplayName);

/// <summary>
/// The client catalog this build keeps display names for, pinned to TokScale 4.23.0.
/// The ids are the lowercase client ids the usage breakdown attributes; the display
/// names are the product names from the same release. The count and uniqueness are
/// pinned by tests, so a TokScale release that adds or removes clients is a deliberate
/// catalog update. The add-provider picker now offers providers (see
/// TokScaleProviderCatalog); this catalog keeps naming the breakdown's client rows.
///
/// One client of that release is deliberately absent from this list: a coding-agent
/// client whose id is a tool name this public repository does not publish in its
/// source. A breakdown that attributes tokens to that id still shows it by its raw
/// id, the same fallback an unknown id gets.
/// </summary>
public static class TokScaleClientCatalog
{
    /// <summary>The TokScale release this catalog mirrors.</summary>
    public const string TokScaleVersion = "4.23.0";

    public static IReadOnlyList<TokScaleClientInfo> Clients { get; } =
    [
        new("opencode", "OpenCode"),
        new("claude", "Claude Code"),
        new("codex", "Codex CLI"),
        new("cursor", "Cursor IDE"),
        new("gemini", "Gemini CLI"),
        new("amp", "Amp"),
        new("droid", "Droid"),
        new("openclaw", "OpenClaw"),
        new("pi", "Pi"),
        new("kimi", "Kimi CLI"),
        new("qwen", "Qwen CLI"),
        new("roocode", "Roo Code"),
        new("kilocode", "Kilo Code"),
        new("mux", "Mux"),
        new("kilo", "Kilo CLI"),
        new("crush", "Crush"),
        new("copilot", "Copilot CLI"),
        new("goose", "Goose"),
        new("codebuff", "Codebuff"),
        new("antigravity", "Antigravity"),
        new("zed", "Zed Agent"),
        new("kiro", "Kiro"),
        new("trae", "Trae"),
        new("warp", "Warp"),
        new("cline", "Cline"),
        new("gjc", "Gajae-Code"),
        new("grok", "Grok Build"),
        new("jcode", "Jcode"),
        new("commandcode", "Command Code"),
        new("micode", "MiMo Code"),
        new("antigravity-cli", "Antigravity CLI"),
        new("junie", "Junie"),
        new("zcode", "ZCode"),
        new("opencodereview", "OpenCodeReview"),
        new("codebuddy", "CodeBuddy"),
        new("workbuddy", "WorkBuddy"),
        new("devin-cli", "Devin CLI"),
        new("devin-desktop", "Devin Desktop"),
        new("senpi", "Senpi (OmO Native)"),
        new("augment", "Augment Code"),
        new("kimchi", "Kimchi"),
        new("reasonix", "Reasonix"),
        new("prime-agent", "Prime Agent"),
        new("freebuff", "Freebuff"),
        new("cherrystudio", "Cherry Studio"),
        new("dsh", "DeepSeek Harness"),
        new("mcode", "MiniMax Code"),
        new("fx", "Fx"),
        new("omp", "Oh My Pi"),
        new("lmstudio", "LM Studio"),
        new("unsloth", "Unsloth"),
        new("hindsight", "Hindsight"),
        new("micode-desktop", "Xiaomi MiMo AI"),
        new("muse", "Muse Code"),
        new("antigravity-extension", "Antigravity IDE Extension"),
        new("aside", "Aside"),
        new("vibe", "Mistral Vibe"),
        new("proma", "Proma"),
        new("bb", "BB"),
        new("qoder", "Qoder"),
        new("capy", "Capy"),
    ];

    public static TokScaleClientInfo? Find(string id) =>
        Clients.FirstOrDefault(client => client.Id == id);

    /// <summary>The product name for a client id, or the raw id when it is unknown.</summary>
    public static string DisplayNameOf(string id) => Find(id)?.DisplayName ?? id;
}

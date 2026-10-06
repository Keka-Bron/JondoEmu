namespace Jondo.Unity.Reversing;

/// <summary>
/// The places where one can have a model, with what is needed to reach them.
///
/// It is a list of shortcuts, not a limitation: «Otro» accepts any server that speaks like
/// OpenAI, which by now is almost any. It is here and not in the interface because it is
/// knowledge of the world —which address each has and which dialect it speaks—, not a decision about
/// how to paint a window.
///
/// ─── Why almost none brings a suggested model ───────────────────────────────────────────
///
/// Because identifiers expire and a hand-written list ages badly: one is left with a
/// name that no longer exists and the error the provider returns does not say that is it. Instead
/// the provider itself is asked with <see cref="Llm.CatalogueAsync"/>, which also checks in
/// passing that the address and the key are valid. Only what is known to be true today carries a suggestion.
/// </summary>
public sealed record Provider(
    string Name,
    Llm.Dialect Dialect,
    string Url,
    bool NeedsKey,
    string Suggested = "",
    string Hint = "")
{
    /// <summary>Whether the model runs on one's own machine and therefore costs no money.</summary>
    public bool Local => !NeedsKey;

    public static IReadOnlyList<Provider> All { get; } = new[]
    {
        new Provider("Claude", Llm.Dialect.Anthropic, "https://api.anthropic.com", true,
                     "claude-sonnet-5", "la clave se saca en console.anthropic.com"),

        new Provider("ChatGPT", Llm.Dialect.OpenAi, "https://api.openai.com", true,
                     Hint: "la clave se saca en platform.openai.com"),

        new Provider("Gemini", Llm.Dialect.Gemini, "https://generativelanguage.googleapis.com", true,
                     Hint: "la clave se saca en aistudio.google.com"),

        new Provider("DeepSeek", Llm.Dialect.OpenAi, "https://api.deepseek.com", true,
                     Hint: "es el que usa Snowbot, y el más barato de los de pago"),

        new Provider("Ollama", Llm.Dialect.OpenAi, "http://localhost:11434/v1", false,
                     Hint: "en tu máquina: arráncalo con «ollama serve» y no cuesta nada"),

        new Provider("LM Studio", Llm.Dialect.OpenAi, "http://localhost:1234/v1", false,
                     Hint: "en tu máquina: enciende el servidor local desde su pestaña de servidor"),

        new Provider("Otro", Llm.Dialect.OpenAi, "", false,
                     Hint: "cualquier servidor que hable como OpenAI: vLLM, llama.cpp, un túnel..."),
    };

    /// <summary>The one that best matches what was already configured, so as not to lose the choice.</summary>
    public static Provider Match(string url, Llm.Dialect dialect)
        => All.FirstOrDefault(p => p.Url.Length > 0 &&
                                   url.StartsWith(p.Url, StringComparison.OrdinalIgnoreCase))
           ?? All.FirstOrDefault(p => p.Dialect == dialect && p.Url.Length == 0)
           ?? All[^1];
}

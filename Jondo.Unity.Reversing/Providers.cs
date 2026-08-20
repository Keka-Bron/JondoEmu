namespace Jondo.Unity.Reversing;

/// <summary>
/// Los sitios donde se puede tener un modelo, con lo que hace falta para llegar a ellos.
///
/// Es una lista de atajos, no una limitación: «Otro» acepta cualquier servidor que hable como
/// OpenAI, que a estas alturas es casi cualquiera. Está aquí y no en la interfaz porque es
/// conocimiento del mundo —qué dirección tiene cada uno y qué dialecto habla—, no una decisión de
/// cómo pintar una ventana.
///
/// ─── Por qué casi ninguno trae modelo sugerido ──────────────────────────────────────────
///
/// Porque los identificadores caducan y una lista escrita a mano envejece mal: se queda uno con un
/// nombre que ya no existe y el error que devuelve el proveedor no dice que sea eso. En su lugar se
/// le pregunta al propio proveedor con <see cref="Llm.CatalogueAsync"/>, que además comprueba de
/// paso que la dirección y la clave valen. Sólo lleva sugerencia lo que se sabe cierto hoy.
/// </summary>
public sealed record Provider(
    string Name,
    Llm.Dialect Dialect,
    string Url,
    bool NeedsKey,
    string Suggested = "",
    string Hint = "")
{
    /// <summary>Si el modelo corre en la máquina de uno y por tanto no cuesta dinero.</summary>
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

    /// <summary>El que mejor case con lo que ya estaba configurado, para no perder la elección.</summary>
    public static Provider Match(string url, Llm.Dialect dialect)
        => All.FirstOrDefault(p => p.Url.Length > 0 &&
                                   url.StartsWith(p.Url, StringComparison.OrdinalIgnoreCase))
           ?? All.FirstOrDefault(p => p.Dialect == dialect && p.Url.Length == 0)
           ?? All[^1];
}

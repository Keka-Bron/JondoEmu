using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jondo.Unity.Reversing;

/// <summary>
/// Cómo se le habla a un proveedor por HTTP.
///
/// Los tres grandes piden lo mismo con formas distintas, y ninguno se parece lo bastante a otro
/// como para poder tratarlos con un <c>if</c>:
///
///   Anthropic   POST /v1/messages          las instrucciones en su propio hueco, clave en x-api-key
///   OpenAI      POST /v1/chat/completions  las instrucciones como un mensaje con papel «system»,
///                                          clave como Bearer. Es el que hablan también DeepSeek,
///                                          Ollama, LM Studio, vLLM y casi todo lo que se pueda
///                                          levantar en casa.
///   Gemini      POST /v1beta/models/{modelo}:generateContent   el modelo va en la RUTA, no en el
///                                          cuerpo; clave en x-goog-api-key
///
/// Cada uno en su clase y el resto del código sin enterarse. Añadir un cuarto es escribir una clase
/// de treinta líneas, no tocar el que ya funciona.
/// </summary>
public abstract class Wire
{
    public static Wire For(Llm.Dialect dialect) => dialect switch
    {
        Llm.Dialect.Anthropic => new AnthropicWire(),
        Llm.Dialect.Gemini => new GeminiWire(),
        _ => new OpenAiWire(),
    };

    /// <summary>La dirección completa a la que se pregunta.</summary>
    public abstract string Address(string baseUrl, string model);

    /// <summary>El cuerpo de la pregunta.</summary>
    public abstract object Body(string model, string system, string prompt, bool strictJson);

    /// <summary>Cómo se acredita uno. Sin clave no se pone nada: un servidor de casa no la pide.</summary>
    public abstract void Authorize(HttpRequestMessage request, string key);

    /// <summary>El texto que ha contestado, sacado de donde lo ponga cada uno.</summary>
    public abstract Task<string> ReadAsync(HttpContent content, CancellationToken cancel);

    /// <summary>
    /// Si se le puede pedir el JSON por contrato.
    ///
    /// Anthropic no tiene esa palanca —se le pide en las instrucciones y ya— pero los otros dos sí,
    /// y con ella el modelo no puede contestar con vallas de código ni con un párrafo de cortesía
    /// delante. Es lo único que merece la pena copiarle al cliente de Snowbot.
    /// </summary>
    public virtual bool SupportsStrictJson => true;

    /// <summary>
    /// Dónde preguntar qué modelos hay.
    ///
    /// Los tres tienen una lista y los tres la dan por GET, así que no hay por qué hacer escribir a
    /// mano un identificador como <c>claude-sonnet-5</c> o <c>qwen2.5-coder:14b</c> y descubrir la
    /// errata cuando falle la primera pregunta. Con un servidor de casa es todavía más útil: nadie
    /// se acuerda de cómo se llamaba exactamente lo que se bajó.
    /// </summary>
    public abstract string Catalogue(string baseUrl);

    /// <summary>Los nombres de modelo que devuelve esa lista.</summary>
    public abstract Task<List<string>> ReadCatalogueAsync(HttpContent content, CancellationToken cancel);

    protected static string Trim(string url) => url.TrimEnd('/');

    /// <summary>La raíz con su /v1 puesto una sola vez, la traiga ya el usuario o no.</summary>
    protected static string WithVersion(string baseUrl, string version)
    {
        string root = Trim(baseUrl);
        return root.EndsWith("/" + version, StringComparison.Ordinal) ? root : root + "/" + version;
    }

    /// <summary>La forma en que OpenAI y Anthropic dan su catálogo: {"data":[{"id":...}]}.</summary>
    protected static async Task<List<string>> ReadDataIdsAsync(HttpContent content, CancellationToken cancel)
    {
        var list = await content.ReadFromJsonAsync<Catalogued>(cancel);
        return list?.Data?.Select(m => m.Id).Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).ToList()
               ?? new List<string>();
    }

    private sealed record Catalogued([property: JsonPropertyName("data")] List<Entry>? Data);
    private sealed record Entry([property: JsonPropertyName("id")] string? Id);

    // ─── Anthropic ──────────────────────────────────────────────────────────────────────

    private sealed class AnthropicWire : Wire
    {
        public override bool SupportsStrictJson => false;

        public override string Address(string baseUrl, string model) => Trim(baseUrl) + "/v1/messages";

        public override string Catalogue(string baseUrl) => WithVersion(baseUrl, "v1") + "/models";

        public override Task<List<string>> ReadCatalogueAsync(HttpContent content, CancellationToken cancel)
            => ReadDataIdsAsync(content, cancel);

        public override object Body(string model, string system, string prompt, bool strictJson) => new
        {
            model,
            max_tokens = 2048,
            system,
            messages = new[] { new { role = "user", content = prompt } },
        };

        public override void Authorize(HttpRequestMessage request, string key)
        {
            if (key.Length == 0) return;
            request.Headers.Add("x-api-key", key);
            request.Headers.Add("anthropic-version", "2023-06-01");
        }

        public override async Task<string> ReadAsync(HttpContent content, CancellationToken cancel)
        {
            var answer = await content.ReadFromJsonAsync<Answer>(cancel);
            return string.Concat(answer?.Content?.Where(b => b.Type == "text").Select(b => b.Text) ?? []);
        }

        private sealed record Answer([property: JsonPropertyName("content")] List<Block>? Content);
        private sealed record Block([property: JsonPropertyName("type")] string? Type,
                                    [property: JsonPropertyName("text")] string? Text);
    }

    // ─── OpenAI y todo lo que le copia ──────────────────────────────────────────────────

    private sealed class OpenAiWire : Wire
    {
        public override string Address(string baseUrl, string model)
        {
            // Muchos proveedores dan la dirección con el /v1 puesto y otros sin él. Poner uno de más
            // da un 404 que no dice por qué, así que se mira antes de añadirlo.
            string root = Trim(baseUrl);
            return root.EndsWith("/v1", StringComparison.Ordinal)
                ? root + "/chat/completions"
                : root + "/v1/chat/completions";
        }

        public override string Catalogue(string baseUrl) => WithVersion(baseUrl, "v1") + "/models";

        public override Task<List<string>> ReadCatalogueAsync(HttpContent content, CancellationToken cancel)
            => ReadDataIdsAsync(content, cancel);

        public override object Body(string model, string system, string prompt, bool strictJson) => new
        {
            model,
            max_tokens = 2048,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = prompt },
            },
            response_format = strictJson ? new { type = "json_object" } : null,
        };

        public override void Authorize(HttpRequestMessage request, string key)
        {
            // Mandar «Bearer » a secas hace que algunos se quejen de una credencial mal formada en
            // vez de atender, así que sin clave no se manda cabecera ninguna.
            if (key.Length == 0) return;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        public override async Task<string> ReadAsync(HttpContent content, CancellationToken cancel)
        {
            var answer = await content.ReadFromJsonAsync<Answer>(cancel);
            return answer?.Choices?.FirstOrDefault()?.Message?.Content ?? "";
        }

        private sealed record Answer([property: JsonPropertyName("choices")] List<Choice>? Choices);
        private sealed record Choice([property: JsonPropertyName("message")] Message? Message);
        private sealed record Message([property: JsonPropertyName("content")] string? Content);
    }

    // ─── Gemini ─────────────────────────────────────────────────────────────────────────

    private sealed class GeminiWire : Wire
    {
        public override string Address(string baseUrl, string model)
        {
            string root = Trim(baseUrl);
            if (!root.Contains("/v1", StringComparison.Ordinal)) root += "/v1beta";
            return $"{root}/models/{model}:generateContent";
        }

        public override string Catalogue(string baseUrl)
        {
            string root = Trim(baseUrl);
            if (!root.Contains("/v1", StringComparison.Ordinal)) root += "/v1beta";
            return root + "/models";
        }

        /// <summary>
        /// Google los da con el prefijo puesto: «models/gemini-…». Se le quita, porque el nombre que
        /// hay que volver a mandarle en la ruta es el de después de la barra.
        /// </summary>
        public override async Task<List<string>> ReadCatalogueAsync(HttpContent content, CancellationToken cancel)
        {
            var list = await content.ReadFromJsonAsync<Catalogue2>(cancel);
            return list?.Models?
                .Select(m => m.Name ?? "")
                .Where(n => n.Length > 0)
                .Select(n => n.StartsWith("models/", StringComparison.Ordinal) ? n["models/".Length..] : n)
                .ToList() ?? new List<string>();
        }

        private sealed record Catalogue2([property: JsonPropertyName("models")] List<Named>? Models);
        private sealed record Named([property: JsonPropertyName("name")] string? Name);

        public override object Body(string model, string system, string prompt, bool strictJson) => new
        {
            system_instruction = new { parts = new[] { new { text = system } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
            generationConfig = new
            {
                maxOutputTokens = 2048,
                responseMimeType = strictJson ? "application/json" : "text/plain",
            },
        };

        public override void Authorize(HttpRequestMessage request, string key)
        {
            // En la cabecera y no en la URL a propósito: una clave en la barra de direcciones
            // acaba en los registros del servidor y en el historial de quien depure con curl.
            if (key.Length > 0) request.Headers.Add("x-goog-api-key", key);
        }

        public override async Task<string> ReadAsync(HttpContent content, CancellationToken cancel)
        {
            var answer = await content.ReadFromJsonAsync<Answer>(cancel);
            var parts = answer?.Candidates?.FirstOrDefault()?.Content?.Parts;
            return string.Concat(parts?.Select(p => p.Text) ?? []);
        }

        private sealed record Answer([property: JsonPropertyName("candidates")] List<Candidate>? Candidates);
        private sealed record Candidate([property: JsonPropertyName("content")] Content? Content);
        private sealed record Content([property: JsonPropertyName("parts")] List<Part>? Parts);
        private sealed record Part([property: JsonPropertyName("text")] string? Text);
    }
}

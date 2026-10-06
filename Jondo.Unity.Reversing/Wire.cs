using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jondo.Unity.Reversing;

/// <summary>
/// How to talk to a provider over HTTP.
///
/// The three big ones ask for the same thing with different shapes, and none resembles another enough
/// to be able to treat them with an <c>if</c>:
///
///   Anthropic   POST /v1/messages          the instructions in their own slot, key in x-api-key
///   OpenAI      POST /v1/chat/completions  the instructions as a message with role «system»,
///                                          key as Bearer. It is the one DeepSeek,
///                                          Ollama, LM Studio, vLLM and almost anything that can be
///                                          run at home also speak.
///   Gemini      POST /v1beta/models/{model}:generateContent   the model goes in the PATH, not in the
///                                          body; key in x-goog-api-key
///
/// Each in its class and the rest of the code none the wiser. Adding a fourth is writing a
/// thirty-line class, not touching the one that already works.
/// </summary>
public abstract class Wire
{
    public static Wire For(Llm.Dialect dialect) => dialect switch
    {
        Llm.Dialect.Anthropic => new AnthropicWire(),
        Llm.Dialect.Gemini => new GeminiWire(),
        _ => new OpenAiWire(),
    };

    /// <summary>The full address asked.</summary>
    public abstract string Address(string baseUrl, string model);

    /// <summary>The question's body.</summary>
    public abstract object Body(string model, string system, string prompt, bool strictJson);

    /// <summary>How one authenticates. Without a key nothing is set: a home server does not ask for one.</summary>
    public abstract void Authorize(HttpRequestMessage request, string key);

    /// <summary>The text it answered, taken from wherever each one puts it.</summary>
    public abstract Task<string> ReadAsync(HttpContent content, CancellationToken cancel);

    /// <summary>
    /// Whether JSON can be asked for by contract.
    ///
    /// Anthropic does not have that lever —it is asked for in the instructions and that is it— but the other two do,
    /// and with it the model cannot answer with code fences nor with a courtesy paragraph
    /// in front. It is the only thing worth copying from Snowbot's client.
    /// </summary>
    public virtual bool SupportsStrictJson => true;

    /// <summary>
    /// Where to ask which models there are.
    ///
    /// All three have a list and all three give it by GET, so there is no reason to make anyone type
    /// an identifier like <c>claude-sonnet-5</c> or <c>qwen2.5-coder:14b</c> by hand and discover the
    /// typo when the first question fails. With a home server it is even more useful: nobody
    /// remembers exactly what the thing they downloaded was called.
    /// </summary>
    public abstract string Catalogue(string baseUrl);

    /// <summary>The model names that list returns.</summary>
    public abstract Task<List<string>> ReadCatalogueAsync(HttpContent content, CancellationToken cancel);

    protected static string Trim(string url) => url.TrimEnd('/');

    /// <summary>The root with its /v1 put only once, whether the user brings it or not.</summary>
    protected static string WithVersion(string baseUrl, string version)
    {
        string root = Trim(baseUrl);
        return root.EndsWith("/" + version, StringComparison.Ordinal) ? root : root + "/" + version;
    }

    /// <summary>The way OpenAI and Anthropic give their catalogue: {"data":[{"id":...}]}.</summary>
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

    // ─── OpenAI and everything that copies it ───────────────────────────────────────────

    private sealed class OpenAiWire : Wire
    {
        public override string Address(string baseUrl, string model)
        {
            // Many providers give the address with the /v1 on and others without it. Adding one too many
            // gives a 404 that does not say why, so it is checked before adding it.
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
            // Sending a bare «Bearer » makes some complain about a malformed credential instead
            // of serving, so without a key no header at all is sent.
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
        /// Google gives them with the prefix on: «models/gemini-…». It is removed, because the name that
        /// has to be sent back to it in the path is the one after the slash.
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
            // In the header and not in the URL on purpose: a key in the address bar
            // ends up in the server's logs and in the history of whoever debugs with curl.
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

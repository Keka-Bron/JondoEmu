using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jondo.Unity.Reversing;

/// <summary>
/// The model, on a leash.
///
/// Stage 4 is thousands of calls to a language model, and that brings three problems that are not
/// of intelligence but of plumbing: it costs money, it fails now and then, and if what it answers is not kept
/// one has to pay for it again. So:
///
///   cache      each question is stored on disk by its hash. Repeating a whole sweep after
///              touching the output format costs nothing.
///   retries    with growing waits, and respecting <c>retry-after</c> when it is sent. A 429
///              halfway through a sweep of two thousand messages cannot bring the sweep down.
///   limit      on how many go at once, because the provider has one and I prefer to get there first.
///
/// The provider is not set in stone, and there are two reasons for that. One is money: a whole
/// sweep is two thousand long questions and it is worth being able to choose whom to pay for them. The other is that
/// a model running on the machine itself —Ollama, LM Studio, llama.cpp— costs nothing and for
/// clearing the thousand messages with no evidence it may be more than enough.
///
/// Talking to each provider is handled by <see cref="Wire"/>, which knows the three dialects there are
/// —Anthropic, OpenAI and Gemini—. Here is only what does not depend on whom one talks to: the cache, the
/// retries, the limit and what is asked.
///
/// Without configuration it is read from the environment, which is how the command line uses it:
///
///   JONDO_LLM_URL       by default https://api.anthropic.com
///   JONDO_LLM_MODEL     by default claude-sonnet-5
///   JONDO_LLM_KEY       or, if it is not there, ANTHROPIC_API_KEY
///   JONDO_LLM_DIALECTO  anthropic (by default), openai or gemini
/// </summary>
public sealed class Llm : IDisposable
{
    /// <summary>The three languages in which something can be asked of a model over HTTP.</summary>
    public enum Dialect
    {
        Anthropic,
        OpenAi,
        Gemini,
    }

    /// <summary>Whom one asks, with what and how many at a time.</summary>
    public sealed record Endpoint(string Url, string Model, string Key, Dialect Dialect, int AtOnce = 4)
    {
        /// <summary>What the environment says, which is how the command line uses it.</summary>
        public static Endpoint FromEnvironment()
        {
            string dialect = Environment.GetEnvironmentVariable("JONDO_LLM_DIALECTO") ?? "";
            return new Endpoint(
                Environment.GetEnvironmentVariable("JONDO_LLM_URL") ?? "https://api.anthropic.com",
                Environment.GetEnvironmentVariable("JONDO_LLM_MODEL") ?? "claude-sonnet-5",
                Environment.GetEnvironmentVariable("JONDO_LLM_KEY")
                    ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "",
                dialect.ToLowerInvariant() switch
                {
                    "openai" => Dialect.OpenAi,
                    "gemini" => Dialect.Gemini,
                    _ => Dialect.Anthropic,
                });
        }

        /// <summary>
        /// Whether it can be called.
        ///
        /// A home server speaks the OpenAI dialect and asks for no key, so always requiring it
        /// would leave out exactly the case that costs no money. What is always needed is where to go
        /// and with which model.
        /// </summary>
        public bool Usable => Url.Length > 0 && Model.Length > 0 &&
                              (Dialect == Dialect.OpenAi || Key.Length > 0);
    }

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly Endpoint _endpoint;
    private readonly string _cache;
    private readonly SemaphoreSlim _gate;

    public Llm(string cacheFolder, Endpoint? endpoint = null)
    {
        _endpoint = endpoint ?? Endpoint.FromEnvironment();
        _cache = cacheFolder;
        _gate = new SemaphoreSlim(Math.Max(1, _endpoint.AtOnce));
        Directory.CreateDirectory(_cache);
    }

    public string Model => _endpoint.Model;

    /// <summary>Whether there is something to call with. Without that, the sweep can only dump the dossiers.</summary>
    public bool Ready => _endpoint.Usable;

    /// <summary>How many answers are already stored, from a batch of questions.</summary>
    public int Cached(IEnumerable<(string Prompt, string System)> questions)
        => questions.Count(q => File.Exists(Path.Combine(_cache, Fingerprint(q.Prompt, q.System) + ".txt")));

    /// <summary>
    /// Asks, or returns what was already asked.
    ///
    /// The hash includes the model and the instructions, not only the dossier: changing the model or
    /// fine-tuning the instructions has to invalidate what is stored, because otherwise one would be mixing
    /// answers from two different criteria in the same table.
    /// </summary>
    public async Task<string> AskAsync(string prompt, string system, CancellationToken cancel = default)
    {
        string path = Path.Combine(_cache, Fingerprint(prompt, system) + ".txt");
        if (File.Exists(path)) return await File.ReadAllTextAsync(path, cancel);

        if (!Ready) throw new InvalidOperationException(
            "falta configurar el modelo: dirección, nombre y —si no es local— la clave");

        await _gate.WaitAsync(cancel);
        try
        {
            string answer = await CallAsync(prompt, system, cancel);

            // Only what can be read again goes to the cache. A 200 with an empty body, or with
            // the JSON cut because the output budget ran out, is a lost answer:
            // storing it makes it permanent, and from then on that message stays mute forever
            // without anyone knowing why. Let it be asked again next time.
            if (Read(answer) == null) return answer;

            // It is written alongside and moved on top. A sweep of two thousand questions gets interrupted
            // —the power goes, one gets tired and hits control-C— and a half-written file is
            // later read as a good, truncated answer, which is the worst kind of error: it does not
            // fail, it lies.
            string half = path + ".escribiendo";
            await File.WriteAllTextAsync(half, answer, cancel);
            File.Move(half, path, overwrite: true);
            return answer;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Asks the provider which models it has.
    ///
    /// It serves two things at once, and that is why it is here: it fills the list so as not to have to type
    /// an identifier by hand, and in passing it checks that the address is correct and the key is valid.
    /// If something is wrong, it shows here —with the message the provider returns— and not three screens
    /// later, in the middle of a sweep of two thousand questions.
    /// </summary>
    public async Task<IReadOnlyList<string>> CatalogueAsync(CancellationToken cancel = default)
    {
        var wire = Wire.For(_endpoint.Dialect);
        using var request = new HttpRequestMessage(HttpMethod.Get, wire.Catalogue(_endpoint.Url));
        wire.Authorize(request, _endpoint.Key);

        using var response = await _http.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync(cancel);
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {Short(detail)}");
        }

        return await wire.ReadCatalogueAsync(response.Content, cancel);
    }

    /// <summary>As much of the error as fits, since some answer with a whole page.</summary>
    private static string Short(string text)
        => text.Length <= 300 ? text : text[..300] + "…";

    private async Task<string> CallAsync(string prompt, string system, CancellationToken cancel)
    {
        var wire = Wire.For(_endpoint.Dialect);
        bool strictJson = wire.SupportsStrictJson;

        for (int attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, wire.Address(_endpoint.Url, _endpoint.Model))
            {
                Content = JsonContent.Create(wire.Body(_endpoint.Model, system, prompt, strictJson)),
            };
            wire.Authorize(request, _endpoint.Key);

            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, cancel); }
            catch (HttpRequestException) when (attempt < 6) { await Wait(attempt, null, cancel); continue; }

            if (response.IsSuccessStatusCode) return await wire.ReadAsync(response.Content, cancel);

            // A server that cannot ask for JSON by contract answers 400. It is removed and repeated
            // at once, without counting the attempt: it is not that it is busy, it is that it does not speak that.
            if (response.StatusCode == HttpStatusCode.BadRequest && strictJson)
            {
                strictJson = false;
                attempt--;
                continue;
            }

            bool worthRetrying = response.StatusCode is HttpStatusCode.TooManyRequests
                                 or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                                 or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
            if (!worthRetrying || attempt >= 6)
            {
                string detail = await response.Content.ReadAsStringAsync(cancel);
                throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
            }

            await Wait(attempt, response.Headers.RetryAfter?.Delta, cancel);
        }
    }

    private static Task Wait(int attempt, TimeSpan? asked, CancellationToken cancel)
        => Task.Delay(asked ?? TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt))), cancel);

    private string Fingerprint(string prompt, string system)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_endpoint.Model + "\n" + system + "\n" + prompt)))[..24];

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    // ─── What is asked of it, and what it is forbidden ──────────────────────────────────
    //
    // The non-negotiable principle: a proposal without what-it-rests-on does not go into the table. That is why
    // the format forces citing the evidence, and that is why «I do not know» is a valid answer and a
    // rewarded one: an empty row costs zero and an invented row costs an afternoon of debugging
    // chasing a message that was never that.

    /// <summary>The instructions, the same for all the sweep's dossiers.</summary>
    public static string System(IEnumerable<Dossier.Anchor> resolved)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            Eres un ingeniero de protocolos trabajando sobre Dofus Unity (Dofus 3), de Ankama.

            El cliente lleva los nombres del protocolo rotados: cada mensaje se llama con tres letras
            minúsculas sin significado, y esas tres letras cambian en cada parche. Tu tarea es
            proponer, para UN mensaje, el nombre que le correspondería en el protocolo real.

            Con qué cuentas:
            - la forma exacta del mensaje, que es dato duro: los números de campo no se barajan
            - de qué otros mensajes es campo
            - las clases del cliente que lo tocan, con los nombres que se le escaparon al ofuscador
            - a veces, lo que ya se ha medido de él viendo pasar el tráfico del juego real

            Cómo se llaman las cosas en Dofus: los nombres del protocolo son PascalCase y suelen
            acabar en Message cuando el mensaje viaja solo por el cable, y no acabar en Message
            cuando es una estructura que va dentro de otro. Los conceptos son los de siempre en
            Dofus: mapa, subárea, celda, actor, entidad contextual, combate, hechizo, oficio,
            inventario, intercambio, gremio, alianza, zaap, mazmorra, arena, almanaque, cofre.

            REGLAS QUE NO SE NEGOCIAN

            1. Sin evidencia no hay nombre. Si lo único que tienes es «int64 en el campo 2», la
               respuesta correcta es confianza "ninguna" y nombre vacío. No pasa nada por no saberlo;
               lo que hace daño es una tabla llena de nombres plausibles y falsos.
            2. En "porque" cita la evidencia concreta que te lleva ahí, nombrando la clase o el
               mensaje del que la sacas. No vale «por la forma del mensaje».
            3. La confianza es una de estas cuatro, y significan esto:
               - "segura"   la evidencia lo dice casi con todas las letras (un nombre filtrado que
                            describe el mensaje, o algo medido en el tráfico)
               - "probable" varias señales apuntan al mismo sitio y ninguna en contra
               - "posible"  encaja, pero encajarían otras dos o tres cosas
               - "ninguna"  no hay por dónde cogerlo
            4. Contesta SÓLO con un objeto JSON, sin texto alrededor y sin vallas de código:
               {"nombre": "...", "confianza": "...", "porque": "..."}
            """);

        var examples = resolved.Where(a => a.Name.Length > 0 && a.Meaning.Length > 0).Take(120).ToList();
        if (examples.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Mensajes de esta misma versión ya resueltos, para que calibres el estilo:");
            sb.AppendLine();
            foreach (var example in examples)
                sb.AppendLine($"  {example.Opcode} -> {example.Name}   ({example.Meaning})");
        }

        return sb.ToString();
    }

    /// <summary>What the model answers, once read.</summary>
    public sealed record Proposal(
        [property: JsonPropertyName("nombre")] string? Name,
        [property: JsonPropertyName("confianza")] string? Confidence,
        [property: JsonPropertyName("porque")] string? Because);

    /// <summary>
    /// Extracts the JSON from the answer even if it comes with decorations.
    ///
    /// It is asked for without code fences and even so sometimes it puts them. Rather than retrying the call
    /// —which costs— it is cheaper to look for the braces.
    /// </summary>
    public static Proposal? Read(string answer)
    {
        int open = answer.IndexOf('{');
        int close = answer.LastIndexOf('}');
        if (open < 0 || close <= open) return null;

        try { return JsonSerializer.Deserialize<Proposal>(answer[open..(close + 1)]); }
        catch (JsonException) { return null; }
    }
}

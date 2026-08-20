using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jondo.Unity.Reversing;

/// <summary>
/// El modelo, con correa.
///
/// La etapa 4 son miles de llamadas a un modelo de lenguaje, y eso trae tres problemas que no son
/// de inteligencia sino de fontanería: cuesta dinero, falla a ratos, y si no se guarda lo que
/// contesta hay que volver a pagarlo. Así que:
///
///   caché      cada pregunta se guarda en disco por su huella. Repetir un barrido entero después
///              de tocar el formato de salida no cuesta nada.
///   reintentos con espera creciente, y respetando el <c>retry-after</c> cuando lo manda. Un 429 a
///              mitad de un barrido de dos mil mensajes no puede tirar el barrido.
///   límite     de cuántas van a la vez, porque el proveedor lo tiene y prefiero llegar yo primero.
///
/// El proveedor no está escrito a fuego, y hay dos motivos para eso. Uno es el dinero: un barrido
/// entero son dos mil preguntas largas y conviene poder elegir a quién pagárselas. El otro es que
/// un modelo que corre en la propia máquina —Ollama, LM Studio, llama.cpp— no cuesta nada y para
/// desbrozar los mil mensajes sin evidencia puede sobrar.
///
/// De hablarle a cada proveedor se encarga <see cref="Wire"/>, que sabe los tres dialectos que hay
/// —Anthropic, OpenAI y Gemini—. Aquí sólo está lo que da igual con quién se hable: la caché, los
/// reintentos, el límite y lo que se le pide.
///
/// Sin configuración se lee del entorno, que es como lo usa la línea de comandos:
///
///   JONDO_LLM_URL       por defecto https://api.anthropic.com
///   JONDO_LLM_MODEL     por defecto claude-sonnet-5
///   JONDO_LLM_KEY       o, si no está, ANTHROPIC_API_KEY
///   JONDO_LLM_DIALECTO  anthropic (por defecto), openai o gemini
/// </summary>
public sealed class Llm : IDisposable
{
    /// <summary>Los tres idiomas en que se le puede pedir algo a un modelo por HTTP.</summary>
    public enum Dialect
    {
        Anthropic,
        OpenAi,
        Gemini,
    }

    /// <summary>A quién se le pregunta, con qué y de cuántas en cuántas.</summary>
    public sealed record Endpoint(string Url, string Model, string Key, Dialect Dialect, int AtOnce = 4)
    {
        /// <summary>Lo que dice el entorno, que es como lo usa la línea de comandos.</summary>
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
        /// Si se le puede llamar.
        ///
        /// Un servidor de casa habla el dialecto de OpenAI y no pide clave, así que exigirla siempre
        /// dejaría fuera justo el caso que no cuesta dinero. Lo que hace falta siempre es adónde ir
        /// y con qué modelo.
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

    /// <summary>Si hay con qué llamar. Sin eso, el barrido sólo puede volcar los expedientes.</summary>
    public bool Ready => _endpoint.Usable;

    /// <summary>Cuántas respuestas hay ya guardadas, de una tanda de preguntas.</summary>
    public int Cached(IEnumerable<(string Prompt, string System)> questions)
        => questions.Count(q => File.Exists(Path.Combine(_cache, Fingerprint(q.Prompt, q.System) + ".txt")));

    /// <summary>
    /// Pregunta, o devuelve lo que ya se preguntó.
    ///
    /// La huella incluye el modelo y las instrucciones, no sólo el expediente: cambiar el modelo o
    /// afinar las instrucciones tiene que invalidar lo guardado, porque si no se estarían mezclando
    /// respuestas de dos criterios distintos en la misma tabla.
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

            // A la caché sólo va lo que se puede volver a leer. Un 200 con el cuerpo vacío, o con
            // el JSON cortado porque se agotó el presupuesto de salida, es una respuesta perdida:
            // guardarla la vuelve permanente, y a partir de ahí ese mensaje queda mudo para siempre
            // sin que nadie sepa por qué. Que se vuelva a preguntar la próxima vez.
            if (Read(answer) == null) return answer;

            // Se escribe al lado y se mueve encima. Un barrido de dos mil preguntas se interrumpe
            // —se corta la luz, se cansa uno y le da a control-C— y un fichero a medio escribir se
            // lee luego como una respuesta buena y truncada, que es la peor clase de error: no
            // falla, miente.
            string half = path + ".escribiendo";
            await File.WriteAllTextAsync(half, answer, cancel);
            File.Move(half, path, overwrite: true);
            return answer;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Le pregunta al proveedor qué modelos tiene.
    ///
    /// Sirve para dos cosas a la vez, y por eso está: llena la lista para no tener que escribir a
    /// mano un identificador, y de paso comprueba que la dirección es correcta y que la clave vale.
    /// Si algo está mal, aquí se ve —con el mensaje que devuelva el proveedor— y no tres pantallas
    /// más adelante, en mitad de un barrido de dos mil preguntas.
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

    /// <summary>Lo que quepa del error, que algunos contestan con una página entera.</summary>
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

            // Un servidor que no sepa pedir JSON por contrato contesta 400. Se le quita y se repite
            // en el acto, sin contar el intento: no es que esté ocupado, es que no habla eso.
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

    // ─── Lo que se le pide, y lo que se le prohíbe ──────────────────────────────────────
    //
    // El principio que no se negocia: una propuesta sin en-qué-se-basa no entra en la tabla. Por eso
    // el formato obliga a citar la evidencia, y por eso «no lo sé» es una respuesta válida y con
    // premio: una fila vacía cuesta cero y una fila inventada cuesta una tarde de depuración
    // persiguiendo un mensaje que nunca fue eso.

    /// <summary>Las instrucciones, iguales para todos los expedientes del barrido.</summary>
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

    /// <summary>Lo que contesta el modelo, una vez leído.</summary>
    public sealed record Proposal(
        [property: JsonPropertyName("nombre")] string? Name,
        [property: JsonPropertyName("confianza")] string? Confidence,
        [property: JsonPropertyName("porque")] string? Because);

    /// <summary>
    /// Saca el JSON de la respuesta aunque venga con adornos.
    ///
    /// Se le pide sin vallas de código y aun así a veces las pone. Antes que reintentar la llamada
    /// —que cuesta— sale más barato buscar las llaves.
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

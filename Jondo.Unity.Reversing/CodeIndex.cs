using System.Text.Json;
using System.Text.Json.Serialization;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;

namespace Jondo.Unity.Reversing;

/// <summary>
/// Qué código del cliente toca cada mensaje, y qué se le puede sacar a eso.
///
/// ─── De dónde sale la señal ─────────────────────────────────────────────────────────────
///
/// Ankama rota los nombres del protocolo, pero NO ofusca el cliente entero: lo que Unity necesita
/// por nombre —los MonoBehaviour, los campos serializados, los espacios de nombres— se queda tal
/// cual. Medido en 3.6.10.10: de <c>Core.dll</c> sobreviven 3.042 nombres de tipo, 15.759 de método
/// y 19.930 de campo. Ahí hay clases que se llaman <c>RoleplayIntroductionService</c> o
/// <c>SmithMagicCoopUi</c>, y lo que toquen dice de qué van.
///
/// Un mensaje se relaciona con el código por cuatro caminos, y se anotan los cuatro:
///
///   la firma      un método que recibe o devuelve el mensaje. Es el más limpio y no hace falta
///                 mirar el cuerpo.
///   la llamada    el ISIL trae el destino ya resuelto, así que se sabe a qué clase pertenece.
///   el tipo       un uso de metadatos que apunta al tipo del mensaje.
///   la dirección  lo que queda: una dirección cruda que hay que resolver contra la tabla de
///                 métodos o contra los metadatos.
///
/// ─── Lo que esto da, medido ─────────────────────────────────────────────────────────────
///
/// De los 2.169 mensajes de 3.6.10.10, a 1.598 (74 %) los toca algún método de fuera del protocolo.
/// De ahí:
///
///   102 (4,7 %)   llegan a un método cuyo NOMBRE se entiende. Arrastrar por el grafo de llamadas
///                 casi no lo mejora, porque entre la red y la interfaz hay un bus de eventos, y un
///                 bus no deja aristas que seguir.
///   524 (24,2 %)  llegan a una clase con HERMANOS legibles, que es lo que de verdad rinde: cinco
///                 veces más.
///    23 (1,1 %)   tienen alguna cadena de texto cerca.
///
/// Conviene decirlo claro, porque cambia el plan: esto NO es lo que hace Snowbot. Su código
/// descompilado enseña que compara el cliente ofuscado contra un <c>gameassemblyNonObfu</c> —una
/// compilación del cliente SIN ofuscar que ellos tienen y nosotros no—. De ahí sacan los nombres.
/// Aquí lo que hay son dos versiones ofuscadas, así que este índice no bautiza mensajes: aporta
/// unas pocas anclas muy buenas y, sobre todo, contexto para desempatar candidatos.
/// </summary>
public static class CodeIndex
{
    /// <summary>Un sitio del código donde se ve el mensaje.</summary>
    public sealed record Sighting(string Method, string Assembly, string How, int Hops)
    {
        /// <summary>Si el nombre dice algo, o es otra ristra de letras rotadas.</summary>
        [JsonIgnore]
        public bool Readable => Legible(Method);
    }

    /// <summary>Todo lo que el código sabe de un mensaje.</summary>
    public sealed record Evidence(
        string Message,
        List<Sighting> Sightings,
        List<string> Context,
        List<string> Strings,
        List<string> Nearby);

    private const int MaxSightings = 60;
    private const int MaxContext = 14;
    private const int MaxStrings = 40;
    private const int MaxNearby = 25;

    /// <summary>
    /// Lo que se le escapó al ofuscador dentro de una clase que sí renombró.
    ///
    /// Ésta es la mejor vena del cliente, y no estaba a la vista. La clase <c>ehl</c> no dice nada,
    /// pero conserva métodos llamados <c>WaitProcessMapComplementaryInfo</c> y
    /// <c>WaitForDroppingObjects</c>; <c>fcf</c> conserva <c>get_roleplayEntitiesService</c> y
    /// <c>get_partyService</c>. Son implementaciones de interfaz, accesores de campos serializados y
    /// máquinas de estado de <c>async</c>, que Unity y el propio runtime necesitan por nombre.
    ///
    /// Medido en <c>Core.dll</c>: 377 clases ofuscadas conservan al menos un nombre así. Un mensaje
    /// que sólo lo toca <c>ehl::dsk</c> parece huérfano mirando el método; mirando a sus hermanos
    /// resulta que vive en la clase que espera la información complementaria del mapa.
    /// </summary>
    private static List<string> Profile(TypeAnalysisContext type)
    {
        var names = new List<string>();

        void Add(string? name)
        {
            if (name == null || names.Count >= 12) return;

            // Los nombres que el compilador fabrica —<Algo>d__7, <Algo>b__0, <Algo>k__BackingField—
            // llevan dentro el nombre original, que es justo lo que interesa. Van anidados unos
            // dentro de otros (<<Algo>b__0>d), así que se coge el de más adentro.
            var inner = Compiler.Match(name);
            if (inner.Success) name = inner.Groups[1].Value;
            else if (name.Contains('<')) return;

            if (name.StartsWith("get_", StringComparison.Ordinal) ||
                name.StartsWith("set_", StringComparison.Ordinal)) name = name[4..];

            // La comprobación va DESPUÉS de desenvolver: <dpgd>k__BackingField tiene mayúsculas por
            // fuera y nada dentro, y colaba el nombre rotado como si dijera algo.
            if (name.Length < 4 || !name.Any(char.IsUpper)) return;
            if (Boilerplate.Contains(name) || names.Contains(name)) return;
            names.Add(name);
        }

        foreach (var method in type.Methods) Add(method.DefaultName);
        foreach (var field in type.Fields) Add(field.Name);
        foreach (var nested in type.NestedTypes) Add(nested.Name);
        return names;
    }

    private static string Mensajes(int count) => count == 1 ? "1 mensaje" : $"{count} mensajes";

    /// <summary>El nombre de verdad, dentro de los ángulos que le puso el compilador.</summary>
    private static readonly System.Text.RegularExpressions.Regex Compiler =
        new(@"<([A-Za-z][A-Za-z0-9_]*)>", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Lo que lleva cualquier clase y por tanto no distingue a ninguna.</summary>
    private static readonly HashSet<string> Boilerplate = new(StringComparer.Ordinal)
    {
        "ToString", "Equals", "GetHashCode", "Dispose", "MoveNext", "SetStateMachine",
        "GetEnumerator", "CompareTo", "Clone", "Update", "LateUpdate", "FixedUpdate",
        "OnEnable", "OnDisable", "Awake", "Start", "OnDestroy", "Invoke", "BeginInvoke",
        "EndInvoke", "Reset", "Init", "Initialize", "Remove", "Enable", "Disable",
    };

    /// <summary>
    /// Recorre el cliente entero y anota, mensaje a mensaje, quién lo toca.
    ///
    /// El barrido analiza los 366.413 métodos y suelta el análisis de cada uno en cuanto lo ha
    /// leído: guardarlos todos se come la memoria y no hace falta, porque lo que interesa se resume
    /// en el momento. Lo único que sobrevive al bucle son las aristas del grafo de llamadas, y ésas
    /// van como índices enteros.
    /// </summary>
    public static Dictionary<string, Evidence> Build(ClientReader client, int hops = 2,
                                                     Action<string>? report = null)
    {
        var protocol = client.Protocol;
        var messages = client.Messages().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var methods = client.AllMethods().ToList();
        var number = new Dictionary<MethodAnalysisContext, int>(methods.Count);
        for (int i = 0; i < methods.Count; i++) number[methods[i]] = i;
        report?.Invoke($"  {methods.Count:N0} métodos, {messages.Count:N0} mensajes");

        var callees = new List<int>[methods.Count];
        var strings = new List<string>?[methods.Count];
        var touches = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var touchedBy = new HashSet<string>?[methods.Count];
        var how = new Dictionary<(string Message, int Method), string>();

        void Note(string? message, int method, string way)
        {
            if (message == null || !messages.Contains(message)) return;
            if (methods[method].DeclaringType?.DeclaringAssembly == protocol) return;   // el propio mensaje no cuenta

            if (!touches.TryGetValue(message, out var list)) touches[message] = list = new List<int>();
            if (!list.Contains(method)) list.Add(method);
            (touchedBy[method] ??= new HashSet<string>(StringComparer.Ordinal)).Add(message);
            how.TryAdd((message, method), way);
        }

        for (int i = 0; i < methods.Count; i++)
        {
            var method = methods[i];
            var destinations = new List<int>();

            foreach (var parameter in method.Parameters)
            {
                if (parameter.ParameterType?.DeclaringAssembly == protocol)
                    Note(parameter.ParameterType.Name, i, "firma");
            }
            if (method.ReturnType?.DeclaringAssembly == protocol) Note(method.ReturnType.Name, i, "firma");

            try
            {
                method.Analyze();
                foreach (var instruction in method.ConvertedIsil ?? [])
                {
                    foreach (var operand in instruction.Operands)
                    {
                        switch (operand.Data)
                        {
                            case IsilMethodOperand call when call.Method != null:
                                if (number.TryGetValue(call.Method, out int target)) destinations.Add(target);
                                if (call.Method.DeclaringType?.DeclaringAssembly == protocol)
                                    Note(call.Method.DeclaringType.Name, i, "llamada");
                                break;

                            case IsilTypeMetadataUsageOperand usage
                                when usage.TypeAnalysisContext?.DeclaringAssembly == protocol:
                                Note(usage.TypeAnalysisContext.Name, i, "tipo");
                                break;

                            case IsilImmediateOperand immediate when immediate.Value is ulong address:
                                Resolve(address, i, destinations);
                                break;

                            case IsilMemoryOperand memory when memory.Base == null && memory.Index == null:
                                Resolve((ulong)memory.Addend, i, destinations);
                                break;
                        }
                    }
                }
            }
            catch
            {
                // Un método que el levantador no sabe leer no vale la pena perseguirlo: son los de
                // código nativo puro y los que Cpp2IL marca como no soportados. El resto del
                // barrido no se entera.
            }
            finally
            {
                try { method.ReleaseAnalysisData(); } catch { }
            }

            callees[i] = destinations;
        }

        void Resolve(ulong address, int from, List<int> destinations)
        {
            if (address <= 0x1_0000_0000) return;

            // Una dirección nativa puede ser de muchos métodos a la vez: IL2CPP pliega los cuerpos
            // idénticos y comparte el de los genéricos. Medido en este cliente: de 261.768
            // direcciones, 24.227 las comparten dos o más métodos, y una de ellas la comparten
            // 2.319. Quedarse con el primero de la lista es echarlo a suertes, y cuando toca un
            // mensaje se le atribuye a quien no era: así acabó `jzd` —que no ha visto una fuente en
            // su vida— con un expediente entero de TMP_FontAsset, y `heo` con uno de FileStream.
            //
            // Si la dirección no señala a uno solo, no señala. Ni se anota ni se cuenta la arista.
            if (client.App.MethodsByAddress.TryGetValue(address, out var found) && found.Count > 0)
            {
                if (found.Count > 1) return;
                if (number.TryGetValue(found[0], out int target)) destinations.Add(target);
                if (found[0].DeclaringType?.DeclaringAssembly == protocol)
                    Note(found[0].DeclaringType.Name, from, "dirección");
                return;
            }

            try
            {
                var usage = LibCpp2IlMain.GetAnyGlobalByAddress(address);
                if (usage is not { IsValid: true }) return;
                switch (usage.Type)
                {
                    case MetadataUsageType.StringLiteral:
                        string? text = usage.AsLiteral();
                        if (Worth(text)) (strings[from] ??= new List<string>()).Add(text!);
                        break;
                    case MetadataUsageType.TypeInfo:
                    case MetadataUsageType.Type:
                        Note(usage.AsType()?.baseType?.Name, from, "tipo");
                        break;
                    case MetadataUsageType.MethodDef:
                        Note(usage.AsMethod()?.DeclaringType?.Name, from, "dirección");
                        break;
                    case MetadataUsageType.FieldInfo:
                        Note(usage.AsField()?.DeclaringType?.Name, from, "campo");
                        break;
                }
            }
            catch { }
        }

        report?.Invoke($"  {touches.Count:N0} mensajes tocados desde fuera del protocolo");

        // Quién llama a quién, del revés: para subir del mensaje hacia los nombres que se entienden.
        var callers = new List<int>[methods.Count];
        for (int i = 0; i < methods.Count; i++) callers[i] = new List<int>();
        for (int i = 0; i < methods.Count; i++)
            foreach (int called in callees[i]) callers[called].Add(i);

        string Label(int i)
        {
            var type = methods[i].DeclaringType;
            return $"{type?.FullName}::{methods[i].DefaultName}";
        }

        // A cuántos mensajes llega cada clase. Es lo que separa una pista de un ruido.
        var reach = new Dictionary<TypeAnalysisContext, HashSet<string>>();
        for (int i = 0; i < methods.Count; i++)
        {
            if (touchedBy[i] is not { Count: > 0 } mine) continue;
            var type = methods[i].DeclaringType;
            if (type == null) continue;
            if (!reach.TryGetValue(type, out var all)) reach[type] = all = new HashSet<string>(StringComparer.Ordinal);
            all.UnionWith(mine);
        }

        var profiles = new Dictionary<TypeAnalysisContext, List<string>>();
        var evidence = new Dictionary<string, Evidence>(StringComparer.Ordinal);
        foreach (var message in messages.OrderBy(m => m, StringComparer.Ordinal))
        {
            var seen = new Dictionary<int, int>();       // método -> a cuántos saltos se ha llegado
            if (touches.TryGetValue(message, out var direct))
                foreach (int i in direct) seen[i] = 0;

            var front = seen.Keys.ToList();
            for (int hop = 1; hop <= hops && front.Count > 0; hop++)
            {
                var next = new List<int>();
                foreach (int i in front)
                    foreach (int parent in callers[i])
                        if (seen.TryAdd(parent, hop)) next.Add(parent);
                front = next;
            }

            // Lo legible primero y lo más cercano antes: es lo que se le va a enseñar al modelo, y
            // en un expediente lo que va arriba es lo que se lee.
            var sightings = seen
                .Select(p => new Sighting(Label(p.Key),
                                          methods[p.Key].DeclaringType?.DeclaringAssembly?.Definition?.AssemblyName.Name ?? "?",
                                          how.GetValueOrDefault((message, p.Key), "arrastre"),
                                          p.Value))
                .OrderByDescending(s => s.Readable)
                .ThenBy(s => s.Hops)
                .ThenBy(s => s.Method, StringComparer.Ordinal)
                .Take(MaxSightings)
                .ToList();

            // Los hermanos que se le escaparon al ofuscador, de los más cercanos a los más lejanos.
            //
            // Con cuántos mensajes comparte cada clase va DENTRO de la línea, y no es adorno: una
            // clase que toca quince mensajes no distingue a ninguno de los quince. Sin ese número,
            // «eqq: PresetListEventWhenCharacterInfo» hizo que dos mensajes distintos —el de las
            // barras de atajos y el de los conjuntos del guardarropa— se llamaran los dos
            // PresetsMessage. Con el número puesto, esa pista se lee como lo que es.
            var context = new List<string>();
            var already = new HashSet<TypeAnalysisContext>();
            foreach (var (i, hop) in seen.OrderBy(p => p.Value).Select(p => (p.Key, p.Value)))
            {
                var type = methods[i].DeclaringType;
                if (type == null || type.DeclaringAssembly == protocol) continue;

                // Una clase, una línea, la de más cerca. La misma clase puede aparecer tocando el
                // mensaje Y llamando a quien lo toca, y repetirla cambiando sólo los saltos llena
                // el expediente de ecos.
                if (!already.Add(type)) continue;
                if (!profiles.TryGetValue(type, out var names))
                    profiles[type] = names = Profile(type);
                if (names.Count == 0) continue;

                // «Sólo a éste» sólo se puede decir de una clase que TOCA el mensaje. Las que
                // llegan por arrastre no están en la cuenta de alcance —ahí sólo entran las que
                // tocan— y con el valor por defecto puesto a uno salían todas marcadas como la
                // pista más fuerte del expediente siendo la más floja: 32 casos en el índice, con
                // ocho clases de Mono.CSharp señalando con el dedo a un mensaje del protocolo.
                int shared = reach.GetValueOrDefault(type)?.Count ?? 0;
                string cuantos = hop > 0
                    ? $"a {Mensajes(shared)}, y a éste sólo de refilón, a {hop} salto{(hop == 1 ? "" : "s")}"
                    : shared <= 1 ? "sólo a éste" : $"a {Mensajes(shared)}";
                string line = $"{type.FullName} (toca {cuantos}): {string.Join(", ", names)}";
                if (!context.Contains(line)) context.Add(line);
                if (context.Count >= MaxContext) break;
            }

            var texts = seen.Keys
                .Select(i => strings[i])
                .Where(l => l != null)
                .SelectMany(l => l!)
                .Distinct(StringComparer.Ordinal)
                .Take(MaxStrings)
                .ToList();

            // Los mensajes que se manejan al lado de éste. Un mensaje solo no dice nada; un mensaje
            // que sale siempre con otros tres es una escena.
            var nearby = (touches.GetValueOrDefault(message) ?? new List<int>())
                .Select(i => touchedBy[i])
                .Where(s => s != null)
                .SelectMany(s => s!)
                .Where(m => m != message)
                .GroupBy(m => m, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .Take(MaxNearby)
                .ToList();

            evidence[message] = new Evidence(message, sightings, context, texts, nearby);
        }

        int withName = evidence.Count(e => e.Value.Sightings.Any(s => s.Readable));
        int withContext = evidence.Count(e => e.Value.Context.Count > 0);
        report?.Invoke($"  {withName:N0} mensajes llegan a un método con nombre legible " +
                       $"({100.0 * withName / messages.Count:0.0} %)");
        report?.Invoke($"  {withContext:N0} mensajes llegan a una clase con hermanos legibles " +
                       $"({100.0 * withContext / messages.Count:0.0} %)");

        return evidence;
    }

    /// <summary>
    /// Si un nombre dice algo o es otra ristra rotada.
    ///
    /// Ankama reparte nombres de dos a cuatro letras minúsculas. Cualquier cosa con mayúsculas
    /// dentro y de cuatro letras para arriba se le escapó al ofuscador, y ésos son los que valen.
    /// Se mira el método, su clase y el último tramo del espacio de nombres: <c>bacr</c> no dice
    /// nada, pero <c>Core.Services.Roleplay.RoleplayIntroductionService::bacr</c> sí.
    /// </summary>
    public static bool Legible(string label)
    {
        int split = label.IndexOf("::", StringComparison.Ordinal);
        string method = split < 0 ? label : label[(split + 2)..];
        string type = split < 0 ? "" : label[..split];

        if (Word(method)) return true;
        foreach (string part in type.Split('.')) if (Word(part)) return true;
        return false;

        static bool Word(string s)
            => s.Length >= 4 && !s.StartsWith('<') && s.Any(char.IsUpper);
    }

    private static bool Worth(string? text)
        => !string.IsNullOrWhiteSpace(text) && text.Length is > 2 and < 90;

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Save(Dictionary<string, Evidence> evidence, string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(evidence, Format));

    public static Dictionary<string, Evidence> Load(string path)
        => JsonSerializer.Deserialize<Dictionary<string, Evidence>>(File.ReadAllText(path))
           ?? new Dictionary<string, Evidence>(StringComparer.Ordinal);
}

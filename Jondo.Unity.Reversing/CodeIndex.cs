using System.Text.Json;
using System.Text.Json.Serialization;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;

namespace Jondo.Unity.Reversing;

/// <summary>
/// Which client code touches each message, and what can be got out of that.
///
/// ─── Where the signal comes from ────────────────────────────────────────────────────────
///
/// Ankama rotates the protocol's names, but it does NOT obfuscate the whole client: what Unity needs
/// by name —the MonoBehaviours, the serialised fields, the namespaces— stays as
/// it is. Measured in 3.6.10.10: of <c>Core.dll</c> 3,042 type names survive, 15,759 method
/// and 19,930 field names. There are classes there called <c>RoleplayIntroductionService</c> or
/// <c>SmithMagicCoopUi</c>, and what they touch says what they are about.
///
/// A message relates to the code in four ways, and all four are noted:
///
///   the signature a method that receives or returns the message. It is the cleanest and there is no need
///                 to look at the body.
///   the call      the ISIL brings the target already resolved, so the class it belongs to is known.
///   the type      a metadata use that points at the message's type.
///   the address   what is left: a raw address that has to be resolved against the
///                 method table or against the metadata.
///
/// ─── What this gives, measured ──────────────────────────────────────────────────────────
///
/// Of the 2,169 messages of 3.6.10.10, 1,598 (74 %) are touched by some method outside the protocol.
/// From there:
///
///   102 (4.7 %)   reach a method whose NAME is understandable. Dragging through the call graph
///                 barely improves it, because between the network and the interface there is an event bus, and a
///                 bus leaves no edges to follow.
///   524 (24.2 %)  reach a class with readable SIBLINGS, which is what really pays off: five
///                 times more.
///    23 (1.1 %)   have some text string nearby.
///
/// It is worth saying plainly, because it changes the plan: this is NOT what Snowbot does. Its
/// decompiled code shows that it compares the obfuscated client against a <c>gameassemblyNonObfu</c> —a
/// build of the client WITHOUT obfuscation that they have and we do not—. That is where they get the names.
/// Here what there is is two obfuscated versions, so this index does not name messages: it contributes
/// a few very good anchors and, above all, context to break ties between candidates.
/// </summary>
public static class CodeIndex
{
    /// <summary>A place in the code where the message is seen.</summary>
    public sealed record Sighting(string Method, string Assembly, string How, int Hops)
    {
        /// <summary>Whether the name says something, or is another string of rotated letters.</summary>
        [JsonIgnore]
        public bool Readable => Legible(Method);
    }

    /// <summary>Everything the code knows about a message.</summary>
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
    /// What escaped the obfuscator inside a class it did rename.
    ///
    /// This is the client's best vein, and it was not in sight. Class <c>ehl</c> says nothing,
    /// but it keeps methods called <c>WaitProcessMapComplementaryInfo</c> and
    /// <c>WaitForDroppingObjects</c>; <c>fcf</c> keeps <c>get_roleplayEntitiesService</c> and
    /// <c>get_partyService</c>. They are interface implementations, accessors of serialised fields and
    /// <c>async</c> state machines, which Unity and the runtime itself need by name.
    ///
    /// Measured in <c>Core.dll</c>: 377 obfuscated classes keep at least one name like that. A message
    /// that only <c>ehl::dsk</c> touches looks orphaned looking at the method; looking at its siblings
    /// it turns out it lives in the class that waits for the map's complementary information.
    /// </summary>
    private static List<string> Profile(TypeAnalysisContext type)
    {
        var names = new List<string>();

        void Add(string? name)
        {
            if (name == null || names.Count >= 12) return;

            // The names the compiler makes up —<Algo>d__7, <Algo>b__0, <Algo>k__BackingField—
            // carry the original name inside, which is exactly what matters. They go nested one
            // inside another (<<Algo>b__0>d), so the innermost one is taken.
            var inner = Compiler.Match(name);
            if (inner.Success) name = inner.Groups[1].Value;
            else if (name.Contains('<')) return;

            if (name.StartsWith("get_", StringComparison.Ordinal) ||
                name.StartsWith("set_", StringComparison.Ordinal)) name = name[4..];

            // The check goes AFTER unwrapping: <dpgd>k__BackingField has capitals on the
            // outside and nothing inside, and it slipped the rotated name in as if it said something.
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

    /// <summary>The real name, inside the angle brackets the compiler put on it.</summary>
    private static readonly System.Text.RegularExpressions.Regex Compiler =
        new(@"<([A-Za-z][A-Za-z0-9_]*)>", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>What any class carries and therefore distinguishes none.</summary>
    private static readonly HashSet<string> Boilerplate = new(StringComparer.Ordinal)
    {
        "ToString", "Equals", "GetHashCode", "Dispose", "MoveNext", "SetStateMachine",
        "GetEnumerator", "CompareTo", "Clone", "Update", "LateUpdate", "FixedUpdate",
        "OnEnable", "OnDisable", "Awake", "Start", "OnDestroy", "Invoke", "BeginInvoke",
        "EndInvoke", "Reset", "Init", "Initialize", "Remove", "Enable", "Disable",
    };

    /// <summary>
    /// Walks the whole client and notes, message by message, who touches it.
    ///
    /// The sweep analyses the 366,413 methods and drops each one's analysis as soon as it has
    /// read it: keeping them all eats the memory and is not needed, because what matters is summarised
    /// on the spot. The only thing that survives the loop is the call graph's edges, and those
    /// go as integer indices.
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
            if (methods[method].DeclaringType?.DeclaringAssembly == protocol) return;   // the message itself does not count

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
                // A method the lifter cannot read is not worth chasing: they are the
                // pure native code ones and those Cpp2IL marks as unsupported. The rest of the
                // sweep does not notice.
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

            // A native address can belong to many methods at once: IL2CPP folds identical
            // bodies and shares the generics' one. Measured on this client: of 261,768
            // addresses, 24,227 are shared by two or more methods, and one of them is shared by
            // 2,319. Keeping the first of the list is drawing lots, and when it touches a
            // message it is attributed to the wrong one: that is how `jzd` —which has never seen a font in
            // its life— ended up with a whole dossier of TMP_FontAsset, and `heo` with one of FileStream.
            //
            // If the address does not point to a single one, it does not point. It is neither noted nor is the edge counted.
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

        // Who calls whom, reversed: to climb from the message to the names that are understandable.
        var callers = new List<int>[methods.Count];
        for (int i = 0; i < methods.Count; i++) callers[i] = new List<int>();
        for (int i = 0; i < methods.Count; i++)
            foreach (int called in callees[i]) callers[called].Add(i);

        string Label(int i)
        {
            var type = methods[i].DeclaringType;
            return $"{type?.FullName}::{methods[i].DefaultName}";
        }

        // How many messages each class reaches. It is what separates a clue from noise.
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

            // The readable first and the closest before: it is what is going to be shown to the model, and
            // in a dossier what goes at the top is what gets read.
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

            // The siblings that slipped past the obfuscator, from the closest to the furthest.
            //
            // How many messages each class shares goes INSIDE the line, and it is not decoration: a
            // class that touches fifteen messages distinguishes none of the fifteen. Without that number,
            // «eqq: PresetListEventWhenCharacterInfo» made two different messages —the one for the
            // shortcut bars and the one for the wardrobe outfits— both be called
            // PresetsMessage. With the number in place, that clue reads as what it is.
            var context = new List<string>();
            var already = new HashSet<TypeAnalysisContext>();
            foreach (var (i, hop) in seen.OrderBy(p => p.Value).Select(p => (p.Key, p.Value)))
            {
                var type = methods[i].DeclaringType;
                if (type == null || type.DeclaringAssembly == protocol) continue;

                // One class, one line, the closest one. The same class can appear touching the
                // message AND calling whoever touches it, and repeating it changing only the hops fills
                // the dossier with echoes.
                if (!already.Add(type)) continue;
                if (!profiles.TryGetValue(type, out var names))
                    profiles[type] = names = Profile(type);
                if (names.Count == 0) continue;

                // «Only this one» can only be said of a class that TOUCHES the message. Those that
                // arrive by dragging are not in the reach count —only those that
                // touch go in there— and with the default value set to one they all came out marked as the
                // strongest clue of the dossier while being the weakest: 32 cases in the index, with
                // eight Mono.CSharp classes pointing the finger at a protocol message.
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

            // The messages handled next to this one. A message alone says nothing; a message
            // that always comes out with three others is a scene.
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
    /// Whether a name says something or is another rotated string.
    ///
    /// Ankama hands out names of two to four lowercase letters. Anything with capitals
    /// inside and four letters or more slipped past the obfuscator, and those are the ones that count.
    /// The method, its class and the last stretch of the namespace are looked at: <c>bacr</c> says
    /// nothing, but <c>Core.Services.Roleplay.RoleplayIntroductionService::bacr</c> does.
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

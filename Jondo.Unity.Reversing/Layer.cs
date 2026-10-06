using System.Text;
using System.Text.RegularExpressions;

namespace Jondo.Unity.Reversing;

/// <summary>
/// The <c>Op</c> layer: one name per opcode, in a single file, generated.
///
/// The emulator carries 495 three-letter literals spread over 310 opcodes and 40 files. The day
/// Ankama rotates the names they all have to be changed, and editing them by hand is not a plan: it is the
/// reason a perfect mapping today cannot be applied. With this layer the patch touches one
/// file.
///
/// ─── Why it is generated and not written ────────────────────────────────────────────────
///
/// There was already an attempt by hand, <c>OpcodeRegistry.cs</c>, and it went wrong both possible ways:
/// nobody used it —zero references outside itself— and on top of that it lied. It said <c>kub</c> was
/// fight placement when it is the character sheet, and that <c>kqu</c> was the character list
/// request when the anchor itself warns that it is NOT. A hand-written table that
/// nobody runs rots in silence; this one comes from the measured data and is redone on every patch.
///
/// ─── What is an opcode and what is not ──────────────────────────────────────────────────
///
/// A three-lowercase-letter literal is not enough. Measured over the whole emulator, of the 310
/// there are: 251 are real protocol messages, 49 are 3.6.4.3 remnants that no longer exist, and 10
/// are not opcodes at all —<c>key</c>, <c>msg</c>, <c>rid</c>, <c>tag</c>, <c>unk</c>,
/// <c>ids</c>, <c>rol</c> and the syllables <c>bel</c>, <c>dan</c>, <c>gor</c> of the name generator.
///
/// The only criterion that separates the three heaps well is asking the client: **it is an opcode if it is
/// a protocol message**. The rest are heuristics that get it wrong.
///
/// And one real collision remains that no rule resolves: <c>kro</c> is both a
/// protocol message and a syllable of the name generator. It goes in <see cref="Forbidden"/>, by hand and
/// explained, because a list of exceptions with a written reason is honest and a rule twisted
/// to make it fit is not.
/// </summary>
public static class Layer
{
    /// <summary>
    /// Opcodes that are protocol messages but that are NOT used as such in the emulator.
    ///
    /// <c>kro</c> is a syllable of the character name generator —<c>"kro", "bel", "dan",
    /// "gor"</c>— and it so happens that there is also a message called that. Replacing it
    /// would change the names the character creator proposes.
    /// </summary>
    public static readonly HashSet<string> Forbidden = new(StringComparer.Ordinal) { "kro" };

    /// <summary>An opcode with its place in the layer.</summary>
    /// <param name="Id">The C# identifier: the real name if it is known, and if not the opcode itself.</param>
    /// <param name="Uses">How many times it appears in the code, to know what hurts most.</param>
    public sealed record Slot(string Id, string Opcode, string Name, string Meaning, int Uses);

    /// <summary>What the sweep found, including what is NOT an opcode.</summary>
    /// <param name="Slots">The real opcodes, already with an identifier.</param>
    /// <param name="Stale">Literals that were opcodes in another version and no longer exist here.</param>
    /// <param name="Ignored">Three-letter literals that are opcodes of nothing.</param>
    public sealed record Sweep(List<Slot> Slots, List<string> Stale, List<string> Ignored,
                              Dictionary<string, string> Renames);

    private static readonly Regex Literal = new("\"([a-z]{3})\"", RegexOptions.Compiled);
    private static readonly Regex Uri = new("\"type\\.ankama\\.com/([a-z]{3})\"", RegexOptions.Compiled);

    /// <summary>
    /// A using DIRECTIVE, which is a different thing from a using statement.
    ///
    /// It requires the whole line —namespace and semicolon, nothing else— because the first version
    /// looked for «starts with using » and <c>using var ms = new MemoryStream();</c> also meets that
    /// inside a method. The directive slipped into the middle of a function's body and eleven
    /// files stopped compiling.
    /// </summary>
    private static readonly Regex Directive =
        new(@"^\s*(global\s+)?using\s+(static\s+)?[\w.]+\s*;\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Where the directives block ends: it stops looking on reaching the namespace,
    /// because from there on whatever looks like a directive no longer is one.
    /// </summary>
    private static int LastDirective(List<string> lines)
    {
        int last = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith("namespace ", StringComparison.Ordinal)) break;
            if (Directive.IsMatch(lines[i])) last = i;
        }
        return last;
    }

    /// <summary>
    /// Sweeps the emulator's code and decides what is an opcode against the client's protocol.
    /// </summary>
    /// <param name="known">The protocol messages of THIS version.</param>
    /// <param name="wasKnown">Those of the previous version, to know what is a remnant and what is garbage.</param>
    public static Sweep Scan(string sourceFolder,
                             IReadOnlyCollection<string> known,
                             IReadOnlyCollection<string> wasKnown,
                             IReadOnlyDictionary<string, Dossier.Anchor> anchors,
                             IReadOnlyDictionary<string, string> bound)
    {
        var uses = new Dictionary<string, int>(StringComparer.Ordinal);
        var stale = new HashSet<string>(StringComparer.Ordinal);
        var ignored = new HashSet<string>(StringComparer.Ordinal);

        // What the layer already says today, if it exists.
        //
        // Without this the command only works ONCE, and I found out the worst way: the first
        // pass replaces the literals with Op.Whatever, so the second does not find a single
        // three-letter literal, believes the emulator uses no opcode and rewrites Op.cs
        // empty. On patch day that would have erased the whole layer instead of updating it.
        var already = Existing(sourceFolder);

        foreach (string file in Directory.EnumerateFiles(sourceFolder, "*.cs", SearchOption.AllDirectories))
        {
            if (Skip(file)) continue;

            foreach (string line in File.ReadLines(file))
            {
                // An opcode cited in a comment is not a use: nobody reads it at run time, and
                // replacing it would leave the comment saying «Op.Foo» where it said «icw».
                string clean = line.TrimStart();
                if (clean.StartsWith("//", StringComparison.Ordinal) ||
                    clean.StartsWith("*", StringComparison.Ordinal)) continue;

                foreach (Match match in Literal.Matches(line))
                {
                    string opcode = match.Groups[1].Value;
                    if (Forbidden.Contains(opcode)) continue;

                    if (known.Contains(opcode)) uses[opcode] = uses.GetValueOrDefault(opcode) + 1;
                    else if (wasKnown.Contains(opcode)) stale.Add(opcode);
                    else ignored.Add(opcode);
                }

                // And those that already went through the layer, which are written Op.Whatever and carry no
                // quotes. For the count they are worth exactly the same: they are uses of the opcode.
                foreach (Match match in Through.Matches(line))
                {
                    if (!already.TryGetValue(match.Groups[1].Value, out string? opcode)) continue;
                    if (known.Contains(opcode)) uses[opcode] = uses.GetValueOrDefault(opcode) + 1;
                    else if (wasKnown.Contains(opcode)) stale.Add(opcode);
                }
            }
        }

        // The real names can repeat: two old messages with the same proposed name
        // would give two constants with the same identifier and the file would not compile. It is detected
        // here and the second keeps its opcode as its identifier, which is always unique.
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var slots = new List<Slot>();

        foreach (var (opcode, count) in uses.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            anchors.TryGetValue(opcode, out var anchor);

            // The name comes from what was bound by hand, NEVER from the anchor: the anchors' names were
            // proposed by us and none turned out to be Ankama's. The anchor's meaning does
            // count and is kept, because it is measured against captures.
            string name = bound.GetValueOrDefault(opcode, "");
            string id = Identifier(name, opcode);

            if (!taken.Add(id)) continue;
            slots.Add(new Slot(id, opcode, name, anchor?.Meaning ?? "", count));
        }

        // What each constant was called before and what it is called now. Without this, changing the
        // identifiers' criterion leaves the emulator not compiling: there are hundreds of «Op.Whatever»
        // spread around pointing to a name that no longer exists.
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        var byOpcode = slots.ToDictionary(s => s.Opcode, s => s.Id, StringComparer.Ordinal);
        foreach (var (oldId, opcode) in already)
        {
            if (byOpcode.TryGetValue(opcode, out string? newId) && newId != oldId) renames[oldId] = newId;
        }

        return new Sweep(
            slots.OrderBy(s => s.Id, StringComparer.Ordinal).ToList(),
            stale.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            ignored.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            renames);
    }

    /// <summary>A use that already went through the layer: <c>Op.Whatever</c>.</summary>
    private static readonly Regex Through = new(@"\bOp\.([A-Z][A-Za-z0-9_]*)\b", RegexOptions.Compiled);

    /// <summary>A layer constant as it is written today in Op.cs.</summary>
    private static readonly Regex Declared =
        new(@"public const string ([A-Za-z0-9_]+) = ""([a-z]{3})"";", RegexOptions.Compiled);

    /// <summary>
    /// What the layer says today: from the identifier to the opcode.
    ///
    /// If there is no Op.cs yet —the first time— it comes out empty and the sweep works only with the
    /// literals, which is exactly what there is at that moment.
    /// </summary>
    private static Dictionary<string, string> Existing(string sourceFolder)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string path = Path.Combine(sourceFolder, "Jondo.Unity.Protocol", "Op.cs");
        if (!File.Exists(path)) return map;

        foreach (Match match in Declared.Matches(File.ReadAllText(path)))
        {
            map[match.Groups[1].Value] = match.Groups[2].Value;
        }
        return map;
    }

    /// <summary>Folders not swept: the generated, the compiled and the working copies.</summary>
    private static bool Skip(string file)
    {
        string path = file.Replace('\\', '/');
        return path.Contains("/obj/", StringComparison.Ordinal)
            || path.Contains("/bin/", StringComparison.Ordinal)
            || path.Contains("/.claude/", StringComparison.Ordinal)
            || Path.GetFileName(file) == "Op.cs";
    }

    /// <summary>
    /// The C# identifier is ALWAYS the opcode. No names.
    ///
    /// It used to carry them: if the anchor proposed a name, the constant was called
    /// <c>Op.HelloGameMessage</c>. They were removed on measuring them against the 513 real names the
    /// client brings: of the 99 we proposed, <b>none</b> was Ankama's. And it was not bad luck,
    /// it was systematic —96 of 99 ended in «Message», which is the Dofus 2 convention, when the
    /// real protocol uses Event, Request and Response (196, 117 and 105 of 513)—.
    ///
    /// An invented name that looks official is worse than none: it is read in the log, cited
    /// in a conversation and ends up in the documentation as if someone had checked it. The
    /// opcode fools nobody, and the meaning —which is measured against captures— goes to the
    /// comment, which is where it belongs.
    ///
    /// The real names come in through another door: <see cref="Bound"/>, which only has the ones
    /// someone has bound by hand choosing from the real list.
    /// </summary>
    private static string Identifier(string name, string opcode)
        => char.ToUpperInvariant(opcode[0]) + opcode[1..];

    /// <summary>
    /// The names someone has bound by hand, from <c>datos/nombres_ligados_&lt;versión&gt;.tsv</c>.
    ///
    /// It is the only source of names accepted. The file is written by the server log's
    /// drop-down: one chooses from the 513 real names with the packet in front, so what
    /// goes in here someone has recognised by looking, nobody has deduced it by resemblance.
    /// </summary>
    public static Dictionary<string, string> Bound(string dataFolder, string version)
    {
        var bound = new Dictionary<string, string>(StringComparer.Ordinal);
        string path = Path.Combine(dataFolder, $"nombres_ligados_{version}.tsv");
        if (!File.Exists(path)) return bound;

        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith('#')) continue;
            var parts = line.Split('\t');
            if (parts.Length >= 2 && parts[0].Length == 3 && parts[1].Length > 0)
                bound[parts[0].Trim()] = parts[1].Trim();
        }
        return bound;
    }

    /// <summary>Adds a binding to the file, without repeating one already there.</summary>
    public static void Bind(string dataFolder, string version, string opcode, string name)
    {
        string path = Path.Combine(dataFolder, $"nombres_ligados_{version}.tsv");
        var bound = Bound(dataFolder, version);
        bound[opcode] = name;

        var text = new StringBuilder();
        text.AppendLine($"# Opcodes de {version} ligados a su nombre real, a mano.");
        text.AppendLine("#");
        text.AppendLine("# Se eligen de datos/nombres_reales_*.tsv, que son los nombres que el cliente lleva dentro.");
        text.AppendLine("# Aquí no se propone nada: lo que está, está porque alguien lo ha reconocido mirando el");
        text.AppendLine("# paquete. Lo escribe el desplegable del registro del servidor.");
        text.AppendLine("#");
        text.AppendLine("# opcode\tnombre");
        foreach (var (code, real) in bound.OrderBy(p => p.Key, StringComparer.Ordinal))
            text.AppendLine($"{code}\t{real}");

        Directory.CreateDirectory(dataFolder);
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
    }

    /// <summary>Writes the layer file and returns the path.</summary>
    public static string Write(Sweep sweep, string version, string path)
    {
        var text = new StringBuilder();

        text.AppendLine("// GENERADO por «protocolbuilder capa». No editar a mano.");
        text.AppendLine("//");
        text.AppendLine($"// Opcodes de {version}. El día del parche esto se vuelve a generar y el emulador");
        text.AppendLine("// no se toca: los identificadores no cambian, cambia lo que valen.");
        text.AppendLine("//");
        text.AppendLine("// El identificador es el nombre real del mensaje cuando se sabe, y el opcode que tenía en");
        text.AppendLine($"// {version} cuando no. Un identificador como «Hjk» es una etiqueta histórica, no una promesa");
        text.AppendLine("// de que el opcode siga llamándose así.");
        text.AppendLine();
        text.AppendLine("namespace Jondo.Unity.Protocol;");
        text.AppendLine();
        text.AppendLine("/// <summary>");
        text.AppendLine($"/// Los {sweep.Slots.Count} opcodes que el emulador usa de verdad, con nombre.");
        text.AppendLine("///");
        text.AppendLine("/// Son <c>const</c> y no propiedades a propósito: hay etiquetas de <c>switch</c> por medio, y");
        text.AppendLine("/// una etiqueta de <c>case</c> exige una constante de tiempo de compilación.");
        text.AppendLine("/// </summary>");
        text.AppendLine("public static class Op");
        text.AppendLine("{");
        text.AppendLine("    /// <summary>Lo que Ankama pone delante del opcode en el sobre.</summary>");
        text.AppendLine("    public const string Prefix = \"type.ankama.com/\";");
        text.AppendLine();
        text.AppendLine("    /// <summary>El opcode tal y como viaja: con su prefijo delante.</summary>");
        text.AppendLine("    public static string Uri(string opcode) => Prefix + opcode;");
        text.AppendLine();

        foreach (var slot in sweep.Slots)
        {
            if (slot.Meaning.Length > 0)
            {
                text.AppendLine($"    /// <summary>{Escape(slot.Meaning)}</summary>");
            }
            else if (slot.Name.Length > 0)
            {
                text.AppendLine($"    /// <summary>{Escape(slot.Name)}</summary>");
            }
            else
            {
                text.AppendLine($"    /// <summary>Sin identificar. {slot.Uses} uso{(slot.Uses == 1 ? "" : "s")} en el emulador.</summary>");
            }

            text.AppendLine($"    public const string {slot.Id} = \"{slot.Opcode}\";");
            text.AppendLine();
        }

        // ─── The way back: from the opcode to its name ──────────────────────────────────
        //
        // The constants go from the name to the opcode, which is what is needed to WRITE a
        // message. The log needs the opposite: «kuf» arrives on the wire and one has to say what
        // it is. It is generated here and not written by hand for the usual reason —a hand-written table rots—
        // and also that way on patch day the names keep coming out right without touching anything.
        var named = sweep.Slots.Where(s => s.Name.Length > 0).ToList();

        text.AppendLine("    /// <summary>");
        text.AppendLine($"    /// El nombre real del mensaje que viaja con este opcode. Se saben {named.Count}");
        text.AppendLine($"    /// de {sweep.Slots.Count}; de los demás devuelve cadena vacía, que es lo honrado.");
        text.AppendLine("    /// </summary>");
        text.AppendLine("    public static string Label(string opcode) => Labels.GetValueOrDefault(opcode, \"\");");
        text.AppendLine();
        text.AppendLine("    /// <summary>Los que se saben, por opcode.</summary>");
        text.AppendLine("    public static readonly IReadOnlyDictionary<string, string> Labels =");
        text.AppendLine("        new Dictionary<string, string>(StringComparer.Ordinal)");
        text.AppendLine("        {");
        foreach (var slot in named.OrderBy(s => s.Opcode, StringComparer.Ordinal))
        {
            text.AppendLine($"            [\"{slot.Opcode}\"] = \"{slot.Name}\",");
        }
        text.AppendLine("        };");
        text.AppendLine("}");

        string half = path + ".parcial";
        File.WriteAllText(half, text.ToString(), new UTF8Encoding(true));
        File.Move(half, path, overwrite: true);
        return path;
    }

    private static string Escape(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ─── The migration ──────────────────────────────────────────────────────────────────

    /// <summary>A line that changes, to be able to see it before touching anything.</summary>
    public sealed record Change(string File, int Line, string Before, string After);

    /// <summary>
    /// Replaces the literals with the layer's constants.
    ///
    /// It is done from here and not with a loose script because it is not a one-off migration: every time
    /// someone writes a three-letter literal instead of using the layer, running this again
    /// fixes it. A script run one afternoon and lost does not give that.
    ///
    /// It only touches what <see cref="Scan"/> has recognised as an opcode of THIS version. The remnants of
    /// earlier versions stay as they are on purpose: they cannot be translated to anything, and
    /// leaving them in sight is what makes them noticed.
    /// </summary>
    public static List<Change> Apply(string sourceFolder, Sweep sweep, bool write)
    {
        var byOpcode = sweep.Slots.ToDictionary(s => s.Opcode, s => s.Id, StringComparer.Ordinal);
        var renames = sweep.Renames;
        var changes = new List<Change>();

        foreach (string file in Directory.EnumerateFiles(sourceFolder, "*.cs", SearchOption.AllDirectories))
        {
            if (Skip(file)) continue;

            string[] lines = File.ReadAllLines(file);
            bool touched = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i], clean = line.TrimStart();
                if (clean.StartsWith("//", StringComparison.Ordinal) ||
                    clean.StartsWith("*", StringComparison.Ordinal)) continue;

                // The whole envelope first. If it were done the other way round, «type.ankama.com/kub» would already
                // have become «type.ankama.com/» + Op.Kub and the envelope would not be recognised.
                string after = Uri.Replace(line, m =>
                    byOpcode.TryGetValue(m.Groups[1].Value, out string? id) ? $"Op.Uri(Op.{id})" : m.Value);

                after = Literal.Replace(after, m =>
                    byOpcode.TryGetValue(m.Groups[1].Value, out string? id) ? $"Op.{id}" : m.Value);

                // And the constants that have changed name. It happens when the identifiers' criterion
                // changes —as when removing the invented names— and without this the emulator
                // is left not compiling with hundreds of «Op.Whatever» pointing to what is no longer there.
                after = Through.Replace(after, m =>
                    renames.TryGetValue(m.Groups[1].Value, out string? renamed) ? $"Op.{renamed}" : m.Value);

                if (after == line) continue;
                changes.Add(new Change(file, i + 1, line.Trim(), after.Trim()));
                lines[i] = after;
                touched = true;
            }

            if (!touched || !write) continue;

            // The protocol project's own files are already in the namespace, and
            // adding the using to them is superfluous; the rest have to be given it or they do not compile.
            // The comparison is EXACT, not «contains». Several files already bring
            // «using Jondo.Unity.Protocol.Messages;», which contains the string but is another
            // namespace and does not bring Op: taking it as good, seven files were left without the directive.
            var text = lines.ToList();
            if (!text.Any(l => l.Contains("namespace Jondo.Unity.Protocol", StringComparison.Ordinal)) &&
                !text.Any(l => l.Trim() == "using Jondo.Unity.Protocol;"))
            {
                text.Insert(LastDirective(text) + 1, "using Jondo.Unity.Protocol;");
            }

            File.WriteAllLines(file, text, new UTF8Encoding(true));
        }

        return changes;
    }
}

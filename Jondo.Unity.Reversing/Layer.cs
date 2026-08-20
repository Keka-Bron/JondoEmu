using System.Text;
using System.Text.RegularExpressions;

namespace Jondo.Unity.Reversing;

/// <summary>
/// La capa <c>Op</c>: un nombre por opcode, en un solo fichero, generado.
///
/// El emulador lleva 495 literales de tres letras repartidos por 310 opcodes y 40 ficheros. El día
/// que Ankama rote los nombres hay que cambiarlos todos, y editarlos a mano no es un plan: es la
/// razón por la que un mapeo perfecto hoy no se puede aplicar. Con esta capa el parche toca un
/// fichero.
///
/// ─── Por qué se genera y no se escribe ──────────────────────────────────────────────────
///
/// Ya hubo un intento a mano, <c>OpcodeRegistry.cs</c>, y salió mal de las dos maneras posibles:
/// no lo usaba nadie —cero referencias fuera de sí mismo— y encima mentía. Decía que <c>kub</c> era
/// la colocación en combate cuando es la hoja de personaje, y que <c>kqu</c> era la petición de
/// lista de personajes cuando la propia ancla avisa de que NO lo es. Una tabla escrita a mano que
/// nadie ejecuta se pudre en silencio; ésta sale de los datos medidos y se rehace en cada parche.
///
/// ─── Qué es un opcode y qué no ──────────────────────────────────────────────────────────
///
/// Un literal de tres letras minúsculas no basta. Medido sobre el emulador entero, de los 310 que
/// hay: 251 son mensajes de verdad del protocolo, 49 son restos de 3.6.4.3 que ya no existen, y 10
/// no son opcodes en absoluto —<c>key</c>, <c>msg</c>, <c>rid</c>, <c>tag</c>, <c>unk</c>,
/// <c>ids</c>, <c>rol</c> y las sílabas <c>bel</c>, <c>dan</c>, <c>gor</c> del generador de nombres.
///
/// El único criterio que separa bien los tres montones es preguntarle al cliente: **es opcode si es
/// un mensaje del protocolo**. Lo demás son heurísticas que se equivocan.
///
/// Y queda una colisión de verdad que ninguna regla resuelve: <c>kro</c> es a la vez un mensaje del
/// protocolo y una sílaba del generador de nombres. Va en <see cref="Forbidden"/>, a mano y
/// explicada, porque una lista de excepciones con motivo escrito es honrada y una regla retorcida
/// para que encaje no lo es.
/// </summary>
public static class Layer
{
    /// <summary>
    /// Opcodes que son mensajes del protocolo pero que en el emulador NO se usan como tales.
    ///
    /// <c>kro</c> es una sílaba del generador de nombres de personaje —<c>"kro", "bel", "dan",
    /// "gor"</c>— y da la casualidad de que también hay un mensaje que se llama así. Sustituirla
    /// cambiaría los nombres que propone el creador de personajes.
    /// </summary>
    public static readonly HashSet<string> Forbidden = new(StringComparer.Ordinal) { "kro" };

    /// <summary>Un opcode con su sitio en la capa.</summary>
    /// <param name="Id">El identificador de C#: el nombre real si se sabe, y si no el propio opcode.</param>
    /// <param name="Uses">Cuántas veces aparece en el código, para saber qué duele más.</param>
    public sealed record Slot(string Id, string Opcode, string Name, string Meaning, int Uses);

    /// <summary>Lo que el barrido encontró, incluido lo que NO es opcode.</summary>
    /// <param name="Slots">Los opcodes de verdad, ya con identificador.</param>
    /// <param name="Stale">Literales que fueron opcodes en otra versión y aquí ya no existen.</param>
    /// <param name="Ignored">Literales de tres letras que no son opcodes de nada.</param>
    public sealed record Sweep(List<Slot> Slots, List<string> Stale, List<string> Ignored);

    private static readonly Regex Literal = new("\"([a-z]{3})\"", RegexOptions.Compiled);
    private static readonly Regex Uri = new("\"type\\.ankama\\.com/([a-z]{3})\"", RegexOptions.Compiled);

    /// <summary>
    /// Una DIRECTIVA using, que es una cosa distinta de una sentencia using.
    ///
    /// Exige la línea entera —espacio de nombres y punto y coma, nada más— porque la primera versión
    /// buscaba «empieza por using » y eso también lo cumple <c>using var ms = new MemoryStream();</c>
    /// dentro de un método. La directiva se colaba en mitad del cuerpo de una función y once
    /// ficheros dejaron de compilar.
    /// </summary>
    private static readonly Regex Directive =
        new(@"^\s*(global\s+)?using\s+(static\s+)?[\w.]+\s*;\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Dónde acaba el bloque de directivas: se deja de mirar al llegar al espacio de nombres,
    /// porque a partir de ahí lo que se parezca a una directiva ya no lo es.
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
    /// Barre el código del emulador y decide qué es opcode contra el protocolo del cliente.
    /// </summary>
    /// <param name="known">Los mensajes del protocolo de ESTA versión.</param>
    /// <param name="wasKnown">Los de la versión anterior, para saber qué es un resto y qué es basura.</param>
    public static Sweep Scan(string sourceFolder,
                             IReadOnlyCollection<string> known,
                             IReadOnlyCollection<string> wasKnown,
                             IReadOnlyDictionary<string, Dossier.Anchor> anchors)
    {
        var uses = new Dictionary<string, int>(StringComparer.Ordinal);
        var stale = new HashSet<string>(StringComparer.Ordinal);
        var ignored = new HashSet<string>(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(sourceFolder, "*.cs", SearchOption.AllDirectories))
        {
            if (Skip(file)) continue;

            foreach (string line in File.ReadLines(file))
            {
                // Un opcode citado en un comentario no es un uso: no lo lee nadie en ejecución, y
                // sustituirlo dejaría el comentario diciendo «Op.Foo» donde decía «icw».
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
            }
        }

        // Los nombres reales pueden repetirse: dos mensajes viejos con el mismo nombre propuesto
        // darían dos constantes con el mismo identificador y el fichero no compilaría. Se detecta
        // aquí y el segundo se queda con su opcode como identificador, que siempre es único.
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var slots = new List<Slot>();

        foreach (var (opcode, count) in uses.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            anchors.TryGetValue(opcode, out var anchor);
            string name = anchor?.Name ?? "";
            string id = Identifier(name, opcode);

            if (!taken.Add(id))
            {
                id = Identifier("", opcode);
                taken.Add(id);
            }

            slots.Add(new Slot(id, opcode, name, anchor?.Meaning ?? "", count));
        }

        return new Sweep(
            slots.OrderBy(s => s.Id, StringComparer.Ordinal).ToList(),
            stale.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            ignored.OrderBy(s => s, StringComparer.Ordinal).ToList());
    }

    /// <summary>Carpetas que no se barren: lo generado, lo compilado y las copias de trabajo.</summary>
    private static bool Skip(string file)
    {
        string path = file.Replace('\\', '/');
        return path.Contains("/obj/", StringComparison.Ordinal)
            || path.Contains("/bin/", StringComparison.Ordinal)
            || path.Contains("/.claude/", StringComparison.Ordinal)
            || Path.GetFileName(file) == "Op.cs";
    }

    /// <summary>
    /// El identificador de C#: el nombre real cuando se sabe, y si no el opcode en mayúscula.
    ///
    /// Los 205 sin nombre se quedan con <c>Hjk</c>, <c>Iuq</c> y demás. No es bonito, pero es el
    /// nombre por el que hoy los conoce quien trabaja con esto, es único, y es estable: cuando el
    /// parche los renombre, el identificador NO cambia —cambia el valor—, que es justo lo que hace
    /// falta. Si algún día se averigua el nombre de verdad, se regenera y se renombra de una vez.
    /// </summary>
    private static string Identifier(string name, string opcode)
    {
        if (name.Length == 0) return char.ToUpperInvariant(opcode[0]) + opcode[1..];

        var clean = new StringBuilder();
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c)) clean.Append(c);
        }

        string id = clean.ToString();
        if (id.Length == 0 || char.IsDigit(id[0])) return char.ToUpperInvariant(opcode[0]) + opcode[1..];
        return char.ToUpperInvariant(id[0]) + id[1..];
    }

    /// <summary>Escribe el fichero de la capa y devuelve la ruta.</summary>
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

        text.AppendLine("}");

        string half = path + ".parcial";
        File.WriteAllText(half, text.ToString(), new UTF8Encoding(true));
        File.Move(half, path, overwrite: true);
        return path;
    }

    private static string Escape(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ─── La migración ───────────────────────────────────────────────────────────────────

    /// <summary>Una línea que cambia, para poder verlo antes de tocar nada.</summary>
    public sealed record Change(string File, int Line, string Before, string After);

    /// <summary>
    /// Sustituye los literales por las constantes de la capa.
    ///
    /// Se hace desde aquí y no con un guión suelto porque no es una migración de una vez: cada vez
    /// que alguien escriba un literal de tres letras en vez de usar la capa, volver a pasar esto lo
    /// arregla. Un guión que se ejecuta una tarde y se pierde no da eso.
    ///
    /// Sólo toca lo que <see cref="Scan"/> ha reconocido como opcode de ESTA versión. Los restos de
    /// versiones anteriores se quedan como están a propósito: no se pueden traducir a nada, y
    /// dejarlos a la vista es lo que hace que se noten.
    /// </summary>
    public static List<Change> Apply(string sourceFolder, IReadOnlyList<Slot> slots, bool write)
    {
        var byOpcode = slots.ToDictionary(s => s.Opcode, s => s.Id, StringComparer.Ordinal);
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

                // El sobre entero primero. Si se hiciera al revés, «type.ankama.com/kub» ya se
                // habría quedado en «type.ankama.com/» + Op.Kub y el sobre no se reconocería.
                string after = Uri.Replace(line, m =>
                    byOpcode.TryGetValue(m.Groups[1].Value, out string? id) ? $"Op.Uri(Op.{id})" : m.Value);

                after = Literal.Replace(after, m =>
                    byOpcode.TryGetValue(m.Groups[1].Value, out string? id) ? $"Op.{id}" : m.Value);

                if (after == line) continue;
                changes.Add(new Change(file, i + 1, line.Trim(), after.Trim()));
                lines[i] = after;
                touched = true;
            }

            if (!touched || !write) continue;

            // Los ficheros del propio proyecto del protocolo ya están en el espacio de nombres, y
            // añadirles el using sobra; a los demás hay que ponérselo o no compilan.
            // La comparación es EXACTA, no «contiene». Varios ficheros traen ya
            // «using Jondo.Unity.Protocol.Messages;», que contiene la cadena pero es otro espacio de
            // nombres y no trae Op: dándolo por bueno, siete ficheros se quedaron sin directiva.
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

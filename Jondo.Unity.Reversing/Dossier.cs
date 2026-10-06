using System.Text;

namespace Jondo.Unity.Reversing;

/// <summary>
/// A message's dossier: everything known about it, together and in writing.
///
/// It is what is put in front of the model in stage 4, and also what would be put in front of
/// a person. That is the proof that it is well made: if a dossier is not enough for a human
/// to decide, it is not enough for the model either, and what comes out will be an invention with formatting.
///
/// ─── What it carries inside, and why ────────────────────────────────────────────────────
///
///   the shape       the fields with their number and their type. It is the only exact thing: the numbers are not
///                   shuffled between versions.
///   who points to it a single-field message is identical to four hundred others; what
///                   distinguishes it is whose field it is. It goes with the field number, which is what is
///                   kept.
///   the code        the classes that touch it and the names that slipped past the obfuscator
///                   inside them. This is where it shows that <c>jss</c> lives next to
///                   <c>WaitProcessMapComplementaryInfo</c>.
///   the captures    for the measured ones: direction, what it does and with what shape it arrived. They are
///                   few —99 of 2,169— but they are checked truth, not deduction.
///
/// ─── What it does NOT carry ─────────────────────────────────────────────────────────────
///
/// Nothing from the old version. The dossier describes one version and is self-sufficient: naming the
/// message is a different problem from matching it with another patch's, and mixing them makes a
/// matching error turn into a wrong name that nobody reviews afterwards.
/// </summary>
public static class Dossier
{
    /// <summary>An opcode something is known about because it has been seen going by.</summary>
    public sealed record Anchor(string Opcode, string Direction, string Name, string Meaning,
                                string Handler, string Shape);

    /// <summary>Reads the table of what was measured. Lines starting with a hash are prose.</summary>
    public static Dictionary<string, Anchor> Anchors(string path)
    {
        var anchors = new Dictionary<string, Anchor>(StringComparer.Ordinal);
        if (!File.Exists(path)) return anchors;

        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] cells = line.Split('\t');
            if (cells.Length < 6 || cells[0].Length != 3) continue;
            anchors[cells[0]] = new Anchor(cells[0], cells[1], cells[2], cells[3], cells[4], cells[5]);
        }
        return anchors;
    }

    /// <summary>Whose field each message is, and with which number.</summary>
    public static Dictionary<string, List<string>> Parents(Matcher.Model model)
    {
        var parents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var known = model.Messages.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var message in model.Messages)
        {
            foreach (var field in message.Fields)
            {
                if (!known.Contains(field.Type)) continue;
                if (!parents.TryGetValue(field.Type, out var list)) parents[field.Type] = list = new List<string>();
                string entry = $"{message.Name} campo {field.Number}{(field.Repeated ? " (lista)" : "")}";
                if (!list.Contains(entry)) list.Add(entry);
            }
        }
        return parents;
    }

    /// <summary>The whole dossier, as text, ready to read or to send.</summary>
    public static string Build(string message, Matcher.Model model,
                               CodeIndex.Evidence? evidence,
                               IReadOnlyDictionary<string, Anchor> anchors,
                               IReadOnlyDictionary<string, List<string>> parents,
                               string version)
    {
        var shapes = model.Messages.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var enums = model.Enums.ToDictionary(e => e.Name, StringComparer.Ordinal);
        var sb = new StringBuilder();

        sb.AppendLine($"# Mensaje {message}   (Dofus Unity {version})");
        sb.AppendLine();

        // ─── The shape ──────────────────────────────────────────────────────────────────
        sb.AppendLine("## Forma");
        sb.AppendLine();
        sb.AppendLine("```proto");
        Shape(sb, message, shapes, enums, 0);
        sb.AppendLine("```");
        sb.AppendLine();

        // ─── Who points to it ───────────────────────────────────────────────────────────
        if (parents.TryGetValue(message, out var mine) && mine.Count > 0)
        {
            sb.AppendLine("## De quién es campo");
            sb.AppendLine();
            foreach (string entry in mine.Take(20)) sb.AppendLine($"- {entry}");
            if (mine.Count > 20) sb.AppendLine($"- ...y {mine.Count - 20} más");
            sb.AppendLine();
        }

        // ─── What was measured ──────────────────────────────────────────────────────────
        if (anchors.TryGetValue(message, out var anchor))
        {
            sb.AppendLine("## Medido en el juego real");
            sb.AppendLine();
            sb.AppendLine($"- dirección: {anchor.Direction}");
            if (anchor.Meaning.Length > 0) sb.AppendLine($"- qué hace: {anchor.Meaning}");
            if (anchor.Shape.Length > 0) sb.AppendLine($"- forma en el cable: {anchor.Shape}");
            if (anchor.Handler.Length > 0) sb.AppendLine($"- lo trata: {anchor.Handler}");
            sb.AppendLine();
        }

        // ─── The code ───────────────────────────────────────────────────────────────────
        if (evidence != null)
        {
            if (evidence.Context.Count > 0)
            {
                sb.AppendLine("## Clases del cliente que lo tocan");
                sb.AppendLine();
                sb.AppendLine("Los nombres de después de los dos puntos son los que se le escaparon al");
                sb.AppendLine("ofuscador dentro de esa clase, y dicen de qué va la clase. Fíjate en a cuántos");
                sb.AppendLine("mensajes toca cada una: una clase que toca a quince no distingue a ninguno de");
                sb.AppendLine("los quince, y una que toca sólo a éste lo está señalando con el dedo.");
                sb.AppendLine();
                foreach (string line in evidence.Context) sb.AppendLine($"- {line}");
                sb.AppendLine();
            }

            var readable = evidence.Sightings.Where(s => s.Readable).Take(12).ToList();
            if (readable.Count > 0)
            {
                sb.AppendLine("## Métodos concretos");
                sb.AppendLine();
                foreach (var sighting in readable)
                    sb.AppendLine($"- {sighting.Method}   ({sighting.How}, a {sighting.Hops} saltos)");
                sb.AppendLine();
            }

            if (evidence.Strings.Count > 0)
            {
                sb.AppendLine("## Cadenas de texto de por ahí cerca");
                sb.AppendLine();
                foreach (string text in evidence.Strings.Take(20)) sb.AppendLine($"- \"{text}\"");
                sb.AppendLine();
            }

            if (evidence.Nearby.Count > 0)
            {
                sb.AppendLine("## Mensajes que se manejan al lado");
                sb.AppendLine();
                var named = evidence.Nearby.Select(m =>
                    anchors.TryGetValue(m, out var a) && a.Name.Length > 0 ? $"{m} ({a.Name})" : m);
                sb.AppendLine(string.Join(", ", named));
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>The message written as .proto, with those hanging from it one level down.</summary>
    private static void Shape(StringBuilder sb, string name,
                              Dictionary<string, ProtoWriter.Message> shapes,
                              Dictionary<string, ProtoWriter.Enumeration> enums,
                              int depth)
    {
        if (!shapes.TryGetValue(name, out var message))
        {
            sb.AppendLine($"{new string(' ', depth * 2)}// {name}: no está en el protocolo");
            return;
        }

        string pad = new(' ', depth * 2);
        sb.AppendLine($"{pad}message {name} {{");
        foreach (var field in message.Fields.OrderBy(f => f.Number))
        {
            string kind = enums.ContainsKey(field.Type) ? " // enumerado" : "";
            sb.AppendLine($"{pad}  {(field.Repeated ? "repeated " : "")}{field.Type} f{field.Number} = {field.Number};{kind}");

            // One level deep and no more: whoever wants to know about the child opens its dossier. With
            // two levels a big message's dossier becomes unreadable and the model gets lost.
            if (depth == 0 && shapes.ContainsKey(field.Type)) Shape(sb, field.Type, shapes, enums, depth + 2);
        }
        sb.AppendLine($"{pad}}}");
    }
}

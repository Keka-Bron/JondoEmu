using System.Text;

namespace Jondo.Unity.Reversing;

/// <summary>
/// El expediente de un mensaje: todo lo que se sabe de él, junto y por escrito.
///
/// Es lo que se le pone delante al modelo en la etapa 4, y también lo que se le pondría delante a
/// una persona. Ésa es la prueba de que está bien hecho: si un expediente no le llega a un humano
/// para decidir, tampoco le llega al modelo, y lo que salga será una invención con formato.
///
/// ─── Qué lleva dentro, y por qué ────────────────────────────────────────────────────────
///
///   la forma        los campos con su número y su tipo. Es lo único exacto: los números no se
///                   barajan entre versiones.
///   quién le apunta un mensaje de un solo campo es idéntico a otros cuatrocientos; lo que lo
///                   distingue es de quién es campo. Va con el número de campo, que es lo que se
///                   conserva.
///   el código       las clases que lo tocan y los nombres que se le escaparon al ofuscador
///                   dentro de ellas. Aquí es donde aparece que <c>jss</c> vive al lado de
///                   <c>WaitProcessMapComplementaryInfo</c>.
///   las capturas    para los que están medidos: dirección, qué hace y con qué forma llegó. Son
///                   pocos —99 de 2.169— pero son verdad comprobada, no deducción.
///
/// ─── Lo que NO lleva ────────────────────────────────────────────────────────────────────
///
/// Nada de la versión vieja. El expediente describe una versión y se basta sola: bautizar el
/// mensaje es un problema distinto de emparejarlo con el de otro parche, y mezclarlos hace que un
/// error de emparejamiento se convierta en un nombre equivocado que luego nadie revisa.
/// </summary>
public static class Dossier
{
    /// <summary>Un opcode del que se sabe algo porque se ha visto pasar.</summary>
    public sealed record Anchor(string Opcode, string Direction, string Name, string Meaning,
                                string Handler, string Shape);

    /// <summary>Lee la tabla de lo medido. Las líneas que empiezan por almohadilla son prosa.</summary>
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

    /// <summary>De quién es campo cada mensaje, y con qué número.</summary>
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

    /// <summary>El expediente entero, en texto, listo para leer o para mandar.</summary>
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

        // ─── La forma ───────────────────────────────────────────────────────────────────
        sb.AppendLine("## Forma");
        sb.AppendLine();
        sb.AppendLine("```proto");
        Shape(sb, message, shapes, enums, 0);
        sb.AppendLine("```");
        sb.AppendLine();

        // ─── Quién le apunta ────────────────────────────────────────────────────────────
        if (parents.TryGetValue(message, out var mine) && mine.Count > 0)
        {
            sb.AppendLine("## De quién es campo");
            sb.AppendLine();
            foreach (string entry in mine.Take(20)) sb.AppendLine($"- {entry}");
            if (mine.Count > 20) sb.AppendLine($"- ...y {mine.Count - 20} más");
            sb.AppendLine();
        }

        // ─── Lo medido ──────────────────────────────────────────────────────────────────
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

        // ─── El código ──────────────────────────────────────────────────────────────────
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

    /// <summary>El mensaje escrito como .proto, con los que cuelgan de él un nivel más abajo.</summary>
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

            // Un nivel de profundidad y no más: el que quiera saber del hijo abre su expediente. Con
            // dos niveles el expediente de un mensaje grande se vuelve ilegible y el modelo se pierde.
            if (depth == 0 && shapes.ContainsKey(field.Type)) Shape(sb, field.Type, shapes, enums, depth + 2);
        }
        sb.AppendLine($"{pad}}}");
    }
}

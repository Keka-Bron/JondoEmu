using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jondo.Unity.Reversing;

/// <summary>
/// La pregunta que se le hace al modelo cuando la estructura duda.
///
/// Es una pregunta pequeña y cerrada, y en eso está todo. No es «¿cómo se llama este mensaje?»
/// —que es adivinar— sino «de estos cinco, ¿cuál es?». La diferencia importa:
///
///   · el modelo elige de una lista, así que la respuesta se puede COMPROBAR. Si contesta algo que
///     no está en la lista, se tira. No hay forma de que se invente un opcode.
///   · se le da lo que la estructura no ve: qué hacía el mensaje viejo —medido contra capturas— y
///     qué clases del cliente nuevo tocan a cada candidato.
///   · y se le da lo que la estructura sí ve, para que lo use: la forma exacta de cada uno.
///
/// Si los cinco candidatos tienen exactamente la misma forma y ninguno tiene pistas en el código,
/// no hay nada que elegir y se le pide que lo diga. Callarse es una respuesta correcta.
/// </summary>
public static class TieBreak
{
    /// <summary>Lo que contesta, una vez leído.</summary>
    public sealed record Verdict(
        [property: JsonPropertyName("elegido")] string? Chosen,
        [property: JsonPropertyName("confianza")] string? Confidence,
        [property: JsonPropertyName("porque")] string? Because);

    public static string System() => """
        Eres un ingeniero de protocolos trabajando sobre Dofus Unity (Dofus 3), de Ankama.

        Ankama rota los nombres del protocolo en cada parche: un mensaje que se llamaba «jsd» pasa a
        llamarse otra cosa. Los NÚMEROS de campo y los tipos no se barajan, así que la forma de un
        mensaje se conserva casi igual entre versiones.

        Un emparejador automático ya ha resuelto la mayoría comparando formas. Te llega lo que no ha
        podido: un mensaje de la versión ANTIGUA, del que se sabe qué hace porque se ha visto pasar
        por el cable, y una lista corta de candidatos de la versión NUEVA que tienen esa misma forma.

        Tu trabajo es elegir cuál de los candidatos es. Nada más.

        REGLAS QUE NO SE NEGOCIAN

        1. Contesta con UNO de los candidatos que se te dan, copiado tal cual. Si contestas cualquier
           otra cosa se descarta la respuesta entera.
        2. Si no hay nada que permita distinguirlos —misma forma, ninguna pista en el código— deja
           "elegido" vacío y pon confianza "ninguna". Callarse es correcto y no cuesta nada; elegir
           al azar mete una pareja falsa en el mapeo, y una pareja falsa arrastra a otras detrás.
        3. En "porque" di qué te ha hecho decidir, citando la pista concreta: una clase del cliente
           nuevo, un campo que encaja, de quién es campo el candidato. No vale «parece el más
           probable».
        4. La confianza es "segura", "probable", "posible" o "ninguna".
        5. Contesta SÓLO con un objeto JSON, sin texto alrededor y sin vallas de código:
           {"elegido": "...", "confianza": "...", "porque": "..."}
        """;

    /// <summary>El expediente de una duda: el viejo, lo que se sabía, y los candidatos.</summary>
    public static string Question(Mapper.Row row, Matcher.Model old, Matcher.Model @new,
                                  IReadOnlyDictionary<string, List<string>> newParents,
                                  IReadOnlyDictionary<string, CodeIndex.Evidence> index,
                                  string oldVersion, string newVersion)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# ¿Cuál de estos es el antiguo «{row.Old}»?");
        sb.AppendLine();

        sb.AppendLine($"## El mensaje de {oldVersion}");
        sb.AppendLine();
        if (row.Name.Length > 0) sb.AppendLine($"Se le llamaba **{row.Name}**.");
        if (row.Meaning.Length > 0) sb.AppendLine($"Qué hace, medido viendo pasar el tráfico: {row.Meaning}");
        sb.AppendLine();
        sb.AppendLine("```proto");
        Shape(sb, row.Old, old);
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine($"## Los candidatos de {newVersion}");
        sb.AppendLine();
        sb.AppendLine("Todos tienen la misma forma que el de arriba; por eso el emparejador no ha podido");
        sb.AppendLine("decidir. Lo que los distingue, si algo los distingue, está debajo de cada uno.");
        sb.AppendLine();

        foreach (string candidate in row.Candidates)
        {
            sb.AppendLine($"### {candidate}");
            sb.AppendLine();
            sb.AppendLine("```proto");
            Shape(sb, candidate, @new);
            sb.AppendLine("```");

            if (newParents.TryGetValue(candidate, out var parents) && parents.Count > 0)
            {
                sb.AppendLine($"De quién es campo: {string.Join(", ", parents.Take(8))}");
            }

            if (index.TryGetValue(candidate, out var evidence) && evidence.Context.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Clases del cliente nuevo que lo tocan, con los nombres que se le escaparon al");
                sb.AppendLine("ofuscador dentro de ellas:");
                foreach (string line in evidence.Context.Take(6)) sb.AppendLine($"- {line}");
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("El código del cliente no dice nada de éste.");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void Shape(StringBuilder sb, string name, Matcher.Model model)
    {
        var message = model.Messages.FirstOrDefault(m => m.Name == name);
        if (message == null) { sb.AppendLine($"// {name}: no está"); return; }

        var enums = model.Enums.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        sb.AppendLine($"message {name} {{");
        foreach (var field in message.Fields.OrderBy(f => f.Number))
        {
            string kind = enums.Contains(field.Type) ? "   // enumerado" : "";
            sb.AppendLine($"  {(field.Repeated ? "repeated " : "")}{field.Type} f{field.Number} = {field.Number};{kind}");
        }
        sb.AppendLine("}");
    }

    /// <summary>Saca el JSON de la respuesta aunque venga con adornos.</summary>
    public static Verdict? Read(string answer)
    {
        int open = answer.IndexOf('{');
        int close = answer.LastIndexOf('}');
        if (open < 0 || close <= open) return null;

        try { return JsonSerializer.Deserialize<Verdict>(answer[open..(close + 1)]); }
        catch (JsonException) { return null; }
    }
}

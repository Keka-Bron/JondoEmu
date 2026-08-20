namespace Jondo.Unity.Reversing;

/// <summary>
/// Una versión de mentira: el mismo protocolo con los nombres rotados.
///
/// Es la única forma de medir el emparejador ANTES de que llegue el parche. Se coge el protocolo
/// de ahora, se le barajan los nombres como haría Ankama, y se le pide al emparejador que
/// reconstruya la correspondencia. Como la respuesta correcta se conoce entera, sale un porcentaje
/// exacto de acierto y, mejor aún, la lista de los que falla.
///
/// Lo que NO simula: en un parche de verdad hay mensajes nuevos, mensajes que desaparecen y
/// campos que se añaden. Aquí sólo cambian los nombres, así que este número es el TECHO del
/// emparejador, no lo que va a dar el día del parche.
/// </summary>
public static class Shuffle
{
    /// <summary>Devuelve el protocolo con los nombres cambiados, y el diccionario de la verdad.</summary>
    public static (Matcher.Model Shuffled, Dictionary<string, string> Truth) Rotate(
        Matcher.Model model, int seed = 7)
    {
        var random = new Random(seed);

        var names = model.Messages.Select(m => m.Name)
            .Concat(model.Enums.Select(e => e.Name))
            .ToList();

        // Nombres nuevos de tres letras, del mismo estilo que los de Ankama y sin repetir.
        var pool = new List<string>();
        for (char a = 'a'; a <= 'z' && pool.Count < names.Count * 2; a++)
            for (char b = 'a'; b <= 'z' && pool.Count < names.Count * 2; b++)
                for (char c = 'a'; c <= 'z' && pool.Count < names.Count * 2; c++)
                    pool.Add($"{a}{b}{c}");

        // Barajado de Fisher-Yates, para que el nombre nuevo no guarde ninguna relación con el
        // viejo: si quedara alguna, el emparejador acertaría por el motivo equivocado.
        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        var truth = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < names.Count; i++) truth[names[i]] = pool[i];

        string Renamed(string type) => truth.TryGetValue(type, out string? now) ? now : type;

        var messages = model.Messages.Select(m => new ProtoWriter.Message(
            Renamed(m.Name),
            m.Fields.Select(f => new ProtoWriter.Field(f.Number, Renamed(f.Type), f.Name, f.Repeated)).ToList(),
            m.Doubtful)).ToList();

        var enums = model.Enums.Select(e => new ProtoWriter.Enumeration(
            Renamed(e.Name), e.Values)).ToList();

        return (new Matcher.Model(messages, enums), truth);
    }
}

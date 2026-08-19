using System.Security.Cryptography;
using System.Text;

namespace Jondo.Unity.ProtocolBuilder;

/// <summary>
/// Emparejar el protocolo de una versión con el de la siguiente, cuando los nombres han cambiado.
///
/// El problema, dicho corto: Ankama rota los nombres de tres letras en cada parche. El jsd de hoy
/// se llamará otra cosa mañana, y hay dos mil mensajes. Emparejar a mano es inviable.
///
/// ─── Por qué no basta con mirar cada mensaje ────────────────────────────────────────────
///
/// Un mensaje de { int64 f2 } es idéntico a otros trescientos. Comparando mensajes de uno en uno,
/// los grandes se emparejan solos y los pequeños son imposibles: es la conclusión a la que llega
/// el hilo de Cadernis, y es correcta MIENTRAS se miren sueltos.
///
/// Pero un mensaje no está suelto: es un nodo de un grafo. Lo que identifica a un { int64 } no es
/// su forma, es QUIÉN LE APUNTA. Si sólo aparece como campo 7 de un mensaje enorme que ya está
/// emparejado con certeza, queda determinado aunque por dentro no tenga nada distintivo.
///
/// ─── Cómo ───────────────────────────────────────────────────────────────────────────────
///
/// Refinamiento por rondas, que es lo que se hace para comparar grafos:
///
///   ronda 0   la huella de un mensaje son sus campos: número, tipo y si es lista. De los que
///             apuntan a otro mensaje sólo se anota que apuntan, no a quién.
///   ronda k   la huella pasa a ser la de la ronda anterior MÁS las huellas de aquellos a los que
///             apunta. Así la información de los vecinos se va propagando.
///
/// Al cabo de unas rondas, dos mensajes tienen la misma huella sólo si tienen la misma forma Y la
/// misma vecindad hasta esa distancia. Los que quedan solos con su huella en las dos versiones se
/// emparejan sin dudar.
///
/// Después se propaga por los campos: si a y b están emparejados y su campo 7 apunta a ta y a tb,
/// entonces ta y tb son el mismo mensaje. Eso arrastra a los pequeños, que es donde estaba el
/// problema.
/// </summary>
public static class Matcher
{
    public sealed record Model(List<ProtoWriter.Message> Messages, List<ProtoWriter.Enumeration> Enums);

    public sealed record Result(
        Dictionary<string, string> Pairs,
        List<string> Ambiguous,
        List<string> Alone);

    /// <summary>Empareja los mensajes de las dos versiones.</summary>
    public static Result Match(Model a, Model b, int rounds = 5)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        var takenB = new HashSet<string>(StringComparer.Ordinal);

        var signA = Signatures(a, rounds);
        var signB = Signatures(b, rounds);

        // ─── Las semillas: huella única a los dos lados ─────────────────────────────────
        //
        // Se va de la ronda más profunda a la más superficial: cuanta más vecindad lleva dentro
        // una huella, menos casualidad es que coincida.
        for (int round = rounds; round >= 0; round--)
        {
            var byA = Group(signA, round);
            var byB = Group(signB, round);

            foreach (var (fingerprint, ones) in byA)
            {
                if (ones.Count != 1) continue;
                if (!byB.TryGetValue(fingerprint, out var others) || others.Count != 1) continue;

                string from = ones[0], to = others[0];
                if (pairs.ContainsKey(from) || takenB.Contains(to)) continue;

                pairs[from] = to;
                takenB.Add(to);
            }
        }

        // ─── El riego: parecido, no igualdad ────────────────────────────────────────────
        //
        // Las semillas de arriba exigen que la huella coincida EXACTAMENTE, y eso sólo pasa cuando
        // el mensaje no ha cambiado nada entre versiones. Entre 3.6.4.3 y 3.6.10.10 la mitad han
        // cambiado —un campo nuevo aquí, un tipo cambiado allá— y con igualdad exacta se emparejó
        // un mísero 5%.
        //
        // Así que a partir de las semillas se riega: cada mensaje sin pareja se puntúa contra los
        // candidatos que se le parezcan, sumando dos cosas —cuánto se parecen por dentro y cuántos
        // de sus vecinos ya están emparejados entre sí— y se acepta el mejor si le saca ventaja al
        // segundo. Cada ronda produce parejas nuevas que mejoran la puntuación de la siguiente,
        // hasta que deja de moverse.
        var messagesA = a.Messages.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var messagesB = b.Messages.ToDictionary(m => m.Name, StringComparer.Ordinal);

        for (int round = 0; round < 12; round++)
        {
            // ─── Arrastre por los padres ────────────────────────────────────────────────
            //
            // Ésta es la parte que de verdad mueve la aguja, y la que faltaba. Si a y b son el
            // mismo mensaje y los dos tienen un campo 3 que apunta a otro mensaje, entonces esos
            // dos son el mismo mensaje también, se parezcan a lo que se parezcan por dentro.
            //
            // Sin esto, un mensaje de un solo campo es indistinguible de otros cuatrocientos: su
            // forma no dice nada y su vecindad tampoco, porque no apunta a nadie. Lo que lo
            // identifica es QUIÉN LE APUNTA, y eso sólo se sabe yendo de los padres a los hijos.
            // De ahí que la primera versión emparejara un 5%: miraba únicamente hacia abajo.
            bool dragged = true;
            while (dragged)
            {
                dragged = false;
                foreach (var (from, to) in pairs.ToList())
                {
                    if (!messagesA.TryGetValue(from, out var parentA)) continue;
                    if (!messagesB.TryGetValue(to, out var parentB)) continue;

                    foreach (var field in parentA.Fields)
                    {
                        var twin = parentB.Fields.FirstOrDefault(f => f.Number == field.Number);
                        if (twin == null) continue;
                        if (!messagesA.TryGetValue(field.Type, out var childA)) continue;
                        if (!messagesB.TryGetValue(twin.Type, out var childB)) continue;
                        if (pairs.ContainsKey(field.Type) || takenB.Contains(twin.Type)) continue;
                        if (field.Repeated != twin.Repeated) continue;

                        // Un mínimo de parecido, para que un campo que cambió de tipo entre
                        // versiones no arrastre a una pareja falsa y ésta a otra detrás.
                        if (Similar(childA, childB, messagesA, messagesB, pairs) < 0.3 &&
                            childA.Fields.Count != childB.Fields.Count) continue;

                        pairs[field.Type] = twin.Type;
                        takenB.Add(twin.Type);
                        dragged = true;
                    }
                }
            }

            var found = new List<(string From, string To, double Score)>();

            foreach (var one in a.Messages)
            {
                if (pairs.ContainsKey(one.Name)) continue;

                double best = 0, second = 0;
                string? winner = null;

                foreach (var other in b.Messages)
                {
                    if (takenB.Contains(other.Name)) continue;

                    double score = Similar(one, other, messagesA, messagesB, pairs);
                    if (score > best) { second = best; best = score; winner = other.Name; }
                    else if (score > second) second = score;
                }

                // Dos listones a la vez: parecerse bastante, y parecerse MÁS QUE NINGÚN OTRO. Sin
                // el segundo, los mensajes de un solo campo se emparejarían al azar entre ellos.
                if (winner != null && best >= 0.55 && best - second >= 0.08)
                {
                    found.Add((one.Name, winner, best));
                }
            }

            if (found.Count == 0) break;

            // Los mejores primero: si dos aspiran al mismo, se lo lleva el que más se parece.
            foreach (var (from, to, _) in found.OrderByDescending(f => f.Score))
            {
                if (pairs.ContainsKey(from) || takenB.Contains(to)) continue;
                pairs[from] = to;
                takenB.Add(to);
            }
        }

        var ambiguous = new List<string>();
        var alone = new List<string>();
        foreach (var message in a.Messages)
        {
            if (pairs.ContainsKey(message.Name)) continue;
            var mates = b.Messages.Where(m => signB[m.Name][0] == signA[message.Name][0]).ToList();
            (mates.Count > 0 ? ambiguous : alone).Add(message.Name);
        }

        return new Result(pairs, ambiguous, alone);
    }

    /// <summary>
    /// Cuánto se parecen dos mensajes, entre 0 y 1.
    ///
    /// Mitad y mitad:
    ///
    ///   la forma     qué campos tiene: número, clase y si es lista. Un campo que coincide en las
    ///                dos suma; los que sobran a un lado o al otro restan.
    ///   la vecindad  de los campos que apuntan a otro mensaje, cuántos apuntan a mensajes que ya
    ///                están emparejados ENTRE SÍ. Esto es lo que salva a los pequeños: un mensaje
    ///                de un solo campo no se distingue de otros trescientos por su forma, pero sí
    ///                por quién le apunta.
    ///
    /// Cuando ninguno de los dos apunta a nadie, la vecindad no dice nada y se puntúa sólo por la
    /// forma; por eso las hojas del grafo son las que se quedan ambiguas, y es inevitable.
    /// </summary>
    private static double Similar(ProtoWriter.Message one, ProtoWriter.Message other,
                                  Dictionary<string, ProtoWriter.Message> messagesA,
                                  Dictionary<string, ProtoWriter.Message> messagesB,
                                  Dictionary<string, string> pairs)
    {
        if (one.Fields.Count == 0 && other.Fields.Count == 0) return 0;   // vacíos: nada que decir

        int shared = 0;
        int neighbours = 0, agree = 0;

        foreach (var field in one.Fields)
        {
            var twin = other.Fields.FirstOrDefault(f => f.Number == field.Number);
            if (twin == null) continue;

            bool oneIsMessage = messagesA.ContainsKey(field.Type);
            bool otherIsMessage = messagesB.ContainsKey(twin.Type);

            // Mismo número y misma clase de contenido.
            if (oneIsMessage != otherIsMessage) continue;
            if (!oneIsMessage && field.Type != twin.Type) continue;
            if (field.Repeated != twin.Repeated) continue;
            shared++;

            if (!oneIsMessage) continue;
            neighbours++;
            if (pairs.TryGetValue(field.Type, out string? already) && already == twin.Type) agree++;
        }

        double shape = 2.0 * shared / (one.Fields.Count + other.Fields.Count);
        if (neighbours == 0) return shape;

        double neighbourhood = (double)agree / neighbours;
        return 0.5 * shape + 0.5 * neighbourhood;
    }

    /// <summary>La huella de cada mensaje en cada ronda.</summary>
    private static Dictionary<string, string[]> Signatures(Model model, int rounds)
    {
        var messages = model.Messages.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var enums = model.Enums.ToDictionary(e => e.Name, StringComparer.Ordinal);
        var signatures = new Dictionary<string, string[]>(StringComparer.Ordinal);

        // Ronda 0: sólo lo propio. De un campo que apunta a otro mensaje se anota que apunta, no a
        // quién: el nombre está rotado y no dice nada.
        foreach (var message in model.Messages)
        {
            var parts = message.Fields
                .OrderBy(f => f.Number)
                .Select(f => $"{f.Number}:{Kind(f, messages, enums)}{(f.Repeated ? "+" : "")}");
            signatures[message.Name] = new string[rounds + 1];
            signatures[message.Name][0] = Hash(string.Join(",", parts));
        }

        // Un enumerado no cambia de forma entre versiones —sus valores son los mismos— así que su
        // huella se puede calcular una vez y sirve de ancla para quien lo use.
        var enumSignature = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in model.Enums)
        {
            enumSignature[e.Name] = Hash("E" + string.Join(",", e.Values.Select(v => v.Value).OrderBy(v => v)));
        }

        for (int round = 1; round <= rounds; round++)
        {
            foreach (var message in model.Messages)
            {
                var neighbours = message.Fields
                    .OrderBy(f => f.Number)
                    .Select(f =>
                    {
                        if (messages.ContainsKey(f.Type))
                            return $"{f.Number}>{signatures[f.Type][round - 1]}";
                        if (enumSignature.TryGetValue(f.Type, out string? e))
                            return $"{f.Number}={e}";
                        return $"{f.Number}.{f.Type}";
                    });

                signatures[message.Name][round] =
                    Hash(signatures[message.Name][round - 1] + "|" + string.Join(",", neighbours));
            }
        }

        return signatures;
    }

    /// <summary>De qué clase es un campo, sin mirar nombres rotados.</summary>
    private static string Kind(ProtoWriter.Field field,
                               Dictionary<string, ProtoWriter.Message> messages,
                               Dictionary<string, ProtoWriter.Enumeration> enums)
    {
        if (messages.ContainsKey(field.Type)) return "M";
        if (enums.ContainsKey(field.Type)) return "E";
        return field.Type;      // los tipos de siempre —int64, string— no cambian de nombre
    }

    private static Dictionary<string, List<string>> Group(Dictionary<string, string[]> signatures, int round)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, rounds) in signatures)
        {
            if (!groups.TryGetValue(rounds[round], out var list))
            {
                list = new List<string>();
                groups[rounds[round]] = list;
            }
            list.Add(name);
        }
        return groups;
    }

    private static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}

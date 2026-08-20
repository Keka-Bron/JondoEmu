namespace Jondo.Unity.Reversing;

/// <summary>
/// El mapeo recorrido parche a parche en vez de un salto largo.
///
/// El salto directo de 3.6.4.3 a 3.6.10.10 sale al 11,3%. El techo del emparejador, medido contra
/// sí mismo con los nombres barajados, es el 68,3%. La distancia entre los dos números son los seis
/// parches que hay en medio: cada uno mueve un poco la forma y seis movimientos encadenados borran
/// la señal. Con los clientes intermedios el salto se parte en saltos de uno.
///
/// ─── Esto es una hipótesis, y por eso mide ──────────────────────────────────────────────
///
/// Encadenar puede salir mal, y conviene decirlo antes de mirar el resultado. Si en cada salto se
/// pierde una tercera parte y las pérdidas fueran independientes, siete saltos dejarían un 6% —
/// peor que el salto directo. Sale bien sólo si es siempre el mismo grupo el que se pierde: los
/// mensajes pequeños y sin vecindad, que no se emparejan en ningún salto. El número dirá cuál de
/// las dos cosas pasa; no lo damos por sabido.
///
/// ─── Lo que se puede comprobar de verdad ────────────────────────────────────────────────
///
/// Hasta ahora todo lo medido era sintético: barajar los nombres de una versión y ver cuántos se
/// recolocan. Aquí hay algo mejor. El emparejador no mira los nombres en ningún momento —sólo
/// números de campo, clases de campo y vecindad; los nombres son la clave del diccionario y nada
/// más—, así que un mensaje que se llama igual en las dos versiones es una respuesta conocida que
/// el emparejador no puede haber copiado.
///
/// De ahí salen los tres números que importan de cada salto: de los que conservan el nombre,
/// cuántos acierta, cuántos falla y de cuántos no se atreve. Fallar es lo grave —un emparejamiento
/// equivocado envenena la cadena entera y nadie se entera—; callarse sólo cuesta cobertura.
/// </summary>
public sealed class Relay
{
    /// <summary>Lo que pasa en un salto de una versión a la siguiente.</summary>
    /// <param name="Rotated">
    /// Si Ankama ha vuelto a repartir los nombres en este parche. Se sabe porque deja de estar
    /// TODO el juego de nombres viejo: mientras no rota, los 2.169 nombres siguen ahí uno a uno.
    /// </param>
    /// <param name="SameName">Cuántos nombres del viejo siguen existiendo en el nuevo.</param>
    /// <param name="SameShape">Cuántas formas del viejo siguen existiendo en el nuevo.</param>
    /// <param name="Right">Sin rotación: a cuántos los empareja consigo mismos.</param>
    /// <param name="Wrong">Sin rotación: a cuántos los empareja con otro. Cada uno es veneno.</param>
    /// <param name="Unsure">Sin rotación: de cuántos no se atreve a decir nada.</param>
    public sealed record Hop(
        string From, string To,
        int OldCount, int NewCount,
        int Paired, int Doubtful, int Gone,
        bool Rotated, int SameName, int SameShape, int Seeds,
        int Right, int Wrong, int Unsure);

    /// <summary>El resultado de recorrer la cadena entera.</summary>
    /// <param name="Chain">Del nombre en la primera versión al nombre en la última.</param>
    /// <param name="Died">Del nombre en la primera versión al salto donde se perdió.</param>
    public sealed record Outcome(
        List<Hop> Hops,
        Dictionary<string, string> Chain,
        Dictionary<string, string> Died);

    /// <summary>
    /// Recorre la cadena. Las carpetas tienen que venir en orden, de la vieja a la nueva.
    ///
    /// Cada cliente se abre una sola vez aunque participe en dos saltos: reconstruir el ensamblado
    /// es medio minuto y con ocho versiones eso son cuatro minutos que no hay por qué gastar dos
    /// veces.
    /// </summary>
    public Outcome Run(IReadOnlyList<string> clients, Action<string> report)
    {
        if (clients.Count < 2) throw new ArgumentException("una cadena necesita al menos dos versiones");

        var models = new Matcher.Model[clients.Count];
        for (int i = 0; i < clients.Count; i++)
        {
            string dll = Dumper.Protocol(clients[i], report);
            models[i] = ProtoWriter.Model(dll);
            report($"  {Name(clients[i])}: {models[i].Messages.Count:N0} mensajes");
        }

        var hops = new List<Hop>();

        // La cadena arranca como la identidad sobre la primera versión: al principio cada mensaje
        // es él mismo, y cada salto la mueve un eslabón o la corta.
        var chain = models[0].Messages.ToDictionary(m => m.Name, m => m.Name, StringComparer.Ordinal);
        var died = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int i = 0; i + 1 < clients.Count; i++)
        {
            string from = Name(clients[i]), to = Name(clients[i + 1]);
            report($"  {from} → {to}: emparejando…");

            var result = Matcher.Match(models[i], models[i + 1]);

            var newNames = models[i + 1].Messages.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
            int sameName = models[i].Messages.Count(m => newNames.Contains(m.Name));

            var oldShapes = Matcher.Shapes(models[i]);
            var newShapes = Matcher.Shapes(models[i + 1]);
            var newSet = newShapes.Values.ToHashSet(StringComparer.Ordinal);
            int sameShape = oldShapes.Values.Count(s => newSet.Contains(s));

            // Las formas ÚNICAS a los dos lados: las semillas del emparejador.
            //
            // Contar formas a secas engaña. La mitad del protocolo son mensajes de un campo, y
            // «2:int64» va a existir en cualquier versión por casualidad; esas formas sobreviven
            // siempre y no sirven para nada. Lo que siembra el emparejamiento es una forma que
            // señale a un solo mensaje en cada versión, y ésas son las que hay que contar.
            var oldOnce = Once(oldShapes);
            var newOnce = Once(newShapes);
            int seeds = oldOnce.Count(s => newOnce.Contains(s));

            // Mientras Ankama no vuelve a repartir los nombres, están TODOS: los 2.169 del viejo
            // siguen en el nuevo. En cuanto rota deja de estarlo, y ése es el aviso.
            bool rotated = sameName < models[i].Messages.Count;

            // Sin rotación hay respuesta conocida y el emparejador no ha podido copiarla, porque no
            // mira los nombres en ningún momento.
            //
            // Con rotación NO la hay, y ésta fue mi equivocación en la primera medida: conté como
            // fallo cada vez que un nombre viejo apuntaba a otro mensaje en el nuevo. Pero después
            // de una rotación eso es justo lo que TIENE que pasar —el nombre se lo ha quedado otro—,
            // así que aquello no medía aciertos del emparejador sino mi propia suposición. Con
            // rotación no se puntúa nada: no hay contra qué.
            int right = 0, wrong = 0, unsure = 0;
            if (!rotated)
            {
                foreach (var message in models[i].Messages)
                {
                    if (!result.Pairs.TryGetValue(message.Name, out string? twin)) unsure++;
                    else if (twin == message.Name) right++;
                    else wrong++;
                }
            }

            hops.Add(new Hop(
                from, to,
                models[i].Messages.Count, models[i + 1].Messages.Count,
                result.Pairs.Count, result.Candidates.Count, result.Alone.Count,
                rotated, sameName, sameShape, seeds,
                right, wrong, unsure));

            // Sólo viaja la certeza. Arrastrar una duda sería multiplicar la duda por la del salto
            // siguiente, y al final de la cadena nadie sabría de qué se fía.
            foreach (string start in chain.Keys.ToList())
            {
                if (result.Pairs.TryGetValue(chain[start], out string? next)) chain[start] = next;
                else
                {
                    died[start] = $"{from} → {to}";
                    chain.Remove(start);
                }
            }
        }

        return new Outcome(hops, chain, died);
    }

    /// <summary>El salto directo, sin escalas, para tener con qué comparar.</summary>
    public static Dictionary<string, string> Direct(string oldClient, string newClient, Action<string> report)
    {
        var a = ProtoWriter.Model(Dumper.Protocol(oldClient, report));
        var b = ProtoWriter.Model(Dumper.Protocol(newClient, report));
        return Matcher.Match(a, b).Pairs;
    }

    /// <summary>Las formas que sólo tiene un mensaje en toda la versión.</summary>
    private static HashSet<string> Once(Dictionary<string, string> shapes)
    {
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string shape in shapes.Values) count[shape] = count.GetValueOrDefault(shape) + 1;
        return count.Where(p => p.Value == 1).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
    }

    private static string Name(string clientFolder)
        => Mapper.VersionOf(Path.GetFileName(clientFolder.TrimEnd(Path.DirectorySeparatorChar)));
}

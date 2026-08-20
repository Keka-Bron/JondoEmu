using System.Text;

namespace Jondo.Unity.Reversing;

/// <summary>
/// El mapeo de una versión a la siguiente: dos protocolos entran, una tabla sale.
///
/// Es lo único que hace falta el día del parche. Le das el cliente que ya conocías y el que acaba
/// de salir, y te dice qué mensaje es ahora cada uno de los que sabías, con lo que sabías de él.
///
/// ─── Quién hace qué, y en qué orden ─────────────────────────────────────────────────────
///
///   1. la estructura   compara las dos versiones por la forma de cada mensaje y por quién apunta
///                      a quién. Resuelve el grueso y NO se equivoca: medido sobre 3.6.10.10 con
///                      los nombres barajados, 1.481 aciertos y CERO fallos. Lo que no tiene claro
///                      lo deja en duda en vez de adivinar.
///   2. el significado  viaja solo. Si de `jsd` se sabía que saca un actor del mapa y la estructura
///                      dice que ahora es `xyz`, entonces `xyz` saca un actor del mapa. No hay que
///                      volver a averiguar nada.
///   3. el modelo       sólo para las dudas, y sólo con la lista corta de candidatos delante. Un
///                      mensaje ambiguo no es un misterio: es elegir entre tres o cinco que tienen
///                      la misma forma. Ahí un modelo aporta lo que la estructura no ve —el
///                      significado del viejo y las pistas del código del nuevo—; con dos mil
///                      candidatos delante no aportaría más que ruido con formato.
///
/// Lo que ni así se resuelve sale marcado como «a mano». Nunca inventado.
/// </summary>
public sealed class Mapper
{
    /// <summary>De dónde ha salido cada pareja, que es lo que dice si uno se puede fiar.</summary>
    public enum How
    {
        /// <summary>La estructura lo resolvió sola. Es la buena.</summary>
        Structure,

        /// <summary>Había varios candidatos y el modelo eligió.</summary>
        Model,

        /// <summary>Hay candidatos y nadie ha elegido todavía.</summary>
        Doubt,

        /// <summary>Ni siquiera hay candidatos: o es nuevo, o lo han retirado.</summary>
        Gone,
    }

    /// <summary>Una línea del mapeo.</summary>
    public sealed class Row
    {
        public required string Old { get; init; }
        public string New { get; set; } = "";
        public How How { get; set; }

        /// <summary>Lo que se sabía del viejo, y que ahora vale para el nuevo.</summary>
        public string Meaning { get; set; } = "";

        /// <summary>El nombre que se le había puesto al viejo, si lo tenía.</summary>
        public string Name { get; set; } = "";

        /// <summary>Lo usa el emulador: de éstos depende que arranque tras el parche.</summary>
        public bool Mine { get; set; }

        /// <summary>Cuando hay duda, entre quiénes.</summary>
        public List<string> Candidates { get; } = new();

        /// <summary>Si lo eligió el modelo, por qué.</summary>
        public string Because { get; set; } = "";
    }

    public List<Row> Rows { get; } = new();
    public string OldVersion { get; private set; } = "";
    public string NewVersion { get; private set; } = "";

    private Matcher.Model? _old;
    private Matcher.Model? _new;
    private Dictionary<string, Dossier.Anchor> _anchors = new(StringComparer.Ordinal);
    private Dictionary<string, CodeIndex.Evidence> _index = new(StringComparer.Ordinal);
    private Dictionary<string, List<string>> _newParents = new(StringComparer.Ordinal);

    /// <summary>
    /// El mapeo entero, salvo las dudas.
    ///
    /// Tarda unos segundos: lo que cuesta es abrir los dos ensamblados, y el emparejamiento en sí
    /// son dos segundos y medio para dos mil mensajes.
    /// </summary>
    public void Build(string oldPath, string newPath, string dataFolder,
                      IReadOnlyCollection<string> mine, Action<string>? report = null)
    {
        // La versión sale de lo que escribió el usuario —«Cliente 3.6.10.10»— y no de la ruta del
        // ensamblado ya resuelta, que acaba en «Ankama.Dofus.Protocol.Game.dll» y no lleva ninguna
        // versión dentro. Con la resuelta se buscaban las anclas de una versión llamada
        // «Ankama.Dofus.Protocol.Game», no se encontraban, y el mapeo salía sin significados y sin
        // una sola duda que preguntar.
        OldVersion = VersionOf(oldPath);
        NewVersion = VersionOf(newPath);

        report?.Invoke("leyendo el protocolo antiguo...");
        _old = ProtoWriter.Model(ProtocolDll(oldPath));

        report?.Invoke("leyendo el protocolo nuevo...");
        _new = ProtoWriter.Model(ProtocolDll(newPath));
        _newParents = Dossier.Parents(_new);

        // Las anclas son del VIEJO: es de él de quien se sabe algo. Lo que hace este programa es
        // llevar ese conocimiento al nuevo.
        _anchors = Dossier.Anchors(Path.Combine(dataFolder, $"anclas_{OldVersion}.tsv"));
        string index = Path.Combine(dataFolder, $"indice_{NewVersion}.json");
        _index = File.Exists(index) ? CodeIndex.Load(index) : new(StringComparer.Ordinal);

        // ─── Lo primero: ¿ha rotado Ankama los nombres en este parche? ──────────────────
        //
        // Medido sobre ocho versiones seguidas (§2.6 de la documentación): en 3 de los 7 parches NO
        // rota. Los 2.169 nombres siguen ahí uno a uno y el mapeo es la identidad. Sin esta
        // comprobación se emparejaba igualmente y salía el 71%, dejando seiscientas dudas que no
        // eran dudas de nada.
        //
        // Se comprueba por el juego entero de nombres y no por unos cuantos: que cien nombres
        // sobrevivan no dice nada —después de rotar siguen existiendo mil trescientos, en manos de
        // otros mensajes—. Lo que sólo pasa cuando no ha rotado es que estén TODOS.
        var newShapes = Matcher.Shapes(_new);
        bool rotated = _old.Messages.Any(m => !newShapes.ContainsKey(m.Name));

        report?.Invoke(rotated
            ? $"{_old.Messages.Count:N0} mensajes antiguos, {_new.Messages.Count:N0} nuevos. " +
              "los nombres han rotado; emparejando..."
            : $"{_old.Messages.Count:N0} mensajes antiguos, {_new.Messages.Count:N0} nuevos. " +
              "los nombres NO han rotado en este parche; comprobando...");

        var result = Matcher.Match(_old, _new);

        // La identidad no se da por buena sólo porque el nombre siga ahí: se exige además que el
        // mensaje tenga la misma forma. Un nombre que sobrevive con otro contenido detrás sería
        // justo la clase de pareja falsa que envenena todo lo que venga después, y sale barato
        // negarse a darla.
        var oldShapes = rotated ? null : Matcher.Shapes(_old);
        int quarrel = 0;

        Rows.Clear();
        foreach (var message in _old.Messages.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var row = new Row { Old = message.Name, Mine = mine.Contains(message.Name) };

            if (_anchors.TryGetValue(message.Name, out var anchor))
            {
                row.Name = anchor.Name;
                row.Meaning = anchor.Meaning;
            }

            if (oldShapes is not null && oldShapes[message.Name] == newShapes[message.Name])
            {
                if (result.Pairs.TryGetValue(message.Name, out string? said) && said != message.Name) quarrel++;
                row.New = message.Name;
                row.How = How.Structure;
                row.Because = "el parche no rotó los nombres";
            }
            else if (result.Pairs.TryGetValue(message.Name, out string? twin))
            {
                row.New = twin;
                row.How = How.Structure;
            }
            else if (result.Candidates.TryGetValue(message.Name, out var candidates))
            {
                row.Candidates.AddRange(candidates);
                row.How = How.Doubt;
            }
            else
            {
                row.How = How.Gone;
            }

            Rows.Add(row);
        }

        report?.Invoke(Tally());
    }

    /// <summary>
    /// Las dudas por las que merece la pena preguntar.
    ///
    /// Sólo las que sabemos qué son. Preguntar por un mensaje viejo del que tampoco sabíamos nada
    /// es pedirle al modelo que elija entre cinco desconocidos sin ninguna pista: contestaría
    /// igual, y no habría forma de saber si acierta.
    /// </summary>
    public IReadOnlyList<Row> Doubts(bool onlyMine = false)
        => Rows.Where(r => r.How == How.Doubt && r.Meaning.Length > 0 && (!onlyMine || r.Mine))
               .OrderByDescending(r => r.Mine)
               .ThenBy(r => r.Candidates.Count)
               .ToList();

    // ─── El desempate ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Le pasa las dudas al modelo, una a una, y se queda con lo que elija.
    ///
    /// Cada pregunta es pequeña y cerrada: un mensaje viejo del que se sabe qué hace, y de tres a
    /// cinco candidatos del cliente nuevo con su forma. Eso es lo que un modelo puede resolver
    /// bien. Si contesta algo que no está entre los candidatos, se descarta sin más: no está para
    /// inventar nombres, está para elegir uno de los que se le dan.
    /// </summary>
    public async Task ResolveAsync(Llm llm, IReadOnlyList<Row> doubts, Action<string> report,
                                   CancellationToken cancel = default)
    {
        string system = TieBreak.System();
        int asked = 0, chosen = 0;

        foreach (var row in doubts)
        {
            cancel.ThrowIfCancellationRequested();
            if (_old == null || _new == null) return;

            string question = TieBreak.Question(row, _old, _new, _newParents, _index, OldVersion, NewVersion);

            string answer;
            try { answer = await llm.AskAsync(question, system, cancel); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { report($"{row.Old}: {e.Message}"); continue; }

            asked++;
            var verdict = TieBreak.Read(answer);

            // La red de seguridad: si lo que dice no está entre los candidatos, no vale. Es la
            // diferencia entre elegir y alucinar.
            if (verdict?.Chosen is not { Length: > 0 } || !row.Candidates.Contains(verdict.Chosen))
            {
                report($"{row.Old}: sin elegir");
                continue;
            }

            row.New = verdict.Chosen;
            row.How = How.Model;
            row.Because = (verdict.Because ?? "").Replace('\t', ' ').Replace('\n', ' ');
            chosen++;
            report($"{row.Old} → {row.New}   ({(row.Name.Length > 0 ? row.Name : row.Meaning)})");
        }

        report($"{asked:N0} preguntadas, {chosen:N0} resueltas");
    }

    // ─── Lo que se lleva uno ────────────────────────────────────────────────────────────

    /// <summary>La tabla, para leerla y para meterla en el repositorio.</summary>
    public string Export(string dataFolder)
    {
        string path = Path.Combine(dataFolder, $"mapeo_{OldVersion}_a_{NewVersion}.tsv");
        var lines = new List<string>
        {
            $"# Mapeo de {OldVersion} a {NewVersion}.",
            "#",
            "# origen: estructura = lo resolvió el emparejador y no se equivoca;",
            "#         modelo     = había varios candidatos con la misma forma y eligió un LLM;",
            "#         duda       = hay candidatos y nadie ha elegido. NO usar sin mirarlo.",
            "#         retirado   = ni un solo candidato: o es nuevo, o ya no está.",
            "#",
            "# viejo\tnuevo\torigen\tnombre\tqué hace\tlo usa el emulador",
        };

        foreach (var row in Rows.Where(r => r.New.Length > 0 || r.How == How.Doubt)
                                .OrderBy(r => r.Old, StringComparer.Ordinal))
        {
            lines.Add(string.Join('\t', row.Old, row.New, Word(row.How), row.Name,
                                  row.Meaning.Replace('\t', ' '), row.Mine ? "sí" : ""));
        }

        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>
    /// El mismo mapeo en el formato del sniffer de tikkamasala, que lo recarga en caliente.
    ///
    /// Con esto se puede jugar con el sniffer delante y ver los nombres de verdad pasar por el
    /// cable en vez de tres letras. Es la única verificación que existe contra el juego real, y
    /// sale gratis: es el mismo dato escrito de otra manera.
    /// </summary>
    public string ExportSniffer(string dataFolder)
    {
        string path = Path.Combine(dataFolder, $"mapping_{NewVersion}.json");
        var sb = new StringBuilder();
        sb.AppendLine("{");

        var named = Rows.Where(r => r.New.Length > 0 && r.Name.Length > 0)
                        .OrderBy(r => r.New, StringComparer.Ordinal)
                        .ToList();

        for (int i = 0; i < named.Count; i++)
        {
            string comma = i < named.Count - 1 ? "," : "";
            sb.AppendLine($"    \"type.ankama.com/{named[i].New}\": \"{named[i].Name}\"{comma}");
        }

        sb.AppendLine("}");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    // ─── Las cuentas ────────────────────────────────────────────────────────────────────

    public string Tally()
    {
        int mine = Rows.Count(r => r.Mine);
        int mineDone = Rows.Count(r => r.Mine && r.New.Length > 0);
        int done = Rows.Count(r => r.New.Length > 0);
        int doubt = Rows.Count(r => r.How == How.Doubt);
        int byModel = Rows.Count(r => r.How == How.Model);

        string tally = mine > 0
            ? $"de los {mine:N0} que usa el emulador: {mineDone:N0} mapeados   ·   "
            : "";

        tally += $"{done:N0} de {Rows.Count:N0} en total";
        if (byModel > 0) tally += $" ({byModel:N0} los eligió el modelo)";
        if (doubt > 0) tally += $"   ·   {doubt:N0} en duda";
        return tally;
    }

    private static string Word(How how) => how switch
    {
        How.Structure => "estructura",
        How.Model => "modelo",
        How.Doubt => "duda",
        _ => "retirado",
    };

    /// <summary>
    /// La versión que dice la ruta: vale la carpeta del cliente o el propio ensamblado.
    ///
    /// Se mira el ÚLTIMO tramo de la ruta antes que la ruta entera, y por un motivo que costó un
    /// rato ver: los clientes de la cadena viven en <c>C:\Jondo 3.6.10.10\clientes\Cliente 3.6.9.9</c>,
    /// y buscando en toda la ruta el primero que aparece es el 3.6.10.10 del nombre de la carpeta
    /// madre. Con eso los ocho clientes decían llamarse igual, se buscaban las anclas de una versión
    /// equivocada y los ocho mapeos se escribían encima del mismo fichero.
    /// </summary>
    public static string VersionOf(string path)
    {
        string tail = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        foreach (string where in new[] { tail, path })
        {
            var match = System.Text.RegularExpressions.Regex.Match(where, @"\d+(\.\d+){2,}");
            if (match.Success) return match.Value;
        }
        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>El ensamblado del protocolo, se le dé la carpeta del cliente o el fichero.</summary>
    public static string ProtocolDll(string path)
    {
        if (File.Exists(path)) return path;
        return Path.Combine(path, "MelonLoader", "Dependencies", "Il2CppAssemblyGenerator",
                            "Cpp2IL", "cpp2il_out", "Ankama.Dofus.Protocol.Game.dll");
    }
}

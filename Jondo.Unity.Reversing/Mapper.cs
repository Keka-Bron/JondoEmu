using System.Text;

namespace Jondo.Unity.Reversing;

/// <summary>
/// The mapping from one version to the next: two protocols go in, one table comes out.
///
/// It is the only thing needed on patch day. You give it the client you already knew and the one just
/// released, and it tells you which message each of the ones you knew now is, with what you knew about it.
///
/// ─── Who does what, and in what order ───────────────────────────────────────────────────
///
///   1. the structure   compares the two versions by each message's shape and by who points
///                      to whom. It solves the bulk and does NOT get it wrong: measured on 3.6.10.10 with
///                      the names shuffled, 1,481 hits and ZERO misses. What it is not sure of
///                      it leaves in doubt instead of guessing.
///   2. the meaning     travels on its own. If it was known of `jsd` that it removes an actor from the map and the structure
///                      says it is now `xyz`, then `xyz` removes an actor from the map. Nothing has to be
///                      found out again.
///   3. the model       only for the doubts, and only with the short list of candidates in front. An
///                      ambiguous message is not a mystery: it is choosing among three or five that have
///                      the same shape. There a model contributes what the structure does not see —the
///                      old one's meaning and the new one's code clues—; with two thousand
///                      candidates in front it would contribute nothing but formatted noise.
///
/// What not even that resolves comes out marked as «by hand». Never invented.
/// </summary>
public sealed class Mapper
{
    /// <summary>Where each pair came from, which is what says whether one can trust it.</summary>
    public enum How
    {
        /// <summary>The structure resolved it on its own. It is the good one.</summary>
        Structure,

        /// <summary>There were several candidates and the model chose.</summary>
        Model,

        /// <summary>There are candidates and nobody has chosen yet.</summary>
        Doubt,

        /// <summary>There are not even candidates: either it is new, or it has been withdrawn.</summary>
        Gone,
    }

    /// <summary>A line of the mapping.</summary>
    public sealed class Row
    {
        public required string Old { get; init; }
        public string New { get; set; } = "";
        public How How { get; set; }

        /// <summary>What was known about the old one, and which now holds for the new one.</summary>
        public string Meaning { get; set; } = "";

        /// <summary>The name the old one had been given, if it had one.</summary>
        public string Name { get; set; } = "";

        /// <summary>The emulator uses it: whether it starts after the patch depends on these.</summary>
        public bool Mine { get; set; }

        /// <summary>When there is doubt, among whom.</summary>
        public List<string> Candidates { get; } = new();

        /// <summary>If the model chose it, why.</summary>
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
    /// The whole mapping, except the doubts.
    ///
    /// It takes a few seconds: what costs is opening the two assemblies, and the matching itself
    /// is two and a half seconds for two thousand messages.
    /// </summary>
    public void Build(string oldPath, string newPath, string dataFolder,
                      IReadOnlyCollection<string> mine, Action<string>? report = null,
                      CancellationToken cancellationToken = default)
    {
        // The version comes from what the user wrote —«Cliente 3.6.10.10»— and not from the
        // already resolved assembly path, which ends in «Ankama.Dofus.Protocol.Game.dll» and carries no
        // version inside. With the resolved one the anchors of a version called
        // «Ankama.Dofus.Protocol.Game» were looked for, they were not found, and the mapping came out without meanings and without
        // a single doubt to ask.
        OldVersion = VersionOf(oldPath);
        NewVersion = VersionOf(newPath);

        report?.Invoke("reading the old protocol...");
        cancellationToken.ThrowIfCancellationRequested();
        _old = ProtoWriter.Model(ProtocolDll(oldPath));

        report?.Invoke("reading the new protocol...");
        cancellationToken.ThrowIfCancellationRequested();
        _new = ProtoWriter.Model(ProtocolDll(newPath));
        _newParents = Dossier.Parents(_new);

        // The anchors are the OLD one's: it is the one something is known about. What this program does is
        // carry that knowledge to the new one.
        _anchors = Dossier.Anchors(Path.Combine(dataFolder, $"anclas_{OldVersion}.tsv"));
        string index = Path.Combine(dataFolder, $"indice_{NewVersion}.json");
        _index = File.Exists(index) ? CodeIndex.Load(index) : new(StringComparer.Ordinal);

        // ─── First of all: has Ankama rotated the names in this patch? ──────────────────
        //
        // Measured over eight versions in a row (§2.6 of the documentation): in 3 of the 7 patches it does NOT
        // rotate. The 2,169 names are still there one by one and the mapping is the identity. Without this
        // check it was matched all the same and came out at 71%, leaving six hundred doubts that
        // were doubts about nothing.
        //
        // It is checked by the whole set of names and not by a few: that a hundred names
        // survive says nothing —after rotating thirteen hundred still exist, in the hands of
        // other messages—. What only happens when it has not rotated is that they are ALL there.
        var newShapes = Matcher.Shapes(_new);
        bool rotated = _old.Messages.Any(m => !newShapes.ContainsKey(m.Name));

        report?.Invoke(rotated
            ? $"{_old.Messages.Count:N0} old messages, {_new.Messages.Count:N0} new. " +
              "the names have rotated; matching..."
            : $"{_old.Messages.Count:N0} old messages, {_new.Messages.Count:N0} new. " +
              "the names have NOT rotated in this patch; checking...");

        var result = Matcher.Match(_old, _new, cancellationToken: cancellationToken);

        // The identity is not taken as good just because the name is still there: it is also required that the
        // message has the same shape. A name surviving with other content behind it would be
        // exactly the kind of false pair that poisons everything that comes after, and it is cheap
        // to refuse to give it.
        var oldShapes = rotated ? null : Matcher.Shapes(_old);
        int quarrel = 0;

        Rows.Clear();
        foreach (var message in _old.Messages.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                row.Because = "the patch did not rotate the names";
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
    /// The doubts worth asking about.
    ///
    /// Only the ones we know what they are. Asking about an old message we did not know anything about either
    /// is asking the model to choose among five unknowns without any clue: it would answer
    /// all the same, and there would be no way of knowing whether it gets it right.
    /// </summary>
    public IReadOnlyList<Row> Doubts(bool onlyMine = false)
        => Rows.Where(r => r.How == How.Doubt && r.Meaning.Length > 0 && (!onlyMine || r.Mine))
               .OrderByDescending(r => r.Mine)
               .ThenBy(r => r.Candidates.Count)
               .ToList();

    // ─── The tie-break ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Passes the doubts to the model, one by one, and keeps what it chooses.
    ///
    /// Each question is small and closed: an old message whose function is known, and three to
    /// five candidates of the new client with their shape. That is what a model can solve
    /// well. If it answers something not among the candidates, it is simply discarded: it is not there to
    /// invent names, it is there to choose one of those it is given.
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

            // The safety net: if what it says is not among the candidates, it does not count. It is the
            // difference between choosing and hallucinating.
            if (verdict?.Chosen is not { Length: > 0 } || !row.Candidates.Contains(verdict.Chosen))
            {
                report($"{row.Old}: not picked");
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

    // ─── What one takes away ────────────────────────────────────────────────────────────

    /// <summary>The table, to read it and to put it in the repository.</summary>
    public string Export(string dataFolder)
    {
        string path = Path.Combine(dataFolder, $"mapeo_{OldVersion}_a_{NewVersion}.tsv");
        var lines = new List<string>
        {
            $"# Mapping from {OldVersion} to {NewVersion}.",
            "#",
            "# origen: estructura = the matcher resolved it, and it does not get it wrong;",
            "#         modelo     = there were several candidates with the same shape and an LLM picked one;",
            "#         duda       = there are candidates and nobody has picked. Do NOT use without looking at it.",
            "#         retirado   = not a single candidate: either it is new, or it is gone.",
            "#",
            "# old\tnew\torigen\tname\twhat it does\tused by the emulator",
        };

        foreach (var row in Rows.Where(r => r.New.Length > 0 || r.How == How.Doubt)
                                .OrderBy(r => r.Old, StringComparer.Ordinal))
        {
            lines.Add(string.Join('\t', row.Old, row.New, Word(row.How), row.Name,
                                  row.Meaning.Replace('\t', ' '), row.Mine ? "sí" : ""));
        }

        WriteExport(path, writer => { foreach (string line in lines) writer.WriteLine(line); });
        return path;
    }

    /// <summary>
    /// The same mapping in the format of tikkamasala's sniffer, which reloads it hot.
    ///
    /// With this one can play with the sniffer in front and see the real names go by on the
    /// wire instead of three letters. It is the only verification there is against the real game, and
    /// it comes free: it is the same data written another way.
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
        WriteExport(path, writer => writer.Write(sb.ToString()));
        return path;
    }

    /// <summary>Readers see either the previous export or the complete replacement.</summary>
    internal static void WriteExport(string path, Action<TextWriter> write)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var writer = new StreamWriter(new FileStream(temporary, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None), new UTF8Encoding(false)))
                write(writer);
            // The temporary is alongside the destination so replacement stays on one filesystem.
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    // ─── The figures ────────────────────────────────────────────────────────────────────

    public string Tally()
    {
        int mine = Rows.Count(r => r.Mine);
        int mineDone = Rows.Count(r => r.Mine && r.New.Length > 0);
        int done = Rows.Count(r => r.New.Length > 0);
        int doubt = Rows.Count(r => r.How == How.Doubt);
        int byModel = Rows.Count(r => r.How == How.Model);

        string tally = mine > 0
            ? $"of the {mine:N0} the emulator uses: {mineDone:N0} mapped   ·   "
            : "";

        tally += $"{done:N0} of {Rows.Count:N0} in all";
        if (byModel > 0) tally += $" ({byModel:N0} picked by the model)";
        if (doubt > 0) tally += $"   ·   {doubt:N0} in doubt";
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
    /// The version the path says: the client folder or the assembly itself will do.
    ///
    /// The LAST stretch of the path is looked at before the whole path, and for a reason that took a
    /// while to see: the chain's clients live in <c>C:\Jondo 3.6.10.10\clientes\Cliente 3.6.9.9</c>,
    /// and searching the whole path the first one to appear is the 3.6.10.10 of the parent
    /// folder's name. With that the eight clients claimed the same name, the anchors of a wrong
    /// version were looked for and the eight mappings were written over the same file.
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

    /// <summary>The protocol assembly, whether it is given the client folder or the file.</summary>
    public static string ProtocolDll(string path)
    {
        if (File.Exists(path)) return path;
        // Standalone Cpp2IL exports work without a MelonLoader installation, including on Linux.
        foreach (string directory in new[] { path, Path.Combine(path, "cpp2il_out"), Path.Combine(path, "Cpp2IL", "cpp2il_out") })
        {
            string exported = Path.Combine(directory, "Ankama.Dofus.Protocol.Game.dll");
            if (File.Exists(exported)) return exported;
        }
        return Path.Combine(path, "MelonLoader", "Dependencies", "Il2CppAssemblyGenerator",
                            "Cpp2IL", "cpp2il_out", "Ankama.Dofus.Protocol.Game.dll");
    }
}

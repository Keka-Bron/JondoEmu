using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Binding a three-letter opcode to its real name, by hand and with the packet in front.
    ///
    /// The real names are in the client —513 of them, in <c>datos/nombres_reales_*.tsv</c>—
    /// but ORPHANED: nothing inside the client says which goes with which. It was checked four
    /// different ways and none gave the link, so there is no automatic way of knowing it.
    ///
    /// What can be done is recognise it by looking. The log shows the packet with its fields and
    /// which way it goes; with that in front, choosing from a closed list of 513 names is not guessing,
    /// it is identifying. What is chosen is stored and never asked again.
    ///
    /// ─── Why this lives here and not in Jondo.Unity.Reversing ───────────────────────────────
    ///
    /// Reading and writing two text files does not justify the server depending on the reverse
    /// engineering library, which drags along Cpp2IL and 110 MB of binary analysis. The format is
    /// two tab-separated columns; having both read it on their own comes cheaper than
    /// tying them together.
    /// </summary>
    public static class NameBinding
    {
        private static List<string>? _real;
        private static Dictionary<string, string>? _bound;

        /// <summary>The version of the client being emulated, which is what names the files.</summary>
        public const string Version = "3.6.10.10";

        private static string Real => Paths.Resolve($"nombres_reales_{Version}.tsv");
        private static string Bound => Paths.Resolve($"nombres_ligados_{Version}.tsv");

        private static Dictionary<string, string>? _domain;

        /// <summary>The names the client carries inside, to choose from.</summary>
        public static IReadOnlyList<string> Catalogue()
        {
            if (_real != null) return _real;

            _real = new List<string>();
            _domain = new Dictionary<string, string>(StringComparer.Ordinal);

            try
            {
                foreach (string line in File.ReadLines(Real))
                {
                    if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                    var parts = line.Split('\t');
                    string name = parts[0].Trim();
                    if (name.Length == 0) continue;

                    _real.Add(name);

                    // The domain comes from the path: «…Protocol.Group.Search.LobbyApplyResponse»
                    // leaves «groupsearch». The dots and capitals are removed because the client
                    // writes it together —UILogic.GroupSearch— and that way both forms meet.
                    if (parts.Length < 2) continue;
                    string full = parts[1].Trim();
                    int protocolo = full.IndexOf(".Protocol.", StringComparison.Ordinal);
                    if (protocolo < 0) continue;

                    string cola = full[(protocolo + ".Protocol.".Length)..];
                    int ultimo = cola.LastIndexOf('.');
                    if (ultimo <= 0) continue;

                    _domain[name] = cola[..ultimo].Replace(".", "").ToLowerInvariant();
                }
            }
            catch { }

            _real.Sort(StringComparer.OrdinalIgnoreCase);
            return _real;
        }

        /// <summary>Which family this name belongs to: «inventory», «fight», «groupsearch»…</summary>
        public static string Domain(string name)
        {
            Catalogue();
            return _domain!.TryGetValue(name, out string? d) ? d : "";
        }

        private static Dictionary<string, HashSet<string>>? _hints;

        /// <summary>
        /// What this opcode is about, according to the client code that touches it.
        ///
        /// The stage 3 index notes which client methods mention each message, and some
        /// keep the readable namespace: <c>Core.UILogic.Inventory.Inventory::AddObjectItem</c>.
        /// That «Inventory» is the same word the protocol uses to group its messages, so it
        /// serves as a hint to sort the list.
        ///
        /// It decides nothing —a hint is not an answer— but it puts in front the dozen names of
        /// the right family instead of the 513 in alphabetical order.
        /// </summary>
        public static IReadOnlyCollection<string> Hints(string opcode)
        {
            if (_hints == null)
            {
                _hints = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                try { LoadHints(); } catch { }
            }

            return _hints.TryGetValue(opcode, out var hints) ? hints : Array.Empty<string>();
        }

        private static void LoadHints()
        {
            string path = Paths.Resolve($"indice_{Version}.json");
            if (!File.Exists(path)) return;

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var found = new HashSet<string>(StringComparer.Ordinal);

                if (entry.Value.TryGetProperty("Sightings", out var sightings))
                {
                    foreach (var sighting in sightings.EnumerateArray())
                    {
                        if (sighting.TryGetProperty("Method", out var method))
                            Harvest(method.GetString(), found);
                    }
                }

                if (found.Count > 0) _hints![entry.Name] = found;
            }
        }

        /// <summary>Extracts the readable words from a name like «Core.UILogic.Inventory.X::Y».</summary>
        private static void Harvest(string? method, HashSet<string> into)
        {
            if (method == null) return;

            foreach (string piece in method.Split(new[] { '.', ':', '+', '<', '>' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // An initial capital and five letters are required.
                //
                // The names the obfuscator has touched are short lowercase strings —bkii,
                // bgmh, baze—, and without this filter they came in by the handful: they were the most
                // frequent «hints» of all and they distinguish nothing. An identifier the obfuscator
                // respected keeps its capital.
                if (piece.Length < 5 || !char.IsUpper(piece[0])) continue;

                // And the ones half the client carries do not say what the message is about either.
                if (piece is "Core" or "UILogic" or "Services" or "Update" or "Initialize"
                          or "Manager" or "Handler" or "Component") continue;

                into.Add(piece.ToLowerInvariant());
            }
        }

        /// <summary>What is already bound.</summary>
        public static IReadOnlyDictionary<string, string> All()
        {
            if (_bound != null) return _bound;

            _bound = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (string line in File.ReadLines(Bound))
                {
                    if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                    var parts = line.Split('\t');
                    if (parts.Length >= 2 && parts[0].Trim().Length == 3)
                        _bound[parts[0].Trim()] = parts[1].Trim();
                }
            }
            catch { }

            return _bound;
        }

        /// <summary>This opcode's name if someone has bound it, or an empty string.</summary>
        public static string Of(string opcode)
            => All().TryGetValue(opcode, out string? name) ? name : "";

        private static Dictionary<string, string>? _meaning;

        /// <summary>
        /// What this message does, according to the anchors.
        ///
        /// This is the only thing from the anchors still in use: the meaning is MEASURED against 242
        /// captures and it is true. The names the anchors proposed are not, and that is why they are not read from
        /// here. In front of the list of 513 names, knowing what the message does is what turns
        /// choosing into recognising.
        /// </summary>
        public static string Meaning(string opcode)
        {
            if (_meaning == null)
            {
                _meaning = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    foreach (string line in File.ReadLines(Paths.Resolve($"anclas_{Version}.tsv")))
                    {
                        if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                        var parts = line.Split('\t');
                        if (parts.Length >= 4 && parts[0].Trim().Length == 3 && parts[3].Trim().Length > 0)
                            _meaning[parts[0].Trim()] = parts[3].Trim();
                    }
                }
                catch { }
            }

            return _meaning.TryGetValue(opcode, out string? what) ? what : "";
        }

        /// <summary>
        /// Stores a binding. With an empty name it is undone, which is also needed.
        /// </summary>
        public static void Bind(string opcode, string name)
        {
            var bound = new Dictionary<string, string>(All(), StringComparer.Ordinal);
            if (name.Length == 0) bound.Remove(opcode);
            else bound[opcode] = name;

            var text = new StringBuilder();
            text.AppendLine($"# Opcodes de {Version} ligados a su nombre real, a mano.");
            text.AppendLine("#");
            text.AppendLine("# Se eligen de nombres_reales_*.tsv, que son los nombres que el cliente lleva dentro.");
            text.AppendLine("# Aqui no se propone nada: lo que esta, esta porque alguien lo ha reconocido mirando el");
            text.AppendLine("# paquete pasar. Lo escribe el menu del registro del servidor.");
            text.AppendLine("#");
            text.AppendLine("# opcode\tnombre");
            foreach (var pair in bound.OrderBy(p => p.Key, StringComparer.Ordinal))
                text.AppendLine($"{pair.Key}\t{pair.Value}");

            // It is written to a temporary file and moved: if the disk fails halfway, the previous file
            // is still whole. These are bindings made by hand and losing an afternoon of work to a
            // half-done write is no fun at all.
            string path = Bound;
            string half = path + ".parcial";
            File.WriteAllText(half, text.ToString(), new UTF8Encoding(true));
            File.Move(half, path, true);

            _bound = bound;
        }
    }
}

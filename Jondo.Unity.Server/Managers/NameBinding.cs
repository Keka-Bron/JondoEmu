using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Ligar un opcode de tres letras a su nombre de verdad, a mano y con el paquete delante.
    ///
    /// Los nombres reales están en el cliente —513 de ellos, en <c>datos/nombres_reales_*.tsv</c>—
    /// pero HUÉRFANOS: nada dentro del cliente dice cuál va con cuál. Se comprobó de cuatro maneras
    /// distintas y ninguna dio el enlace, así que no hay forma automática de saberlo.
    ///
    /// Lo que sí se puede es reconocerlo mirando. El registro enseña el paquete con sus campos y
    /// hacia dónde va; con eso delante, elegir de una lista cerrada de 513 nombres no es adivinar,
    /// es identificar. Lo que se elige se guarda y ya no se vuelve a preguntar.
    ///
    /// ─── Por qué esto vive aquí y no en Jondo.Unity.Reversing ───────────────────────────────
    ///
    /// Leer y escribir dos ficheros de texto no justifica que el servidor dependa de la biblioteca
    /// de ingeniería inversa, que arrastra Cpp2IL y 110 MB de análisis de binarios. El formato es de
    /// dos columnas separadas por tabulador; que lo lean los dos por su cuenta sale más barato que
    /// atarlos.
    /// </summary>
    public static class NameBinding
    {
        private static List<string>? _real;
        private static Dictionary<string, string>? _bound;

        /// <summary>La versión del cliente que se está emulando, que es la que nombra los ficheros.</summary>
        public const string Version = "3.6.10.10";

        private static string Real => Paths.Resolve($"nombres_reales_{Version}.tsv");
        private static string Bound => Paths.Resolve($"nombres_ligados_{Version}.tsv");

        private static Dictionary<string, string>? _domain;

        /// <summary>Los nombres que el cliente lleva dentro, para elegir de ahí.</summary>
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

                    // El dominio sale de la ruta: de «…Protocol.Group.Search.LobbyApplyResponse»
                    // queda «groupsearch». Se quitan los puntos y las mayúsculas porque el cliente
                    // lo escribe junto —UILogic.GroupSearch— y así las dos formas se encuentran.
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

        /// <summary>De qué familia es este nombre: «inventory», «fight», «groupsearch»…</summary>
        public static string Domain(string name)
        {
            Catalogue();
            return _domain!.TryGetValue(name, out string? d) ? d : "";
        }

        private static Dictionary<string, HashSet<string>>? _hints;

        /// <summary>
        /// De qué va este opcode, según el código del cliente que lo toca.
        ///
        /// El índice de la etapa 3 anota qué métodos del cliente mencionan cada mensaje, y algunos
        /// conservan el espacio de nombres legible: <c>Core.UILogic.Inventory.Inventory::AddObjectItem</c>.
        /// Ese «Inventory» es la misma palabra que usa el protocolo para agrupar sus mensajes, así que
        /// sirve de pista para ordenar la lista.
        ///
        /// No decide nada —una pista no es una respuesta— pero pone delante la docena de nombres de
        /// la familia correcta en vez de los 513 por orden alfabético.
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

        /// <summary>Saca las palabras legibles de un nombre como «Core.UILogic.Inventory.X::Y».</summary>
        private static void Harvest(string? method, HashSet<string> into)
        {
            if (method == null) return;

            foreach (string piece in method.Split(new[] { '.', ':', '+', '<', '>' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // Se exige mayúscula inicial y cinco letras.
                //
                // Los nombres que el ofuscador ha tocado son tiras cortas y en minúscula —bkii,
                // bgmh, baze—, y sin este filtro entraban a puñados: eran las «pistas» más
                // frecuentes de todas y no distinguen nada. Un identificador que el ofuscador
                // respetó conserva su mayúscula.
                if (piece.Length < 5 || !char.IsUpper(piece[0])) continue;

                // Y las que lleva medio cliente tampoco dicen de qué va el mensaje.
                if (piece is "Core" or "UILogic" or "Services" or "Update" or "Initialize"
                          or "Manager" or "Handler" or "Component") continue;

                into.Add(piece.ToLowerInvariant());
            }
        }

        /// <summary>Lo que ya está ligado.</summary>
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

        /// <summary>El nombre de este opcode si alguien lo ha ligado, o cadena vacía.</summary>
        public static string Of(string opcode)
            => All().TryGetValue(opcode, out string? name) ? name : "";

        private static Dictionary<string, string>? _meaning;

        /// <summary>
        /// Qué hace este mensaje, según las anclas.
        ///
        /// Esto es lo único de las anclas que se sigue usando: el significado está MEDIDO contra 242
        /// capturas y es cierto. Los nombres que las anclas proponían no, y por eso no se leen de
        /// aquí. Delante de la lista de 513 nombres, saber qué hace el mensaje es lo que convierte
        /// elegir en reconocer.
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
        /// Guarda una ligadura. Con el nombre vacío se deshace, que también hace falta.
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

            // Se escribe a un temporal y se mueve: si el disco falla a mitad, el fichero de antes
            // sigue entero. Son ligaduras hechas a mano y perder una tarde de trabajo por una
            // escritura a medias no tiene ninguna gracia.
            string path = Bound;
            string half = path + ".parcial";
            File.WriteAllText(half, text.ToString(), new UTF8Encoding(true));
            File.Move(half, path, true);

            _bound = bound;
        }
    }
}

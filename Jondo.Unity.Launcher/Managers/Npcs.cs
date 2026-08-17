using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Los NPCs de cada mapa: dónde están, qué se puede hacer con ellos y qué dicen.
    ///
    /// Un NPC es un actor más del jss, con la misma envoltura que el jugador o que un grupo de
    /// monstruos. Lo único que cambia es qué campo aparece dentro de f2.f1: f5 el jugador, f4 un
    /// grupo de monstruos y f7 un NPC. Medido sobre noventa actores de NPC en trece mapas de la
    /// captura del servidor de torneos.
    ///
    /// El nombre NO viaja. En todo el hilo no va ni un solo nombre de NPC: el actor lleva sólo el
    /// id de plantilla y el cliente saca de sus propios datos el nombre, el dibujo, el diálogo y
    /// las acciones. Por eso basta con elegir una plantilla que el cliente ya conozca.
    ///
    /// El id contextual es negativo y local al mapa. El servidor real reparte -20000, -20001... en
    /// orden, y el mismo número se repite en mapas distintos sin problema. Aquí se hace igual. No
    /// choca con los monstruos porque esos usan su propio rango, de -1000000 para abajo.
    /// </summary>
    public static class Npcs
    {
        /// <summary>Un NPC puesto en un mapa.</summary>
        public sealed class Spawn
        {
            public long MapId;
            public int NpcId;
            public int Cell;
            public int Orientation;

            /// <summary>El negativo con el que el cliente se refiere a él dentro de este mapa.</summary>
            public long ContextualId;

            /// <summary>El aspecto, ya troceado: "{5949|||200}".</summary>
            public long Bones;
            public long[] Skins = Array.Empty<long>();
            public long[] Colors = Array.Empty<long>();
            public long[] Scales = Array.Empty<long>();
        }

        /// <summary>Lo que la plantilla del NPC dice de él.</summary>
        public sealed class Template
        {
            public int Id;
            public string Look = "";
            public int Gender;

            /// <summary>Qué se le puede hacer. Es el número que el cliente manda en el f1 del iov.</summary>
            public int[] Actions = Array.Empty<int>();

            /// <summary>La pregunta que abre, si tiene diálogo.</summary>
            public long DialogMessageId;

            /// <summary>Las respuestas que se le ofrecen al jugador.</summary>
            public long[] Replies = Array.Empty<long>();
        }

        /// <summary>Comprar y vender: la acción que contesta con el catálogo.</summary>
        public const int Trade = 1;

        /// <summary>Hablar: la que abre el diálogo.</summary>
        public const int Talk = 3;

        /// <summary>La tienda de apariencias, que en el cable se comporta igual que la normal.</summary>
        public const int TradeCosmetics = 11;

        /// <summary>Desde dónde se empieza a numerar, que es lo que hace el servidor real.</summary>
        private const long FirstContextualId = -20000;

        private static readonly Dictionary<long, List<Spawn>> _byMap = new();
        private static readonly Dictionary<int, Template> _templates = new();

        public static int Count { get; private set; }

        public static void Initialize()
        {
            _byMap.Clear();
            _templates.Clear();

            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                Read(connection);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NPCs] No se han podido leer: {ex.Message}");
            }
        }

        private static void Read(SqliteConnection connection)
        {
            var spawns = connection.CreateCommand();
            spawns.CommandText = "SELECT MapId, NpcId, CellId, Orientation, Look FROM NpcSpawns ORDER BY MapId, Id;";
            using (var reader = spawns.ExecuteReader())
            {
                while (reader.Read())
                {
                    long mapId = reader.GetInt64(0);
                    if (!_byMap.TryGetValue(mapId, out var here))
                    {
                        here = new List<Spawn>();
                        _byMap[mapId] = here;
                    }

                    var spawn = new Spawn
                    {
                        MapId = mapId,
                        NpcId = reader.GetInt32(1),
                        Cell = reader.GetInt32(2),
                        Orientation = reader.GetInt32(3),
                        ContextualId = FirstContextualId - here.Count,
                    };

                    // El Look de la fila manda, y si viene vacío se usa el de la plantilla.
                    ReadLook(reader.IsDBNull(4) ? "" : reader.GetString(4), spawn);
                    here.Add(spawn);
                }
            }

            // Sólo las plantillas que hacen falta: son 6.468 en la base y aquí se usan unas pocas.
            var wanted = new HashSet<int>();
            foreach (var here in _byMap.Values)
            {
                foreach (var spawn in here) wanted.Add(spawn.NpcId);
            }

            foreach (int npcId in wanted)
            {
                var template = connection.CreateCommand();
                template.CommandText = "SELECT Look, Data FROM NpcTemplates WHERE Id = $id;";
                template.Parameters.AddWithValue("$id", npcId);
                using var reader = template.ExecuteReader();
                if (!reader.Read()) continue;

                var read = new Template
                {
                    Id = npcId,
                    Look = reader.IsDBNull(0) ? "" : reader.GetString(0),
                };
                ReadData(reader.IsDBNull(1) ? "" : reader.GetString(1), read);
                _templates[npcId] = read;
            }

            // Los que no traían aspecto propio lo heredan de su plantilla.
            foreach (var here in _byMap.Values)
            {
                foreach (var spawn in here)
                {
                    if (spawn.Bones != 0) continue;
                    if (_templates.TryGetValue(spawn.NpcId, out var template)) ReadLook(template.Look, spawn);
                }
            }

            Count = 0;
            foreach (var here in _byMap.Values) Count += here.Count;

            Console.WriteLine($"[NPCs] {Count} puestos en {_byMap.Count} mapas, " +
                              $"{_templates.Count} plantillas.");
        }

        public static IReadOnlyList<Spawn> Of(long mapId)
            => _byMap.TryGetValue(mapId, out var here) ? here : (IReadOnlyList<Spawn>)Array.Empty<Spawn>();

        /// <summary>Quién es el negativo que el cliente acaba de clicar.</summary>
        public static Spawn? Find(long mapId, long contextualId)
        {
            if (!_byMap.TryGetValue(mapId, out var here)) return null;
            return here.Find(s => s.ContextualId == contextualId);
        }

        public static Template? TemplateOf(int npcId)
            => _templates.TryGetValue(npcId, out var template) ? template : null;

        /// <summary>
        /// El aspecto en la notación del propio cliente: "{huesos|pieles|colores|escalas}".
        ///
        /// Cada hueco puede llevar varios números separados por comas, y casi todos van vacíos: de
        /// los cincuenta y seis NPCs de la captura ninguno lleva pieles y sólo cinco llevan colores.
        /// </summary>
        private static void ReadLook(string look, Spawn spawn)
        {
            if (string.IsNullOrEmpty(look)) return;

            int start = look.IndexOf('{');
            int end = look.LastIndexOf('}');
            if (start < 0 || end <= start) return;

            string[] parts = look.Substring(start + 1, end - start - 1).Split('|');
            spawn.Bones = parts.Length > 0 ? First(parts[0]) : 0;
            spawn.Skins = parts.Length > 1 ? Numbers(parts[1]) : Array.Empty<long>();
            spawn.Colors = parts.Length > 2 ? Numbers(parts[2]) : Array.Empty<long>();
            spawn.Scales = parts.Length > 3 ? Numbers(parts[3]) : Array.Empty<long>();
        }

        private static long First(string part)
        {
            var numbers = Numbers(part);
            return numbers.Length > 0 ? numbers[0] : 0;
        }

        private static long[] Numbers(string part)
        {
            if (string.IsNullOrWhiteSpace(part)) return Array.Empty<long>();

            string[] pieces = part.Split(',');
            var fuera = new List<long>(pieces.Length);
            foreach (string piece in pieces)
            {
                if (long.TryParse(piece.Trim(), out long value)) fuera.Add(value);
            }
            return fuera.ToArray();
        }

        /// <summary>
        /// Lo que hace falta del Data de la plantilla: las acciones y el diálogo.
        ///
        /// Las acciones importan porque el f1 que el cliente manda en el iov es exactamente el
        /// actions[0] de la plantilla —comprobado en los cincuenta y un NPCs de tienda de la
        /// captura, cincuenta y uno de cincuenta y uno— así que un NPC que no declare la acción ni
        /// siquiera ofrece la opción en el menú del botón derecho.
        /// </summary>
        private static void ReadData(string json, Template template)
        {
            if (string.IsNullOrEmpty(json)) return;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                template.Actions = Array_(root, "actions");
                if (root.TryGetProperty("gender", out var gender) && gender.ValueKind == JsonValueKind.Number)
                {
                    template.Gender = gender.GetInt32();
                }

                // dialogData es una lista de bloques y de cada uno interesa el messageId.
                if (root.TryGetProperty("dialogData", out var dialog)
                    && dialog.TryGetProperty("Array", out var blocks)
                    && blocks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in blocks.EnumerateArray())
                    {
                        if (block.TryGetProperty("messageId", out var messageId)
                            && messageId.ValueKind == JsonValueKind.Number)
                        {
                            template.DialogMessageId = messageId.GetInt64();
                            break;
                        }
                    }
                }

                // dialogReplies es una lista de pares [idDeRespuesta, idDeTexto] y el primero manda.
                if (root.TryGetProperty("dialogReplies", out var replies)
                    && replies.TryGetProperty("Array", out var list)
                    && list.ValueKind == JsonValueKind.Array)
                {
                    var fuera = new List<long>();
                    foreach (var reply in list.EnumerateArray())
                    {
                        if (!reply.TryGetProperty("values", out var values)) continue;
                        if (!values.TryGetProperty("Array", out var pair)) continue;
                        if (pair.ValueKind != JsonValueKind.Array) continue;

                        foreach (var value in pair.EnumerateArray())
                        {
                            if (value.ValueKind == JsonValueKind.Number) fuera.Add(value.GetInt64());
                            break;
                        }
                    }
                    template.Replies = fuera.ToArray();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NPCs] No se ha podido leer la plantilla {template.Id}: {ex.Message}");
            }
        }

        private static int[] Array_(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var holder)) return Array.Empty<int>();
            if (!holder.TryGetProperty("Array", out var list)) return Array.Empty<int>();
            if (list.ValueKind != JsonValueKind.Array) return Array.Empty<int>();

            var fuera = new List<int>();
            foreach (var value in list.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.Number) fuera.Add(value.GetInt32());
            }
            return fuera.ToArray();
        }
    }
}

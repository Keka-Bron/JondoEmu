using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
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

            /// <summary>El hueso de la columna BoneId, que es lo que usa el jpv de la carga de mapa.</summary>
            public int BoneId;

            /// <summary>El Look de la fila tal cual, sin la vuelta a la plantilla. Lo pide el jpv.</summary>
            public string RawLook = "";

            /// <summary>El aspecto, ya troceado: "{5949|||200}". Es el del primer variante.</summary>
            public long Bones;
            public long[] Skins = Array.Empty<long>();
            public long[] Colors = Array.Empty<long>();
            public long[] Scales = Array.Empty<long>();

            /// <summary>Todos los aspectos que puede tener, con su condición. Casi siempre uno.</summary>
            public List<LookVariant> Variants = new();
        }

        /// <summary>Uno de los aspectos de un NPC, con lo que hace falta para verlo así.</summary>
        public sealed class LookVariant
        {
            public long Bones;
            public long[] Skins = Array.Empty<long>();
            public long[] Colors = Array.Empty<long>();
            public long[] Scales = Array.Empty<long>();

            /// <summary>Vacío en el de por defecto, que es el que se lleva quien no cumpla otro.</summary>
            public string Criterion = "";
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

            /// <summary>
            /// The translation key beside each reply, in the same order as <see cref="Replies"/>.
            /// </summary>
            /// <remarks>
            /// Kept because a reply id on its own says nothing about what the reply is, and one
            /// caller needs to know: the dungeon door has to find "Utilizar el manojo de llaves"
            /// and "Darle la llave y entrar" among everything else the guardian can say. Those ids
            /// are per-NPC -- 121 different ones across the game for the keyring alone -- so they
            /// cannot be written down; the WORDING is fixed, so they can be looked up.
            /// </remarks>
            public long[] ReplyTexts = Array.Empty<long>();
        }

        /// <summary>Comprar y vender: la acción que contesta con el catálogo.</summary>
        public const int Trade = 1;

        /// <summary>Hablar: la que abre el diálogo.</summary>
        public const int Talk = 3;

        /// <summary>La tienda de apariencias, que en el cable se comporta igual que la normal.</summary>
        public const int TradeCosmetics = 11;

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
            spawns.CommandText =
                "SELECT MapId, NpcId, CellId, Orientation, Look, BoneId FROM NpcSpawns ORDER BY MapId, Id;";
            using (var reader = spawns.ExecuteReader())
            {
                while (reader.Read())
                {
                    // Los vendedores que otro ha absorbido no se ponen en el mapa: su catálogo ya
                    // está en el que se queda, y dejarlos ahí sería el mismo escaparate dos veces
                    // en dos casillas contiguas.
                    if (Vendors.IsAbsorbed(reader.GetInt32(1))) continue;

                    long mapId = reader.GetInt64(0);
                    if (!_byMap.TryGetValue(mapId, out var here))
                    {
                        here = new List<Spawn>();
                        _byMap[mapId] = here;
                    }

                    // Donde lo pone Jondo manda sobre lo que diga la tabla.
                    //
                    // La colocacion de NpcSpawns se genero para 52 vendedores en bloques
                    // contiguos de cinco por familia, y al juntarlos por categoria dejaron de
                    // sembrarse 29 sin recalcular nada: de cada bloque quedaba el primero y
                    // cuatro huecos seguidos detras. Las casillas buenas estan en
                    // datos/vendedores_jondo.json, que si se versiona.
                    int npcId = reader.GetInt32(1);
                    var sitio = Vendors.PlacementOf(npcId);

                    var spawn = new Spawn
                    {
                        MapId = mapId,
                        NpcId = npcId,
                        Cell = sitio?.Cell ?? reader.GetInt32(2),
                        Orientation = sitio?.Orientation ?? reader.GetInt32(3),
                        ContextualId = ActorIds.NpcDelMapa(here.Count),
                        RawLook = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        BoneId = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    };

                    // El Look de la fila manda, y si viene vacío se usa el de la plantilla.
                    ReadLook(spawn.RawLook, spawn);
                    here.Add(spawn);
                }
            }

            // Sólo las plantillas que hacen falta: son 6.468 en la base y aquí se usan unas pocas.
            // Los del mundo se siembran AQUÍ, antes de recoger las plantillas: si fueran después
            // se quedarían sin aspecto, porque lo que se lee de NpcTemplates es sólo lo que hace
            // falta para los que ya están puestos.
            SembrarLosDelMundo();
            SembrarLasLuminomaquinas();
            NpcDialogues.Load();

            var wanted = new HashSet<int>(PlacedLater);
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

        /// <summary>Los mapas que tienen algún NPC puesto.</summary>
        /// <summary>
        /// Los NPCs del mundo, con la casilla y la orientación que tenían en el servidor de Ankama.
        ///
        /// No están colocados a ojo. Cada vez que el jugador entraba en un mapa, el servidor real
        /// le declaraba en el jss los NPCs que había; barriendo las 305 capturas salen 422 en 202
        /// mapas, de 327 plantillas distintas, y esto es ese barrido tal cual.
        ///
        /// El aspecto no viene en el fichero porque sale de la plantilla —las 327 tienen Look— y
        /// el diálogo tampoco: 246 de las 327 traen uno escrito en NpcTemplates y el manejador de
        /// NPCs ya lo sabe leer. Las otras 81 se quedan calladas.
        ///
        /// NO se comprueba que la casilla sea andable, y es a propósito: un NPC puede estar de pie
        /// sobre una casilla que el jugador no pisa, y de hecho sólo 151 de las 422 lo son. Lo que
        /// manda es la captura.
        ///
        /// Si un mapa ya tenía NPCs sembrados de NpcSpawns —el del zaap de Amakna, con nuestros
        /// vendedores— se deja como está y no se le añade nada. En las capturas ese mapa no tiene
        /// ni un NPC, así que hoy no se pisa nada, pero la regla vale para el día que sí.
        /// </summary>
        /// <summary>
        /// Seeds the NPCs that Ankama places around the world, through the content layers.
        /// </summary>
        /// <remarks>
        /// This used to read datos/npcs_reales.json straight off the disk. It now goes through
        /// NpcSpawnContent, which merges that file — the measured layer, 422 placements read off
        /// the captures — with content/npcs/spawns.json, the authored one. Same placements, plus
        /// whatever a person has decided on top, and every row remembers which of the two it came
        /// from.
        ///
        /// Nothing else changes: the map is still seeded before the templates are loaded, so each
        /// spawn inherits its look further down.
        /// </remarks>
        private static void SembrarLosDelMundo()
        {
            var spawns = Jondo.Unity.World.Content.NpcSpawnContent.Load(
                Paths.WorldNpcsJson,
                Paths.ContentFile(Jondo.Unity.World.Content.NpcSpawnContent.AuthoredFile),
                Console.WriteLine,
                Paths.WorldNpcsDerivedJson);

            if (spawns.Count == 0)
            {
                Console.WriteLine("[NPCs] No world placements at all: neither the measured file " +
                                  "nor the authored one had any.");
                return;
            }

            // Maps that already carry NPCs of ours are left alone. Today that is only the Amakna
            // zaap map, with the vendors; the captures put no NPC there, so nothing is overwritten,
            // but the rule holds for the day one is.
            var nuestros = new HashSet<long>(_byMap.Keys);

            int puestos = 0, saltados = 0, absorbidos = 0;
            try
            {
                foreach (var entrada in spawns.Values)
                {
                    long mapId = entrada.MapId;
                    if (nuestros.Contains(mapId)) { saltados++; continue; }

                    // Un vendedor absorbido tampoco se siembra AQUI, no solo en NpcSpawns.
                    //
                    // Los mapas de vendedores del servidor de torneos de Ankama estan en las
                    // capturas -de ahi salio el catalogo- asi que los 29 absorbidos volvian a
                    // aparecer por esta puerta. Y con la tienda vacia, porque su catalogo se lo
                    // quedo el que los absorbio: al abrirlos el servidor dice «tiene accion de
                    // tienda pero no vende nada» y al jugador no le sale nada.
                    int quien = entrada.NpcId;
                    if (Vendors.IsAbsorbed(quien)) { absorbidos++; continue; }

                    if (!_byMap.TryGetValue(mapId, out var aqui))
                    {
                        aqui = new List<Spawn>();
                        _byMap[mapId] = aqui;
                    }

                    // Sin aspecto: se lo pone el paso de más abajo, el que hereda el Look de la
                    // plantilla. Por eso esto tiene que correr antes de cargar las plantillas.
                    aqui.Add(new Spawn
                    {
                        MapId = mapId,
                        NpcId = quien,
                        Cell = entrada.Cell,
                        Orientation = entrada.Orientation,
                        ContextualId = ActorIds.NpcDelMapa(aqui.Count),
                    });
                    puestos++;
                }

                Count += puestos;
                var censo = spawns.Census();
                Console.WriteLine($"[NPCs] {puestos} del mundo, donde los tenía Ankama" +
                                  (saltados > 0 ? $", {saltados} en un mapa nuestro" : "") +
                                  (absorbidos > 0 ? $", {absorbidos} absorbidos por otro vendedor" : "") + ".");
                Console.WriteLine($"[Content] npc spawns: {censo[Jondo.Unity.World.Content.ContentLayer.Measured]} measured, " +
                                  $"{censo[Jondo.Unity.World.Content.ContentLayer.Authored]} authored, " +
                                  $"{spawns.ErasedCount} erased by hand.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NPCs] World placements could not be seeded: {ex.Message}");
            }
        }

        /// <summary>
        /// Las luminomáquinas de la Sima, una por planta con luz.
        /// </summary>
        /// <remarks>
        /// No van por la capa escrita a mano porque no hay nada que escribir: su sitio se CALCULA
        /// de la base -la planta, su mapa más bajo, la casilla andable más cercana al centro- y
        /// dejarlo en un fichero serían cinco números mágicos que envejecen mal. Dónde y por qué
        /// está en <see cref="Luminomachines.Place"/>, que es quien lo decide.
        ///
        /// Aquí, como los del mundo: sin aspecto, que lo hereda de la plantilla en el paso de más
        /// abajo. Por eso esto corre antes de cargar las plantillas y no después.
        /// </remarks>
        private static void SembrarLasLuminomaquinas()
        {
            try
            {
                Luminomachines.Place();
                foreach (var machine in Luminomachines.Placed)
                {
                    Poner(machine.MapId, Jondo.Unity.World.Content.Luminomachine.NpcId, machine.Cell);
                }

                RaidChests.Place();
                foreach (var chest in RaidChests.Placed)
                {
                    Poner(chest.MapId, Jondo.Unity.World.Content.RaidChest.NpcId, chest.Cell);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Raids] No se han podido poner las máquinas y los cofres: {ex.Message}");
            }
        }

        /// <summary>Un NPC del mobiliario de las raids, en su mapa.</summary>
        private static void Poner(long mapId, int npcId, int cell)
        {
            if (mapId <= 0) return;
            if (!_byMap.TryGetValue(mapId, out var aqui))
            {
                aqui = new List<Spawn>();
                _byMap[mapId] = aqui;
            }

            aqui.Add(new Spawn
            {
                MapId = mapId,
                NpcId = npcId,
                Cell = cell,
                Orientation = DefaultOrientation,
                ContextualId = ActorIds.NpcDelMapa(aqui.Count),
            });
        }

        /// <summary>Mirando al sureste, que es lo que le toca a quien no dice otra cosa.</summary>
        private const int DefaultOrientation = 1;

        /// <summary>
        /// Pone un NPC en un mapa de sala de sueño, si no está ya.
        /// </summary>
        /// <remarks>
        /// Aparte de las tres capas normales a propósito. Aquéllas describen el mundo, que es
        /// igual para todos; esto es de UNA partida: el Rey Gob aparece en la fuente, y el
        /// Dispensador de favores en el favor, del sueño de quien la abrió y no tiene por qué
        /// estar ahí para nadie más.
        ///
        /// Se hereda el aspecto de la plantilla igual que en la carga normal, porque si no el
        /// cliente recibe un actor sin nada que dibujar.
        /// </remarks>
        public static void PonerDelSueno(long mapId, int npcId, int cell, int orientation)
        {
            lock (_byMap)
            {
                if (!_byMap.TryGetValue(mapId, out var aqui))
                {
                    aqui = new List<Spawn>();
                    _byMap[mapId] = aqui;
                }

                foreach (var puesto in aqui)
                {
                    if (puesto.NpcId == npcId && puesto.Cell == cell) return;
                }

                var plantilla = TemplateOf(npcId);
                var spawn = new Spawn
                {
                    MapId = mapId,
                    NpcId = npcId,
                    Cell = cell,
                    Orientation = orientation,
                    ContextualId = ActorIds.NpcDelMapa(aqui.Count),
                    RawLook = plantilla?.Look ?? "",
                };

                // The look cut into bones, skins, colours and scale, as every other spawn has it.
                // Only the raw text was copied, and an NPC with bone 0 is the question mark the
                // client draws for a look it cannot find: the Rey Gob came out as one. The real
                // one is bone 6243, skin 1665, six colours and 110 of scale -- the template's.
                ReadLook(spawn.RawLook, spawn);
                spawn.BoneId = (int)spawn.Bones;
                aqui.Add(spawn);
            }

            Console.WriteLine($"[Sueños] NPC {npcId} placed on map {mapId}, cell {cell}.");
        }

        public static IEnumerable<long> Maps => _byMap.Keys;

        /// <summary>Every NPC placed in the world, map by map.</summary>
        public static IEnumerable<Spawn> AllSpawns
        {
            get
            {
                foreach (var here in _byMap.Values)
                    foreach (var spawn in here) yield return spawn;
            }
        }

        public static IReadOnlyList<Spawn> Of(long mapId)
            => _byMap.TryGetValue(mapId, out var here) ? here : (IReadOnlyList<Spawn>)Array.Empty<Spawn>();

        /// <summary>Quién es el negativo que el cliente acaba de clicar.</summary>
        /// <summary>Los NPCs que hay en un mapa. Vacio si no hay ninguno.</summary>
        public static IReadOnlyList<Spawn> OnMap(long mapId)
            => _byMap.TryGetValue(mapId, out var here) ? here : (IReadOnlyList<Spawn>)Array.Empty<Spawn>();

        public static Spawn? Find(long mapId, long contextualId)
        {
            if (!_byMap.TryGetValue(mapId, out var here)) return null;
            return here.Find(s => s.ContextualId == contextualId);
        }

        /// <summary>
        /// NPCs that stand on no map when the server starts and are placed later -- the dream's
        /// Rey Gob and Dispensador de favores -- whose templates are read all the same. Only the
        /// placed ones' were, and the Rey Gob came out with no template, no look, and the question
        /// mark for a face.
        /// </summary>
        private static readonly int[] PlacedLater = { Dreams.ReyGob, Dreams.FavorNpc };

        public static Template? TemplateOf(int npcId)
            => _templates.TryGetValue(npcId, out var template) ? template : null;

        /// <summary>Every template that has been read, for the passes that have to look at all of them.</summary>
        public static IEnumerable<Template> Templates => _templates.Values;

        /// <summary>
        /// El aspecto en la notación del propio cliente: "{huesos|pieles|colores|escalas}".
        ///
        /// Cada hueco puede llevar varios números separados por comas, y casi todos van vacíos: de
        /// los cincuenta y seis NPCs de la captura ninguno lleva pieles y sólo cinco llevan colores.
        /// </summary>
        /// <summary>
        /// El aspecto de un NPC, que puede ser VARIOS con una condición cada uno.
        /// </summary>
        /// <remarks>
        /// Cuarenta y ocho plantillas de las 6.467 traen el aspecto escrito como una lista separada
        /// por comas, cada uno con su criterio detrás de un dólar:
        ///
        ///   {10152|||95$1;0;0;RV&lt;7,Raid_Score,5000},{10151|||95$1;0;0;RV&gt;7,Raid_Score,4999&amp;...}
        ///
        /// Eso es el cofre de la raid, que se va llenando según la puntuación. Antes esto cortaba
        /// por el PRIMER corchete y el ÚLTIMO, así que en una plantilla de varias se tragaba los
        /// cinco de una vez y salía un aspecto imposible; ahora se leen uno a uno.
        ///
        /// El primero manda mientras nadie elija otro: en 47 de las 48 el primero no lleva criterio
        /// —es el de siempre— y quien sí lo lleva es este cofre, cuyo primero es el del cofre
        /// vacío. Las dos cosas quieren lo mismo: sin nada que preguntar, el de por defecto.
        /// </remarks>
        private static void ReadLook(string look, Spawn spawn)
        {
            if (string.IsNullOrEmpty(look)) return;

            var variants = Variantes(look);
            if (variants.Count == 0) return;

            spawn.Variants = variants;
            spawn.Bones = variants[0].Bones;
            spawn.Skins = variants[0].Skins;
            spawn.Colors = variants[0].Colors;
            spawn.Scales = variants[0].Scales;
        }

        /// <summary>Los aspectos de arriba, separados por su nivel de corchete y no por comas.</summary>
        /// <remarks>
        /// Por el nivel de corchete porque un aspecto puede llevar otro dentro —la montura de un
        /// NPC es <c>1@0={195|||110}</c>— y las comas de dentro no separan nada.
        /// </remarks>
        internal static List<LookVariant> Variantes(string look)
        {
            var fuera = new List<LookVariant>();
            int depth = 0, start = -1;

            for (int i = 0; i < look.Length; i++)
            {
                char c = look[i];
                if (c == '{')
                {
                    if (depth == 0) start = i + 1;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        fuera.Add(Variante(look.Substring(start, i - start)));
                        start = -1;
                    }
                }
            }

            return fuera;
        }

        /// <summary>Un aspecto suelto: los números delante del dólar y el criterio detrás.</summary>
        private static LookVariant Variante(string contenido)
        {
            string criterio = "";
            int depth = 0, dolar = -1;

            for (int i = 0; i < contenido.Length; i++)
            {
                char c = contenido[i];
                if (c == '{') depth++;
                else if (c == '}') depth--;
                else if (c == '$' && depth == 0) { dolar = i; break; }
            }

            if (dolar >= 0)
            {
                // Detrás del dólar van «orden;?;?;criterio», y el criterio es todo lo que queda
                // después del tercer punto y coma: puede llevar los suyos dentro.
                string cola = contenido.Substring(dolar + 1);
                contenido = contenido.Substring(0, dolar);

                int at = 0;
                for (int saltos = 0; saltos < 3 && at >= 0; saltos++)
                {
                    int next = cola.IndexOf(';', at);
                    at = next < 0 ? -1 : next + 1;
                }

                if (at >= 0 && at <= cola.Length) criterio = cola.Substring(at);
            }

            string[] parts = contenido.Split('|');
            return new LookVariant
            {
                Bones = parts.Length > 0 ? First(parts[0]) : 0,
                Skins = parts.Length > 1 ? Numbers(parts[1]) : Array.Empty<long>(),
                Colors = parts.Length > 2 ? Colores(parts[2]) : Array.Empty<long>(),
                Scales = parts.Length > 3 ? Numbers(parts[3]) : Array.Empty<long>(),
                Criterion = criterio,
            };
        }

        /// <summary>
        /// Cuál de los aspectos de un NPC le toca ver a quien pregunta, o null para el de siempre.
        /// </summary>
        /// <remarks>
        /// El de por defecto es el primero, y se devuelve null en vez de él para que quien llame no
        /// tenga que copiar nada: si nadie gana, se queda lo que el spawn ya traía.
        ///
        /// Los que NO llevan criterio no se eligen, se heredan: son el de siempre, y preguntarles
        /// daría que sí antes de mirar los demás. Y lo que no se sabe contestar tampoco gana, que
        /// es lo que deja al cofre vacío para quien no está en ninguna raid.
        /// </remarks>
        public static LookVariant VariantFor(Spawn spawn, Jondo.Unity.World.Content.Criterion.Resolver resolver)
        {
            if (spawn == null || spawn.Variants.Count <= 1) return null;

            foreach (var variant in spawn.Variants)
            {
                if (variant.Criterion.Length == 0) continue;
                if (Jondo.Unity.World.Content.Criterion.Met(variant.Criterion, resolver)) return variant;
            }

            return null;
        }

        private static long First(string part)
        {
            var numbers = Numbers(part);
            return numbers.Length > 0 ? numbers[0] : 0;
        }

        /// <summary>
        /// Los colores de un aspecto, en la forma que espera el cliente.
        ///
        /// La sección de color de un look NO es una lista de números: son pares
        /// «índice=valor», y el valor viene en decimal o en hexadecimal con almohadilla. El
        /// Bontariano enfadado es {1|90,2140|2=16305204,3=3772345,4=14024699,6=#8F5203|53}.
        ///
        /// Se leía con Numbers(), que espera números sueltos separados por comas: no parseaba ni
        /// uno, no llegaba ni un color, y el cliente pintaba el aspecto sin tintes, o sea GRIS.
        /// Medido sobre los 6.468 NPCs del catálogo: 2.045 llevan colores y LOS 2.045 usan la
        /// forma de pares. Ni uno usa una lista plana, así que estaban saliendo grises todos.
        ///
        /// Por el cable el color va con su índice metido en el byte alto —(índice &lt;&lt; 24) | rgb—,
        /// que es la misma cuenta que hace BreedLookTable.IndexColors para el personaje del
        /// jugador. La diferencia está en de dónde sale el índice: allí es la posición en la
        /// lista, y aquí viene ESCRITO y no es correlativo. El Bontariano usa el 2, el 3, el 4 y
        /// el 6, y se salta el 1 y el 5; numerándolos por posición, sus tintes irían a las
        /// ranuras equivocadas.
        /// </summary>
        private static long[] Colores(string parte)
        {
            if (string.IsNullOrWhiteSpace(parte)) return Array.Empty<long>();

            var fuera = new List<long>();
            int posicion = 0;
            foreach (string trozo in parte.Split(','))
            {
                string p = trozo.Trim();
                if (p.Length == 0) continue;
                posicion++;

                // Sin el «índice=» delante se numera por posición, que es lo que hace el aspecto
                // del jugador. Hoy no lo usa ni un NPC; queda por si algún día cambia el dato.
                long indice = posicion;
                string valor = p;

                int igual = p.IndexOf('=');
                if (igual > 0)
                {
                    if (long.TryParse(p.Substring(0, igual).Trim(), out long suyo)) indice = suyo;
                    valor = p.Substring(igual + 1).Trim();
                }

                if (!LeerColor(valor, out long rgb)) continue;
                fuera.Add((indice << 24) | (rgb & 0xFFFFFF));
            }
            return fuera.ToArray();
        }

        /// <summary>Un color, en decimal o en hexadecimal con almohadilla delante.</summary>
        private static bool LeerColor(string texto, out long rgb)
        {
            rgb = 0;
            if (string.IsNullOrEmpty(texto)) return false;

            if (texto[0] == '#')
            {
                return long.TryParse(texto.Substring(1),
                                     System.Globalization.NumberStyles.HexNumber,
                                     System.Globalization.CultureInfo.InvariantCulture, out rgb);
            }
            return long.TryParse(texto, out rgb);
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

                // dialogReplies es una lista de pares [idDeRespuesta, idDeTexto]. Se guardan LOS
                // DOS: el id es con lo que se contesta, y la clave de texto es lo único que dice
                // qué respuesta es. Ver Template.ReplyTexts.
                if (root.TryGetProperty("dialogReplies", out var replies)
                    && replies.TryGetProperty("Array", out var list)
                    && list.ValueKind == JsonValueKind.Array)
                {
                    var fuera = new List<long>();
                    var textos = new List<long>();
                    foreach (var reply in list.EnumerateArray())
                    {
                        if (!reply.TryGetProperty("values", out var values)) continue;
                        if (!values.TryGetProperty("Array", out var pair)) continue;
                        if (pair.ValueKind != JsonValueKind.Array) continue;

                        long id = 0, texto = 0;
                        int cual = 0;
                        foreach (var value in pair.EnumerateArray())
                        {
                            if (value.ValueKind == JsonValueKind.Number)
                            {
                                if (cual == 0) id = value.GetInt64();
                                else if (cual == 1) texto = value.GetInt64();
                            }

                            if (++cual >= 2) break;
                        }

                        if (id == 0) continue;
                        fuera.Add(id);
                        textos.Add(texto);
                    }

                    template.Replies = fuera.ToArray();
                    template.ReplyTexts = textos.ToArray();
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

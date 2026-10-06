using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Each map's NPCs: where they are, what can be done with them and what they say.
    ///
    /// An NPC is one more actor of the jss, with the same envelope as the player or a group of
    /// monsters. The only thing that changes is which field appears inside f2.f1: f5 the player, f4 a
    /// monster group and f7 an NPC. Measured over ninety NPC actors on thirteen maps of the
    /// tournament server's capture.
    ///
    /// The name does NOT travel. In the whole thread not a single NPC name goes: the actor only carries the
    /// template id and the client takes from its own data the name, the drawing, the dialogue and
    /// the actions. That is why choosing a template the client already knows is enough.
    ///
    /// The contextual id is negative and local to the map. The real server hands out -20000, -20001... in
    /// order, and the same number repeats on different maps without a problem. Here it is done the same. It does not
    /// clash with the monsters because those use their own range, from -1000000 downwards.
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

            /// <summary>The negative the client refers to it by within this map.</summary>
            public long ContextualId;

            /// <summary>The bone in the BoneId column, which is what the map load's jpv uses.</summary>
            public int BoneId;

            /// <summary>The row's Look as is, without falling back to the template. The jpv asks for it.</summary>
            public string RawLook = "";

            /// <summary>The look, already split up: "{5949|||200}". It is the first variant's.</summary>
            public long Bones;
            public long[] Skins = Array.Empty<long>();
            public long[] Colors = Array.Empty<long>();
            public long[] Scales = Array.Empty<long>();

            /// <summary>All the looks it can have, with their condition. Almost always one.</summary>
            public List<LookVariant> Variants = new();
        }

        /// <summary>One of an NPC's looks, with what is needed to see it that way.</summary>
        public sealed class LookVariant
        {
            public long Bones;
            public long[] Skins = Array.Empty<long>();
            public long[] Colors = Array.Empty<long>();
            public long[] Scales = Array.Empty<long>();

            /// <summary>Empty in the default one, which is the one whoever meets no other gets.</summary>
            public string Criterion = "";
        }

        /// <summary>What the NPC's template says about it.</summary>
        public sealed class Template
        {
            public int Id;
            public string Look = "";
            public int Gender;

            /// <summary>What can be done to it. It is the number the client sends in the iov's f1.</summary>
            public int[] Actions = Array.Empty<int>();

            /// <summary>The question it opens with, if it has dialogue.</summary>
            public long DialogMessageId;

            /// <summary>The replies offered to the player.</summary>
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

        /// <summary>Buy and sell: the action that answers with the catalogue.</summary>
        public const int Trade = 1;

        /// <summary>Talk: the one that opens the dialogue.</summary>
        public const int Talk = 3;

        /// <summary>The appearance shop, which on the wire behaves just like the normal one.</summary>
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
                    // The sellers another has absorbed are not placed on the map: their catalogue already
                    // is in the one kept, and leaving them there would be the same shop window twice
                    // on two adjacent cells.
                    if (Vendors.IsAbsorbed(reader.GetInt32(1))) continue;

                    long mapId = reader.GetInt64(0);
                    if (!_byMap.TryGetValue(mapId, out var here))
                    {
                        here = new List<Spawn>();
                        _byMap[mapId] = here;
                    }

                    // Where Jondo puts it rules over what the table says.
                    //
                    // NpcSpawns's placement was generated for 52 sellers in
                    // contiguous blocks of five per family, and when they were merged by category 29
                    // stopped being seeded without recalculating anything: of each block the first remained and
                    // four gaps in a row behind it. The good cells are in
                    // datos/vendedores_jondo.json, which is versioned.
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

                    // The row's Look rules, and if it comes empty the template's is used.
                    ReadLook(spawn.RawLook, spawn);
                    here.Add(spawn);
                }
            }

            // Only the templates that are needed: there are 6,468 in the base and a few are used here.
            // The world's ones are seeded HERE, before gathering the templates: if they came after
            // they would be left without a look, because what is read from NpcTemplates is only what is
            // needed for the ones already placed.
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

            // Those that did not bring a look of their own inherit their template's.
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

        /// <summary>The maps that have some NPC placed.</summary>
        /// <summary>
        /// The world's NPCs, with the cell and the orientation they had on Ankama's server.
        ///
        /// They are not placed by eye. Each time the player entered a map, the real server
        /// declared in the jss the NPCs there were; sweeping the 305 captures gives 422 on 202
        /// maps, of 327 distinct templates, and this is that sweep as is.
        ///
        /// The look does not come in the file because it comes from the template —all 327 have a Look— and
        /// the dialogue does not either: 246 of the 327 bring one written in NpcTemplates and the NPC
        /// handler already knows how to read it. The other 81 stay silent.
        ///
        /// It is NOT checked that the cell is walkable, and on purpose: an NPC can be standing
        /// on a cell the player does not step on, and in fact only 151 of the 422 are. What
        /// rules is the capture.
        ///
        /// If a map already had NPCs seeded from NpcSpawns —the Amakna zaap one, with our
        /// sellers— it is left as it is and nothing is added to it. In the captures that map does not have
        /// a single NPC, so today nothing is overwritten, but the rule holds for the day it does.
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

                    // An absorbed seller is not seeded HERE either, not only in NpcSpawns.
                    //
                    // The seller maps of Ankama's tournament server are in the
                    // captures -that is where the catalogue came from- so the 29 absorbed ones came back
                    // in through this door. And with an empty shop, because their catalogue went to
                    // the one that absorbed them: on opening them the server says «has a shop
                    // action but sells nothing» and the player gets nothing.
                    int quien = entrada.NpcId;
                    if (Vendors.IsAbsorbed(quien)) { absorbidos++; continue; }

                    if (!_byMap.TryGetValue(mapId, out var aqui))
                    {
                        aqui = new List<Spawn>();
                        _byMap[mapId] = aqui;
                    }

                    // Without a look: the step further down gives it one, the one that inherits the
                    // template's Look. That is why this has to run before loading the templates.
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
        /// The Sima's luminomachines, one per lit floor.
        /// </summary>
        /// <remarks>
        /// They do not go through the hand-written layer because there is nothing to write: their place is WORKED OUT
        /// from the base -the floor, its lowest map, the walkable cell closest to the centre- and
        /// leaving it in a file would be five magic numbers that age badly. Where and why
        /// is in <see cref="Luminomachines.Place"/>, which is what decides it.
        ///
        /// Here, like the world's ones: without a look, which it inherits from the template in the step further
        /// down. That is why this runs before loading the templates and not after.
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

        /// <summary>An NPC of the raids' furniture, on its map.</summary>
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

        /// <summary>Facing south-east, which is what whoever does not say otherwise gets.</summary>
        private const int DefaultOrientation = 1;

        /// <summary>
        /// Places an NPC on a dream room map, if it is not there already.
        /// </summary>
        /// <remarks>
        /// Apart from the three normal layers on purpose. Those describe the world, which is
        /// the same for everyone; this belongs to ONE run: the Rey Gob appears at the fountain, and the
        /// Dispensador de favores at the favour, of the dream of whoever opened it, and it has no reason to
        /// be there for anybody else.
        ///
        /// The look is inherited from the template just like in the normal load, because otherwise the
        /// client receives an actor with nothing to draw.
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

        /// <summary>Who the negative the client has just clicked is.</summary>
        /// <summary>The NPCs on a map. Empty if there are none.</summary>
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
            => _templates.TryGetValue(npcId, out var template) ? template
               : _onDemand.TryGetValue(npcId, out var later) ? later : null;

        /// <summary>
        /// The templates read after the start, for NPCs an administrator puts on a map: kept apart
        /// from the ones read at boot, which nothing writes to once the server is up and so can be
        /// read from every socket without a lock.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Template> _onDemand = new();

        /// <summary>This NPC's template, read from the base if no NPC of it stood anywhere at boot.</summary>
        public static Template? EnsureTemplate(int npcId)
        {
            var known = TemplateOf(npcId);
            if (known != null) return known;
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT Look, Data FROM NpcTemplates WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", npcId);
                using var reader = command.ExecuteReader();
                if (!reader.Read()) return null;
                var read = new Template { Id = npcId, Look = reader.IsDBNull(0) ? "" : reader.GetString(0) };
                ReadData(reader.IsDBNull(1) ? "" : reader.GetString(1), read);
                return _onDemand.GetOrAdd(npcId, read);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NPCs] No se ha podido leer la plantilla {npcId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Puts an NPC on a map while the server runs -- an administrator's, on the cell he stands
        /// on -- until the server stops. Null when the NPC has no template, or one of it already
        /// stands on that cell.
        /// </summary>
        /// <remarks>
        /// The map's list is replaced, never changed in place: the actor list of the map is built
        /// on other sockets by walking it, and a list that changes under a walk throws.
        /// </remarks>
        public static Spawn? PlaceAtRuntime(long mapId, int npcId, int cell, int orientation)
        {
            var template = EnsureTemplate(npcId);
            if (template == null || mapId <= 0) return null;

            lock (_byMap)
            {
                var here = _byMap.TryGetValue(mapId, out var list) ? new List<Spawn>(list) : new List<Spawn>();
                if (here.Any(s => s.NpcId == npcId && s.Cell == cell)) return null;

                // A contextual id nobody on the map has: after a removal the count no longer is one.
                int position = here.Count;
                while (here.Any(s => s.ContextualId == ActorIds.NpcDelMapa(position))) position++;

                var spawn = new Spawn
                {
                    MapId = mapId,
                    NpcId = npcId,
                    Cell = cell,
                    Orientation = orientation,
                    ContextualId = ActorIds.NpcDelMapa(position),
                    RawLook = template.Look,
                };
                ReadLook(spawn.RawLook, spawn);
                spawn.BoneId = (int)spawn.Bones;
                here.Add(spawn);
                _byMap[mapId] = here;
                return spawn;
            }
        }

        /// <summary>Takes an NPC off its map until the server stops. False when it was not there.</summary>
        public static bool RemoveAtRuntime(long mapId, long contextualId)
        {
            lock (_byMap)
            {
                if (!_byMap.TryGetValue(mapId, out var list)) return false;
                var here = new List<Spawn>(list);
                if (here.RemoveAll(s => s.ContextualId == contextualId) == 0) return false;
                _byMap[mapId] = here;
                return true;
            }
        }

        /// <summary>Every template that has been read, for the passes that have to look at all of them.</summary>
        public static IEnumerable<Template> Templates => _templates.Values;

        /// <summary>
        /// The look in the client's own notation: "{bones|skins|colours|scales}".
        ///
        /// Each slot can carry several comma-separated numbers, and almost all go empty: of
        /// the fifty-six NPCs of the capture none carries skins and only five carry colours.
        /// </summary>
        /// <summary>
        /// An NPC's look, which can be SEVERAL with a condition each.
        /// </summary>
        /// <remarks>
        /// Forty-eight templates of the 6,467 bring the look written as a comma-separated
        /// list, each one with its criterion behind a dollar sign:
        ///
        ///   {10152|||95$1;0;0;RV&lt;7,Raid_Score,5000},{10151|||95$1;0;0;RV&gt;7,Raid_Score,4999&amp;...}
        ///
        /// That is the raid's chest, which fills up according to the score. Before, this cut
        /// at the FIRST bracket and the LAST, so in a template with several it swallowed the
        /// five at once and an impossible look came out; now they are read one by one.
        ///
        /// The first rules while nobody picks another: in 47 of the 48 the first carries no criterion
        /// —it is the usual one— and the one that does carry it is this chest, whose first is the empty
        /// chest's. Both things want the same: with nothing to ask, the default one.
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

        /// <summary>The looks above, split by their bracket level and not by commas.</summary>
        /// <remarks>
        /// By bracket level because a look can carry another inside —an NPC's mount
        /// is <c>1@0={195|||110}</c>— and the commas inside separate nothing.
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

        /// <summary>A single look: the numbers in front of the dollar sign and the criterion behind.</summary>
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
                // Behind the dollar sign go «order;?;?;criterion», and the criterion is everything left
                // after the third semicolon: it can carry its own inside.
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
        /// Which of an NPC's looks whoever asks gets to see, or null for the usual one.
        /// </summary>
        /// <remarks>
        /// The default is the first, and null is returned instead of it so that the caller does not
        /// have to copy anything: if nobody wins, what the spawn already brought stays.
        ///
        /// The ones that carry NO criterion are not chosen, they are inherited: they are the usual one, and asking them
        /// would give yes before looking at the rest. And what cannot be answered does not win either, which
        /// is what leaves the empty chest for whoever is in no raid.
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
        /// A look's colours, in the shape the client expects.
        ///
        /// The colour section of a look is NOT a list of numbers: they are
        /// «index=value» pairs, and the value comes in decimal or in hexadecimal with a hash. The
        /// angry Bontarian is {1|90,2140|2=16305204,3=3772345,4=14024699,6=#8F5203|53}.
        ///
        /// It was read with Numbers(), which expects loose comma-separated numbers: it did not parse
        /// a single one, not one colour arrived, and the client drew the look without tints, that is GREY.
        /// Measured over the 6,468 NPCs of the catalogue: 2,045 carry colours and ALL 2,045 use the
        /// pair form. Not one uses a flat list, so they were all coming out grey.
        ///
        /// On the wire the colour goes with its index in the high byte —(index &lt;&lt; 24) | rgb—,
        /// which is the same arithmetic BreedLookTable.IndexColors does for the player's
        /// character. The difference is where the index comes from: there it is the position in the
        /// list, and here it comes WRITTEN and is not consecutive. The Bontarian uses 2, 3, 4 and
        /// 6, and skips 1 and 5; numbering them by position, his tints would go to the
        /// wrong slots.
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

                // Without the «index=» in front it is numbered by position, which is what the player's
                // look does. Today not one NPC uses it; it stays in case the data ever changes.
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

        /// <summary>A colour, in decimal or in hexadecimal with a hash in front.</summary>
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
        /// What is needed from the template's Data: the actions and the dialogue.
        ///
        /// The actions matter because the f1 the client sends in the iov is exactly the
        /// template's actions[0] —checked on the fifty-one shop NPCs of the
        /// capture, fifty-one out of fifty-one— so an NPC that does not declare the action does not
        /// even offer the option in the right-click menu.
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

                // dialogData is a list of blocks and of each one the messageId is what matters.
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

                // dialogReplies is a list of [replyId, textId] pairs. BOTH are
                // kept: the id is what one answers with, and the text key is the only thing saying
                // which reply it is. See Template.ReplyTexts.
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

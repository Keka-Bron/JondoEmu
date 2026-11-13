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
    /// The dungeons: which maps are their rooms, where you go in and where you come out.
    ///
    /// It comes from the client's own data through extract_dungeons.py, and it is the only
    /// topology the game publishes beyond the 2.223 entries of MapScrolls. 187 dungeons, 763
    /// rooms and 159 entrance and exit maps — of which only 17 rooms had a MapScrolls row, so
    /// nearly all of it is new.
    ///
    /// Two things it is NOT:
    ///
    ///   - Entrances and exits are not border neighbours. entranceMapId is the map OUTSIDE with
    ///     the door on it, and in most dungeons the exit is that same map. They have no business
    ///     in MapScrolls.
    ///   - The order of the rooms is the order the data gives, and there is nothing to say it is
    ///     the order they are walked in. The Biblioteca del Maestro Cuerbok lists its three at
    ///     x = -14, -13, -15, which is not a progression. <see cref="NextRoom"/> follows it
    ///     anyway because it is the best there is, and it says so here rather than pretending.
    ///
    /// It is wired up now: <see cref="Handlers.DungeonHandler"/> takes the key at the door and
    /// puts the player in the first room, and the end of a fight moves them on to the next room
    /// or out through the exit.
    ///
    /// The walking order was doubted here for good reason and it has since been checked against a
    /// real playthrough. In the capture of the Corte del Jalató Real the player goes
    /// 121373185 → 121374209 → 121375233 → 121373187 → 121374211, which is exactly the order the
    /// data lists. That is one dungeon of 187, so the doubt stands for the other 186 — but the
    /// order is no longer only a guess.
    /// </summary>
    public static class DungeonManager
    {
        public sealed class Dungeon
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public int MinLevel { get; set; }
            public int OptimalLevel { get; set; }
            public int Difficulty { get; set; }
            public long EntranceMapId { get; set; }
            public long ExitMapId { get; set; }
            /// <summary>The rooms, in the order the client's data lists them.</summary>
            public List<long> Rooms { get; } = new List<long>();
            public List<int> Bosses { get; } = new List<int>();

            /// <summary>Whether the keyring opens it as well as its own key. 107 of the 187.</summary>
            public bool OnKeyring { get; set; }

            /// <summary>
            /// Whether the automatic dungeon search offers it: the client data's autoGroup, 117 of
            /// the 187 (Matchmaking).
            /// </summary>
            public bool AutoGroup { get; set; }

            /// <summary>
            /// Written by hand in content/world/dungeons.json, its rooms in the order they are
            /// fought: the mini-dungeons a quest NPC takes you into, which the catalogue lacks.
            /// </summary>
            public bool Authored { get; set; }

            /// <summary>
            /// Whether a hand-written dungeon still has a guardian who takes its key or the keyring at its
            /// entrance («door» in content/world/dungeons.json), as the catalogue's do.
            /// </summary>
            public bool KeepsADoor { get; set; }

            /// <summary>
            /// What has to be in the bag to get in: item id and how many. 126 of the 187 ask for
            /// something.
            /// </summary>
            /// <remarks>
            /// It was in the client's data all along and the extractor was dropping it. The Jalató
            /// dungeon asks for item 1568, "Llave de la Corte del Jalató Real", and the capture of
            /// somebody walking in shows the guardian asking "¿Seguro que quieres utilizar el
            /// manojo de llaves para entrar?" before taking it.
            /// </remarks>
            public List<(int Item, int Count)> Required { get; } = new List<(int, int)>();

            /// <summary>
            /// The last room, where the boss stands. Zero when it has no rooms.
            /// </summary>
            /// <remarks>
            /// Not always <c>Rooms[^1]</c>: the catalogue sometimes trails a hub or an Exit after
            /// the real boss room, and a failed reorder used to leave First Room at the end.
            /// <see cref="OrderRooms"/> picks it; until then it falls back to the last entry.
            /// </remarks>
            public long LastRoom { get; internal set; }

            /// <summary>The room you start in.</summary>
            public long FirstRoom => Rooms.Count == 0 ? 0 : Rooms[0];

            /// <summary>
            /// The monsters, not bosses, that a quest asks to beat in one of its rooms: room → them.
            /// Dramak, the mini-boss of his theatre's fourth auditorium, whom «Ainsi font les petites
            /// marionnettes» (1425's 7920) wants beaten there.
            /// </summary>
            public Dictionary<long, List<int>> Quarry { get; } = new();

            /// <summary>
            /// The monsters a person wrote for a room of a hand-written dungeon (content/world/dungeons.json
            /// «quarry»), put into <see cref="Quarry"/> with the quests' own: Capsálocke in the room of
            /// Lord Crow's library that leads to the throne, which no objective names a map for.
            /// </summary>
            public Dictionary<long, List<int>> WrittenQuarry { get; } = new();

            /// <summary>
            /// Who a room of a hand-written dungeon is made of (content/world/dungeons.json
            /// «roster»), in the place of its subarea's monsters: Lord Crow's library's subarea 211
            /// lists Capsálocke's companions and the Master's together, and each room has its own.
            /// </summary>
            public Dictionary<long, List<int>> WrittenRoster { get; } = new();

            /// <summary>Whether its boss is fought alone in his room (<see cref="BossFightsAlone"/>).</summary>
            public bool BossAlone { get; set; }
        }

        /// <summary>
        /// The dungeons whose boss stands alone in his room, not leading a group the room fills up to
        /// eight. The Giant Kralamoure (31): «Troisième et dernier combat : le Kralamoure Géant»,
        /// nobody else (wiki-dofus, Dofus pour les noobs); composed like the others, he came with
        /// seven of his lair's monsters. Nothing in the client's data says it.
        /// </summary>
        public static readonly int[] BossFightsAlone = { 31 };

        /// <summary>
        /// The dungeons as one set: <see cref="Initialize"/> fills a new one on its own thread and
        /// publishes it whole, so a reader meanwhile sees the old dungeons or the new ones. It used
        /// to empty them in place, and a search window opened in that moment listed none.
        /// </summary>
        private sealed class Tables
        {
            public readonly Dictionary<int, Dungeon> ById = new();
            public readonly Dictionary<long, List<Dungeon>> ByRoom = new();
        }

        private static volatile Tables _published = new();
        [ThreadStatic] private static Tables? _building;
        private static Tables Current => _building ?? _published;

        private static Dictionary<int, Dungeon> _byId => Current.ById;

        /// <summary>Every dungeon a map belongs to. A map can be a room of more than one.</summary>
        private static Dictionary<long, List<Dungeon>> _byRoom => Current.ByRoom;

        public static IReadOnlyDictionary<int, Dungeon> All => _byId;
        public static bool IsLoaded => _byId.Count > 0;

        public static void Initialize()
        {
            var fresh = new Tables();
            _building = fresh;
            try
            {
                if (!Fill()) return;
            }
            finally
            {
                _building = null;
            }
            _published = fresh;
        }

        /// <summary>The loading proper, into the set being built. False when there was nothing to read.</summary>
        private static bool Fill()
        {
            if (!Read()) return false;
            int reordenadas = OrderRooms();
            Store();

            if (reordenadas > 0)
                Console.WriteLine($"[DungeonManager] {reordenadas} dungeon(s) had their rooms " +
                                  $"out of order; they have been put in their play order.");

            int rooms = 0;
            foreach (var dungeon in _byId.Values) rooms += dungeon.Rooms.Count;
            Console.WriteLine($"[DungeonManager] {_byId.Count} dungeons, {rooms} rooms, " +
                              $"{_byRoom.Count} maps that are one.");
            return true;
        }

        /// <summary>
        /// Puts each dungeon's rooms into the order they are walked. Returns how many were wrong.
        /// </summary>
        /// <remarks>
        /// The extractor lists the rooms in whatever order the client's bundle held them, which is
        /// not the order they are played, and this server advances a player by taking the NEXT
        /// entry in the list. In the Famished Sunflower's Barn that list was Second, Fourth, Fifth,
        /// First, Third -- and a player entering it landed in room two, and was moved to four, then
        /// five, then three. Exactly the list, read straight down.
        ///
        /// Three sources say what the order is, and they are used in this order:
        ///
        /// <list type="number">
        /// <item>The room NAMES, when every room carries a different ordinal ("… - Third Room").
        /// It is the plainest statement of the order there is.</item>
        /// <item>The map LINKS otherwise. A room leads to the next through a corridor, and the
        /// corridor is the same map on both sides: the right-hand exit of one room is the left-hand
        /// entrance of the next. Following that from the only room nothing leads into gives the
        /// chain.</item>
        /// <item>The PARTIAL names when neither answers: the numbered rooms in their order, and the
        /// hubs and Exits without an ordinal kept after them instead of giving the dungeon up.
        /// Sandy Castle used to fail because one map is just "Sandy Castle".</item>
        /// </list>
        ///
        /// Then <see cref="Dungeon.LastRoom"/>: the map a quest names for one of the dungeon's
        /// bosses; otherwise, for a walked order, its last room short of the Exit; and only for a
        /// dungeon ordered by its partial names, the guess from them -- never a bare hub or an
        /// Exit, a named throne or fissure before the highest ordinal. Measured against what the
        /// boss rooms were before: the nine the corridors had right keep them, nineteen that sat
        /// on a First Room, an Entrance or an Exit get a real one.
        ///
        /// Anything neither source can order keeps the catalogue list, but LastRoom is still
        /// resolved — Lord Crow's Library has no ordinals and still needs its Throne Room.
        /// </remarks>
        private static int OrderRooms()
        {
            var scrolls = new Dictionary<long, (long Right, long Bottom, long Left, long Top)>();
            var names = new Dictionary<long, string>();

            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var links = connection.CreateCommand();
                links.CommandText =
                    "SELECT MapId, RightMapId, BottomMapId, LeftMapId, TopMapId FROM MapScrolls;";
                using (var reader = links.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        scrolls[reader.GetInt64(0)] = (reader.GetInt64(1), reader.GetInt64(2),
                                                       reader.GetInt64(3), reader.GetInt64(4));
                    }
                }

                var titles = connection.CreateCommand();
                titles.CommandText = "SELECT MapId, Name FROM MapPositions WHERE Name IS NOT NULL;";
                using (var reader = titles.ExecuteReader())
                {
                    while (reader.Read()) names[reader.GetInt64(0)] = reader.GetString(1);
                }
            }
            catch (Exception ex)
            {
                // A first boot, or a world without these tables. The rooms keep the order they
                // came in, which is what happened before this method existed.
                Console.WriteLine($"[DungeonManager] Could not read the map to order the " +
                                  $"rooms: {ex.Message}");
                foreach (var dungeon in _byId.Values)
                    dungeon.LastRoom = dungeon.Rooms.Count == 0 ? 0 : dungeon.Rooms[^1];
                return 0;
            }

            var questBossRooms = BossRoomsTheQuestsName();
            QuarryTheQuestsName();

            int changed = 0;
            foreach (var dungeon in _byId.Values)
            {
                if (dungeon.Rooms.Count == 0)
                {
                    dungeon.LastRoom = 0;
                    continue;
                }

                // A hand-written dungeon is already in its order; its last room is its boss room.
                if (dungeon.Authored)
                {
                    dungeon.LastRoom = dungeon.Rooms[^1];
                    continue;
                }

                if (dungeon.Rooms.Count == 1)
                {
                    dungeon.LastRoom = dungeon.Rooms[0];
                    continue;
                }

                // The walked order first: every room numbered, or the corridors followed from the
                // only room nothing leads into. Only a dungeon neither orders is ordered by its
                // partial names, and only there is its boss room guessed from them. The other way
                // round, the partial names moved the boss of nine dungeons the corridors had right
                // -- Captain Meno's Ship, Koutoulou's Temple, Dantinea's Palace, the Mastodon
                // Cemetery, the Belly of the Whale, Kardorim's Crypt, LeChouque's Boat, the Cursed
                // Araknas Temple, the Bearbarian Antichamber -- into their Fourth or Third Room,
                // and a win there left the dungeon before its last room.
                var walked = ByEveryName(dungeon.Rooms, names) ?? ByLinks(dungeon.Rooms, scrolls);
                var byName = walked == null ? ByName(dungeon.Rooms, names) : default;
                List<long>? ordered = walked ?? byName.Rooms;
                if (ordered != null && !ordered.SequenceEqual(dungeon.Rooms))
                {
                    dungeon.Rooms.Clear();
                    dungeon.Rooms.AddRange(ordered);
                    changed++;
                }

                // The boss room a quest names is the boss room: the client's own catalogue says
                // "beat this boss on this map" for 31 dungeons.
                long named = questBossRooms.TryGetValue(dungeon.Id, out var maps)
                    ? dungeon.Rooms.LastOrDefault(maps.Contains)
                    : 0;
                dungeon.LastRoom = named != 0 ? named
                                 : walked != null ? LastBeforeTheExit(dungeon.Rooms, names)
                                 : PickBossRoom(dungeon.Rooms, names, byName.NumberedLast);

                // No ordinal chain (Lord Crow): put the throne after the antechamber so FirstRoom
                // is not the boss and a win there still leaves through WayOut.
                if (walked == null && byName.NumberedLast == 0 && MoveBossBeforeExits(dungeon, names))
                    changed++;
            }

            return changed;
        }

        /// <summary>
        /// The maps the quest catalogue names for each dungeon's bosses: the objectives that ask
        /// to beat one of them "on this map" (type 16). Sandy Castle's quest 896 wants its boss
        /// beaten in the Fifth Room, Ilyzaelle's in Erzal's Fissure, the Mastodon Cemetery's in
        /// its Final Room.
        /// </summary>
        private static Dictionary<int, HashSet<long>> BossRoomsTheQuestsName()
        {
            const int BeatOnThisMap = 16;
            var byDungeon = new Dictionary<int, HashSet<long>>();
            try
            {
                string path = Paths.QuestsJson;
                if (!File.Exists(path)) return byDungeon;

                var mapsOfMonster = new Dictionary<long, HashSet<long>>();
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("objectives", out var objectives)) return byDungeon;
                foreach (var objective in objectives.EnumerateObject())
                {
                    var o = objective.Value;
                    if (!o.TryGetProperty("type", out var type) || type.GetInt32() != BeatOnThisMap) continue;
                    if (!o.TryGetProperty("map", out var map) || !o.TryGetProperty("params", out var parameters)
                        || parameters.GetArrayLength() == 0) continue;
                    long monster = parameters[0].GetInt64();
                    if (!mapsOfMonster.TryGetValue(monster, out var set)) mapsOfMonster[monster] = set = new HashSet<long>();
                    set.Add(map.GetInt64());
                }

                foreach (var dungeon in _byId.Values)
                {
                    var rooms = new HashSet<long>(dungeon.Rooms);
                    foreach (int boss in dungeon.Bosses)
                    {
                        if (!mapsOfMonster.TryGetValue(boss, out var maps)) continue;
                        foreach (long map in maps.Where(rooms.Contains))
                        {
                            if (!byDungeon.TryGetValue(dungeon.Id, out var set)) byDungeon[dungeon.Id] = set = new HashSet<long>();
                            set.Add(map);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DungeonManager] Could not read the quests' boss maps: {ex.Message}");
            }
            return byDungeon;
        }

        /// <summary>
        /// Fills each dungeon's <see cref="Dungeon.Quarry"/>: the objectives that ask to beat a
        /// monster that is not one of its bosses in one of its rooms -- «in one fight» (6) or «on
        /// this map» (16), the monster their first parameter.
        /// </summary>
        /// <remarks>
        /// Five in the whole catalogue, and each a monster the room's group has to bring: a room is
        /// composed from its subarea's monsters in a drawn order and a player alone fights the first
        /// four, so Dramak, one of five there, stood in the fourth auditorium's fight or did not.
        /// </remarks>
        private static void QuarryTheQuestsName()
        {
            const int InOneFight = 6, OnThisMap = 16;
            try
            {
                string path = Paths.QuestsJson;
                if (!File.Exists(path)) return;

                var dungeonOfRoom = new Dictionary<long, Dungeon>();
                foreach (var dungeon in _byId.Values)
                {
                    dungeon.Quarry.Clear();
                    foreach (var (room, monsters) in dungeon.WrittenQuarry)
                        dungeon.Quarry[room] = monsters.Where(m => !dungeon.Bosses.Contains(m)).Distinct().ToList();
                    foreach (long room in dungeon.Rooms) dungeonOfRoom.TryAdd(room, dungeon);
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("objectives", out var objectives)) return;
                foreach (var objective in objectives.EnumerateObject())
                {
                    var o = objective.Value;
                    if (!o.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.Number) continue;
                    if (type.GetInt32() is not (InOneFight or OnThisMap)) continue;
                    if (!o.TryGetProperty("map", out var map) || map.ValueKind != JsonValueKind.Number) continue;
                    if (!o.TryGetProperty("params", out var parameters) || parameters.GetArrayLength() == 0) continue;
                    if (!dungeonOfRoom.TryGetValue(map.GetInt64(), out var where)) continue;

                    int monster = parameters[0].GetInt32();
                    if (where.Bosses.Contains(monster)) continue;
                    if (!where.Quarry.TryGetValue(map.GetInt64(), out var list)) where.Quarry[map.GetInt64()] = list = new List<int>();
                    if (!list.Contains(monster)) list.Add(monster);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DungeonManager] Could not read the quests' monsters in dungeon rooms: {ex.Message}");
            }
        }

        /// <summary>
        /// The last room of a walked order, the Exit left out: the corridors of the Magik Riktus
        /// Big Top run on into its Exit, which is no boss room.
        /// </summary>
        private static long LastBeforeTheExit(IReadOnlyList<long> rooms, Dictionary<long, string> names)
        {
            for (int i = rooms.Count - 1; i >= 0; i--)
            {
                if (names.TryGetValue(rooms[i], out string? name) && name != null && IsExitName(name)) continue;
                return rooms[i];
            }
            return rooms.Count == 0 ? 0 : rooms[^1];
        }

        /// <summary>
        /// Rooms by the ordinal in their name when EVERY room has a different one, or null: the
        /// plainest statement of the order there is, and the one this server trusted first.
        /// </summary>
        internal static List<long>? ByEveryName(List<long> rooms, Dictionary<long, string> names)
        {
            var numbered = new List<(long Map, int Position)>();
            foreach (long room in rooms)
            {
                if (!names.TryGetValue(room, out string? name) || name == null) return null;
                int position = OrdinalOf(name);
                if (position == 0) return null;
                numbered.Add((room, position));
            }

            // Two rooms claiming the same number is not an order, it is a coincidence of wording.
            if (numbered.Select(pair => pair.Position).Distinct().Count() != numbered.Count) return null;
            return numbered.OrderBy(pair => pair.Position).Select(pair => pair.Map).ToList();
        }

        /// <summary>Moves <see cref="Dungeon.LastRoom"/> ahead of any Exit maps. Returns whether the list changed.</summary>
        private static bool MoveBossBeforeExits(Dungeon dungeon, Dictionary<long, string> names)
        {
            long boss = dungeon.LastRoom;
            if (boss == 0 || !dungeon.Rooms.Contains(boss)) return false;

            var exits = new List<long>();
            var rest = new List<long>();
            foreach (long room in dungeon.Rooms)
            {
                if (room == boss) continue;
                if (names.TryGetValue(room, out string? name) && name != null && IsExitName(name))
                    exits.Add(room);
                else
                    rest.Add(room);
            }

            var rebuilt = new List<long>(rest) { boss };
            rebuilt.AddRange(exits);
            if (rebuilt.SequenceEqual(dungeon.Rooms)) return false;

            dungeon.Rooms.Clear();
            dungeon.Rooms.AddRange(rebuilt);
            return true;
        }

        /// <summary>The ordinals a room name can carry, in the language the map table is written in.</summary>
        private static readonly string[] Ordinals =
        {
            "first", "second", "third", "fourth", "fifth", "sixth",
            "seventh", "eighth", "ninth", "tenth", "eleventh", "twelfth",
            // The Dragon Pig's Den runs to a Thirteenth Room; without it the twelfth was its end.
            "thirteenth", "fourteenth", "fifteenth", "sixteenth", "seventeenth", "eighteenth",
            "nineteenth", "twentieth",
        };

        /// <summary>Result of a name-based reorder: the full room list and the last numbered map.</summary>
        internal readonly struct NameOrder
        {
            public List<long>? Rooms { get; init; }
            public long NumberedLast { get; init; }
        }

        /// <summary>
        /// Rooms by the ordinal in their name. Hubs and Exits without an ordinal are kept after
        /// the numbered chain instead of failing the whole dungeon.
        /// </summary>
        internal static NameOrder ByName(List<long> rooms, Dictionary<long, string> names)
        {
            var numbered = new List<(long Map, int Position, string Prefix)>();
            var extras = new List<long>();

            foreach (long room in rooms)
            {
                if (!names.TryGetValue(room, out string? name) || name == null)
                {
                    extras.Add(room);
                    continue;
                }

                int position = OrdinalOf(name);
                if (position == 0)
                {
                    extras.Add(room);
                    continue;
                }

                numbered.Add((room, position, PrefixOf(name)));
            }

            if (numbered.Count < 2) return default;

            // Several prefixes (Koolich's Lair vs Koolich Cavern): keep the chain that owns an
            // Exit sibling, otherwise the longest unique-ordinal chain.
            var groups = numbered.GroupBy(pair => pair.Prefix, StringComparer.OrdinalIgnoreCase)
                                 .Select(group => group.ToList())
                                 .ToList();

            List<(long Map, int Position, string Prefix)>? chosen = null;
            foreach (var group in groups.OrderByDescending(g => g.Count))
            {
                if (group.Select(pair => pair.Position).Distinct().Count() != group.Count) continue;

                bool hasExit = extras.Any(map =>
                    names.TryGetValue(map, out string? n) && n != null &&
                    IsExitName(n) &&
                    string.Equals(PrefixOf(n), group[0].Prefix, StringComparison.OrdinalIgnoreCase));

                if (chosen == null || hasExit)
                {
                    chosen = group;
                    if (hasExit) break;
                }
            }

            if (chosen == null) return default;

            var chosenMaps = new HashSet<long>(chosen.Select(pair => pair.Map));
            var ordered = chosen.OrderBy(pair => pair.Position).Select(pair => pair.Map).ToList();
            long numberedLast = ordered[^1];

            // Numbered rooms from other prefixes stay reachable for OfRoom, after the main chain.
            foreach (var pair in numbered)
            {
                if (!chosenMaps.Contains(pair.Map)) extras.Add(pair.Map);
            }

            var nonExit = extras.Where(map =>
                !(names.TryGetValue(map, out string? n) && n != null && IsExitName(n))).ToList();
            var exits = extras.Where(map =>
                names.TryGetValue(map, out string? n) && n != null && IsExitName(n)).ToList();

            ordered.AddRange(nonExit);
            ordered.AddRange(exits);
            return new NameOrder { Rooms = ordered, NumberedLast = numberedLast };
        }

        /// <summary>
        /// Picks the boss room. Never an Exit or a bare hub; a named throne / fissure wins over
        /// the highest ordinal; otherwise the last numbered room from <see cref="ByName"/>.
        /// </summary>
        internal static long PickBossRoom(
            IReadOnlyList<long> rooms, Dictionary<long, string> names, long numberedLast = 0)
        {
            if (rooms == null || rooms.Count == 0) return 0;

            long named = 0;
            foreach (long room in rooms)
            {
                if (!names.TryGetValue(room, out string? name) || string.IsNullOrEmpty(name)) continue;
                if (IsExitName(name) || IsBareHub(name)) continue;
                if (OrdinalOf(name) != 0) continue;
                if (!LooksLikeBossName(name)) continue;
                named = room;
            }
            if (named != 0) return named;

            if (numberedLast != 0 && rooms.Contains(numberedLast)) return numberedLast;

            for (int i = rooms.Count - 1; i >= 0; i--)
            {
                long room = rooms[i];
                if (!names.TryGetValue(room, out string? name) || string.IsNullOrEmpty(name))
                    return room;
                if (IsExitName(name) || IsBareHub(name)) continue;
                return room;
            }

            return rooms[^1];
        }

        internal static int OrdinalOf(string name)
        {
            string lower = name.ToLowerInvariant();
            for (int i = 0; i < Ordinals.Length; i++)
            {
                // " third room", and not just "third": "Third Room" is the ordinal of a room,
                // while a name like "Thirsty Room" must not be read as one.
                if (lower.Contains(Ordinals[i] + " room")) return i + 1;
            }
            return FigureOf(name);
        }

        /// <summary>
        /// A room numbered with a figure, «Dramak's Theatre - Auditorium 5»: its number, or 0.
        /// </summary>
        /// <remarks>
        /// Dramak's Theatre is the one dungeon that numbers its rooms this way, and it has two runs
        /// of them: the five auditoriums it is fought through -- Dramak in the fourth, the Puppet
        /// Master in the fifth (Dofus pour les noobs, «Théâtre de Dramak») -- and the two backstage
        /// rooms of its secret passage. Unread, the catalogue's list stood, which starts in the
        /// Puppet Master's auditorium and left the fourth as the boss room.
        /// </remarks>
        private static int FigureOf(string name)
        {
            var figure = NumberedRoom.Match(name);
            return figure.Success && int.TryParse(figure.Groups[1].Value, out int number)
                                  && number >= 1 && number <= Ordinals.Length
                ? number
                : 0;
        }

        /// <summary>The room part of a name ending in a figure: « - Auditorium 5».</summary>
        private static readonly System.Text.RegularExpressions.Regex NumberedRoom =
            new(@" - [^-]*\D (\d{1,2})$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// The run a numbered room belongs to: the dungeon's name, and for a room numbered with a
        /// figure its kind too («Dramak's Theatre - Auditorium»), so the backstage rooms are a run
        /// of their own and not a second room 1 and 2 of the auditoriums.
        /// </summary>
        private static string PrefixOf(string name)
        {
            if (FigureOf(name) != 0)
                return name.Substring(0, name.LastIndexOf(' '));
            int dash = name.LastIndexOf(" - ", StringComparison.Ordinal);
            return dash > 0 ? name.Substring(0, dash) : name;
        }

        internal static bool IsExitName(string name)
            => name.EndsWith(" - Exit", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Exit", StringComparison.OrdinalIgnoreCase);

        /// <summary>A map titled only with the dungeon name, no " - …" room qualifier.</summary>
        internal static bool IsBareHub(string name)
            => name.IndexOf(" - ", StringComparison.Ordinal) < 0;

        private static bool LooksLikeBossName(string name)
        {
            string lower = name.ToLowerInvariant();
            if (lower.Contains("throne")) return true;
            if (lower.Contains("fissure")) return true;
            if (lower.Contains("last room")) return true;
            if (lower.Contains(" boss")) return true;
            // "Erzal's Fissure" already matched; "Somebody's Room" without an ordinal.
            if (lower.Contains("'s room") && OrdinalOf(name) == 0) return true;
            return false;
        }

        /// <summary>Rooms by following the corridors, or null when the chain is not a single line.</summary>
        private static List<long>? ByLinks(
            List<long> rooms, Dictionary<long, (long Right, long Bottom, long Left, long Top)> scrolls)
        {
            // Only the forward exits. Reading the backward ones too puts an edge in both directions
            // between every pair of neighbours, and then no room has nothing leading into it: the
            // first attempt at this ordered 31 dungeons instead of 143 for exactly that reason.
            var next = new Dictionary<long, HashSet<long>>();
            var incoming = new Dictionary<long, int>();
            foreach (long room in rooms) incoming[room] = 0;

            foreach (long from in rooms)
            {
                if (!scrolls.TryGetValue(from, out var exits)) continue;

                foreach (var (corridor, back) in new[] { (exits.Right, true), (exits.Bottom, false) })
                {
                    if (corridor == 0) continue;

                    foreach (long to in rooms)
                    {
                        if (to == from || !scrolls.TryGetValue(to, out var entrances)) continue;
                        if ((back ? entrances.Left : entrances.Top) != corridor) continue;

                        if (!next.TryGetValue(from, out var set)) next[from] = set = new HashSet<long>();
                        if (set.Add(to)) incoming[to]++;
                    }
                }
            }

            var starts = rooms.Where(room => incoming[room] == 0).ToList();
            if (starts.Count != 1) return null;

            var chain = new List<long>();
            var seen = new HashSet<long>();
            long? current = starts[0];

            while (current != null && seen.Add(current.Value))
            {
                chain.Add(current.Value);
                current = next.TryGetValue(current.Value, out var forward) && forward.Count == 1
                    ? forward.First()
                    : (long?)null;
            }

            return chain.Count == rooms.Count ? chain : null;
        }

        /// <summary>
        /// The mini-dungeons a person declared, in the same form as the client's: rooms a fight
        /// leads through with no key at their door -- Doctor Nauch's laboratory of «Le fils caché»,
        /// whose rooms the client's dungeon list does not hold.
        /// </summary>
        public const string AuthoredFile = "world/dungeons.json";

        private static bool Read()
        {
            string path = Paths.DungeonsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[DungeonManager] {Path.GetFileName(path)} is not there; " +
                                  "run extract_dungeons.py.");
                return false;
            }
            bool read = Read(path);
            string authored = Paths.ContentFile(AuthoredFile);
            if (File.Exists(authored)) Read(authored, authored: true);
            return read;
        }

        private static bool Read(string path, bool authored = false)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int id)) continue;
                    if (entry.Value.ValueKind != JsonValueKind.Object) continue;

                    var dungeon = new Dungeon
                    {
                        Id = id,
                        Name = Text(entry.Value, "name"),
                        MinLevel = Number(entry.Value, "minLevel"),
                        OptimalLevel = Number(entry.Value, "optimalLevel"),
                        Difficulty = Number(entry.Value, "difficulty"),
                        EntranceMapId = Long(entry.Value, "entrance"),
                        ExitMapId = Long(entry.Value, "exit"),
                        Authored = authored,
                        KeepsADoor = authored && Number(entry.Value, "door") != 0,
                        BossAlone = BossFightsAlone.Contains(id),
                    };

                    if (entry.Value.TryGetProperty("rooms", out var rooms))
                    {
                        foreach (var room in rooms.EnumerateArray()) dungeon.Rooms.Add(room.GetInt64());
                    }
                    if (entry.Value.TryGetProperty("bosses", out var bosses))
                    {
                        foreach (var boss in bosses.EnumerateArray()) dungeon.Bosses.Add(boss.GetInt32());
                    }

                    dungeon.OnKeyring = Number(entry.Value, "keyring") != 0;
                    dungeon.AutoGroup = Number(entry.Value, "autoGroup") != 0;
                    if (entry.Value.TryGetProperty("required", out var required))
                    {
                        foreach (var pair in required.EnumerateArray())
                        {
                            var numbers = pair.EnumerateArray();
                            if (!numbers.MoveNext()) continue;
                            int item = numbers.Current.GetInt32();
                            int count = numbers.MoveNext() ? numbers.Current.GetInt32() : 1;
                            if (item != 0) dungeon.Required.Add((item, Math.Max(1, count)));
                        }
                    }

                    if (entry.Value.TryGetProperty("quarry", out var quarry) && quarry.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var room in quarry.EnumerateObject())
                        {
                            if (!long.TryParse(room.Name, out long map) || room.Value.ValueKind != JsonValueKind.Array) continue;
                            dungeon.WrittenQuarry[map] = room.Value.EnumerateArray()
                                .Where(m => m.TryGetInt32(out _)).Select(m => m.GetInt32()).ToList();
                        }
                    }

                    if (entry.Value.TryGetProperty("roster", out var roster) && roster.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var room in roster.EnumerateObject())
                        {
                            if (!long.TryParse(room.Name, out long map) || room.Value.ValueKind != JsonValueKind.Array) continue;
                            var monsters = room.Value.EnumerateArray()
                                .Where(m => m.TryGetInt32(out _)).Select(m => m.GetInt32()).Where(m => m > 0).ToList();
                            if (monsters.Count > 0) dungeon.WrittenRoster[map] = monsters;
                        }
                    }

                    // A hand-written dungeon with a catalogue id takes that dungeon's place: Ankama
                    // remade Lord Crow's library after the catalogue's rooms were written. Its old
                    // rooms are no longer its rooms.
                    if (_byId.TryGetValue(id, out var replaced))
                    {
                        foreach (long room in replaced.Rooms)
                        {
                            if (_byRoom.TryGetValue(room, out var holders) && holders.Remove(replaced) && holders.Count == 0)
                                _byRoom.Remove(room);
                        }
                    }

                    _byId[id] = dungeon;
                    foreach (long room in dungeon.Rooms)
                    {
                        if (!_byRoom.TryGetValue(room, out var list))
                        {
                            list = new List<Dungeon>();
                            _byRoom[room] = list;
                        }
                        list.Add(dungeon);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DungeonManager] Could not read {Path.GetFileName(path)}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Writes what was read into world.db, replacing whatever was there.</summary>
        private static void Store()
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var transaction = connection.BeginTransaction();

                var clear = connection.CreateCommand();
                clear.CommandText = "DELETE FROM DungeonRooms; DELETE FROM Dungeons;";
                clear.ExecuteNonQuery();

                var dungeon = connection.CreateCommand();
                dungeon.CommandText = @"INSERT INTO Dungeons
                    (Id, Name, MinLevel, OptimalLevel, Difficulty, EntranceMapId, ExitMapId, Bosses)
                    VALUES ($id, $name, $min, $opt, $dif, $in, $out, $bosses);";
                var room = connection.CreateCommand();
                room.CommandText = "INSERT INTO DungeonRooms (DungeonId, Position, MapId) " +
                                   "VALUES ($id, $pos, $map);";

                foreach (var d in _byId.Values)
                {
                    dungeon.Parameters.Clear();
                    dungeon.Parameters.AddWithValue("$id", d.Id);
                    dungeon.Parameters.AddWithValue("$name", d.Name ?? "");
                    dungeon.Parameters.AddWithValue("$min", d.MinLevel);
                    dungeon.Parameters.AddWithValue("$opt", d.OptimalLevel);
                    dungeon.Parameters.AddWithValue("$dif", d.Difficulty);
                    dungeon.Parameters.AddWithValue("$in", d.EntranceMapId);
                    dungeon.Parameters.AddWithValue("$out", d.ExitMapId);
                    dungeon.Parameters.AddWithValue("$bosses", string.Join(",", d.Bosses));
                    dungeon.ExecuteNonQuery();

                    for (int i = 0; i < d.Rooms.Count; i++)
                    {
                        room.Parameters.Clear();
                        room.Parameters.AddWithValue("$id", d.Id);
                        room.Parameters.AddWithValue("$pos", i);
                        room.Parameters.AddWithValue("$map", d.Rooms[i]);
                        room.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DungeonManager] Could not write the dungeons to world.db: {ex.Message}");
            }
        }

        // ─── What it is for ─────────────────────────────────────────────────────

        public static Dungeon? Get(int id) => _byId.TryGetValue(id, out var d) ? d : null;

        /// <summary>The dungeon a map is a room of, or null. When a map belongs to more than one,
        /// the lowest id wins, which is arbitrary and will need the fight to say which it is.</summary>
        public static Dungeon? OfRoom(long mapId)
        {
            if (!_byRoom.TryGetValue(mapId, out var list) || list.Count == 0) return null;

            var best = list[0];
            foreach (var d in list) if (d.Id < best.Id) best = d;
            return best;
        }

        public static bool IsRoom(long mapId) => _byRoom.ContainsKey(mapId);

        /// <summary>Whether a monster is a dungeon's guardian: one of the bosses of any of them.</summary>
        public static bool IsBoss(int monsterId) => _byId.Values.Any(d => d.Bosses.Contains(monsterId));

        /// <summary>
        /// The dungeon whose door is on this map, or null.
        /// </summary>
        /// <remarks>
        /// Built lazily rather than kept as a third index because it is asked once per NPC
        /// conversation and there are 187 of them. When two dungeons share an entrance map the
        /// lowest id wins, the same arbitrary rule <see cref="OfRoom"/> uses and for the same
        /// reason: nothing in the data says which the player meant.
        /// </remarks>
        public static Dungeon? AtEntrance(long mapId)
        {
            if (mapId == 0) return null;

            Dungeon? best = null;
            foreach (var dungeon in _byId.Values)
            {
                if (dungeon.EntranceMapId != mapId) continue;
                // A hand-written one has no door: its NPC's tree takes you in (Doctor Niet's
                // «Entrar en el laboratorio»), and his other replies are his own. Unless it says
                // it keeps one: Lord Crow's library takes its key from Virgil in its first room.
                if (dungeon.Authored && !dungeon.KeepsADoor) continue;
                if (best == null || dungeon.Id < best.Id) best = dungeon;
            }

            return best;
        }

        /// <summary>
        /// The room after this one, or 0 when this is the boss room (or unknown). Hubs and Exit
        /// maps that trail the catalogue after the boss are not walked into after a win.
        /// </summary>
        public static long NextRoom(Dungeon dungeon, long currentMapId)
        {
            if (dungeon == null) return 0;
            if (dungeon.LastRoom != 0 && currentMapId == dungeon.LastRoom) return 0;

            int at = dungeon.Rooms.IndexOf(currentMapId);
            if (at < 0 || at + 1 >= dungeon.Rooms.Count) return 0;

            return dungeon.Rooms[at + 1];
        }

        /// <summary>Where the dungeon lets you out. Falls back to the entrance, which is where
        /// most of them put you: in 152 of the 187 the two are the same map.</summary>
        public static long WayOut(Dungeon dungeon)
        {
            if (dungeon == null) return 0;
            return dungeon.ExitMapId != 0 ? dungeon.ExitMapId : dungeon.EntranceMapId;
        }

        private static string Text(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

        private static int Number(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : 0;

        private static long Long(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.TryGetInt64(out long n) ? n : 0;
    }
}

using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
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

            /// <summary>The last room, where the boss stands. Zero when it has no rooms.</summary>
            public long LastRoom => Rooms.Count == 0 ? 0 : Rooms[^1];

            /// <summary>The room you start in.</summary>
            public long FirstRoom => Rooms.Count == 0 ? 0 : Rooms[0];
        }

        private static readonly Dictionary<int, Dungeon> _byId = new Dictionary<int, Dungeon>();

        /// <summary>Every dungeon a map belongs to. A map can be a room of more than one.</summary>
        private static readonly Dictionary<long, List<Dungeon>> _byRoom = new Dictionary<long, List<Dungeon>>();

        public static IReadOnlyDictionary<int, Dungeon> All => _byId;
        public static bool IsLoaded => _byId.Count > 0;

        public static void Initialize()
        {
            _byId.Clear();
            _byRoom.Clear();

            if (!Read()) return;
            Store();

            int rooms = 0;
            foreach (var dungeon in _byId.Values) rooms += dungeon.Rooms.Count;
            Console.WriteLine($"[DungeonManager] {_byId.Count} dungeons, {rooms} rooms, " +
                              $"{_byRoom.Count} maps that are one.");
        }

        private static bool Read()
        {
            string path = Paths.DungeonsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[DungeonManager] {Path.GetFileName(path)} is not there; " +
                                  "run extract_dungeons.py.");
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int id)) continue;

                    var dungeon = new Dungeon
                    {
                        Id = id,
                        Name = Text(entry.Value, "name"),
                        MinLevel = Number(entry.Value, "minLevel"),
                        OptimalLevel = Number(entry.Value, "optimalLevel"),
                        Difficulty = Number(entry.Value, "difficulty"),
                        EntranceMapId = Long(entry.Value, "entrance"),
                        ExitMapId = Long(entry.Value, "exit"),
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
                if (best == null || dungeon.Id < best.Id) best = dungeon;
            }

            return best;
        }

        /// <summary>
        /// The room after this one, or 0 when this is the last. Follows the order the data gives;
        /// see the warning at the top of the class about what that order is worth.
        /// </summary>
        public static long NextRoom(Dungeon dungeon, long currentMapId)
        {
            if (dungeon == null) return 0;

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

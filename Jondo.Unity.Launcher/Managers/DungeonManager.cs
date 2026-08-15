using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Launcher.Managers
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
    /// Nothing calls this yet. It is the groundwork for moving a player on from one room to the
    /// next after a win, and for putting them at the entrance and at the exit; combat is still on
    /// the previous version of the protocol, so that wiring waits for it.
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

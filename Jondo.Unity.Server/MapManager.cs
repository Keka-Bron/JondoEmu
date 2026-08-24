using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Launcher
{
    public class MapInfo
    {
        public long MapId { get; set; }
        public int PosX { get; set; }
        public int PosY { get; set; }
        public int SubAreaId { get; set; }
        public bool Outdoor { get; set; }
        public string Name { get; set; }
        public long Flags { get; set; }
    }

    public class MapScrollAction
    {
        public long MapId { get; set; }
        public long RightMapId { get; set; }
        public long BottomMapId { get; set; }
        public long LeftMapId { get; set; }
        public long TopMapId { get; set; }
    }

    public static class MapManager
    {
        public static Dictionary<long, MapInfo> Maps = new Dictionary<long, MapInfo>();
        public static Dictionary<long, MapScrollAction> ScrollActions = new Dictionary<long, MapScrollAction>();
        public static Dictionary<long, List<int>> WalkableCells = new Dictionary<long, List<int>>();

        /// <summary>Cells that can be walked on during a fight (mov=1 and nonWalkableDuringFight=0).</summary>
        public static Dictionary<long, HashSet<int>> FightWalkableCells = new Dictionary<long, HashSet<int>>();

        /// <summary>Opaque cells (los=0): they break the line of sight of spells.</summary>
        public static Dictionary<long, HashSet<int>> LosBlockingCells = new Dictionary<long, HashSet<int>>();

        public static void Initialize()
        {
            try
            {
                Maps.Clear();
                ScrollActions.Clear();
                WalkableCells.Clear();
                FightWalkableCells.Clear();
                LosBlockingCells.Clear();

                // Fight data. map_walkable_cells.json is no use here: it trims the map borders on
                // purpose so that mobs can be placed in roleplay, and on top of that it says
                // nothing about which cells block sight. This other file comes from the same place
                // (the client bundles) but keeps the whole map and carries the `los` field.
                string fightJsonPath = Paths.FightCellsJson;
                if (File.Exists(fightJsonPath))
                {
                    try
                    {
                        using var fDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(fightJsonPath));
                        foreach (var prop in fDoc.RootElement.EnumerateObject())
                        {
                            if (!long.TryParse(prop.Name, out long mId)) continue;

                            if (prop.Value.TryGetProperty("f", out var fArr))
                            {
                                var set = new HashSet<int>();
                                foreach (var e in fArr.EnumerateArray()) set.Add(e.GetInt32());
                                if (set.Count > 0) FightWalkableCells[mId] = set;
                            }
                            if (prop.Value.TryGetProperty("b", out var bArr))
                            {
                                var set = new HashSet<int>();
                                foreach (var e in bArr.EnumerateArray()) set.Add(e.GetInt32());
                                if (set.Count > 0) LosBlockingCells[mId] = set;
                            }
                        }
                        Console.WriteLine($"[MapManager] Loaded fight data for {FightWalkableCells.Count} maps ({LosBlockingCells.Count} with opaque cells).");
                    }
                    catch (Exception fex)
                    {
                        Console.WriteLine($"[MapManager] Error loading map_fight_cells.json: {fex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"[MapManager] WARNING: {fightJsonPath} does not exist. No line of sight and no fight walkability.");
                }

                string walkableJsonPath = Paths.WalkableCellsJson;
                if (File.Exists(walkableJsonPath))
                {
                    try
                    {
                        string json = File.ReadAllText(walkableJsonPath);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (long.TryParse(prop.Name, out long mId))
                            {
                                var cellList = new List<int>();
                                foreach (var elem in prop.Value.EnumerateArray())
                                {
                                    cellList.Add(elem.GetInt32());
                                }
                                if (cellList.Count > 0)
                                {
                                    WalkableCells[mId] = cellList;
                                }
                            }
                        }
                        Console.WriteLine($"[MapManager] Loaded walkable cell data for {WalkableCells.Count} maps from map_walkable_cells.json.");
                    }
                    catch (Exception wex)
                    {
                        Console.WriteLine($"[MapManager] Error loading map_walkable_cells.json: {wex.Message}");
                    }
                }

                using (var connection = new SqliteConnection(DatabaseManager.WorldConnectionString))
                {
                    connection.Open();

                    // 1. Load Positions & Flags
                    var infoCommand = connection.CreateCommand();
                    infoCommand.CommandText = "SELECT p.MapId, p.PosX, p.PosY, p.SubAreaId, p.Outdoor, p.Name, t.Data FROM MapPositions p LEFT JOIN MapTemplates t ON p.MapId = t.Id;";
                    int infoCount = 0;
                    using (var reader = infoCommand.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            long mapId = reader.GetInt64(0);
                            int subAreaId = reader.GetInt32(3);

                            long flags = 0;
                            if (!reader.IsDBNull(6))
                            {
                                try
                                {
                                    using var tDoc = System.Text.Json.JsonDocument.Parse(reader.GetString(6));
                                    if (tDoc.RootElement.TryGetProperty("m_flags", out var fElem))
                                    {
                                        flags = fElem.GetInt64();
                                    }
                                }
                                catch { }
                            }

                            var info = new MapInfo
                            {
                                MapId = mapId,
                                PosX = reader.GetInt32(1),
                                PosY = reader.GetInt32(2),
                                SubAreaId = subAreaId,
                                Outdoor = reader.GetInt32(4) == 1,
                                Name = reader.IsDBNull(5) ? "" : reader.GetString(5),
                                Flags = flags
                            };
                            Maps[mapId] = info;
                            infoCount++;
                        }
                    }
                    Console.WriteLine($"[MapManager] Loaded {infoCount} map info records (with flags) from database successfully.");

                    // 2. Load Scrolls
                    var scrollCommand = connection.CreateCommand();
                    scrollCommand.CommandText = "SELECT MapId, RightMapId, BottomMapId, LeftMapId, TopMapId FROM MapScrolls;";
                    int scrollCount = 0;
                    using (var reader = scrollCommand.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            long mapId = reader.GetInt64(0);
                            var action = new MapScrollAction
                            {
                                MapId = mapId,
                                RightMapId = reader.GetInt64(1),
                                BottomMapId = reader.GetInt64(2),
                                LeftMapId = reader.GetInt64(3),
                                TopMapId = reader.GetInt64(4)
                            };
                            ScrollActions[mapId] = action;
                            scrollCount++;
                        }
                    }
                    Console.WriteLine($"[MapManager] Loaded {scrollCount} map scroll action records from database successfully.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MapManager] Error loading maps from database: {ex.Message}");
            }
        }

        public static MapInfo GetMapInfo(long mapId)
        {
            if (Maps.TryGetValue(mapId, out var info))
            {
                return info;
            }
            return null;
        }

        public static long ResolveArenaMapId(long roleplayMapId)
        {
            if (!Maps.TryGetValue(roleplayMapId, out var info)) return roleplayMapId;

            // Do NOT return the roleplay map here for outdoor maps: the reference capture proves
            // the opposite. The Incarnam fight at (-2,-3) -- an OUTDOOR map with real coordinates,
            // 154010883 -- was fought in arena 153891076. Proof: 6 out of 6 fighter positions are
            // walkable in the arena and 0 out of 6 in the roleplay map. With the early return,
            // every open-air fight stayed on the roleplay map: no arena and therefore no scene
            // change and no music.

            var arenas = Maps.Values
                .Where(m => m.SubAreaId == info.SubAreaId
                         && m.PosX == 0 && m.PosY == 0
                         && m.Outdoor == true
                         && string.IsNullOrEmpty(m.Name)
                         && m.Flags == 69262589)
                .Select(m => m.MapId)
                .Where(id => WalkableCells.TryGetValue(id, out var c) && c.Count >= 40)
                .OrderBy(id => id)
                .ToList();

            if (arenas.Count == 0)
            {
                arenas = Maps.Values
                    .Where(m => m.SubAreaId == info.SubAreaId
                             && m.PosX == 0 && m.PosY == 0
                             && m.Flags == 69262589)
                    .Select(m => m.MapId)
                    .Where(id => WalkableCells.TryGetValue(id, out var c) && c.Count >= 40)
                    .OrderBy(id => id)
                    .ToList();
            }

            if (arenas.Count == 0) return roleplayMapId;

            // Pairing by a fixed id offset. There is no single rule for the whole game, but within
            // a given zone the arena id is the roleplay map id plus a small, constant offset.
            // Verified against real data:
            //   Astrub City (y=-18): 191102978 -> 191102984, 191104002 -> 191104008,
            //                        191105026 -> 191105032, 191106050 -> 191106056   (+6)
            //   Tutorial maps                                                          (+4)
            // We try the small offsets in order and keep the first one that lands on a valid arena
            // in the same subarea.
            foreach (int delta in new[] { 4, 6, 2, 8, 10, 12, 14, 16 })
            {
                if (arenas.Contains(roleplayMapId + delta))
                {
                    return roleplayMapId + delta;
                }
            }

            // No recognizable offset: deterministic assignment so that at least the same map
            // always ends up using the same arena.
            return arenas[(int)(Math.Abs(roleplayMapId) % arenas.Count)];
        }

        /// <summary>Cells walkable during a fight; falls back to the roleplay ones when there is no data.</summary>
        public static HashSet<int> GetFightWalkable(long mapId)
        {
            if (FightWalkableCells.TryGetValue(mapId, out var set)) return set;
            if (WalkableCells.TryGetValue(mapId, out var list)) return new HashSet<int>(list);
            return null;
        }

        /// <summary>Cells that break the line of sight; empty if the map has none.</summary>
        public static HashSet<int> GetLosBlockers(long mapId)
            => LosBlockingCells.TryGetValue(mapId, out var set) ? set : null;

        public static MapScrollAction GetScrollAction(long mapId)
        {
            if (ScrollActions.TryGetValue(mapId, out var action))
            {
                return action;
            }
            return null;
        }

        public static bool IsCellWalkable(long mapId, int cellId)
        {
            if (WalkableCells.TryGetValue(mapId, out var cells))
            {
                return cells.Contains(cellId);
            }
            return true;
        }

        public static int GetNearestWalkableCell(long mapId, int targetCellId)
        {
            if (!WalkableCells.TryGetValue(mapId, out var cells) || cells.Count == 0)
            {
                return targetCellId;
            }
            if (cells.Contains(targetCellId))
            {
                return targetCellId;
            }

            int targetRow = targetCellId / 14;
            int targetCol = targetCellId % 14;

            int bestCell = cells[0];
            double minDistance = double.MaxValue;

            foreach (var cell in cells)
            {
                int r = cell / 14;
                int c = cell % 14;
                double dist = Math.Pow(r - targetRow, 2) + Math.Pow(c - targetCol, 2);
                if (dist < minDistance)
                {
                    minDistance = dist;
                    bestCell = cell;
                }
            }
            return bestCell;
        }
    }
}

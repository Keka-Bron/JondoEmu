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

        public static void Initialize()
        {
            try
            {
                Maps.Clear();
                ScrollActions.Clear();
                WalkableCells.Clear();

                string walkableJsonPath = @"C:\Jondo\map_walkable_cells.json";
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

                    // 1. Load Positions
                    var infoCommand = connection.CreateCommand();
                    infoCommand.CommandText = "SELECT MapId, PosX, PosY, SubAreaId, Outdoor, Name FROM MapPositions;";
                    int infoCount = 0;
                    using (var reader = infoCommand.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            long mapId = reader.GetInt64(0);
                            int subAreaId = reader.GetInt32(3);
                            if (subAreaId == 444)
                            {
                                subAreaId = 20663;
                            }
                            var info = new MapInfo
                            {
                                MapId = mapId,
                                PosX = reader.GetInt32(1),
                                PosY = reader.GetInt32(2),
                                SubAreaId = subAreaId,
                                Outdoor = reader.GetInt32(4) == 1,
                                Name = reader.IsDBNull(5) ? "" : reader.GetString(5)
                            };
                            Maps[mapId] = info;
                            infoCount++;
                        }
                    }
                    Console.WriteLine($"[MapManager] Loaded {infoCount} map info records from database successfully.");

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

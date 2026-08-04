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

        /// <summary>Casillas pisables en combate (mov=1 y nonWalkableDuringFight=0).</summary>
        public static Dictionary<long, HashSet<int>> FightWalkableCells = new Dictionary<long, HashSet<int>>();

        /// <summary>Casillas opacas (los=0): cortan la línea de visión de los hechizos.</summary>
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

                // Datos de combate. map_walkable_cells.json no sirve aquí: recorta los bordes del
                // mapa a propósito para colocar mobs en roleplay, y además no dice nada de qué
                // casillas tapan la visión. Este otro fichero sale del mismo sitio (los bundles
                // del cliente) pero conserva el mapa entero y trae el campo `los`.
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
                        Console.WriteLine($"[MapManager] Datos de combate cargados para {FightWalkableCells.Count} mapas ({LosBlockingCells.Count} con casillas opacas).");
                    }
                    catch (Exception fex)
                    {
                        Console.WriteLine($"[MapManager] Error cargando map_fight_cells.json: {fex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"[MapManager] AVISO: no existe {fightJsonPath}. Sin línea de visión ni transitabilidad de combate.");
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
                            if (subAreaId == 444)
                            {
                                subAreaId = 20663;
                            }

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

            // NO devolver aquí el mapa de roleplay para mapas exteriores: la captura de referencia
            // demuestra lo contrario. El combate de Incarnam (-2,-3) —un mapa EXTERIOR con
            // coordenadas reales, 154010883— se libró en la arena 153891076. Prueba: 6 de 6
            // posiciones de luchadores son transitables en la arena y 0 de 6 en el mapa de
            // roleplay. Con la salida anticipada, todos los combates al aire libre se quedaban
            // en el mapa de roleplay: sin arena y, por tanto, sin cambio de escena ni música.

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

            // Emparejamiento por desplazamiento fijo del id. No hay una regla única para todo el
            // juego, pero dentro de cada zona el id de la arena es el del mapa de roleplay más un
            // desplazamiento pequeño y constante. Verificado con datos reales:
            //   Ciudad de Astrub (y=-18): 191102978→191102984, 191104002→191104008,
            //                             191105026→191105032, 191106050→191106056   (+6)
            //   Mapas del tutorial                                                    (+4)
            // Probamos los desplazamientos pequeños en orden y nos quedamos con el primero que
            // caiga en una arena válida de la misma subárea.
            foreach (int delta in new[] { 4, 6, 2, 8, 10, 12, 14, 16 })
            {
                if (arenas.Contains(roleplayMapId + delta))
                {
                    return roleplayMapId + delta;
                }
            }

            // Sin desplazamiento reconocible: reparto determinista para que al menos el mismo
            // mapa use siempre la misma arena.
            return arenas[(int)(Math.Abs(roleplayMapId) % arenas.Count)];
        }

        /// <summary>Casillas pisables en combate; si no hay datos, cae en las de roleplay.</summary>
        public static HashSet<int> GetFightWalkable(long mapId)
        {
            if (FightWalkableCells.TryGetValue(mapId, out var set)) return set;
            if (WalkableCells.TryGetValue(mapId, out var list)) return new HashSet<int>(list);
            return null;
        }

        /// <summary>Casillas que cortan la línea de visión; vacío si el mapa no tiene ninguna.</summary>
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

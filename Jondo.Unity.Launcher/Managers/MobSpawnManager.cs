using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Linq;

namespace Jondo.Unity.Launcher.Managers
{
    public static class MobSpawnManager
    {
        public class MonsterGrade
        {
            public int Level { get; set; }
        }

        public class MonsterData
        {
            public int Id { get; set; }
            public int NameId { get; set; }
            public string Look { get; set; }
            public List<MonsterGrade> Grades { get; set; } = new List<MonsterGrade>();
        }

        public class MobMember
        {
            public MonsterData Monster { get; set; }
            public int GradeIndex { get; set; }

            /// <summary>Cuántos grados acepta el cliente: del 1 al 5, ni uno más.</summary>
            public const int MaxGradesPerMonster = 5;
            public int Level { get; set; }
        }

        public class MobGroup
        {
            public long MobId { get; set; }
            public int CellId { get; set; }
            public List<MobMember> Members { get; set; } = new List<MobMember>();
        }

        private static Dictionary<int, MonsterData> _monsters = new Dictionary<int, MonsterData>();
        private static Dictionary<long, List<MobGroup>> _mapMobs = new Dictionary<long, List<MobGroup>>();

        public static void InitializeAndSpawnAll()
        {
            Console.WriteLine("[MobSpawnManager] Loading data from SQLite...");
            
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();

            DatabaseManager.EnsureMobsSeeded(connection);

            // Load Monsters
            var cmdMonsters = connection.CreateCommand();
            cmdMonsters.CommandText = "SELECT Id, NameId, Look, Grades FROM Monsters;";
            using (var reader = cmdMonsters.ExecuteReader())
            {
                while (reader.Read())
                {
                    var id = reader.GetInt32(0);
                    var data = new MonsterData {
                        Id = id,
                        NameId = reader.GetInt32(1),
                        Look = reader.GetString(2)
                    };
                    string gradesJson = reader.GetString(3);
                    try {
                        using var doc = System.Text.Json.JsonDocument.Parse(gradesJson);
                        var root = doc.RootElement;
                        if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("Array", out var arrProp))
                        {
                            root = arrProp;
                        }
                        if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach(var g in root.EnumerateArray()) {
                                int lvl = g.TryGetProperty("level", out var l) ? l.GetInt32() : 1;
                                data.Grades.Add(new MonsterGrade { Level = lvl });
                            }
                        }
                    } catch {}
                    _monsters[id] = data;
                }
            }

            _mapMobs.Clear();

            // Load MapMobs from SQLite database
            // Who is an archmonster and where each one belongs. It has to be known before the
            // groups are read, because they are thinned as they come in.
            Archimonsters.Initialize(connection);

            var cmdMapMobs = connection.CreateCommand();
            cmdMapMobs.CommandText = "SELECT MapId, MobId, CellId, MembersJson FROM MapMobs ORDER BY MapId, MobId;";
            int count = 0;
            int archmonsters = 0;
            using (var reader = cmdMapMobs.ExecuteReader())
            {
                while (reader.Read())
                {
                    long mapId = reader.GetInt64(0);
                    long mobId = reader.GetInt64(1);
                    int cellId = reader.GetInt32(2);
                    string membersJson = reader.GetString(3);

                    var group = new MobGroup {
                        MobId = mobId,
                        CellId = cellId
                    };

                    try {
                        using var doc = System.Text.Json.JsonDocument.Parse(membersJson);
                        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            var ids = new List<int>();
                            var grades = new List<int>();
                            var levels = new List<int>();
                            foreach(var m in doc.RootElement.EnumerateArray()) {
                                ids.Add(m.GetProperty("id").GetInt32());
                                grades.Add(m.GetProperty("grade").GetInt32());
                                levels.Add(m.GetProperty("level").GetInt32());
                            }

                            // The database ships four groups in ten holding an archmonster, up to
                            // eight in one group. This thins them to the rules — one per group,
                            // one per map, one in ten, and one of each per zone — swapping the
                            // ones that do not stay for the ordinary monster they are the rare
                            // version of, so the group keeps its size.
                            if (Archimonsters.Thin(mapId, mobId, ids) != 0) archmonsters++;

                            for (int i = 0; i < ids.Count; i++) {
                                if (_monsters.TryGetValue(ids[i], out var mData)) {
                                    // Lo mismo que arriba: la base de datos guarda grados que el
                                    // cliente no acepta, y hay que recortarlos aquí también.
                                    int grade = Math.Clamp(grades[i], 0, MobMember.MaxGradesPerMonster - 1);
                                    group.Members.Add(new MobMember {
                                        Monster = mData,
                                        GradeIndex = grade,
                                        Level = grade == grades[i] || grade >= mData.Grades.Count
                                            ? levels[i]
                                            : mData.Grades[grade].Level
                                    });
                                }
                            }
                        }
                    } catch {}

                    if (!_mapMobs.ContainsKey(mapId))
                        _mapMobs[mapId] = new List<MobGroup>();
                    _mapMobs[mapId].Add(group);
                    count++;
                }
            }

            Console.WriteLine($"[MobSpawnManager] Loaded {count} persistent mobs across {_mapMobs.Count} maps from database.");
            Console.WriteLine($"[MobSpawnManager] {archmonsters} groups keep an archmonster " +
                              $"({100.0 * archmonsters / Math.Max(1, count):0.0}% of them), one per map and one per zone.");
        }

        private static long _nextDynamicMobId = -2000000;
        private static Random _rand = new Random();

        public static List<MobGroup> GetMobsForMap(long mapId)
        {
            if (_mapMobs.TryGetValue(mapId, out var mobs) && mobs.Count > 0)
                return mobs;

            mobs = GenerateDynamicMobsForMap(mapId);
            _mapMobs[mapId] = mobs;
            return mobs;
        }

        /// <summary>
        /// Qué monstruos pueden salir en un mapa que no tiene grupos escritos.
        ///
        /// Los de su zona, y nadie más. Antes esto devolvía una lista fija de pios —491, 492, 493,
        /// 463 y los 234x— para cualquier mapa del mundo, así que al pie de la torre de la clepsidra
        /// de Frigost, que no tiene grupos en la tabla, salían pios de Astrub. Y dentro del
        /// merkasako también.
        ///
        /// La zona se sabe por la subzona del mapa, y lo que vive en ella por los grupos que sí
        /// están escritos en los otros mapas de esa misma subzona: 12.907 mapas los tienen, así que
        /// casi siempre hay de dónde sacarlo. Si no lo hay, no sale nadie, que es mejor que sacar a
        /// quien no toca.
        /// </summary>
        private static List<int> GetSpawnableMonsterIds(long mapId)
        {
            var map = MapManager.GetMapInfo(mapId);
            if (map == null) return new List<int>();

            if (_bySubArea.TryGetValue(map.SubAreaId, out var vecinos)) return vecinos;

            var salida = new List<int>();
            foreach (var otro in _mapMobs)
            {
                var suyo = MapManager.GetMapInfo(otro.Key);
                if (suyo == null || suyo.SubAreaId != map.SubAreaId) continue;

                foreach (var grupo in otro.Value)
                {
                    foreach (var miembro in grupo.Members)
                    {
                        if (miembro.Monster != null && !salida.Contains(miembro.Monster.Id))
                        {
                            salida.Add(miembro.Monster.Id);
                        }
                    }
                }
            }

            _bySubArea[map.SubAreaId] = salida;
            return salida;
        }

        /// <summary>Los monstruos de cada subzona, que se calculan una vez y se guardan.</summary>
        private static readonly Dictionary<int, List<int>> _bySubArea = new Dictionary<int, List<int>>();

        private static MobGroup? BuildRandomGroup(long mapId, List<int> availableMonsters, List<int> validCells, HashSet<int> usedCells)
        {
            if (availableMonsters.Count == 0 || validCells.Count == 0) return null;

            int cellId = validCells[_rand.Next(validCells.Count)];
            int attempts = 0;
            while (usedCells.Contains(cellId) && attempts++ < validCells.Count)
            {
                cellId = validCells[_rand.Next(validCells.Count)];
            }
            if (usedCells.Contains(cellId)) return null;
            usedCells.Add(cellId);

            var group = new MobGroup
            {
                MobId = _nextDynamicMobId--,
                CellId = cellId
            };

            int groupSize = _rand.Next(1, 9); // 1 to 8 monsters, just like in Dofus
            for (int m = 0; m < groupSize; m++)
            {
                int monsterId = availableMonsters[_rand.Next(availableMonsters.Count)];
                var mData = _monsters[monsterId];
                int gradeIdx = 0;
                int lvl = 1;
                if (mData.Grades.Count > 0)
                {
                    // Solo los cinco primeros. El grado que viaja al cliente va de 1 a 5 y ningún
                    // monstruo de las capturas reales pasa de ahí, pero nuestros datos traen
                    // monstruos con seis, diez y hasta veinte grados. Elegir uno de los de más
                    // arriba mandaba un grado que el cliente no sabe resolver, y ese grupo se
                    // quedaba sin información al pasarle el ratón.
                    gradeIdx = _rand.Next(Math.Min(mData.Grades.Count, MobMember.MaxGradesPerMonster));
                    lvl = mData.Grades[gradeIdx].Level;
                }

                group.Members.Add(new MobMember
                {
                    Monster = mData,
                    GradeIndex = gradeIdx,
                    Level = lvl
                });
            }

            return group;
        }

        private static List<MobGroup> GenerateDynamicMobsForMap(long mapId)
        {
            var result = new List<MobGroup>();
            if (_monsters.Count == 0) return result;

            // En el merkasako no se pelea con nadie: es la casa de uno.
            if (Merkasako.IsHavenBag(mapId)) return result;

            var availableMonsters = GetSpawnableMonsterIds(mapId);
            var validCells = GetInnerWalkableCells(mapId);
            var usedCells = new HashSet<int>();

            int numMobs = _rand.Next(2, 5); // 2 to 4 groups per map
            for (int i = 0; i < numMobs; i++)
            {
                var g = BuildRandomGroup(mapId, availableMonsters, validCells, usedCells);
                if (g != null) result.Add(g);
            }

            return result;
        }

        /// <summary>
        /// Restocks one monster group on the map after the player has defeated another one. It
        /// leaves the existing groups untouched: it only adds a new one on a free cell, using the
        /// same generator that populates a map for the first time.
        /// Returns null if there was no room left.
        /// </summary>
        public static MobGroup? RespawnOneGroup(long mapId)
        {
            if (_monsters.Count == 0) return null;

            if (!_mapMobs.TryGetValue(mapId, out var mobs))
            {
                mobs = new List<MobGroup>();
                _mapMobs[mapId] = mobs;
            }

            var usedCells = new HashSet<int>(mobs.Select(m => m.CellId));
            var group = BuildRandomGroup(mapId, GetSpawnableMonsterIds(mapId), GetInnerWalkableCells(mapId), usedCells);
            if (group == null) return null;

            mobs.Add(group);
            return group;
        }

        public static List<int> GetInnerWalkableCells(long mapId)
        {
            if (!MapManager.WalkableCells.TryGetValue(mapId, out var cells) || cells.Count == 0)
            {
                return new List<int> { 288, 303, 312, 327, 344, 350 };
            }

            var cellSet = new HashSet<int>(cells);
            var innerCells = new List<int>();

            // Offsets for radius 1 and radius 2 surrounding cells (12 neighbor cells total)
            int[] radiusOffsets = new int[]
            {
                -14, 14, -1, 1,          // Radius 1
                -28, 28, -2, 2,          // Radius 2 orthogonal
                -15, -13, 13, 15         // Radius 2 diagonal
            };

            foreach (var cell in cells)
            {
                int row = cell / 14;
                int col = cell % 14;

                // Exclude map borders
                if (row < 8 || row > 28 || col < 2 || col > 11) continue;

                // Verify all 12 cells in a radius of 2 steps are 100% walkable
                bool allWalkable = true;
                foreach (int offset in radiusOffsets)
                {
                    if (!cellSet.Contains(cell + offset))
                    {
                        allWalkable = false;
                        break;
                    }
                }

                if (allWalkable)
                {
                    innerCells.Add(cell);
                }
            }

            return innerCells.Count > 0 ? innerCells : cells;
        }

        /// <summary>
        /// Returns the MobGroup occupying the specified cell on the given map, or null if no mob is there.
        /// Uses a proximity check (±1 cell) to account for pathfinding rounding.
        /// </summary>
        public static MobGroup? GetMobAtCell(long mapId, int cellId)
        {
            if (!_mapMobs.TryGetValue(mapId, out var mobs)) return null;
            // Exact match first
            var exact = mobs.FirstOrDefault(m => m.CellId == cellId);
            if (exact != null) return exact;
            // Proximity check: adjacent cells (±1, ±14)
            return mobs.FirstOrDefault(m =>
                Math.Abs(m.CellId - cellId) == 1 ||
                Math.Abs(m.CellId - cellId) == 14);
        }

        /// <summary>
        /// Removes a mob group from the map after it is defeated in combat.
        /// </summary>
        public static void RemoveMobGroup(long mapId, long mobId)
        {
            if (_mapMobs.TryGetValue(mapId, out var mobs))
            {
                mobs.RemoveAll(m => m.MobId == mobId);
            }
        }

        public static MobGroup? GetMobGroupById(long mobId)
        {
            foreach (var list in _mapMobs.Values)
            {
                var found = list.FirstOrDefault(m => m.MobId == mobId);
                if (found != null) return found;
            }
            return null;
        }

        public static MonsterData? GetMonsterData(int monsterId)
        {
            return _monsters.TryGetValue(monsterId, out var data) ? data : null;
        }
    }
}


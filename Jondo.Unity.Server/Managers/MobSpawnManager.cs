using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Linq;

namespace Jondo.Unity.Server.Managers
{
    public static class MobSpawnManager
    {
        /// <summary>
        /// The maps where a monster is NOT placed, however much the table has them.
        ///
        /// They are seen while playing: pios inside a house, inside a bank, inside a shop and
        /// standing on top of the village zaap. Measured over the 38,744 placed groups: 9,331
        /// —24.1 %— are on an indoor map.
        ///
        /// The rule is two lists and one exception, and the exception is the important one:
        ///
        ///   INDOORS         MapPositions.Outdoor = 0, which is 4,165 maps
        ///   WITH A ZAAP     the 62 of the travel point catalogue; 53 had monsters on top
        ///   EXCEPT DUNGEON  753 of the 763 dungeon rooms are marked «indoors»
        ///
        /// Without that exception, banning indoors WOULD EMPTY THE WHOLE DUNGEONS: they are 2,290
        /// groups, and a dungeon without creatures is not a dungeon. With it, 7,214 groups are removed
        /// (18.6 %) spread over 2,393 maps, and the 763 rooms stay as they are.
        ///
        /// It is filtered ON LOADING and not by deleting rows from the database on purpose: world.db is regenerated and
        /// distributed compressed, so a deletion would be lost in the next regeneration and
        /// one would have to remember to repeat it. With this there is nothing to remember.
        /// </summary>
        /// <summary>The banned maps, kept so that the repopulator respects them too.</summary>
        private static HashSet<long> _vetados = new HashSet<long>();

        private static HashSet<long> MapasSinMonstruos(Microsoft.Data.Sqlite.SqliteConnection connection)
        {
            var vetados = new HashSet<long>();
            var salas = new HashSet<long>();

            void Recoger(string sql, HashSet<long> donde)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) donde.Add(reader.GetInt64(0));
            }

            try
            {
                Recoger("SELECT MapId FROM DungeonRooms;", salas);
                Recoger("SELECT MapId FROM MapPositions WHERE Outdoor = 0;", vetados);
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[MobSpawnManager] No se ha podido leer dónde no van monstruos: {ex.Message}");
                return new HashSet<long>();
            }

            // The zaaps come from the travel point catalogue. The file is read here instead of
            // asking Interactives because that one is initialised AFTER the spawner, and
            // asking it now would return an empty list without giving any error.
            try
            {
                string ruta = Paths.WaypointsJson;
                if (File.Exists(ruta))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ruta));
                    foreach (var entrada in doc.RootElement.EnumerateArray())
                    {
                        if (entrada.TryGetProperty("mapId", out var m) && m.TryGetInt64(out long mapa))
                        {
                            vetados.Add(mapa);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[MobSpawnManager] No he podido leer los zaaps: {ex.Message}");
            }

            // And the dungeon rules over everything else.
            vetados.ExceptWith(salas);
            return vetados;
        }

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

            /// <summary>How many grades are handed out to generated groups: 1 to 5, not one more.</summary>
            public const int MaxGradesPerMonster = 5;

            /// <summary>
            /// And up to the sixth for a group written or composed by hand, if the monster has it.
            /// </summary>
            /// <remarks>
            /// Measured: the kanojedo's level 200 Puch Ingball travels as <c>f2=200 f4=6</c> in
            /// two captures and the client draws it and lets it be inspected. The cap of five was measured on
            /// wild groups, which never went beyond that; it is not that the sixth does not exist, it is that
            /// it had not been seen.
            /// </remarks>
            public const int MaxWrittenGrades = 6;
            public int Level { get; set; }
        }

        public class MobGroup
        {
            public long MobId { get; set; }
            public int CellId { get; set; }

            /// <summary>Which way it faces, 1 to 7. Generated ones face south-east.</summary>
            public int Orientation { get; set; } = 1;

            public List<MobMember> Members { get; set; } = new List<MobMember>();

            /// <summary>
            /// A dungeon room's group: its eight in a fixed order, of which a fight takes the first
            /// clamp(fighters, 4, 8) -- see <see cref="MembersFor"/>. Drawn with its alternatives.
            /// </summary>
            public bool Modular { get; set; }
        }

        private static Dictionary<int, MonsterData> _monsters = new Dictionary<int, MonsterData>();

        /// <summary>Every monster the server knows, with its grades: what the admin window lists levels from.</summary>
        public static IReadOnlyCollection<MonsterData> AllMonsters => _monsters.Values;
        private static Dictionary<long, List<MobGroup>> _mapMobs = new Dictionary<long, List<MobGroup>>();

        /// <summary>
        /// The lock on the groups per map.
        ///
        /// <see cref="_mapMobs"/> is a bare Dictionary and it is touched from each player's thread:
        /// two entering at once maps with no written groups both did a <c>_mapMobs[id] =</c>
        /// on the same table, which is how a Dictionary really breaks —an infinite loop
        /// inside .NET itself, not an exception—. With one player it was never noticed.
        ///
        /// This is NOT the shared monsters phase: marking a group as busy when it is already in a
        /// fight is still missing. It is just that handing out ids touches this same
        /// dictionary and leaving it without a lock would make it worse.
        /// </summary>
        private static readonly object _candado = new object();

        /// <summary>
        /// Whether this is one of the maps where no monster may be planted.
        /// </summary>
        /// <remarks>
        /// Indoors, and on top of a zaap. 3,472 maps, and 7,214 groups from the database are
        /// dropped for them at boot. Public because the answer is worth being able to ask: the bug
        /// this exposes was that two different places had to agree about it and only one did.
        /// </remarks>
        public static bool IsVetoed(long mapId) => _vetados.Contains(mapId);

        /// <summary>How many maps are vetoed. Zero before the world is loaded.</summary>
        public static int VetoedCount => _vetados.Count;

        /// <summary>The monsters and their grades, from the Monsters table.</summary>
        private static void LoadMonsterData(SqliteConnection connection)
        {
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
        }

        /// <summary>
        /// For tests: the monsters without the world -- what composing a group needs, without
        /// reading and spawning 38,744 groups.
        /// </summary>
        internal static void EnsureMonsterData()
        {
            if (_monsters.Count > 0) return;
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            LoadMonsterData(connection);
        }

        public static void InitializeAndSpawnAll()
        {
            Console.WriteLine("[MobSpawnManager] Loading data from SQLite...");
            
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();

            DatabaseManager.EnsureMobsSeeded(connection);

            LoadMonsterData(connection);

            _mapMobs.Clear();

            // Load MapMobs from SQLite database
            // Who is an archmonster and where each one belongs. It has to be known before the
            // groups are read, because they are thinned as they come in.
            Archimonsters.Initialize(connection);

            // Who lives in each subarea, which is who a dungeon room is made of.
            _subareaRosters = LoadSubareaRosters(connection);

            // And where a monster is NOT placed however much the table says so.
            _vetados = MapasSinMonstruos(connection);
            var vetados = _vetados;

            var cmdMapMobs = connection.CreateCommand();
            cmdMapMobs.CommandText = "SELECT MapId, MobId, CellId, MembersJson FROM MapMobs ORDER BY MapId, MobId;";
            int count = 0;
            int archmonsters = 0;
            int bajoTecho = 0;
            using (var reader = cmdMapMobs.ExecuteReader())
            {
                while (reader.Read())
                {
                    long mapId = reader.GetInt64(0);

                    // Not in a house, not in a bank, not in a shop, not on top of a zaap.
                    if (vetados.Contains(mapId)) { bajoTecho++; continue; }

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
                                    // Same as above: the database stores grades the
                                    // client does not accept, and they have to be trimmed here too.
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

            // The dungeon bosses, before what is written by hand so that a person can
            // move them or remove them.
            int jefes = ComposeDungeonRooms();

            // And whatever a person has decided, on top of everything before.
            var deLaMano = AplicarLosEscritos();

            // The written groups bring their id set from the seeding. The dispenser has to
            // step below the lowest of all of them before handing out its first one, or the
            // first group generated on the fly would take a number already taken on another
            // map —and then GetMobGroupById would return the wrong one—. The hand-placed ones
            // count the same: their ids start at -2,000,000 and are the lowest of all.
            long menor = ActorIds.PrimerMonstruo;
            foreach (var lista in _mapMobs.Values)
            {
                foreach (var grupo in lista)
                {
                    if (grupo.MobId < menor) menor = grupo.MobId;
                }
            }
            ActorIds.ReservarMonstruosHasta(menor);

            Console.WriteLine($"[MobSpawnManager] Loaded {count} persistent mobs across {_mapMobs.Count} maps from database.");
            if (bajoTecho > 0)
            {
                Console.WriteLine($"[MobSpawnManager] {bajoTecho} grupos descartados por estar bajo " +
                                  $"techo o encima de un zaap; {vetados.Count} mapas vetados.");
            }
            Console.WriteLine($"[MobSpawnManager] Ids de grupo repartidos hasta el {menor}; " +
                              "los que se generen al vuelo siguen por debajo.");
            Console.WriteLine($"[MobSpawnManager] {archmonsters} groups keep an archmonster " +
                              $"({100.0 * archmonsters / Math.Max(1, count):0.0}% of them), one per map and one per zone.");
            // The two numbers separately, not the subtraction. With one group placed and another removed the
            // subtraction gives zero and the line does not come out, which is exactly the start where it is most needed
            // to see that content/ has touched something.
            if (deLaMano.Puestos != 0 || deLaMano.Quitados != 0)
            {
                Console.WriteLine($"[MobSpawnManager] Desde content/: {deLaMano.Puestos} grupo(s) " +
                                  $"puestos a mano y {deLaMano.Quitados} quitados.");
            }
        }

        /// <summary>Where the boss groups are numbered from. Below the hand-written ones.</summary>
        private const long PrimerJefe = -3_000_000;

        /// <summary>
        /// Where the groups of the dungeon rooms that had none in the base are numbered from: past
        /// the bosses' stretch and above the missions', so no two ways of handing out ids meet.
        /// </summary>
        private const long FirstComposedRoom = -3_500_000;

        /// <summary>The members a dungeon room's group has: one leader and seven, as in the capture.</summary>
        public const int DungeonGroupSize = 8;

        /// <summary>The fewest monsters a dungeon room fights with: four, whoever comes in alone.</summary>
        public const int DungeonMinimum = 4;

        /// <summary>
        /// Every dungeon room with its group, composed once, the way the jalatós capture shows it.
        /// </summary>
        /// <remarks>
        /// Measured in <c>Mazmorras/mazmorra de los jalatós completa</c>, a Sacrier alone through
        /// the five rooms of dungeon 1:
        ///
        /// <list type="bullet">
        /// <item>each room has ONE group of eight -- a leader and seven -- and its jss carries the
        /// ones a team fights, by team size: 4 for 1 player (and up to 4), then 5, 6, 7 and 8,
        /// each the first N of the eight;</item>
        /// <item>the first four are four different monsters; in the boss room the boss leads and
        /// is there once;</item>
        /// <item>every monster of room k is at grade k: 1 to 5 over the five rooms, the boss at 5
        /// with the others;</item>
        /// <item>the solo player fought four in every room, the boss's included -- the player and
        /// fighters -1 to -4 in the jxg and the jzu.</item>
        /// </list>
        ///
        /// Who is in them is the dungeon's own: <c>Subareas.Monsters</c> of the room's subarea,
        /// the dungeon's bosses taken out, which for dungeon 1 is exactly the capture's
        /// {149, 134, 101, 148, 4822}. The base's MapMobs for these maps are the subarea's generic
        /// background -- two to four groups of one to eight, drawn at random -- and a room made of
        /// them was what left a solo player facing one monster in 574 of the 763 rooms.
        ///
        /// Runs BEFORE the layer written by hand, so a person can still move or remove a group
        /// from the editor without touching code.
        /// </remarks>
        private static int ComposeDungeonRooms()
        {
            if (!DungeonManager.IsLoaded) return 0;

            int composed = 0, bosses = 0, empty = 0;
            foreach (var dungeon in DungeonManager.All.Values)
            {
                if (dungeon.Rooms.Count == 0) continue;

                // What the base put in its rooms, for a room whose subarea lists nobody.
                var fallback = new List<int>();
                foreach (long room in dungeon.Rooms)
                {
                    if (!_mapMobs.TryGetValue(room, out var groups)) continue;
                    foreach (var member in groups.SelectMany(g => g.Members))
                        if (member.Monster != null && !fallback.Contains(member.Monster.Id)) fallback.Add(member.Monster.Id);
                }

                for (int index = 0; index < dungeon.Rooms.Count; index++)
                {
                    long room = dungeon.Rooms[index];
                    _mapMobs.TryGetValue(room, out var before);
                    bool bossRoom = room == dungeon.LastRoom && dungeon.Bosses.Count > 0;
                    long id = bossRoom ? PrimerJefe - bosses
                            : before?.FirstOrDefault()?.MobId ?? FirstComposedRoom - composed;

                    var group = ComposeDungeonRoom(dungeon, index, fallback, id, before?.FirstOrDefault()?.CellId);
                    if (group == null) { empty++; continue; }

                    _mapMobs[room] = new List<MobGroup> { group };
                    composed++;
                    if (bossRoom) bosses++;
                }
            }

            Console.WriteLine($"[Mazmorra] {composed} salas compuestas a {DungeonGroupSize} ({bosses} con su jefe); " +
                              $"{empty} sin nadie que poner.");
            return bosses;
        }

        /// <summary>
        /// A dungeon room's group: its eight, from the dungeon's own monsters, the boss leading in
        /// the boss room. Null when there is nobody to put in it.
        /// </summary>
        private static MobGroup? ComposeDungeonRoom(DungeonManager.Dungeon dungeon, int index,
                                                    IReadOnlyList<int> fallback, long id, int? cell)
        {
            long room = dungeon.Rooms[index];
            var bosses = new HashSet<int>(dungeon.Bosses);
            bool bossRoom = room == dungeon.LastRoom && dungeon.Bosses.Count > 0;

            var roster = RosterOf(room).Where(m => !bosses.Contains(m)).ToList();
            if (roster.Count == 0) roster = fallback.Where(m => !bosses.Contains(m)).ToList();

            var members = ComposeRoom(roster, bossRoom ? dungeon.Bosses : Array.Empty<int>(),
                                      index, dungeon.Rooms.Count, room);
            if (members.Count == 0) return null;

            return new MobGroup
            {
                MobId = id,
                CellId = cell ?? MapManager.GetNearestWalkableCell(room, Handlers.TeleportHandler.MapCentre),
                Members = members,
                Modular = true,
            };
        }

        /// <summary>
        /// The eight of a dungeon room, in their order: the bosses first, at their top grade and
        /// never twice; then every monster of the roster once, so the four a solo player fights are
        /// four different ones; then the roster again, drawn, up to eight. All but the bosses at
        /// the room's grade. Seeded by the map, so a room is the same each time it is composed.
        /// </summary>
        internal static List<MobMember> ComposeRoom(IReadOnlyList<int> roster, IReadOnlyList<int> bosses,
                                                    int room, int rooms, long seed)
        {
            var dice = new Random(unchecked((int)(seed ^ (seed >> 32))));
            var members = new List<MobMember>();

            foreach (int boss in bosses.Distinct())
            {
                if (members.Count >= DungeonGroupSize) break;
                if (!_monsters.TryGetValue(boss, out var data) || data.Grades.Count == 0)
                {
                    Console.WriteLine($"[Mazmorra] El jefe {boss} no está en la base.");
                    continue;
                }
                members.Add(MemberAt(data, int.MaxValue));
            }

            var species = roster.Distinct().Where(m => _monsters.TryGetValue(m, out var d) && d.Grades.Count > 0)
                                .OrderBy(m => m).ToList();
            var order = species.OrderBy(_ => dice.Next()).ToList();
            foreach (int monster in order)
            {
                if (members.Count >= DungeonGroupSize) break;
                members.Add(MemberAt(_monsters[monster], RoomGrade(room, rooms)));
            }
            while (members.Count < DungeonGroupSize && order.Count > 0)
                members.Add(MemberAt(_monsters[order[dice.Next(order.Count)]], RoomGrade(room, rooms)));

            return members;
        }

        /// <summary>
        /// The grade of a room's monsters, 0-based: the rooms spread over the five grades so the
        /// first is at the lowest and the last at the highest -- rooms 1 to 5 at grades 1 to 5 in
        /// the capture. A dungeon of one room is all top grade. How Ankama spreads the rooms of a
        /// dungeon with more or fewer than five is not measured; this is the linear reading.
        /// </summary>
        internal static int RoomGrade(int room, int rooms)
        {
            int top = MobMember.MaxGradesPerMonster - 1;
            if (rooms <= 1) return top;
            return (int)Math.Round((double)Math.Clamp(room, 0, rooms - 1) * top / (rooms - 1), MidpointRounding.AwayFromZero);
        }

        /// <summary>A monster at a grade, brought down to the ones it has; its level from that grade.</summary>
        private static MobMember MemberAt(MonsterData data, int grade)
        {
            int index = Math.Clamp(grade, 0, Math.Min(data.Grades.Count, MobMember.MaxGradesPerMonster) - 1);
            return new MobMember { Monster = data, GradeIndex = index, Level = data.Grades[index].Level };
        }

        /// <summary>The monsters of a map's subarea, as <c>Subareas.Monsters</c> lists them.</summary>
        private static IReadOnlyList<int> RosterOf(long mapId)
        {
            var info = MapManager.GetMapInfo(mapId);
            if (info == null) return Array.Empty<int>();
            return _subareaRosters.TryGetValue(info.SubAreaId, out var roster) ? roster : Array.Empty<int>();
        }

        private static Dictionary<int, List<int>> _subareaRosters = new Dictionary<int, List<int>>();

        /// <summary>Who lives in each subarea: <c>Subareas.Monsters</c>, a JSON <c>{"Array":[...]}</c>.</summary>
        private static Dictionary<int, List<int>> LoadSubareaRosters(SqliteConnection connection)
        {
            var rosters = new Dictionary<int, List<int>>();
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, Monsters FROM Subareas;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(1)) continue;
                    using var doc = System.Text.Json.JsonDocument.Parse(reader.GetString(1));
                    var array = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                                doc.RootElement.TryGetProperty("Array", out var inner) ? inner : doc.RootElement;
                    if (array.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                    rosters[reader.GetInt32(0)] = array.EnumerateArray()
                        .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.Number)
                        .Select(e => e.GetInt32()).ToList();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MobSpawnManager] Could not read the subareas' monsters: {ex.Message}");
            }
            return rosters;
        }

        /// <summary>
        /// A dungeon room's group again, after it was beaten: the same composition, under a new
        /// id. Null for a map that is not a dungeon room, or one with nobody to put in it.
        /// </summary>
        public static MobGroup? RecomposeDungeonRoom(long mapId)
        {
            var dungeon = DungeonManager.OfRoom(mapId);
            if (dungeon == null) return null;

            lock (_candado)
            {
                var group = ComposeDungeonRoom(dungeon, dungeon.Rooms.IndexOf(mapId), Array.Empty<int>(),
                                               ActorIds.NuevoMonstruo(), null);
                if (group == null) return null;
                _mapMobs[mapId] = new List<MobGroup> { group };
                return group;
            }
        }

        /// <summary>
        /// Who of a group comes into a fight: all of an ordinary group; of a dungeon room's, the
        /// first <c>clamp(fighters, 4, 8)</c> -- four for a player alone, one more each from the
        /// fifth, as the jss's alternatives say.
        /// </summary>
        public static List<MobMember> MembersFor(MobGroup group, int fighters)
            => !group.Modular
                ? group.Members
                : group.Members.Take(Math.Clamp(fighters, DungeonMinimum, DungeonGroupSize)).ToList();


        /// <summary>
        /// Places the groups a person has decided and removes the ones they decided to remove.
        /// </summary>
        /// <remarks>
        /// The base's 38,744 groups are Ankama's placement and are regenerated with it, so
        /// neither adding nor removing can be done there: the work would disappear the next time
        /// someone remade the base, without warning. That is why this goes in <c>content/</c>, in text and
        /// versioned.
        ///
        /// The removed ones are deleted AFTER the base has been loaded on purpose. The other way round the list of
        /// tombstones would have to be checked inside the reading loop, and that list is empty almost
        /// always: this way it is paid once per tombstone instead of 38,744 times for nothing.
        ///
        /// Each member's level is not written: it comes from the monster and the grade, which is where
        /// it comes from for the base's ones. Storing it would be a second copy of a derived number.
        ///
        /// Returns the two numbers, for the log.
        /// </remarks>
        private static (int Puestos, int Quitados) AplicarLosEscritos()
        {
            int puestos = 0, quitados = 0;

            try
            {
                var escritos = Jondo.Unity.World.Content.MobGroupContent.Load(
                    Paths.ContentFile(Jondo.Unity.World.Content.MobGroupContent.AuthoredFile),
                    Console.WriteLine);

                foreach (var clave in escritos.ErasedKeys)
                {
                    if (!_mapMobs.TryGetValue(clave.MapId, out var aqui)) continue;
                    quitados += aqui.RemoveAll(grupo => grupo.MobId == clave.GroupId);
                }

                foreach (var escrito in escritos.Values)
                {
                    var grupo = new MobGroup
                    {
                        MobId = escrito.GroupId,
                        CellId = escrito.Cell,
                        Orientation = escrito.Orientation,
                    };

                    foreach (var miembro in escrito.Members)
                    {
                        if (!_monsters.TryGetValue(miembro.MonsterId, out var datos))
                        {
                            Console.WriteLine($"[MobSpawnManager] El grupo {escrito.GroupId} pide el " +
                                              $"monstruo {miembro.MonsterId}, que no está en la base.");
                            continue;
                        }

                        // Up to the sixth grade if the monster declares it: the level 200 Puch Ingball
                        // travels as grade 6 in the kanojedo captures and the client
                        // draws it. The cap of five stays for the generated ones, which is where it was measured.
                        int grado = Math.Clamp(miembro.Grade, 0,
                                               Math.Min(datos.Grades.Count, MobMember.MaxWrittenGrades) - 1);
                        grupo.Members.Add(new MobMember
                        {
                            Monster = datos,
                            GradeIndex = grado,
                            Level = grado < datos.Grades.Count ? datos.Grades[grado].Level : 1,
                        });
                    }

                    // A group with nobody inside is not placed: the client draws an empty group and
                    // attacking it opens a fight without enemies that cannot be left.
                    if (grupo.Members.Count == 0) continue;

                    if (!_mapMobs.TryGetValue(escrito.MapId, out var aqui))
                    {
                        aqui = new List<MobGroup>();
                        _mapMobs[escrito.MapId] = aqui;
                    }

                    // One written for a banned map is placed all the same, and on purpose: the ban is a
                    // rule about what Ankama placed on its own, not about what someone places
                    // here knowingly.
                    aqui.RemoveAll(otro => otro.MobId == escrito.GroupId);
                    aqui.Add(grupo);
                    puestos++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MobSpawnManager] Los grupos escritos no se han podido aplicar: {ex.Message}");
            }

            return (puestos, quitados);
        }

        private static Random _rand = new Random();

        /// <summary>
        /// A map's groups, in a SEPARATE list.
        ///
        /// It used to return the inner list as is, and whoever received it walked it already outside the
        /// lock: the jpv of a map load, the jss of an entry, the group lookup on
        /// attacking. Meanwhile, another player winning his fight on that same map did a
        /// RemoveAll and an Add on that same list, and the first one's foreach died with a
        /// «Collection was modified» that MapLoadHandler's try/catch swallows without a word:
        /// the jpv did not go out, and the player entered an empty map —without his character, without NPCs and without
        /// monsters— with no error anywhere.
        ///
        /// The MobGroups inside are still the same objects; what is copied is the list.
        /// </summary>
        public static List<MobGroup> GetMobsForMap(long mapId)
        {
            lock (_candado)
            {
                if (_mapMobs.TryGetValue(mapId, out var mobs) && (mobs.Count > 0 || _emptiedByHand.Contains(mapId)))
                    return new List<MobGroup>(mobs);

                mobs = GenerateDynamicMobsForMap(mapId);
                _mapMobs[mapId] = mobs;
                return new List<MobGroup>(mobs);
            }
        }

        /// <summary>
        /// Which monsters can come out on a map that has no written groups.
        ///
        /// Those of its zone, and nobody else. Before, this returned a fixed list of pios —491, 492, 493,
        /// 463 and the 234x— for any map in the world, so at the foot of Frigost's clepsydra
        /// tower, which has no groups in the table, Astrub pios came out. And inside the
        /// haven bag too.
        ///
        /// The zone is known by the map's subzone, and what lives in it by the groups that are
        /// written on the other maps of that same subzone: 12,907 maps have them, so
        /// there is almost always somewhere to take it from. If there is not, nobody comes out, which is better than bringing out
        /// whoever does not belong.
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

        /// <summary>Each subzone's monsters, which are worked out once and kept.</summary>
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
                MobId = ActorIds.NuevoMonstruo(),
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
                    // Only the first five. The grade that travels to the client goes from 1 to 5 and no
                    // monster in the real captures goes beyond that, but our data brings
                    // monsters with six, ten and even twenty grades. Picking one of the higher
                    // ones sent a grade the client cannot resolve, and that group
                    // was left without information on hovering over it.
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

            // INDOORS AND ON TOP OF A ZAAP NOBODY IS PLACED, same as on loading. Without this
            // line the ban bit its own tail, and that is the whole explanation of the creature inside the
            // blacksmiths' workshop and of the ones that came out on top of the Astrub zaap:
            //
            //   on start the base's groups of the 3,472 banned maps are discarded
            //     -> those maps are left WITHOUT A KEY in _mapMobs
            //       -> GetMobsForMap finds nothing and takes them for empty maps
            //         -> it repopulates them on the fly with 2 to 4 groups of the subzone
            //
            // So removing the groups was exactly what caused others to appear.
            // The first one to enter any of those 3,472 maps found them.
            //
            // It goes IN HERE and not in GetMobsForMap on purpose: the groups written by hand do
            // ignore the ban -that is what they are for- and already live in _mapMobs, so checking it higher
            // up would hide them. With the check here, an indoor quest group is
            // still served, and when the player kills it the map stays empty instead of
            // filling up again with creatures of the zone.
            if (_vetados.Contains(mapId)) return result;

            // In the haven bag one fights nobody: it is one's home.
            if (Merkasako.IsHavenBag(mapId)) return result;

            // A dungeon room is its own composition, not the subarea's background.
            if (DungeonManager.IsLoaded && DungeonManager.IsRoom(mapId))
            {
                var dungeon = DungeonManager.OfRoom(mapId)!;
                var room = ComposeDungeonRoom(dungeon, dungeon.Rooms.IndexOf(mapId), Array.Empty<int>(),
                                              ActorIds.NuevoMonstruo(), null);
                if (room != null) result.Add(room);
                return result;
            }

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

            // The same rule as on loading. Today a banned map should not get here -if no
            // group is placed, there is no fight to replenish-, but this is the other door through which
            // monsters appear and both had better say the same.
            if (_vetados.Contains(mapId)) return null;

            lock (_candado)
            {
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
        }

        /// <summary>Where the groups a quest brings out are numbered from.</summary>
        /// <remarks>
        /// Their own bracket, below the bosses', so that the three id dispensers -- the
        /// hand-written ones, the bosses and these -- never step on each other.
        /// </remarks>
        private const long PrimerGrupoDeMision = -4_000_000;
        private static long _siguienteDeMision;

        /// <summary>
        /// Places on the map a group of one specific monster, the one a quest asks for.
        /// </summary>
        /// <remarks>
        /// It goes through neither the ban nor the subzone's distribution: a map is not being populated here,
        /// a creature is being brought out of its hiding place because someone has pressed something. The Rata
        /// Nsiosa is in zero groups of the world precisely because its place is this and not the map.
        ///
        /// Returns null when the monster is not in the base or the map has nowhere to put it,
        /// and the caller has to report it: an objective that says «make it come out» and does not bring it out leaves
        /// the quest stuck without saying why.
        /// </remarks>
        public static MobGroup? SpawnNamed(long mapId, int monsterId, int howMany)
        {
            if (!_monsters.TryGetValue(monsterId, out var datos)) return null;

            lock (_candado)
            {
                if (!_mapMobs.TryGetValue(mapId, out var mobs))
                {
                    mobs = new List<MobGroup>();
                    _mapMobs[mapId] = mobs;
                }

                var ocupadas = new HashSet<int>(mobs.Select(m => m.CellId));
                int celda = 0;
                foreach (int libre in GetInnerWalkableCells(mapId))
                {
                    if (ocupadas.Contains(libre)) continue;
                    celda = libre;
                    break;
                }

                if (celda == 0) celda = MapManager.GetNearestWalkableCell(
                    mapId, Handlers.TeleportHandler.MapCentre);
                if (celda == 0) return null;

                int grado = Math.Clamp(datos.Grades.Count - 1, 0, MobMember.MaxGradesPerMonster - 1);
                var miembros = new List<MobMember>();
                for (int i = 0; i < Math.Max(1, howMany); i++)
                {
                    miembros.Add(new MobMember
                    {
                        Monster = datos,
                        GradeIndex = grado,
                        Level = grado < datos.Grades.Count ? datos.Grades[grado].Level : 1,
                    });
                }

                var grupo = new MobGroup
                {
                    MobId = PrimerGrupoDeMision - _siguienteDeMision++,
                    CellId = celda,
                    Members = miembros,
                };

                mobs.Add(grupo);
                return grupo;
            }
        }

        /// <summary>
        /// Plants a group with a given composition, for the rooms of the Infinite Dreams.
        /// </summary>
        /// <remarks>
        /// <see cref="SpawnNamed"/> plants N copies of one same monster, which serves what
        /// it was made for —a quest objective— and not this: a dream's room reproduces a group
        /// of the world, and those groups are mixed. Planting five copies of the first would change the
        /// fight without telling anyone.
        ///
        /// The group goes with one of the negative identifiers, just like the quest ones, so that it does not
        /// clash with the world's nor survive a respawn.
        /// </remarks>
        /// <param name="cell">
        /// The cell to put it on -- the one an administrator stands on --, or -1 for the first free
        /// one inside the map.
        /// </param>
        public static MobGroup? SpawnComposed(long mapId, IEnumerable<(int Monstruo, int Grado)> miembros, int cell = -1)
        {
            var quienes = Componer(miembros);
            if (quienes.Count == 0) return null;

            lock (_candado)
            {
                if (!_mapMobs.TryGetValue(mapId, out var mobs))
                {
                    mobs = new List<MobGroup>();
                    _mapMobs[mapId] = mobs;
                }

                var ocupadas = new HashSet<int>(mobs.Select(m => m.CellId));
                int celda = cell >= 0 ? cell : 0;
                foreach (int libre in cell >= 0 ? (IEnumerable<int>)Array.Empty<int>() : GetInnerWalkableCells(mapId))
                {
                    if (ocupadas.Contains(libre)) continue;
                    celda = libre;
                    break;
                }

                if (celda == 0) celda = MapManager.GetNearestWalkableCell(
                    mapId, Handlers.TeleportHandler.MapCentre);
                if (celda == 0) return null;

                var grupo = new MobGroup
                {
                    MobId = PrimerGrupoDeMision - _siguienteDeMision++,
                    CellId = celda,
                    Members = quienes,
                };

                mobs.Add(grupo);
                return grupo;
            }
        }

        /// <summary>The members of a hand-composed group, up to the sixth grade.</summary>
        private static List<MobMember> Componer(IEnumerable<(int Monstruo, int Grado)> miembros)
        {
            var quienes = new List<MobMember>();
            foreach (var (monstruo, grado) in miembros)
            {
                if (!_monsters.TryGetValue(monstruo, out var datos)) continue;
                if (datos.Grades.Count == 0) continue;

                int cual = Math.Clamp(grado, 0, Math.Min(datos.Grades.Count,
                                                         MobMember.MaxWrittenGrades) - 1);
                quienes.Add(new MobMember
                {
                    Monster = datos,
                    GradeIndex = cual,
                    Level = datos.Grades[cual].Level,
                });
            }

            return quienes;
        }

        /// <summary>
        /// A hand-composed group that is NOT placed on any map: it only exists for the fight
        /// about to be opened with it.
        /// </summary>
        /// <remarks>
        /// It is what the kanojedo's puch master does: in the capture the fight starts with a
        /// group with a new id -the kmu carries a -23597 that was not in the jss- and no jsn
        /// draws it on the map beforehand. Placing it on the map as <see cref="SpawnComposed"/> does would
        /// leave it visible and clickable for the others while the fight lasts.
        /// </remarks>
        public static MobGroup? ComposeOffMap(IEnumerable<(int Monstruo, int Grado)> miembros)
        {
            var quienes = Componer(miembros);
            if (quienes.Count == 0) return null;

            lock (_candado)
            {
                return new MobGroup
                {
                    MobId = PrimerGrupoDeMision - _siguienteDeMision++,
                    CellId = 0,
                    Members = quienes,
                };
            }
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

            // If there is no cell far enough inside, the old fallback
            // to walkable cells is kept. It has to be chosen BEFORE removing the interactives:
            // otherwise, when all the inside cells are taken, the fallback puts back exactly
            // the clickable cells that have just been set aside.
            var candidatas = innerCells.Count > 0 ? innerCells : new List<int>(cells);

            // And out with the cells that have something clickable on top. A group planted
            // on the zaap covers it: the click goes to the monster and there is no longer a way to travel.
            // The map ban already leaves out the 62 zaap maps entirely, but the doors, the
            // workshops and the resources are on maps that are not banned and count just the same.
            var ocupadas = new HashSet<int>();
            foreach (var elemento in Interactives.ElementsOf(mapId))
            {
                if (elemento.Cell != 0) ocupadas.Add(elemento.Cell);
            }

            if (ocupadas.Count > 0)
            {
                candidatas.RemoveAll(c => ocupadas.Contains(c));
            }

            return candidatas;
        }

        /// <summary>
        /// Returns the MobGroup occupying the specified cell on the given map, or null if no mob is there.
        /// Uses a proximity check (±1 cell) to account for pathfinding rounding.
        /// </summary>
        public static MobGroup? GetMobAtCell(long mapId, int cellId)
        {
            lock (_candado)
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
        }

        /// <summary>
        /// The maps an administrator emptied by hand. An empty map is otherwise filled again with
        /// fresh groups the next time somebody loads it (<see cref="GetMobsForMap"/>), which is
        /// right after a fight and wrong after "take these monsters away".
        /// </summary>
        private static readonly HashSet<long> _emptiedByHand = new();

        /// <summary>
        /// An administrator takes a group off its map, until the server stops; an emptied map stays
        /// empty. False when the group was not there.
        /// </summary>
        public static bool RemoveByHand(long mapId, long mobId)
        {
            lock (_candado)
            {
                if (!_mapMobs.TryGetValue(mapId, out var mobs) || mobs.RemoveAll(m => m.MobId == mobId) == 0)
                    return false;
                if (mobs.Count == 0) _emptiedByHand.Add(mapId);
                return true;
            }
        }

        /// <summary>
        /// Removes a mob group from the map after it is defeated in combat.
        /// </summary>
        public static void RemoveMobGroup(long mapId, long mobId)
        {
            lock (_candado)
            {
                if (_mapMobs.TryGetValue(mapId, out var mobs))
                {
                    mobs.RemoveAll(m => m.MobId == mobId);
                }
            }
        }

        public static MobGroup? GetMobGroupById(long mobId)
        {
            lock (_candado)
            {
                foreach (var list in _mapMobs.Values)
                {
                    var found = list.FirstOrDefault(m => m.MobId == mobId);
                    if (found != null) return found;
                }
                return null;
            }
        }

        /// <summary>How many monster groups are placed in the whole world. The server prints it.</summary>
        public static int TotalGrupos
        {
            get
            {
                lock (_candado)
                {
                    int total = 0;
                    foreach (var lista in _mapMobs.Values) total += lista.Count;
                    return total;
                }
            }
        }

        /// <summary>En cuantos mapas hay grupos puestos.</summary>
        public static int MapasConGrupos
        {
            get { lock (_candado) { return _mapMobs.Count; } }
        }

        public static MonsterData? GetMonsterData(int monsterId)
        {
            return _monsters.TryGetValue(monsterId, out var data) ? data : null;
        }
    }
}


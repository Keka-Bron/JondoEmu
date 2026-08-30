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
        /// Los mapas donde NO se pone un monstruo, por mucho que la tabla los tenga.
        ///
        /// Se ven jugando: pios dentro de una casa, dentro de un banco, dentro de una tienda y
        /// plantados encima del zaap del pueblo. Medido sobre los 38.744 grupos colocados: 9.331
        /// —el 24,1 %— están en un mapa bajo techo.
        ///
        /// La regla son dos listas y una excepción, y la excepción es la importante:
        ///
        ///   BAJO TECHO      MapPositions.Outdoor = 0, que son 4.165 mapas
        ///   CON ZAAP        los 62 del catálogo de puntos de viaje; 53 tenían monstruos encima
        ///   SALVO MAZMORRA  753 de las 763 salas de mazmorra están marcadas «bajo techo»
        ///
        /// Sin esa excepción, prohibir el interior VACIARÍA LAS MAZMORRAS ENTERAS: son 2.290
        /// grupos, y una mazmorra sin bichos no es una mazmorra. Con ella, se retiran 7.214 grupos
        /// (el 18,6 %) repartidos por 2.393 mapas, y las 763 salas se quedan como están.
        ///
        /// Se filtra AL CARGAR y no borrando filas de la base a propósito: world.db se regenera y
        /// se distribuye comprimida, así que un borrado se perdería en la próxima regeneración y
        /// habría que acordarse de repetirlo. Esto no hay que acordarse de nada.
        /// </summary>
        /// <summary>Los mapas vetados, guardados para que el repoblador también los respete.</summary>
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

            // Los zaaps salen del catálogo de puntos de viaje. Se lee aquí el fichero en vez de
            // preguntarle a Interactives porque ése se inicializa DESPUÉS que el spawner, y
            // preguntárselo ahora devolvería una lista vacía sin dar ningún error.
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

            // Y la mazmorra manda sobre todo lo demás.
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

        /// <summary>
        /// El candado de los grupos por mapa.
        ///
        /// <see cref="_mapMobs"/> es un Dictionary pelado y se toca desde el hilo de cada jugador:
        /// dos entrando a la vez a mapas sin grupos escritos hacían los dos un <c>_mapMobs[id] =</c>
        /// sobre la misma tabla, que es como se rompe un Dictionary de verdad —bucle infinito
        /// dentro del propio .NET, no una excepción—. Con un jugador no se notaba nunca.
        ///
        /// Esto NO es la fase de monstruos compartidos: sigue faltando marcar un grupo como
        /// ocupado cuando ya está en un combate. Es sólo que el reparto de ids toca este mismo
        /// diccionario y dejarlo sin candado sería empeorarlo.
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

            // Y dónde NO se pone un monstruo por mucho que la tabla lo diga.
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

                    // Ni en una casa, ni en un banco, ni en una tienda, ni encima de un zaap.
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

            // Los jefes de mazmorra, antes de lo escrito a mano para que una persona pueda
            // cambiarlos de sitio o quitarlos.
            int jefes = PonerLosJefesDeMazmorra();

            // Y lo que haya decidido una persona, encima de todo lo anterior.
            var deLaMano = AplicarLosEscritos();

            // Los grupos escritos traen su id puesto desde la siembra. El repartidor tiene que
            // apartarse por debajo del más bajo de todos ellos antes de dar el primero suyo, o el
            // primer grupo generado al vuelo se llevaría un número que ya está ocupado en otro
            // mapa —y entonces GetMobGroupById devolvería el equivocado—. Los puestos a mano
            // cuentan igual: sus ids arrancan en el -2.000.000 y son los más bajos de todos.
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
            // Los dos números por separado, no la resta. Con un grupo puesto y otro quitado la
            // resta da cero y la línea no sale, que es justo el arranque en el que más falta hace
            // ver que content/ ha tocado algo.
            if (deLaMano.Puestos != 0 || deLaMano.Quitados != 0)
            {
                Console.WriteLine($"[MobSpawnManager] Desde content/: {deLaMano.Puestos} grupo(s) " +
                                  $"puestos a mano y {deLaMano.Quitados} quitados.");
            }
        }

        /// <summary>
        /// Pone los grupos que ha decidido una persona y quita los que ha decidido quitar.
        /// </summary>
        /// <remarks>
        /// Los 38.744 grupos de la base son la colocación de Ankama y se regeneran con ella, así
        /// que ni añadir ni quitar se puede hacer ahí: el trabajo desaparecería la próxima vez que
        /// alguien rehiciera la base, sin avisar. Por eso esto va en <c>content/</c>, en texto y
        /// versionado.
        ///
        /// Los quitados se borran DESPUÉS de haber cargado la base a propósito. Al revés habría que
        /// consultar la lista de lápidas dentro del bucle de lectura, y esa lista está vacía casi
        /// siempre: así se paga una vez por lápida en vez de 38.744 veces por nada.
        ///
        /// El nivel de cada miembro no viene escrito: sale del monstruo y del grado, que es de
        /// donde sale para los de la base. Guardarlo sería una segunda copia de un número derivado.
        ///
        /// Devuelve los dos números, para el registro.
        /// </remarks>
        /// <summary>
        /// Pone al jefe de cada mazmorra en su última sala, y sólo a él.
        /// </summary>
        /// <remarks>
        /// Sin esto una mazmorra no tiene final: la última sala se llena con lo mismo que las
        /// demás, porque los grupos que trae world.db para los mapas de mazmorra son el fondo
        /// genérico de la subzona —los seis bichos de la zona repartidos por los once mapas— y no
        /// la disposición de Ankama. Se ve mirando dónde cae el 147, el Jalató Real: sale en dos
        /// pasillos y en ninguna de las cinco salas.
        ///
        /// Lo que sí es de Ankama es QUIÉN es el jefe: el campo <c>bosses</c> del volcado del
        /// cliente, que 126 de las 187 mazmorras rellenan.
        ///
        /// La sala del jefe se vacía primero. Un jefe compartiendo mapa con tres grupos corrientes
        /// se puede esquivar, y una mazmorra que se puede terminar sin pelearse con el jefe no es
        /// una mazmorra.
        ///
        /// Va ANTES de la capa escrita a mano a propósito, para que se pueda mover o quitar desde
        /// el editor sin tocar código.
        /// </remarks>
        /// <summary>Desde donde se numeran los grupos de jefe. Por debajo de los escritos a mano.</summary>
        private const long PrimerJefe = -3_000_000;

        private static int PonerLosJefesDeMazmorra()
        {
            if (!DungeonManager.IsLoaded) return 0;

            int puestos = 0;
            foreach (var mazmorra in DungeonManager.All.Values)
            {
                long sala = mazmorra.LastRoom;
                if (sala == 0 || mazmorra.Bosses.Count == 0) continue;

                var miembros = new List<MobMember>();
                foreach (int jefe in mazmorra.Bosses)
                {
                    if (!_monsters.TryGetValue(jefe, out var datos))
                    {
                        Console.WriteLine($"[Mazmorra] {mazmorra.Name}: el jefe {jefe} no está en la base.");
                        continue;
                    }

                    // El grado más alto que declare. Un jefe a grado 0 es el mismo bicho que los
                    // que se han venido matando por el camino.
                    int grado = Math.Clamp(datos.Grades.Count - 1, 0, MobMember.MaxGradesPerMonster - 1);
                    miembros.Add(new MobMember
                    {
                        Monster = datos,
                        GradeIndex = grado,
                        Level = grado < datos.Grades.Count ? datos.Grades[grado].Level : 1,
                    });
                }

                if (miembros.Count == 0) continue;

                if (!_mapMobs.TryGetValue(sala, out var aqui))
                {
                    _mapMobs[sala] = aqui = new List<MobGroup>();
                }

                aqui.Clear();
                aqui.Add(new MobGroup
                {
                    // Su propio tramo, por debajo del -2.000.000 de los escritos a mano, para que
                    // ninguno de los tres repartos de ids se pise con otro. El repartidor de abajo
                    // se aparta por debajo del menor de todos antes de dar el primero suyo.
                    MobId = PrimerJefe - puestos,
                    CellId = MapManager.GetNearestWalkableCell(sala, Handlers.TeleportHandler.MapCentre),
                    Members = miembros,
                });
                puestos++;
            }

            if (puestos > 0) Console.WriteLine($"[Mazmorra] {puestos} jefes puestos en su última sala.");

            CurarLasSalas();
            return puestos;
        }

        /// <summary>Cuántos monstruos lleva la sala del jefe cuando entra una sola persona.</summary>
        /// <remarks>
        /// El jefe y tres más. Un jefe solo en medio de una sala vacía no es el final de nada, y
        /// además se le mata de un turno. Cuatro es el mínimo; con más atacantes sube a tantos
        /// monstruos como atacantes, y de eso se encarga el escalado por grupo.
        /// </remarks>
        public const int MinimoEnLaSalaDelJefe = 4;

        /// <summary>
        /// Deja cada sala de mazmorra con UN grupo, y al jefe sólo en la suya.
        /// </summary>
        /// <remarks>
        /// Los grupos que world.db trae para los mapas de mazmorra son el fondo genérico de la
        /// subzona, no la disposición de Ankama, y eso se notaba de tres maneras a la vez:
        ///
        /// <list type="bullet">
        /// <item>varios grupos en la misma sala, cuando una sala de mazmorra tiene uno;</item>
        /// <item>el bicho que hace de jefe apareciendo por el camino, porque en esta zona el jefe
        /// es también el monstruo corriente —el Girasol Hambriento sale en los campos— y el fondo
        /// de subzona lo reparte por todas partes;</item>
        /// <item>y la sala del jefe con el jefe solo, porque ponerlo la vacía primero.</item>
        /// </list>
        ///
        /// Aquí se arregla lo primero y lo segundo, y se rellena la del jefe hasta el mínimo con
        /// monstruos sacados de las OTRAS salas de esa misma mazmorra, que es de donde vienen los
        /// que acompañan al jefe en el juego.
        /// </remarks>
        private static int CurarLasSalas()
        {
            if (!DungeonManager.IsLoaded) return 0;

            int tocadas = 0;
            foreach (var mazmorra in DungeonManager.All.Values)
            {
                if (mazmorra.Rooms.Count == 0) continue;
                long salaDelJefe = mazmorra.LastRoom;
                var jefes = new HashSet<int>(mazmorra.Bosses);

                // El repertorio de la mazmorra: lo que sale por sus salas, sin contar al jefe. Es
                // de donde se saca la escolta y con lo que se rellena una sala que se quede vacía.
                var repertorio = new List<MobMember>();
                foreach (long sala in mazmorra.Rooms)
                {
                    if (!_mapMobs.TryGetValue(sala, out var grupos)) continue;
                    foreach (var grupo in grupos)
                    {
                        foreach (var miembro in grupo.Members)
                        {
                            if (miembro.Monster != null && !jefes.Contains(miembro.Monster.Id))
                                repertorio.Add(miembro);
                        }
                    }
                }

                foreach (long sala in mazmorra.Rooms)
                {
                    if (!_mapMobs.TryGetValue(sala, out var grupos) || grupos.Count == 0) continue;

                    if (sala == salaDelJefe)
                    {
                        var jefe = grupos[0];
                        int falta = MinimoEnLaSalaDelJefe - jefe.Members.Count;
                        for (int i = 0; i < falta && repertorio.Count > 0; i++)
                        {
                            jefe.Members.Add(repertorio[i % repertorio.Count]);
                        }

                        if (grupos.Count > 1) { grupos.RemoveRange(1, grupos.Count - 1); tocadas++; }
                        if (falta > 0 && repertorio.Count > 0) tocadas++;
                        continue;
                    }

                    // Una sala corriente: un grupo, y sin el jefe dentro.
                    var primero = grupos[0];
                    if (grupos.Count > 1) { grupos.RemoveRange(1, grupos.Count - 1); tocadas++; }

                    int antes = primero.Members.Count;
                    primero.Members.RemoveAll(
                        miembro => miembro.Monster != null && jefes.Contains(miembro.Monster.Id));

                    if (primero.Members.Count == antes) continue;
                    tocadas++;

                    // Quitar al jefe puede dejar el grupo vacío, y una sala sin nada no se puede
                    // pasar: el jugador se queda dentro sin manera de avanzar.
                    if (primero.Members.Count == 0 && repertorio.Count > 0)
                        primero.Members.Add(repertorio[0]);
                    else if (primero.Members.Count == 0)
                        grupos.Clear();
                }
            }

            if (tocadas > 0)
                Console.WriteLine($"[Mazmorra] {tocadas} sala(s) corregidas: un grupo por sala y " +
                                  $"el jefe sólo en la última.");
            return tocadas;
        }

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
                    var grupo = new MobGroup { MobId = escrito.GroupId, CellId = escrito.Cell };

                    foreach (var miembro in escrito.Members)
                    {
                        if (!_monsters.TryGetValue(miembro.MonsterId, out var datos))
                        {
                            Console.WriteLine($"[MobSpawnManager] El grupo {escrito.GroupId} pide el " +
                                              $"monstruo {miembro.MonsterId}, que no está en la base.");
                            continue;
                        }

                        int grado = Math.Clamp(miembro.Grade, 0, MobMember.MaxGradesPerMonster - 1);
                        grupo.Members.Add(new MobMember
                        {
                            Monster = datos,
                            GradeIndex = grado,
                            Level = grado < datos.Grades.Count ? datos.Grades[grado].Level : 1,
                        });
                    }

                    // Un grupo sin nadie dentro no se pone: el cliente pinta un grupo vacío y
                    // atacarlo abre un combate sin enemigos del que no se sale.
                    if (grupo.Members.Count == 0) continue;

                    if (!_mapMobs.TryGetValue(escrito.MapId, out var aqui))
                    {
                        aqui = new List<MobGroup>();
                        _mapMobs[escrito.MapId] = aqui;
                    }

                    // Uno escrito para un mapa vetado se pone igual, y a propósito: el veto es una
                    // regla sobre lo que Ankama colocó por su cuenta, no sobre lo que alguien pone
                    // aquí a sabiendas.
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
        /// Los grupos de un mapa, en una lista APARTE.
        ///
        /// Devolvía la lista de dentro tal cual, y quien la recibía la recorría ya fuera del
        /// candado: el jpv de una carga de mapa, el jss de una entrada, la búsqueda del grupo al
        /// atacar. Mientras tanto, otro jugador que ganase su combate en ese mismo mapa hacía un
        /// RemoveAll y un Add sobre esa misma lista, y el foreach del primero moría con un
        /// «Collection was modified» que el try/catch de MapLoadHandler se traga sin decir nada:
        /// el jpv no salía, y el jugador entraba a un mapa vacío —sin su personaje, sin NPCs y sin
        /// monstruos— sin ningún error por ninguna parte.
        ///
        /// Los MobGroup de dentro siguen siendo los mismos objetos; lo que se copia es la lista.
        /// </summary>
        public static List<MobGroup> GetMobsForMap(long mapId)
        {
            lock (_candado)
            {
                if (_mapMobs.TryGetValue(mapId, out var mobs) && mobs.Count > 0)
                    return new List<MobGroup>(mobs);

                mobs = GenerateDynamicMobsForMap(mapId);
                _mapMobs[mapId] = mobs;
                return new List<MobGroup>(mobs);
            }
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

            // BAJO TECHO Y ENCIMA DE UN ZAAP NO SE PONE A NADIE, lo mismo que al cargar. Sin esta
            // linea el veto se mordia la cola, y esa es toda la explicacion del bicho dentro del
            // taller de herreros y de los que salian encima del zaap de Astrub:
            //
            //   al arrancar se descartan los grupos de la base de los 3.472 mapas vetados
            //     -> esos mapas se quedan SIN CLAVE en _mapMobs
            //       -> GetMobsForMap no encuentra nada y los toma por mapas vacios
            //         -> los repuebla al vuelo con 2 a 4 grupos de la subzona
            //
            // O sea que quitar los grupos era exactamente lo que provocaba que aparecieran otros.
            // El primero que entrara en cualquiera de esos 3.472 mapas se los encontraba.
            //
            // Va AQUI dentro y no en GetMobsForMap a proposito: los grupos escritos a mano si
            // ignoran el veto -para eso estan- y viven ya en _mapMobs, asi que comprobarlo mas
            // arriba los escondiria. Con la comprobacion aqui, un grupo de mision bajo techo se
            // sigue sirviendo, y cuando el jugador lo mata el mapa se queda vacio en vez de
            // volver a llenarse de bichos de la zona.
            if (_vetados.Contains(mapId)) return result;

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

            // La misma regla que al cargar. Hoy no debería llegar aquí un mapa vetado -si no se
            // pone un grupo, no hay pelea que reponer-, pero ésta es la otra puerta por la que
            // aparecen monstruos y más vale que las dos digan lo mismo.
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

        /// <summary>Desde donde se numeran los grupos que saca una misión.</summary>
        /// <remarks>
        /// Su propio tramo, por debajo del de los jefes, para que los tres repartos de ids -- los
        /// escritos a mano, los jefes y éstos -- no se pisen nunca.
        /// </remarks>
        private const long PrimerGrupoDeMision = -4_000_000;
        private static long _siguienteDeMision;

        /// <summary>
        /// Pone en el mapa un grupo de un monstruo concreto, el que pida una misión.
        /// </summary>
        /// <remarks>
        /// No pasa por el veto ni por el reparto de la subzona: aquí no se está poblando un mapa,
        /// se está sacando a un bicho de su escondite porque alguien ha pulsado algo. La Rata
        /// Nsiosa está en cero grupos del mundo justamente porque su sitio es éste y no el mapa.
        ///
        /// Devuelve null cuando el monstruo no está en la base o el mapa no tiene donde ponerlo,
        /// y quien llama tiene que contarlo: un objetivo que dice «hazla salir» y no la saca deja
        /// la misión encallada sin decir por qué.
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

            // Si no hay ninguna casilla suficientemente interior, se conserva el antiguo repli
            // sur les cases marchables. Il faut le choisir AVANT de retirer les interactifs :
            // sinon, lorsque toutes les cases intérieures sont occupées, le repli remet exactement
            // les cases cliquables que l'on vient d'écarter.
            var candidatas = innerCells.Count > 0 ? innerCells : new List<int>(cells);

            // Y fuera las casillas que tienen algo encima que se pueda clicar. Un grupo plantado
            // sobre el zaap lo tapa: el clic se lo lleva el monstruo y ya no hay forma de viajar.
            // El veto de mapas ya deja fuera los 62 mapas de zaap enteros, pero las puertas, los
            // talleres y los recursos estan en mapas que no estan vetados y valen igual.
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

        /// <summary>Cuantos grupos de monstruos hay puestos en todo el mundo. Lo pinta el servidor.</summary>
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


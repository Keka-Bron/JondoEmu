using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Las casas del mundo: sus puertas, y a qué interior lleva cada una.
    ///
    /// ─── Qué está medido ────────────────────────────────────────────────────────────────────
    ///
    /// Que un elemento es la puerta de una casa lo dice el servidor real declarándolo con TIPO
    /// 300, y ese tipo trae tres habilidades que sólo tiene una vivienda: entrar (84), código de
    /// acceso (100) y poner en venta (98, o 108 si ya lo está). Un edificio que no es casa sale
    /// con tipo −1 y no se puede clicar siquiera.
    ///
    /// Entrar y salir NO son el mismo mensaje, y eso costó verlo:
    ///
    ///   entrar   iwo { f1: habilidad, f2: elemento, f3: vivienda }  →  iwn  →  jqw { f1: mapa }
    ///   salir    iwo { f1: habilidad, f2: elemento }                →  iwn  →  jru { f2: mapa }
    ///
    /// Ojo al número de campo del mapa: en el jqw va en el f1 y en el jru va en el f2.
    ///
    /// El f3 del iwo de entrada es el NÚMERO DE VIVIENDA, un campo que no estaba en el esquema
    /// que conocíamos: un mismo elemento puerta sirve a varias viviendas de un mismo edificio
    /// —en la captura, once— y sin él el servidor no sabría a cuál entrar. Aquí cada puerta tiene
    /// un solo interior, así que se lee y se ignora; queda escrito para cuando haya varias.
    ///
    /// ─── Qué hemos decidido nosotros ────────────────────────────────────────────────────────
    ///
    /// A qué mapa lleva cada puerta NO ESTÁ EN EL CLIENTE. Se comprobó a tres bandas:
    /// HousesDataRoot trae seis campos y ninguno es un mapa, los 569 bundles de mapas no llevan
    /// ni un campo con «house», y «doorCell» da cero ocurrencias en global-metadata.dat. Ese
    /// vínculo lo pone el servidor de Ankama y no lo tenemos.
    ///
    /// Así que lo ponemos nosotros: cada puerta coge un interior de la piscina de su subzona —y
    /// si su subzona no tiene, de su área—, repartido por índice con todo ordenado. Sale siempre
    /// igual, sin guardar nada, y la casa se acuerda con la zona. Lo hace tools/casas_mundo.py y
    /// se puede corregir a mano en el .json: es una decisión nuestra, no un dato medido.
    ///
    /// ─── Estado mutable ──────────────────────────────────────────────────────────────────────
    ///
    /// Esta clase sólo descubre la geometría. <see cref="HouseManager"/> materializa cada puerta
    /// en SQLite y conserva su modelo, dueño, precio y política. La ficha <c>lnx</c> viaja ya en
    /// <c>jss.f9</c>; skill 97 abre <c>khr</c> y <c>jal</c> confirma una oferta ligada a la sesión.
    /// Los cofres y códigos de acceso siguen siendo trabajo separado.
    /// </summary>
    public static class Houses
    {
        /// <summary>El tipo con el que el cliente dibuja la puerta de una casa.</summary>
        public const int DoorType = 300;

        /// <summary>La habilidad «entrar en la casa».</summary>
        public const int EnterSkill = 84;

        /// <summary>Opens the purchase confirmation UI; the confirmed price arrives in jal.</summary>
        public const int BuySkill = 97;

        /// <summary>El tipo de la puerta de dentro, la que devuelve a la calle.</summary>
        public const int ExitType = 316;

        /// <summary>La habilidad de la puerta de dentro. Es la genérica de «usar».</summary>
        public const int ExitSkill = 184;

        /// <summary>Una puerta de casa: dónde está y a dónde lleva.</summary>
        public readonly struct Door
        {
            public Door(long mapId, int elementId, int cell, int gfx, long interiorMapId)
            {
                MapId = mapId; ElementId = elementId; Cell = cell; Gfx = gfx;
                InteriorMapId = interiorMapId;
            }

            public long MapId { get; }
            public int ElementId { get; }
            public int Cell { get; }
            public int Gfx { get; }
            public long InteriorMapId { get; }
        }

        /// <summary>
        /// El elemento por el que se sale de un interior.
        ///
        /// Cuando <see cref="IsRealDoor"/> es cierto, es una puerta de verdad —lleva el dibujo
        /// 44035, el que se midió en la captura—. Cuando es falso, es el elemento de número más
        /// bajo del mapa y lo hemos elegido nosotros: puede ser un mueble. Es a propósito, porque
        /// un mueble por el que se sale es mejor que un interior del que no se sale.
        /// </summary>
        public readonly struct Exit
        {
            public Exit(int elementId, int cell, int gfx, bool isRealDoor)
            {
                ElementId = elementId; Cell = cell; Gfx = gfx; IsRealDoor = isRealDoor;
            }

            public int ElementId { get; }
            public int Cell { get; }
            public int Gfx { get; }
            public bool IsRealDoor { get; }
        }

        private static readonly Dictionary<long, List<Door>> _byMap = new();
        private static readonly Dictionary<(long MapId, int ElementId), Door> _byElement = new();
        private static readonly Dictionary<long, Exit> _exits = new();

        /// <summary>Por qué puerta se vuelve a la calle desde cada interior.</summary>
        private static readonly Dictionary<long, Door> _wayBack = new();

        public static int Count => _byElement.Count;
        public static int InteriorCount => _exits.Count;
        public static IEnumerable<Door> All => _byElement.Values;
        public static IEnumerable<long> Interiors => _exits.Keys;

        public static void Initialize()
        {
            _byMap.Clear();
            _byElement.Clear();
            _exits.Clear();
            _wayBack.Clear();
            int graphDoorsSkipped = 0;
            int graphExitsSkipped = 0;

            string path = Paths.HouseWorldJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Casas] Falta {Path.GetFileName(path)}; sin él no hay casas. " +
                                  "Genéralo con tools/casas_mundo.py.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("puertas", out var list)) return;

                foreach (var entry in list.EnumerateArray())
                {
                    long mapId = entry.GetProperty("mapa").GetInt64();
                    int elementId = entry.GetProperty("elemento").GetInt32();
                    long interior = entry.TryGetProperty("interior", out var i) ? i.GetInt64() : 0;

                    // The placement file may have been generated from an older map snapshot.
                    // Only declare a door that still exists in the pinned 3.6.10.10 map assets,
                    // and take its live cell/gfx from those assets rather than stale duplicates.
                    var liveElement = Interactives.ByElementId(mapId, elementId);
                    if (liveElement.Id == 0) continue;

                    // A graphic family is only a heuristic.  When the pinned world graph names
                    // this exact live element as a generic interactive transition, that stronger
                    // per-element evidence wins.  In 3.6.10.10 those collisions use skill 184,
                    // never house-enter skill 84; treating them as houses sent players to a
                    // synthetic and demonstrably different map.
                    if (WorldInteractiveTransitions.HasGraphEvidence(mapId, elementId))
                    {
                        graphDoorsSkipped++;
                        continue;
                    }

                    // Una puerta que no lleva a ningún sitio no se declara: el jugador clicaría y
                    // no pasaría nada, que es peor que no poder clicar.
                    if (interior <= 0) continue;
                    if (MapManager.GetMapInfo(interior) == null) continue;

                    var door = new Door(
                        mapId, elementId,
                        liveElement.Cell,
                        liveElement.Gfx,
                        interior);

                    if (_byElement.ContainsKey((mapId, elementId))) continue;
                    _byElement.Add((mapId, elementId), door);
                    if (!_byMap.TryGetValue(mapId, out var doors))
                    {
                        doors = new List<Door>();
                        _byMap.Add(mapId, doors);
                    }
                    doors.Add(door);
                }
                if (doc.RootElement.TryGetProperty("salidas", out var exits))
                {
                    foreach (var entry in exits.EnumerateObject())
                    {
                        if (!long.TryParse(entry.Name, out long interior)) continue;

                        // The placement catalogue predates the pinned map snapshot.  An exit
                        // that no longer exists in Map/Data would let a character enter a room
                        // from which this client can never leave.  Join it back to the live
                        // element identity and use the live cell/gfx, just as exterior doors do.
                        int elementId = entry.Value.GetProperty("elemento").GetInt32();
                        var liveElement = Interactives.ByElementId(interior, elementId);
                        if (liveElement.Id == 0) continue;

                        // Interior graphics are heuristic too.  If the exact element has a
                        // world-graph edge, that edge owns the click and its measured target
                        // must not be hidden behind a synthetic house return route.
                        if (WorldInteractiveTransitions.HasGraphEvidence(interior, elementId))
                        {
                            graphExitsSkipped++;
                            continue;
                        }

                        _exits[interior] = new Exit(
                            elementId,
                            liveElement.Cell,
                            liveElement.Gfx,
                            entry.Value.TryGetProperty("puerta", out var d) && d.GetBoolean());
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Casas] No se han podido leer las puertas: {ex.Message}");
                return;
            }

            // Una puerta cuyo interior no tenga salida declarada no se declara tampoco: entrar
            // ahí sería encerrar al jugador.
            var huerfanas = new List<(long, int)>();
            foreach (var pair in _byElement)
            {
                if (!_exits.ContainsKey(pair.Value.InteriorMapId)) huerfanas.Add(pair.Key);
            }
            foreach (var clave in huerfanas)
            {
                var door = _byElement[clave];
                _byElement.Remove(clave);
                if (_byMap.TryGetValue(door.MapId, out var lista)) lista.Remove(door);
            }
            foreach (long mapId in new List<long>(_byMap.Keys))
            {
                if (_byMap[mapId].Count == 0) _byMap.Remove(mapId);
            }

            // An exit without any remaining exterior door is not a house exit.  Registering it
            // would consume the element and then fail in LeaveAsync because no street-side
            // route exists.  Keep only the interiors which an active house can actually enter.
            var usedInteriors = new HashSet<long>();
            foreach (var door in _byElement.Values) usedInteriors.Add(door.InteriorMapId);
            foreach (long interior in new List<long>(_exits.Keys))
            {
                if (!usedInteriors.Contains(interior)) _exits.Remove(interior);
            }
            if (huerfanas.Count > 0)
                Console.WriteLine($"[Casas] {huerfanas.Count} puertas descartadas: su interior no " +
                                  "tiene por dónde salir.");
            if (graphDoorsSkipped > 0)
                Console.WriteLine($"[Casas] {graphDoorsSkipped} candidatos por gráfico cedidos " +
                                  "al grafo mundial por evidencia exacta de elemento.");
            if (graphExitsSkipped > 0)
                Console.WriteLine($"[Casas] {graphExitsSkipped} salidas heurísticas cedidas " +
                                  "al grafo mundial por evidencia exacta de elemento.");

            // Y por dónde se vuelve: la primera puerta que lleva a cada interior. Se saca de los
            // datos y no de la sesión a propósito, para que salir siga funcionando después de
            // desconectarse dentro de una casa.
            foreach (long mapId in SortedKeys(_byMap))
            {
                foreach (var door in _byMap[mapId])
                {
                    if (!_wayBack.ContainsKey(door.InteriorMapId)) _wayBack[door.InteriorMapId] = door;
                }
            }

            int puertasDeVerdad = 0;
            foreach (var exit in _exits.Values)
            {
                if (exit.IsRealDoor) puertasDeVerdad++;
            }

            Console.WriteLine($"[Casas] {_byElement.Count} puertas en {_byMap.Count} mapas, " +
                              $"{_exits.Count} interiores ({puertasDeVerdad} con puerta de verdad).");
        }

        public static IReadOnlyList<Door> On(long mapId)
            => _byMap.TryGetValue(mapId, out var doors)
                ? doors
                : (IReadOnlyList<Door>)Array.Empty<Door>();

        public static bool TryGetDoor(long mapId, int elementId, out Door door)
            => _byElement.TryGetValue((mapId, elementId), out door);

        /// <summary>¿Este mapa es el interior de alguna casa?</summary>
        public static bool IsInterior(long mapId) => _exits.ContainsKey(mapId);

        public static bool TryGetExit(long interiorMapId, out Exit exit)
            => _exits.TryGetValue(interiorMapId, out exit);

        /// <summary>La puerta por la que se vuelve a la calle desde este interior.</summary>
        public static bool TryGetWayBack(long interiorMapId, out Door door)
            => _wayBack.TryGetValue(interiorMapId, out door);

        private static List<long> SortedKeys(Dictionary<long, List<Door>> source)
        {
            var keys = new List<long>(source.Keys);
            keys.Sort();
            return keys;
        }
    }
}

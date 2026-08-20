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
    /// ─── Qué NO hace todavía ────────────────────────────────────────────────────────────────
    ///
    /// No se manda la FICHA de la casa. La ficha es el submensaje que el cliente llama <c>lnx</c>
    /// y viaja dentro del jss en el campo 9: precio en su f7, dueño en su f8, y estar en venta no
    /// es un booleano sino llevar el f7. El problema es «sin dueño»: de las 1.276 fichas que hay
    /// en las 34 carpetas de capturas, las 1.276 traen dueño. No hay ni una muestra de casa libre,
    /// así que omitir el f8 es lo coherente con el formato pero NO está medido — y el jss es el
    /// mensaje del que cuelga el mapa entero. Se deja fuera hasta tener una captura.
    ///
    /// Tampoco se hace el cofre de la casa, ni el código de acceso, ni comprar o vender.
    /// </summary>
    public static class Houses
    {
        /// <summary>El tipo con el que el cliente dibuja la puerta de una casa.</summary>
        public const int DoorType = 300;

        /// <summary>La habilidad «entrar en la casa».</summary>
        public const int EnterSkill = 84;

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
        public static IEnumerable<long> Interiors => _exits.Keys;

        public static void Initialize()
        {
            _byMap.Clear();
            _byElement.Clear();
            _exits.Clear();
            _wayBack.Clear();

            string path = Paths.Resolve("casas_mundo_3.6.10.10.json");
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

                    // Una puerta que no lleva a ningún sitio no se declara: el jugador clicaría y
                    // no pasaría nada, que es peor que no poder clicar.
                    if (interior <= 0) continue;
                    if (MapManager.GetMapInfo(interior) == null) continue;

                    var door = new Door(
                        mapId, elementId,
                        entry.TryGetProperty("casilla", out var c) ? c.GetInt32() : 0,
                        entry.TryGetProperty("gfx", out var g) ? g.GetInt32() : 0,
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
                        _exits[interior] = new Exit(
                            entry.Value.GetProperty("elemento").GetInt32(),
                            entry.Value.TryGetProperty("casilla", out var c) ? c.GetInt32() : 0,
                            entry.Value.TryGetProperty("gfx", out var g) ? g.GetInt32() : 0,
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
            if (huerfanas.Count > 0)
                Console.WriteLine($"[Casas] {huerfanas.Count} puertas descartadas: su interior no " +
                                  "tiene por dónde salir.");

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

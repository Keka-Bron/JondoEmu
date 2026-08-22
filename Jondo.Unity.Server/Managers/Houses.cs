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
    ///   entrar   iwo { f1: habilidad, f2: elemento, f3: instancia } →  iwn  →  jqw { f1: mapa }
    ///   salir    iwo { f1: habilidad, f2: elemento }                →  iwn  →  jru { f2: mapa }
    ///
    /// Ojo al número de campo del mapa: en el jqw va en el f1 y en el jru va en el f2.
    ///
    /// El f3 del iwo de entrada dice A QUÉ INSTANCIA se entra, y no es un piso. Cuando Ankama
    /// fusionó servidores no había casas para todo el mundo, así que una misma puerta con un
    /// mismo interior pasó a pertenecer a mucha gente a la vez: cada dueño tiene su propia copia
    /// del MISMO mapa, separada de las demás. En la captura ese edificio tiene once dueños, no
    /// once plantas.
    ///
    /// Por eso una puerta lleva a un interior y sólo a uno, que es como está hecho aquí. Y por eso
    /// en Jondo, donde las casas no tienen dueño, con una sola instancia basta: el f3 se lee y se
    /// ignora. El día que haya dueños, ese campo es el que dice de quién es la copia que se abre.
    ///
    /// ─── Qué hemos decidido nosotros ────────────────────────────────────────────────────────
    ///
    /// A qué mapa lleva cada puerta NO ESTÁ EN EL CLIENTE. Se comprobó a tres bandas:
    /// HousesDataRoot trae seis campos y ninguno es un mapa, los 569 bundles de mapas no llevan
    /// ni un campo con «house», y «doorCell» da cero ocurrencias en global-metadata.dat. Ese
    /// vínculo lo pone el servidor de Ankama y no lo tenemos.
    ///
    /// Así que lo ponemos nosotros, y con una lista de INCLUSIÓN, no de exclusiones. Los interiores
    /// salen sólo de las dos subzonas que se sabe que son viviendas —983 Residencia brakmariana y
    /// 984 Residencia bontariana, 114 mapas— repartidos por índice con todo ordenado: la misma
    /// puerta lleva siempre al mismo sitio, sin guardar nada.
    ///
    /// La primera versión hacía lo contrario —cualquier mapa en (0,0) menos una lista de vetos— y
    /// salió mal en el juego: una puerta de Astrub llevaba a un taller de herrero de Tierradala,
    /// que es un sitio público al que se llega andando, y encima se le declaraba a su FORJA que
    /// era la salida de la casa. Es decir, un interactivo de oficio de un mapa legítimo pasaba a
    /// sacarte a la calle. Con 2.377 mapas en (0,0) una lista de vetos nunca iba a ser suficiente:
    /// hay que decir cuáles SÍ.
    ///
    /// El precio es que las casas ya no se acuerdan de su zona: sólo Bonta y Brakmar tienen
    /// residencias, así que 1.251 de las 1.437 puertas llevan a un interior de otra parte. Es feo
    /// y es a propósito, porque lo otro rompía contenido que funcionaba.
    ///
    /// Lo hace tools/casas_mundo.py y se puede corregir a mano en el .json.
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

        /// <summary>Una puerta de casa: dónde está, a dónde lleva y de qué casa es.</summary>
        public readonly struct Door
        {
            public Door(long mapId, int elementId, int cell, int gfx, long interiorMapId,
                        int model, string name, long price, int rooms, int dwellings)
            {
                MapId = mapId; ElementId = elementId; Cell = cell; Gfx = gfx;
                InteriorMapId = interiorMapId;
                Model = model; Name = name; Price = price; Rooms = rooms; Dwellings = dwellings;
            }

            public long MapId { get; }
            public int ElementId { get; }
            public int Cell { get; }
            public int Gfx { get; }
            public long InteriorMapId { get; }

            /// <summary>
            /// El modelo de casa, el typeId de HousesDataRoot, o cero si no se sabe.
            ///
            /// Sólo lo tienen las 37 puertas de los 25 mapas donde la lista de casas que manda el
            /// servidor real cuadra en número con las puertas que reconocemos. Ahí se emparejan
            /// por orden, y el único caso comprobable dice que el orden acierta: la puerta 522653
            /// del mapa 212601864 sale como la «Casa grande de Bonta» de once dueños, que es
            /// exactamente el edificio en el que se entra en la captura.
            /// </summary>
            public int Model { get; }

            public string Name { get; }
            public long Price { get; }
            public int Rooms { get; }

            /// <summary>
            /// Cuántos DUEÑOS distintos tiene el edificio, cada uno con su copia del mismo
            /// interior. No son plantas: ver la explicación de la clase.
            /// </summary>
            public int Dwellings { get; }

            public bool IsKnown => Model != 0;
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

                    int dwellings = 0;
                    if (entry.TryGetProperty("instancias", out var flats))
                        dwellings = flats.GetArrayLength();

                    var door = new Door(
                        mapId, elementId,
                        entry.TryGetProperty("casilla", out var c) ? c.GetInt32() : 0,
                        entry.TryGetProperty("gfx", out var g) ? g.GetInt32() : 0,
                        interior,
                        entry.TryGetProperty("casa", out var h) ? h.GetInt32() : 0,
                        entry.TryGetProperty("nombre", out var n) ? (n.GetString() ?? "") : "",
                        entry.TryGetProperty("precio", out var pr) ? pr.GetInt64() : 0,
                        entry.TryGetProperty("habitaciones", out var rm) ? rm.GetInt32() : 0,
                        dwellings);

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

            int conNombre = 0;
            foreach (var door in _byElement.Values)
            {
                if (door.IsKnown) conNombre++;
            }

            Console.WriteLine($"[Casas] {_byElement.Count} puertas en {_byMap.Count} mapas, " +
                              $"{_exits.Count} interiores ({puertasDeVerdad} con puerta de verdad), " +
                              $"{conNombre} con casa identificada.");
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

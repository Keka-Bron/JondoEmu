using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The world's houses: their doors, and which interior each one leads to.
    ///
    /// ─── What is measured ───────────────────────────────────────────────────────────────────
    ///
    /// That an element is a house door is said by the real server declaring it with TYPE
    /// 300, and that type brings three skills only a dwelling has: enter (84), access
    /// code (100) and put up for sale (98, or 108 if it already is). A building that is not a house comes
    /// out with type −1 and cannot even be clicked.
    ///
    /// Entering and leaving are NOT the same message, and that took some seeing:
    ///
    ///   enter    iwo { f1: skill, f2: element, f3: instance } →  iwn  →  jqw { f1: map }
    ///   leave    iwo { f1: skill, f2: element }               →  iwn  →  jru { f2: map }
    ///
    /// Mind the map's field number: in the jqw it goes in f1 and in the jru it goes in f2.
    ///
    /// The f3 of the entering iwo says WHICH INSTANCE is entered, and it is not a floor. When Ankama
    /// merged servers there were not houses for everybody, so one same door with one same
    /// interior came to belong to many people at once: each owner has his own copy
    /// of the SAME map, separate from the others. In the capture that building has eleven owners, not
    /// eleven floors.
    ///
    /// That is why a door leads to one interior and only one, which is how it is done here. And that is why
    /// in Jondo, where houses have no owner, a single instance is enough: the f3 is read and
    /// ignored. The day there are owners, that field is the one saying whose copy is opened.
    ///
    /// ─── What we decided ourselves ──────────────────────────────────────────────────────────
    ///
    /// Which map each door leads to IS NOT IN THE CLIENT. It was checked three ways:
    /// HousesDataRoot brings six fields and none is a map, the 569 map bundles do not carry
    /// a single field with «house», and «doorCell» gives zero hits in global-metadata.dat. That
    /// link is set by Ankama's server and we do not have it.
    ///
    /// So we set it ourselves, and with an INCLUSION list, not one of exclusions. The interiors
    /// come only from the two subzones known to be dwellings —983 Residencia brakmariana and
    /// 984 Residencia bontariana, 114 maps— handed out by index with everything sorted: the same
    /// door always leads to the same place, without storing anything.
    ///
    /// The first version did the opposite —any map at (0,0) except a list of vetoes— and
    /// it went wrong in the game: an Astrub door led to a blacksmith's workshop in Tierradala,
    /// which is a public place reached on foot, and on top of that its FORGE was declared to be
    /// the house's exit. That is, a profession interactive of a legitimate map ended up throwing
    /// you out into the street. With 3,357 maps at (0,0) a veto list was never going to be enough:
    /// one has to say which ones YES.
    ///
    /// The price is that houses no longer remember their zone: only Bonta and Brakmar have
    /// residences, so 1,251 of the 1,437 doors lead to an interior from somewhere else. It is ugly
    /// and it is on purpose, because the other way broke content that worked.
    ///
    /// tools/casas_mundo.py does it, and it can be corrected by hand in the .json.
    ///
    /// ─── Owners ──────────────────────────────────────────────────────────────────────────────
    ///
    /// A house can have an owner now (see <see cref="HouseStore"/>), and only the 37 doors whose
    /// model is known can: the plaque names the house by its model (f4 of the jss f9 entry) and
    /// the model is where the price comes from. The other doors stay what they were, open to
    /// everybody and nobody's.
    ///
    /// The plaque -- lnx -- only ever travels for a house WITH an owner: of the 1,276 plaques in
    /// the 34 capture folders, all 1,276 have one, and there is no sample of a free house. A
    /// house that can be owned and has no owner sends one all the same, without a name and at its
    /// model's price: the client opens no buyer's window for a house its map did not declare.
    ///
    /// Each door is ONE house with ONE instance (<see cref="Instance"/>); the house id the
    /// protocol carries is ours, the door's rank in (map, element) order, stable while the doors
    /// are. What is kept is keyed by the door, not by that number.
    /// </summary>
    public static class Houses
    {
        /// <summary>The type the client draws a house door with.</summary>
        public const int DoorType = 300;

        /// <summary>The «enter the house» skill.</summary>
        public const int EnterSkill = 84;

        /// <summary>
        /// "Comprar". Not in any capture: nobody bought a house. It is the client's own skill for
        /// it, with the same action (5) as the four door skills that are measured.
        /// </summary>
        public const int BuySkill = 97;

        /// <summary>"Vender": the owner's door when the house is not on sale, "poner casa en venta", frame 2.</summary>
        public const int SellSkill = 98;

        /// <summary>"Modificar el código", the owner's door, "cambiar codigo acceso", frame 5.</summary>
        public const int CodeSkill = 100;

        /// <summary>"Modificar el precio de venta": the owner's door once on sale, "retirar casa de la venta", frame 2.</summary>
        public const int PriceSkill = 108;

        /// <summary>
        /// The skills a door of a house that can be owned is registered with, each with its own
        /// skill instance. Which of them a viewer is offered is decided when the map is sent:
        /// see <see cref="Handlers.HouseHandler.DoorSkillsFor"/>.
        /// </summary>
        public static readonly IReadOnlyList<int> OwnableDoorSkills = new[] { EnterSkill, BuySkill, SellSkill, CodeSkill, PriceSkill };

        /// <summary>
        /// The instance of every house: one per door here. The real server numbers the owners of a
        /// building -- 22 is Sacrogrito69's in "Casas/" -- and the client sends it back in iwo f3,
        /// izv f1 and jan f2; it is read and checked against this.
        /// </summary>
        public const int Instance = 1;

        /// <summary>A house chest, as the interior's jss declares it: type 85, "entrar en mi casa", frame 16.</summary>
        public const int ChestType = 85;

        /// <summary>"Abrir", the chest's first skill (104), and "Poner el cerrojo", its second (105).</summary>
        public const int ChestOpenSkill = 104;
        public const int ChestLockSkill = 105;

        /// <summary>
        /// The two chests there are inside the 114 interiors: 12367 -- the one in the capture,
        /// element 522477 -- and 46581, which the captures declare with type 85 three times.
        /// </summary>
        private static readonly HashSet<int> ChestGraphics = new() { 12367, 46581 };

        /// <summary>The type of the inside door, the one leading back to the street.</summary>
        public const int ExitType = 316;

        /// <summary>The inside door's skill. It is the generic «use» one.</summary>
        public const int ExitSkill = 184;

        /// <summary>A house door: where it is, where it leads and which house it belongs to.</summary>
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
            /// The house model, HousesDataRoot's typeId, or zero if it is not known.
            ///
            /// Only the 37 doors of the 25 maps where the house list the real server sends matches
            /// in number the doors we recognise have it. There they are paired in order, and the
            /// only checkable case says the order is right: door 522653 of map 212601864 comes out
            /// as the eleven-owner «Casa grande de Bonta», which is exactly the building entered in
            /// the capture.
            /// </summary>
            public int Model { get; }

            public string Name { get; }
            public long Price { get; }
            public int Rooms { get; }

            /// <summary>
            /// How many distinct OWNERS the building has, each one with his copy of the same
            /// interior. They are not floors: see the class's explanation.
            /// </summary>
            public int Dwellings { get; }

            public bool IsKnown => Model != 0;

            /// <summary>Whether the house can have an owner: its model, and so its price, is known.</summary>
            public bool IsOwnable => IsKnown && Price > 0;
        }

        /// <summary>
        /// The element an interior is left through.
        ///
        /// When <see cref="IsRealDoor"/> is true, it is a real door —it carries drawing
        /// 44035, the one measured in the capture—. When it is false, it is the lowest-numbered
        /// element of the map and we chose it ourselves: it may be a piece of furniture. It is on purpose, because
        /// a piece of furniture one leaves through is better than an interior one cannot leave.
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

        /// <summary>Which door leads back to the street from each interior.</summary>
        private static readonly Dictionary<long, Door> _wayBack = new();

        /// <summary>The house id of every door, and the door of every house id.</summary>
        private static readonly Dictionary<(long MapId, int ElementId), int> _houseIds = new();
        private static readonly Dictionary<int, Door> _byHouseId = new();

        public static int Count => _byElement.Count;
        public static int InteriorCount => _exits.Count;
        public static IEnumerable<long> Interiors => _exits.Keys;

        public static void Initialize()
        {
            _byMap.Clear();
            _byElement.Clear();
            _exits.Clear();
            _wayBack.Clear();
            _houseIds.Clear();
            _byHouseId.Clear();

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

                    // A door that leads nowhere is not declared: the player would click and nothing
                    // would happen, which is worse than not being able to click.
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

            // A door whose interior has no declared exit is not declared either: entering
            // there would lock the player in.
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

            // And where one goes back through: the first door leading to each interior. It is taken
            // from the data and not from the session on purpose, so that leaving still works after
            // disconnecting inside a house.
            foreach (long mapId in SortedKeys(_byMap))
            {
                foreach (var door in _byMap[mapId])
                {
                    if (!_wayBack.ContainsKey(door.InteriorMapId)) _wayBack[door.InteriorMapId] = door;
                }
            }

            // The house ids: the doors' rank in (map, element) order, from 1.
            var keys = new List<(long MapId, int ElementId)>(_byElement.Keys);
            keys.Sort();
            for (int i = 0; i < keys.Count; i++)
            {
                _houseIds[keys[i]] = i + 1;
                _byHouseId[i + 1] = _byElement[keys[i]];
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

        /// <summary>Is this map the interior of some house?</summary>
        public static bool IsInterior(long mapId) => _exits.ContainsKey(mapId);

        public static bool TryGetExit(long interiorMapId, out Exit exit)
            => _exits.TryGetValue(interiorMapId, out exit);

        /// <summary>The door that leads back to the street from this interior.</summary>
        public static bool TryGetWayBack(long interiorMapId, out Door door)
            => _wayBack.TryGetValue(interiorMapId, out door);

        /// <summary>The house id this door carries on the wire; zero for a door that is not a house.</summary>
        public static int HouseIdOf(long mapId, int elementId)
            => _houseIds.TryGetValue((mapId, elementId), out int id) ? id : 0;

        /// <summary>The door of a house id, as the client sends it back in izv f2.</summary>
        public static bool TryGetByHouseId(int houseId, out Door door)
            => _byHouseId.TryGetValue(houseId, out door);

        /// <summary>The chests standing in an interior: the elements with a chest's graphic.</summary>
        public static List<Interactives.Element> ChestsIn(long interiorMapId)
        {
            var chests = new List<Interactives.Element>();
            if (!IsInterior(interiorMapId)) return chests;
            foreach (var element in Interactives.ElementsOf(interiorMapId))
            {
                if (ChestGraphics.Contains(element.Gfx)) chests.Add(element);
            }
            return chests;
        }

        /// <summary>Whether this element is a chest of this interior.</summary>
        public static bool IsChest(long interiorMapId, int elementId)
        {
            foreach (var chest in ChestsIn(interiorMapId))
            {
                if (chest.Id == elementId) return true;
            }
            return false;
        }

        private static List<long> SortedKeys(Dictionary<long, List<Door>> source)
        {
            var keys = new List<long>(source.Keys);
            keys.Sort();
            return keys;
        }
    }
}

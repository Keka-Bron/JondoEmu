using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The zaapis of Bonta and Brakmar: the short transport inside the city.
    ///
    /// On the outside they work just like a zaap —it is clicked, the server sends the destination list and one
    /// chooses— but they are something else: they cost a fixed 20 kamas, they do not have to be activated and they only lead to
    /// places in their own city, mostly workshops and marketplaces.
    ///
    /// ─── Where each number comes from ───────────────────────────────────────────────────────
    ///
    /// The TYPE (106) and the SKILL (157) come from the captures: the real server sends them in every
    /// jss and every iwn. The GRAPHICS come from crossing the 304 captures with the client's dump
    /// —tools/tipos_interactivos.py does it— and there appeared what a single capture did not show:
    /// Bonta uses TWO different graphics, not one.
    ///
    /// ─── Why the destinations come from a capture ───────────────────────────────────────────
    ///
    /// The network cannot be deduced from the client. It was checked: of every six destinations, four are maps
    /// that have no zaapi of their own —they are the workshop or marketplace it takes you to—, so the list
    /// is not «the maps where there is one». The server sends it whole on using the element, and that is where
    /// it is taken from, just as the zaaps were done.
    ///
    /// Only Bonta and Brakmar are there because there are only captures of those two. The other 33 maps with a
    /// type 106 graphic —34925 and 70914— belong to neither of the two networks: they are the
    /// Saltadorillo and Frigost transporters, which move the same but have their own
    /// network. They are left out on purpose until their destinations are taken from the capture there is:
    /// registering them now would give an element that can be clicked and does nothing, which is worse than not
    /// having it. Adding them is putting their city in the .json and their graphic in <see cref="GraphicsOf"/>.
    /// </summary>
    public static class Zaapis
    {
        /// <summary>The type the client draws a zaapi with. Measured from the real jss.</summary>
        public const int Type = 106;

        /// <summary>The «use» skill, which the server returns in the iwn.</summary>
        public const int UseSkill = 157;

        /// <summary>What one hop costs, fixed. It comes out the same in all three captures.</summary>
        public const int Cost = 20;

        /// <summary>
        /// The tab where the client puts these destinations: 1.
        ///
        /// It goes in the f3 of each hjj entry and the client returns it in the hjc's f2. It appears in
        /// the 69 entries of the two zaapi captures. We did not send it, and that is why the
        /// client treated them as normal zaaps.
        /// </summary>
        public const int Kind = 1;

        /// <summary>
        /// Which teleporter it is, for the f4 of the hjj's ROOT: 0 the zaap, 1 the zaapi, 3 the
        /// boat. It is what decides which window the client opens.
        ///
        /// It has the same value as <see cref="Kind"/> by chance: that one goes in each destination and says which
        /// tab it falls in, this one goes only once and says which window opens. They are two different
        /// fields and are kept separate so that nobody confuses them the day they stop matching
        /// —the boat already does not match: its window is 3 and its destinations carry no tab—.
        /// </summary>
        public const int Teleporter = 1;

        /// <summary>The zone level that accompanies each destination in the list.</summary>
        private const int Level = 10;

        /// <summary>A network: a city's.</summary>
        public sealed class Network
        {
            public string City { get; init; } = "";
            public IReadOnlyList<Destination> Destinations { get; init; } = Array.Empty<Destination>();
        }

        /// <summary>A place the zaapi leads to.</summary>
        public readonly struct Destination
        {
            public Destination(long mapId, int subAreaId) { MapId = mapId; SubAreaId = subAreaId; }
            public long MapId { get; }
            public int SubAreaId { get; }
        }

        private static readonly Dictionary<int, Network> _byGfx = new();
        private static readonly Dictionary<long, Network> _byMap = new();

        public static int Count => _byMap.Count;
        public static IReadOnlyDictionary<int, Network> Networks => _byGfx;

        /// <summary>
        /// Loads the networks and works out which maps have a zaapi.
        ///
        /// A map belongs to a network by the GRAPHIC of its element, not by being in the destination
        /// list: one can leave from a zaapi even if that map is nobody's destination.
        /// </summary>
        public static void Initialize()
        {
            _byGfx.Clear();
            _byMap.Clear();

            string path = Paths.Resolve("zaapis_3.6.10.10.json");
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Zaapis] Falta {Path.GetFileName(path)}; sin el no hay zaapis. " +
                                  "Generalo con tools/extraer_zaapis.py.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var city in doc.RootElement.EnumerateObject())
                {
                    var destinations = new List<Destination>();
                    if (city.Value.TryGetProperty("destinos", out var list))
                    {
                        foreach (var d in list.EnumerateArray())
                        {
                            destinations.Add(new Destination(
                                d.GetProperty("mapa").GetInt64(),
                                d.TryGetProperty("subzona", out var s) ? s.GetInt32() : 0));
                        }
                    }

                    var network = new Network { City = city.Name, Destinations = destinations };
                    foreach (int gfx in GraphicsOf(city.Name)) _byGfx[gfx] = network;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Zaapis] No se ha podido leer la red: {ex.Message}");
                return;
            }

            foreach (long mapId in Interactives.MapIds)
            {
                foreach (var element in Interactives.ElementsOf(mapId))
                {
                    if (_byGfx.TryGetValue(element.Gfx, out var network)) _byMap[mapId] = network;
                }
            }

            int destinos = 0;
            foreach (var n in _byGfx.Values) destinos = Math.Max(destinos, n.Destinations.Count);
            Console.WriteLine($"[Zaapis] {_byMap.Count} mapas con zaapi en {_byGfx.Count} gráficos, " +
                              $"redes de {string.Join(" y ", CityNames())}.");
        }

        /// <summary>
        /// Which graphics each city uses.
        ///
        /// It goes by hand and with the list in front because it is what was measured, not a rule: Bonta uses
        /// two —70520 and 70521— and Brakmar one. Deducing it from the city's name would be inventing
        /// a correspondence nobody has checked.
        /// </summary>
        private static int[] GraphicsOf(string city) => city switch
        {
            "bonta" => new[] { 70520, 70521 },
            "brakmar" => new[] { 304418 },
            _ => Array.Empty<int>(),
        };

        private static IEnumerable<string> CityNames()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in _byGfx.Values) if (seen.Add(n.City)) yield return n.City;
        }

        /// <summary>The zaapis on this map.</summary>
        public static List<Interactives.Element> ElementsOn(long mapId)
        {
            var found = new List<Interactives.Element>();
            foreach (var element in Interactives.ElementsOf(mapId))
            {
                if (_byGfx.ContainsKey(element.Gfx)) found.Add(element);
            }
            return found;
        }

        /// <summary>The network this map belongs to, or null if there is no zaapi.</summary>
        public static Network? NetworkOn(long mapId)
            => _byMap.TryGetValue(mapId, out var network) ? network : null;
    }
}

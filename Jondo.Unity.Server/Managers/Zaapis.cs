using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Los zaapis de Bonta y Brakmar: el transporte corto dentro de la ciudad.
    ///
    /// Por fuera funcionan igual que un zaap —se clica, el servidor manda la lista de destinos y se
    /// elige— pero son otra cosa: cuestan 20 kamas fijos, no hay que activarlos y sólo llevan a
    /// sitios de su propia ciudad, sobre todo talleres y mercadillos.
    ///
    /// ─── De dónde sale cada número ──────────────────────────────────────────────────────────
    ///
    /// El TIPO (106) y la HABILIDAD (157) salen de las capturas: el servidor real los manda en cada
    /// jss y en cada iwn. Los GRÁFICOS salen de cruzar las 304 capturas con el volcado del cliente
    /// —lo hace tools/tipos_interactivos.py— y ahí apareció lo que una sola captura no enseñaba:
    /// Bonta usa DOS gráficos distintos, no uno.
    ///
    /// ─── Por qué los destinos vienen de una captura ─────────────────────────────────────────
    ///
    /// La red no se puede deducir del cliente. Se comprobó: de cada seis destinos, cuatro son mapas
    /// que no tienen zaapi propio —son el taller o el mercadillo al que te lleva—, así que la lista
    /// no es «los mapas donde hay uno». El servidor la manda entera al usar el elemento, y de ahí
    /// está sacada, igual que se hicieron los zaaps.
    ///
    /// Sólo están Bonta y Brakmar porque sólo hay capturas de esas dos. Los otros 33 mapas con
    /// gráfico de tipo 106 —34925 y 70914— no pertenecen a ninguna de las dos redes: son los
    /// transportadores saltadorillos y los frigosteños, que se mueven igual pero tienen su propia
    /// red. Se quedan fuera a propósito hasta que se saquen sus destinos de la captura que hay:
    /// registrarlos ahora daría un elemento que se puede clicar y no hace nada, que es peor que no
    /// tenerlo. Añadirlos es meter su ciudad en el .json y su gráfico en <see cref="GraphicsOf"/>.
    /// </summary>
    public static class Zaapis
    {
        /// <summary>El tipo con el que el cliente dibuja un zaapi. Medido del jss real.</summary>
        public const int Type = 106;

        /// <summary>La habilidad de «usar», que el servidor devuelve en el iwn.</summary>
        public const int UseSkill = 157;

        /// <summary>Lo que cuesta un salto, fijo. Sale igual en las tres capturas.</summary>
        public const int Cost = 20;

        /// <summary>
        /// La pestaña donde el cliente pone estos destinos: 1.
        ///
        /// Va en el f3 de cada entrada del hjj y el cliente lo devuelve en el f2 del hjc. Sale en
        /// las 69 entradas de las dos capturas de zaapi. Nosotros no lo mandábamos, y por eso el
        /// cliente los trataba como zaaps normales.
        /// </summary>
        public const int Kind = 1;

        /// <summary>
        /// Qué teletransportador es, para el f4 de la RAÍZ del hjj: 0 el zaap, 1 el zaapi, 3 el
        /// barco. Es lo que decide qué ventana abre el cliente.
        ///
        /// Vale lo mismo que <see cref="Kind"/> por casualidad: aquél va en cada destino y dice en
        /// qué pestaña cae, éste va una sola vez y dice qué ventana se abre. Son dos campos
        /// distintos y se dejan separados para que nadie los confunda el día que dejen de coincidir
        /// —el barco ya no coincide: su ventana es la 3 y sus destinos no llevan pestaña—.
        /// </summary>
        public const int Teleporter = 1;

        /// <summary>El nivel de zona que acompaña a cada destino en la lista.</summary>
        private const int Level = 10;

        /// <summary>Una red: la de una ciudad.</summary>
        public sealed class Network
        {
            public string City { get; init; } = "";
            public IReadOnlyList<Destination> Destinations { get; init; } = Array.Empty<Destination>();
        }

        /// <summary>Un sitio al que lleva el zaapi.</summary>
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
        /// Carga las redes y averigua en qué mapas hay zaapi.
        ///
        /// Un mapa pertenece a una red por el GRÁFICO de su elemento, no por estar en la lista de
        /// destinos: desde un zaapi se puede salir aunque ese mapa no sea destino de nadie.
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
        /// Qué gráficos usa cada ciudad.
        ///
        /// Va a mano y con la lista delante porque es lo que se ha medido, no una regla: Bonta usa
        /// dos —70520 y 70521— y Brakmar uno. Deducirlo del nombre de la ciudad sería inventarse
        /// una correspondencia que nadie ha comprobado.
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

        /// <summary>Los zaapis que hay en este mapa.</summary>
        public static List<Interactives.Element> ElementsOn(long mapId)
        {
            var found = new List<Interactives.Element>();
            foreach (var element in Interactives.ElementsOf(mapId))
            {
                if (_byGfx.ContainsKey(element.Gfx)) found.Add(element);
            }
            return found;
        }

        /// <summary>La red a la que pertenece este mapa, o null si no hay zaapi.</summary>
        public static Network? NetworkOn(long mapId)
            => _byMap.TryGetValue(mapId, out var network) ? network : null;
    }
}

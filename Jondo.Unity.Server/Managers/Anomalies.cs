using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Las anomalías temporales: la pestaña que sale al lado de la lista de zaaps.
    ///
    /// ─── No son zaaps sin activar ───────────────────────────────────────────────────────────
    ///
    /// Lo parecen: la tabla del cliente trae 62 zaaps y marca 15 como no activados, y esos 15 son
    /// justo los que llevan el dibujo 74685 en vez del 301199. Pero el dibujo no es «otro modelo de
    /// zaap», es el VESTIGIO, y el servidor real lo declara con tipo 359, no 16. Lo que hay en esos
    /// mapas no es un zaap apagado: es el sitio donde puede aparecer una anomalía.
    ///
    /// ─── Cómo viajan por el cable ───────────────────────────────────────────────────────────
    ///
    /// En el MISMO hjj que los zaaps y en el mismo campo repetido. Lo que las distingue son dos
    /// campos que el zaap normal no manda:
    ///
    ///   f3 = 4          la pestaña. 1 es el zaapi, 4 la anomalía, y el zaap no manda el campo.
    ///   f4 { f2, f3 }   el reloj: f2 minutos que le quedan, f3 los que dura.
    ///
    /// Y al elegirla el cliente contesta <c>hjc { f2: 4, f3: subzona }</c> — la SUBZONA, no el
    /// mapa, que es al revés que el zaap y el zaapi. Por eso se indexan por subzona.
    ///
    /// ─── Qué está medido y qué es nuestro ───────────────────────────────────────────────────
    ///
    /// Medido: los 120 minutos de duración (sale así en las 27 entradas), que el f2 baja de minuto
    /// en minuto —entre dos capturas separadas 70,9 segundos, cinco de las seis anomalías activas
    /// bajaron exactamente 1—, que el nivel es el de la subzona (16 de 16), que cuesta lo mismo que
    /// ir a ese mapa en zaap, y que se aterriza en el mapa 196085762, de la subzona 916,
    /// «Anomalías temporales».
    ///
    /// Nuestro: CUÁLES están activas. El servidor de Ankama rota unas seis cada dos horas y esa
    /// rotación no está en ningún dato del cliente. Aquí se ofrecen las dieciséis medidas, todas a
    /// la vez, cada una con su reloj. Es la decisión honesta: inventarse una rotación no la haría
    /// más real, sólo escondería la mitad de las anomalías la mitad del tiempo.
    /// </summary>
    public static class Anomalies
    {
        /// <summary>La pestaña donde el cliente las pone. 1 es el zaapi, 4 la anomalía.</summary>
        public const int Kind = 4;

        /// <summary>Una anomalía: dónde está su vestigio y de qué zona es.</summary>
        public readonly struct Anomaly
        {
            public Anomaly(long mapId, int subAreaId, int level, string name)
            {
                MapId = mapId; SubAreaId = subAreaId; Level = level; Name = name;
            }

            /// <summary>El mapa donde está el vestigio. Es lo que se cobra, como un zaap.</summary>
            public long MapId { get; }

            /// <summary>De qué zona es la anomalía. Es lo que el cliente manda en el hjc.</summary>
            public int SubAreaId { get; }

            public int Level { get; }
            public string Name { get; }
        }

        private static readonly List<Anomaly> _all = new();
        private static readonly Dictionary<int, Anomaly> _bySubArea = new();

        /// <summary>Cuántos minutos vive una anomalía. Del f4.f3 del hjj.</summary>
        public static int Duration { get; private set; } = 120;

        /// <summary>Dónde deja el servidor al viajar a una. Medido de la única captura que lo hace.</summary>
        public static long ArrivalMap { get; private set; }

        public static int Count => _all.Count;
        public static IReadOnlyList<Anomaly> All => _all;

        public static void Initialize()
        {
            _all.Clear();
            _bySubArea.Clear();

            string path = Paths.ServerData("anomalias_3.6.10.10.json");
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Anomalías] Falta {Path.GetFileName(path)}; sin él no hay " +
                                  "pestaña de anomalías. Genéralo con tools/extraer_anomalias.py.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;

                if (root.TryGetProperty("duracion", out var duration) && duration.GetInt32() > 0)
                    Duration = duration.GetInt32();
                if (root.TryGetProperty("mapaDestino", out var arrival))
                    ArrivalMap = arrival.GetInt64();

                if (root.TryGetProperty("anomalias", out var list))
                {
                    foreach (var entry in list.EnumerateArray())
                    {
                        var anomaly = new Anomaly(
                            entry.GetProperty("mapa").GetInt64(),
                            entry.GetProperty("subzona").GetInt32(),
                            entry.TryGetProperty("nivel", out var level) ? level.GetInt32() : 0,
                            entry.TryGetProperty("nombre", out var name) ? (name.GetString() ?? "") : "");

                        if (anomaly.SubAreaId == 0) continue;
                        _all.Add(anomaly);
                        _bySubArea[anomaly.SubAreaId] = anomaly;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Anomalías] No se ha podido leer la lista: {ex.Message}");
                return;
            }

            // Si no hay mapa de destino no se ofrece ninguna: una anomalía que no lleva a ningún
            // sitio es una entrada en la lista que al clicarla no hace nada, y eso es peor que no
            // enseñarla.
            if (ArrivalMap == 0 && _all.Count > 0)
            {
                Console.WriteLine("[Anomalías] La lista no dice a qué mapa se viaja; no se ofrecen.");
                _all.Clear();
                _bySubArea.Clear();
                return;
            }

            Console.WriteLine($"[Anomalías] {_all.Count} activas, {Duration} minutos cada una, " +
                              $"se entra por el mapa {ArrivalMap}.");
        }

        public static bool TryGet(int subAreaId, out Anomaly anomaly)
            => _bySubArea.TryGetValue(subAreaId, out anomaly);

        /// <summary>
        /// Los minutos que le quedan a una anomalía.
        ///
        /// El reloj de verdad lo lleva el servidor de Ankama y no está en ningún dato del cliente,
        /// así que éste es nuestro: baja de minuto en minuto hasta uno y vuelve a empezar, igual
        /// que se ve en las capturas. El desfase por subzona es para que no caduquen todas a la vez
        /// —en las capturas cada una llevaba su cuenta— y sale de la propia subzona para que sea
        /// estable entre arranques sin tener que guardarlo en ningún sitio.
        /// </summary>
        public static int MinutesLeft(int subAreaId)
        {
            if (Duration <= 0) return 0;
            long minutes = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
            int offset = Math.Abs(subAreaId) % Duration;
            return Duration - (int)((minutes + offset) % Duration);
        }
    }
}

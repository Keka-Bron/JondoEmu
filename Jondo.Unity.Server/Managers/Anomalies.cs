using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The temporal anomalies: the tab that appears next to the zaaps list.
    ///
    /// ─── They are not unactivated zaaps ─────────────────────────────────────────────────────
    ///
    /// They look like it: the client's table carries 62 zaaps and marks 15 as not activated, and those
    /// 15 are exactly the ones carrying drawing 74685 instead of 301199. But the drawing is not «another
    /// zaap model», it is the VESTIGE, and the real server declares it with type 359, not 16. What
    /// there is on those maps is not a switched-off zaap: it is the place where an anomaly can appear.
    ///
    /// ─── How they travel on the wire ────────────────────────────────────────────────────────
    ///
    /// In the SAME hjj as the zaaps and in the same repeated field. What tells them apart are two fields
    /// the normal zaap does not send:
    ///
    ///   f3 = 4          the tab. 1 is the zaapi, 4 the anomaly, and the zaap does not send the field.
    ///   f4 { f2, f3 }   the clock: f2 minutes it has left, f3 how long it lasts.
    ///
    /// And on choosing it the client answers <c>hjc { f2: 4, f3: subarea }</c> -- the SUBAREA, not the
    /// map, which is the reverse of the zaap and the zaapi. That is why they are indexed by subarea.
    ///
    /// ─── What is measured and what is ours ──────────────────────────────────────────────────
    ///
    /// Measured: the 120 minutes of duration (it comes out like that in the 27 entries), that f2 goes
    /// down minute by minute -- between two captures 70.9 seconds apart, five of the six active
    /// anomalies went down exactly 1 --, that the level is the subarea's (16 of 16), that it costs the
    /// same as going to that map by zaap, and that one lands on map 196085762, of subarea 916,
    /// «Anomalías temporales».
    ///
    /// Ours: WHICH ones are active. Ankama's server rotates about six every two hours and that rotation
    /// is in no client data. Here the sixteen measured ones are offered, all at once, each with its
    /// clock. It is the honest decision: making up a rotation would not make it more real, it would
    /// only hide half the anomalies half the time.
    /// </summary>
    public static class Anomalies
    {
        /// <summary>The tab where the client puts them. 1 is the zaapi, 4 the anomaly.</summary>
        public const int Kind = 4;

        /// <summary>An anomaly: where its vestige is and which zone it belongs to.</summary>
        public readonly struct Anomaly
        {
            public Anomaly(long mapId, int subAreaId, int level, string name)
            {
                MapId = mapId; SubAreaId = subAreaId; Level = level; Name = name;
            }

            /// <summary>The map where the vestige is. It is what is charged, like a zaap.</summary>
            public long MapId { get; }

            /// <summary>Which zone the anomaly belongs to. It is what the client sends in the hjc.</summary>
            public int SubAreaId { get; }

            public int Level { get; }
            public string Name { get; }
        }

        private static readonly List<Anomaly> _all = new();
        private static readonly Dictionary<int, Anomaly> _bySubArea = new();

        /// <summary>
        /// Whether the list is read in and safe to use. Volatile because the fast path in
        /// <see cref="Ensure"/> reads it outside the lock, and raised LAST so a reader never
        /// sees the tables half filled.
        /// </summary>
        private static volatile bool _loaded;
        private static readonly object _lock = new object();

        private static int _duration = 120;
        private static long _arrivalMap;

        /// <summary>How many minutes an anomaly lives. From the hjj's f4.f3.</summary>
        public static int Duration { get { Ensure(); return _duration; } }

        /// <summary>Where the server leaves one on travelling to one. Measured from the only capture that does it.</summary>
        /// <remarks>
        /// EVERY reader here goes through Ensure, this one above all: ZaapTravelHandler asks for
        /// the arrival map BEFORE it asks for the list, and bails out when GetMapInfo cannot find
        /// it. Answering zero because nothing had been read in yet would make the whole anomaly
        /// tab vanish, and it would not log a thing on the way out.
        /// </remarks>
        public static long ArrivalMap { get { Ensure(); return _arrivalMap; } }

        public static int Count { get { Ensure(); return _all.Count; } }
        public static IReadOnlyList<Anomaly> All { get { Ensure(); return _all; } }

        /// <summary>
        /// Reads the list, once per run. Kept as a separate call so the server pays for it at
        /// boot, with its log line, instead of on whoever first opens the travel window.
        /// </summary>
        /// <remarks>
        /// Calling it again does nothing, on purpose: the json is a measurement that does not
        /// change while the server is up, and clearing the tables to re-read it was what let a
        /// reader catch them empty.
        /// </remarks>
        public static void Initialize() => Ensure();

        private static void Ensure()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try
                {
                    Load();
                }
                finally
                {
                    // In a finally so a missing file counts as tried: the early returns below
                    // would otherwise send every travel request back to the disk.
                    _loaded = true;
                }
            }
        }

        private static void Load()
        {
            string path = Paths.Resolve("anomalias_3.6.10.10.json");
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
                    _duration = duration.GetInt32();
                if (root.TryGetProperty("mapaDestino", out var arrival))
                    _arrivalMap = arrival.GetInt64();

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

            // If there is no destination map none is offered: an anomaly that leads nowhere is an
            // entry in the list that does nothing when clicked, and that is worse than not showing it.
            if (_arrivalMap == 0 && _all.Count > 0)
            {
                Console.WriteLine("[Anomalías] La lista no dice a qué mapa se viaja; no se ofrecen.");
                _all.Clear();
                _bySubArea.Clear();
                return;
            }

            Console.WriteLine($"[Anomalías] {_all.Count} activas, {_duration} minutos cada una, " +
                              $"se entra por el mapa {_arrivalMap}.");
        }

        public static bool TryGet(int subAreaId, out Anomaly anomaly)
        {
            Ensure();
            return _bySubArea.TryGetValue(subAreaId, out anomaly);
        }

        /// <summary>
        /// The minutes an anomaly has left.
        ///
        /// The real clock is kept by Ankama's server and is in no client data, so this one is ours: it goes
        /// down minute by minute to one and starts again, the same as seen in the captures. The per-subarea
        /// offset is so that they do not all expire at once -- in the captures each kept its own count --
        /// and it comes from the subarea itself so that it is stable between starts without having to be
        /// stored anywhere.
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

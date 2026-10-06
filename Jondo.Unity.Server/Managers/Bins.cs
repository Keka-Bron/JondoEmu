using System;
using System.Collections.Generic;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The bins: the public storage where what people throw away goes.
    ///
    /// In the real game they keep what others have thrown away. Here they start EMPTY on purpose:
    /// nobody has thrown anything on this server yet, and filling them with invented items would
    /// put things in the world that come from nowhere. What players throw in stays, for anyone,
    /// through restarts: each bin is a storage of its own in <see cref="StorageStacks"/>, and
    /// <see cref="Handlers.BinHandler"/> opens it.
    ///
    /// ─── Where each number comes from ───────────────────────────────────────────────────────
    ///
    /// The TYPE (105) and the SKILL (153) come from the capture of the bin in front of Bonta's bank:
    /// the real server sends them in the jss and the iwn.
    ///
    /// The GRAPHICS come from crossing the 304 captures with the client dump
    /// -- tools/tipos_interactivos.py --, and that is the reason for not trusting a single capture:
    /// Bonta's showed graphic 260022 and with it 31 bins came out. There are four different
    /// graphics, and in total there are <b>67</b> spread over 63 maps.
    /// </summary>
    public static class Bins
    {
        /// <summary>The type the client draws a bin with. Measured from the real jss.</summary>
        public const int Type = 105;

        /// <summary>The «use» skill, which the server returns in the iwn.</summary>
        public const int UseSkill = 153;

        /// <summary>
        /// The four looks a bin has.
        ///
        /// They are not decorative variants: each city uses its own, and with only one 36 of the 67
        /// were left out.
        /// </summary>
        private static readonly HashSet<int> Graphics = new() { 8438, 46529, 63081, 260022 };

        private static readonly Dictionary<long, List<Interactives.Element>> _byMap = new();

        public static int Count { get; private set; }
        public static int MapCount => _byMap.Count;

        public static void Initialize()
        {
            _byMap.Clear();
            Count = 0;

            foreach (long mapId in Interactives.MapIds)
            {
                List<Interactives.Element>? here = null;
                foreach (var element in Interactives.ElementsOf(mapId))
                {
                    if (!Graphics.Contains(element.Gfx)) continue;
                    (here ??= new List<Interactives.Element>()).Add(element);
                    Count++;
                }
                if (here != null) _byMap[mapId] = here;
            }

            Console.WriteLine($"[Papeleras] {Count} en {_byMap.Count} mapas.");
        }

        /// <summary>The bins on this map.</summary>
        public static IReadOnlyList<Interactives.Element> On(long mapId)
            => _byMap.TryGetValue(mapId, out var found)
                ? found
                : (IReadOnlyList<Interactives.Element>)Array.Empty<Interactives.Element>();
    }
}

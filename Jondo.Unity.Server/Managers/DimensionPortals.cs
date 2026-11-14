using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The divine dimensions' old portals, kept as a nod to them: Anutropía's, Sramvil's,
    /// Xelorium's and Zurcalia's, standing for good in the Amakna village zaap's square, where
    /// Jondo's vendors are, crossed as often as anybody likes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game took its world portals out with its 3.2 update (July 2025, Osavora): before, each
    /// stood on a random outdoor map for 128 crossings or a day unused (JOL, Dimensions Divines);
    /// since, the way in is the Travelers' Tower's portal with its alteration. The 3.6 client
    /// still draws and crosses them, and the server's owner wanted them back -- here, always open.
    /// </para>
    /// <para>
    /// No capture shows one; the client has them all (Ghidra, 3.6.10.11). A portal is a map actor
    /// of kind 3 (lnh { f2 the portal, f3 the dimension's area }), drawn with its NPC's look and
    /// named after the area. Its menu offers «Hablar» -- the NPC dialogue (iov 3), whose ioc
    /// carries the portal's type in f2 so that the client makes the portal's NPC speak (fsw:
    /// 1 → 2374 «Portal de Anutropía», 2 → 2594, 3 → 2667, 4 → 3136) -- and «Usar», which asks
    /// to be sure and sends hhu { f3 the portal }. The NPC's sentence is «Queda(n) #1
    /// utilización(es) antes de que se cierre el portal. El portal desaparecerá en $time#2 si
    /// nadie lo utiliza», #2 a moment in unix seconds; its one reply goes through.
    /// </para>
    /// <para>
    /// The square as tools/dibujar_mapa.py draws it: the four stand on row 21 (297, 299, 301, 303),
    /// walkable and free, between the zaap and the vendors of the south square, leaving 300 -- the
    /// way to the zaap -- open.
    /// </para>
    /// </remarks>
    public static class DimensionPortals
    {
        /// <summary>The Amakna village zaap's map, the vendors' (datos/vendedores_jondo.json).</summary>
        public const long VillageMap = 88212759;

        /// <summary>Where each portal stands, by its type: 1 Anutropía … 4 Zurcalia.</summary>
        private static readonly int[] Cells = { 297, 299, 301, 303 };

        /// <summary>The way they face, as the vendors near them do.</summary>
        public const int Facing = 1;

        /// <summary>The portals' actor ids, one each, below everything else's.</summary>
        public const long ContextBase = -980000;

        /// <summary>What the sentence says of the crossings left: they do not run out.</summary>
        public const string UsesLeft = "∞";

        /// <summary>How far off the sentence puts the closing: a century, which is to say never.</summary>
        public const int ClosesInYears = 100;

        public sealed class Portal
        {
            public TravelersTower.Dimension Dimension { get; init; } = null!;
            public long MapId { get; init; }
            public int Cell { get; init; }

            public int Type => Dimension.PortalType;
            public long ContextId => ContextBase - Type;
        }

        private static readonly IReadOnlyList<Portal> _portals = TravelersTower.Dimensions
            .Where(d => d.PortalType > 0)
            .Select(d => new Portal { Dimension = d, MapId = VillageMap, Cell = Cells[d.PortalType - 1] })
            .ToList();

        /// <summary>The four portals.</summary>
        public static IReadOnlyList<Portal> All => _portals;

        public static Portal? OfType(int type) => _portals.FirstOrDefault(p => p.Type == type);

        public static Portal? ByContext(long contextId) => _portals.FirstOrDefault(p => p.ContextId == contextId);

        public static IEnumerable<Portal> OnMap(long mapId) => _portals.Where(p => p.MapId == mapId);
    }
}

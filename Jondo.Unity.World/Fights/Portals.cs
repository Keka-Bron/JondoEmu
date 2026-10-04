using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.World.Fights
{
    /// <summary>
    /// A portal on the ground (effect 1181, "Coloca un portal"): the Selatrop's marks, which carry
    /// whoever steps on them and whatever spell is cast at them to another portal of the same
    /// network.
    /// </summary>
    /// <remarks>
    /// The row that lays one writes, as every portal row of the class does, "dn=2 ds=44338 v=0":
    /// the value and the dice are the "+#3% daños, +#1% de daños por casilla que separe entre 2
    /// portales" of its text, and the side of the dice is the level id of Teleportal (14573 grade
    /// 1), which the jwe 401 carries in its f2 as a glyph carries its spell's grade.
    /// </remarks>
    public sealed class Portal
    {
        /// <summary>The mark number the clients see: the f4 of its jwe 401, the f1 of its 1181.</summary>
        public int Id { get; set; }

        /// <summary>Whose network it belongs to: the fighter that laid it.</summary>
        public long Owner { get; init; }

        public int Cell { get; init; }

        /// <summary>The spell that laid it and its grade, the f9 and f6 of its jwe 401.</summary>
        public int LayingSpell { get; init; }
        public int Grade { get; init; }

        /// <summary>The placing row's dice and value: see the remarks of the class.</summary>
        public int DiceNum { get; init; }
        public int DiceSide { get; init; }
        public int Value { get; init; }

        /// <summary>
        /// Crossed this turn -- the portal walked into and the one walked out of. Off until the
        /// next turn begins, whoever's it is: "pegar a traves de diferentes portales", frames
        /// 186-187 and 262-263.
        /// </summary>
        public bool Used { get; set; }

        /// <summary>
        /// Switched off by a 1183 ("Desactiva un portal"), until the turn of the one who did it
        /// begins again -- its duration of 1: "usar neutral en portales", frames 7 and 70.
        /// Zero when nobody did.
        /// </summary>
        public long NeutralisedBy { get; set; }

        /// <summary>The state the clients were last told: the f11 of the 401, then the 1181s.</summary>
        public bool Active { get; set; }

        public override string ToString() => $"portal {Id} of {Owner} on {Cell}";
    }

    /// <summary>
    /// Every portal of a fight, and the rules that tie them, read off the six Selatrop captures.
    /// </summary>
    /// <remarks>
    /// <para>What is measured:</para>
    /// <list type="bullet">
    /// <item>Four per owner: the fifth takes the oldest off first, "jwe 310" then the "jwe 401" of
    /// the new one ("poner portales de selatrop", frames 234-235 and five more).</item>
    /// <item>A portal is active when it can be used and another of its network can too: the first
    /// one goes down inactive, the second turns it on (frames 109-132); a portal left alone by
    /// the others' use goes off ("trascendencia y resonancia", frame 228) and comes back with the
    /// next one laid (frame 243).</item>
    /// <item>It cannot be used when it was crossed this turn, when a 1183 switched it off, or when
    /// somebody stands on it: the Selatrop standing on the portal he came out of keeps it off at
    /// the next turn start while the one he went in through comes back ("usar neutral",
    /// frames 187-212); Estela's portal under its caster goes off until he has jumped away.</item>
    /// <item>The chain: from the portal entered, the nearest usable portal of the network not yet
    /// in the chain, again and again; the last one is the way out. Nearest is the fight
    /// distance, and between two as near the newest goes first -- 9 to 10 rather than to 8 in
    /// "pegar", 11 to 12 rather than to 10 in "usar neutral", both three cells away.</item>
    /// <item>A spell cast at a portal lands where the vector from the caster to that portal
    /// leads from the way out: seven projections across three captures, cell for cell.</item>
    /// </list>
    /// <para>
    /// INFERRED: that a portal's cell occupied by its own owner does not keep it off at the moment
    /// it is laid -- the only way both Estela's "401 f11=1" under its caster and Resonancia's
    /// "401" without it under an enemy come out of one rule -- and that only the owner's portals
    /// make a network.
    /// </para>
    /// </remarks>
    public sealed class PortalNetwork
    {
        /// <summary>How many portals one owner keeps: the fifth pushes the oldest out.</summary>
        public const int PerOwner = 4;

        private readonly List<Portal> _portals = new List<Portal>();

        public IReadOnlyList<Portal> All => _portals;

        public int Count => _portals.Count;

        public Portal At(int cell) => _portals.FirstOrDefault(p => p.Cell == cell);

        public Portal ById(int id) => _portals.FirstOrDefault(p => p.Id == id);

        public IEnumerable<Portal> Of(long owner) => _portals.Where(p => p.Owner == owner);

        /// <summary>The portal a new one of this owner pushes out, or null while he keeps fewer than four.</summary>
        public Portal Displaced(long owner)
        {
            var his = _portals.Where(p => p.Owner == owner).OrderBy(p => p.Id).ToList();
            return his.Count >= PerOwner ? his[0] : null;
        }

        public void Add(Portal portal) => _portals.Add(portal);

        public bool Remove(Portal portal) => _portals.Remove(portal);

        /// <summary>
        /// Whether a portal can be used: not crossed this turn, not switched off, and nobody on it
        /// but, when named, <paramref name="ignoring"/> -- the one about to go through it.
        /// </summary>
        public static bool Usable(Portal portal, Func<int, long> occupantOf, long ignoring = 0)
        {
            if (portal == null || portal.Used || portal.NeutralisedBy != 0) return false;
            long occupant = occupantOf?.Invoke(portal.Cell) ?? 0;
            return occupant == 0 || occupant == ignoring;
        }

        /// <summary>Whether a portal should be on: usable itself, and another of its network usable too.</summary>
        public bool ShouldBeActive(Portal portal, Func<int, long> occupantOf, long ignoring = 0)
        {
            if (!Usable(portal, occupantOf, ignoring)) return false;
            return _portals.Any(p => p != portal && p.Owner == portal.Owner && Usable(p, occupantOf));
        }

        /// <summary>
        /// Brings every portal's state up to date and returns the ones whose state changed, in the
        /// order of their numbers: the order of the 1181s the real server sends when a turn
        /// begins -- "7, 8, 9, 10" at frames 434-437 of "pegar".
        /// </summary>
        public List<Portal> Refresh(Func<int, long> occupantOf)
        {
            var changed = new List<Portal>();
            foreach (var portal in _portals.OrderBy(p => p.Id))
            {
                bool now = ShouldBeActive(portal, occupantOf);
                if (now == portal.Active) continue;
                portal.Active = now;
                changed.Add(portal);
            }
            return changed;
        }

        /// <summary>
        /// A turn begins: what was crossed can be used again, and what the one beginning switched
        /// off comes back on.
        /// </summary>
        public void TurnBegins(long fighter)
        {
            foreach (var portal in _portals)
            {
                portal.Used = false;
                if (portal.NeutralisedBy == fighter) portal.NeutralisedBy = 0;
            }
        }

        /// <summary>
        /// The portals a crossing goes through, the one entered first and the way out last; fewer
        /// than two when there is no way out. <paramref name="traveller"/> is the one going
        /// through, whose own cell does not keep the entry off.
        /// </summary>
        public List<Portal> Chain(Portal entry, Func<int, long> occupantOf, long traveller = 0)
        {
            var chain = new List<Portal>();
            if (entry == null) return chain;
            chain.Add(entry);

            var current = entry;
            while (true)
            {
                Portal next = null;
                int best = int.MaxValue;
                foreach (var candidate in _portals)
                {
                    if (candidate.Owner != entry.Owner || chain.Contains(candidate)) continue;
                    if (!Usable(candidate, occupantOf)) continue;
                    int distance = MapGeometry.Distance(current.Cell, candidate.Cell);
                    if (distance < best || (distance == best && next != null && candidate.Id > next.Id))
                    {
                        best = distance;
                        next = candidate;
                    }
                }
                if (next == null) break;
                chain.Add(next);
                current = next;
            }
            return chain;
        }

        /// <summary>
        /// Where a spell cast at the entry of a chain lands: the vector from the caster to the
        /// entry, laid from the way out. Minus one when it falls off the board.
        /// </summary>
        public static int Projection(int casterCell, int entryCell, int exitCell)
        {
            var (cx, cy) = MapGeometry.CellToPoint(casterCell);
            var (ex, ey) = MapGeometry.CellToPoint(entryCell);
            var (ox, oy) = MapGeometry.CellToPoint(exitCell);
            if (cx == int.MinValue || ex == int.MinValue || ox == int.MinValue) return -1;
            return MapGeometry.PointToCell(ox + (ex - cx), oy + (ey - cy));
        }

        /// <summary>The cells that separate the portals of a chain, one to the next, added up.</summary>
        public static int CellsBetween(IReadOnlyList<Portal> chain)
        {
            int total = 0;
            for (int i = 1; i < (chain?.Count ?? 0); i++)
                total += MapGeometry.Distance(chain[i - 1].Cell, chain[i].Cell);
            return total;
        }

        /// <summary>
        /// The bonus, in percent, of what goes through a chain: the entry's "+#3%" and its "#1%
        /// per cell between two portals". INFERRED from the effect's own text; no capture holds a
        /// blow of known stats sent both ways.
        /// </summary>
        public static int BonusPercent(IReadOnlyList<Portal> chain)
        {
            if (chain == null || chain.Count < 2) return 0;
            var entry = chain[0];
            return Math.Max(0, entry.Value) + Math.Max(0, entry.DiceNum) * CellsBetween(chain);
        }
    }
}

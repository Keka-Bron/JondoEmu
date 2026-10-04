using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>One wall: the bombs that hold it up and the ground between them.</summary>
    public sealed class BombWall
    {
        public BombWall(long owner, int template, IReadOnlyList<Fighter> bombs,
                        IReadOnlyList<int> cells)
        {
            Owner = owner;
            Template = template;
            Bombs = bombs;
            Cells = cells;
        }

        public long Owner { get; }
        public int Template { get; }
        public IReadOnlyList<Fighter> Bombs { get; }

        /// <summary>The cells BETWEEN the bombs. The bombs' own cells are not part of it.</summary>
        public IReadOnlyList<int> Cells { get; }

        public bool Covers(int cell) => Cells.Contains(cell);

        public override string ToString()
            => $"muro de {Bombs.Count} bomba(s) {Template} de {Owner}, {Cells.Count} casilla(s)";
    }

    /// <summary>
    /// The walls the Rogue's bombs raise between themselves.
    /// </summary>
    /// <remarks>
    /// From the client's own class sheet: "Cuando hay al menos 2 bombas alineadas y espaciadas de
    /// 2 a 6 casillas máximo, forman automáticamente un muro de bombas. Se trata de un glifo en el
    /// suelo que no bloquea los desplazamientos ni las líneas de visión. Un muro puede estar
    /// formado por tres bombas como mucho." The "2 a 6" is the room between the bombs, not
    /// their distance -- see <see cref="MaxGap"/>.
    ///
    /// Nothing places a wall and nothing removes one: it is a FUNCTION of where the bombs are, so
    /// it is computed on demand and never stored. A bomb that dies takes its wall with it without
    /// anybody having to remember to clean up, and one that is pushed into line raises a wall on
    /// the spot -- which is what the sheet means by "automáticamente".
    ///
    /// Two things here are inference rather than measurement, and both are named as such:
    ///
    /// - THE BOMBS MUST BE OF THE SAME KIND. The sheet never says so outright, but it speaks of
    ///   "muros de aire, es decir, formados por tornabombas" and the catalogue has one wall spell
    ///   per element -- 13458 fire, 13461 air, 13465 water, 13501 earth -- with no spell for a
    ///   mixed one. A wall of two different bombs would have no spell to cast.
    /// - "ALIGNED" IS THE GAME'S OWN SENSE of the word: same row or same column of the isometric
    ///   grid, which in (x, y) means sharing one coordinate. Diagonals do not count.
    /// </remarks>
    public static class BombWalls
    {
        /// <summary>
        /// The closest and furthest two bombs can stand and still hold a wall, as a distance
        /// between their cells: 2 is one cell between them, 7 is six.
        /// </summary>
        /// <remarks>
        /// MEASURED, and the sheet's "de 2 a 6 casillas" turned out to count the cells BETWEEN
        /// the bombs, not the distance: read as a distance it refused the widest wall a player
        /// can lay. Every wall the real server raised in the 22 Tymador captures, re-announced
        /// as a burst of 401 glyphs whenever a bomb lands or moves, scored against the two
        /// bombs holding it: distance 2 walled 4 times (245-274 with 260 between), 3 six times,
        /// 4 six, 5 ten, 6 five, and 7 four times -- frame 4213 of "explobomba-tornabomba...":
        /// bombs on 144 and 245 with all six cells 158, 173, 187, 202, 216, 231 announced. At 8
        /// the wall is refused: frame 8192 of the same capture summons a bomb on 129 with one on
        /// 245, the server takes every wall down and puts back only the 245-303 one.
        /// </remarks>
        public const int MinGap = 2;
        public const int MaxGap = 7;

        /// <summary>"Un muro puede estar formado por tres bombas como mucho."</summary>
        public const int MaxBombs = 3;

        /// <summary>
        /// Which spell each wall throws at whoever walks into it.
        /// </summary>
        /// <remarks>
        /// STILL MEASURED, unlike the explosion table next door. The client's SpellBombData does
        /// carry a <c>wallId</c> for every bomb -- 2 fire, 3 air, 4 water, 5 earth -- but the
        /// table those ids point INTO is not in the dump, so there is nothing to read the spell
        /// from. The four here come from the catalogue's own element groups: 13458 Muro de Fuego
        /// is type 2320 with the fire explosion, 13461 is 2321 with the air one, 13465 is 2322,
        /// 13501 is 2323, and the wallIds run 2, 3, 4, 5 in that same order.
        /// </remarks>
        public static readonly IReadOnlyDictionary<int, int> WallSpell = new Dictionary<int, int>
        {
            [3112] = 13458,   // Explobomba     -> Muro de Fuego
            [3113] = 13461,   // Tornabomba     -> Muro de Aire
            [3114] = 13465,   // Bomba de agua  -> Muro de Agua
            [5161] = 13501,   // Sismobomba     -> Muro de Tierra
        };

        /// <summary>Whether this glyph on the ground is one of the four bomb walls.</summary>
        public static bool IsWall(Glifo glyph)
            => glyph != null && WallSpell.Values.Contains(glyph.Hechizo);

        /// <summary>The state that shields from explosions and from walls.</summary>
        /// <remarks>
        /// Not written by hand: it is the value of effect 950 in the two spells that apply it,
        /// 13450 "Kabum" -- the very one the class sheet uses as that paragraph icon -- and 13489
        /// "Impostura", both under the mask "a,f3112,f3113,f3114,f5161": allies yes, bombs no.
        /// </remarks>
        public const int KabumState = 92;

        /// <summary>
        /// Whether a wall goes off under this fighter.
        /// </summary>
        /// <remarks>
        /// Three rules, and all three are measured or written down:
        ///
        /// - KABUM SPARES. "Los hechizos Kabum e Impostura permiten aplicar el estado Kabum al
        ///   lanzador y a sus aliados, que los protege de los danos de las explosiones y de los
        ///   muros." Which is also what says the wall hits its OWN Rogue without it: the sheet
        ///   calls the victim "una entidad", not an enemy.
        /// - ITS OWN BOMBS ARE NEVER CAUGHT. Three of the eight displacements onto a wall cell in
        ///   the captures are bombs of the Rogue holding the wall up -- frames 10552 and 10645 of
        ///   "explobomba-tornabomba-...-explotandolas" and 337 of "tymador-cruce" -- and none of
        ///   the three sets it off. Which is the only way Imantacion can work at all: it drags the
        ///   bombs along the very line they are holding.
        /// - ONCE A TURN, BUT ONLY WHEN PUSHED. See <see cref="FightInstance.WallHitThisTurn"/>.
        ///   Walking is exempt: "caminar en el muro no se ve afectado por este limite".
        /// </remarks>
        public static bool Catches(FightInstance fight, Glifo wall, Fighter who,
                                   bool byDisplacement)
        {
            if (wall == null || who == null) return false;
            if (who.Buffs.Estados.Contains(KabumState)) return false;
            if (who.EsInvocado && who.Invocador == wall.Dueno && Bombs.Is(who.MonsterId))
                return false;
            if (byDisplacement && fight != null && fight.WallHitThisTurn.Contains(who.Id))
                return false;
            return true;
        }

        /// <summary>
        /// The cells that stop a displacement dead: whatever wall would catch this fighter.
        /// </summary>
        public static HashSet<int> StoppingCells(FightInstance fight, Fighter who)
        {
            var cells = new HashSet<int>();
            if (fight == null || who == null) return cells;

            foreach (var glyph in fight.Glifos)
            {
                if (!IsWall(glyph)) continue;
                if (!Catches(fight, glyph, who, byDisplacement: true)) continue;
                foreach (int cell in glyph.Casillas) cells.Add(cell);
            }
            return cells;
        }

        /// <summary>Every wall a fighter's bombs are holding up right now.</summary>
        public static List<BombWall> Of(IEnumerable<Fighter> everybody, Fighter owner)
        {
            var walls = new List<BombWall>();
            if (owner == null) return walls;

            var bombs = everybody
                .Where(f => f != null && f.IsAlive && f.EsInvocado && f.Invocador == owner.Id
                            && WallSpell.ContainsKey(f.MonsterId))
                .ToList();
            if (bombs.Count < 2) return walls;

            foreach (var byTemplate in bombs.GroupBy(f => f.MonsterId))
            {
                // Two passes, one per axis: bombs sharing an X stand in a column, bombs sharing
                // a Y stand in a row.
                foreach (bool alongY in new[] { true, false })
                {
                    var lines = byTemplate.GroupBy(f => alongY
                        ? MapGeometry.CellToPoint(f.CellId).X
                        : MapGeometry.CellToPoint(f.CellId).Y);

                    foreach (var line in lines)
                    {
                        var inOrder = line
                            .OrderBy(f => alongY
                                ? MapGeometry.CellToPoint(f.CellId).Y
                                : MapGeometry.CellToPoint(f.CellId).X)
                            .ToList();
                        if (inOrder.Count < 2) continue;

                        walls.AddRange(Chains(owner.Id, byTemplate.Key, inOrder, alongY));
                    }
                }
            }
            return walls;
        }

        /// <summary>The wall covering a cell, if any of this fighter's walls does.</summary>
        public static BombWall Covering(IEnumerable<Fighter> everybody, Fighter owner, int cell)
            => Of(everybody, owner).FirstOrDefault(wall => wall.Covers(cell));

        /// <summary>
        /// The other bombs this one shares a wall with. Its own summoner's, and nobody else's.
        /// </summary>
        /// <remarks>
        /// One hop, not the whole web: whoever calls this is walking a chain and will ask again
        /// for each bomb it reaches, so the transitive closure falls out of the walk. And it comes
        /// out right even when four bombs stand in a row, because a wall holds three at most and
        /// the third one belongs to BOTH walls -- so the chain crosses at the joint.
        ///
        /// From the class sheet: "Si una bomba está unida a otras por un muro y explota, hará
        /// explotar también a las otras bombas del muro."
        /// </remarks>
        public static IEnumerable<Fighter> LasDelMismoMuro(IEnumerable<Fighter> everybody,
                                                           Fighter bomba)
        {
            if (bomba == null) return Enumerable.Empty<Fighter>();

            var todos = everybody as IReadOnlyCollection<Fighter> ?? everybody.ToList();
            var dueno = todos.FirstOrDefault(f => f != null && f.Id == bomba.Invocador);
            if (dueno == null) return Enumerable.Empty<Fighter>();

            return Of(todos, dueno)
                .Where(wall => wall.Bombs.Contains(bomba))
                .SelectMany(wall => wall.Bombs)
                .Where(other => other != bomba)
                .Distinct();
        }

        /// <summary>
        /// Walks one line of bombs and cuts it into walls: consecutive ones join while the gap
        /// stays inside the allowed range, and a wall closes as soon as it holds three.
        /// </summary>
        private static IEnumerable<BombWall> Chains(long owner, int template,
                                                    IReadOnlyList<Fighter> inOrder, bool alongY)
        {
            var current = new List<Fighter> { inOrder[0] };

            for (int i = 1; i < inOrder.Count; i++)
            {
                int gap = MapGeometry.Distance(current[^1].CellId, inOrder[i].CellId);
                bool joins = gap >= MinGap && gap <= MaxGap;

                if (joins) current.Add(inOrder[i]);

                if (!joins || current.Count == MaxBombs)
                {
                    if (current.Count >= 2) yield return Build(owner, template, current, alongY);
                    // A bomb can only hold one wall at a time, so the next chain starts fresh
                    // from the one that closed it.
                    current = new List<Fighter> { inOrder[i] };
                }
            }

            if (current.Count >= 2) yield return Build(owner, template, current, alongY);
        }

        private static BombWall Build(long owner, int template, List<Fighter> bombs, bool alongY)
        {
            var cells = new List<int>();
            for (int i = 1; i < bombs.Count; i++)
            {
                cells.AddRange(Between(bombs[i - 1].CellId, bombs[i].CellId, alongY));
            }
            return new BombWall(owner, template, bombs.ToList(), cells);
        }

        /// <summary>The cells strictly between two aligned ones.</summary>
        private static IEnumerable<int> Between(int from, int to, bool alongY)
        {
            var (fx, fy) = MapGeometry.CellToPoint(from);
            var (tx, ty) = MapGeometry.CellToPoint(to);

            int steps = alongY ? ty - fy : tx - fx;
            int step = steps > 0 ? 1 : -1;

            for (int i = 1; i < System.Math.Abs(steps); i++)
            {
                int cell = alongY
                    ? MapGeometry.PointToCell(fx, fy + i * step)
                    : MapGeometry.PointToCell(fx + i * step, fy);
                if (cell >= 0) yield return cell;
            }
        }
    }
}

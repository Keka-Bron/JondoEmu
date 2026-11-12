using System;
using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.World.Maps
{
    /// <summary>
    /// Where the two teams of a fight are placed, drawn anew for every fight.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client's maps carry no placement cells: the server picks them, and the real game's change
    /// from one fight to the next on the same map. Ours did not: on most maps the same sixteen cells
    /// (286, 298... and 411, 424...) came out every time, on every map that had them all walkable,
    /// and on the others the first eight cells of each half of the map by number.
    /// </para>
    /// <para>
    /// Two groups of eight, each spread within a few cells of a centre drawn at random, the red one
    /// in the far third of the ground from the blue one; teammates not side by side while there is
    /// room, as the eight nearest cells made one tight blob of monsters. Both on the largest piece of ground that holds
    /// together -- a pocket cut off by obstacles would leave a team with nobody to reach -- and no
    /// red cell next to a blue one, so nobody starts the fight already in contact.
    /// </para>
    /// </remarks>
    public static class PlacementLayout
    {
        /// <summary>Places per team.</summary>
        public const int PerTeam = 8;

        /// <summary>How far from its centre a team's cells are drawn: spread over the area, not one
        /// tight blob of monsters standing shoulder to shoulder.</summary>
        private const int Reach = 6;

        /// <summary>
        /// The two teams' cells on <paramref name="walkable"/>, the centre of each first; null when
        /// the ground is too small or too cut up to hold two teams apart.
        /// </summary>
        public static (List<int> Blue, List<int> Red)? Draw(IReadOnlyCollection<int> walkable, Random dice)
        {
            var ground = LargestPiece(walkable);
            if (ground.Count < 2 * PerTeam + 4) return null;

            int blueCentre = ground[dice.Next(ground.Count)];
            var byDistance = ground.OrderByDescending(c => MapGeometry.Distance(blueCentre, c)).ToList();
            var far = byDistance.Take(Math.Max(1, byDistance.Count / 3)).ToList();
            int redCentre = far[dice.Next(far.Count)];

            var blue = Around(blueCentre, ground, _ => true, dice);
            var red = Around(redCentre, ground,
                             c => !blue.Contains(c) && blue.All(b => MapGeometry.Distance(b, c) > 1), dice);
            if (blue.Count < PerTeam || red.Count < PerTeam) return null;
            return (blue, red);
        }

        /// <summary>
        /// The centre, then seven more drawn within <see cref="Reach"/> of it, none next to a
        /// teammate while there is room; closer in, and side by side, only when the ground is short.
        /// </summary>
        private static List<int> Around(int centre, List<int> ground, Func<int, bool> allowed, Random dice)
        {
            var team = new List<int>();
            if (allowed(centre)) team.Add(centre);

            var candidates = ground.Where(c => c != centre && allowed(c))
                                   .OrderBy(_ => dice.Next())
                                   .ToList();
            var withinReach = candidates.Where(c => MapGeometry.Distance(centre, c) <= Reach).ToList();
            var byDistance = candidates.OrderBy(c => MapGeometry.Distance(centre, c)).ToList();

            // Apart within reach; then side by side within reach; then the nearest left.
            foreach (var (pool, apart) in new[] { (withinReach, true), (withinReach, false), (byDistance, false) })
            {
                foreach (int cell in pool)
                {
                    if (team.Count >= PerTeam) return team;
                    if (team.Contains(cell)) continue;
                    if (apart && team.Any(t => MapGeometry.Distance(t, cell) <= 1)) continue;
                    team.Add(cell);
                }
            }
            return team;
        }

        /// <summary>The largest set of these cells that reach each other one step at a time.</summary>
        private static List<int> LargestPiece(IReadOnlyCollection<int> walkable)
        {
            var left = new HashSet<int>(walkable);
            var best = new List<int>();
            while (left.Count > 0)
            {
                int start = left.First();
                left.Remove(start);
                var piece = new List<int> { start };
                for (int i = 0; i < piece.Count; i++)
                {
                    int at = piece[i];
                    foreach (int next in left.Where(c => MapGeometry.Distance(at, c) == 1).ToList())
                    {
                        left.Remove(next);
                        piece.Add(next);
                    }
                }
                if (piece.Count > best.Count) best = piece;
            }
            best.Sort();
            return best;
        }
    }
}

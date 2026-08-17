using System;
using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.World.Maps
{
    /// <summary>
    /// Geometry of the Dofus isometric grid, for COMBAT.
    ///
    /// A map is 560 cells numbered in "half rows" of 14. Every cell has exactly four real
    /// neighbors -- the ones sharing an edge -- and those are the only cells a single MP step
    /// can reach. Expressed as cell numbers:
    ///
    ///     even row   (cell/14 even) :  -15, -14, +13, +14
    ///     odd row                   :  -14, -13, +14, +15
    ///
    /// The +1 and +28 offsets are NOT one step: they are two. +1 is (+14) followed by (-13),
    /// and +28 is (+14) twice. They used to be treated as direct neighbors -- eight neighbors
    /// instead of four -- and that produced three bugs at once: monsters walked twice as many
    /// cells as their MP allowed, they moved diagonally, and spell range came up short. In the
    /// session that was analyzed, the piou attacked from a real distance of 10 with a range-6
    /// spell, because the eight-neighbor metric scored that distance as 6.
    ///
    /// Converted to (x, y) coordinates, the four neighbors are (x±1, y) and (x, y±1), so combat
    /// distance is simply |dx| + |dy|. That is the very metric Dofus uses for spell range.
    ///
    /// Note: roleplay does allow all eight directions. This class is used by combat only.
    /// </summary>
    public static class MapGeometry
    {
        public const int MapWidth = 14;
        public const int MapHeight = 40;
        public const int MaxCells = 560;

        private static readonly int[] PointX = new int[MaxCells];
        private static readonly int[] PointY = new int[MaxCells];
        private static readonly Dictionary<(int X, int Y), int> CellByPoint = new Dictionary<(int, int), int>(MaxCells);
        private static readonly int[][] Neighbors = new int[MaxCells][];

        static MapGeometry()
        {
            for (int cell = 0; cell < MaxCells; cell++)
            {
                int row = cell / MapWidth;
                int col = cell % MapWidth;
                int x = col + (row + 1) / 2;
                int y = col - row / 2;
                PointX[cell] = x;
                PointY[cell] = y;
                CellByPoint[(x, y)] = cell;
            }

            for (int cell = 0; cell < MaxCells; cell++)
            {
                int x = PointX[cell], y = PointY[cell];
                var list = new List<int>(4);
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    if (CellByPoint.TryGetValue((x + dx, y + dy), out int n)) list.Add(n);
                }
                Neighbors[cell] = list.ToArray();
            }

            RunSelfTest();
        }

        public static bool IsValid(int cell) => cell >= 0 && cell < MaxCells;

        public static (int X, int Y) CellToPoint(int cell)
            => IsValid(cell) ? (PointX[cell], PointY[cell]) : (int.MinValue, int.MinValue);

        public static int PointToCell(int x, int y)
            => CellByPoint.TryGetValue((x, y), out int c) ? c : -1;

        /// <summary>The four cells one step (1 MP) away. Never diagonal.</summary>
        public static IEnumerable<int> GetNeighbors(int cell)
            => IsValid(cell) ? Neighbors[cell] : Array.Empty<int>();

        /// <summary>
        /// Combat distance: the minimum number of steps, ignoring obstacles. This is what the
        /// game uses for spell range.
        /// </summary>
        public static int Distance(int cellA, int cellB)
        {
            if (!IsValid(cellA) || !IsValid(cellB)) return 999;
            return Math.Abs(PointX[cellA] - PointX[cellB]) + Math.Abs(PointY[cellA] - PointY[cellB]);
        }

        /// <summary>
        /// Is there line of sight between two cells? Walks the segment joining their centers and
        /// fails if it crosses any cell flagged as opaque. The endpoints do not count: caster and
        /// target never block themselves.
        ///
        /// <paramref name="blockers"/> comes from map_fight_cells.json, from the `los` field of
        /// the client map data. When there is no data for that map, sight is assumed clear, which
        /// is preferable to blocking legitimate casts.
        /// </summary>
        public static bool HasLineOfSight(int fromCell, int toCell, HashSet<int> blockers)
        {
            if (blockers == null || blockers.Count == 0) return true;
            if (!IsValid(fromCell) || !IsValid(toCell) || fromCell == toCell) return true;

            int x0 = PointX[fromCell], y0 = PointY[fromCell];
            int x1 = PointX[toCell], y1 = PointY[toCell];

            int dx = x1 - x0, dy = y1 - y0;
            int steps = Math.Abs(dx) + Math.Abs(dy);
            if (steps <= 1) return true;

            // The segment is walked in `steps` sections and at every intermediate point we look
            // at which cells touch it. When the point lands exactly on an edge or a corner there
            // is more than one candidate, and it is enough for ONE of them to be clear: that way
            // a row of obstacles blocks sight, but a lone obstacle does not close the gap beside
            // it.
            for (int i = 1; i < steps; i++)
            {
                double px = x0 + (double)dx * i / steps;
                double py = y0 + (double)dy * i / steps;

                int fx = (int)Math.Floor(px), cx = (int)Math.Ceiling(px);
                int fy = (int)Math.Floor(py), cy = (int)Math.Ceiling(py);

                bool anyOpen = false;
                bool anyCell = false;
                foreach (int gx in (fx == cx) ? new[] { fx } : new[] { fx, cx })
                {
                    foreach (int gy in (fy == cy) ? new[] { fy } : new[] { fy, cy })
                    {
                        int cell = PointToCell(gx, gy);
                        if (cell < 0 || cell == fromCell || cell == toCell) continue;
                        anyCell = true;
                        if (!blockers.Contains(cell)) anyOpen = true;
                    }
                }

                if (anyCell && !anyOpen) return false;
            }

            return true;
        }

        /// <summary>
        /// Cells travelled through by a pushed target (or a pulled one, with a negative distance).
        /// The displacement follows the straight line from caster to target and stops at the first
        /// obstacle. Returns the path starting at the target's current cell; if it cannot move,
        /// that cell is the only entry.
        /// </summary>
        public static List<int> ComputePush(int casterCell, int targetCell, int distance,
                                            HashSet<int> walkable, HashSet<int> occupied)
        {
            var path = new List<int> { targetCell };
            if (distance == 0 || !IsValid(casterCell) || !IsValid(targetCell) || casterCell == targetCell)
                return path;

            int dx = PointX[targetCell] - PointX[casterCell];
            int dy = PointY[targetCell] - PointY[casterCell];

            // A single direction out of the four: the axis along which the two are furthest apart.
            int stepX = 0, stepY = 0;
            if (Math.Abs(dx) >= Math.Abs(dy)) stepX = Math.Sign(dx);
            else stepY = Math.Sign(dy);
            if (distance < 0) { stepX = -stepX; stepY = -stepY; }

            int steps = Math.Abs(distance);
            int x = PointX[targetCell], y = PointY[targetCell];
            for (int i = 0; i < steps; i++)
            {
                x += stepX; y += stepY;
                int next = PointToCell(x, y);
                if (next < 0) break;
                if (walkable != null && !walkable.Contains(next)) break;
                if (occupied != null && occupied.Contains(next)) break;
                path.Add(next);
            }

            return path;
        }

        public static List<int> FindShortestPath(int startCell, int targetCell, HashSet<int> walkableCells = null, HashSet<int> occupiedCells = null)
        {
            if (startCell == targetCell) return new List<int> { startCell };
            if (!IsValid(startCell) || !IsValid(targetCell)) return new List<int>();

            var parentMap = new Dictionary<int, int>();
            var visited = new HashSet<int> { startCell };
            var queue = new Queue<int>();
            queue.Enqueue(startCell);

            bool found = false;

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                if (current == targetCell)
                {
                    found = true;
                    break;
                }

                foreach (int n in GetNeighbors(current))
                {
                    if (visited.Contains(n)) continue;
                    if (walkableCells != null && !walkableCells.Contains(n)) continue;
                    if (occupiedCells != null && occupiedCells.Contains(n) && n != targetCell) continue;

                    visited.Add(n);
                    parentMap[n] = current;
                    queue.Enqueue(n);
                }
            }

            if (!found) return new List<int>();

            var path = new List<int>();
            int curr = targetCell;
            while (curr != startCell)
            {
                path.Add(curr);
                curr = parentMap[curr];
            }
            path.Add(startCell);
            path.Reverse();

            return path;
        }

        /// <summary>
        /// Turns the list of vertices sent by the client (direction changes only) into the
        /// cell-by-cell path. Each leg is resolved through the real shortest path, so a vertex
        /// that is two steps away costs two MP and not one.
        /// </summary>
        public static List<int> ExpandPath(List<int> vertices, HashSet<int> walkableCells = null,
                                           HashSet<int> occupiedCells = null)
        {
            if (vertices == null || vertices.Count == 0) return new List<int>();
            if (vertices.Count == 1) return new List<int> { vertices[0] };

            var fullPath = new List<int> { vertices[0] };

            for (int i = 0; i < vertices.Count - 1; i++)
            {
                int fromCell = vertices[i];
                int toCell = vertices[i + 1];

                // Las casillas ocupadas también cortan: sin pasarlas, el camino atraviesa a los
                // demás combatientes y además sale más corto de lo que de verdad se anda.
                var stepPath = FindShortestPath(fromCell, toCell, walkableCells, occupiedCells);
                if (stepPath.Count > 1)
                {
                    fullPath.AddRange(stepPath.Skip(1));
                }
                else if (fromCell != toCell)
                {
                    // No walkable path: try again ignoring walkability so the fighter is not left
                    // stuck in place.
                    var raw = FindShortestPath(fromCell, toCell);
                    if (raw.Count > 1) fullPath.AddRange(raw.Skip(1));
                    else fullPath.Add(toCell);
                }
            }

            return fullPath;
        }

        private static void RunSelfTest()
        {
            // Real combat steps observed in the 2026-08-04 session (deltas +14 and +15).
            int[][] combatSteps =
            {
                new[] { 189, 204, 218, 233, 247, 261 },
                new[] { 274, 289 },
            };

            foreach (var path in combatSteps)
            {
                for (int i = 0; i < path.Length - 1; i++)
                {
                    int d = Distance(path[i], path[i + 1]);
                    if (d != 1)
                        throw new Exception($"[MapGeometry] Distance({path[i]}, {path[i + 1]}) = {d}, expected 1.");
                }
            }

            // Diagonals are worth two steps, not one.
            foreach (var (a, b) in new[] { (100, 101), (100, 99), (100, 128), (100, 72) })
            {
                if (Distance(a, b) != 2)
                    throw new Exception($"[MapGeometry] Distance({a}, {b}) = {Distance(a, b)}, expected 2 (diagonal).");
            }

            for (int cell = 0; cell < MaxCells; cell++)
            {
                if (Neighbors[cell].Length > 4)
                    throw new Exception($"[MapGeometry] cell {cell} has {Neighbors[cell].Length} neighbors; the maximum is 4.");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.World.Maps
{
    /// <summary>
    /// Geometría de la rejilla isométrica de Dofus para COMBATE.
    ///
    /// El mapa son 560 casillas numeradas por "media fila" de 14. Cada casilla tiene cuatro
    /// vecinas de verdad —las que comparten arista— y a esas es a las que se puede dar un paso
    /// de 1 PM. En números de casilla eso son:
    ///
    ///     fila par   (celda/14 par) :  -15, -14, +13, +14
    ///     fila impar                 :  -14, -13, +14, +15
    ///
    /// Los desplazamientos +1 y +28 NO son un paso: son dos. +1 equivale a (+14) seguido de
    /// (-13), y +28 a dos veces (+14). Antes se trataban como vecinas directas —ocho vecinas en
    /// vez de cuatro—, y de ahí salían tres fallos a la vez: los monstruos recorrían el doble de
    /// casillas de las que les permitían sus PM, se movían en diagonal, y el alcance de los
    /// hechizos se quedaba corto. En la partida analizada el pío atacó desde una distancia real
    /// de 10 con un hechizo de alcance 6, porque la métrica de ocho vecinas la contaba como 6.
    ///
    /// Convertidas a coordenadas (x, y), las cuatro vecinas son (x±1, y) y (x, y±1), así que la
    /// distancia de combate es simplemente |dx| + |dy|. Es la misma que usa Dofus para el alcance
    /// de los hechizos.
    ///
    /// Nota: en roleplay sí se permiten los ocho sentidos. Esta clase solo la usa el combate.
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

        /// <summary>Las cuatro casillas a un paso (1 PM). Nunca en diagonal.</summary>
        public static IEnumerable<int> GetNeighbors(int cell)
            => IsValid(cell) ? Neighbors[cell] : Array.Empty<int>();

        /// <summary>
        /// Distancia de combate: número mínimo de pasos sin contar obstáculos. Es la que usa el
        /// juego para el alcance de los hechizos.
        /// </summary>
        public static int Distance(int cellA, int cellB)
        {
            if (!IsValid(cellA) || !IsValid(cellB)) return 999;
            return Math.Abs(PointX[cellA] - PointX[cellB]) + Math.Abs(PointY[cellA] - PointY[cellB]);
        }

        /// <summary>
        /// ¿Hay línea de visión entre dos casillas? Recorre el segmento que une sus centros y
        /// falla si atraviesa alguna casilla marcada como opaca. Los extremos no cuentan: el
        /// lanzador y el objetivo nunca se tapan a sí mismos.
        ///
        /// <paramref name="blockers"/> sale de map_fight_cells.json, del campo `los` de los datos
        /// de mapa del cliente. Si no hay datos para ese mapa se da por buena la visión, que es
        /// preferible a bloquear disparos legítimos.
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

            // Se recorre el segmento en `steps` tramos y en cada punto intermedio se mira qué
            // casillas lo tocan. Cuando el punto cae justo en una arista o esquina hay más de una
            // candidata, y basta con que UNA deje pasar: así una fila de obstáculos corta la
            // visión, pero un obstáculo suelto no tapa el hueco que tiene al lado.
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
        /// Casillas por las que pasa un objetivo empujado (o atraído, con distancia negativa).
        /// El desplazamiento sigue la recta que va del lanzador al objetivo y se detiene en el
        /// primer obstáculo. Devuelve el recorrido empezando por la casilla actual del objetivo;
        /// si no se puede mover, devuelve solo esa.
        /// </summary>
        public static List<int> ComputePush(int casterCell, int targetCell, int distance,
                                            HashSet<int> walkable, HashSet<int> occupied)
        {
            var path = new List<int> { targetCell };
            if (distance == 0 || !IsValid(casterCell) || !IsValid(targetCell) || casterCell == targetCell)
                return path;

            int dx = PointX[targetCell] - PointX[casterCell];
            int dy = PointY[targetCell] - PointY[casterCell];

            // Un solo sentido de los cuatro: el del eje que más separa a los dos.
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
        /// Convierte la lista de vértices que manda el cliente (solo los cambios de dirección) en
        /// el camino casilla a casilla. Cada tramo se resuelve por el camino más corto real, así
        /// que un vértice que esté a dos pasos consume dos PM y no uno.
        /// </summary>
        public static List<int> ExpandPath(List<int> vertices, HashSet<int> walkableCells = null)
        {
            if (vertices == null || vertices.Count == 0) return new List<int>();
            if (vertices.Count == 1) return new List<int> { vertices[0] };

            var fullPath = new List<int> { vertices[0] };

            for (int i = 0; i < vertices.Count - 1; i++)
            {
                int fromCell = vertices[i];
                int toCell = vertices[i + 1];

                var stepPath = FindShortestPath(fromCell, toCell, walkableCells);
                if (stepPath.Count > 1)
                {
                    fullPath.AddRange(stepPath.Skip(1));
                }
                else if (fromCell != toCell)
                {
                    // Sin camino transitable: probamos otra vez ignorando la transitabilidad para
                    // no dejar al luchador clavado en el sitio.
                    var raw = FindShortestPath(fromCell, toCell);
                    if (raw.Count > 1) fullPath.AddRange(raw.Skip(1));
                    else fullPath.Add(toCell);
                }
            }

            return fullPath;
        }

        private static void RunSelfTest()
        {
            // Pasos reales de combate observados en la partida del 4-8-2026 (deltas +14 y +15).
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
                        throw new Exception($"[MapGeometry] Distance({path[i]}, {path[i + 1]}) = {d}, se esperaba 1.");
                }
            }

            // Las diagonales valen dos pasos, no uno.
            foreach (var (a, b) in new[] { (100, 101), (100, 99), (100, 128), (100, 72) })
            {
                if (Distance(a, b) != 2)
                    throw new Exception($"[MapGeometry] Distance({a}, {b}) = {Distance(a, b)}, se esperaba 2 (diagonal).");
            }

            for (int cell = 0; cell < MaxCells; cell++)
            {
                if (Neighbors[cell].Length > 4)
                    throw new Exception($"[MapGeometry] la casilla {cell} tiene {Neighbors[cell].Length} vecinas; el máximo es 4.");
            }
        }
    }
}

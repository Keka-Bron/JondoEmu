using System;
using System.Collections.Generic;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Buscar un mapa por sus coordenadas, que es lo que hace falta para teleportar a mano.
    ///
    /// Unas coordenadas NO identifican un mapa: en [-1,0] hay siete y en [0,0] hay tres mil
    /// trescientos. Lo que las comparte son las casas, los interiores, los mundos aparte —el Reino
    /// de Papel, Sueños Infinitos— y, sobre todo, las arenas de combate, que van todas apuntadas
    /// en el 0,0 y son ellas solas dos mil seiscientas sesenta y nueve.
    ///
    /// Se elige el de la subzona MÁS GRANDE, medida en casillas andables sumando todos sus mapas.
    /// Es lo que separa lo de fuera de lo de dentro sin listas escritas a mano: en [-1,0] gana el
    /// Pueblo de Amakna con 19.629 casillas repartidas en 158 mapas, por delante del Reino de Papel
    /// (8.345) y del Test Convencionado (3.767), y eso a pesar de que el mapa del Test tiene 275
    /// casillas él solo y el del pueblo 274. Mirando el mapa suelto habría ganado el de pruebas.
    ///
    /// Antes de eso se descartan las arenas por el mismo criterio que ya usa
    /// <see cref="MapManager.ResolveArenaMapId"/>: bandera 69262589. Son mapas de combate sin
    /// salida —no tienen bordes por los que andar a otro sitio— y teletransportarse a uno deja al
    /// personaje encerrado.
    /// </summary>
    public static class MapLookup
    {
        /// <summary>La bandera que llevan las arenas de combate, la misma que mira MapManager.</summary>
        private const long ArenaFlags = 69262589;

        /// <summary>El mapa elegido y por qué, para poder contarlo por el chat.</summary>
        public sealed class Match
        {
            public MapInfo Map { get; init; } = null!;

            /// <summary>Cuántos mapas había en esas coordenadas, arenas aparte.</summary>
            public int Candidates { get; init; }

            /// <summary>Casillas andables de toda la subzona del elegido.</summary>
            public int SubAreaCells { get; init; }
        }

        /// <summary>Casillas andables de cada subzona, sumando las de todos sus mapas.</summary>
        private static readonly Dictionary<int, int> _cellsBySubArea = new Dictionary<int, int>();
        private static int _countedMaps = -1;
        private static readonly object _lock = new object();

        /// <summary>Result of cycling to the next map sharing the current coordinates.</summary>
        public sealed class RelativeMatch
        {
            public MapInfo Map { get; init; } = null!;
            public int Candidates { get; init; }
            public int Position { get; init; }
            public bool Wrapped { get; init; }
        }

        /// <summary>
        /// Finds the next map at the same world coordinates, as Giny's <c>.relative</c> command
        /// does. Ordering by MapId makes the cycle reproducible despite dictionary/database order.
        /// Combat arenas are intentionally not discarded here: this is an administrator browsing
        /// command and Giny cycles through every map position, not only outdoor roleplay maps.
        /// </summary>
        public static RelativeMatch? NextRelative(long currentMapId)
        {
            if (!MapManager.Maps.TryGetValue(currentMapId, out var current)) return null;

            var maps = new List<MapInfo>();
            foreach (var map in MapManager.Maps.Values)
            {
                if (map.MapId > 0 && map.PosX == current.PosX && map.PosY == current.PosY)
                    maps.Add(map);
            }
            maps.Sort((a, b) => a.MapId.CompareTo(b.MapId));
            if (maps.Count <= 1) return null;

            int currentIndex = maps.FindIndex(m => m.MapId == currentMapId);
            if (currentIndex < 0) return null;
            int nextIndex = currentIndex + 1;
            bool wrapped = nextIndex >= maps.Count;
            if (wrapped) nextIndex = 0;

            return new RelativeMatch
            {
                Map = maps[nextIndex],
                Candidates = maps.Count,
                Position = nextIndex + 1,
                Wrapped = wrapped,
            };
        }

        /// <summary>
        /// El mapa que hay en unas coordenadas, o null si no hay ninguno que valga.
        /// </summary>
        public static Match? AtCoordinates(int x, int y)
        {
            var candidates = new List<MapInfo>();
            foreach (var map in MapManager.Maps.Values)
            {
                if (map.PosX != x || map.PosY != y) continue;

                // El mapa cero está en la tabla y no es un mapa: no tiene casillas ni bordes.
                if (map.MapId <= 0) continue;

                if (map.Flags == ArenaFlags) continue;
                candidates.Add(map);
            }

            if (candidates.Count == 0) return null;

            // Un mapa del que no sabemos las casillas andables es un mapa donde no sabemos dónde
            // dejar al personaje. Se descarta mientras quede otro; si no queda ninguno, se usa
            // igualmente y GetNearestWalkableCell devolverá la casilla pedida tal cual.
            var walkable = candidates.FindAll(m => Cells(m.MapId) > 0);
            var usable = walkable.Count > 0 ? walkable : candidates;

            usable.Sort((a, b) =>
            {
                int bySubArea = SubAreaCells(b.SubAreaId).CompareTo(SubAreaCells(a.SubAreaId));
                if (bySubArea != 0) return bySubArea;

                // A igualdad de subzona, el de fuera antes que el de dentro: en unas coordenadas
                // del mundo lo que se espera es la calle, no la casa que da a ella.
                int byOutdoor = b.Outdoor.CompareTo(a.Outdoor);
                if (byOutdoor != 0) return byOutdoor;

                int byCells = Cells(b.MapId).CompareTo(Cells(a.MapId));
                if (byCells != 0) return byCells;

                // Y un desempate estable, para que las mismas coordenadas lleven siempre al mismo
                // sitio entre arranques.
                return a.MapId.CompareTo(b.MapId);
            });

            var chosen = usable[0];
            return new Match
            {
                Map = chosen,
                Candidates = candidates.Count,
                SubAreaCells = SubAreaCells(chosen.SubAreaId)
            };
        }

        private static int Cells(long mapId)
            => MapManager.WalkableCells.TryGetValue(mapId, out var cells) ? cells.Count : 0;

        /// <summary>
        /// Casillas andables de una subzona entera. Se cuenta una vez y se guarda: son treinta y
        /// tantos mil mapas y esto se pregunta una vez por candidato en cada comparación.
        /// </summary>
        private static int SubAreaCells(int subAreaId)
        {
            lock (_lock)
            {
                if (_countedMaps != MapManager.Maps.Count)
                {
                    _cellsBySubArea.Clear();
                    foreach (var map in MapManager.Maps.Values)
                    {
                        _cellsBySubArea.TryGetValue(map.SubAreaId, out int sum);
                        _cellsBySubArea[map.SubAreaId] = sum + Cells(map.MapId);
                    }
                    _countedMaps = MapManager.Maps.Count;
                }

                return _cellsBySubArea.TryGetValue(subAreaId, out int total) ? total : 0;
            }
        }
    }
}

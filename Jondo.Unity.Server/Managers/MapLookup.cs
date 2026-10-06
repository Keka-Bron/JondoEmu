using System;
using System.Collections.Generic;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Finding a map by its coordinates, which is what is needed to teleport by hand.
    ///
    /// Coordinates do NOT identify a map: at [-1,0] there are seven and at [0,0] there are three thousand
    /// three hundred. What shares them are houses, interiors, the separate worlds —the Paper
    /// Kingdom, Infinite Dreams— and, above all, the combat arenas, which are all recorded
    /// at 0,0 and are two thousand six hundred and sixty-nine on their own.
    ///
    /// The one of the BIGGEST subzone is chosen, measured in walkable cells adding up all its maps.
    /// It is what separates the outside from the inside without hand-written lists: at [-1,0] the
    /// Amakna Village wins with 19,629 cells spread over 158 maps, ahead of the Paper Kingdom
    /// (8,345) and the Test Convencionado (3,767), and that even though the Test map has 275
    /// cells on its own and the village one 274. Looking at the single map the test one would have won.
    ///
    /// Before that the arenas are discarded by the same criterion
    /// <see cref="MapManager.ResolveArenaMapId"/> already uses: flag 69262589. They are combat maps with no
    /// way out —they have no edges to walk somewhere else through— and teleporting to one leaves the
    /// character locked in.
    /// </summary>
    public static class MapLookup
    {
        /// <summary>The flag the combat arenas carry, the same one MapManager looks at.</summary>
        private const long ArenaFlags = 69262589;

        /// <summary>The chosen map and why, to be able to tell it through the chat.</summary>
        public sealed class Match
        {
            public MapInfo Map { get; init; } = null!;

            /// <summary>Cuántos mapas había en esas coordenadas, arenas aparte.</summary>
            public int Candidates { get; init; }

            /// <summary>Walkable cells of the chosen one's whole subzone.</summary>
            public int SubAreaCells { get; init; }
        }

        /// <summary>Walkable cells of each subzone, adding up those of all its maps.</summary>
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
        /// The map at some coordinates, or null if there is none that will do.
        /// </summary>
        public static Match? AtCoordinates(int x, int y)
        {
            var candidates = new List<MapInfo>();
            foreach (var map in MapManager.Maps.Values)
            {
                if (map.PosX != x || map.PosY != y) continue;

                // Map zero is in the table and is not a map: it has no cells and no edges.
                if (map.MapId <= 0) continue;

                if (map.Flags == ArenaFlags) continue;
                candidates.Add(map);
            }

            if (candidates.Count == 0) return null;

            // A map whose walkable cells we do not know is a map where we do not know where to
            // leave the character. It is discarded while another remains; if none remains, it is used
            // anyway and GetNearestWalkableCell will return the requested cell as is.
            var walkable = candidates.FindAll(m => Cells(m.MapId) > 0);
            var usable = walkable.Count > 0 ? walkable : candidates;

            usable.Sort((a, b) =>
            {
                int bySubArea = SubAreaCells(b.SubAreaId).CompareTo(SubAreaCells(a.SubAreaId));
                if (bySubArea != 0) return bySubArea;

                // With equal subzone, the outside one before the inside one: at some world
                // coordinates what is expected is the street, not the house facing it.
                int byOutdoor = b.Outdoor.CompareTo(a.Outdoor);
                if (byOutdoor != 0) return byOutdoor;

                int byCells = Cells(b.MapId).CompareTo(Cells(a.MapId));
                if (byCells != 0) return byCells;

                // And a stable tie-break, so that the same coordinates always lead to the same
                // place between starts.
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
        /// Walkable cells of a whole subzone. It is counted once and kept: there are thirty-odd
        /// thousand maps and this is asked once per candidate in each comparison.
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

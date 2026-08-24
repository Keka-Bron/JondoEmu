using System;
using System.Collections.Generic;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Character-owned ordinary-zaap discovery. The static waypoint catalogue describes what
    /// exists; only validated arrival on one of those maps creates character progress.
    /// </summary>
    public static class ZaapDiscovery
    {
        /// <summary>
        /// A discoverable map must be an active official waypoint with a real, usable element in
        /// this pinned world snapshot. Haven-bag zaaps and anomaly vestiges are deliberately not
        /// waypoints and therefore never pass this check.
        /// </summary>
        internal static bool IsDiscoverableMap(long mapId)
        {
            var waypoint = Interactives.WaypointOf(mapId);
            return waypoint != null &&
                   waypoint.Activated &&
                   MapManager.GetMapInfo(mapId) != null &&
                   Interactives.CanLeaveFrom(mapId);
        }

        /// <summary>
        /// Records the first validated arrival on an ordinary zaap map. INSERT OR IGNORE makes
        /// reconnects and repeated visits idempotent. Returns true only for a new discovery.
        /// </summary>
        public static bool DiscoverOnArrival(long characterId, long mapId)
        {
            if (characterId <= 0 || !IsDiscoverableMap(mapId)) return false;
            if (!DatabaseManager.TryDiscoverZaap(characterId, mapId, out bool discovered) ||
                !discovered)
            {
                return false;
            }

            Console.WriteLine($"[Zaap] Character {characterId} discovered map {mapId} on arrival.");
            return true;
        }

        /// <summary>
        /// Returns only valid character-owned discoveries, in the stable official catalogue
        /// order used by destination lists. Invalid or stale database rows never reach the wire.
        /// </summary>
        public static List<long> KnownMaps(long characterId)
        {
            var result = new List<long>();
            var owned = new HashSet<long>(DatabaseManager.GetDiscoveredZaapMaps(characterId));
            foreach (var waypoint in Interactives.Waypoints)
            {
                if (owned.Contains(waypoint.MapId) && IsDiscoverableMap(waypoint.MapId))
                    result.Add(waypoint.MapId);
            }
            return result;
        }
    }
}

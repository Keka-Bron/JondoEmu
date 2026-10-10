using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The ways between a guild raid's floors: its lifts and the passages its enigmas open. No
    /// capture has a raid and the world's data has no route between those floors, so they are read
    /// off the client's own maps, where the elements stand.
    /// </summary>
    /// <remarks>
    /// The lifts are the elements of graphic 142037. They stand exactly where the guides put the
    /// Gigalodón's lifts -- floor -1 at [4,3], floor -2 at [2,7], the diving cage of floor -5 at
    /// [10,14] -- and one more on each floor below them, two maps away: that one is its arrival.
    /// So each lift goes to the nearest lift of the next floor up or down. The pair that reaches the
    /// last floor is the diving cage, whose type the client's data names apart.
    ///
    /// The passages are the elements of graphic 44035 two maps from one on the next floor: the
    /// Luminarium's, from floor -3 [4,12] to floor -4 [5,11] (cells 476 and 477, either side of the
    /// same edge), and the Exécrabe's, from floor -4 [9,12] to floor -5 [10,13].
    ///
    /// What opens each floor is its goal (GuildRaidManager.KeepsOutAsync, on every teleport).
    ///
    /// The Gigalodón's way in is its outpost, the floor -1 map where the hatch down from the
    /// surface platform comes out (graphic 121002, at [3,1]): the room with the chest, a machine
    /// and the window on the abyss. It stands on legs over [3,2] and [2,2], and has no way out by
    /// its edges: a ladder inside it (graphic 42043, the client's type 455 "Escalera") goes down
    /// under the building, and the ladder there -- one element, seen from both maps -- comes back
    /// up. The floor's maps without a position ([0,0]) are not the world's: they are its fight
    /// arenas (MapManager.ResolveArenaMapId).
    ///
    /// The Santuario has no floors but zones, and its zones are joined through a hub: the castle's
    /// map [15,17], with a round portal to each zone (graphics 140017, 140018, 140019, 140026), and
    /// in each zone a portal back (graphic 140021). Which portal leads where is drawn on the portal
    /// itself -- the battleship's board, the gardens' pedestals, the black-and-white Obra, the
    /// chessboard -- and the way back shows the hub's statue. Each portal leads to its zone's way
    /// back, on the zone's most central map; each way back, to the hub beside its zone's portal.
    /// They are the client's type 232 "Portal" with skill 114, as the world's portals are. The hub
    /// is the raid's entrance (GuildRaidManager.EntryMapOf).
    /// </remarks>
    public static class GuildRaidPassages
    {
        /// <summary>The lifts' graphic, in the client's maps.</summary>
        public const int LiftGfx = 142037;

        /// <summary>The floor passages' graphic, in the client's maps.</summary>
        public const int PassageGfx = 44035;

        /// <summary>"Ascensor" and "Jaula de buceo" in the client's interactive types.</summary>
        public const int LiftType = 226;
        public const int CageType = 462;

        /// <summary>The hatch's lower end, inside the Gigalodón's outpost: the raid's way in.</summary>
        public const int EntryGfx = 121002;

        /// <summary>Its upper end, on the surface platform above the outpost.</summary>
        public const int HatchTopGfx = 121001;

        /// <summary>The outpost's ladders, and the client's type 455 "Escalera".</summary>
        public const int LadderGfx = 42043;
        public const int LadderType = 455;

        /// <summary>"Bajar" and "Subir" in the client's skills.</summary>
        public const int DownSkill = 447;
        public const int UpSkill = 448;

        /// <summary>The Santuario's portals to its zones, by the zone they lead to (its place in the raid's zones).</summary>
        public static readonly IReadOnlyDictionary<int, int> ZonePortalGfx = new Dictionary<int, int>
        {
            [140019] = 1,      // Obra monocromática, black and white
            [140018] = 2,      // Enclave de los protectores, the pedestals' gardens
            [140017] = 3,      // Reserva de Belladona, the battleship's board
            [140026] = 4,      // Patio de Efedra, the chessboard
        };

        /// <summary>A Santuario zone's way back to the hub, showing the hub's statue.</summary>
        public const int ReturnPortalGfx = 140021;

        /// <summary>The client's interactive type 232, "Portal".</summary>
        public const int PortalType = 232;

        /// <summary>A passage's type: none, as the captures send graphic 44035 elsewhere.</summary>
        public const int PassageType = -1;

        /// <summary>How far, in maps, a lift and its arrival stand; and a passage and its other side.</summary>
        private const int LiftReach = 3;
        private const int PassageReach = 2;
        private const int LadderReach = 2;

        private sealed record Spot(int Floor, long MapId, int X, int Y, Interactives.Element Element);

        private static int Distance(Spot a, Spot b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

        /// <summary>The elements of a graphic on a raid's floors, with their floor and map coordinates.</summary>
        private static List<Spot> SpotsOf(RaidKind kind, int gfx)
        {
            var spots = new List<Spot>();
            for (int floor = 1; floor <= kind.Floors.Count; floor++)
            {
                foreach (long map in DatabaseManager.MapsOfSubArea(kind.Floors[floor - 1]))
                {
                    if (PositionOf(map) is not (int x, int y)) continue;
                    foreach (var element in Interactives.ElementsOf(map).Where(e => e.Gfx == gfx))
                        spots.Add(new Spot(floor, map, x, y, element));
                }
            }
            return spots;
        }

        /// <summary>A map's world coordinates, from the world's data.</summary>
        private static (int X, int Y)? PositionOf(long mapId)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            var query = connection.CreateCommand();
            query.CommandText = "SELECT PosX, PosY FROM MapPositions WHERE MapId = $m;";
            query.Parameters.AddWithValue("$m", mapId);
            using var reader = query.ExecuteReader();
            return reader.Read() ? (reader.GetInt32(0), reader.GetInt32(1)) : null;
        }

        private static InteractiveTeleport Route(Spot from, Spot to, int type, int skill) => new InteractiveTeleport
        {
            SourceMapId = from.MapId,
            ElementId = from.Element.Id,
            SourceCellId = from.Element.Cell,
            GfxId = from.Element.Gfx,
            InteractiveType = type,
            SkillId = skill,
            DestinationMapId = to.MapId,
            DestinationCellId = to.Element.Cell,
            SourceVersion = "raid",
            Confidence = "derived",
        };

        /// <summary>Whether an element stands on a map's edge cell, where the client repeats a neighbour's.</summary>
        private static bool OnEdge(Interactives.Element element) => element.Cell is 0 or 13 or 559;

        /// <summary>
        /// A raid's hub: the map holding a portal to each of its zones, and those portals by the zone
        /// they lead to. Null for a raid without one.
        /// </summary>
        public static (long MapId, Dictionary<int, Interactives.Element> Portals)? HubOf(RaidKind kind)
        {
            if (kind == null) return null;
            foreach (int subArea in kind.Floors)
            foreach (long map in DatabaseManager.MapsOfSubArea(subArea))
            {
                var portals = Interactives.ElementsOf(map)
                                          .Where(e => ZonePortalGfx.ContainsKey(e.Gfx) && !OnEdge(e))
                                          .GroupBy(e => ZonePortalGfx[e.Gfx])
                                          .ToDictionary(g => g.Key, g => g.First());
                if (portals.Count == ZonePortalGfx.Count) return (map, portals);
            }
            return null;
        }

        /// <summary>The hub's portals and each zone's ways back.</summary>
        private static IEnumerable<InteractiveTeleport> PortalsOf(RaidKind kind)
        {
            if (HubOf(kind) is not { } hub || PositionOf(hub.MapId) is not (int hx, int hy)) yield break;
            var hubSpots = hub.Portals.ToDictionary(p => p.Key, p => new Spot(0, hub.MapId, hx, hy, p.Value));

            foreach (var (zone, portal) in hubSpots)
            {
                var backs = WaysBack(kind, zone);
                if (backs.Count == 0) continue;

                // The way in: to the way back on the zone's most central map.
                yield return Route(portal, Arrival(kind, zone, backs), PortalType, TeleportManager.UseSkill);

                // And every way back to the hub, beside this zone's portal.
                foreach (var back in backs) yield return Route(back, portal, PortalType, TeleportManager.UseSkill);
            }
        }

        private static List<Spot> WaysBack(RaidKind kind, int zone)
            => zone < 1 || zone > kind.Floors.Count
                ? new List<Spot>()
                : SpotsOf(kind, ReturnPortalGfx).Where(s => s.Floor == zone && !OnEdge(s.Element)).ToList();

        private static Spot Arrival(RaidKind kind, int zone, List<Spot> backs)
        {
            var maps = DatabaseManager.MapsOfSubArea(kind.Floors[zone - 1])
                                      .Select(PositionOf).Where(p => p != null).Select(p => p.Value).ToList();
            double cx = maps.Average(p => p.X), cy = maps.Average(p => p.Y);
            return backs.OrderBy(b => Math.Abs(b.X - cx) + Math.Abs(b.Y - cy)).ThenBy(b => b.MapId).First();
        }

        /// <summary>Where the hub's portal to a zone arrives: the zone's central map and its way back's cell.</summary>
        public static (long MapId, int Cell)? ArrivalOf(RaidKind kind, int zone)
        {
            if (HubOf(kind) == null) return null;
            var backs = WaysBack(kind, zone);
            if (backs.Count == 0) return null;
            var arrival = Arrival(kind, zone, backs);
            return (arrival.MapId, arrival.Element.Cell);
        }

        /// <summary>The first floor's map holding the way in (<see cref="EntryGfx"/>), or 0 when none does.</summary>
        public static long EntryOf(RaidKind kind)
            => kind == null || kind.Floors.Count == 0
                ? 0
                : SpotsOf(kind, EntryGfx).Where(s => s.Floor == 1 && !OnEdge(s.Element))
                                         .Select(s => s.MapId).OrderBy(m => m).FirstOrDefault();

        /// <summary>
        /// The map each floor is reached by: the first floor's way in, and for the rest where the way
        /// down from the floor above arrives -- a lift, a passage. 0 for a floor nothing leads to.
        /// </summary>
        public static long ArrivalMapOf(RaidKind kind, int floor, IReadOnlyList<InteractiveTeleport> routes)
        {
            if (kind == null || floor < 1 || floor > kind.Floors.Count) return 0;
            if (floor == 1) return EntryOf(kind);
            var here = DatabaseManager.MapsOfSubArea(kind.Floors[floor - 1]).ToHashSet();
            var above = DatabaseManager.MapsOfSubArea(kind.Floors[floor - 2]).ToHashSet();
            return routes.Where(r => above.Contains(r.SourceMapId) && here.Contains(r.DestinationMapId))
                         .Select(r => r.DestinationMapId).OrderBy(m => m).FirstOrDefault();
        }

        /// <summary>
        /// The hatch between the outpost and the surface platform above it ([3,0]), both ways: the
        /// platform is floor -1's too, with two groups of its own, and could not be reached.
        /// </summary>
        private static IEnumerable<InteractiveTeleport> HatchOf(RaidKind kind)
        {
            var bottoms = SpotsOf(kind, EntryGfx).Where(s => s.Floor == 1 && !OnEdge(s.Element)).ToList();
            var tops = SpotsOf(kind, HatchTopGfx).Where(s => s.Floor == 1 && !OnEdge(s.Element)).ToList();
            foreach (var bottom in bottoms)
            {
                var top = tops.Where(t => Distance(bottom, t) <= LadderReach).OrderBy(t => Distance(bottom, t)).FirstOrDefault();
                if (top == null) continue;
                yield return Route(bottom, top, LadderType, UpSkill);
                yield return Route(top, bottom, LadderType, DownSkill);
            }
        }

        /// <summary>The outpost's ladders: the one inside goes down, the one under the building comes up.</summary>
        private static IEnumerable<InteractiveTeleport> LaddersOf(RaidKind kind)
        {
            long entry = EntryOf(kind);
            if (entry == 0) yield break;
            var ladders = SpotsOf(kind, LadderGfx).Where(s => s.Floor == 1 && !OnEdge(s.Element)).ToList();
            foreach (var inside in ladders.Where(s => s.MapId == entry))
            {
                var below = ladders.Where(s => s.MapId != entry && Distance(inside, s) <= LadderReach).ToList();
                var down = below.OrderBy(s => Distance(inside, s)).ThenBy(s => s.MapId).FirstOrDefault();
                if (down != null) yield return Route(inside, down, LadderType, DownSkill);
                foreach (var under in below) yield return Route(under, inside, LadderType, UpSkill);
            }
        }

        /// <summary>Every lift, passage and ladder of the raids' floors, both ways, and the Santuario's portals.</summary>
        public static List<InteractiveTeleport> Derive()
        {
            var routes = new List<InteractiveTeleport>();
            foreach (var kind in Raids.All)
            {
                routes.AddRange(PortalsOf(kind));
                routes.AddRange(LaddersOf(kind));
                routes.AddRange(HatchOf(kind));

                var lifts = SpotsOf(kind, LiftGfx);
                foreach (var lift in lifts)
                {
                    var to = lifts.Where(o => Math.Abs(o.Floor - lift.Floor) == 1)
                                  .OrderBy(o => Distance(lift, o)).FirstOrDefault();
                    if (to == null || Distance(lift, to) > LiftReach) continue;
                    bool cage = Math.Max(lift.Floor, to.Floor) == kind.Floors.Count;
                    routes.Add(Route(lift, to, cage ? CageType : LiftType, to.Floor > lift.Floor ? DownSkill : UpSkill));
                }

                var passages = SpotsOf(kind, PassageGfx);
                foreach (var passage in passages)
                {
                    var to = passages.Where(o => Math.Abs(o.Floor - passage.Floor) == 1 && Distance(passage, o) <= PassageReach)
                                     .OrderBy(o => Distance(passage, o))
                                     .ThenBy(o => Math.Abs(o.Element.Cell - passage.Element.Cell))
                                     .FirstOrDefault();
                    if (to == null) continue;

                    // One passage per pair of maps: the element of this map nearest the other side's.
                    var nearest = passages.Where(p => p.MapId == passage.MapId)
                                          .OrderBy(p => Math.Abs(p.Element.Cell - to.Element.Cell)).First();
                    if (nearest.Element.Id != passage.Element.Id) continue;
                    routes.Add(Route(passage, to, PassageType, TeleportManager.UseSkill));
                }
            }
            return routes;
        }
    }
}

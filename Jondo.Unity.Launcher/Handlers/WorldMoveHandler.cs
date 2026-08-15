using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Network;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>
    /// Walking, and walking off the edge of a map, in the 3.6.10.10 protocol.
    ///
    /// The four Movimiento captures spell the whole exchange out. Nothing here is guessed:
    ///
    ///   C  jrw { f1: map, f2: the path }      the client asks to walk
    ///   S  jsj { f1: cells, f2: facing, f5: who }
    ///   C  jqi {}                             it reached the edge and wants out
    ///   S  jsq {}                             go ahead      <- on root field 3, not 1
    ///   C  jqk { f2: the map it wants }
    ///   S  jsd { f2: who }                    take it off the old map
    ///   S  jru { f2: the map }                load this one
    ///   S  lqu, lqn, hjk
    ///   C  kmv, jrh                           the map is loaded, who is on it
    ///   S  jss, lva
    ///
    /// The old handlers spoke joi/jos/jpp, which are the names of an earlier version and never
    /// matched anything the 3.6.10.10 client says. That is why the client walked around a map
    /// happily and then stopped dead at the edge.
    /// </summary>
    public static class WorldMoveHandler
    {
        /// <summary>
        /// Where a character lands on the next map, and which way it ends up facing.
        ///
        /// A map is fourteen cells across, laid out as a diamond, so leaving through the side
        /// moves the cell by thirteen and through the top or the bottom by five hundred and
        /// thirty-two. All four are read straight off the captures:
        ///
        ///   right   405 -> 392    -13,  facing 0
        ///   left    322 -> 335    +13,  facing 4
        ///   up       23 -> 555   +532,  facing 6
        ///   down    542 ->  10   -532,  facing 2
        /// </summary>
        private const int SideStep = 13;
        private const int VerticalStep = 532;

        private enum Way { None, Right, Left, Up, Down }

        // ─── jrw: walking inside a map ──────────────────────────────────────────

        /// <summary>
        /// The client says where it is walking to, and gets its movement back.
        ///
        ///   jrw { f1: map id, f2: the path, packed, each entry facing &lt;&lt; 12 | cell }
        ///   jsj { f1: the cells, packed, f2: how it ends up facing, f5: whose it is }
        ///
        /// This went unanswered for a while, on the grounds that the jsj is how every OTHER client
        /// learns about the movement and there is nobody else here. That was wrong in a way that
        /// showed: walking off the left edge of a map, the character turned to face RIGHT in the
        /// instant before the screen faded. Facing right is orientation zero, which is what an
        /// actor falls back to when nothing has told the client otherwise — and nothing had,
        /// because we never confirmed the walk.
        ///
        /// The path travels back as the client sent it, cell by cell, which is not what the real
        /// server does: it expands the straight runs into every cell walked. Ours is the client's
        /// own keyframes, and since the client has already walked them it has nothing left to
        /// interpolate.
        /// </summary>
        public static async Task ConfirmMovementAsync(NetworkStream stream, byte[] payload)
        {
            var (cells, facing) = Remember(payload);
            if (cells.Count == 0) return;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildActorMoved(GameState.CharacterId, cells, facing));
        }

        /// <summary>
        /// Writes down where the walk ends. The map change works the way out from that cell, and
        /// until this was recorded the character was still officially standing wherever it logged
        /// in.
        /// </summary>
        private static (List<long> Cells, int Facing) Remember(byte[] payload)
        {
            var nothing = (new List<long>(), GameState.Orientation);

            byte[]? jrw = ConnectionProtocol.ReadPayload(payload, "jrw");
            if (jrw == null || jrw.Length == 0) return nothing;

            long mapId = 0;
            var steps = new List<long>();

            foreach (var f in ProtoMessage.Parse(jrw).Fields)
            {
                if (f.FieldNumber == 1 && f.WireType == 0) mapId = f.VarIntValue;
                else if (f.FieldNumber == 2 && f.WireType == 2) steps = Unpack(f.BytesValue);
            }

            if (steps.Count == 0) return nothing;

            if (mapId > 0 && mapId != GameState.MapId)
            {
                // The client believes it is on another map. Trusting it here would let a stray
                // message move the character anywhere, so it is only logged.
                Console.WriteLine($"[Move] jrw says map {mapId} and the session says {GameState.MapId}. Ignored.");
                return nothing;
            }

            // Each step carries its cell in the low twelve bits and the way the character is
            // facing on that step above them.
            var cells = new List<long>();
            foreach (long step in steps) cells.Add(step & 0xFFF);

            long last = steps[steps.Count - 1];
            int cell = (int)(last & 0xFFF);
            int facing = (int)(last >> 12);

            GameState.CellId = cell;
            if (facing >= 0 && facing <= 7) GameState.Orientation = facing;
            DatabaseManager.SaveCurrentCharacter();

            return (cells, GameState.Orientation);
        }

        private static List<long> Unpack(byte[] packed)
        {
            var values = new List<long>();
            int i = 0;
            while (i < packed.Length)
            {
                long value = 0;
                int shift = 0;
                while (i < packed.Length)
                {
                    byte b = packed[i++];
                    value |= (long)(b & 0x7F) << shift;
                    if ((b & 0x80) == 0) break;
                    shift += 7;
                }
                values.Add(value);
            }
            return values;
        }

        // ─── jqi: may I leave? ──────────────────────────────────────────────────

        /// <summary>
        /// The client reached the edge and asks to leave. The answer carries nothing but the id of
        /// the request, and it travels on root field 3, which is the one used for answers.
        ///
        /// Without it the client never sends the jqk that names the map it wants, and the
        /// character stands on the border for good.
        /// </summary>
        public static async Task AllowMapExitAsync(NetworkStream stream, byte[] payload)
        {
            long request = ConnectionProtocol.RequestId(payload);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer("jsq", null, request));
        }

        // ─── jqk: take me to this map ───────────────────────────────────────────

        public static async Task ChangeMapAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? jqk = ConnectionProtocol.ReadPayload(payload, "jqk");
            if (jqk == null || jqk.Length == 0) return;

            long asked = 0;
            foreach (var f in ProtoMessage.Parse(jqk).Fields)
            {
                if (f.FieldNumber == 2 && f.WireType == 0) asked = f.VarIntValue;
            }
            if (asked <= 0) return;

            Way way = WayOut(GameState.MapId, GameState.CellId, asked);
            long target = Neighbour(GameState.MapId, way, asked);

            if (target <= 0 || target == GameState.MapId)
            {
                Console.WriteLine($"[Move] There is no map {way} of {GameState.MapId}. " +
                                  $"The client asked for {asked} and stays where it is.");
                return;
            }

            // And never move onto a map the world data does not describe. The client cannot load
            // one either, so it would sit on the border while the database says it is somewhere
            // that does not exist — which is exactly what happened with 191105029.
            if (MapManager.GetMapInfo(target) == null)
            {
                Console.WriteLine($"[Move] Map {target} is not in the world data. Not moving.");
                return;
            }

            int arrival = Landing(target, GameState.CellId, way);

            GameState.MapId = target;
            GameState.CellId = arrival;
            GameState.Orientation = FacingFor(way, GameState.Orientation);
            DatabaseManager.SaveCurrentCharacter();

            // Exactly the five the capture sends, in the same order. jsd first: the character is
            // leaving the map it was on, and the client has to be told before it is told to load
            // another one.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildActorLeft(GameState.CharacterId));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildLoadMap(target));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildMapClock());
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildMapDiscovered(target));

            Console.WriteLine($"[Move] Map change {way}: {target}, arriving on cell {arrival} " +
                              $"facing {GameState.Orientation}. Waiting for jrh.");
        }

        /// <summary>
        /// Which way the character is leaving.
        ///
        /// The map id in jqk is a GUESS, not an instruction. The client works it out by arithmetic
        /// on the id it is standing on, and that only holds where the map next door happens to be
        /// the next id along. Two of the captures show the real server loading a different map
        /// from the one it was asked for, and the session of 14/08 shows the same thing from the
        /// other side: standing on 191105028 at [5,-17] and walking off the bottom, the client
        /// asked for 191105029, which does not exist. The map below is 188745734. We echoed the
        /// guess back in jru, the client had nothing to load, and the character stayed pinned to
        /// the border on cell 556 — every jrw after that starts from there.
        ///
        /// So the guess is only used when it happens to name one of the four neighbours, which is
        /// what makes it useful: it settles the corners, where the cell alone cannot say whether
        /// the character is leaving sideways or downwards. Otherwise the cell decides.
        /// </summary>
        private static Way WayOut(long from, int cell, long asked)
        {
            var scroll = MapManager.GetScrollAction(from);
            if (scroll != null)
            {
                if (asked != 0 && scroll.RightMapId == asked) return Way.Right;
                if (asked != 0 && scroll.LeftMapId == asked) return Way.Left;
                if (asked != 0 && scroll.TopMapId == asked) return Way.Up;
                if (asked != 0 && scroll.BottomMapId == asked) return Way.Down;
            }

            return FromEdge(cell);
        }

        /// <summary>
        /// Which border the character is standing on. A map is fourteen cells across and forty
        /// rows down; the sides are the first and last column and the top and bottom are the first
        /// and last two rows, which is what the captured departures say: 405 and 322 leave
        /// sideways from columns 13 and 0, and 23 and 542 leave up and down from rows 1 and 38.
        ///
        /// A corner belongs to two of them at once. Sideways wins there, because the cell of
        /// arrival for a side is thirteen away and the one for a top or bottom is five hundred
        /// and thirty-two: getting a side wrong lands the character one row off, getting a
        /// vertical wrong lands it on the far side of the map.
        /// </summary>
        private static Way FromEdge(int cell)
        {
            int row = cell / 14;
            int column = cell % 14;

            if (column == 13) return Way.Right;
            if (column == 0) return Way.Left;
            if (row <= 1) return Way.Up;
            if (row >= 38) return Way.Down;

            Console.WriteLine($"[Move] Cell {cell} is not on any border, so there is no telling " +
                              "which way out was meant.");
            return Way.None;
        }

        /// <summary>
        /// The map that way, in three goes.
        ///
        /// The guess first, because most of the time it is right and it is the only one of the
        /// three that knows which of several maps sharing a square the player means. It is only
        /// taken when it names a map that exists AND sits on the square next door in the direction
        /// being walked, which is a real check: 191105029 passed neither.
        ///
        /// Then MapScrolls, which is the game's own list of neighbours. It is right where it is
        /// filled in, and it is filled in for 2.223 maps out of 15.360 — the map at [5,-16], where
        /// this went wrong twice, only has its top neighbour written down.
        ///
        /// And failing both, the coordinates, which every one of the 15.360 maps has. Where more
        /// than one map sits on a square the outdoor one wins, and within that the one in the same
        /// subarea: at [5,-17] there are four, one out in the open and three interiors.
        /// </summary>
        private static long Neighbour(long from, Way way, long asked)
        {
            if (way == Way.None) return 0;

            var here = MapManager.GetMapInfo(from);
            var guess = asked == 0 ? null : MapManager.GetMapInfo(asked);
            if (here != null && guess != null && IsNextDoor(here, guess, way)) return asked;

            var scroll = MapManager.GetScrollAction(from);
            long written = scroll == null ? 0 : way switch
            {
                Way.Right => scroll.RightMapId,
                Way.Left => scroll.LeftMapId,
                Way.Up => scroll.TopMapId,
                Way.Down => scroll.BottomMapId,
                _ => 0,
            };
            if (written != 0)
            {
                Console.WriteLine($"[Move] The client asked for {asked}; the world data says " +
                                  $"{way} of {from} is {written}.");
                return written;
            }

            if (here == null) return 0;
            long found = ByCoordinates(here, way);
            if (found != 0)
            {
                Console.WriteLine($"[Move] The client asked for {asked}; the map {way} of {from} " +
                                  $"is {found} by its coordinates.");
            }
            return found;
        }

        private static bool IsNextDoor(MapInfo here, MapInfo there, Way way) => way switch
        {
            Way.Right => there.PosX == here.PosX + 1 && there.PosY == here.PosY,
            Way.Left => there.PosX == here.PosX - 1 && there.PosY == here.PosY,
            Way.Up => there.PosY == here.PosY - 1 && there.PosX == here.PosX,
            Way.Down => there.PosY == here.PosY + 1 && there.PosX == here.PosX,
            _ => false,
        };

        private static long ByCoordinates(MapInfo here, Way way)
        {
            int x = here.PosX + (way == Way.Right ? 1 : way == Way.Left ? -1 : 0);
            int y = here.PosY + (way == Way.Down ? 1 : way == Way.Up ? -1 : 0);

            MapInfo? best = null;
            foreach (var candidate in MapManager.Maps.Values)
            {
                if (candidate.PosX != x || candidate.PosY != y) continue;
                if (best == null || Better(candidate, best, here)) best = candidate;
            }
            return best?.MapId ?? 0;
        }

        /// <summary>
        /// Out in the open beats an interior, the subarea we are already in beats another one,
        /// and after that the id nearest the one we are leaving — map ids are handed out in
        /// blocks, so a neighbour is usually numerically close.
        ///
        /// Measured against the 3.463 neighbours the world data does have written down: nearest
        /// id gets 71,0% of them right and lowest id 69,8%. Neither can do much better, because
        /// 27,2% of the real neighbours are not on the square next door at all — a good quarter of
        /// the borders in this game lead somewhere that is not simply one step over.
        /// </summary>
        private static bool Better(MapInfo candidate, MapInfo best, MapInfo here)
        {
            if (candidate.Outdoor != best.Outdoor) return candidate.Outdoor;

            bool candidateSame = candidate.SubAreaId == here.SubAreaId;
            bool bestSame = best.SubAreaId == here.SubAreaId;
            if (candidateSame != bestSame) return candidateSame;

            return Math.Abs(candidate.MapId - here.MapId) < Math.Abs(best.MapId - here.MapId);
        }

        /// <summary>
        /// The cell on the other side, checked against the map.
        ///
        /// The check is against the fight walkability and not against map_walkable_cells.json:
        /// that file trims the borders on purpose so that monsters do not spawn on them, and the
        /// border is precisely where somebody arriving from the next map lands. Asking it would
        /// drag every arrival several cells inland.
        /// </summary>
        private static int Landing(long map, int cell, Way way)
        {
            int arrival = way switch
            {
                Way.Right => cell - SideStep,
                Way.Left => cell + SideStep,
                Way.Up => cell + VerticalStep,
                Way.Down => cell - VerticalStep,
                _ => cell,
            };

            if (arrival < 0 || arrival > 559) arrival = cell;

            var walkable = MapManager.GetFightWalkable(map);
            if (walkable == null || walkable.Contains(arrival)) return arrival;

            int nearest = MapManager.GetNearestWalkableCell(map, arrival);
            Console.WriteLine($"[Move] Cell {arrival} of map {map} cannot be stood on; {nearest} instead.");
            return nearest;
        }

        private static int FacingFor(Way way, int current) => way switch
        {
            Way.Right => 0,
            Way.Down => 2,
            Way.Left => 4,
            Way.Up => 6,
            _ => current,
        };
    }
}

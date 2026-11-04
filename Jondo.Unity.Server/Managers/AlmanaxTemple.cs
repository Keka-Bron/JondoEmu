using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The Almanax's temple (subarea 178, -4,-24): the sanctuary where Ontoral Zo stands, the four
    /// halls of the Temple of Scriptures, the four seasons' rooms -- and today's saint, the only
    /// one standing, in the room of today.
    /// </summary>
    /// <remarks>
    /// <b>INFERRED: no capture sets foot in the temple.</b> What it stands on, all of it the client's:
    /// <list type="bullet">
    ///   <item><b>The season of a day</b> is the one its offering's prayer names, «Reza ante el altar
    ///   de verano»: spring from 20 March, summer from 21 June, autumn from 22 September, winter
    ///   from 21 December -- read off the 366 days' prayers, which change on those days and on no
    ///   other.</item>
    ///   <item><b>Each season has four rooms</b>, four maps named «Spring Room», «Summer Room»…
    ///   (MapPositions): three alike, and a fourth drawn darker, whose altar is one of 535031-535034.
    ///   The 80 saints whose «ve a ver a …» objective names a map
    ///   all stand in the summer's three lit rooms, a month each, from the 21st to the 20th:
    ///   101453313 from 1 to 20 July, 101453315 from 21 July to 20 August, 101453317 from 21
    ///   August to 17 September. The other seasons' rooms are numbered the same way, so their
    ///   months are read the same way. The dark one is the room asleep: the season's when it is
    ///   not today's.</item>
    ///   <item><b>The four halls</b> (101450249, 101451273, 101452297, 101453321) are one room four
    ///   times, each with the four seasons' doors -- spring on cell 244, summer 273, autumn 346,
    ///   winter 375, as the 2.73 world graph sends them -- and one of the four drawn otherwise
    ///   (38572-38575 instead of 38566-38569): that hall is the one of that door's season. Nor has
    ///   the graph a static way through that door: it is the one that leads to today.</item>
    /// </list>
    /// So: the sanctuary's door leads to today's hall; there today's season's door to today's
    /// room, and the other three to their rooms asleep; and every room's way out back to today's
    /// hall, by the door of its season.
    /// </remarks>
    public static class AlmanaxTemple
    {
        public enum Season { Spring, Summer, Autumn, Winter }

        /// <summary>Ontoral Zo's sanctuary, and its door to the halls (element 476222).</summary>
        public const long Sanctuary = 101450251;
        public const int SanctuaryDoorCell = 275;

        /// <summary>Where one lands in a hall: by its way out, on cell 466.</summary>
        public const int HallArrival = 452, HallExitCell = 466;

        /// <summary>Where one lands in a room: the 2.73 graph's cell, by its two ways out (437, 452).</summary>
        public const int RoomArrival = 423;
        public static readonly int[] RoomExitCells = { 437, 452 };

        /// <summary>
        /// Where today's saint stands: across the pond from the way in, facing it (south-west). No
        /// capture says where; any cell of the room would close the objective.
        /// </summary>
        public const int SaintCell = 302, SaintFacing = 3;

        /// <summary>
        /// Where a room's altar stands: «Reza ante el altar de otoño» is said there. Measured on the
        /// client's maps: each of the sixteen rooms has one element besides its two ways out, on this
        /// cell, drawn with its season's altar (38616 to 38619).
        /// </summary>
        public const int AltarCell = 384;

        private static readonly (Season Season, int Month, int Day)[] Starts =
        {
            (Season.Spring, 3, 20), (Season.Summer, 6, 21), (Season.Autumn, 9, 22), (Season.Winter, 12, 21),
        };

        /// <summary>The first of the season's three months, counting a month from the 21st.</summary>
        private static int FirstMonth(Season season) => season switch
        {
            Season.Spring => 3,
            Season.Summer => 6,
            Season.Autumn => 9,
            _ => 12,
        };

        /// <summary>The hall where the season's door is the one drawn otherwise.</summary>
        public static long HallOf(Season season) => season switch
        {
            Season.Spring => 101450249,
            Season.Summer => 101451273,
            Season.Autumn => 101452297,
            _ => 101453321,
        };

        /// <summary>The season's door in every hall.</summary>
        public static int DoorCellOf(Season season) => season switch
        {
            Season.Spring => 244,
            Season.Summer => 273,
            Season.Autumn => 346,
            _ => 375,
        };

        /// <summary>The map before the season's four rooms: +1, +3, +5 lit, +7 asleep.</summary>
        private static long RoomsBase(Season season) => season switch
        {
            Season.Spring => 101452288,
            Season.Summer => 101453312,
            Season.Autumn => 101450240,
            _ => 101451264,
        };

        private static readonly Season[] AllSeasons = { Season.Spring, Season.Summer, Season.Autumn, Season.Winter };

        public static Season SeasonOf(DateTime date)
        {
            int md = date.Month * 100 + date.Day;
            var season = Season.Winter;
            foreach (var (s, month, day) in Starts)
            {
                if (md >= month * 100 + day) season = s;
            }
            return season;
        }

        /// <summary>Which of its season's three months a day falls in, a month running from the 21st.</summary>
        public static int ThirdOf(DateTime date)
        {
            int month = date.Day >= 21 ? date.Month : date.Month - 1;
            if (month == 0) month = 12;
            int k = (month - FirstMonth(SeasonOf(date)) + 12) % 12;
            // 20 March is spring's before its first 21st; 21 September summer's after its last.
            return k == 11 ? 0 : Math.Min(k, 2);
        }

        /// <summary>Today's room of a date: its season's lit room of that month.</summary>
        public static long RoomOf(DateTime date) => RoomsBase(SeasonOf(date)) + 1 + 2 * ThirdOf(date);

        /// <summary>The season's room asleep, where its door leads when it is not today's.</summary>
        public static long AsleepRoomOf(Season season) => RoomsBase(season) + 7;

        private static Season? SeasonOfRoom(long mapId)
        {
            foreach (var season in AllSeasons)
            {
                long offset = mapId - RoomsBase(season);
                if (offset is 1 or 3 or 5 or 7) return season;
            }
            return null;
        }

        public static bool IsHall(long mapId) => AllSeasons.Any(s => HallOf(s) == mapId);

        public static bool IsRoom(long mapId) => SeasonOfRoom(mapId) != null;

        /// <summary>A room's altar: its element on <see cref="AltarCell"/>. Zero when the map is no room or has none.</summary>
        public static int AltarOf(long mapId)
        {
            if (!IsRoom(mapId)) return 0;
            foreach (var element in Interactives.ElementsOf(mapId))
                if (element.Cell == AltarCell) return element.Id;
            return 0;
        }

        /// <summary>
        /// Where a way of the temple leads today: the element on that cell clicked, or the cell
        /// walked onto. Null when the cell is no way of the temple's.
        /// </summary>
        public static (long Map, int Cell)? Destination(long mapId, int cell, DateTime now)
        {
            var today = SeasonOf(now);
            if (mapId == Sanctuary)
                return cell == SanctuaryDoorCell ? (HallOf(today), HallArrival) : null;

            if (IsHall(mapId))
            {
                if (cell == HallExitCell) return (Sanctuary, SanctuaryDoorCell);
                foreach (var season in AllSeasons)
                {
                    if (DoorCellOf(season) != cell) continue;
                    return (season == today ? RoomOf(now) : AsleepRoomOf(season), RoomArrival);
                }
                return null;
            }

            if (SeasonOfRoom(mapId) is { } of && RoomExitCells.Contains(cell))
                return (HallOf(today), DoorCellOf(of));
            return null;
        }

        /// <summary>The temple's ways, element by element, for the interactives to declare.</summary>
        public static IEnumerable<(long Map, Interactives.Element Element)> Ways()
        {
            foreach (var element in Interactives.ElementsOf(Sanctuary))
                if (element.Cell == SanctuaryDoorCell) yield return (Sanctuary, element);

            foreach (var season in AllSeasons)
            {
                long hall = HallOf(season);
                foreach (var element in Interactives.ElementsOf(hall))
                {
                    if (element.Cell == HallExitCell || AllSeasons.Any(s => DoorCellOf(s) == element.Cell))
                        yield return (hall, element);
                }

                foreach (int k in new[] { 1, 3, 5, 7 })
                {
                    long room = RoomsBase(season) + k;
                    foreach (var element in Interactives.ElementsOf(room))
                        if (RoomExitCells.Contains(element.Cell)) yield return (room, element);
                }
            }
        }

        // ─── Today's saint ──────────────────────────────────────────────────────

        private static readonly object _gate = new object();
        private static long _placedDay = -1;
        private static bool _cleared;
        private static (long Map, long Actor)? _placed;

        /// <summary>
        /// Puts today's saint in today's room, before anyone sees a room of the temple: the first
        /// time, the saints the quest catalogue had placed in the rooms for good are taken away;
        /// every new day, yesterday's goes and today's comes.
        /// </summary>
        public static void KeepSaint(long mapId)
        {
            if (!IsRoom(mapId)) return;
            var now = Almanax.Clock();
            if (Volatile.Read(ref _placedDay) == now.Date.Ticks) return;

            lock (_gate)
            {
                if (_placedDay == now.Date.Ticks) return;
                var today = Almanax.Today();
                if (today == null) return;

                if (!_cleared)
                {
                    var standing = Npcs.AllSpawns.Where(s => IsRoom(s.MapId) && Almanax.IsSaint(s.NpcId))
                                       .Select(s => (s.MapId, s.ContextualId)).ToList();
                    foreach (var (map, actor) in standing) Npcs.RemoveAtRuntime(map, actor);
                    _cleared = true;
                }

                if (_placed is { } yesterday) Npcs.RemoveAtRuntime(yesterday.Map, yesterday.Actor);
                _placed = null;

                if (today.Saint > 0)
                {
                    var spawn = Npcs.PlaceAtRuntime(RoomOf(now), today.Saint, SaintCell, SaintFacing);
                    if (spawn != null) _placed = (spawn.MapId, spawn.ContextualId);
                }

                Volatile.Write(ref _placedDay, now.Date.Ticks);
            }
        }

        /// <summary>Today's saint, where it stands: for the tests.</summary>
        internal static (long Map, long Actor)? PlacedSaint => _placed;

        /// <summary>
        /// At start, once the calendar is read: every room's NPC list made, and today's saint put.
        /// </summary>
        public static void Prepare()
        {
            foreach (var season in AllSeasons)
                foreach (int k in new[] { 1, 3, 5, 7 }) Npcs.Reserve(RoomsBase(season) + k);
            KeepSaint(RoomOf(Almanax.Clock()));
            if (_placed is { } placed)
                Console.WriteLine($"[Almanax] Today's saint stands in the temple, map {placed.Map}, cell {SaintCell}.");
        }

        /// <summary>The NPCs read anew: no saint of ours stands any more, nor were the others taken.</summary>
        internal static void Forget()
        {
            lock (_gate)
            {
                _placed = null;
                _cleared = false;
                Volatile.Write(ref _placedDay, -1);
            }
        }

        /// <summary>Takes the saint away and forgets the day, for the tests.</summary>
        internal static void Reset()
        {
            lock (_gate)
            {
                if (_placed is { } placed) Npcs.RemoveAtRuntime(placed.Map, placed.Actor);
                _placed = null;
                Volatile.Write(ref _placedDay, -1);
            }
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.Server.Bots
{
    /// <summary>
    /// What a simulated player does in the world, a thing at a time with pauses between, as a
    /// player does: walk about its map, go to the next one, attack a group of monsters it can beat,
    /// gather a resource of its professions, enrol in the Koliseo, challenge another of the bots --
    /// and, at war, aggress an enemy or hunt its prey (SimulatedPlayer.War).
    /// </summary>
    internal sealed partial class SimulatedPlayer
    {
        /// <summary>
        /// The area the bots live in: the one of the map new characters start on (Astrub), where
        /// new players arrive and where there is something for every activity.
        /// </summary>
        internal static readonly Lazy<int> HomeArea = new(() => AreaOf(CharacterCreationHandler.StartingMap));

        /// <summary>The ways out of each map, worked out once: the map past each border, and its cells.</summary>
        private static readonly ConcurrentDictionary<long, List<(long To, int[] Cells)>> Exits = new();

        /// <summary>The kum's field of the characteristic its capital goes to: intelligence, chance, agility or strength.</summary>
        private static readonly int[] Elements = { 1, 2, 5, 6 };

        /// <summary>Since when it waits in a Koliseo queue; null when it is in none.</summary>
        private DateTime? _queuedSince;

        private async Task LiveAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested && !_closed)
            {
                var session = Session;
                if (session == null || !session.IsInWorld) return;
                if (DateTime.UtcNow >= _leaveAt) _leaving = true;

                // A fight plays itself from what the server sends (SimulatedPlayer.Fight).
                if (session.State.IsInFight)
                {
                    await Task.Delay(1000, stop);
                    continue;
                }
                if (_leaving)
                {
                    if (_queuedSince != null) await SendAsync(Op.Lsi);
                    Console.WriteLine($"[Bots] {_name} leaves the world.");
                    return;
                }

                await SpendCapitalAsync();
                await ActAsync(session, stop);
                await Task.Delay(_dice.Next(2000, 10000), stop);
            }
        }

        /// <summary>
        /// One thing to do, drawn: a fight, a resource, the Koliseo, a duel, another map or a walk,
        /// each falling through to the next when there is nothing for it here.
        /// </summary>
        private async Task ActAsync(GameSession session, CancellationToken stop)
        {
            // A queue waited in long enough is left, as a player gives up on it.
            if (_queuedSince is DateTime since && DateTime.UtcNow - since > TimeSpan.FromMinutes(6))
            {
                await SendAsync(Op.Lsi);
                _queuedSince = null;
            }

            int roll = _dice.Next(100);
            bool free = _queuedSince == null;
            if (_atWar && free)
            {
                if (await HuntAsync(session, stop)) return;
                if (await BackToTheFrontAsync(session, stop)) return;
                if (_dice.Next(100) < 4) TakeAnOrder(session);
                if (_dice.Next(100) < 70 && await AggressAsync(session, stop)) return;
                if (_dice.Next(100) < 50 && await SeekAsync(session, stop)) return;
            }
            if (roll < 22 && free && await FightMonstersAsync(session, stop)) return;
            if (roll < 40 && await GatherAsync(session, stop)) return;
            if (roll < 45 && free && await EnrolAsync()) return;
            if (roll < 48 && free && await ChallengeAsync(session)) return;
            if (roll < 68 && await ChangeMapAsync(session, stop)) return;
            if (roll < 92) await WanderAsync(session, stop);
        }

        // ─── Walking ─────────────────────────────────────────────────────────────────────────

        internal static int AreaOf(long map)
        {
            var info = MapManager.GetMapInfo(map);
            return info == null ? -1 : Interactives.AreaOfSubArea(info.SubAreaId);
        }

        /// <summary>
        /// A map's whole floor: the walkable cells and the fight's. The first leave the borders out on
        /// purpose, so that no monster group stands on one (WorldMoveHandler.Landing), and a border
        /// is exactly where a way out is walked to.
        /// </summary>
        private static HashSet<int> WalkableOf(long map)
        {
            var floor = MapManager.WalkableCells.TryGetValue(map, out var cells) ? new HashSet<int>(cells) : new HashSet<int>();
            var fight = MapManager.GetFightWalkable(map);
            if (fight != null) floor.UnionWith(fight);
            return floor;
        }

        /// <summary>A few steps to a cell of the map, as a click would.</summary>
        private async Task<bool> WanderAsync(GameSession session, CancellationToken stop)
        {
            var cells = MobSpawnManager.GetInnerWalkableCells(session.State.MapId);
            if (cells.Count == 0) return false;
            return await WalkToAsync(session, cells[_dice.Next(cells.Count)], stop);
        }

        /// <summary>
        /// Walks to a cell: the jrw with the path's corners, the time the walk takes, then the jqi
        /// that says it has arrived. The path keeps to the map's floor.
        /// </summary>
        private async Task<bool> WalkToAsync(GameSession session, int target, CancellationToken stop)
        {
            long map = session.State.MapId;
            int from = session.State.CellId;
            if (from == target) return true;
            var path = MapGeometry.FindShortestPath(from, target, WalkableOf(map));
            if (path.Count < 2 || path[0] != from || path[^1] != target) return false;

            await SendAsync(Op.Jrw, Pb.New().Var(1, map).Packed(2, Corners(path)).Build());
            await Task.Delay(path.Count * 280, stop);
            await SendAsync("jqi");
            await Task.Delay(300, stop);
            return session.State.MapId == map && session.State.CellId == target;
        }

        /// <summary>
        /// A path as the client sends it: its start, the cells it turns on and its end, each with
        /// the way it faces there in the bits above the cell's twelve.
        /// </summary>
        internal static List<long> Corners(IReadOnlyList<int> path)
        {
            var corners = new List<long>();
            for (int i = 0; i < path.Count; i++)
            {
                int facing = i == 0 ? Facing(path[0], path[1]) : Facing(path[i - 1], path[i]);
                bool turns = i == 0 || i == path.Count - 1 || Facing(path[i], path[i + 1]) != facing;
                if (turns) corners.Add((long)path[i] | ((long)facing << 12));
            }
            return corners;
        }

        /// <summary>
        /// The way one cell looks at the next, in the client's eight: 0 right, then clockwise -- 1
        /// down-right, 2 down, 3 down-left, 4 left, 5 up-left, 6 up, 7 up-right. Odd rows sit half a
        /// cell to the right.
        /// </summary>
        internal static int Facing(int from, int to)
        {
            int Sx(int c) => (c % 14) * 2 + ((c / 14) & 1);
            int dx = Math.Sign(Sx(to) - Sx(from)), dy = Math.Sign(to / 14 - from / 14);
            return (dx, dy) switch
            {
                (1, 0) => 0, (1, 1) => 1, (0, 1) => 2, (-1, 1) => 3,
                (-1, 0) => 4, (-1, -1) => 5, (0, -1) => 6, _ => 7,
            };
        }

        /// <summary>
        /// To the next map: a border with a map past it in the bot's area, walked to, and the jqk
        /// that asks for that map; the server answers with the map to load (jru). Out of its area
        /// -- after a hunt -- any way out, until it is back.
        /// </summary>
        private async Task<bool> ChangeMapAsync(GameSession session, CancellationToken stop)
        {
            var exits = Exits.GetOrAdd(session.State.MapId, WaysOut);
            var ways = exits.Where(w => AreaOf(w.To) == _range).ToList();
            if (ways.Count == 0 && AreaOf(session.State.MapId) != _range) ways = exits;
            if (ways.Count == 0) return false;
            return await TakeTheWayAsync(session, ways[_dice.Next(ways.Count)], stop);
        }

        private async Task<bool> TakeTheWayAsync(GameSession session, (long To, int[] Cells) way, CancellationToken stop)
        {
            int here = session.State.CellId;
            int door = way.Cells.OrderBy(c => MapGeometry.Distance(here, c)).First();
            if (!await WalkToAsync(session, door, stop)) return false;

            _arrived = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            await SendAsync(Op.Jqk, Pb.New().Var(2, way.To).Build());
            return await WithinAsync(_arrived.Task) == way.To;
        }

        /// <summary>The maps past a map's four borders, each with the border's walkable cells.</summary>
        internal static List<(long To, int[] Cells)> WaysOut(long map)
        {
            var floor = WalkableOf(map);
            var sides = new[]
            {
                floor.Where(c => c % 14 == 13).ToArray(),
                floor.Where(c => c % 14 == 0).ToArray(),
                floor.Where(c => c / 14 <= 1 && c % 14 is not 0 and not 13).ToArray(),
                floor.Where(c => c / 14 >= 38 && c % 14 is not 0 and not 13).ToArray(),
            };
            var ways = new List<(long, int[])>();
            foreach (var cells in sides)
            {
                if (cells.Length == 0) continue;
                long to = WorldMoveHandler.NeighbourPast(map, cells[0]);
                if (to > 0 && MapManager.WalkableCells.TryGetValue(to, out var there) && there.Count > 0)
                    ways.Add((to, cells));
            }
            return ways;
        }

        // ─── Monsters ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A group of the map it can beat -- their levels together no more than half again its own
        /// -- walked up to and attacked (hqa), as a click on it does.
        /// </summary>
        private async Task<bool> FightMonstersAsync(GameSession session, CancellationToken stop)
        {
            long map = session.State.MapId;
            int level = session.State.CharacterLevel;
            var groups = MobSpawnManager.GetMobsForMap(map)
                .Where(g => g.Members.Count > 0 && !FightHandler.IsGroupFighting(g.MobId)
                            && g.Members.Sum(m => m.Level) <= Math.Max(12, level * 3 / 2))
                .ToList();
            if (groups.Count == 0) return false;
            var group = groups[_dice.Next(groups.Count)];

            await WalkNextToAsync(session, group.CellId, stop);
            if (session.State.MapId != map) return false;
            await SendAsync(Op.Hqa, Pb.New().Var(1, group.MobId).Build());
            for (int waited = 0; waited < 50 && !session.State.IsInFight; waited++) await Task.Delay(100, stop);
            return session.State.IsInFight;
        }

        /// <summary>Walks to the floor cell nearest to <paramref name="cell"/> it can reach.</summary>
        private async Task WalkNextToAsync(GameSession session, int cell, CancellationToken stop)
        {
            var floor = WalkableOf(session.State.MapId);
            var beside = MapGeometry.GetNeighbors(cell).Where(floor.Contains).ToList();
            if (floor.Contains(cell)) beside.Add(cell);
            int here = session.State.CellId;
            foreach (int target in beside.OrderBy(c => MapGeometry.Distance(here, c)))
            {
                if (target == here || await WalkToAsync(session, target, stop)) return;
            }
        }

        // ─── Gathering ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A full resource of the map its profession reaches, walked up to and used (iwo); the
        /// server does the rest, the three seconds of the gesture and the harvest, as for anybody.
        /// </summary>
        private async Task<bool> GatherAsync(GameSession session, CancellationToken stop)
        {
            long map = session.State.MapId;
            var ripe = Resources.On(map)
                .Where(r => Resources.StateOf(map, r.ElementId) == ResourceState.Full
                            && session.State.JobLevel(r.JobId) >= r.LevelMin)
                .ToList();
            if (ripe.Count == 0) return false;
            var resource = ripe[_dice.Next(ripe.Count)];

            await WalkNextToAsync(session, resource.Cell, stop);
            if (session.State.MapId != map) return false;
            await SendAsync(Op.Iwo, Pb.New()
                .Var(1, Interactives.SkillInstanceOf(resource.ElementId))
                .Var(2, resource.ElementId).Build());
            await Task.Delay(TimeSpan.FromSeconds(Resources.GatherTenths / 10.0 + 1), stop);
            return true;
        }

        // ─── The Koliseo and duels ───────────────────────────────────────────────────────────

        /// <summary>
        /// Into the Koliseo's queue (lsm with the mode): mostly the 1 against 1, now and then the
        /// 2 or 3 against 2 or 3. The match it is offered is accepted when it comes (lsh).
        /// </summary>
        private async Task<bool> EnrolAsync()
        {
            if (KoliseoOffers.MinutesLeft(_characterId) > 0 || KoliseoQueue.Waits(_characterId)) return false;
            int mode = _dice.Next(10) < 7 ? 0 : _dice.Next(1, 3);
            await SendAsync(Op.Lsm, Pb.New().VarIfNotZero(1, mode).Build());
            _queuedSince = DateTime.UtcNow;
            return true;
        }

        private async Task AcceptKoliseoAsync()
        {
            await Task.Delay(_dice.Next(1000, 4000));
            await SendAsync(Op.Luy, Pb.New().Var(2, 1).Build());
            _queuedSince = null;
        }

        /// <summary>
        /// A duel offered to another bot on its map (hph, friendly). Only to the bots: a player is
        /// never pestered with one, but whoever offers one to a bot always gets a yes.
        /// </summary>
        private async Task<bool> ChallengeAsync(GameSession session)
        {
            var others = SessionRegistry.All()
                .Where(s => s.IsInWorld && s.MapId == session.State.MapId && s.CharacterId != _characterId
                            && SimulatedPlayers.IsSimulated(s.AccountId) && !s.State.IsInFight)
                .ToList();
            if (others.Count == 0) return false;
            var other = others[_dice.Next(others.Count)];
            await SendAsync(Op.Hph, Pb.New().Var(1, other.CharacterId).Var(2, 1).Build());
            return true;
        }

        private async Task AcceptDuelAsync(int duel)
        {
            await Task.Delay(_dice.Next(1000, 3000));
            // Out of the Koliseo's queue first: a match found in the middle would be lost.
            if (_queuedSince != null)
            {
                await SendAsync(Op.Lsi);
                _queuedSince = null;
            }
            await SendAsync(Op.Hpu, Pb.New().Var(1, duel).Var(2, 1).Build());
        }

        // ─── Its sheet ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Its capital, all of it, into its element (kum is the sheet as it should be, not an
        /// increment): the element is its number's, so each bot keeps one.
        /// </summary>
        private async Task SpendCapitalAsync()
        {
            var session = Session;
            if (session == null || session.State.CharacterRemainingPoints <= 0) return;
            int capital = StatsHandler.TotalCapitalForLevel(session.State.CharacterLevel);
            // Once per total: the server takes a moment to put the points down.
            if (capital <= _capitalSent) return;
            _capitalSent = capital;
            await SendAsync(Op.Kum, Pb.New().Var(Elements[_number % Elements.Length], capital).Build());
        }

        /// <summary>The capital it last asked to have spent.</summary>
        private int _capitalSent;
    }
}

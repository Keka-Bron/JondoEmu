using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Bots
{
    /// <summary>
    /// The way from one map to another, as a player goes: over the borders, through the doors and
    /// the cells that take one elsewhere, and by zaap when it is far.
    /// </summary>
    internal sealed partial class SimulatedPlayer
    {
        /// <summary>How a step of a route is taken.</summary>
        internal enum StepKind { Border, Passage, Zaap }

        /// <summary>
        /// One step: to <see cref="To"/>, over a border (its cells), through a passage (its cell,
        /// and its element when the cell cannot be walked on), or by the zaap of the map.
        /// </summary>
        internal sealed record Step(StepKind Kind, long To, int[] Cells, int Cell, int Element, int Instance);

        /// <summary>A zaap trip costs what fifteen maps walked do: it is taken when the walk is longer.</summary>
        private const int ZaapCost = 15;

        /// <summary>How many maps a search may look at before it gives up.</summary>
        private const int SearchLimit = 40000;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, List<Step>> _steps = new();

        /// <summary>The steps out of a map, worked out once.</summary>
        internal static List<Step> StepsFrom(long map) => _steps.GetOrAdd(map, ReadSteps);

        private static List<Step> ReadSteps(long map)
        {
            var steps = Exits.GetOrAdd(map, WaysOut)
                .Select(w => new Step(StepKind.Border, w.To, w.Cells, 0, 0, 0)).ToList();

            var floor = WalkableOf(map);
            foreach (var route in TeleportManager.On(map))
            {
                if (route.NpcId != 0 || route.Criterion.Length > 0 || route.DestinationMapId <= 0) continue;
                if (MapManager.GetMapInfo(route.DestinationMapId) == null) continue;
                int instance = 0;
                if (route.ElementId > 0)
                {
                    var registered = InteractiveRegistry.OnMap(map).FirstOrDefault(i => i.Element.Id == route.ElementId);
                    instance = registered?.Actions.FirstOrDefault(a => a.Kind == InteractiveActionKind.Teleport)?.SkillInstanceId ?? 0;
                }
                if (!floor.Contains(route.SourceCellId) && instance == 0) continue;
                steps.Add(new Step(StepKind.Passage, route.DestinationMapId, Array.Empty<int>(),
                                   route.SourceCellId, route.ElementId, instance));
            }

            var zaap = InteractiveRegistry.OnMap(map)
                .SelectMany(i => i.Actions.Where(a => a.Kind == InteractiveActionKind.Zaap).Select(a => (i.Element, a.SkillInstanceId)))
                .FirstOrDefault();
            if (zaap.SkillInstanceId != 0)
            {
                foreach (var waypoint in Interactives.Waypoints)
                {
                    if (!waypoint.Activated || waypoint.MapId == map || MapManager.GetMapInfo(waypoint.MapId) == null) continue;
                    steps.Add(new Step(StepKind.Zaap, waypoint.MapId, Array.Empty<int>(), zaap.Element.Cell,
                                       zaap.Element.Id, zaap.SkillInstanceId));
                }
            }
            return steps;
        }

        /// <summary>The cheapest way from a map to the first map that is a goal, or null when there is none.</summary>
        internal static List<Step>? RouteTo(long from, Func<long, bool> goal, ISet<(long From, long To)>? avoid = null)
        {
            if (goal(from)) return new List<Step>();
            var cost = new Dictionary<long, int> { [from] = 0 };
            var came = new Dictionary<long, (long From, Step Step)>();
            var open = new PriorityQueue<long, int>();
            open.Enqueue(from, 0);
            int looked = 0;
            while (open.TryDequeue(out long map, out int paid))
            {
                if (paid > cost[map]) continue;
                if (goal(map))
                {
                    var route = new List<Step>();
                    for (long at = map; at != from; at = came[at].From) route.Add(came[at].Step);
                    route.Reverse();
                    return route;
                }
                if (++looked > SearchLimit) break;
                foreach (var step in StepsFrom(map))
                {
                    if (avoid != null && avoid.Contains((map, step.To))) continue;
                    int next = paid + (step.Kind == StepKind.Zaap ? ZaapCost : 1);
                    if (cost.TryGetValue(step.To, out int known) && known <= next) continue;
                    cost[step.To] = next;
                    came[step.To] = (map, step);
                    open.Enqueue(step.To, next);
                }
            }
            return null;
        }

        /// <summary>
        /// Goes to the first map that is a goal, step by step; false when there is no way or a step
        /// did not take it where it should have.
        /// </summary>
        private async Task<bool> GoToAsync(GameSession session, Func<long, bool> goal, CancellationToken stop)
        {
            // A step that did not go is not tried again on this trip: the way is sought anew
            // without it, twice at the most.
            var failed = new HashSet<(long From, long To)>();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var route = RouteTo(session.State.MapId, goal, failed);
                if (route == null) return false;
                bool went = true;
                foreach (var step in route)
                {
                    if (_closed || session.State.IsInFight) return false;
                    long at = session.State.MapId;
                    if (!await TakeAsync(session, step, stop))
                    {
                        SimulatedPlayers.Journal($"{_name}: a {step.Kind} step from {at} to {step.To} did not take it there " +
                                                 $"(it is on {session.State.MapId}).");
                        failed.Add((at, step.To));
                        went = false;
                        break;
                    }
                    // The map loads, as the client's does, before the next step: the server puts it
                    // on its cell only after the jrh.
                    await Task.Delay(800, stop);
                }
                if (went) return goal(session.State.MapId);
            }
            return false;
        }

        private Task<bool> GoToAsync(GameSession session, long map, CancellationToken stop)
            => GoToAsync(session, m => m == map, stop);

        /// <summary>One step of a route, as a click does it.</summary>
        private async Task<bool> TakeAsync(GameSession session, Step step, CancellationToken stop)
        {
            switch (step.Kind)
            {
                case StepKind.Border:
                    return await TakeTheWayAsync(session, (step.To, step.Cells), stop);

                case StepKind.Passage:
                    _arrived = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (WalkableOf(session.State.MapId).Contains(step.Cell))
                    {
                        // Onto the passage's cell: the walk's end takes it through (WorldMoveHandler),
                        // so the walk itself ends on another map and is not asked how it went.
                        await WalkToAsync(session, step.Cell, stop);
                    }
                    else
                    {
                        await WalkNextToAsync(session, step.Cell, stop);
                        await SendAsync(Op.Iwo, Pb.New().Var(1, step.Instance).Var(2, step.Element).Build());
                    }
                    return await WithinAsync(_arrived.Task) == step.To;

                default:
                    await WalkNextToAsync(session, step.Cell, stop);
                    _arrived = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                    await SendAsync(Op.Iwo, Pb.New().Var(1, step.Instance).Var(2, step.Element).Build());
                    await Task.Delay(800, stop);
                    await SendAsync(Op.Hjc, Pb.New().Var(3, step.To).Build());
                    return await WithinAsync(_arrived.Task) == step.To;
            }
        }
    }
}

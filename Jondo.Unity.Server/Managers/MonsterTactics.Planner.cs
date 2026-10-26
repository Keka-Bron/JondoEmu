using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.World.Combat;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// A whole turn, planned: which casts, in which order, from where -- the sequence worth most,
    /// not the best blow at each step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Next"/> takes, again and again, the best thing per AP that it sees. It played as a
    /// player who never thinks two casts ahead: the same cheap spell until its cap and three AP left
    /// over; the power buff weighed against one blow when it raises five; the AP that Precipitación
    /// gives worth nothing, because a point is only worth the casts it pays for; a summon put down
    /// where it can do nothing for the zone blows that follow it.
    /// </para>
    /// <para>
    /// This looks for the turn as a beam: from where the bot stands it takes the few best things to
    /// do, plays each of them on a copy of the fight (<see cref="Simulation"/>) -- the AP and MP spent,
    /// the cell it ends on, the life it takes, the states it leaves, the buffs it gives itself, the
    /// creature it summons --, and from each of those the few best again, until the AP run out or
    /// nothing is worth doing. The sequence worth most in total wins, and its first step is taken. So a
    /// state put first is worth what the blows that read it do after; +2 AP is worth the cast it buys;
    /// a creature that hands back its blows is summoned next to the enemy and then struck.
    /// </para>
    /// </remarks>
    public static partial class MonsterTactics
    {
        /// <summary>How long a plan may look, in milliseconds, before it takes the best it has found.</summary>
        internal const int PlanBudgetMs = 2000;

        /// <summary>How many sequences are kept at each depth.</summary>
        internal const int PlanWidth = 6;

        /// <summary>How many casts deep a turn is looked at: twelve AP of two-AP spells are six, and a buff can add more.</summary>
        internal const int PlanDepth = 8;

        /// <summary>How many of the best next steps each sequence tries.</summary>
        internal const int PlanBranch = 6;

        /// <summary>
        /// What an AP not yet spent is worth, as a share of the best rate of the turn: enough that
        /// a sequence keeping the AP for a big blow is not cut for one that spent them on little.
        /// </summary>
        private const double HeldApShare = 0.8;

        /// <summary>One sequence of the beam.</summary>
        private sealed class Node
        {
            public List<Action> Steps { get; init; } = new();
            public double Total { get; init; }
            public int ActionPoints { get; init; }
            public double Terminal { get; set; }

            /// <summary>
            /// The AP a creature just put where a teleport lands promises -- the telefrag's +2 --:
            /// it keeps the line in the beam for the cast that would give them, and counts for
            /// nothing after it, nor in the end.
            /// </summary>
            public int PromisedAp { get; init; }

            /// <summary>
            /// What a switch of the bot's own state just made promises -- how much better the best
            /// cast it opens is than the best there was --, for the cast after it, like
            /// <see cref="PromisedAp"/>: it keeps the line in the beam and counts for nothing in the end.
            /// </summary>
            public double PromisedValue { get; init; }
        }

        /// <summary>How deep in the turn a switch of the bot's own state looks one cast ahead: at its start, where it is played.</summary>
        private const int SwitchLookahead = 1;

        /// <summary>How deep a portal laid looks ahead: the first and the second, which makes the network.</summary>
        private const int PortalLookahead = 2;

        /// <summary>How many cells by the enemy a first portal's second is looked at on.</summary>
        private const int SecondPortalTries = 4;

        /// <summary>
        /// What the bot's portals promise its coming turn's blows, in percent, «+#3% daños, +#1%
        /// por casilla que separe entre 2 portales»: the network's own, through a way in within a
        /// walk and a blow of the bot whose way out is within a blow of an enemy -- a projection
        /// lands as far from the way out as the caster stands from the way in --; and when none
        /// is, the one a portal more by the enemy will make with a portal of the bot's at hand.
        /// It was counted from its portal furthest from the enemy, wherever the bot was: a
        /// Selatrop sowed portals on its own side, far from everybody, and never struck through.
        /// </summary>
        private static int PortalPromise(Board board, Fighter bot, IReadOnlyList<Spell> spells)
        {
            var network = board.Portals?.Invoke();
            var mine = network?.All.Where(p => p.Owner == bot.Id).ToList();
            if (network == null || mine == null || mine.Count == 0) return 0;
            var foes = Foes(board, bot);
            if (foes.Count == 0) return 0;
            int blows = BlowsThroughPortals(spells, bot);
            if (blows <= 0) return 0;
            int walk = Math.Max(0, bot.MaxMP);
            long Occupant(int cell)
                => board.Fighters.FirstOrDefault(f => f.IsAlive && !f.EstaCargado && f != bot && f.CellId == cell)?.Id ?? 0;
            int best = 0;
            foreach (var entry in mine)
            {
                if (MapGeometry.Distance(entry.Cell, bot.CellId) > walk + blows) continue;
                var chain = network.Chain(entry, Occupant, bot.Id);
                if (chain.Count < 2) continue;
                int exit = chain[chain.Count - 1].Cell;
                if (foes.Any(e => MapGeometry.Distance(exit, e.CellId) <= blows))
                    best = Math.Max(best, PortalNetwork.BonusPercent(chain));
            }
            if (best > 0) return best;
            int laying = spells.Where(s => s.LaysPortals).Select(s => s.ReachFor(bot)).DefaultIfEmpty(0).Max();
            if (!foes.Any(e => MapGeometry.Distance(bot.CellId, e.CellId) <= walk + laying + 1)) return 0;
            var atHand = mine.Where(p => MapGeometry.Distance(p.Cell, bot.CellId) <= walk + 1).ToList();
            return atHand.Count == 0 ? 0
                : atHand.Max(p => Math.Max(0, p.Value) + Math.Max(0, p.DiceNum) * foes.Min(e => MapGeometry.Distance(p.Cell, e.CellId)));
        }

        /// <summary>How far the bot's blows that go through portals reach: their longest range.</summary>
        private static int BlowsThroughPortals(IReadOnlyList<Spell> spells, Fighter bot)
            => spells.Where(s => s.Offensive && s.ThroughPortals).Select(s => s.ReachFor(bot)).DefaultIfEmpty(0).Max();

        /// <summary>
        /// What a portal laid on this cell is worth as a way out: the bonus between it and a portal
        /// of the bot's, when a cell the bot can walk to strikes an enemy through that one and out
        /// of this -- that cell is the way in plus the way out less him (PortalNetwork.Projection).
        /// Zero when none does. It orders the cells a portal is tried on: tried by the shortest
        /// walk, the first ones were all by the bot.
        /// </summary>
        private static double ExitWorth(Board board, Fighter bot, Dictionary<int, Route> reach, List<Fighter> foes,
                                        int blows, int cell)
        {
            var mine = board.Portals?.Invoke()?.All.Where(p => p.Owner == bot.Id && p.Cell != cell).ToList();
            if (mine == null || mine.Count == 0 || blows <= 0) return 0;
            var (xx, xy) = MapGeometry.CellToPoint(cell);
            double best = 0;
            foreach (var entry in mine)
            {
                var (ex, ey) = MapGeometry.CellToPoint(entry.Cell);
                foreach (var foe in foes)
                {
                    var (tx, ty) = MapGeometry.CellToPoint(foe.CellId);
                    int from = MapGeometry.PointToCell(ex + xx - tx, ey + xy - ty);
                    if (!MapGeometry.IsValid(from) || !reach.ContainsKey(from) || MapGeometry.Distance(from, entry.Cell) > blows) continue;
                    best = Math.Max(best, Math.Max(0, entry.Value) + Math.Max(0, entry.DiceNum) * MapGeometry.Distance(entry.Cell, cell));
                }
            }
            return best;
        }

        /// <summary>The ExitWorth of each portal option, on the board as it stands, or null when there is none.</summary>
        private static Func<Action, double>? ExitWorths(Board board, Fighter bot, IReadOnlyList<Spell> spells, List<Action> options)
        {
            if (!options.Any(o => o.Spell.LaysPortals)) return null;
            var reach = Routes(board, bot);
            var foes = Foes(board, bot);
            int blows = BlowsThroughPortals(spells, bot);
            var worth = new Dictionary<int, double>();
            return o => o.Spell.LaysPortals
                ? worth.TryGetValue(o.TargetCell, out double known) ? known : worth[o.TargetCell] = ExitWorth(board, bot, reach, foes, blows, o.TargetCell)
                : 0;
        }

        /// <summary>How many portals the bot has out, on its copy of the fight.</summary>
        private static int OwnPortals(Board board, Fighter bot)
            => board.Portals?.Invoke()?.All.Count(p => p.Owner == bot.Id) ?? 0;

        /// <summary>The AP a telefrag gives the Xelor, once per spell and turn.</summary>
        private const int TelefragAp = 2;

        /// <summary>The first cast of the best turn found, or null when nothing is worth doing.</summary>
        public static Action? Plan(Board board, Fighter monster, IReadOnlyList<Spell> spells,
                                   Func<Spell, bool>? allowed = null)
            => PlanTurn(board, monster, spells, allowed).FirstOrDefault();

        /// <summary>The best turn found: its casts in order.</summary>
        public static List<Action> PlanTurn(Board board, Fighter monster, IReadOnlyList<Spell> spells,
                                            Func<Spell, bool>? allowed = null,
                                            int width = PlanWidth, int depth = PlanDepth, int branch = PlanBranch,
                                            System.Action<string>? trace = null, int budgetMs = PlanBudgetMs)
        {
            var beam = new List<Node> { new() { ActionPoints = monster.CurrentAP } };
            var finished = new List<Node>();
            double rate = 0;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int level = 0; level < depth && beam.Count > 0; level++)
            {
                // Past its time the plan stops looking deeper and takes the best it has: a Xelor's
                // spells chain casts within casts, and a turn must not keep the fight waiting.
                if (clock.ElapsedMilliseconds > budgetMs) break;
                var next = new List<Node>();
                foreach (var node in beam)
                {
                    // And within a level: a Zurcarák's cards and sub-spells made one level alone
                    // take over a second. The lines not looked at end where they are.
                    if (clock.ElapsedMilliseconds > budgetMs)
                    {
                        finished.Add(node);
                        continue;
                    }
                    List<Action> children;
                    var grown = new List<Node>();
                    using (var sim = Simulation.Replay(board, monster, node.Steps))
                    {
                        var usable = allowed == null ? spells : spells.Where(allowed).ToList();
                        node.Terminal = Threat(sim, monster, spells);
                        // Each candidate's threat, played and taken back on the same copy: a step is
                        // tried, and its line kept, by what it does now AND by what it leaves for the
                        // coming turn -- or the turn that pins the enemy for the big blow is never tried.
                        var threats = new Dictionary<Action, double>();
                        var hooked = new Dictionary<Action, double>();
                        var apLeft = new Dictionary<Action, int>();
                        // The cells the bot's own leaps and teleports land somebody on, and where
                        // each is cast from, by what the best cast that does it is worth: a creature
                        // summoned there, from that same cell, is a telefrag waiting for that cast.
                        var landings = new Dictionary<(int Cell, int From), double>();
                        // What a switch of its own state opens: the best cast after it, against the
                        // best there is now -- drunk, the Pandawa's Ola Marejadora hits harder and
                        // Fermentación can be cast. A switch alone is worth nothing, or less.
                        var opens = new Dictionary<Action, double>();
                        double bestNow = 0;
                        void Try(Action candidate)
                        {
                            int mark = sim.Mark();
                            double hooksBefore = sim.HookDamage;
                            var stood = sim.Board.Fighters.Where(f => f.IsAlive).Select(f => (Who: f, Cell: f.CellId)).ToList();
                            sim.Play(candidate);
                            var landed = stood.Where(s => s.Who.IsAlive && s.Who.CellId != s.Cell && s.Who.CellId != candidate.From)
                                              .Select(s => s.Who.CellId).ToList();
                            threats[candidate] = Threat(sim, monster, spells);
                            hooked[candidate] = sim.HookDamage - hooksBefore;
                            double worth = candidate.Value + hooked[candidate] + threats[candidate] - node.Terminal;
                            foreach (int cell in landed)
                                landings[(cell, candidate.From)] = Math.Max(worth, landings.TryGetValue((cell, candidate.From), out double was) ? was : double.MinValue);
                            // What the cast leaves, the AP it gives back counted: a telefrag's +2.
                            apLeft[candidate] = monster.CurrentAP;
                            if ((candidate.Spell.Switches && node.Steps.Count < SwitchLookahead)
                                || (candidate.Spell.LaysPortals && node.Steps.Count < PortalLookahead))
                            {
                                var then = allowed == null ? spells : spells.Where(allowed).ToList();
                                double bestThen = Options(sim.Board, monster, then).Select(o => o.Value).DefaultIfEmpty(0).Max();
                                // A first portal opens nothing alone -- a network needs two --: the
                                // second is looked at too, by the enemy, its way out.
                                if (candidate.Spell.LaysPortals && OwnPortals(sim.Board, monster) == 1)
                                {
                                    var foes = Foes(sim.Board, monster);
                                    var layable = Options(sim.Board, monster, then.Where(x => x.LaysPortals).ToList(), movers: true)
                                        .Where(o => o.Path.Count == 1).ToList();
                                    // Where a blow through the first can come out on the enemy, and
                                    // failing that next to him.
                                    var exitWorth = ExitWorths(sim.Board, monster, spells, layable);
                                    var seconds = layable.Where(o => exitWorth != null && exitWorth(o) > 0)
                                        .OrderByDescending(o => exitWorth!(o))
                                        .Concat(layable.Where(o => foes.Any(e => MapGeometry.Distance(e.CellId, o.TargetCell) <= 1)))
                                        .Distinct().Take(SecondPortalTries).ToList();
                                    foreach (var second in seconds)
                                    {
                                        int again = sim.Mark();
                                        sim.Play(second);
                                        bestThen = Math.Max(bestThen, Options(sim.Board, monster, then).Select(o => o.Value).DefaultIfEmpty(0).Max());
                                        sim.Rewind(again);
                                    }
                                }
                                opens[candidate] = Math.Max(0, bestThen - bestNow);
                            }
                            sim.Rewind(mark);
                        }
                        var options = Options(sim.Board, monster, usable, movers: true);
                        bestNow = options.Select(o => o.Value).DefaultIfEmpty(0).Max();
                        foreach (var candidate in Candidates(options, ExitWorths(sim.Board, monster, spells, options))) Try(candidate);
                        // The summons again, on the cells those casts land somebody on.
                        var summons = usable.Where(IsSummon).ToList();
                        var onLandings = landings.Count == 0 || summons.Count == 0 ? new List<Action>()
                            : Options(sim.Board, monster, summons, movers: true, landings: landings.Keys.ToList());
                        foreach (var candidate in SummonsOn(onLandings, landings))
                            if (!threats.ContainsKey(candidate)) Try(candidate);
                        double Worth(Action a) => a.Value + hooked[a] + threats[a] - node.Terminal + opens.GetValueOrDefault(a);
                        children = Pick(threats.Keys.ToList(), branch, Worth);
                        // And the switch that opens most, tried whatever it is worth by itself.
                        children = children.Concat(opens.Where(o => o.Value > 0).OrderByDescending(o => Worth(o.Key))
                                                         .Take(1).Select(o => o.Key))
                                           .Distinct().ToList();
                        // And the creature put where a teleport lands: worth no more than anywhere
                        // else until the cast after it, which only its line can show.
                        var waiting = SummonsOn(threats.Keys.ToList(), landings).Take(2).ToList();
                        children = children.Concat(waiting).Distinct().ToList();
                        foreach (var child in children)
                        {
                            grown.Add(new Node
                            {
                                Steps = new List<Action>(node.Steps) { child },
                                Total = node.Total + child.Value + hooked[child],
                                ActionPoints = apLeft[child],
                                Terminal = threats[child],
                                PromisedAp = waiting.Contains(child) ? TelefragAp : 0,
                                PromisedValue = opens.GetValueOrDefault(child),
                            });
                        }
                    }
                    finished.Add(node);
                    if (level == 0 && children.Count > 0)
                        rate = children.Max(c => c.Value / Math.Max(1, c.Spell.Cost));
                    next.AddRange(grown);
                }
                // The same casts in another order are one line, the better of them: kept twice they
                // filled the beam with the turn that pins the enemy, in every order.
                // And no more than half of it from one line: kept by their worth alone, the sons of
                // one promising step filled the whole beam, and the line it promised for was lost
                // with all the others.
                var perParent = new Dictionary<string, int>();
                int cap = beam.Count <= 1 ? width : Math.Max(1, (width + 1) / 2);
                beam = next.OrderByDescending(n => n.Total + n.Terminal + n.PromisedValue
                                                   + rate * HeldApShare * Math.Max(0, n.ActionPoints + n.PromisedAp))
                           .GroupBy(n => string.Join(",", n.Steps.Select(a => (a.Spell.Id, a.TargetCell)).OrderBy(k => k)))
                           .Select(g => g.First())
                           .Where(n =>
                           {
                               string parent = string.Join(",", n.Steps.Take(n.Steps.Count - 1).Select(a => (a.Spell.Id, a.TargetCell, a.From)));
                               int had = perParent.GetValueOrDefault(parent);
                               perParent[parent] = had + 1;
                               return had < cap;
                           })
                           .Take(width).ToList();
            }
            finished.AddRange(beam);

            // And the greedy turn, played on the copy too: the plan is never worse than what the
            // bot did before it -- the beam can cut a line that would have come out ahead.
            var greedy = Greedy(board, monster, spells, allowed, depth);
            using (var sim = Simulation.Replay(board, monster, greedy.Steps)) greedy.Terminal = Threat(sim, monster, spells);
            finished.Add(greedy);

            var ranked = finished.OrderByDescending(n => n.Total + n.Terminal).ThenBy(n => n.Steps.Count).ToList();
            if (trace != null)
                foreach (var n in ranked.Take(8))
                    trace($"{n.Total + n.Terminal:0} = {n.Total:0} + threat {n.Terminal:0}: " +
                          string.Join(", ", n.Steps.Select(a => $"{a.Spell.Id}({a.Spell.Cost})@{a.TargetCell}")));
            return ranked[0].Steps;
        }

        /// <summary>How much of the coming turn's blow counts in this turn's plan.</summary>
        private const double ThreatWeight = 0.35;

        /// <summary>
        /// How likely an enemy free to walk is still in reach when the bot's turn comes again: of a
        /// blow from next door he steps out of easily, of one from afar much less.
        /// </summary>
        private const double FreeEnemyStaysMelee = 0.25;
        private const double FreeEnemyStaysRanged = 0.6;

        /// <summary>How likely one left with no MP is: he can still be pushed, or teleport.</summary>
        private const double PinnedEnemyStays = 0.9;

        /// <summary>
        /// What the bot's next turn threatens, as the plan leaves the fight: its best blow then -- the
        /// spells its cooldowns will allow, Ira de Yopuka back after its three turns among them -- on
        /// the enemy it can reach, the AP left over spent at its best rate, and the odds the enemy is
        /// still there: likely when the plan left him with no MP for his turn (Influencia, Duelo
        /// Yopukil), much less when he can walk away. It is what makes the turn before a big blow
        /// the turn to pin the enemy down and stay next to him.
        /// </summary>
        internal static double Threat(Simulation sim, Fighter bot, IReadOnlyList<Spell> spells)
        {
            // Read where the turn's end leaves things: Rebobinamiento cast on itself takes the
            // Xelor back to where its turn began, and what its hooks deal then counts too.
            int mark = sim.Mark();
            double hooksBefore = sim.HookDamage;
            sim.EndTurn();
            double dealtAtTheEnd = sim.HookDamage - hooksBefore;
            try
            {
                // What its creatures give it at the start of their turns -- an evolved turret's
                // damage -- and what they deal on them, for the few turns they last.
                var (alone, aloneRaw) = NextBlows(sim, bot, spells);
                double fromSummons = 0;
                if (sim.Board.Fighters.Any(f => f.IsAlive && f.Invocador == bot.Id))
                {
                    sim.SummonsBeginTheirTurns(bot);
                    double helped = NextBlows(sim, bot, spells).Uncapped;
                    fromSummons = Math.Max(0, helped - aloneRaw) + SummonsNextTurn(sim, bot);
                }
                return ThreatWeight * alone * (100 + PortalPromise(sim.Board, bot, spells)) / 100.0
                       + SummonWeight * SummonTurns * fromSummons + dealtAtTheEnd + sim.Pending(bot)
                       + sim.WallsAtTheirTurnStart(bot) - Danger(sim, bot);
            }
            finally
            {
                sim.Rewind(mark);
            }
        }

        /// <summary>The bot's best blow on its coming turn, by the odds the enemy is still there for it: see <see cref="Threat"/>.</summary>
        /// <param name="uncapped">
        /// Without the cap at what the enemy can take: to read what a bonus adds -- an evolved
        /// turret's +20 damage on every blow --, which the cap swallows when the turn kills anyway.
        /// </param>
        internal static double NextBlow(Simulation sim, Fighter bot, IReadOnlyList<Spell> spells, bool uncapped = false)
        {
            var (capped, raw) = NextBlows(sim, bot, spells);
            return uncapped ? raw : capped;
        }

        /// <summary><see cref="NextBlow"/> both ways at once, capped and not: one pass over the spells.</summary>
        private static (double Capped, double Uncapped) NextBlows(Simulation sim, Fighter bot, IReadOnlyList<Spell> spells)
        {
            double best = 0, bestRaw = 0;
            int walk = Math.Max(bot.MaxMP, 0) + Math.Max(0, bot.CurrentMP);
            foreach (var enemy in Foes(sim.Board, bot))
            {
                int distance = MapGeometry.Distance(bot.CellId, enemy.CellId);
                bool pinned = sim.MovementNextTurn(enemy) == 0;
                var castable = new List<(Spell Spell, double Blow)>();
                foreach (var spell in spells)
                {
                    if (spell.Cost <= 0 || spell.Cost > Math.Max(bot.MaxAP, 0)) continue;
                    if (bot.Recarga.TryGetValue(spell.Id, out int wait) && wait > 1) continue;
                    if (distance > walk + spell.ReachFor(bot)) continue;
                    double blow = BlowOf(sim.Board, spell, bot, enemy);
                    if (blow > 0) castable.Add((spell, blow));
                }
                if (castable.Count == 0) continue;
                double rate = castable.Max(c => c.Blow / c.Spell.Cost);
                foreach (var (spell, blow) in castable)
                {
                    // The turn built round this blow: it, and the AP left at the best rate.
                    double turn = blow + (Math.Max(bot.MaxAP, 0) - spell.Cost) * rate;
                    double stays = pinned ? PinnedEnemyStays
                                 : spell.ReachFor(bot) <= 2 ? FreeEnemyStaysMelee : FreeEnemyStaysRanged;
                    best = Math.Max(best, stays * Math.Min(turn, enemy.CurrentHP + enemy.MaxHP * 0.5));
                    bestRaw = Math.Max(bestRaw, stays * turn);
                }
            }
            return (best, bestRaw);
        }

        /// <summary>
        /// How many turns what the bot's creatures give counts for: they stay, and a turret evolved
        /// gives its damage and its blows turn after turn, not once.
        /// </summary>
        private const double SummonTurns = 2;

        /// <summary>
        /// How sure what a creature gives is, each of those turns: more than the bot's own blow
        /// (<see cref="ThreatWeight"/>) -- a turret does not have to walk up to anybody, it reaches
        /// seven cells, and what it gives its summoner comes whatever the enemy does.
        /// </summary>
        private const double SummonWeight = 0.5;

        /// <summary>
        /// What the bot's creatures that play will deal on their coming turn: each its best blow
        /// with its own spells (Board.SpellsOf), as many as its AP pay for, on the enemy it reaches
        /// from where it stands -- the Steamer's turret, which does not walk, hits harder evolved.
        /// </summary>
        internal static double SummonsNextTurn(Simulation sim, Fighter bot)
        {
            if (sim.Board.SpellsOf == null) return 0;
            var foes = Foes(sim.Board, bot);
            double total = 0;
            foreach (var summon in sim.Board.Fighters.Where(f => f.IsAlive && f.Invocador == bot.Id && f.JuegaTurno).ToList())
            {
                double best = 0;
                foreach (var spell in sim.Board.SpellsOf(summon))
                {
                    if (spell.Cost <= 0 || spell.Cost > Math.Max(summon.MaxAP, 0)) continue;
                    if (!SpellCriteria.Allows(summon, spell.Id, spell.Grade)) continue;
                    int casts = Math.Max(1, summon.MaxAP / spell.Cost);
                    if (spell.PerTurn > 0) casts = Math.Min(casts, spell.PerTurn);
                    foreach (var enemy in foes)
                    {
                        int distance = MapGeometry.Distance(summon.CellId, enemy.CellId);
                        if (distance < spell.MinRange || distance > spell.ReachFor(summon) + Math.Max(0, summon.MaxMP)) continue;
                        best = Math.Max(best, BlowOf(sim.Board, spell, summon, enemy) * casts);
                    }
                }
                total += best;
            }
            return total;
        }

        /// <summary>How much of the blows the enemies can bring on the bot in their coming turn counts against the plan.</summary>
        private const double DangerWeight = 0.35;

        /// <summary>The reach an enemy's blows are taken to have, beyond his walk: a player has spells at range.</summary>
        private const int EnemyReach = 6;

        /// <summary>
        /// What the enemies' coming turn threatens the bot with, where the turn leaves it: a turn of
        /// each one's blows, sure from the one who can walk up to it, half from one who can shoot it,
        /// little from the rest -- the MP the bot keeps take it that much further, as it walks off
        /// with them (MonsterTactics.Reposition). Weighed by the life it has left: worth something
        /// whole, much more when low. It is what makes «get in with a telefrag, strike, and get out
        /// with Rebobinamiento» a turn worth playing.
        /// </summary>
        internal static double Danger(Simulation sim, Fighter bot)
        {
            double incoming = 0;
            foreach (var enemy in Foes(sim.Board, bot))
            {
                int distance = MapGeometry.Distance(bot.CellId, enemy.CellId) + Math.Max(0, bot.CurrentMP);
                int walk = sim.MovementNextTurn(enemy);
                double chance = distance <= walk + 1 ? 1.0 : distance <= walk + EnemyReach ? 0.5 : 0.15;
                incoming += TurnDamageOf(enemy) * chance;
            }
            double missing = bot.MaxHP <= 0 ? 0 : 1.0 - (double)bot.CurrentHP / bot.MaxHP;
            return DangerWeight * incoming * (0.5 + 1.5 * missing);
        }

        /// <summary>«Teletransporta o intercambia posiciones» (1101), the forced swap (1023), and the three backs:
        /// to the previous cell (1100), to the turn's start (1099), to the fight's start (784).</summary>
        private const int TeleportOrSwap = 1101;
        private const int ForcedSwap = 1023;
        private const int BackToPrevious = 1100;
        private const int BackToTurnStart = 1099;
        private const int BackToFightStart = 784;

        /// <summary>Indesplazable: nothing moves who carries it.</summary>
        private const int IndesplazableState = 97;

        /// <summary>The mirror leaps: across the target (1104), across the caster (1105), across the aimed cell (1106).</summary>
        private const int SymmetricToTarget = 1104;
        private const int SymmetricToCaster = 1105;
        private const int Symmetric = 1106;

        /// <summary>A spell's blow on one enemy, every damage row of it on him alone: an estimate to rank the coming turn by.</summary>
        private static double BlowOf(Board board, Spell spell, Fighter caster, Fighter enemy)
        {
            if (!spell.HasRows) return spell.Damage > 0 ? Blow(spell, caster, enemy) : 0;
            double total = 0;
            int bonus = BaseBonus(board, caster, spell.Id);
            foreach (var row in spell.Rows)
            {
                if (!IsDamage(row.EffectId)) continue;
                if (Timing(row) < 1 || !HitsEnemies(row)) continue;
                // A turret's blow at its evolution: «*E134», «*E135».
                if (!EffectEngine.CasterMeets(caster, row)) continue;
                total += Blow(Average(row) + bonus, ElementOf(row.EffectId, caster), caster, enemy);
            }
            return total;
        }

        /// <summary>The turn <see cref="Next"/> alone would play, each step on the copy of the fight.</summary>
        private static Node Greedy(Board board, Fighter monster, IReadOnlyList<Spell> spells, Func<Spell, bool>? allowed, int depth)
        {
            var steps = new List<Action>();
            double total = 0;
            int ap = monster.CurrentAP;
            for (int level = 0; level < depth + 2; level++)
            {
                Action? step;
                using (var sim = Simulation.Replay(board, monster, steps))
                {
                    var usable = allowed == null ? spells : spells.Where(allowed).ToList();
                    step = Next(sim.Board, monster, usable);
                }
                if (step == null) break;
                steps.Add(step);
                total += step.Value;
                ap = step.ActionPoints - step.Spell.Cost;
            }
            return new Node { Steps = steps, Total = total, ActionPoints = ap };
        }

        /// <summary>
        /// A planned step checked against the fight as it now stands, after the steps before it
        /// were really played: the way to its cell again, from where the bot really is, and its
        /// cast still possible and still worth something. Null when it is not -- the turn is then
        /// planned again.
        /// </summary>
        public static Action? Revalidate(Board board, Fighter monster, Action planned)
        {
            if (planned.Spell.Cost > monster.CurrentAP) return null;
            if (monster.Recarga.TryGetValue(planned.Spell.Id, out int wait) && wait > 0) return null;
            monster.LanzadosEsteTurno.TryGetValue(planned.Spell.Id, out int thisTurn);
            if (planned.Spell.PerTurn > 0 && thisTurn >= planned.Spell.PerTurn) return null;

            var routes = Routes(board, monster);
            if (!routes.TryGetValue(planned.From, out var route) || route.ActionPoints < planned.Spell.Cost) return null;

            var target = board.Fighters.FirstOrDefault(f => f.IsAlive && f != monster && f.CellId == planned.TargetCell) ?? monster;
            // Whoever it was aimed at has gone, or someone else stands there now.
            if (planned.Target != monster && planned.Target != target) return null;
            if (!CanCast(board, planned.Spell, monster, planned.From, planned.TargetCell, target)) return null;

            var enemies = Foes(board, monster);
            var allies = board.Fighters.Where(f => f.IsAlive && f.TeamId == monster.TeamId).ToList();
            double value = ValueOfCast(board, planned.Spell, monster, planned.From, planned.TargetCell, enemies, allies, target);
            // A step worth nothing on its own was planned for where it leaves things (a leap, a
            // way back, a state of its own): still castable, it still is what the plan counted on.
            if (value <= 0 && (planned.Value > 0 || !(planned.Spell.Moves || planned.Spell.Switches || planned.Spell.Tends
                                                      || planned.Spell.LaysPortals))) return null;
            return planned with
            {
                Path = route.Path, Target = target, Value = value,
                ActionPoints = route.ActionPoints, MovementPoints = route.MovementPoints,
            };
        }

        /// <summary>
        /// The steps worth trying from here: the best way of casting each spell, and of those the
        /// best per AP and the best whole -- a cheap blow and a big one both get their chance.
        /// </summary>
        /// <summary>The best way of casting each spell.</summary>
        private static List<Action> BestPerSpell(List<Action> options)
            => options.GroupBy(o => o.Spell.Id).Select(g => g.OrderByDescending(o => o.Value).First()).ToList();

        /// <summary>How many ways of casting a spell that moves somebody, or summons, a plan tries at each step.</summary>
        private const int MoverTries = 8;

        /// <summary>
        /// The steps a plan tries from here: the best way of casting each spell; and of a spell that
        /// moves somebody, every way, nearest first -- what it is worth is where it leaves them, and
        /// only playing it shows that: the leap behind the enemy, the telefrag, the way back. And
        /// of a summon, the few best cells: the Xelor's Cómplice is put down where a teleport
        /// will land the enemy on it, and that only shows a cast later.
        /// </summary>
        private static List<Action> Candidates(List<Action> options, Func<Action, double>? exitWorth = null)
        {
            bool Several(Spell spell) => spell.Moves || IsSummon(spell) || spell.Tends || spell.LaysPortals;
            var best = BestPerSpell(options.Where(o => o.Value > 0 || !o.Spell.Moves).ToList());
            var moving = options.Where(o => Several(o.Spell))
                                .GroupBy(o => o.Spell.Id)
                                .SelectMany(g => g.OrderByDescending(o => o.Value)
                                                  .ThenByDescending(o => exitWorth?.Invoke(o) ?? 0)
                                                  .ThenBy(o => o.Path.Count).Take(MoverTries));
            return best.Concat(moving).Distinct().ToList();
        }

        private static bool IsSummon(Spell spell) => spell.Summons || spell.SummonsByRow;

        /// <summary>
        /// A summon put down on a cell where one of the bot's own casts lands somebody: the
        /// Xelor's Cómplice where Perturbación will throw the enemy, and the two swap -- a telefrag.
        /// </summary>
        private static IEnumerable<Action> SummonsOn(List<Action> options, Dictionary<(int Cell, int From), double> landings)
            => landings.Count == 0
                ? Enumerable.Empty<Action>()
                : options.Where(o => IsSummon(o.Spell) && landings.ContainsKey((o.TargetCell, o.From)))
                         .GroupBy(o => (o.Spell.Id, o.TargetCell, o.From)).Select(g => g.First())
                         .GroupBy(o => o.Spell.Id)
                         .SelectMany(g => g.OrderByDescending(o => landings[(o.TargetCell, o.From)]).ThenByDescending(o => o.Value).Take(MoverTries))
                         .OrderByDescending(o => landings[(o.TargetCell, o.From)]).ThenByDescending(o => o.Value);

        private static List<Action> Pick(List<Action> perSpell, int branch, Func<Action, double>? worth = null)
        {
            if (perSpell.Count == 0) return perSpell;
            worth ??= a => a.Value;
            var byRate = perSpell.OrderByDescending(o => worth(o) / Math.Max(1, o.Spell.Cost)).Take(branch);
            var byWhole = perSpell.OrderByDescending(worth).Take(Math.Max(2, branch / 2));
            // And the best that prepares instead of striking: a summon, a buff, a state laid. Alone it
            // is worth little per AP; it is the blows after it that it is for, and only trying it
            // first shows them -- the strategist put down next to the enemy, then struck.
            var setups = perSpell.Where(o => Prepares(o.Spell)).OrderByDescending(worth).Take(2);
            return byRate.Concat(byWhole).Concat(setups).Distinct().ToList();
        }

        /// <summary>
        /// Whether a spell sets something up rather than strikes: it summons, gives its side a buff,
        /// lays a state, or moves somebody -- the leap that brings the caster in reach, the pull that
        /// brings the enemy.
        /// </summary>
        private static bool Prepares(Spell spell)
            => spell.Summons || spell.SummonsByRow || spell.Buff > 0
               || spell.Rows.Any(r => r.EffectId == EffectSupport.AddState || IsSelfBuff(r) || Repositions(r.EffectId));

        private static bool Repositions(int effect)
            => effect is EffectSupport.Teleport or EffectSupport.SwapPositions or EffectSupport.Pull
                      or EffectSupport.StepForward or EffectSupport.StepBack
                      or SymmetricToTarget or SymmetricToCaster or Symmetric;

        private static bool IsSelfBuff(SpellEffect row)
        {
            var (characteristic, sign) = DatabaseManager.EffectMeta(row.EffectId);
            string mask = row.TargetMask ?? "";
            return characteristic > 0 && sign > 0 && (mask.Contains('C') || mask.Contains('a'));
        }

        // ─── The copy of the fight a plan is played on ─────────────────────────────────────

        /// <summary>
        /// The fight with a plan's steps played on it, roughly: what each cast changes that the
        /// weighing reads, put on the fighters themselves and taken off again on disposal. The
        /// summons it puts down are fighters of its own, on a board of its own.
        /// </summary>
        internal sealed class Simulation : IDisposable
        {
            private readonly Stack<System.Action> _undo = new();
            private readonly List<Fighter> _summoned = new();
            private readonly Board _real;
            private readonly Fighter _monster;
            private static long _nextId = -900_000_000_000;

            public Board Board { get; private set; }

            private Simulation(Board real, Fighter monster)
            {
                _real = real;
                _monster = monster;
                Board = real;
            }

            /// <summary>The fight with these steps played on it.</summary>
            public static Simulation Replay(Board board, Fighter monster, IEnumerable<Action> steps)
            {
                var sim = new Simulation(board, monster);
                try
                {
                    foreach (var step in steps) sim.Play(step);
                }
                catch
                {
                    sim.Dispose();
                    throw;
                }
                return sim;
            }

            public void Dispose()
            {
                while (_undo.Count > 0) _undo.Pop()();
            }

            /// <summary>Where the copy stands now, to come back to with <see cref="Rewind"/>.</summary>
            public int Mark() => _undo.Count;

            /// <summary>Takes back every change since the mark.</summary>
            public void Rewind(int mark)
            {
                while (_undo.Count > mark) _undo.Pop()();
            }

            private void Set(Fighter who, Func<Fighter, int> get, Action<Fighter, int> set, int value)
            {
                int was = get(who);
                if (was == value) return;
                set(who, value);
                _undo.Push(() => set(who, was));
            }

            private void Count<TKey>(Dictionary<TKey, int> counts, TKey key) where TKey : notnull
            {
                bool had = counts.TryGetValue(key, out int was);
                counts[key] = was + 1;
                _undo.Push(() => { if (had) counts[key] = was; else counts.Remove(key); });
            }

            private void AddState(Fighter who, int state, int depth = 0)
            {
                if (state == 0 || who.Buffs.TieneEstado(state)) return;
                who.Buffs.PonerEstado(state);
                _undo.Push(() => who.Buffs.QuitarEstado(state));
                Fire(who, EffectEngine.AlPonerseElEstado(state), depth);
                Fire(who, EffectEngine.AlActivarseElEstado(state), depth);
            }

            private void RemoveState(Fighter who, int state, int depth = 0)
            {
                if (state == 0 || !who.Buffs.TieneEstado(state)) return;
                who.Buffs.QuitarEstado(state);
                _undo.Push(() => who.Buffs.PonerEstado(state));
                Fire(who, EffectEngine.AlQuitarseElEstado(state), depth);
            }

            /// <summary>One step played: the walk, the cast's price, and what its rows do.</summary>
            public void Play(Action step)
            {
                var bot = _monster;
                // The walk, remembered as the engine's is (FightTackle: MoverA on its last cell);
                // through a portal, the two it went through go off for the turn.
                if (step.From != bot.CellId) Moved(bot, step.From);
                for (int i = 1; i < step.Path.Count; i++)
                    if (MapGeometry.Distance(step.Path[i - 1], step.Path[i]) > 1) UsePortals(step.Path[i - 1], step.Path[i]);
                Set(bot, f => f.CurrentMP, (f, v) => f.CurrentMP = v, Math.Max(0, step.MovementPoints));
                Set(bot, f => f.CurrentAP, (f, v) => f.CurrentAP = v, Math.Max(0, step.ActionPoints - step.Spell.Cost));
                Count(bot.LanzadosEsteTurno, step.Spell.Id);
                if (step.Spell.Interval > 0)
                {
                    bool had = bot.Recarga.TryGetValue(step.Spell.Id, out int was);
                    bot.Recarga[step.Spell.Id] = step.Spell.Interval;
                    _undo.Push(() => { if (had) bot.Recarga[step.Spell.Id] = was; else bot.Recarga.Remove(step.Spell.Id); });
                }
                var target = Board.Fighters.FirstOrDefault(f => f.IsAlive && f.CellId == step.TargetCell && f != bot);
                if (target != null) Count(bot.LanzadosPorObjetivo, (step.Spell.Id, target.Id));

                var rows = step.Spell.HasRows ? step.Spell.Rows : Array.Empty<SpellEffect>();
                // The spell's own telefrags, as the engine keeps them: empty at its start, read by
                // the «T» of its rows, and the ones from before put back after it.
                var telefrags = _real.Telefrags?.Invoke();
                var before = telefrags?.ToList();
                telefrags?.Clear();
                _spellTelefrags = 0;
                var wallsBefore = WallCells();
                var stood = Board.Fighters.Concat(_summoned).Where(f => f.IsAlive).Distinct().ToDictionary(f => f, f => f.CellId);
                // Aimed at a portal, it goes through the network: cast from the way out, landing
                // beyond it, grown by the network's bonus, the masks' «R» told so.
                var through = step.Spell.HasRows ? ThroughPortals(Board, step.Spell, bot, step.From, step.TargetCell) : null;
                if (through != null)
                {
                    _boost = (100 + through.Value.Bonus) / 100.0;
                    Board.ThroughPortal?.Invoke(true);
                }
                try
                {
                    Rows(step.Spell.Id, rows, bot, through?.Exit ?? step.From, through?.Landing ?? step.TargetCell, 0);
                    WallsCatch(wallsBefore, stood);
                }
                finally
                {
                    if (through != null)
                    {
                        _boost = 1;
                        Board.ThroughPortal?.Invoke(false);
                    }
                    if (telefrags != null)
                    {
                        telefrags.Clear();
                        foreach (var (k, v) in before!) telefrags[k] = v;
                    }
                }
                if (!step.Spell.HasRows) Summary(step);
            }

            private int _spellTelefrags;

            /// <summary>What the cast under way's damage and healing are multiplied by: the portals' bonus.</summary>
            private double _boost = 1;

            /// <summary>
            /// The damage the copy's hooks have done so far -- Aguja's, when the enemy loses his
            /// Telefrag --: no weighing of a spell's rows sees it, so the plan adds it itself.
            /// </summary>
            public double HookDamage { get; private set; }

            private void Moved(Fighter who, int cell, bool remember = true)
            {
                int was = who.CellId;
                int[] before = who.CellHistory();
                if (remember) who.MoverA(cell);
                else who.VolverALaAnterior(cell);
                _undo.Push(() => { who.CellId = was; who.RestoreCellHistory(before); });
                RefreshPortals();
            }

            /// <summary>
            /// Somebody moved: a portal he left comes back on, one he stands on goes off -- as the
            /// engine refreshes the network after a walk, a push, a teleport, a swap, a throw
            /// (FightPortals.RefreshPortalsAsync). Estela's portal, under the caster until he jumps.
            /// </summary>
            private void RefreshPortals()
            {
                var current = Board.Portals?.Invoke();
                if (current == null || current.Count == 0) return;
                if (current.All.All(p => current.ShouldBeActive(p, OccupantOf) == p.Active)) return;
                var network = OwnNetwork()!;
                var was = network.All.Select(p => (Portal: p, p.Active)).ToList();
                _undo.Push(() => { foreach (var (portal, active) in was) portal.Active = active; });
                network.Refresh(OccupantOf);
            }

            /// <summary>Who stands on a cell, for the portals: nobody carried counts.</summary>
            private long OccupantOf(int at)
                => Board.Fighters.Concat(_summoned).FirstOrDefault(f => f.IsAlive && !f.EstaCargado && f.CellId == at)?.Id ?? 0;

            /// <summary>Whether nothing moves a fighter: Indesplazable, or a state that says so -- as the engine reads it.</summary>
            private static bool Pinned(Fighter who)
            {
                if (who.Buffs.TieneEstado(IndesplazableState)) return true;
                foreach (int state in who.Buffs.Estados)
                {
                    var flags = SpellStates.Of(state);
                    if (flags != null && (flags.CantBeMoved || flags.CantSwitchPosition)) return true;
                }
                return false;
            }

            /// <summary>
            /// A teleport to a cell, as the engine's own (EffectEngine.TeletransportarConTelefrag): a
            /// free cell he lands on; a taken one swaps the two -- a TELEFRAG, one a spell, noted for
            /// the «T» of its rows --; nobody pinned goes anywhere. Undoing the last move (1100) does
            /// not count as a move of its own.
            /// </summary>
            private void Teleport(Fighter who, int cell, bool undoing = false)
            {
                if (!who.IsAlive || cell < 0 || cell == who.CellId || Pinned(who)) return;
                if (!MapGeometry.IsValid(cell) || !_real.Walkable(cell)) return;
                var there = Board.Fighters.FirstOrDefault(f => f.IsAlive && f != who && f.CellId == cell);
                int from = who.CellId;
                if (there == null)
                {
                    Moved(who, cell, remember: !undoing);
                    return;
                }
                if (_spellTelefrags > 0 || Pinned(there)) return;
                Moved(who, cell, remember: !undoing);
                Moved(there, from);
                _spellTelefrags++;
                var telefrags = _real.Telefrags?.Invoke();
                if (telefrags != null)
                {
                    telefrags[who.Id] = there.Id;
                    telefrags[there.Id] = who.Id;
                }
            }

            /// <summary>A hook armed on the copy: rows waiting for a state to come or go on someone.</summary>
            private sealed record Hook(Fighter On, Fighter Caster, string Trigger, SpellEffect Row);

            private readonly List<Hook> _hooks = new();

            private void Arm(Fighter on, Fighter caster, string trigger, SpellEffect row)
            {
                var hook = new Hook(on, caster, trigger, row);
                _hooks.Add(hook);
                _undo.Push(() => _hooks.Remove(hook));
            }

            /// <summary>
            /// A state come or gone on someone: the hooks waiting for it go off, the copy's own and
            /// the ones the fighter already carries from the fight -- Aguja's damage when the enemy
            /// loses his Telefrag.
            /// </summary>
            private void Fire(Fighter who, string trigger, int depth)
            {
                if (depth >= SubCastDepth + 1) return;
                foreach (var hook in _hooks.Where(h => h.On == who && h.Trigger == trigger).ToList())
                    Child(hook.Row, hook.Caster, who, depth + 1);
                foreach (var hooked in who.Buffs.ActiveSpells.ToList())
                {
                    foreach (var row in SpellEffects.De(hooked.Hechizo, hooked.Grado))
                    {
                        if (!row.Disparadores().Contains(trigger, StringComparer.OrdinalIgnoreCase)) continue;
                        var caster = Board.Fighters.FirstOrDefault(f => f.Id == hooked.Lanzador) ?? who;
                        Child(row, caster, who, depth + 1);
                    }
                }
                // And his attitudes, the class passive among them: the Pandawa's Jarrita gives
                // him power while he carries somebody (EON3).
                foreach (int attitude in who.Buffs.Actitudes.ToList())
                    foreach (var row in SpellEffects.De(attitude, who.Buffs.GradoDeActitud(attitude)))
                        if (!row.ForClientOnly && row.Disparadores().Contains(trigger, StringComparer.OrdinalIgnoreCase))
                            Child(row, who, who, depth + 1);
                // And a creature's behaviour spell: the Steamer's turret gives him its damage at the
                // start of its turn (TB), evolved.
                if (who.Conducta.Spell != 0 && !who.Buffs.Actitudes.Contains(who.Conducta.Spell))
                    foreach (var row in SpellEffects.De(who.Conducta.Spell, Math.Max(1, who.Conducta.Grade)))
                        if (!row.ForClientOnly && row.Disparadores().Contains(trigger, StringComparer.OrdinalIgnoreCase))
                            Child(row, who, who, depth + 1);
            }

            /// <summary>
            /// The bot's creatures begin their turns: what waits on it (TB) goes off on them -- the
            /// Steamer's evolved turret gives him its damage. Taken back with <see cref="Rewind"/>.
            /// </summary>
            public void SummonsBeginTheirTurns(Fighter bot)
            {
                foreach (var summon in Board.Fighters.Where(f => f.IsAlive && f.Invocador == bot.Id).ToList())
                    Fire(summon, EffectEngine.AlEmpezarElTurno, 0);
            }

            /// <summary>A hooked row gone off: its sub-cast at whom it waited on, or itself.</summary>
            private void Child(SpellEffect row, Fighter caster, Fighter on, int depth)
            {
                double before = Foes(Board, _monster).Sum(e => e.CurrentHP);
                if (EffectEngine.EsDeLaFamiliaDeSublanzar(row.EffectId) && row.DiceNum > 0)
                    Rows(row.DiceNum, SpellEffects.De(row.DiceNum, Math.Max(1, row.DiceSide)), caster, on.CellId, on.CellId, depth);
                else
                    Rows(0, new[] { row }, caster, on.CellId, on.CellId, depth, immediate: true);
                double after = Foes(Board, _monster).Sum(e => e.CurrentHP);
                double was = HookDamage;
                HookDamage += Math.Max(0, before - after);
                _undo.Push(() => HookDamage = was);
            }

            /// <summary>
            /// The bot's turn over: what waits on its end (TE) goes off on it -- Rebobinamiento cast
            /// on itself sends it back to where the turn began. Taken back with <see cref="Rewind"/>.
            /// </summary>
            public void EndTurn() => Fire(_monster, EffectEngine.AlAcabarElTurno, 0);

            /// <summary>The odds a hook waiting for an enemy to lose a state he holds goes off: it wears off, if nothing takes it first.</summary>
            private const double PendingSure = 0.8;

            /// <summary>The odds of one waiting for a state he has yet to get or lose: the Xelor has to telefrag him first.</summary>
            private const double PendingLikely = 0.35;

            /// <summary>
            /// What the hooks this plan left on the enemies would deal when the state they wait for
            /// comes or goes, by the odds it does: Aguja's blow when the enemy loses his Telefrag --
            /// sure to come when he holds it, likely when the Xelor has yet to give it him. Each
            /// hook played on the copy and taken back.
            /// </summary>
            public double Pending(Fighter bot)
            {
                var odds = new Dictionary<(Fighter On, SpellEffect Row), (Hook Hook, double Odds)>();
                foreach (var hook in _hooks)
                {
                    if (!hook.On.IsAlive || hook.On.TeamId == bot.TeamId) continue;
                    bool goes = hook.Trigger.StartsWith("EOFF", StringComparison.OrdinalIgnoreCase);
                    bool comes = !goes && hook.Trigger.StartsWith("EON", StringComparison.OrdinalIgnoreCase);
                    if (!goes && !comes) continue;
                    if (!int.TryParse(hook.Trigger.Substring(goes ? 4 : 3), out int state)) continue;
                    bool holds = hook.On.Buffs.TieneEstado(state);
                    double chance = goes ? (holds ? PendingSure : PendingLikely) : (holds ? 0 : PendingLikely);
                    var key = (hook.On, hook.Row);
                    if (chance > 0 && (!odds.TryGetValue(key, out var known) || known.Odds < chance)) odds[key] = (hook, chance);
                }
                double total = 0;
                foreach (var (hook, chance) in odds.Values)
                {
                    int mark = Mark();
                    double before = HookDamage;
                    Child(hook.Row, hook.Caster, hook.On, 1);
                    total += chance * (HookDamage - before);
                    Rewind(mark);
                }
                return total;
            }

            /// <summary>A monster's spell, by its summary: its blow on whom it hits, and its summon.</summary>
            private void Summary(Action step)
            {
                var spell = step.Spell;
                if (spell.Damage > 0)
                {
                    foreach (var enemy in Foes(Board, _monster).Where(e => Hit(spell, step.TargetCell, e)))
                        Hurt(enemy, Blow(spell, _monster, enemy));
                }
            }

            private void Hurt(Fighter who, double blow)
            {
                if (blow <= 0 || !who.IsAlive) return;
                Set(who, f => f.CurrentHP, (f, v) => f.CurrentHP = v, Math.Max(0, who.CurrentHP - (int)Math.Round(blow)));
            }

            private void Heal(Fighter who, double amount)
            {
                if (amount <= 0 || !who.IsAlive) return;
                Set(who, f => f.CurrentHP, (f, v) => f.CurrentHP = v, Math.Min(who.MaxHP, who.CurrentHP + (int)Math.Round(amount)));
            }

            /// <summary>
            /// Where everybody stood, and what states he carried, as the cast under way began: its
            /// rows reach by those, as the engine's do (EffectEngine.Resolver, «celdasAlEmpezar»,
            /// «estadosAlEmpezar»). Golpe de Xelor sends the enemy across the caster and its blow
            /// still lands on him; a telefrag's sub-spell puts its once-a-turn state on the Xelor
            /// and its +2 AP, on «*e» of that state, still comes -- once, and not on the next cast.
            /// </summary>
            private Dictionary<Fighter, (int Cell, HashSet<int> States, long Carrying)>? _asItBegan;

            /// <summary>Whom a row reaches: the fight's own reading, and the summons of this copy by the plain one.</summary>
            private IReadOnlyList<Fighter> Reached(SpellEffect row, Fighter caster, int from, int aim)
            {
                // Everybody is read as the cast began -- the cell he stood on, the states he
                // carried, whom he carried: the «K» of a throw's blows is the one thrown -- and put
                // back as he is.
                List<(Fighter Who, int Now)>? moved = null;
                List<(Fighter Who, int State, bool Had)>? swapped = null;
                long carryingNow = caster.Carrying;
                if (_asItBegan != null && _asItBegan.TryGetValue(caster, out var casterBegan)) caster.Carrying = casterBegan.Carrying;
                if (_asItBegan != null)
                    foreach (var (who, (cell, states, _)) in _asItBegan)
                    {
                        if (who != caster && who.CellId != cell)
                        {
                            (moved ??= new List<(Fighter, int)>()).Add((who, who.CellId));
                            who.CellId = cell;
                        }
                        foreach (int state in who.Buffs.Estados.Where(s => !states.Contains(s)).ToList())
                        {
                            (swapped ??= new List<(Fighter, int, bool)>()).Add((who, state, true));
                            who.Buffs.QuitarEstado(state);
                        }
                        foreach (int state in states.Where(s => !who.Buffs.TieneEstado(s)).ToList())
                        {
                            (swapped ??= new List<(Fighter, int, bool)>()).Add((who, state, false));
                            who.Buffs.PonerEstado(state);
                        }
                    }
                try
                {
                    var reached = ReachOf(_real, row, caster, from, aim).ToList();
                    if (_summoned.Count == 0) return reached;
                    // This copy's own creatures, which the fight does not know of, by the plain
                    // reading of the zone, and the letters of the mask that one leaves out: the
                    // caster's conditions, the sides and states of the creature, the telefragged.
                    if (!EffectEngine.CasterMeets(caster, row)) return reached;
                    bool telefragged = (row.TargetMask ?? "").Split(',').Any(p => p.Trim() == "T");
                    var telefrags = _real.Telefrags?.Invoke();
                    foreach (var summon in ReachOf(new Board { Fighters = _summoned }, row, caster, from, aim))
                    {
                        if (!_summoned.Contains(summon) || reached.Contains(summon)) continue;
                        if (!EffectEngine.CumpleLaMascara(caster, summon, row.TargetMask ?? "")) continue;
                        if (telefragged && (telefrags == null || !telefrags.ContainsKey(summon.Id))) continue;
                        reached.Add(summon);
                    }
                    return reached;
                }
                finally
                {
                    caster.Carrying = carryingNow;
                    if (moved != null) foreach (var (who, now) in moved) who.CellId = now;
                    if (swapped != null)
                        foreach (var (who, state, had) in swapped)
                            if (had) who.Buffs.PonerEstado(state); else who.Buffs.QuitarEstado(state);
                }
            }

            /// <summary>What a cast's rows do, as far as the weighing reads them.</summary>
            private void Rows(int spellId, IReadOnlyList<SpellEffect> rows, Fighter caster, int from, int aim, int depth,
                              bool immediate = false)
            {
                var outer = _asItBegan;
                _asItBegan = Board.Fighters.Concat(_summoned).Where(f => f.IsAlive).Distinct()
                                  .ToDictionary(f => f, f => (f.CellId, new HashSet<int>(f.Buffs.Estados), f.Carrying));
                try
                {
                    RowsAsCast(spellId, rows, caster, from, aim, depth, immediate);
                }
                finally
                {
                    _asItBegan = outer;
                }
            }

            private void RowsAsCast(int spellId, IReadOnlyList<SpellEffect> rows, Fighter caster, int from, int aim, int depth,
                                    bool immediate)
            {
                bool summoned = false;
                foreach (var row in rows)
                {
                    if (EffectEngine.EsMarcadorDeGuion(row.EffectId) || row.ForClientOnly) continue;
                    // A row that waits a turn or more goes off after this turn: Borroso Temporal's AP back.
                    if (row.Delay > 0 && !immediate) continue;
                    if (!immediate && Timing(row) < 1)
                    {
                        // A row waiting for a state to come or go, or for the end of the turn of whom
                        // it reaches: armed on him.
                        var waits = row.Disparadores().Where(t => t.StartsWith("EON", StringComparison.OrdinalIgnoreCase)
                                                               || t.StartsWith("EOFF", StringComparison.OrdinalIgnoreCase)
                                                               || string.Equals(t, EffectEngine.AlAcabarElTurno, StringComparison.OrdinalIgnoreCase))
                                                    .ToList();
                        if (waits.Count > 0)
                            foreach (var who in Reached(row, caster, from, aim))
                                foreach (var trigger in waits) Arm(who, caster, trigger, row);
                        continue;
                    }
                    if (row.Probabilidad > 0 && row.Probabilidad < 50) continue;
                    int id = row.EffectId;

                    if (id is KillsAndReplaces or KillsAndReplacesBySummon)
                    {
                        // A creature of its own side turned into another: the tree goes, the doll
                        // comes out on its cell (EffectEngine, MataYReemplaza).
                        if (summoned || row.DiceNum <= 0) continue;
                        var replaced = Reached(row, caster, from, aim).FirstOrDefault(w => w.TeamId == caster.TeamId && w.EsInvocado);
                        if (replaced == null) continue;
                        int where = replaced.CellId;
                        Set(replaced, f => f.CurrentHP, (f, v) => f.CurrentHP = v, 0);
                        summoned = Summon(caster, row, where, depth);
                        continue;
                    }

                    if (EffectEngine.EsInvocacion(id))
                    {
                        if (summoned || aim == from || !_real.Walkable(aim) || Board.Occupied(aim)) continue;
                        summoned = Summon(caster, row, aim, depth);
                        continue;
                    }

                    if (id == EffectEngine.ColocaUnPortal)
                    {
                        LayPortal(caster, row, spellId, aim);
                        continue;
                    }

                    if (id == EffectSupport.Kill)
                    {
                        // Killed outright: the turret a new one of its kind replaces, the bomb its
                        // own explosion takes.
                        foreach (var who in Reached(row, caster, from, aim).Where(w => w.IsAlive).ToList())
                            Set(who, f => f.CurrentHP, (f, v) => f.CurrentHP = v, 0);
                        continue;
                    }

                    if (id == EffectSupport.Carry)
                    {
                        // Picked up (EffectEngine, Carry): onto the carrier's cell, Carrying and
                        // Carried states on the two -- one at a time, nobody carried or carrying.
                        if (caster.Carrying != 0) continue;
                        var lifted = Reached(row, caster, from, aim)
                            .FirstOrDefault(w => w != caster && !w.EstaCargado && w.Carrying == 0);
                        if (lifted == null) continue;
                        Carry(caster, lifted);
                        continue;
                    }
                    if (id == EffectSupport.Throw)
                    {
                        // Thrown (EffectEngine, Throw): what the caster carried as the spell began,
                        // onto the aimed cell if it is free.
                        if (!CasterMeetsAsItBegan(caster, row) || caster.Carrying == 0) continue;
                        var carried = Board.Fighters.Concat(_summoned).FirstOrDefault(f => f.Id == caster.Carrying);
                        if (carried == null || aim < 0 || !_real.Walkable(aim) || Board.Occupied(aim)) continue;
                        Drop(caster, carried, aim);
                        continue;
                    }

                    if (id == EffectSupport.Teleport)
                    {
                        // «Teletransporta a la casilla objetivo» moves THE CASTER, to the aimed cell
                        // if it is free, or to the cell its ';' zone names (EffectEngine, Teletransportar):
                        // the Iop's Salto, the Ocra's Paso de Cacería, the Selatrop's Estela. It went
                        // looking for somebody on the empty cell and moved nobody.
                        int to = row.Forma == ';' && row.CeldasFijas.Count > 0 ? row.CeldasFijas[0] : aim;
                        if (to >= 0 && CasterMeetsAsItBegan(caster, row) && !Board.Occupied(to) && _real.Walkable(to))
                            Moved(caster, to);
                        continue;
                    }
                    if (id == Teleportal)
                    {
                        // «Teleportal»: whoever stands on a portal goes through the network (Exilio).
                        foreach (var who in Reached(row, caster, from, aim).ToList()) CrossPortal(who, walkedIn: false);
                        continue;
                    }
                    if (id is EffectSupport.SwapPositions or ForcedSwap)
                    {
                        // The target and the caster swap cells -- no telefrag: Astrolabio, Palabra de Vuelo.
                        foreach (var who in Reached(row, caster, from, aim).Where(w => w != caster).Take(1).ToList())
                        {
                            int mine = caster.CellId, his = who.CellId;
                            Moved(who, mine);
                            Moved(caster, his);
                        }
                        continue;
                    }
                    if (id == TeleportOrSwap)
                    {
                        // The caster to the aimed cell, swapping with whoever is there: Premonición.
                        Teleport(caster, aim);
                        continue;
                    }
                    if (id is BackToPrevious or BackToTurnStart or BackToFightStart)
                    {
                        // Back to a cell he stood on: before his last move, when his turn began, when
                        // the fight began -- Rebobinamiento, Recuerdo, Congelación.
                        foreach (var who in Reached(row, caster, from, aim).ToList())
                        {
                            int back = id == BackToTurnStart ? who.CasillaAlEmpezarTurno
                                     : id == BackToFightStart ? who.CasillaAlEmpezarCombate : who.CasillaAnterior;
                            Teleport(who, back, undoing: id == BackToPrevious);
                        }
                        continue;
                    }

                    var reached = Reached(row, caster, from, aim);
                    if (reached.Count == 0) continue;

                    if (id is EffectSupport.Push or EffectSupport.PushWithoutDamage or EffectSupport.Pull)
                    {
                        // Moved straight away from the caster, or toward him: what is planned after
                        // reads where they are -- the enemy pulled into reach.
                        bool away = id != EffectSupport.Pull;
                        foreach (var who in reached.Where(w => w != caster && !SpellStates.PinsInPlace(w)).ToList())
                        {
                            var (cell, short_) = Slide(who, from, Math.Max(1, row.DiceNum), away);
                            if (cell != who.CellId) Moved(who, cell);
                            if (id == EffectSupport.Push && short_ > 0 && who.TeamId != _monster.TeamId)
                                Hurt(who, EffectEngine.DanoDeColision(caster.Level, caster.PushDamage, 0, short_));
                        }
                        continue;
                    }

                    if (id is EffectSupport.StepForward or EffectSupport.StepBack)
                    {
                        // The caster walks toward where it aimed, or away from it, without its MP.
                        var (cell, _) = Slide(caster, aim, Math.Max(1, row.DiceNum), away: id == EffectSupport.StepBack);
                        if (cell != caster.CellId) Moved(caster, cell);
                        continue;
                    }

                    if (id is SymmetricToTarget or SymmetricToCaster or Symmetric)
                    {
                        // A leap to the mirror cell, as the engine resolves it: the caster across
                        // whom the row reaches (1104), whom it reaches across the caster (1105) or
                        // across the aimed cell (1106); onto somebody, a telefrag.
                        foreach (var who in reached.ToList())
                        {
                            var mover = id == SymmetricToTarget ? caster : who;
                            if (id != SymmetricToTarget && who == caster) continue;
                            int pivot = id switch
                            {
                                SymmetricToCaster => caster.CellId,
                                SymmetricToTarget => who != caster ? who.CellId : aim,
                                _ => aim,
                            };
                            Teleport(mover, MapGeometry.Reflejar(mover.CellId, pivot));
                            if (id == SymmetricToTarget) break;
                        }
                        continue;
                    }

                    if (IsDamage(id))
                    {
                        double dice = Average(row) + BaseBonus(_real, caster, spellId);
                        var element = ElementOf(id, caster);
                        foreach (var who in reached)
                        {
                            double blow = Shielded(caster, from, who) ? 0 : Blow(dice, element, caster, who) * _boost;
                            if (who.TeamId != _monster.TeamId) Hurt(who, blow);
                            else
                            {
                                var echo = EchoOf(who);
                                Hurt(who, blow * echo.AllyDamageTaken);
                                if (echo.Share > 0)
                                    foreach (var enemy in Foes(Board, _monster).Where(e => MapGeometry.Distance(e.CellId, who.CellId) <= echo.Radius))
                                        Hurt(enemy, blow * echo.AllyDamageTaken * echo.Share);
                            }
                        }
                        continue;
                    }

                    if (id == EffectSupport.FireHeal || id == EffectSupport.HealPercent)
                    {
                        foreach (var who in reached)
                            Heal(who, _boost * (id == EffectSupport.HealPercent ? who.MaxHP * Average(row) / 100.0
                                                                              : Average(row) * (100 + Math.Max(0, caster.Intelligence)) / 100.0));
                        continue;
                    }

                    if (id == EffectSupport.AddState)
                    {
                        foreach (var who in reached) AddState(who, row.Value, depth);
                        continue;
                    }
                    if (id == EffectSupport.RemoveState)
                    {
                        foreach (var who in reached) RemoveState(who, row.Value, depth);
                        continue;
                    }

                    if (EffectEngine.EsDeLaFamiliaDeSublanzar(id))
                    {
                        if (depth >= SubCastDepth || row.DiceNum <= 0) continue;
                        var child = SpellEffects.De(row.DiceNum, Math.Max(1, row.DiceSide));
                        foreach (var (childCaster, childFrom, childAim) in SubCasts(row, caster, from, aim, reached))
                            Rows(row.DiceNum, child, childCaster, childFrom, childAim, depth + 1);
                        continue;
                    }

                    if (id == EffectEngine.ActivarBomba)
                    {
                        // Each bomb reached casts its own explosion (EffectEngine, ActivarBomba).
                        if (depth >= SubCastDepth) continue;
                        foreach (var bomb in BombChain(Board.Fighters.Concat(_summoned).Distinct(), reached))
                        {
                            int explosion = Bombs.Explosion(bomb.MonsterId);
                            if (explosion == 0 || !bomb.IsAlive) continue;
                            Rows(explosion, SpellEffects.De(explosion, Math.Max(1, bomb.GradeIndex)), bomb, bomb.CellId, bomb.CellId, depth + 1);
                        }
                        continue;
                    }

                    if (id is EffectSupport.VitalityPercentBonus or EffectSupport.VitalityPercentBoost)
                    {
                        foreach (var who in reached) Stat(who, 11, (int)Math.Round(who.MaxHP * Average(row) / 100.0));
                        continue;
                    }

                    var (characteristic, sign) = DatabaseManager.EffectMeta(id);
                    if (characteristic <= 0 || sign == 0) continue;
                    int amount = (int)Math.Round(Average(row)) * sign;
                    foreach (var who in reached)
                    {
                        Stat(who, characteristic, amount);
                        // MP taken for a turn or more: the enemy starts his own turn without them.
                        if (characteristic == 23 && amount < 0 && row.Duration >= 1 && who.TeamId != _monster.TeamId)
                            Lock(who, -amount);
                    }
                }
            }

            // ─── The bot's bomb walls ───────────────────────────────────────────────────

            /// <summary>The grade a wall hits with (FightHandler, MuroGrado).</summary>
            private const int WallGrade = 3;

            /// <summary>
            /// The cells of the bot's bomb walls as its bombs stand now, and the spell each throws:
            /// a wall is a function of where the bombs are (BombWalls.Of), on this copy as in the fight.
            /// </summary>
            private Dictionary<int, int> WallCells()
            {
                var cells = new Dictionary<int, int>();
                if (!_summoned.Concat(Board.Fighters).Any(f => f.IsAlive && f.Invocador == _monster.Id && Bombs.Is(f.MonsterId)))
                    return cells;
                foreach (var wall in BombWalls.Of(Board.Fighters.Concat(_summoned).Distinct().ToList(), _monster))
                    if (BombWalls.WallSpell.TryGetValue(wall.Template, out int spell))
                        foreach (int cell in wall.Cells) cells[cell] = spell;
                return cells;
            }

            /// <summary>Whether a wall of the bot's catches this one (BombWalls.Catches): never its own bombs nor one under Kabum.</summary>
            private bool WallCatches(Fighter who)
                => who.IsAlive && !who.Buffs.TieneEstado(BombWalls.KabumState)
                   && !(who.EsInvocado && who.Invocador == _monster.Id && Bombs.Is(who.MonsterId));

            /// <summary>
            /// Who a wall has caught by a displacement this turn: once a turn, as the fight has it
            /// (FightInstance.WallHitThisTurn) -- sent into it again, he goes through and takes nothing.
            /// </summary>
            private readonly HashSet<Fighter> _wallHitByDisplacement = new();

            /// <summary>The cells that stop a push of this one dead: the bot's walls, if they catch him.</summary>
            private IReadOnlyCollection<int>? WallStops(Fighter who)
            {
                if (!WallCatches(who) || _wallHitByDisplacement.Contains(who)) return null;
                var walls = WallCells();
                return walls.Count == 0 ? null : walls.Keys.ToHashSet();
            }

            /// <summary>
            /// After a step, whoever the bot's walls now cover and did not before -- a wall raised
            /// under his feet, or a push into one -- takes the wall's blow, as the fight deals it
            /// (FightHandler, ReconciliarLosMurosAsync). It counts as what the step's hooks dealt.
            /// </summary>
            private void WallsCatch(Dictionary<int, int> before, Dictionary<Fighter, int> stood)
            {
                var walls = WallCells();
                if (walls.Count == 0) return;
                foreach (var who in Board.Fighters.Concat(_summoned).Where(f => f.IsAlive).Distinct().ToList())
                {
                    if (!walls.TryGetValue(who.CellId, out int spell) || !WallCatches(who)) continue;
                    bool moved = !stood.TryGetValue(who, out int was) || was != who.CellId;
                    if (!moved && before.ContainsKey(who.CellId)) continue;
                    if (moved)
                    {
                        if (!_wallHitByDisplacement.Add(who)) continue;
                        _undo.Push(() => _wallHitByDisplacement.Remove(who));
                    }
                    WallHits(who, spell);
                }
            }

            /// <summary>
            /// A wall's blow on one standing in it, cast in the bot's name at the wall's grade, and
            /// grown by half the combo of the bombs holding it up -- «los muros se benefician de la
            /// mitad del combo», the highest of them (FightHandler, THE COMBO).
            /// </summary>
            private void WallHits(Fighter who, int spell)
            {
                double before = Foes(Board, _monster).Sum(e => e.CurrentHP);
                int life = who.CurrentHP;
                Rows(spell, SpellEffects.De(spell, WallGrade), _monster, _monster.CellId, who.CellId, 1, immediate: true);
                var wall = BombWalls.Covering(Board.Fighters.Concat(_summoned).Distinct().ToList(), _monster, who.CellId);
                int combo = wall == null ? 0 : wall.Bombs.Max(Combo.PercentOf) / 2;
                if (combo > 0 && who.CurrentHP < life) Hurt(who, (life - who.CurrentHP) * combo / 100.0);
                double after = Foes(Board, _monster).Sum(e => e.CurrentHP);
                double was = HookDamage;
                HookDamage += Math.Max(0, before - after);
                _undo.Push(() => HookDamage = was);
            }

            /// <summary>
            /// What the bot's walls deal the enemies standing in them when their turns begin --
            /// «incluso si empieza su turno en el interior» --: sure to come, before they can walk out.
            /// Played on the copy and taken back.
            /// </summary>
            public double WallsAtTheirTurnStart(Fighter bot)
            {
                var walls = WallCells();
                if (walls.Count == 0) return 0;
                double total = 0;
                foreach (var foe in Foes(Board, bot).ToList())
                {
                    if (!walls.TryGetValue(foe.CellId, out int spell) || !WallCatches(foe)) continue;
                    int mark = Mark();
                    int life = foe.CurrentHP;
                    WallHits(foe, spell);
                    total += Math.Max(0, life - foe.CurrentHP);
                    Rewind(mark);
                }
                return total;
            }

            /// <summary>
            /// A portal laid on the copy, as the fight lays it (FightHandler, LayPortalAsync): one a
            /// cell, the fifth of an owner pushing his oldest out, on when another of its network is.
            /// The copy's network is its own: the fight's portals are copied, never touched.
            /// </summary>
            private void LayPortal(Fighter caster, SpellEffect row, int spellId, int cell)
            {
                var current = Board.Portals?.Invoke();
                if (current?.At(cell) != null || !MapGeometry.IsValid(cell) || !_real.Walkable(cell)) return;
                var network = new PortalNetwork();
                foreach (var p in current?.All ?? Array.Empty<Portal>())
                    network.Add(new Portal
                    {
                        Id = p.Id, Owner = p.Owner, Cell = p.Cell, LayingSpell = p.LayingSpell, Grade = p.Grade,
                        DiceNum = p.DiceNum, DiceSide = p.DiceSide, Value = p.Value,
                        Used = p.Used, NeutralisedBy = p.NeutralisedBy, Active = p.Active,
                    });
                var displaced = network.Displaced(caster.Id);
                if (displaced != null) network.Remove(displaced);
                network.Add(new Portal
                {
                    Id = (network.All.Count == 0 ? 0 : network.All.Max(p => p.Id)) + 1, Owner = caster.Id, Cell = cell,
                    LayingSpell = spellId, DiceNum = row.DiceNum, DiceSide = row.DiceSide, Value = row.Value,
                });
                network.Refresh(OccupantOf);
                var before = Board;
                var wasOwn = _ownNetwork;
                _ownNetwork = network;
                Board = before with { Portals = () => network };
                _undo.Push(() => { Board = before; _ownNetwork = wasOwn; });
            }

            /// <summary>«Teleportal» (1182): whoever stands on a portal goes through the network.</summary>
            private const int Teleportal = 1182;

            /// <summary>How many portals one push goes through at most (FightHandler, MaxPortalLegs).</summary>
            private const int MaxPortalLegs = 8;

            /// <summary>
            /// A push, a pull, a step: one cell at a time straight away from <paramref name="origin"/>
            /// (or toward it), stopped by what cannot be walked or by anybody -- the cells it could
            /// not go, for the crash --, stopped dead on a bomb wall of the bot's that catches him,
            /// and through a portal that is on: what was left goes on from its way out, the same way
            /// (FightHandler.ThroughThePortalsAsync, EffectEngine.PastThePortal).
            /// </summary>
            private (int Cell, int Short) Slide(Fighter who, int origin, int cells, bool away)
            {
                var (x, y) = MapGeometry.CellToPoint(who.CellId);
                var (ox, oy) = MapGeometry.CellToPoint(origin);
                int dx = Math.Sign(x - ox), dy = Math.Sign(y - oy);
                if (!away) { dx = -dx; dy = -dy; }
                if (dx == 0 && dy == 0) return (who.CellId, 0);
                var walls = WallStops(who);
                int at = who.CellId, left = cells;
                for (int legs = 0; legs <= MaxPortalLegs; legs++)
                {
                    bool crossed = false;
                    for (; left > 0; left--)
                    {
                        var (cx, cy) = MapGeometry.CellToPoint(at);
                        int next = MapGeometry.PointToCell(cx + dx, cy + dy);
                        if (!MapGeometry.IsValid(next) || !_real.Walkable(next) || next == origin
                            || Board.Fighters.Concat(_summoned).Any(f => f.IsAlive && f != who && f.CellId == next))
                            return (at, left);
                        at = next;
                        if (walls != null && walls.Contains(at)) return (at, 0);
                        if (legs < MaxPortalLegs && WayOut(Board, who, at) is int exit && exit != at)
                        {
                            UsePortals(at, exit);
                            at = exit;
                            left--;
                            crossed = true;
                            break;
                        }
                    }
                    if (!crossed) return (at, 0);
                }
                return (at, 0);
            }

            /// <summary>
            /// One goes through the network from the portal he stands on, as the fight takes him
            /// (FightHandler.CrossPortalAsync): to its way out, and the two portals go off for the turn.
            /// </summary>
            private void CrossPortal(Fighter who, bool walkedIn)
            {
                var network = Board.Portals?.Invoke();
                var entry = network?.At(who.CellId);
                if (entry == null || !CanUsePortals(who)) return;
                if (walkedIn ? !entry.Active : !PortalNetwork.Usable(entry, OccupantOf, ignoring: who.Id)) return;
                var chain = network!.Chain(entry, OccupantOf, who.Id);
                if (chain.Count < 2) return;
                int exit = chain[chain.Count - 1].Cell;
                UsePortals(entry.Cell, exit);
                Moved(who, exit);
            }

            /// <summary>Two portals gone through: used, and off, until the next turn begins -- on the copy's own network.</summary>
            private void UsePortals(int entryCell, int exitCell)
            {
                var network = OwnNetwork();
                if (network == null) return;
                var was = network.All.Select(p => (Portal: p, p.Used, p.Active)).ToList();
                _undo.Push(() => { foreach (var (portal, used, active) in was) { portal.Used = used; portal.Active = active; } });
                foreach (var portal in network.All.Where(p => p.Cell == entryCell || p.Cell == exitCell))
                {
                    portal.Used = true;
                    portal.Active = false;
                }
                network.Refresh(OccupantOf);
            }

            /// <summary>
            /// The copy's network, its own from here on: the fight's portals copied, put on the
            /// copy's board, and taken back with the rest. Null when there are no portals.
            /// </summary>
            private PortalNetwork? OwnNetwork()
            {
                var current = Board.Portals?.Invoke();
                if (current == null) return null;
                if (_ownNetwork != null && ReferenceEquals(current, _ownNetwork)) return _ownNetwork;
                var network = new PortalNetwork();
                foreach (var p in current.All)
                    network.Add(new Portal
                    {
                        Id = p.Id, Owner = p.Owner, Cell = p.Cell, LayingSpell = p.LayingSpell, Grade = p.Grade,
                        DiceNum = p.DiceNum, DiceSide = p.DiceSide, Value = p.Value,
                        Used = p.Used, NeutralisedBy = p.NeutralisedBy, Active = p.Active,
                    });
                var before = Board;
                var wasOwn = _ownNetwork;
                _ownNetwork = network;
                Board = before with { Portals = () => network };
                _undo.Push(() => { Board = before; _ownNetwork = wasOwn; });
                return network;
            }

            private PortalNetwork? _ownNetwork;

            /// <summary>Whether the caster met a row's own conditions on him («*E», «*e») as the cast began.</summary>

            private bool CasterMeetsAsItBegan(Fighter caster, SpellEffect row)
                => _asItBegan != null && _asItBegan.TryGetValue(caster, out var began)
                    ? EffectEngine.CasterQualifies(caster, row.TargetMask, new Dictionary<Fighter, HashSet<int>> { [caster] = began.States })
                    : EffectEngine.CasterMeets(caster, row);

            private void Carry(Fighter carrier, Fighter lifted)
            {
                long wasCarrying = carrier.Carrying, wasCarriedBy = lifted.CarriedBy;
                carrier.Carrying = lifted.Id;
                lifted.CarriedBy = carrier.Id;
                _undo.Push(() => { carrier.Carrying = wasCarrying; lifted.CarriedBy = wasCarriedBy; });
                Moved(lifted, carrier.CellId);
                AddState(carrier, EffectSupport.CarryingState);
                AddState(lifted, EffectSupport.CarriedState);
            }

            private void Drop(Fighter carrier, Fighter carried, int cell)
            {
                long wasCarrying = carrier.Carrying, wasCarriedBy = carried.CarriedBy;
                carrier.Carrying = 0;
                carried.CarriedBy = 0;
                _undo.Push(() => { carrier.Carrying = wasCarrying; carried.CarriedBy = wasCarriedBy; });
                Moved(carried, cell);
                RemoveState(carrier, EffectSupport.CarryingState);
                RemoveState(carried, EffectSupport.CarriedState);
            }

            private readonly Dictionary<Fighter, int> _lockedMp = new();

            private void Lock(Fighter who, int points)
            {
                _lockedMp.TryGetValue(who, out int had);
                _lockedMp[who] = had + points;
                _undo.Push(() => { if (had == 0) _lockedMp.Remove(who); else _lockedMp[who] = had; });
            }

            /// <summary>The MP an enemy will have to walk with on his coming turn.</summary>
            public int MovementNextTurn(Fighter who)
                => Math.Max(0, Math.Max(who.MaxMP, 0) - (_lockedMp.TryGetValue(who, out int locked) ? locked : 0));

            /// <summary>A characteristic moved on a fighter, where the weighing reads it.</summary>
            private void Stat(Fighter who, int characteristic, int amount)
            {
                switch (characteristic)
                {
                    case 1:
                        Set(who, f => f.CurrentAP, (f, v) => f.CurrentAP = v, Math.Max(0, who.CurrentAP + amount));
                        break;
                    case 23:
                        Set(who, f => f.CurrentMP, (f, v) => f.CurrentMP = v, Math.Max(0, who.CurrentMP + amount));
                        break;
                    case 25:
                        Set(who, f => f.Power, (f, v) => f.Power = v, who.Power + amount);
                        break;
                    case 16:
                        Set(who, f => f.FlatDamage, (f, v) => f.FlatDamage = v, who.FlatDamage + amount);
                        break;
                    case 10:
                        Set(who, f => f.Strength, (f, v) => f.Strength = v, who.Strength + amount);
                        break;
                    case 13:
                        Set(who, f => f.Chance, (f, v) => f.Chance = v, who.Chance + amount);
                        break;
                    case 14:
                        Set(who, f => f.Agility, (f, v) => f.Agility = v, who.Agility + amount);
                        break;
                    case 15:
                        Set(who, f => f.Intelligence, (f, v) => f.Intelligence = v, who.Intelligence + amount);
                        break;
                    case 19:
                        Set(who, f => f.Range, (f, v) => f.Range = v, who.Range + amount);
                        break;
                    case 11:
                        Set(who, f => f.MaxHP, (f, v) => f.MaxHP = v, Math.Max(1, who.MaxHP + amount));
                        Set(who, f => f.CurrentHP, (f, v) => f.CurrentHP = v, Math.Max(1, who.CurrentHP + amount));
                        break;
                }
            }

            /// <summary>
            /// A creature put down on this copy: its life and its behaviour as the fight will give
            /// them, so that what is done after reads it -- the strategist's echo.
            /// </summary>
            private bool Summon(Fighter caster, SpellEffect row, int cell, int depth)
            {
                if (row.DiceNum <= 0 || (_real.CanSummon != null && !_real.CanSummon(caster, row.DiceNum, Math.Max(1, row.DiceSide))))
                    return false;
                var recipe = Summons.De(row.DiceNum, Math.Max(1, row.DiceSide));
                var creature = new Fighter
                {
                    Id = System.Threading.Interlocked.Decrement(ref _nextId),
                    TeamId = caster.TeamId,
                    CellId = cell,
                    MonsterId = row.DiceNum,
                    IsMonster = true,
                    Level = caster.Level,
                    MaxHP = 100,
                    CurrentHP = 100,
                    Invocador = caster.Id,
                };
                // As the fight makes it: its grade, its characteristics -- a bomb's blow is its own.
                if (recipe != null) Summons.Characteristics(creature, recipe, caster);
                if (recipe != null && recipe.HechizoPropio != 0)
                    creature.Conducta = (recipe.HechizoPropio, recipe.GradoDelHechizoPropio);
                _summoned.Add(creature);
                var before = Board;
                Board = before with
                {
                    Fighters = before.Fighters.Concat(new[] { creature }).ToList(),
                    Reach = (r, c, f, a) => Reached(r, c, f, a),
                    CanSummon = (c, t, g) => false,
                };
                _undo.Push(() => { _summoned.Remove(creature); Board = before; });
                // And it casts its behaviour spell as it comes out, as the fight has it do
                // (FightHandler, LanzarLaConductaAsync): the Steamer's turret destroys the one of
                // its kind already out -- «solo puede haber una misma torreta por equipo».
                if (creature.Conducta.Spell != 0 && depth < SubCastDepth)
                    Rows(creature.Conducta.Spell, SpellEffects.De(creature.Conducta.Spell, Math.Max(1, creature.Conducta.Grade)),
                         creature, cell, cell, depth + 1);
                return true;
            }
        }

        // ─── A creature that hands its blows back ───────────────────────────────────────────

        /// <summary>
        /// What happens to a blow of its own side on a fighter: the share of it the fighter takes
        /// (<see cref="AllyDamageTaken"/>), and the share of what it takes that it hands back to
        /// everybody around it within <see cref="Radius"/> -- the Yopuka's strategist takes half of
        /// its side's blows (1163 «Daños sufridos x50%» on «DBA») and hands back all it took (1223,
        /// circle of two), the enemy hurt, its summoner spared.
        /// </summary>
        internal readonly record struct Echo(double AllyDamageTaken, double Share, int Radius)
        {
            public static readonly Echo None = new(1.0, 0, 0);
        }

        private static readonly Dictionary<(int Spell, int Grade), Echo> _echoes = new();

        /// <summary>The echo a fighter carries in its behaviour and its attitudes, or none.</summary>
        internal static Echo EchoOf(Fighter who)
        {
            var spells = new List<(int, int)>();
            if (who.Conducta.Spell != 0) spells.Add((who.Conducta.Spell, Math.Max(1, who.Conducta.Grade)));
            foreach (int attitude in who.Buffs.Actitudes) spells.Add((attitude, Math.Max(1, who.Buffs.GradoDeActitud(attitude))));
            var echo = Echo.None;
            foreach (var key in spells)
            {
                Echo found;
                lock (_echoes)
                {
                    if (!_echoes.TryGetValue(key, out found))
                    {
                        found = ReadEcho(key.Item1, key.Item2);
                        _echoes[key] = found;
                    }
                }
                if (found.Share > echo.Share || found.AllyDamageTaken < echo.AllyDamageTaken)
                    echo = new Echo(Math.Min(echo.AllyDamageTaken, found.AllyDamageTaken),
                                    Math.Max(echo.Share, found.Share), Math.Max(echo.Radius, found.Radius));
            }
            return echo;
        }

        /// <summary>The 1163 on its side's blows and the 1223-1228 it hands back, through the casts it chains.</summary>
        private static Echo ReadEcho(int spell, int grade)
        {
            double taken = 1.0, share = 0;
            int radius = 0;
            var seen = new HashSet<(int, int)>();
            void Walk(int s, int g, int depth)
            {
                if (depth > 5 || !seen.Add((s, g))) return;
                foreach (var row in SpellEffects.De(s, g))
                {
                    string triggers = row.Triggers ?? "";
                    if (row.EffectId == 1163 && triggers.Contains("DBA"))
                        taken = Math.Min(taken, Math.Max(0, row.DiceNum) / 100.0);
                    else if (row.EffectId >= 1223 && row.EffectId <= 1228)
                    {
                        share = Math.Max(share, row.DiceNum / 100.0);
                        radius = Math.Max(radius, row.Tamano);
                    }
                    else if (EffectEngine.EsDeLaFamiliaDeSublanzar(row.EffectId) && row.DiceNum > 0)
                        Walk(row.DiceNum, Math.Max(1, row.DiceSide), depth + 1);
                }
            }
            Walk(spell, grade, 0);
            return share > 0 ? new Echo(taken, share, Math.Max(1, radius)) : new Echo(taken, 0, 0);
        }
    }
}

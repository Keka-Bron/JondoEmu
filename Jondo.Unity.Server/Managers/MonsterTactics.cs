using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// A monster's turn, thought out: what is worth doing, from where, to whom -- and where to
    /// stand when it is done.
    /// </summary>
    /// <remarks>
    /// The turn it replaces walked once toward the nearest enemy and then went down its spell
    /// list in the order of its sheet, casting the first that reached as often as it could and
    /// giving up on any that was out of range or out of sight. So monsters repeated one spell,
    /// stood still behind a pillar, and never moved after attacking.
    ///
    /// This one weighs every spell it can pay for, against every target it can reach, from every
    /// cell its movement points can take it to, and takes the best -- again and again while it
    /// has points. What a spell is worth:
    ///
    ///   damage   the blow as the fight would roughly deal it, against the target's resistance,
    ///            with the zone's other enemies; a kill is worth half the victim's life on top,
    ///            and the weaker the victim the more it is worth -- they all go for the same one
    ///   heal     the life it gives back to the most wounded of its side, if any is wounded
    ///   debuff   AP and MP taken from an enemy that still has them
    ///   buff     once a turn, and only when nothing better is at hand
    ///   summon   once a turn, when a cell next to it is free
    ///
    /// divided by what it costs, and a little less for every cell it has to walk. The fight's
    /// own limits apply: per turn, per target, and the cooldown.
    ///
    /// When it has nothing left worth casting it places itself: a ranged one at the reach of its
    /// best spell and as far from the enemy as that allows, a melee one next to the weakest enemy
    /// it can reach -- where it locks -- and one with little life left and no heal, as far away
    /// as it can get.
    /// </remarks>
    public static class MonsterTactics
    {
        /// <summary>One of the monster's spells, as the tactics see it.</summary>
        public sealed class Spell
        {
            public int Id { get; init; }
            public int Grade { get; init; }
            public int Cost { get; init; }
            public int MinRange { get; init; }
            public int MaxRange { get; init; }
            public bool NeedsLineOfSight { get; init; }
            public bool InLine { get; init; }
            public int PerTurn { get; init; }
            public int PerTarget { get; init; }

            /// <summary>Average base damage of the spell's blow to enemies, and its element.</summary>
            public double Damage { get; init; }
            public ElementType Element { get; init; }

            /// <summary>The cells around the aimed one the blow also reaches: 0 for one cell.</summary>
            public int Zone { get; init; }

            /// <summary>Whether its blow also hurts the caster's side inside the zone.</summary>
            public bool HurtsAllies { get; init; }

            /// <summary>Life given back to one of its side.</summary>
            public double Heal { get; init; }

            /// <summary>AP and MP taken from an enemy.</summary>
            public int Removal { get; init; }

            /// <summary>Points of characteristics given to one of its side.</summary>
            public int Buff { get; init; }

            public bool Summons { get; init; }

            /// <summary>
            /// What a spell of pure mechanics is worth -- a state, a glyph, a teleport, a sub-cast,
            /// nothing that hurts or heals: the boss's own moves, which scored nothing and were
            /// never cast. Cast once a turn, on the enemy when its rows are for enemies
            /// (<see cref="UtilityOnEnemies"/>), on itself otherwise.
            /// </summary>
            public double Utility { get; init; }

            /// <summary>The level asks for a free cell: a leap lands NEXT to its target, never on him.</summary>
            public bool NeedsFreeCell { get; init; }
            public bool UtilityOnEnemies { get; init; }

            /// <summary>Cast on itself only: range zero.</summary>
            public bool OnSelf => MaxRange <= 0;

            /// <summary>
            /// The spell's own rows, as the effect engine reads them. A class spell -- a JondoBot's
            /// -- carries them, and is weighed row by row: whom each row reaches from where the
            /// caster stands and where it aims, and what it does to each of them (see
            /// <see cref="ValueOfRows"/>). A monster's spell, and one made by hand for a test,
            /// has none and is weighed by the summary above.
            /// </summary>
            public IReadOnlyList<SpellEffect> Rows { get; init; } = Array.Empty<SpellEffect>();

            public bool HasRows => Rows.Count > 0;

            /// <summary>Whether one of its rows summons -- a double too: it is then aimed at free cells too.</summary>
            public bool SummonsByRow => Rows.Any(r => EffectEngine.EsInvocacion(r.EffectId) || r.EffectId == EffectEngine.InvocaUnDoble);

            /// <summary>Whether one of its rows lays a trap: it is then aimed at the cells the enemy may walk.</summary>
            public bool LaysTraps => Rows.Any(r => r.EffectId == EffectEngine.ColocaUnaTrampa);

            public bool Offensive => Damage > 0 || Removal > 0;
            public bool Supportive => Heal > 0 || Buff > 0;
        }

        /// <summary>What the board says: where one can stand, who stands where, who sees whom.</summary>
        public sealed class Board
        {
            public Func<int, bool> Walkable { get; init; } = _ => true;
            public Func<int, int, bool> Sees { get; init; } = (_, _) => true;
            public IReadOnlyList<Fighter> Fighters { get; init; } = Array.Empty<Fighter>();

            /// <summary>
            /// What leaving a cell costs the walker holding these AP and MP: the fight's own
            /// tackle rule (<see cref="Tackle"/>), handed in by the fight. Nothing by default.
            /// </summary>
            public Func<Fighter, int, int, int, Tackle.Loss> TackleAt { get; init; } = (_, _, _, _) => Tackle.Loss.None;

            public bool Occupied(int cell) => Fighters.Any(f => f.IsAlive && f.CellId == cell);

            /// <summary>
            /// Whom a row reaches when the caster stands on the first cell and aims at the second:
            /// the engine's own reading of its mask and zone (<see cref="EffectEngine.ReachOf"/>),
            /// handed in by the fight. Without one, a plain reading of the zone and the sides.
            /// </summary>
            public Func<SpellEffect, Fighter, int, int, IReadOnlyList<Fighter>>? Reach { get; init; }

            /// <summary>
            /// Whether the caster may put out one more of this template at this grade: the fight's
            /// own limit, which counts each summon by its cost. Without one, it may.
            /// </summary>
            public Func<Fighter, int, int, bool>? CanSummon { get; init; }

            /// <summary>
            /// Whether a fighter is out of sight -- invisible -- and where his enemies saw him
            /// last, -1 for nowhere. An invisible enemy is not aimed at where he stands, which
            /// nobody on the other side knows, but at where he was last seen (<see cref="Ghosts"/>).
            /// </summary>
            public Func<Fighter, bool> Hidden { get; init; } = _ => false;
            public Func<Fighter, int> LastSeen { get; init; } = _ => -1;

            /// <summary>Whether a trap of the caster's side already covers the cell, and how many it has out.</summary>
            public Func<int, bool> Trapped { get; init; } = _ => false;
            public Func<int> TrapsOut { get; init; } = () => 0;
        }

        /// <summary>The enemies the monster sees: alive, of the other side, not invisible.</summary>
        internal static List<Fighter> Foes(Board board, Fighter monster)
            => board.Fighters.Where(f => f.IsAlive && f.TeamId != monster.TeamId && !board.Hidden(f)).ToList();

        /// <summary>
        /// The invisible enemies, each where he was last seen -- where he went invisible, or
        /// where he last cast from. A guess: he may have walked away since, so what lands there
        /// is worth <see cref="GuessOdds"/> of a sure blow.
        /// </summary>
        internal static List<(Fighter Enemy, int Cell)> Ghosts(Board board, Fighter monster)
            => board.Fighters.Where(f => f.IsAlive && f.TeamId != monster.TeamId && board.Hidden(f))
                             .Select(f => (f, board.LastSeen(f)))
                             .Where(g => MapGeometry.IsValid(g.Item2))
                             .ToList();

        /// <summary>How likely an invisible enemy still stands where he was last seen.</summary>
        internal const double GuessOdds = 0.5;

        /// <summary>Whether a fighter is invisible: an invisibility row (150) on him that has taken hold.</summary>
        public static bool IsInvisible(Fighter fighter)
            => fighter.Buffs.Puestos.Any(b => b.EffectId == Jondo.Unity.World.Combat.EffectSupport.Visibility && !b.Pendiente);

        /// <summary>
        /// A way to a cell: the path, and the AP and MP the monster still holds on arrival once
        /// every tackle on the way is paid.
        /// </summary>
        public sealed record Route(List<int> Path, int ActionPoints, int MovementPoints)
        {
            /// <summary>The points the walk costs beyond its steps: what the tackles take.</summary>
            public int LostToTackles(Fighter monster)
                => monster.CurrentAP - ActionPoints + (monster.CurrentMP - MovementPoints - (Path.Count - 1));
        }

        /// <summary>A step of the turn: walk this path, then cast this spell at this cell.</summary>
        public sealed record Action(IReadOnlyList<int> Path, Spell Spell, Fighter Target, int TargetCell, double Score)
        {
            public int From => Path[Path.Count - 1];
        }

        /// <summary>Walking a cell costs a little of what an action is worth: it spends the MP that places the monster afterwards.</summary>
        private const double WalkPenalty = 0.06;

        /// <summary>The next thing worth doing, or null when there is none.</summary>
        public static Action? Next(Board board, Fighter monster, IReadOnlyList<Spell> spells)
        {
            var enemies = Foes(board, monster);
            if (enemies.Count == 0 && Ghosts(board, monster).Count == 0) return null;
            var allies = board.Fighters.Where(f => f.IsAlive && f.TeamId == monster.TeamId).ToList();

            var reach = Routes(board, monster);
            Action? best = null;

            // One summon a turn, whichever spell brings it: a Feca with three of them spent a whole
            // turn putting out barricades.
            bool summoned = spells.Any(s => s.Summons && monster.LanzadosEsteTurno.TryGetValue(s.Id, out int n) && n > 0);

            foreach (var spell in spells)
            {
                if (spell.Cost < 0 || spell.Cost > monster.CurrentAP) continue;
                // A javelin that hits and leaves its lance is a blow first: only the spells that
                // do nothing but summon wait for the next turn.
                if (spell.Summons && summoned && !spell.Offensive) continue;
                if (spell.Cost == 0 && spell.Utility <= 0 && !spell.Offensive) continue;
                if (monster.Recarga.TryGetValue(spell.Id, out int wait) && wait > 0) continue;
                monster.LanzadosEsteTurno.TryGetValue(spell.Id, out int thisTurn);
                if (spell.PerTurn > 0 && thisTurn >= spell.PerTurn) continue;
                // Support once a turn: a monster that buffs itself three times has wasted two.
                // And anything free once a turn too, or a free spell is cast until the cap.
                if ((!spell.Offensive || spell.Cost == 0) && thisTurn > 0) continue;

                foreach (var (cell, route) in reach)
                {
                    // What the tackles on the way take is not there to cast with, and every
                    // point they take weighs like a cell walked.
                    if (spell.Cost > route.ActionPoints) continue;
                    var path = route.Path;
                    double walk = 1.0 - WalkPenalty * (path.Count - 1 + route.LostToTackles(monster));
                    var aims = spell.HasRows ? AimsByRows(board, spell, monster, cell, enemies) : Targets(spell, monster, cell, enemies, allies);
                    foreach (var (target, aim) in aims)
                    {
                        if (!CanCast(board, spell, monster, cell, aim, target)) continue;
                        double value = spell.HasRows
                            ? ValueOfRows(board, spell, monster, cell, aim, enemies)
                            : Value(spell, monster, target, aim, cell, enemies, allies, board);
                        if (value <= 0) continue;

                        double score = value / Math.Max(1, spell.Cost) * walk;
                        if (best == null || score > best.Score)
                            best = new Action(path, spell, target, aim, score);
                    }
                }
            }

            // Power before the blow: a buff that raises its damage goes first when both can be
            // paid -- "lancer puissance le tour d'avant", as the players' own advice goes.
            if (best != null && best.Spell.Offensive && best.Target.TeamId != monster.TeamId)
            {
                var boost = BoostBefore(board, monster, spells, best);
                if (boost != null && boost.Score >= BoostShare * best.Score) return boost;
            }
            return best;
        }

        /// <summary>
        /// The characteristics that make a blow hit harder, for <see cref="BoostBefore"/>: the
        /// four elements, power, damage, critical hits and the action points to cast with.
        /// </summary>
        private static readonly HashSet<int> Boosting = new() { 1, 10, 13, 14, 15, 16, 18, 25 };

        /// <summary>
        /// How much of the blow's worth a buff must be worth, per AP, to go before it: one that
        /// is not takes the AP of a blow for less than a blow gives.
        /// </summary>
        private const double BoostShare = 0.5;

        /// <summary>
        /// A spell of the caster's own that raises what the coming blow does, castable where it
        /// stands and leaving the AP to strike after it -- or null.
        /// </summary>
        private static Action? BoostBefore(Board board, Fighter monster, IReadOnlyList<Spell> spells, Action blow)
        {
            Action? boost = null;
            foreach (var spell in spells)
            {
                if (!spell.HasRows || spell == blow.Spell || spell.Offensive) continue;
                if (spell.Cost < 0 || spell.Cost + blow.Spell.Cost > monster.CurrentAP) continue;
                if (monster.Recarga.TryGetValue(spell.Id, out int wait) && wait > 0) continue;
                monster.LanzadosEsteTurno.TryGetValue(spell.Id, out int thisTurn);
                if (thisTurn > 0 || (spell.PerTurn > 0 && thisTurn >= spell.PerTurn)) continue;

                int from = monster.CellId;
                foreach (int aim in new[] { from }.Concat(board.Fighters.Where(f => f.IsAlive && f.TeamId == monster.TeamId).Select(f => f.CellId)).Distinct())
                {
                    var target = board.Fighters.FirstOrDefault(f => f.IsAlive && f.CellId == aim) ?? monster;
                    if (!CanCast(board, spell, monster, from, aim, target)) continue;
                    bool raises = spell.Rows.Any(row =>
                    {
                        var (characteristic, sign) = DatabaseManager.EffectMeta(row.EffectId);
                        return sign > 0 && Boosting.Contains(characteristic) && Timing(row) >= 1
                               && ReachOf(board, row, monster, from, aim).Contains(monster)
                               && !AlreadyHas(monster, spell.Id);
                    });
                    if (!raises) continue;
                    double value = ValueOfRows(board, spell, monster, from, aim, Foes(board, monster));
                    if (value <= 0) continue;
                    double score = value / Math.Max(1, spell.Cost);
                    if (boost == null || score > boost.Score)
                        boost = new Action(new List<int> { from }, spell, target, aim, score);
                }
            }
            return boost;
        }

        /// <summary>
        /// Where to stand once the casting is done: the path there, or just the monster's own cell.
        /// </summary>
        public static List<int> Reposition(Board board, Fighter monster, IReadOnlyList<Spell> spells)
        {
            var enemies = Foes(board, monster);
            // Where it can get to once the tackles of its way are paid: a monster held in melee
            // does not plan a retreat its MP will not cover.
            var reach = Reachable(board, monster);
            if (enemies.Count == 0 || reach.Count <= 1) return new List<int> { monster.CellId };

            int Nearest(int cell) => enemies.Min(e => MapGeometry.Distance(cell, e.CellId));

            // "Le nombre de gens qui passent leur tour sur la case où ils viennent de taper est
            // impressionnant": the turn ends where no enemy sees it, when it can, and not stuck
            // to one of them -- whoever ends next to an enemy is tackled on his next turn.
            bool Seen(int cell) => enemies.Any(e => board.Sees(e.CellId, cell));
            bool Stuck(int cell) => enemies.Any(e => MapGeometry.Distance(cell, e.CellId) == 1);

            bool dying = monster.MaxHP > 0 && monster.CurrentHP * 100 / monster.MaxHP < 25
                         && !spells.Any(s => s.Heal > 0);
            var attack = spells.Where(s => s.Damage > 0 && !s.OnSelf).OrderByDescending(s => s.Damage / Math.Max(1, s.Cost)).FirstOrDefault();
            bool ranged = attack != null && (attack.MinRange >= 2 || attack.MaxRange >= 4);

            IEnumerable<KeyValuePair<int, List<int>>> cells = reach;
            KeyValuePair<int, List<int>> chosen;

            if (dying)
            {
                // Away from all of them, as far as its legs go, and out of their sight.
                chosen = cells.OrderBy(kv => Seen(kv.Key) ? 1 : 0)
                              .ThenByDescending(kv => Nearest(kv.Key)).ThenBy(kv => kv.Value.Count).First();
            }
            else if (ranged)
            {
                // At the reach of its best spell from the nearest enemy, no closer than it must be.
                int want = attack!.MaxRange + monster.Range;
                chosen = cells.OrderBy(kv => Math.Abs(Nearest(kv.Key) - want)
                                             + (Seen(kv.Key) ? HiddenWorth : 0) + (Stuck(kv.Key) ? StuckCost : 0))
                              .ThenByDescending(kv => Nearest(kv.Key))
                              .ThenBy(kv => kv.Value.Count).First();
            }
            else
            {
                // Next to the weakest enemy it can reach, where it locks him; else toward the nearest.
                var weakest = enemies.OrderBy(e => e.CurrentHP).ThenBy(e => MapGeometry.Distance(monster.CellId, e.CellId));
                chosen = default;
                foreach (var enemy in weakest)
                {
                    var next = cells.Where(kv => MapGeometry.Distance(kv.Key, enemy.CellId) == 1)
                                    .OrderBy(kv => kv.Value.Count).FirstOrDefault();
                    if (next.Value != null) { chosen = next; break; }
                }
                if (chosen.Value == null)
                    chosen = cells.OrderBy(kv => Nearest(kv.Key)).ThenBy(kv => kv.Value.Count).First();
            }
            return chosen.Value;
        }

        /// <summary>What ending the turn out of every enemy's sight is worth, in cells of distance.</summary>
        private const int HiddenWorth = 2;

        /// <summary>What ending it next to an enemy costs a ranged fighter, in cells of distance.</summary>
        private const int StuckCost = 5;

        /// <summary>
        /// Every cell the monster can stand on this turn, with the path there: a search over the
        /// four neighbours of each cell, as far as its MP go, around whoever stands in the way.
        /// </summary>
        public static Dictionary<int, List<int>> Reachable(Board board, Fighter monster)
            => Routes(board, monster).ToDictionary(kv => kv.Key, kv => kv.Value.Path);

        /// <summary>
        /// <see cref="Reachable"/> with the points left on arrival. Every cell left next to an
        /// enemy able to tackle costs what the board's tackle rule says -- computed on the
        /// points held when leaving it, as the walk will -- so the search keeps, for each cell,
        /// the way that leaves the most MP, then the most AP, then the fewest steps. With nobody
        /// to tackle it is the plain breadth-first search it always was, cell for cell.
        /// </summary>
        public static Dictionary<int, Route> Routes(Board board, Fighter monster)
        {
            var routes = new Dictionary<int, Route>
            {
                [monster.CellId] = new Route(new List<int> { monster.CellId }, monster.CurrentAP, monster.CurrentMP),
            };
            var queue = new Queue<int>();
            queue.Enqueue(monster.CellId);
            while (queue.Count > 0)
            {
                int here = queue.Dequeue();
                var route = routes[here];

                // Leaving this cell: the tackle is paid before the step.
                var loss = board.TackleAt(monster, here, route.ActionPoints, route.MovementPoints);
                int actionPoints = route.ActionPoints - loss.ActionPoints;
                int movementPoints = route.MovementPoints - loss.MovementPoints;
                if (movementPoints <= 0) continue;

                foreach (int next in MapGeometry.GetNeighbors(here))
                {
                    if (!board.Walkable(next) || board.Occupied(next)) continue;
                    var candidate = new Route(new List<int>(route.Path) { next }, actionPoints, movementPoints - 1);
                    if (routes.TryGetValue(next, out var known) && !Better(candidate, known)) continue;
                    routes[next] = candidate;
                    queue.Enqueue(next);
                }
            }
            return routes;
        }

        private static bool Better(Route candidate, Route known)
        {
            if (candidate.MovementPoints != known.MovementPoints) return candidate.MovementPoints > known.MovementPoints;
            if (candidate.ActionPoints != known.ActionPoints) return candidate.ActionPoints > known.ActionPoints;
            return candidate.Path.Count < known.Path.Count;
        }

        /// <summary>Whom a spell can be aimed at, and at which cell.</summary>
        private static IEnumerable<(Fighter Target, int Aim)> Targets(Spell spell, Fighter monster, int from,
                                                                     List<Fighter> enemies, List<Fighter> allies)
        {
            if (spell.OnSelf)
            {
                yield return (monster, from);
                yield break;
            }
            if (spell.Offensive && spell.NeedsFreeCell)
            {
                foreach (var enemy in enemies)
                    foreach (int cell in MapGeometry.GetNeighbors(enemy.CellId))
                        yield return (enemy, cell);
            }
            else if (spell.Offensive)
                foreach (var enemy in enemies) yield return (enemy, enemy.CellId);
            if (spell.Heal > 0 || spell.Buff > 0)
                foreach (var ally in allies) yield return (ally, ally == monster ? from : ally.CellId);
            if (spell.Summons)
                foreach (int cell in MapGeometry.GetNeighbors(from)) yield return (monster, cell);
            if (spell.Utility > 0)
            {
                if (spell.UtilityOnEnemies)
                {
                    if (!spell.Offensive) foreach (var enemy in enemies) yield return (enemy, enemy.CellId);
                }
                else yield return (monster, from);
            }
        }

        private static bool CanCast(Board board, Spell spell, Fighter monster, int from, int aim, Fighter target)
        {
            if (spell.OnSelf) return aim == from;
            if (!spell.HasRows && spell.Summons && target == monster && aim != from)
                return board.Walkable(aim) && !board.Occupied(aim) && MapGeometry.Distance(from, aim) <= Math.Max(1, spell.MaxRange);

            if (spell.NeedsFreeCell && (board.Occupied(aim) || aim == from || !board.Walkable(aim))) return false;

            int distance = MapGeometry.Distance(from, aim);
            if (distance < spell.MinRange || distance > spell.MaxRange + monster.Range) return false;
            if (spell.InLine && !InLine(from, aim)) return false;
            if (spell.NeedsLineOfSight && distance > 1 && !board.Sees(from, aim)) return false;

            if (spell.PerTarget > 0 && monster.LanzadosPorObjetivo.TryGetValue((spell.Id, target.Id), out int onIt)
                && onIt >= spell.PerTarget)
                return false;
            return true;
        }

        private static double Value(Spell spell, Fighter monster, Fighter target, int aim, int from,
                                    List<Fighter> enemies, List<Fighter> allies, Board board)
        {
            double value = 0;
            bool onEnemy = target.TeamId != monster.TeamId;

            // A spell that hurts whoever stands on the aimed cell is never aimed at one of its
            // own, whatever else it gives: the JondoBot's Bumerán Pérfido "buffed" its own summon
            // to death.
            if (!onEnemy && target != monster && spell.Damage > 0 && Hit(spell, aim, target)) return 0;

            if (spell.Damage > 0 && (onEnemy || spell.OnSelf))
            {
                foreach (var hit in enemies.Where(e => Hit(spell, aim, e)))
                    value += Worth(spell, monster, hit);
                if (spell.HurtsAllies)
                    foreach (var own in allies.Where(a => Hit(spell, aim, a)))
                        value -= Blow(spell, monster, own);
            }

            if (spell.Removal > 0 && onEnemy && (target.CurrentAP > 0 || target.CurrentMP > 0))
                value += spell.Removal * (20 + target.Level / 4.0);

            if (spell.Heal > 0 && !onEnemy)
            {
                int missing = target.MaxHP - target.CurrentHP;
                if (missing * 10 >= target.MaxHP)
                {
                    value += Math.Min(spell.Heal, missing) * (target.CurrentHP * 2 < target.MaxHP ? 1.5 : 1.0);
                }
            }

            if (spell.Buff > 0 && !onEnemy)
                value += 10 + spell.Buff * 2 + monster.Level / 10.0;

            if (spell.Summons && target == monster && aim != from)
            {
                // On the side the enemy is on, between them, not behind: a barricade behind its
                // caster shields him from nothing.
                int Nearest(int cell) => enemies.Count == 0 ? 0 : enemies.Min(e => MapGeometry.Distance(cell, e.CellId));
                value += 40 + monster.Level / 2.0 + 15 * (Nearest(from) - Nearest(aim));
            }

            if (spell.Utility > 0 && (spell.UtilityOnEnemies ? onEnemy : target == monster))
                value += spell.Utility;

            return value;
        }

        /// <summary>
        /// What hitting an enemy is worth: the blow, a kill on top, and more the weaker he is --
        /// which is what makes the whole group go for the same one.
        /// </summary>
        private static double Worth(Spell spell, Fighter monster, Fighter enemy)
            => WorthOfBlow(Blow(spell, monster, enemy), enemy);

        private static double WorthOfBlow(double blow, Fighter enemy)
        {
            double worth = Math.Min(blow, enemy.CurrentHP);
            if (blow >= enemy.CurrentHP) worth += enemy.MaxHP * 0.5;
            if (enemy.MaxHP > 0) worth *= 1.0 + 0.5 * (1.0 - (double)enemy.CurrentHP / enemy.MaxHP);
            if (enemy.EsInvocado) worth *= 0.6;
            return worth;
        }

        /// <summary>
        /// The blow as the fight would roughly deal it: the base, raised by the caster's
        /// characteristic of its element, its power and its fixed damage, cut by the target's
        /// resistance. An estimate to compare spells with, not the fight's own reckoning.
        /// </summary>
        public static double Blow(Spell spell, Fighter caster, Fighter target)
            => Blow(spell.Damage, spell.Element, caster, target);

        public static double Blow(double baseDamage, ElementType element, Fighter caster, Fighter target)
        {
            int characteristic = Math.Max(0, caster.GetStatForElement(element));
            double raw = baseDamage * (100 + characteristic + Math.Max(0, caster.Power)) / 100.0 + caster.FlatDamage;
            int resistance = Math.Clamp(target.GetResPctForElement(element), -100, 100);
            return Math.Max(0, raw * (100 - resistance) / 100.0);
        }

        // ─── A class spell, weighed row by row ─────────────────────────────────────────────

        /// <summary>
        /// Where a spell weighed by its rows may be aimed from a cell: its own cell when it
        /// reaches it, every fighter's cell, the free cells around the enemies -- a leap lands
        /// there, a line thrown there passes through them -- and, for a spell that summons, the
        /// free cells in its reach nearest to the enemy. The target is whoever stands there.
        /// </summary>
        /// <remarks>
        /// A lance thrown was only ever put down on one of the four cells next to its caster,
        /// whichever way the enemy was: the Forjalanza JondoBot threw it behind itself on its
        /// first turn.
        /// </remarks>
        private static IEnumerable<(Fighter Target, int Aim)> AimsByRows(Board board, Spell spell, Fighter monster,
                                                                          int from, List<Fighter> enemies)
        {
            var aims = new HashSet<int>();
            if (spell.OnSelf || spell.MinRange == 0) aims.Add(from);
            bool Unseen(Fighter f) => f.TeamId != monster.TeamId && board.Hidden(f);
            if (!spell.OnSelf)
            {
                foreach (var fighter in board.Fighters)
                    if (fighter.IsAlive && !Unseen(fighter)) aims.Add(fighter == monster ? from : fighter.CellId);
                foreach (var enemy in enemies)
                    foreach (int cell in MapGeometry.GetNeighbors(enemy.CellId))
                        if (board.Walkable(cell) && !board.Occupied(cell)) aims.Add(cell);
                // Where an invisible one was last seen, and around it: a blow thrown there on a guess.
                foreach (var (_, seen) in Ghosts(board, monster))
                {
                    aims.Add(seen);
                    foreach (int cell in MapGeometry.GetNeighbors(seen))
                        if (board.Walkable(cell)) aims.Add(cell);
                }

                // A trap goes where the enemy is likely to walk: the free cells in its reach
                // with the best odds of being stepped on.
                if (spell.LaysTraps)
                {
                    int reach = spell.MaxRange + monster.Range;
                    var threats = Threats(board, monster, enemies);
                    var likely = Enumerable.Range(0, MapGeometry.MaxCells)
                        .Where(c => c != from && board.Walkable(c) && !board.Occupied(c) && !board.Trapped(c))
                        .Where(c => { int d = MapGeometry.Distance(from, c); return d >= spell.MinRange && d <= reach; })
                        .Select(c => (Cell: c, Odds: threats.Count == 0 ? 0 : threats.Max(t => TrapOdds(board, new[] { c }, t.Cell, t.MP, from))))
                        .Where(c => c.Odds > 0)
                        .OrderByDescending(c => c.Odds).Take(TrapCellsTried);
                    foreach (var (cell, _) in likely) aims.Add(cell);
                }

                if (spell.SummonsByRow && enemies.Count > 0)
                {
                    int reach = spell.MaxRange + monster.Range;
                    int Nearest(int cell) => enemies.Min(e => MapGeometry.Distance(cell, e.CellId));
                    var toward = Enumerable.Range(0, MapGeometry.MaxCells)
                        .Where(c => c != from && board.Walkable(c) && !board.Occupied(c) && !board.Trapped(c))
                        .Where(c => { int d = MapGeometry.Distance(from, c); return d >= spell.MinRange && d <= reach; })
                        .OrderBy(Nearest).Take(SummonCellsTried);
                    foreach (int cell in toward) aims.Add(cell);
                }
            }

            foreach (int aim in aims)
            {
                var there = aim == from ? monster
                    : board.Fighters.FirstOrDefault(f => f.IsAlive && f != monster && f.CellId == aim && !Unseen(f));
                yield return (there ?? monster, aim);
            }
        }

        /// <summary>How many of the cells nearest to the enemy a summon is weighed on.</summary>
        private const int SummonCellsTried = 6;

        /// <summary>How many of the likeliest cells a trap is weighed on.</summary>
        private const int TrapCellsTried = 8;

        /// <summary>
        /// Where the enemies are, as the monster knows it: each one it sees where he stands, each
        /// invisible one where he was last seen, with the MP he will walk on his turn.
        /// </summary>
        private static List<(Fighter Who, int Cell, int MP, double Sure)> Threats(Board board, Fighter monster, List<Fighter> enemies)
        {
            var threats = enemies.Select(e => (e, e.CellId, Math.Max(e.MaxMP, e.CurrentMP), 1.0)).ToList();
            foreach (var (ghost, seen) in Ghosts(board, monster))
                threats.Add((ghost, seen, Math.Max(ghost.MaxMP, ghost.CurrentMP), GuessOdds));
            return threats;
        }

        /// <summary>
        /// The odds an enemy walks onto a trap covering these cells on his coming turn -- a
        /// guess, the enemy not seeing it: best on his way to the monster within his MP, then
        /// next to the monster, where one who fights up close ends, then anywhere he can walk,
        /// and a little for later, a trap lasting until something steps on it.
        /// </summary>
        internal static double TrapOdds(Board board, IEnumerable<int> footprint, int enemyCell, int enemyMp, int monsterCell)
        {
            double best = 0;
            int apart = MapGeometry.Distance(enemyCell, monsterCell);
            foreach (int cell in footprint)
            {
                if (!board.Walkable(cell)) continue;
                int toCell = MapGeometry.Distance(enemyCell, cell);
                // Under him it waits for him to be moved onto it again, which is not his to choose.
                if (toCell == 0) continue;
                int toMonster = MapGeometry.Distance(cell, monsterCell);
                double odds = toCell > enemyMp ? 0.05
                    : toCell + toMonster == apart ? 0.5
                    : toMonster == 1 ? 0.35
                    : 0.15;
                best = Math.Max(best, odds);
            }
            return best;
        }

        /// <summary>
        /// What a cast of a class spell is worth, read the way the engine will apply it: row by
        /// row, whom each one reaches from <paramref name="from"/> aimed at <paramref name="aim"/>,
        /// and what it does to each of them.
        /// </summary>
        /// <remarks>
        /// <code>
        ///   damage    the blow of each row on each enemy it reaches, summed per enemy and then
        ///             worth what a kill and a weak enemy make it worth; on one of its own side,
        ///             twice its cost -- a JondoBot does not hit its own
        ///   heal      the life given back to a wounded one of its side, and lost if it is an enemy's
        ///   stats     each point by what it is (<see cref="StatWeight"/>): given to its side,
        ///             taken from the enemy; a buff it already carries from the same spell, nothing
        ///   summon    once, on a free cell, the nearer the enemy the better
        ///   sub-cast  the child spell's own rows, cast by whom and at whom the row says
        ///   delayed   a poison or a buff that waits on the turn's start or end, a little less
        /// </code>
        /// It replaces a summary that added every positive number of the spell as a buff --
        /// Bumerán Pérfido's four random characteristics made it 640, Punzón 200 -- and cast
        /// whatever did nothing it could weigh on itself: Eclipse, which only moves the lance,
        /// went out with no lance on the board.
        /// </remarks>
        internal static double ValueOfRows(Board board, Spell spell, Fighter monster, int from, int aim, List<Fighter> enemies)
        {
            var blows = new Dictionary<Fighter, double>();
            var heals = new Dictionary<Fighter, double>();
            double value = 0;
            int Nearest(int cell) => enemies.Count == 0 ? 0 : enemies.Min(e => MapGeometry.Distance(cell, e.CellId));
            var ghosts = Ghosts(board, monster);
            bool trapped = false;

            void Rows(int spellId, IReadOnlyList<SpellEffect> rows, Fighter caster, int castFrom, int castAim,
                      double weight, int depth)
            {
                bool summoned = false;
                foreach (var row in rows)
                {
                    if (EffectEngine.EsMarcadorDeGuion(row.EffectId) || row.ForClientOnly) continue;
                    double timing = Timing(row);
                    if (timing <= 0) continue;
                    double chance = row.Probabilidad > 0 && row.Probabilidad < 100 ? row.Probabilidad / 100.0 : 1.0;
                    double p = weight * timing * chance;
                    int id = row.EffectId;

                    bool twin = id == EffectEngine.InvocaUnDoble;
                    if (EffectEngine.EsInvocacion(id) || twin)
                    {
                        // Put down where it was aimed: a free cell, and the nearer the enemy the
                        // better. It plays every turn from then on, so it is worth a blow or two --
                        // when the fight's limit lets it out at all. A double -- the Sram's, a
                        // copy of it that locks and takes the blows -- goes out the same way; the
                        // engine does not count it against the limit.
                        if (!summoned && castAim != castFrom && board.Walkable(castAim) && !board.Occupied(castAim)
                            && !board.Trapped(castAim) && EffectEngine.CasterMeets(caster, row)
                            && (twin || board.CanSummon == null || board.CanSummon(caster, row.DiceNum, Math.Max(1, row.DiceSide))))
                        {
                            value += p * (150 + 1.5 * monster.Level + 15 * (Nearest(castFrom) - Nearest(castAim)));
                            summoned = true;
                        }
                        continue;
                    }

                    if (id == EffectEngine.ColocaUnaTrampa)
                    {
                        // A trap: its own spell on whoever steps on it, times the odds anybody
                        // does. Put on a free cell, one per cast, not over another of its side's,
                        // and each one out already makes the next less likely to be walked on.
                        if (trapped || depth > 0 || row.DiceNum <= 0) continue;
                        if (castAim == castFrom || !board.Walkable(castAim) || board.Occupied(castAim) || board.Trapped(castAim)) continue;
                        trapped = true;
                        var footprint = EffectEngine.CasillasDelEfecto(row, castFrom, castAim);
                        if (footprint.Count == 0) footprint = new List<int> { castAim };
                        double crowd = Math.Pow(TrapCrowding, board.TrapsOut());
                        foreach (var (who, cell, mp, sure) in Threats(board, monster, enemies))
                        {
                            double odds = TrapOdds(board, footprint, cell, mp, castFrom) * sure * crowd;
                            if (odds <= 0) continue;
                            double sprung = TrapBlow(caster, who, row.DiceNum, Math.Max(1, row.DiceSide), castAim, 0);
                            blows.TryGetValue(who, out double had);
                            blows[who] = had + sprung * odds * p;
                        }
                        continue;
                    }

                    if (id == Jondo.Unity.World.Combat.EffectSupport.Visibility)
                    {
                        // Invisible: the enemy cannot aim at it, only guess. Worth more to one
                        // under half its life, nothing to one already out of sight, and the
                        // opposite of a gift on an enemy.
                        foreach (var who in ReachOf(board, row, caster, castFrom, castAim))
                        {
                            if (IsInvisible(who)) continue;
                            double worth = p * InvisibleShare * who.MaxHP
                                           * (who.CurrentHP * 2 < who.MaxHP ? 1.5 : 1.0);
                            value += who.TeamId == monster.TeamId ? worth : -worth;
                        }
                        continue;
                    }

                    // Whom it reaches -- but not an invisible enemy where he really is: nobody on
                    // this side knows that. He counts where he was last seen, on a guess.
                    var reached = ReachOf(board, row, caster, castFrom, castAim)
                        .Where(w => w.TeamId == monster.TeamId || !board.Hidden(w)).ToList();
                    bool damage = id >= Jondo.Unity.World.Combat.EffectSupport.FirstDamage && id <= Jondo.Unity.World.Combat.EffectSupport.LastDamage;
                    if (damage && ghosts.Count > 0 && HitsEnemies(row))
                    {
                        var zone = EffectEngine.CasillasDelEfecto(row, castFrom, castAim);
                        foreach (var (ghost, seen) in ghosts)
                        {
                            if (!zone.Contains(seen)) continue;
                            double guess = Blow(Average(row), ElementOfDamage(id), caster, ghost) * p * GuessOdds;
                            blows.TryGetValue(ghost, out double had);
                            blows[ghost] = had + guess;
                        }
                    }
                    if (reached.Count == 0) continue;

                    if (damage)
                    {
                        double dice = Average(row);
                        var element = ElementOfDamage(id);
                        foreach (var who in reached)
                        {
                            double blow = Blow(dice, element, caster, who) * p;
                            if (who.TeamId != monster.TeamId)
                            {
                                blows.TryGetValue(who, out double had);
                                blows[who] = had + blow;
                                // Life stolen: half of it back to whoever stole it.
                                if (id <= LastLifeSteal && caster.TeamId == monster.TeamId)
                                {
                                    heals.TryGetValue(caster, out double healed);
                                    heals[caster] = healed + blow / 2;
                                }
                            }
                            else value -= blow * (who.EsInvocado ? 1.0 : 2.0);
                        }
                        continue;
                    }

                    if (id == Jondo.Unity.World.Combat.EffectSupport.FireHeal || id == Jondo.Unity.World.Combat.EffectSupport.HealPercent)
                    {
                        foreach (var who in reached)
                        {
                            double amount = id == Jondo.Unity.World.Combat.EffectSupport.HealPercent
                                ? who.MaxHP * Average(row) / 100.0
                                : Average(row) * (100 + Math.Max(0, caster.Intelligence)) / 100.0;
                            amount *= p;
                            if (who.TeamId == monster.TeamId)
                            {
                                heals.TryGetValue(who, out double had);
                                heals[who] = had + amount;
                            }
                            else value -= Math.Min(amount, who.MaxHP - who.CurrentHP);
                        }
                        continue;
                    }

                    if (EffectEngine.EsDeLaFamiliaDeSublanzar(id))
                    {
                        if (depth >= SubCastDepth || row.DiceNum <= 0) continue;
                        var child = SpellEffects.De(row.DiceNum, Math.Max(1, row.DiceSide));
                        bool byTheOne = EffectEngine.ComoSublanza(id).LanzaElCandidato;
                        foreach (var who in reached)
                        {
                            int whoCell = who == monster ? from : who.CellId;
                            Rows(row.DiceNum, child, byTheOne ? who : caster, byTheOne ? whoCell : castFrom, whoCell,
                                 p * SubCastShare, depth + 1);
                        }
                        continue;
                    }

                    var (characteristic, sign) = DatabaseManager.EffectMeta(id);
                    if (characteristic > 0 && sign != 0)
                    {
                        double amount = Average(row) * sign * StatWeight(characteristic) * p
                                        * (row.Duration >= 2 ? 1.3 : 1.0);
                        foreach (var who in reached)
                        {
                            bool mine = who.TeamId == monster.TeamId;
                            if (mine && amount > 0 && AlreadyHas(who, spellId)) continue;
                            double onHim = amount * (who.EsInvocado ? 0.5 : 1.0);
                            // Holding out is worth twice as much to one of its side under half its life.
                            if (mine && amount > 0 && Defensive.Contains(characteristic) && who.CurrentHP * 2 < who.MaxHP)
                                onHim *= 2;
                            // Points taken are only ever the ones he has: Influencia's "-100 MP"
                            // is all of them, six, not a hundred.
                            if (onHim < 0 && (characteristic == 1 || characteristic == 23))
                            {
                                int held = characteristic == 1 ? Math.Max(who.MaxAP, who.CurrentAP) : Math.Max(who.MaxMP, who.CurrentMP);
                                onHim = Math.Max(onHim, -held * StatWeight(characteristic) * p);
                            }
                            value += mine ? onHim : -onHim;
                        }
                    }
                }
            }

            Rows(spell.Id, spell.Rows, monster, from, aim, 1.0, 0);

            foreach (var (enemy, blow) in blows) value += WorthOfBlow(blow, enemy);
            foreach (var (own, healed) in heals)
            {
                int missing = own.MaxHP - own.CurrentHP;
                if (missing * 10 < own.MaxHP) continue;
                value += Math.Min(healed, missing) * (own.CurrentHP * 2 < own.MaxHP ? 1.5 : 1.0);
            }
            return value;
        }

        /// <summary>How deep a spell's sub-casts are followed, and how much of theirs counts.</summary>
        private const int SubCastDepth = 2;
        private const double SubCastShare = 0.9;

        /// <summary>
        /// What going invisible is worth, as a share of the life of whoever goes invisible: the
        /// blow or so of the enemy's that cannot be aimed. For a JondoBot, 333 of its 6,666 --
        /// on 2 AP as much as its best blow per AP --, half as much again under half its life.
        /// </summary>
        private const double InvisibleShare = 0.05;

        /// <summary>How much less likely a trap is walked on for each one of its side already out.</summary>
        private const double TrapCrowding = 0.7;

        /// <summary>Whether a row's mask takes in enemies -- all of them, or the players among them.</summary>
        private static bool HitsEnemies(SpellEffect row)
            => (row.TargetMask ?? "").Split(',').Select(t => t.Trim().TrimStart('*'))
                                     .Any(t => t == "A" || t == "H");

        /// <summary>
        /// What a trap's own spell does to the one who steps on it, the spell going off on the
        /// trap's centre with him on it: the rows whose zone takes his cell in -- Fragmentación
        /// hits the centre with one row and three rings around it with three more, and he is in
        /// the centre, not in the rings --, its sub-casts' as far as they go, and the AP and MP
        /// it takes from him.
        /// </summary>
        private static double TrapBlow(Fighter caster, Fighter victim, int spell, int grade, int centre, int depth)
        {
            double total = 0;
            foreach (var row in SpellEffects.De(spell, grade))
            {
                if (EffectEngine.EsMarcadorDeGuion(row.EffectId) || row.ForClientOnly || Timing(row) <= 0) continue;
                int id = row.EffectId;
                bool onHim = EffectEngine.CasillasDelEfecto(row, centre, centre).Contains(centre);
                if (id >= Jondo.Unity.World.Combat.EffectSupport.FirstDamage && id <= Jondo.Unity.World.Combat.EffectSupport.LastDamage)
                {
                    if (onHim && HitsEnemies(row)) total += Blow(Average(row), ElementOfDamage(id), caster, victim) * Timing(row);
                }
                else if (EffectEngine.EsDeLaFamiliaDeSublanzar(id) && depth < SubCastDepth && row.DiceNum > 0)
                    total += SubCastShare * TrapBlow(caster, victim, row.DiceNum, Math.Max(1, row.DiceSide), centre, depth + 1);
                else if (onHim && HitsEnemies(row))
                {
                    var (characteristic, sign) = DatabaseManager.EffectMeta(id);
                    if (sign < 0 && (characteristic == 1 || characteristic == 23))
                    {
                        int held = characteristic == 1 ? Math.Max(victim.MaxAP, victim.CurrentAP) : Math.Max(victim.MaxMP, victim.CurrentMP);
                        total += Math.Min(Average(row), held) * StatWeight(characteristic);
                    }
                }
            }
            return total;
        }

        /// <summary>The life-stealing blows run from 91 to 95; 96 to 100 only hurt.</summary>
        private const int LastLifeSteal = 95;

        /// <summary>
        /// Whether a row goes off at the cast, later -- a poison or a buff hooked on the start or
        /// the end of a turn, worth a little less -- or on something this turn cannot count on.
        /// </summary>
        private static double Timing(SpellEffect row)
        {
            bool now = false, later = false;
            foreach (var trigger in row.Disparadores())
            {
                if (string.Equals(trigger, EffectEngine.AlLanzar, StringComparison.OrdinalIgnoreCase)) now = true;
                else if (string.Equals(trigger, EffectEngine.AlEmpezarElTurno, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(trigger, EffectEngine.AlAcabarElTurno, StringComparison.OrdinalIgnoreCase)) later = true;
            }
            return now ? 1.0 : later ? 0.7 * Repeats(row) : 0;
        }

        /// <summary>
        /// How many times a row that waits on the turn's start or end goes off: once for each
        /// turn it lasts -- a poison of three turns bites three times --, each a little less sure
        /// than the one before, and no more than four counted.
        /// </summary>
        private static double Repeats(SpellEffect row)
        {
            int turns = row.Duration < 0 ? 4 : Math.Clamp(row.Duration, 1, 4);
            double total = 0, weight = 1;
            for (int i = 0; i < turns; i++, weight *= 0.8) total += weight;
            return total;
        }

        /// <summary>
        /// The amount a row deals or gives: its dice, as the fight rolls them. Never its value,
        /// which is a parameter for most rows -- the state a 950 puts, the spell a 1160 casts --
        /// and read as an amount it made every state a buff of five thousand points.
        /// </summary>
        private static double Average(SpellEffect row)
            => row.DiceSide > row.DiceNum ? (row.DiceNum + row.DiceSide) / 2.0 : row.DiceNum;

        /// <summary>The element of a damage effect: 91-95 steal life and 96-100 only hurt, water, earth, air, fire, neutral.</summary>
        private static ElementType ElementOfDamage(int effect) => ((effect - 91) % 5) switch
        {
            0 => ElementType.Water,
            1 => ElementType.Earth,
            2 => ElementType.Air,
            3 => ElementType.Fire,
            _ => ElementType.Neutral,
        };

        /// <summary>
        /// What one point of a characteristic is worth to a fighter of level 200 with 1,500 in
        /// every element -- a JondoBot: an AP is a spell, a hundred of an element a few percent
        /// of every blow, a point of resistance a point of every blow taken.
        /// </summary>
        private static double StatWeight(int characteristic) => characteristic switch
        {
            1 => 55,                   // action points
            23 => 30,                  // movement points
            19 => 8,                   // range
            26 => 10,                  // summons
            16 => 4,                   // damage
            18 => 2,                   // critical hits
            25 => 0.35,                // power
            10 or 13 or 14 or 15 => 0.25, // strength, chance, agility, intelligence
            11 => 0.2,                 // vitality
            49 => 1.5,                 // heals
            78 or 79 => 1.5,           // escape, lock
            27 or 28 => 1,             // AP and MP dodge
            82 or 83 => 1,             // AP and MP withdrawal
            96 => 0.8,                 // shield points
            >= 33 and <= 37 => 3,      // resistance in percent: a point off every blow taken
            44 => 0.02,                // initiative
            0 or 97 => 0,              // life and its loss go by the blows and heals
            71 => 0,                   // a state: what it does is in the rows that read it
            _ => 0.5,
        };

        /// <summary>The characteristics that keep one standing: shield, resistances, vitality.</summary>
        private static readonly HashSet<int> Defensive = new() { 96, 33, 34, 35, 36, 37, 11 };

        /// <summary>Whether a fighter already carries a row of this spell: a buff is not stacked on itself.</summary>
        private static bool AlreadyHas(Fighter who, int spellId)
            => who.Buffs.Puestos.Any(b => b.HechizoOrigen == spellId);

        /// <summary>Whom a row reaches: the board's reading, or a plain one of its zone and sides.</summary>
        private static IReadOnlyList<Fighter> ReachOf(Board board, SpellEffect row, Fighter caster, int from, int aim)
        {
            if (board.Reach != null) return board.Reach(row, caster, from, aim);

            var cells = EffectEngine.CasillasDelEfecto(row, from, aim);
            var inside = cells.Count > 0 ? new HashSet<int>(cells) : new HashSet<int> { aim };
            var parts = (row.TargetMask ?? "").Split(',').Select(p => p.Trim())
                                              .Where(p => p.Length > 0 && p[0] != '*').ToList();
            var templates = parts.Where(p => p.Length > 1 && p[0] == 'F' && int.TryParse(p.Substring(1), out _))
                                 .Select(p => int.Parse(p.Substring(1))).ToList();
            bool self = parts.Contains("C"), own = parts.Contains("a"), other = parts.Contains("A"), mates = parts.Contains("g");

            var reached = new List<Fighter>();
            if (self) reached.Add(caster);
            foreach (var who in board.Fighters)
            {
                if (!who.IsAlive || reached.Contains(who)) continue;
                int cell = who == caster ? from : who.CellId;
                if (!inside.Contains(cell)) continue;
                bool mine = who.TeamId == caster.TeamId;
                bool takes = parts.Count == 0 ? cell == aim
                           : (own && mine) || (other && !mine) || (mates && mine && who != caster);
                if (templates.Count > 0 && (!who.IsMonster || !templates.Contains(who.MonsterId))) takes = false;
                if (takes) reached.Add(who);
            }
            return reached;
        }

        private static bool Hit(Spell spell, int aim, Fighter who)
            => MapGeometry.Distance(aim, who.CellId) <= spell.Zone;

        /// <summary>Same row or same column of the diamond grid: a straight line.</summary>
        private static bool InLine(int a, int b)
        {
            var (ax, ay) = MapGeometry.CellToPoint(a);
            var (bx, by) = MapGeometry.CellToPoint(b);
            return ax == bx || ay == by;
        }
    }
}

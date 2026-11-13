using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.World.Combat;
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
    public static partial class MonsterTactics
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
            /// Whether the caster's range adds to the spell's (the level's RangeCanBeBoosted). A
            /// JondoBot's +6 made Ira de Yopuka, a blow of range 1, land from seven cells away.
            /// </summary>
            public bool RangeBoostable { get; init; } = true;

            /// <summary>
            /// Whether what it does depends on where it is cast from, not only on where it is aimed:
            /// a line or a cone drawn from the caster, a push, a pull, a leap, a swap.
            /// </summary>
            public bool DependsOnWhereFrom => _dependsOnWhereFrom ??= Rows.Any(r =>
                DirectionalShapes.Contains((char)r.Forma) || Moving.Contains(r.EffectId));

            private bool? _dependsOnWhereFrom;

            /// <summary>
            /// Whether the spell moves somebody, by its rows or by what they cast: a leap, a swap, a
            /// way back -- Rebobinamiento's, cast by its sub-spell when the turn ends. What it is
            /// worth is where it leaves them, which only playing it on a copy of the fight shows.
            /// </summary>
            public bool Moves => _moves ??= MovesByRows(Rows, 0);

            private bool? _moves;

            private static bool MovesByRows(IEnumerable<SpellEffect> rows, int depth)
                => rows.Any(r => !r.ForClientOnly
                                 && (Moving.Contains(r.EffectId) || Returning.Contains(r.EffectId)
                                     || (depth < SubCastDepth && EffectEngine.EsDeLaFamiliaDeSublanzar(r.EffectId) && r.DiceNum > 0
                                         && MovesByRows(SpellEffects.De(r.DiceNum, Math.Max(1, r.DiceSide)), depth + 1))));

            /// <summary>
            /// Whether the spell puts or takes a state on its caster, by its rows or by what they
            /// cast: the Pandawa's Comilona, sober to drunk and back; or picks somebody up, which
            /// leaves him Carrying and opens his throws. What it is worth is the spells
            /// the new state opens and changes, which only playing it on a copy of the fight shows.
            /// </summary>
            public bool Switches => _switches ??= SwitchesByRows(Rows, 0);

            private bool? _switches;

            private static bool SwitchesByRows(IEnumerable<SpellEffect> rows, int depth)
                => rows.Any(r => !r.ForClientOnly
                                 && ((r.EffectId is EffectSupport.AddState or EffectSupport.RemoveState && OnCaster(r))
                                     || r.EffectId == EffectSupport.Carry
                                     || (depth < SubCastDepth && EffectEngine.EsDeLaFamiliaDeSublanzar(r.EffectId) && r.DiceNum > 0
                                         && OnCaster(r)
                                         && SwitchesByRows(SpellEffects.De(r.DiceNum, Math.Max(1, r.DiceSide)), depth + 1))));

            /// <summary>
            /// Whether the spell puts or takes a state on the caster's own creatures, by its rows or
            /// by what they cast: the Steamer's Evolución, its turret's states II and III. What it is
            /// worth is what the creature does after it, on its turn and to its summoner.
            /// </summary>
            public bool Tends => _tends ??= TendsByRows(Rows, 0);

            private bool? _tends;

            private static bool TendsByRows(IEnumerable<SpellEffect> rows, int depth)
                => rows.Any(r => !r.ForClientOnly
                                 && ((r.EffectId is EffectSupport.AddState or EffectSupport.RemoveState && OnOwnCreature(r))
                                     || (depth < SubCastDepth && EffectEngine.EsDeLaFamiliaDeSublanzar(r.EffectId) && r.DiceNum > 0
                                         && TendsByRows(SpellEffects.De(r.DiceNum, Math.Max(1, r.DiceSide)), depth + 1))));

            /// <summary>Whether a row's mask names the caster's own creatures: P, or its side with a template.</summary>
            private static bool OnOwnCreature(SpellEffect row)
            {
                var parts = (row.TargetMask ?? "").Split(',').Select(p => p.Trim()).ToList();
                return parts.Contains("P") || (parts.Contains("a") && parts.Any(p => p.Length > 1 && p[0] == 'F' && char.IsDigit(p[1])));
            }

            /// <summary>Whether a row's mask names the caster: C, or c in its zone.</summary>
            private static bool OnCaster(SpellEffect row)
                => (row.TargetMask ?? "").Split(',').Any(p => p.Trim() is "C" or "c");

            /// <summary>The turns between two casts of it: Ira de Yopuka's three.</summary>
            public int Interval { get; init; }

            /// <summary>
            /// Whether it may be cast through the portals: not when its level forbids it
            /// (PortalProjectionForbidden), nor when it switches portals off -- Neutral aimed at one
            /// switches it off (FightHandler.Projection).
            /// </summary>
            public bool ThroughPortals { get; init; } = true;

            /// <summary>
            /// Whether it lays a portal (1181): Portal, Errancia, Exilio. Worth nothing alone, it
            /// is worth the casts through the network it makes.
            /// </summary>
            public bool LaysPortals => Rows.Any(r => r.EffectId == EffectEngine.ColocaUnPortal);

            /// <summary>How far the spell reaches in this caster's hands.</summary>
            public int ReachFor(Fighter caster) => MaxRange + (RangeBoostable ? Math.Max(0, caster.Range) : 0);

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
        }

        /// <summary>What the board says: where one can stand, who stands where, who sees whom.</summary>
        /// <remarks>A record: the copies the weighing and the plans make of it are <c>with</c> it, and keep all of it.</remarks>
        public sealed record Board
        {
            public Func<int, bool> Walkable { get; init; } = _ => true;
            public Func<int, int, bool> Sees { get; init; } = (_, _) => true;
            public IReadOnlyList<Fighter> Fighters { get; init; } = Array.Empty<Fighter>();

            /// <summary>
            /// What leaving a cell costs the walker holding these AP and MP: the fight's own
            /// tackle rule (<see cref="Tackle"/>), handed in by the fight. Nothing by default.
            /// </summary>
            public Func<Fighter, int, int, int, Tackle.Loss> TackleAt { get; init; } = (_, _, _, _) => Tackle.Loss.None;

            public bool Occupied(int cell) => Fighters.Any(f => f.IsAlive && f.Occupies(cell));

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

            /// <summary>
            /// The fight's round: the rows a buff holds count from the round they start in, and the
            /// base damage a spell has grown -- Ira de Yopuka's +110 a turn of its wait -- is read by it.
            /// </summary>
            public int Round { get; init; }

            /// <summary>
            /// The telefrags of the spell being resolved (FightInstance.Telefrags): the «T» of the
            /// masks reads them, and the plan's copy of the fight writes its own there while it plays a
            /// spell, as the engine does.
            /// </summary>
            public Func<Dictionary<long, long>>? Telefrags { get; init; }

            /// <summary>
            /// A fighter's spells as the tactics weigh them (FightHandler.TacticsOf): what the bot's
            /// summons will cast on their turn -- a turret evolved hits and heals harder.
            /// </summary>
            public Func<Fighter, List<Spell>>? SpellsOf { get; init; }

            /// <summary>
            /// The fight's portals (FightInstance.Portales): a spell aimed at one goes through the
            /// network and lands beyond its way out, harder for the cells between them.
            /// </summary>
            public Func<PortalNetwork>? Portals { get; init; }

            /// <summary>
            /// Tells the fight whether the cast being weighed goes through a portal, which the «R»
            /// and «r» of the masks read (FightInstance.CastThroughPortal): Mofa steals the range
            /// it only takes otherwise, Ridículo takes MP. Only ever on while a cast is weighed.
            /// </summary>
            public Action<bool>? ThroughPortal { get; init; }

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
                => monster.CurrentAP - ActionPoints + (monster.CurrentMP - MovementPoints - Steps);

            /// <summary>The cells walked: going through a portal is no step.</summary>
            public int Steps
            {
                get
                {
                    int steps = 0;
                    for (int i = 1; i < Path.Count; i++)
                        if (MapGeometry.Distance(Path[i - 1], Path[i]) == 1) steps++;
                    return steps;
                }
            }
        }

        /// <summary>A step of the turn: walk this path, then cast this spell at this cell.</summary>
        /// <param name="Score">What it is worth per AP, a little less for every cell walked: what <see cref="Next"/> picks by.</param>
        /// <param name="Value">What it is worth whole, a little less for every cell walked: what a plan adds up.</param>
        /// <param name="ActionPoints">The AP left on arrival, the tackles of the way paid, before the cast.</param>
        /// <param name="MovementPoints">The MP left on arrival.</param>
        public sealed record Action(IReadOnlyList<int> Path, Spell Spell, Fighter Target, int TargetCell, double Score,
                                    double Value = 0, int ActionPoints = 0, int MovementPoints = 0)
        {
            public int From => Path[Path.Count - 1];
        }

        /// <summary>Walking a cell costs a little of what an action is worth: it spends the MP that places the monster afterwards.</summary>
        private const double WalkPenalty = 0.06;

        /// <summary>The next thing worth doing, or null when there is none.</summary>
        public static Action? Next(Board board, Fighter monster, IReadOnlyList<Spell> spells)
        {
            Action? best = null;
            foreach (var option in Options(board, monster, spells))
                if (best == null || option.Score > best.Score) best = option;

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
        /// Everything worth doing from where the monster stands: for each spell and each cell it can
        /// be aimed at, the best place to cast it from, with what it is worth.
        /// </summary>
        /// <remarks>
        /// With <paramref name="movers"/>, a spell that moves somebody (<see cref="Spell.Moves"/>)
        /// is kept when it is worth nothing by itself, each way of casting it from each cell it
        /// can be cast from: the plan plays it and sees where it leaves things. So is one that
        /// switches the caster's own states (<see cref="Spell.Switches"/>), once, and one that
        /// tends its creatures (<see cref="Spell.Tends"/>).
        /// </remarks>
        internal static List<Action> Options(Board board, Fighter monster, IReadOnlyList<Spell> spells, bool movers = false,
                                             IReadOnlyCollection<(int Cell, int From)>? landings = null)
        {
            // Who sees whom does not change between one spell and the next: worked out once per
            // pair of cells for the whole decision, not once per spell.
            var seen = new Dictionary<int, bool>();
            var raw = board.Sees;
            board = board with
            {
                Sees = (from, to) =>
                {
                    int key = from * MapGeometry.MaxCells + to;
                    if (!seen.TryGetValue(key, out bool sees)) seen[key] = sees = raw(from, to);
                    return sees;
                },
            };
            // The portals a spell may be aimed at to go through the network: the ones on.
            var portalCells = board.Portals?.Invoke()?.All.Where(p => p.Active).Select(p => p.Cell).ToList() ?? new List<int>();
            var options = new Dictionary<(int Spell, int Aim, int From), Action>();
            var enemies = Foes(board, monster);
            if (enemies.Count == 0 && Ghosts(board, monster).Count == 0) return new List<Action>();
            var allies = board.Fighters.Where(f => f.IsAlive && f.TeamId == monster.TeamId).ToList();

            var reach = Routes(board, monster);
            var ordered = reach.OrderBy(kv => kv.Value.Path.Count).ThenByDescending(kv => kv.Value.ActionPoints).ToList();
            var cache = new AimCache(board, monster, enemies, landings);
            var mirrored = MirroredFroms(board, monster, portalCells, enemies, reach);

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

                // Nearest cells first, and an aim weighed from the nearest cell it can be cast from:
                // farther away it is worth the same and costs the walk -- unless where it is cast
                // from changes what it does (a line, a cone, a push, a leap), and then from the few
                // nearest. An aim on the cell it is cast from -- a blow around the caster -- is
                // weighed on every cell with somebody within its zone, and on one of the others.
                var tries = new Dictionary<int, int>();
                int triesPerAim = spell.DependsOnWhereFrom ? DirectionalTries : 1;
                bool lonelyDone = false;
                int around = spell.Rows.Count == 0 ? spell.Zone : spell.Rows.Max(r => r.Forma == 'P' ? 0 : r.Tamano);
                // A zone round the caster as wide as the walk itself is much the same from any of
                // its cells: the nearest few are weighed. The Tymador's Último Aliento, nine cells
                // round him, was weighed from every one he could reach, and took a second a plan.
                int selfAims = 0, selfAimCap = around >= WideOwnZone ? WideOwnZoneTries : int.MaxValue;
                foreach (var (cell, route) in ordered)
                {
                    // What the tackles on the way take is not there to cast with, and every
                    // point they take weighs like a cell walked.
                    if (spell.Cost > route.ActionPoints) continue;
                    var path = route.Path;
                    double walk = 1.0 - WalkPenalty * (route.Steps + route.LostToTackles(monster));
                    var aims = spell.HasRows ? AimsByRows(board, spell, monster, cell, enemies, cache) : Targets(spell, monster, cell, enemies, allies, cache);
                    // And the portals that are on: aimed at one, the spell goes through the network.
                    if (portalCells.Count > 0 && spell.HasRows && spell.ThroughPortals)
                        aims = aims.Concat(portalCells.Where(c => c != cell).Select(c => (monster, c))).ToList();
                    foreach (var (target, aim) in aims)
                    {
                        // A cell a cast from here lands somebody on is weighed from here, whatever
                        // nearer cell it was weighed from already: the telefrag needs both from here.
                        bool landing = landings != null && landings.Contains((aim, cell));
                        // Through a portal, where the spell lands depends on where it is cast from:
                        // the vector from the caster to the portal, laid from the way out.
                        bool throughPortal = portalCells.Contains(aim);
                        if (throughPortal && !mirrored.Contains((cell, aim))
                            && tries.TryGetValue(aim, out int portalTries) && portalTries >= PortalTries) continue;
                        if (!landing && !throughPortal && aim != cell && tries.TryGetValue(aim, out int tried) && tried >= triesPerAim) continue;
                        bool lonely = aim == cell && !board.Fighters.Any(f => f.IsAlive && f != monster
                                                                           && MapGeometry.Distance(f.CellId, cell) <= around + 1);
                        if (lonely && lonelyDone) continue;
                        if (aim == cell && !lonely && selfAims >= selfAimCap) continue;
                        if (!CanCast(board, spell, monster, cell, aim, target)) continue;
                        if (aim != cell) tries[aim] = tries.GetValueOrDefault(aim) + 1;
                        if (lonely) lonelyDone = true;
                        else if (aim == cell) selfAims++;
                        double value = ValueOfCast(board, spell, monster, cell, aim, enemies, allies, target);
                        bool leaves = movers && spell.Moves;
                        // A creature of its own tended: only one of its own, the turret it evolves.
                        bool tends = movers && spell.Tends && target != monster && target.TeamId == monster.TeamId && target.EsInvocado;
                        if (value <= 0 && !leaves && !(movers && (spell.Switches || spell.LaysPortals)) && !tends) continue;

                        double score = value / Math.Max(1, spell.Cost) * walk;
                        // A spell that moves somebody does it otherwise from every cell: each of them
                        // is its own option for the plan to play.
                        var key = (spell.Id, aim, leaves || landing || throughPortal ? cell : -1);
                        if (!options.TryGetValue(key, out var known) || score > known.Score)
                            options[key] = new Action(path, spell, target, aim, score, value * walk,
                                                      route.ActionPoints, route.MovementPoints);
                    }
                }
            }
            return options.Values.ToList();
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
                int want = attack!.ReachFor(monster);
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
                // Toward the nearest by the steps it would take to get there, not as the crow flies:
                // the cell nearest as the crow flies is often across a hole or behind crates, a dead
                // end it never leaves -- Feu Follets stood there turn after turn, their turns passed
                // at once, until the player came to them.
                if (chosen.Value == null)
                {
                    var walks = enemies.Select(e => WalkingDistances(board, e.CellId)).ToList();
                    int Steps(int cell)
                    {
                        int best = int.MaxValue;
                        foreach (var walk in walks)
                            if (walk.TryGetValue(cell, out int steps) && steps < best) best = steps;
                        return best == int.MaxValue ? UnreachableSteps + Nearest(cell) : best;
                    }
                    chosen = cells.OrderBy(kv => Steps(kv.Key)).ThenBy(kv => Nearest(kv.Key)).ThenBy(kv => kv.Value.Count).First();
                }
            }
            return chosen.Value;
        }

        /// <summary>What ending the turn out of every enemy's sight is worth, in cells of distance.</summary>
        private const int HiddenWorth = 2;

        /// <summary>Steps counted for a cell no walk leads to from any enemy: behind every reachable one.</summary>
        private const int UnreachableSteps = 1000;

        /// <summary>
        /// How many steps each walkable cell is from <paramref name="from"/>, walking on the board's
        /// floor around holes and obstacles. The fighters are walked through: they move, and a
        /// cell behind one is still the way.
        /// </summary>
        internal static Dictionary<int, int> WalkingDistances(Board board, int from)
        {
            var steps = new Dictionary<int, int> { [from] = 0 };
            var queue = new Queue<int>();
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                int here = queue.Dequeue();
                foreach (int next in MapGeometry.GetNeighbors(here))
                {
                    if (steps.ContainsKey(next) || !board.Walkable(next)) continue;
                    steps[next] = steps[here] + 1;
                    queue.Enqueue(next);
                }
            }
            return steps;
        }

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
                    // Walked onto a portal that is on, the walk stops there and the network takes
                    // him on (FightHandler, StopAtThePortal and CrossPortalAsync): he is at its way
                    // out with what he had left. One crossing a walk: the two portals go off.
                    if (!route.Path.Zip(route.Path.Skip(1)).Any(s => MapGeometry.Distance(s.First, s.Second) > 1)
                        && WayOut(board, monster, next) is int exit)
                    {
                        if (exit == next) continue;
                        candidate = new Route(new List<int>(route.Path) { next, exit }, actionPoints, movementPoints - 1);
                        if (routes.TryGetValue(exit, out var there) && !Better(candidate, there)) continue;
                        routes[exit] = candidate;
                        queue.Enqueue(exit);
                        continue;
                    }
                    if (routes.TryGetValue(next, out var known) && !Better(candidate, known)) continue;
                    routes[next] = candidate;
                    queue.Enqueue(next);
                }
            }
            return routes;
        }

        /// <summary>
        /// The cells a blow through the portals lands ON an enemy from: it comes out at the way
        /// out plus the vector from the caster to the way in (PortalNetwork.Projection), so the
        /// cell to cast from is the way in plus the way out less him. Those are tried whatever
        /// else was: the nearest few cells a portal is tried from hardly ever are the one, and a
        /// Selatrop with its network out struck the enemy straight and never through it.
        /// </summary>
        private static HashSet<(int From, int Entry)> MirroredFroms(Board board, Fighter monster, List<int> portalCells,
                                                                   List<Fighter> enemies, Dictionary<int, Route> reach)
        {
            var mirrored = new HashSet<(int From, int Entry)>();
            var network = board.Portals?.Invoke();
            if (network == null || portalCells.Count < 2 || !CanUsePortals(monster)) return mirrored;
            long Occupant(int cell)
                => board.Fighters.FirstOrDefault(f => f.IsAlive && !f.EstaCargado && f != monster && f.CellId == cell)?.Id ?? 0;
            foreach (int entryCell in portalCells)
            {
                var entry = network.At(entryCell);
                if (entry == null) continue;
                var chain = network.Chain(entry, Occupant, monster.Id);
                if (chain.Count < 2) continue;
                var (ex, ey) = MapGeometry.CellToPoint(entryCell);
                var (ox, oy) = MapGeometry.CellToPoint(chain[chain.Count - 1].Cell);
                foreach (var enemy in enemies)
                {
                    var (tx, ty) = MapGeometry.CellToPoint(enemy.CellId);
                    int from = MapGeometry.PointToCell(ex + ox - tx, ey + oy - ty);
                    if (MapGeometry.IsValid(from) && reach.ContainsKey(from)) mirrored.Add((from, entryCell));
                }
            }
            return mirrored;
        }

        /// <summary>
        /// Where one who walks onto this cell comes out, when a portal that is on stands there and
        /// a network is behind it: its way out (FightHandler.CrossPortalAsync). Null when he just
        /// stands on the cell.
        /// </summary>
        internal static int? WayOut(Board board, Fighter walker, int cell)
        {
            var network = board.Portals?.Invoke();
            if (network == null || network.Count < 2) return null;
            var entry = network.At(cell);
            if (entry == null || !entry.Active || !CanUsePortals(walker)) return null;
            long Occupant(int at) => board.Fighters.FirstOrDefault(f => f.IsAlive && !f.EstaCargado && f != walker && f.CellId == at)?.Id ?? 0;
            var chain = network.Chain(entry, Occupant, walker.Id);
            return chain.Count < 2 ? null : chain[chain.Count - 1].Cell;
        }

        /// <summary>Whether a fighter may go through the portals at all (FightHandler.CanUsePortals).</summary>
        internal static bool CanUsePortals(Fighter who)
            => who.IsAlive && !who.EstaCargado && !who.EsIlusion && !SpellStates.KeepsOutOfPortals(who);

        private static bool Better(Route candidate, Route known)
        {
            if (candidate.MovementPoints != known.MovementPoints) return candidate.MovementPoints > known.MovementPoints;
            if (candidate.ActionPoints != known.ActionPoints) return candidate.ActionPoints > known.ActionPoints;
            return candidate.Steps < known.Steps;
        }

        /// <summary>Whom a spell can be aimed at, and at which cell.</summary>
        private static IEnumerable<(Fighter Target, int Aim)> Targets(Spell spell, Fighter monster, int from,
                                                                     List<Fighter> enemies, List<Fighter> allies,
                                                                     AimCache cache)
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
            {
                foreach (int cell in MapGeometry.GetNeighbors(from)) yield return (monster, cell);
                // A summon that cannot be put next to its caster: the free cells nearest the
                // enemy, in its reach. The Kralamoure's tentacles come out four to twelve cells
                // away, and the cells around him are not walkable: aimed at its neighbours only,
                // he never summoned one.
                if (spell.MinRange > 1)
                    foreach (int cell in cache.Toward)
                    {
                        int distance = MapGeometry.Distance(from, cell);
                        if (distance >= spell.MinRange && distance <= spell.ReachFor(monster)) yield return (monster, cell);
                    }
            }
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
                return board.Walkable(aim) && !board.Occupied(aim)
                       && MapGeometry.Distance(from, aim) >= spell.MinRange
                       && MapGeometry.Distance(from, aim) <= Math.Max(1, spell.MaxRange);

            if (spell.NeedsFreeCell && (board.Occupied(aim) || aim == from || !board.Walkable(aim))) return false;

            int distance = MapGeometry.Distance(from, aim);
            if (distance < spell.MinRange || distance > spell.ReachFor(monster)) return false;
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

            // Cast on itself, a spell for enemies is never aimed at one, and weighed as one it was
            // worth nothing.
            if (spell.Utility > 0 && spell.OnSelf && spell.UtilityOnEnemies)
                value += OwnZoneUtility(spell);
            else if (spell.Utility > 0 && (spell.UtilityOnEnemies ? onEnemy : target == monster))
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
            // And a bomb's combo, as the fight deals it (FightHandler, THE COMBO): from nothing at
            // Combo I to +360% at XV -- the Tymador's Detonador is worth what the bomb has grown.
            int combo = Combo.PercentOf(caster);
            return Math.Max(0, raw * (100 - resistance) / 100.0 * (100 + combo) / 100.0);
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
        /// <summary>
        /// The cells a summon or a trap may be put on, worked out once per decision rather than once
        /// for every cell the caster could cast from: the free ones nearest the enemy, and the ones an
        /// enemy is likeliest to walk on.
        /// </summary>
        private sealed class AimCache
        {
            private readonly Board _board;
            private readonly Fighter _monster;
            private readonly List<Fighter> _enemies;
            private List<int>? _toward;
            private List<(int Cell, double Odds)>? _likely;

            public AimCache(Board board, Fighter monster, List<Fighter> enemies, IReadOnlyCollection<(int Cell, int From)>? landings = null)
            {
                _board = board;
                _monster = monster;
                _enemies = enemies;
                Landings = landings ?? Array.Empty<(int, int)>();
            }

            /// <summary>
            /// The cells the bot's own casts land somebody on, and the cell each is cast from
            /// (MonsterTactics.PlanTurn): a summon cast from that cell is weighed on it too.
            /// </summary>
            public IReadOnlyCollection<(int Cell, int From)> Landings { get; }

            /// <summary>The free cells nearest the enemy, nearest first.</summary>
            public List<int> Toward => _toward ??= _enemies.Count == 0 ? new List<int>()
                : Enumerable.Range(0, MapGeometry.MaxCells)
                    .Where(c => c != _monster.CellId && _board.Walkable(c) && !_board.Occupied(c) && !_board.Trapped(c))
                    .OrderBy(c => _enemies.Min(e => MapGeometry.Distance(c, e.CellId)))
                    .Take(SummonCellsKept).ToList();

            /// <summary>The free cells an enemy is likeliest to walk on, likeliest first.</summary>
            public List<(int Cell, double Odds)> Likely
            {
                get
                {
                    if (_likely != null) return _likely;
                    var threats = Threats(_board, _monster, _enemies);
                    _likely = Enumerable.Range(0, MapGeometry.MaxCells)
                        .Where(c => c != _monster.CellId && _board.Walkable(c) && !_board.Occupied(c) && !_board.Trapped(c))
                        .Select(c => (Cell: c, Odds: threats.Count == 0 ? 0 : threats.Max(t => TrapOdds(_board, new[] { c }, t.Cell, t.MP, _monster.CellId))))
                        .Where(c => c.Odds > 0)
                        .OrderByDescending(c => c.Odds).Take(TrapCellsKept).ToList();
                    return _likely;
                }
            }
        }

        /// <summary>How many cells an aim is weighed from when where it is cast from matters.</summary>
        private const int DirectionalTries = 3;

        /// <summary>The zone shapes drawn from the caster's side: lines, the perpendicular, cones, half circles.</summary>
        private static readonly HashSet<char> DirectionalShapes = new() { 'L', 'l', 'T', 'V', 'U', 'W', '-', '/', 'A', 'a' };

        /// <summary>The ways back: to the cell before the last move (1100), the turn's start (1099), the fight's (784); and the teleport-or-swap (1101).</summary>
        private static readonly HashSet<int> Returning = new() { 1100, 1099, 784, 1101 };

        /// <summary>The effects that move someone, which way depending on where the caster stands.</summary>
        private static readonly HashSet<int> Moving = new()
        {
            EffectSupport.Push, EffectSupport.Pull, EffectSupport.Teleport, EffectSupport.SwapPositions,
            EffectSupport.PushWithoutDamage, EffectSupport.PushToTargetCell, EffectSupport.PullToTargetCell,
            EffectSupport.StepBack, EffectSupport.StepForward, 1021, 1022, 1023, 1104, 1105, 1106,
            EffectSupport.Carry, EffectSupport.Throw,
        };

        /// <summary>How many of the cells nearest the enemy are kept for summons, and of the likeliest for traps.</summary>
        private const int SummonCellsKept = 40;
        private const int TrapCellsKept = 40;

        private static IEnumerable<(Fighter Target, int Aim)> AimsByRows(Board board, Spell spell, Fighter monster,
                                                                          int from, List<Fighter> enemies, AimCache? cache = null)
        {
            cache ??= new AimCache(board, monster, enemies);
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

                // A portal goes by the enemy, its way out, and by the caster, its way in.
                if (spell.LaysPortals)
                    foreach (int cell in MapGeometry.GetNeighbors(from))
                        if (board.Walkable(cell) && !board.Occupied(cell)) aims.Add(cell);

                // A throw lands what the caster carries on a free cell: next to it, to pick him up
                // again, and as far as it goes, out of his way.
                if (monster.Carrying != 0 && spell.Rows.Any(r => r.EffectId == EffectSupport.Throw))
                {
                    int reach = spell.ReachFor(monster);
                    var free = Enumerable.Range(0, MapGeometry.MaxCells)
                        .Where(c => { int d = MapGeometry.Distance(from, c); return d >= Math.Max(1, spell.MinRange) && d <= reach; })
                        .Where(c => board.Walkable(c) && !board.Occupied(c))
                        .ToList();
                    foreach (int cell in free.Where(c => MapGeometry.Distance(from, c) <= 1)) aims.Add(cell);
                    foreach (int cell in free.OrderByDescending(c => enemies.Where(e => e.Id != monster.Carrying)
                                                                           .Select(e => -MapGeometry.Distance(c, e.CellId))
                                                                           .DefaultIfEmpty(MapGeometry.Distance(from, c)).Max())
                                             .Take(ThrowCellsTried))
                        aims.Add(cell);
                }

                // A trap goes where the enemy is likely to walk: the free cells in its reach
                // with the best odds of being stepped on.
                if (spell.LaysTraps)
                {
                    int reach = spell.ReachFor(monster);
                    var likely = cache.Likely
                        .Where(c => c.Cell != from)
                        .Where(c => { int d = MapGeometry.Distance(from, c.Cell); return d >= spell.MinRange && d <= reach; })
                        .Take(TrapCellsTried);
                    foreach (var (cell, _) in likely) aims.Add(cell);
                }

                if (spell.SummonsByRow && enemies.Count > 0)
                {
                    int reach = spell.ReachFor(monster);
                    var toward = cache.Toward
                        .Where(c => c != from)
                        .Where(c => { int d = MapGeometry.Distance(from, c); return d >= spell.MinRange && d <= reach; })
                        .Take(SummonCellsTried);
                    foreach (int cell in toward) aims.Add(cell);
                    // And where a bomb raises a wall over the enemy, with one of its kind already out.
                    foreach (int template in spell.Rows.Where(r => EffectEngine.EsInvocacion(r.EffectId) && BombWalls.WallSpell.ContainsKey(r.DiceNum))
                                                       .Select(r => r.DiceNum).Distinct())
                        foreach (int cell in WallCellsOver(board, monster, template, enemies))
                        {
                            int d = MapGeometry.Distance(from, cell);
                            if (cell != from && d >= spell.MinRange && d <= reach) aims.Add(cell);
                        }
                    // And where its own casts from here will land somebody: a creature there is a telefrag.
                    foreach (var (cell, castFrom) in cache.Landings)
                    {
                        if (castFrom != from) continue;
                        int d = MapGeometry.Distance(from, cell);
                        if (cell != from && d >= spell.MinRange && d <= reach && board.Walkable(cell) && !board.Occupied(cell))
                            aims.Add(cell);
                    }
                }
            }

            foreach (int aim in aims)
            {
                var there = aim == from ? monster
                    : board.Fighters.FirstOrDefault(f => f.IsAlive && f != monster && f.CellId == aim && !Unseen(f));
                yield return (there ?? monster, aim);
            }
        }

        /// <summary>How many far cells a throw is weighed on, besides the ones next to the caster.</summary>
        private const int ThrowCellsTried = 4;

        /// <summary>How wide a zone round the caster has to be to be weighed from its nearest cells only, and from how many.</summary>
        private const int WideOwnZone = 5;
        private const int WideOwnZoneTries = 3;

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
            // The states this cast puts, as it goes: what it casts after them sees them, as the
            // engine resolves a sub-cast (EffectEngine.Resolver, «estadosAlEmpezar»), and its own
            // rows do not. Emboscada marks the enemy (130) and its turrets' specials strike whoever
            // carries the mark: weighed without it, they struck nobody.
            var marked = new List<(Fighter Who, int State)>();

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

                    if (id is KillsAndReplaces or KillsAndReplacesBySummon)
                    {
                        // A creature of its own side turned into another (EffectEngine, MataYReemplaza):
                        // the Sadida's tree made a doll. A summon where the tree stood.
                        var replaced = ReachOf(board, row, caster, castFrom, castAim).FirstOrDefault(w => w.TeamId == monster.TeamId && w.EsInvocado);
                        if (replaced != null && !summoned && row.DiceNum > 0 && EffectEngine.CasterMeets(caster, row))
                        {
                            value += p * (SummonStanding(monster, caster, row) + 15 * (Nearest(castFrom) - Nearest(replaced.CellId)));
                            summoned = true;
                        }
                        continue;
                    }

                    bool twin = id == EffectEngine.InvocaUnDoble;
                    if (EffectEngine.EsInvocacion(id) || twin)
                    {
                        // Put down where it was aimed: a free cell, and the nearer the enemy the
                        // better. It plays every turn from then on, so it is worth a blow or two --
                        // when the fight's limit lets it out at all --, the more the longer it
                        // stands (SummonStanding); what it deals is counted at the plan's end. A
                        // double -- the Sram's, a copy of it that locks and takes the blows -- goes
                        // out the same way; the engine does not count it against the limit.
                        if (!summoned && castAim != castFrom && board.Walkable(castAim) && !board.Occupied(castAim)
                            && !board.Trapped(castAim) && EffectEngine.CasterMeets(caster, row)
                            && (twin || board.CanSummon == null || board.CanSummon(caster, row.DiceNum, Math.Max(1, row.DiceSide))))
                        {
                            value += p * ((twin ? 150 + 1.5 * monster.Level : SummonStanding(monster, caster, row))
                                          + 15 * (Nearest(castFrom) - Nearest(castAim)));
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
                    bool damage = IsDamage(id);
                    if (damage && ghosts.Count > 0 && HitsEnemies(row))
                    {
                        var zone = EffectEngine.CasillasDelEfecto(row, castFrom, castAim);
                        foreach (var (ghost, seen) in ghosts)
                        {
                            if (!zone.Contains(seen)) continue;
                            double guess = Blow(Average(row), ElementOf(id, caster), caster, ghost) * p * GuessOdds;
                            blows.TryGetValue(ghost, out double had);
                            blows[ghost] = had + guess;
                        }
                    }
                    if (reached.Count == 0) continue;

                    if (damage)
                    {
                        double dice = Average(row) + BaseBonus(board, caster, spellId);
                        var element = ElementOf(id, caster);
                        foreach (var who in reached)
                        {
                            double blow = Shielded(caster, castFrom, who) ? 0 : Blow(dice, element, caster, who) * p;
                            if (who.TeamId != monster.TeamId)
                            {
                                blows.TryGetValue(who, out double had);
                                blows[who] = had + blow;
                                // Life stolen: half of it back to whoever stole it.
                                if (StealsLife(id) && caster.TeamId == monster.TeamId)
                                {
                                    heals.TryGetValue(caster, out double healed);
                                    heals[caster] = healed + blow / 2;
                                }
                            }
                            else
                            {
                                // One of its own that hands blows back -- the Yopuka's strategist --
                                // is struck on purpose: it takes its share of the blow, and what it
                                // takes goes to every enemy around it. Any other of its own, never.
                                var echo = EchoOf(who);
                                if (echo.Share > 0 && who.EsInvocado)
                                {
                                    double taken = Math.Min(blow * echo.AllyDamageTaken, who.CurrentHP);
                                    value -= taken * EchoSelfCost;
                                    foreach (var enemy in enemies.Where(e => MapGeometry.Distance(e.CellId, who.CellId) <= echo.Radius))
                                    {
                                        blows.TryGetValue(enemy, out double had);
                                        blows[enemy] = had + taken * echo.Share;
                                    }
                                }
                                else value -= blow * (who.EsInvocado ? 1.0 : 2.0);
                            }
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
                        if (depth >= ValueDepth || row.DiceNum <= 0) continue;
                        var child = SpellEffects.De(row.DiceNum, Math.Max(1, row.DiceSide));
                        // The states put so far, on for what it casts, and off again after.
                        var applied = marked.Distinct().Where(m => !m.Who.Buffs.TieneEstado(m.State)).ToList();
                        foreach (var (who, state) in applied) who.Buffs.PonerEstado(state);
                        try
                        {
                            // Where the bot stands is where it would cast from, not where it is now.
                            foreach (var (childCaster, childFrom, childAim) in SubCasts(row, caster, castFrom, castAim,
                                                                                        reached, w => w == monster ? from : w.CellId))
                                Rows(row.DiceNum, child, childCaster, childFrom, childAim, p * SubCastShare, depth + 1);
                        }
                        finally
                        {
                            foreach (var (who, state) in applied) who.Buffs.QuitarEstado(state);
                        }
                        continue;
                    }

                    if (id == EffectEngine.ActivarBomba)
                    {
                        // «Activa una bomba»: each bomb it reaches casts its own explosion, from its
                        // cell and at its grade (EffectEngine, ActivarBomba) -- the Tymador's
                        // Detonador on a bomb by the enemy is the bomb's blow on him.
                        if (depth >= ValueDepth) continue;
                        foreach (var bomb in BombChain(board.Fighters, reached))
                        {
                            int explosion = Bombs.Explosion(bomb.MonsterId);
                            if (explosion == 0) continue;
                            Rows(explosion, SpellEffects.De(explosion, Math.Max(1, bomb.GradeIndex)), bomb, bomb.CellId, bomb.CellId,
                                 p, depth + 1);
                        }
                        continue;
                    }

                    if (id is EffectEngine.ShieldPerLevel or EffectEngine.ShieldPerLife)
                    {
                        // A shield: life the next blows take first, worth as life is to whom gets it.
                        foreach (var who in reached)
                        {
                            if (who.TeamId != monster.TeamId) continue;
                            double shield = id == EffectEngine.ShieldPerLevel ? caster.Level * Average(row) / 100.0
                                                                              : who.MaxHP * Average(row) / 100.0;
                            value += shield * LifeWorth(who) * ShieldShare * p * (who.EsInvocado ? 0.5 : 1.0);
                        }
                        continue;
                    }

                    if (id == DamageTakenTimes)
                    {
                        // «Daños sufridos x#1%»: on an enemy above a hundred, every blow of its side
                        // worth that much more while it lasts; on one of its own below a hundred,
                        // the enemy's blows that much less.
                        double share = (Average(row) - 100) / 100.0;
                        foreach (var who in reached)
                        {
                            bool mine = who.TeamId == monster.TeamId;
                            if (mine && share < 0)
                                value += -share * IncomingPerTurn(enemies) * LifeWorth(who) * (1 + FutureTurns(row.Duration)) * p;
                            else if (!mine && share > 0)
                                value += share * TurnDamageOf(monster) * (0.5 + FutureTurns(row.Duration)) * p;
                        }
                        continue;
                    }

                    if (id == DamageTakenLess)
                    {
                        // «-#1 de daños recibidos»: that much off each enemy blow, three or four a turn.
                        foreach (var who in reached.Where(w => w.TeamId == monster.TeamId))
                            value += Average(row) * BlowsPerTurn * LifeWorth(who) * (1 + FutureTurns(row.Duration)) * p;
                        continue;
                    }

                    if (id == EffectSupport.AddState && row.Value != 0)
                        foreach (var who in reached) marked.Add((who, row.Value));

                    if (id == EffectSupport.AddState && row.Duration >= 1)
                    {
                        // A state is mostly what the rows that read it do -- the plan plays them --,
                        // but some are worth by themselves: out of reach on its own side, unable to
                        // strike on the enemy's, for the enemy's coming turn.
                        var state = SpellStates.Of(row.Value);
                        if (state == null) continue;
                        foreach (var who in reached)
                        {
                            bool mine = who.TeamId == monster.TeamId;
                            if (mine && !who.Buffs.TieneEstado(row.Value))
                            {
                                double shielded = state.Invulnerable ? 1.0 : (state.InvulnerableMelee || state.InvulnerableRange) ? 0.5 : 0;
                                value += shielded * IncomingPerTurn(enemies) * LifeWorth(who) * p * (who.EsInvocado ? 0.3 : 1.0);
                            }
                            else if (!mine && state.CantDealDamage)
                                value += IncomingPerTurn(new List<Fighter> { who }) * LifeWorth(monster) * p;
                        }
                        continue;
                    }

                    if (id == ShortensEffects)
                    {
                        // Effects that end sooner: half of what a dispel takes off, on whom it reaches.
                        foreach (var who in reached)
                        {
                            double worth = 0.5 * BuffsWorth(who) * p;
                            value += who.TeamId == monster.TeamId ? -worth : worth;
                        }
                        continue;
                    }

                    if (id == EffectEngine.DesvelaLosInvisibles)
                    {
                        // An invisible enemy in the zone is seen again: no more guessing where to strike.
                        var zone = EffectEngine.CasillasDelEfecto(row, castFrom, castAim);
                        foreach (var (ghost, seen) in ghosts)
                            if (zone.Contains(seen) || zone.Contains(ghost.CellId)) value += TurnDamageOf(monster) * 0.3 * p;
                        continue;
                    }

                    if (id == Erodes)
                    {
                        // Erosion: a share of every blow it takes stays off its maximum for good.
                        foreach (var who in reached.Where(w => w.TeamId != monster.TeamId))
                            value += Average(row) / 100.0 * TurnDamageOf(monster) * ErosionShare * p;
                        continue;
                    }

                    if (id == EffectSupport.Push && depth == 0)
                    {
                        // A push into something: the crash's damage on whom it reaches, as the
                        // engine reckons it (EffectEngine.DanoDeColision).
                        foreach (var who in reached.Where(w => w.TeamId != monster.TeamId && w != caster))
                        {
                            int short_ = Pushed(board, who.CellId, castFrom, Math.Max(1, row.DiceNum), away: true).Short;
                            if (short_ <= 0) continue;
                            double crash = EffectEngine.DanoDeColision(caster.Level, caster.PushDamage, 0, short_);
                            blows.TryGetValue(who, out double had);
                            blows[who] = had + crash * p;
                        }
                        continue;
                    }

                    if (id == Dispel)
                    {
                        // What it takes off: the enemy's buffs, worth what they would have done; one
                        // of its own side's, the same lost.
                        foreach (var who in reached)
                        {
                            double worth = BuffsWorth(who) * p;
                            value += who.TeamId == monster.TeamId ? -worth : worth;
                        }
                        continue;
                    }

                    if (id is EffectSupport.VitalityPercentBonus or EffectSupport.VitalityPercentBoost
                        or EffectSupport.VitalityFlatBonus)
                    {
                        // Life given, the percentage of it on its own maximum: Vitalidad's +20 % is
                        // 1,333 of a JondoBot's 6,666. Worth more the less life is left.
                        foreach (var who in reached)
                        {
                            if (who.TeamId != monster.TeamId || AlreadyHas(who, spellId)) continue;
                            double life = id == EffectSupport.VitalityFlatBonus ? Average(row) : who.MaxHP * Average(row) / 100.0;
                            value += life * LifeWorth(who) * p * (who.EsInvocado ? 0.5 : 1.0);
                        }
                        continue;
                    }

                    var (characteristic, sign) = DatabaseManager.EffectMeta(id);
                    if (characteristic > 0 && sign != 0 && Raising.Contains(characteristic) && sign > 0)
                    {
                        // A buff that makes the blows hit harder, weighed by the turns it lasts
                        // after this one: what it does to the blows of this turn is in the plan,
                        // which plays them with it on (MonsterTactics.Planner).
                        foreach (var who in reached)
                        {
                            if (who.TeamId != monster.TeamId || AlreadyHas(who, spellId)) continue;
                            value += Average(row) * PerPointPerTurn(who, characteristic) * FutureTurns(row.Duration) * p
                                     * (who.EsInvocado ? 0.3 : 1.0);
                        }
                        continue;
                    }
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

        /// <summary>What the life a creature that hands blows back loses to them is worth, as a share of it.</summary>
        private const double EchoSelfCost = 0.3;

        /// <summary>
        /// The base damage a spell has gained on its caster (293, «+#3 de daños básicos»): what Ira de
        /// Yopuka adds to itself each turn it waits, the Ocra's charged arrows.
        /// </summary>
        internal static int BaseBonus(Board board, Fighter caster, int spellId)
            => spellId <= 0 ? 0 : SpellModifiers.Total(caster, spellId, SpellAspect.DanoBase, board.Round);

        /// <summary>Whether an effect deals damage: the ten of an element (91-100), and the best and worst element's.</summary>
        internal static bool IsDamage(int effect)
            => (effect >= EffectSupport.FirstDamage && effect <= EffectSupport.LastDamage)
               || effect is BestElementDamage or WorstElementDamage or BestElementLifeSteal;

        /// <summary>Whether a damage effect gives the caster life: 91-95, and the best element's theft.</summary>
        internal static bool StealsLife(int effect) => effect <= LastLifeSteal || effect == BestElementLifeSteal;

        /// <summary>The element a damage effect deals: its own, or the caster's best or worst.</summary>
        internal static ElementType ElementOf(int effect, Fighter caster)
        {
            if (effect is BestElementDamage or BestElementLifeSteal or WorstElementDamage)
            {
                var elements = new[]
                {
                    (ElementType.Earth, caster.Strength), (ElementType.Fire, caster.Intelligence),
                    (ElementType.Water, caster.Chance), (ElementType.Air, caster.Agility),
                };
                return effect == WorstElementDamage
                    ? elements.OrderBy(e => e.Item2).First().Item1
                    : elements.OrderByDescending(e => e.Item2).First().Item1;
            }
            return ElementOfDamage(effect);
        }

        /// <summary>«Daños del mejor elemento» (2822), «del peor» (2832), and life stolen with the best (2828).</summary>
        private const int BestElementDamage = 2822;
        private const int WorstElementDamage = 2832;
        private const int BestElementLifeSteal = 2828;

        /// <summary>«Daños sufridos x#1%» (1163) and «-#1 de daños recibidos» (265).</summary>
        private const int DamageTakenTimes = 1163;
        private const int DamageTakenLess = 265;

        /// <summary>«Duración de los efectos: -#1» (1075).</summary>
        private const int ShortensEffects = 1075;

        /// <summary>«#1% daños sufridos incurables» (776), erosion.</summary>
        private const int Erodes = 776;

        /// <summary>What share of a turn of blows a point of erosion is worth: it pays off over the fight.</summary>
        private const double ErosionShare = 0.3;

        /// <summary>A shield is life that goes when the effect ends: a little less than life.</summary>
        private const double ShieldShare = 0.8;

        /// <summary>The blows a turn has, three or four.</summary>
        private const double BlowsPerTurn = 3.5;

        /// <summary>A turn of a fighter's blows, roughly: ten of base damage per AP, raised by its best element and its power.</summary>
        internal static double TurnDamageOf(Fighter who)
        {
            int best = Math.Max(Math.Max(who.Strength, who.Intelligence), Math.Max(who.Chance, who.Agility));
            return Math.Max(6, Math.Max(who.MaxAP, who.CurrentAP)) * BaseDamagePerAp * (100 + Math.Max(0, best) + Math.Max(0, who.Power)) / 100.0;
        }

        /// <summary>The blows the enemy's coming turn threatens: the strongest of them, a turn of his.</summary>
        internal static double IncomingPerTurn(IReadOnlyList<Fighter> enemies)
            => enemies.Count == 0 ? 0 : enemies.Max(TurnDamageOf);

        /// <summary>
        /// Where a push or a pull leaves whom it moves, and how many of its cells it could not go:
        /// straight away from the caster, or toward him, one cell at a time, stopped by what cannot
        /// be walked or by anybody.
        /// </summary>
        /// <param name="stops">
        /// Cells that stop the one moved dead on them: the bomb walls that would catch him --
        /// «desplazar una entidad a un muro detendrá su desplazamiento y le infligirá daños».
        /// </param>
        internal static (int Cell, int Short) Pushed(Board board, int cell, int from, int cells, bool away,
                                                     IReadOnlyCollection<int>? stops = null)
        {
            var (x, y) = MapGeometry.CellToPoint(cell);
            var (fx, fy) = MapGeometry.CellToPoint(from);
            int dx = Math.Sign(x - fx), dy = Math.Sign(y - fy);
            if (!away) { dx = -dx; dy = -dy; }
            if (dx == 0 && dy == 0) return (cell, 0);
            int at = cell;
            for (int step = 0; step < cells; step++)
            {
                var (cx, cy) = MapGeometry.CellToPoint(at);
                int next = MapGeometry.PointToCell(cx + dx, cy + dy);
                if (!MapGeometry.IsValid(next) || !board.Walkable(next) || board.Occupied(next) || next == from)
                    return (at, cells - step);
                at = next;
                if (stops != null && stops.Contains(at)) return (at, 0);
            }
            return (at, 0);
        }

        /// <summary>«Desembruja al objetivo» (132): the dispellable rows of whom it reaches, off.</summary>
        private const int Dispel = 132;

        /// <summary>
        /// Whether a blow of the caster's does nothing to this one: a state shields him from it at
        /// that distance -- Influencia's Invulnerable (269), Duelo Yopukil's at range (375) --, or a
        /// state of the caster's keeps him from dealing damage at all.
        /// </summary>
        internal static bool Shielded(Fighter caster, int from, Fighter who)
            => SpellStates.ShieldsFromBlow(who, MapGeometry.Distance(from, who.CellId))
               || SpellStates.KeepsFromDealingDamage(caster);

        /// <summary>The characteristics that make one's blows hit harder: power, damage and the four elements.</summary>
        private static readonly HashSet<int> Raising = new() { 25, 16, 10, 13, 14, 15 };

        /// <summary>
        /// What one point of a characteristic that raises the blows adds to a turn of them: a turn of
        /// a fighter's blows as roughly ten of base damage per AP, raised by its best element and its
        /// power -- some 1,900 for a JondoBot --, and a point of power or of the element its share of
        /// it; a point of damage is a point on each of the three or four blows of a turn.
        /// </summary>
        internal static double PerPointPerTurn(Fighter who, int characteristic)
        {
            int best = Math.Max(Math.Max(who.Strength, who.Intelligence), Math.Max(who.Chance, who.Agility));
            double multiplier = 100 + Math.Max(0, best) + Math.Max(0, who.Power);
            double turn = Math.Max(6, Math.Max(who.MaxAP, who.CurrentAP)) * BaseDamagePerAp * multiplier / 100.0;
            return characteristic == 16 ? Math.Max(6, who.MaxAP) / 3.0 : turn / multiplier;
        }

        /// <summary>Base damage per AP of a class's blows, before the characteristics: about thirty for three AP.</summary>
        private const double BaseDamagePerAp = 10;

        /// <summary>
        /// The turns after this one a buff of this duration is still on, each a little less sure than
        /// the one before: Potencia's three are two more turns, worth 0.8 and 0.64.
        /// </summary>
        internal static double FutureTurns(int duration)
        {
            double total = 0, weight = FutureDiscount;
            for (int turn = 1; turn < Math.Min(duration, 5); turn++, weight *= FutureDiscount) total += weight;
            return total;
        }

        private const double FutureDiscount = 0.8;

        /// <summary>
        /// What a point of life given back or added is worth to whom gets it: little at full life, a
        /// point for a point at half of it, half as much again below that.
        /// </summary>
        internal static double LifeWorth(Fighter who)
        {
            double missing = who.MaxHP <= 0 ? 0 : 1.0 - (double)who.CurrentHP / who.MaxHP;
            return 0.25 + 1.5 * missing;
        }

        /// <summary>
        /// What a fighter's dispellable rows are worth to him: each characteristic by its weight for
        /// the turns it has left, a state that keeps him out of reach -- invulnerable -- as much as a
        /// turn of blows, and a malus he carries counted against.
        /// </summary>
        internal static double BuffsWorth(Fighter who)
        {
            double worth = 0;
            foreach (var row in who.Buffs.Puestos)
            {
                if (row.Dispellable != 1 || row.Pendiente) continue;
                double turns = Math.Max(1, row.Duracion);
                if (row.Estado != 0 && row.EffectId == EffectSupport.AddState)
                {
                    var state = SpellStates.Of(row.Estado);
                    if (state != null && (state.Invulnerable || state.InvulnerableMelee || state.InvulnerableRange))
                        worth += 0.6 * PerPointPerTurn(who, 25) * 1000;
                    continue;
                }
                if (row.Caracteristica <= 0 || row.Cuanto == 0) continue;
                double each = Raising.Contains(row.Caracteristica) ? PerPointPerTurn(who, row.Caracteristica) : StatWeight(row.Caracteristica);
                worth += row.Cuanto * each * Math.Min(turns, 3);
            }
            return worth;
        }

        /// <summary>
        /// The children a sub-cast row brings out, as the engine resolves them (EffectEngine.SubCastOf):
        /// who casts each, from where, at what -- the candidate its mask picked, the parent's caster,
        /// the parent's cell, the nearest one; and only so many when its value caps them. The Steamer's
        /// Evolución has its turret pass the evolution back to him and he casts it at the turret
        /// (1017, «at the parent's caster»): read as «at the candidate», the turret's states landed
        /// on the Steamer.
        /// </summary>
        internal static IEnumerable<(Fighter Caster, int From, int Aim)> SubCasts(SpellEffect row, Fighter caster, int from, int aim,
                                                                               IReadOnlyList<Fighter> reached,
                                                                               Func<Fighter, int>? cellOf = null)
        {
            cellOf ??= w => w.CellId;
            var rule = EffectEngine.SubCastOf(row.EffectId);
            var candidates = reached.Where(w => w.IsAlive).ToList();
            if (rule.CappedByValue && row.Value > 0 && row.Value < 999 && candidates.Count > row.Value)
                candidates = candidates.Take(row.Value).ToList();
            if (rule.Aim == EffectEngine.SubCastAim.Nearest && candidates.Count > 1)
                candidates = candidates.OrderBy(w => aim >= 0 ? MapGeometry.Distance(aim, cellOf(w)) : 0).ThenBy(w => w.Id).Take(1).ToList();
            foreach (var candidate in candidates)
            {
                var childCaster = rule.ByTheOrigin ? caster : rule.ByTheCandidate ? candidate : caster;
                int childFrom = childCaster == caster ? from : cellOf(childCaster);
                int childAim = rule.Aim switch
                {
                    EffectEngine.SubCastAim.ParentCaster or EffectEngine.SubCastAim.Origin => from,
                    EffectEngine.SubCastAim.ParentCell => aim >= 0 ? aim : cellOf(candidate),
                    _ => cellOf(candidate),
                };
                yield return (childCaster, childFrom, childAim);
            }
        }

        /// <summary>
        /// The bombs a detonation sets off: the ones it reaches, and every bomb walled to one of them,
        /// and theirs -- «si una bomba está unida a otras por un muro y explota, hará explotar también
        /// a las otras bombas del muro» (EffectEngine, ActivarBomba). Worked out before any goes off:
        /// a bomb that has blown up holds no wall.
        /// </summary>
        internal static List<Fighter> BombChain(IEnumerable<Fighter> everybody, IEnumerable<Fighter> reached)
        {
            var all = everybody.ToList();
            var chain = new List<Fighter>();
            var queue = new Queue<Fighter>(reached.Where(b => b.IsAlive));
            while (queue.Count > 0)
            {
                var bomb = queue.Dequeue();
                if (chain.Contains(bomb)) continue;
                chain.Add(bomb);
                foreach (var other in BombWalls.LasDelMismoMuro(all, bomb)) queue.Enqueue(other);
            }
            return chain;
        }

        /// <summary>
        /// Where a bomb raises a wall over an enemy: in line with a bomb of its kind the caster has
        /// out, on the far side of the enemy, between two and seven cells from it -- the wall runs
        /// between the two and hits him as it is raised, and again when his turn begins inside.
        /// </summary>
        private static IEnumerable<int> WallCellsOver(Board board, Fighter monster, int template, List<Fighter> enemies)
        {
            var bombs = board.Fighters.Where(f => f.IsAlive && f.EsInvocado && f.Invocador == monster.Id && f.MonsterId == template).ToList();
            foreach (var bomb in bombs)
            {
                var (bx, by) = MapGeometry.CellToPoint(bomb.CellId);
                foreach (var enemy in enemies)
                {
                    var (ex, ey) = MapGeometry.CellToPoint(enemy.CellId);
                    if (bx != ex && by != ey) continue;
                    int dx = Math.Sign(ex - bx), dy = Math.Sign(ey - by);
                    for (int k = 1; k <= BombWalls.MaxGap; k++)
                    {
                        int cell = MapGeometry.PointToCell(ex + dx * k, ey + dy * k);
                        if (!MapGeometry.IsValid(cell)) break;
                        int gap = MapGeometry.Distance(bomb.CellId, cell);
                        if (gap > BombWalls.MaxGap) break;
                        if (gap >= BombWalls.MinGap && board.Walkable(cell) && !board.Occupied(cell)) yield return cell;
                    }
                }
            }
        }

        /// <summary>The life from which a summon counts whole for standing on the board: a turret, a doll.</summary>
        private const double SturdySummonLife = 1500;

        /// <summary>
        /// What a creature put down is worth for standing on the board -- blocking, taking blows,
        /// being there to cast its own --: a blow or two of the bot's, the more the longer it lasts.
        /// A 315-life spider (Invocación de Arakna) was worth as much as a 1,890-life turret, and
        /// every class opened its turn with one.
        /// </summary>
        private static double SummonStanding(Fighter monster, Fighter caster, SpellEffect row)
        {
            double whole = 150 + 1.5 * monster.Level;
            var recipe = Summons.De(row.DiceNum, Math.Max(1, row.DiceSide));
            if (recipe == null) return whole;
            int life = Summons.VidaDelInvocado(recipe.Vida, caster.StatLevel, recipe.VidaFija);
            return whole * (0.3 + 0.7 * Math.Min(1, life / SturdySummonLife));
        }

        /// <summary>How many cells a cast through one portal is weighed from: where it lands moves with each.</summary>
        private const int PortalTries = 6;

        /// <summary>A cast through the portals: where it lands, the way out it comes from, the bonus of the network.</summary>
        internal readonly record struct Projection(int Landing, int Exit, int Bonus);

        /// <summary>
        /// Where a spell cast from a cell at a portal comes out, as the fight projects it
        /// (FightHandler.Projection): the portal on, a network behind it to go through, the caster
        /// one the portals let in; the vector from the caster to the portal laid from the way out,
        /// and «+#3% daños, +#1% de daños por casilla que separe entre 2 portales». Null when the
        /// spell would land on the portal itself.
        /// </summary>
        internal static Projection? ThroughPortals(Board board, Spell spell, Fighter caster, int from, int aim)
        {
            if (!spell.ThroughPortals || board.Portals == null) return null;
            var network = board.Portals();
            if (network == null || network.Count < 2) return null;
            var entry = network.At(aim);
            if (entry == null || !entry.Active || !CanUsePortals(caster)) return null;
            long Occupant(int cell)
                => board.Fighters.FirstOrDefault(f => f.IsAlive && !f.EstaCargado && (f == caster ? from : f.CellId) == cell)?.Id ?? 0;
            var chain = network.Chain(entry, Occupant);
            if (chain.Count < 2) return null;
            int exit = chain[chain.Count - 1].Cell;
            int lands = PortalNetwork.Projection(from, entry.Cell, exit);
            if (!MapGeometry.IsValid(lands)) return null;
            return new Projection(lands, exit, PortalNetwork.BonusPercent(chain));
        }

        /// <summary>
        /// What a cast is worth, from a cell at a cell: weighed row by row where it lands -- beyond
        /// the portals' way out when it is aimed at one, as if cast from there, its damage and
        /// healing grown by the network's bonus, the masks' «R» told so --, or by its summary.
        /// </summary>
        internal static double ValueOfCast(Board board, Spell spell, Fighter monster, int from, int aim,
                                           List<Fighter> enemies, List<Fighter> allies, Fighter target)
        {
            if (!spell.HasRows) return Value(spell, monster, target, aim, from, enemies, allies, board);
            if (ThroughPortals(board, spell, monster, from, aim) is { } through)
            {
                board.ThroughPortal?.Invoke(true);
                try
                {
                    return ValueOfRows(board, spell, monster, through.Exit, through.Landing, enemies) * (100 + through.Bonus) / 100.0;
                }
                finally
                {
                    board.ThroughPortal?.Invoke(false);
                }
            }
            double byRows = ValueOfRows(board, spell, monster, from, aim, enemies);
            return byRows > 0 ? byRows : OwnZoneUtility(spell);
        }

        /// <summary>
        /// A spell cast on itself for the enemies round it, worth its utility every turn. The
        /// tactirelle's and the chalutier's put a state round them and push or pull: row by row
        /// that is worth nothing, and they passed every turn. In the game they cast it every turn,
        /// whoever stands round them.
        /// </summary>
        private static double OwnZoneUtility(Spell spell)
            => spell.Utility > 0 && spell.OnSelf && spell.UtilityOnEnemies ? spell.Utility : 0;

        /// <summary>«Mata al objetivo y lo sustituye por la invocación» (405), and its twin (2796).</summary>
        internal const int KillsAndReplaces = 405;
        internal const int KillsAndReplacesBySummon = 2796;

        /// <summary>How deep a spell's sub-casts are followed, and how much of theirs counts.</summary>
        /// <remarks>
        /// Four: the Zobal's masks take the mask he wears off three sub-spells down (18634 at
        /// grade 1, then 2, then 5); followed two deep, every mask he put on stayed on.
        /// </remarks>
        private const int SubCastDepth = 4;

        /// <summary>
        /// How deep the weighing of a spell (<see cref="ValueOfRows"/>) follows its sub-casts, as
        /// deep as the copy of the fight: the Steamer's Emboscada strikes three sub-casts down --
        /// its activation, the turret's special, the turret's blow.
        /// </summary>
        private const int ValueDepth = SubCastDepth;
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
            // The damage percentages, a point on every blow dealt; and the ones taken, which a
            // resistance lowers (DamagePercentages), a point on every blow taken.
            120 or 122 or 123 or 125 => 3,
            121 or 124 or 141 or 142 => -3,
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
            // A starred mask gates the whole row, including any sub-casts it would expand.
            if (!EffectEngine.CasterMeets(caster, row)) return Array.Empty<Fighter>();

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

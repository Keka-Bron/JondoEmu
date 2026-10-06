using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A JondoBot's spells weighed row by row, as the effect engine will apply them: whom each row
    /// reaches from where the bot stands and where it aims, and what it does to each of them.
    /// </summary>
    [Collection("koliseo")]
    public class JondoBotTacticsTests
    {
        private const int Centre = 300;

        private static int InLineAt(int from, int distance)
            => Enumerable.Range(0, MapGeometry.MaxCells)
                         .First(c => MapGeometry.Distance(from, c) == distance
                                     && MapGeometry.CellToPoint(c).Item2 == MapGeometry.CellToPoint(from).Item2);

        private static Fighter Player(int cell) => new()
        {
            Id = 8_990_000_001, CellId = cell, MaxHP = 4500, CurrentHP = 4500, Level = 200,
            CurrentAP = 12, CurrentMP = 6, MaxAP = 12, MaxMP = 6,
        };

        /// <summary>What the start of the fight gives the bot: the states its passive and initial spells put on it.</summary>
        private static void StartOfFight(Fighter bot)
        {
            foreach (int attitude in bot.Buffs.Actitudes)
                foreach (var row in SpellEffects.De(attitude, bot.Buffs.GradoDeActitud(attitude)))
                {
                    if (row.EffectId == 950 && row.TargetMask == "C") bot.Buffs.PonerEstado(row.Value);
                    if ((row.EffectId == 1160 || row.EffectId == 792) && row.TargetMask == "C" && row.Triggers.Split('|').Contains("I"))
                        foreach (var child in SpellEffects.De(row.DiceNum, System.Math.Max(1, row.DiceSide)))
                            if (child.EffectId == 950 && child.TargetMask == "C") bot.Buffs.PonerEstado(child.Value);
                }
        }

        /// <summary>
        /// The Forjalanza's lance was put down on one of the four cells next to its caster, and on
        /// its first turn it went behind it. Every spell of it that leaves the lance is now aimed
        /// nearer the enemy than the bot stands -- next to him, or through him.
        /// </summary>
        [Fact]
        public void The_Forjalanza_throws_its_lance_at_the_enemy_and_never_behind_itself()
        {
            var spec = KoliseoBots.Create(20);
            try
            {
                var fight = new FightInstance(9_991_020, 1, 1);
                var bot = KoliseoBots.BuildFighter(spec);
                bot.CellId = Centre;
                var enemy = Player(InLineAt(Centre, 7));
                fight.AddPlayer(enemy);
                fight.AddOpponent(bot);
                StartOfFight(bot);
                var board = new MonsterTactics.Board
                {
                    Fighters = new[] { bot, enemy },
                    Reach = (row, caster, from, aim) => EffectEngine.ReachOf(fight, caster, row, from, aim),
                    CanSummon = (c, t, g) => FightHandler.FitsTheSummonLimit(fight, c, t, g),
                };
                var spells = FightHandler.TacticsOf(bot);

                var lances = new List<MonsterTactics.Action>();
                for (int step = 0; step < 8; step++)
                {
                    var action = MonsterTactics.Next(board, bot, spells.FindAll(s => SpellCriteria.Allows(bot, s.Id, s.Grade)));
                    if (action == null) break;
                    if (action.Spell.SummonsByRow) lances.Add(action);
                    bot.CurrentMP -= action.Path.Count - 1;
                    bot.CellId = action.From;
                    bot.CurrentAP -= action.Spell.Cost;
                    bot.LanzadosEsteTurno[action.Spell.Id] = bot.LanzadosEsteTurno.GetValueOrDefault(action.Spell.Id) + 1;
                }

                Assert.NotEmpty(lances);
                Assert.All(lances, a => Assert.True(
                    MapGeometry.Distance(a.TargetCell, enemy.CellId) < MapGeometry.Distance(a.From, enemy.CellId),
                    $"spell {a.Spell.Id} aimed at {a.TargetCell}, from {a.From}, with the enemy on {enemy.CellId}"));
            }
            finally { KoliseoBots.Forget(spec.Id); }
        }

        /// <summary>
        /// A fighter in between blocks sight as a pillar does: a JondoBot does not shoot through
        /// an ally or a summon. The one whose turn it is does not block itself -- it plans from
        /// where it will be, and has left where it stood.
        /// </summary>
        [Fact]
        public void A_fighter_in_between_blocks_the_bots_sight()
        {
            var fight = new FightInstance(9_991_021, 1, 1);
            fight.AddPlayer(Player(0));
            fight.AddOpponent(new Fighter { Id = -7, TeamId = 1, Level = 200, MaxHP = 6666, CurrentHP = 6666, IsBot = true });
            fight.AddOpponent(new Fighter { Id = -8, TeamId = 1, Level = 200, MaxHP = 100, CurrentHP = 100 });
            var planner = fight.CurrentFighter!;
            var others = fight.TurnOrder.Where(f => f != planner).ToList();
            var (target, between) = (others[0], others[1]);
            planner.CellId = Centre;
            target.CellId = InLineAt(Centre, 4);
            between.CellId = InLineAt(Centre, 6);
            Assert.True(FightHandler.BoardOf(fight).Sees(Centre, target.CellId));

            between.CellId = InLineAt(Centre, 2);
            Assert.False(FightHandler.BoardOf(fight).Sees(Centre, target.CellId));
            // From past the one in between it sees again; and the dead block nothing.
            Assert.True(FightHandler.BoardOf(fight).Sees(InLineAt(Centre, 3), target.CellId));
            between.CurrentHP = 0;
            Assert.True(FightHandler.BoardOf(fight).Sees(Centre, target.CellId));

            // Its own cell does not block it either: from behind where it stands, it plans a shot
            // it will take once it has walked there.
            int behind = Enumerable.Range(0, MapGeometry.MaxCells).First(c => MapGeometry.Distance(c, target.CellId) == 5
                && MapGeometry.Distance(c, Centre) == 1 && MapGeometry.CellToPoint(c).Item2 == MapGeometry.CellToPoint(Centre).Item2);
            Assert.True(FightHandler.BoardOf(fight).Sees(behind, target.CellId));
        }

        private static MonsterTactics.Spell Made(params SpellEffect[] rows) => new()
        {
            Id = 999_001, Grade = 1, Cost = 2, MinRange = 0, MaxRange = 6, Rows = rows,
        };

        private static (Fighter Bot, Fighter Enemy, MonsterTactics.Board Board) Duel()
        {
            var bot = new Fighter { Id = -1, TeamId = 1, CellId = Centre, Level = 200, MaxHP = 6666, CurrentHP = 6666, CurrentAP = 12, CurrentMP = 6, IsBot = true };
            var enemy = Player(InLineAt(Centre, 4));
            enemy.TeamId = 0;
            return (bot, enemy, new MonsterTactics.Board { Fighters = new[] { bot, enemy } });
        }

        /// <summary>
        /// A state's row carries the state in its value, and read as an amount every state was a
        /// buff of five thousand points: Pavés and Fiebre del Oro outweighed any blow.
        /// </summary>
        [Fact]
        public void A_state_is_not_a_buff()
        {
            var (bot, enemy, board) = Duel();
            var state = Made(new SpellEffect { EffectId = 950, Value = 5269, Duration = 1, TargetMask = "C", Triggers = "I" });
            Assert.Equal(0, MonsterTactics.ValueOfRows(board, state, bot, bot.CellId, bot.CellId, new List<Fighter> { enemy }));
        }

        /// <summary>Influencia's "-100 MP" takes the six he has, not a hundred.</summary>
        [Fact]
        public void Taking_points_takes_only_the_ones_he_has()
        {
            var (bot, enemy, board) = Duel();
            var all = Made(new SpellEffect { EffectId = 169, DiceNum = 100, Duration = 1, TargetMask = "A", Triggers = "I" });
            var two = Made(new SpellEffect { EffectId = 169, DiceNum = 2, Duration = 1, TargetMask = "A", Triggers = "I" });

            double allOfThem = MonsterTactics.ValueOfRows(board, all, bot, bot.CellId, enemy.CellId, new List<Fighter> { enemy });
            double twoOfThem = MonsterTactics.ValueOfRows(board, two, bot, bot.CellId, enemy.CellId, new List<Fighter> { enemy });
            Assert.True(twoOfThem > 0);
            Assert.Equal(3 * twoOfThem, allOfThem, 3);
        }

        /// <summary>A summon the fight would refuse is worth nothing: its AP go to a blow.</summary>
        [Fact]
        public void A_summon_over_the_limit_is_worth_nothing()
        {
            var (bot, enemy, _) = Duel();
            int free = MapGeometry.GetNeighbors(enemy.CellId).First(c => MapGeometry.Distance(c, bot.CellId) < MapGeometry.Distance(enemy.CellId, bot.CellId));
            var summon = Made(new SpellEffect { EffectId = 181, DiceNum = 7139, DiceSide = 1, TargetMask = "a,A", Triggers = "I" });

            var room = new MonsterTactics.Board { Fighters = new[] { bot, enemy }, CanSummon = (_, _, _) => true };
            var full = new MonsterTactics.Board { Fighters = new[] { bot, enemy }, CanSummon = (_, _, _) => false };
            Assert.True(MonsterTactics.ValueOfRows(room, summon, bot, bot.CellId, free, new List<Fighter> { enemy }) > 0);
            Assert.Equal(0, MonsterTactics.ValueOfRows(full, summon, bot, bot.CellId, free, new List<Fighter> { enemy }));
        }

        /// <summary>A JondoBot carries the summons of an optimized set: four out at once.</summary>
        [Fact]
        public void A_jondobot_can_have_four_summons_out()
        {
            var spec = KoliseoBots.Create(2);
            try { Assert.Equal(1 + KoliseoBots.Summons, FightHandler.SummonLimitFor(KoliseoBots.BuildFighter(spec), 1)); }
            finally { KoliseoBots.Forget(spec.Id); }
        }

        /// <summary>
        /// "Le nombre de gens qui passent leur tour sur la case où ils viennent de taper est
        /// impressionnant": a ranged fighter ends its turn where no enemy sees it, when it can.
        /// </summary>
        [Fact]
        public void A_ranged_fighter_ends_its_turn_out_of_sight_when_it_can()
        {
            var monster = new Fighter { Id = -1, TeamId = 1, CellId = Centre, Level = 100, MaxHP = 1000, CurrentHP = 1000, CurrentMP = 4 };
            var enemy = Player(InLineAt(Centre, 6));
            enemy.TeamId = 0;
            var bow = new MonsterTactics.Spell { Id = 1, Grade = 1, Cost = 3, MinRange = 1, MaxRange = 5, Damage = 20 };

            var open = new MonsterTactics.Board { Fighters = new[] { monster, enemy } };
            int hidden = MonsterTactics.Reachable(open, monster).Keys
                                       .First(c => MapGeometry.Distance(c, enemy.CellId) == 5 && c != Centre);
            var board = new MonsterTactics.Board
            {
                Fighters = new[] { monster, enemy },
                Sees = (from, to) => to != hidden,
            };

            Assert.Equal(hidden, MonsterTactics.Reposition(board, monster, new[] { bow })[^1]);
        }

        /// <summary>
        /// Three turns of a Sram JondoBot, the enemy seven cells away and coming no nearer: what
        /// it casts, spell by spell, with a turn's limits and cooldowns as the fight keeps them.
        /// </summary>
        private static List<MonsterTactics.Action> SramTurns(int turns, params int[] picks)
        {
            // The Sram's picks: these where their pair holds them, the base spell elsewhere.
            var spec = new KoliseoBots.Spec
            {
                Id = KoliseoBots.FirstId - 4, Breed = 4, Sex = 0, Name = "JondoBot Sram",
                Choices = SpellTable.PairsOf(4).ToDictionary(p => p.Id, p => picks.FirstOrDefault(p.Holds) is int s && s != 0 ? s : p.Base),
            };
            try
            {
                var fight = new FightInstance(9_991_004, 1, 1);
                var bot = KoliseoBots.BuildFighter(spec);
                bot.CellId = Centre;
                var enemy = Player(InLineAt(Centre, 7));
                fight.AddPlayer(enemy);
                fight.AddOpponent(bot);
                StartOfFight(bot);
                var traps = new List<int>();
                var board = new MonsterTactics.Board
                {
                    Fighters = new[] { bot, enemy },
                    Reach = (row, caster, from, aim) => EffectEngine.ReachOf(fight, caster, row, from, aim),
                    CanSummon = (c, t, g) => FightHandler.FitsTheSummonLimit(fight, c, t, g),
                    Trapped = cell => traps.Contains(cell),
                    TrapsOut = () => traps.Count,
                };
                var spells = FightHandler.TacticsOf(bot);
                var cast = new List<MonsterTactics.Action>();
                for (int turn = 0; turn < turns; turn++)
                {
                    bot.CurrentAP = bot.MaxAP;
                    bot.CurrentMP = bot.MaxMP;
                    bot.LanzadosEsteTurno.Clear();
                    foreach (var key in bot.Recarga.Keys.ToList()) bot.Recarga[key] = Math.Max(0, bot.Recarga[key] - 1);
                    for (int step = 0; step < 10; step++)
                    {
                        var action = MonsterTactics.Next(board, bot, spells.FindAll(s => SpellCriteria.Allows(bot, s.Id, s.Grade)));
                        if (action == null) break;
                        cast.Add(action);
                        if (action.Spell.LaysTraps) traps.Add(action.TargetCell);
                        bot.CurrentMP -= action.Path.Count - 1;
                        bot.CellId = action.From;
                        bot.CurrentAP -= action.Spell.Cost;
                        bot.LanzadosEsteTurno[action.Spell.Id] = bot.LanzadosEsteTurno.GetValueOrDefault(action.Spell.Id) + 1;
                        int wait = FightHandler.LimitesDeGrado(action.Spell.Id, action.Spell.Grade).Intervalo;
                        if (wait > 0) bot.Recarga[action.Spell.Id] = wait;
                    }
                }
                return cast;
            }
            finally { KoliseoBots.Forget(spec.Id); }
        }

        private const int Invisibilidad = 12913, Doble = 12915, Arsenico = 12907;

        /// <summary>
        /// A Sram JondoBot plays like one: it lays traps where the enemy may walk, never two on one
        /// cell, puts its double out -- not on a trap of its own --, and goes invisible. It used
        /// to cast only its ranged blows and run: a trap, invisibility and a double were each
        /// worth nothing to it.
        /// </summary>
        [Fact]
        public void A_Sram_JondoBot_lays_traps_goes_invisible_and_puts_its_double_out()
        {
            var cast = SramTurns(3, Invisibilidad, Doble, Arsenico);
            string said = string.Join(", ", cast.Select(a => $"{a.Spell.Id}@{a.TargetCell}"));

            var traps = cast.Where(a => a.Spell.LaysTraps).Select(a => a.TargetCell).ToList();
            Assert.True(traps.Count >= 2, said);
            Assert.Equal(traps.Count, traps.Distinct().Count());
            var twin = Assert.Single(cast, a => a.Spell.Id == Doble);
            Assert.DoesNotContain(twin.TargetCell, traps);
            Assert.Contains(cast, a => a.Spell.Id == Invisibilidad);
        }

        /// <summary>
        /// A trap is likelier to be walked on on the enemy's way to its caster than off to a side,
        /// and not at all out of his reach but for later.
        /// </summary>
        [Fact]
        public void A_trap_on_the_enemys_way_is_likelier_to_be_walked_on()
        {
            var board = new MonsterTactics.Board();
            int enemy = InLineAt(Centre, 6);
            int onTheWay = InLineAt(Centre, 3);
            int aside = Enumerable.Range(0, MapGeometry.MaxCells).First(c => MapGeometry.Distance(c, enemy) == 3
                && MapGeometry.Distance(c, Centre) > 3);
            int far = Enumerable.Range(0, MapGeometry.MaxCells).First(c => MapGeometry.Distance(c, enemy) == 12);

            double way = MonsterTactics.TrapOdds(board, new[] { onTheWay }, enemy, 6, Centre);
            double side = MonsterTactics.TrapOdds(board, new[] { aside }, enemy, 6, Centre);
            double later = MonsterTactics.TrapOdds(board, new[] { far }, enemy, 6, Centre);
            Assert.True(way > side && side > later && later > 0, $"{way} {side} {later}");
        }

        /// <summary>
        /// An invisible enemy is not aimed at where he stands, which the bot cannot know, but
        /// where he was last seen -- and with nowhere to aim, the bot has nothing to cast at him.
        /// It used to hit an invisible player as if he were in plain sight.
        /// </summary>
        [Fact]
        public void An_invisible_enemy_is_aimed_at_where_he_was_last_seen()
        {
            var (bot, enemy, _) = Duel();
            int seen = InLineAt(Centre, 3);
            var blow = Made(new SpellEffect { EffectId = 97, DiceNum = 30, DiceSide = 40, TargetMask = "a,A", Triggers = "I" });

            var sighted = new MonsterTactics.Board { Fighters = new[] { bot, enemy } };
            Assert.Equal(enemy.CellId, MonsterTactics.Next(sighted, bot, new[] { blow })!.TargetCell);

            var unseen = new MonsterTactics.Board { Fighters = new[] { bot, enemy }, Hidden = f => f == enemy, LastSeen = _ => seen };
            var guess = MonsterTactics.Next(unseen, bot, new[] { blow });
            Assert.NotNull(guess);
            Assert.Equal(seen, guess!.TargetCell);

            var lost = new MonsterTactics.Board { Fighters = new[] { bot, enemy }, Hidden = f => f == enemy };
            Assert.Null(MonsterTactics.Next(lost, bot, new[] { blow }));
        }

        /// <summary>A poison of three turns bites three times, and is worth more than one of a single turn.</summary>
        [Fact]
        public void A_poison_that_lasts_is_worth_its_turns()
        {
            var (bot, enemy, board) = Duel();
            SpellEffect Poison(int turns) => new() { EffectId = 97, DiceNum = 20, DiceSide = 30, Duration = turns, TargetMask = "a,A", Triggers = "TB" };
            double once = MonsterTactics.ValueOfRows(board, Made(Poison(1)), bot, bot.CellId, enemy.CellId, new List<Fighter> { enemy });
            double thrice = MonsterTactics.ValueOfRows(board, Made(Poison(3)), bot, bot.CellId, enemy.CellId, new List<Fighter> { enemy });
            Assert.True(once > 0 && thrice > 2 * once, $"{once} {thrice}");
        }
    }
}

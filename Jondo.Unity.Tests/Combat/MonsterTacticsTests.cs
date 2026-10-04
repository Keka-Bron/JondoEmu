using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The monsters' tactics on boards made to measure: whom they go for, where they walk to
    /// cast, what they leave for later, and where they stand when they are done.
    /// </summary>
    public class MonsterTacticsTests
    {
        private const int Centre = 300;

        private static int CellAt(int from, int distance, int skip = 0)
        {
            foreach (int cell in Enumerable.Range(0, 560))
                if (MapGeometry.Distance(from, cell) == distance && skip-- <= 0) return cell;
            return -1;
        }

        private static Fighter Monster(int ap = 6, int mp = 3, int hp = 1000)
            => new Fighter { Id = -1, TeamId = 1, CellId = Centre, MaxHP = hp, CurrentHP = hp, CurrentAP = ap, CurrentMP = mp, Level = 100, IsMonster = true };

        private static Fighter Player(long id, int cell, int hp = 1000, int max = 1000)
            => new Fighter { Id = id, TeamId = 0, CellId = cell, MaxHP = max, CurrentHP = hp, CurrentAP = 6, CurrentMP = 3, Level = 200 };

        private static MonsterTactics.Board Board(params Fighter[] fighters)
            => new MonsterTactics.Board { Fighters = fighters };

        private static MonsterTactics.Spell Hit(int id = 1, int cost = 3, int min = 1, int max = 1, double damage = 100, int perTurn = 0)
            => new MonsterTactics.Spell { Id = id, Grade = 1, Cost = cost, MinRange = min, MaxRange = max, Damage = damage, PerTurn = perTurn };

        /// <summary>Between two in reach, the one the blow kills -- the whole group goes for the same one.</summary>
        [Fact]
        public void It_goes_for_the_kill()
        {
            var monster = Monster();
            var healthy = Player(1, CellAt(Centre, 1), hp: 1000);
            var dying = Player(2, CellAt(Centre, 1, skip: 1), hp: 80);

            var action = MonsterTactics.Next(Board(monster, healthy, dying), monster, new[] { Hit() });

            Assert.Same(dying, action!.Target);
        }

        /// <summary>Out of reach from where it stands, it walks to a cell from which the spell reaches.</summary>
        [Fact]
        public void It_walks_to_where_the_spell_reaches()
        {
            var monster = Monster(mp: 4);
            var player = Player(1, CellAt(Centre, 5));

            var action = MonsterTactics.Next(Board(monster, player), monster, new[] { Hit(max: 2) });

            Assert.NotNull(action);
            Assert.True(action!.Path.Count > 1);
            Assert.InRange(MapGeometry.Distance(action.From, player.CellId), 1, 2);
        }

        /// <summary>No line of sight from here: it moves round rather than giving up the spell.</summary>
        [Fact]
        public void It_looks_for_a_line_of_sight()
        {
            var monster = Monster(mp: 3);
            var player = Player(1, CellAt(Centre, 4));
            var board = new MonsterTactics.Board
            {
                Fighters = new[] { monster, player },
                Sees = (from, to) => from != Centre,
            };

            var sighted = new MonsterTactics.Spell { Id = 1, Grade = 1, Cost = 3, MinRange = 1, MaxRange = 6, Damage = 100, NeedsLineOfSight = true };
            var action = MonsterTactics.Next(board, monster, new[] { sighted });

            Assert.NotNull(action);
            Assert.NotEqual(Centre, action!.From);
        }

        /// <summary>The strongest blow for what it costs, not the first spell of the sheet.</summary>
        [Fact]
        public void It_casts_the_best_blow_for_its_points()
        {
            var monster = Monster();
            var player = Player(1, CellAt(Centre, 1));
            var weak = Hit(id: 1, cost: 3, damage: 30);
            var strong = Hit(id: 2, cost: 3, damage: 120);

            Assert.Equal(2, MonsterTactics.Next(Board(monster, player), monster, new[] { weak, strong })!.Spell.Id);
        }

        /// <summary>An ally at a fifth of its life is healed before anybody is hit.</summary>
        [Fact]
        public void It_heals_the_badly_wounded_first()
        {
            var monster = Monster();
            var ally = new Fighter { Id = -2, TeamId = 1, CellId = CellAt(Centre, 1), MaxHP = 1000, CurrentHP = 200, IsMonster = true };
            var player = Player(1, CellAt(Centre, 1, skip: 1));
            var heal = new MonsterTactics.Spell { Id = 3, Grade = 1, Cost = 3, MinRange = 0, MaxRange = 3, Heal = 300 };

            var action = MonsterTactics.Next(Board(monster, ally, player), monster, new[] { Hit(damage: 60), heal });

            Assert.Equal(3, action!.Spell.Id);
            Assert.Same(ally, action.Target);
        }

        /// <summary>A buff once a turn, a spell in its cooldown never, a spell at its cap per turn no more.</summary>
        [Fact]
        public void It_keeps_to_the_limits()
        {
            var monster = Monster();
            var player = Player(1, CellAt(Centre, 1));
            var buff = new MonsterTactics.Spell { Id = 4, Grade = 1, Cost = 2, MinRange = 0, MaxRange = 0, Buff = 50 };

            monster.LanzadosEsteTurno[4] = 1;
            Assert.Null(MonsterTactics.Next(Board(monster, player), monster, new[] { buff }));

            monster.Recarga[1] = 2;
            Assert.Null(MonsterTactics.Next(Board(monster, player), monster, new[] { Hit() }));

            monster.Recarga.Clear();
            monster.LanzadosEsteTurno[1] = 2;
            Assert.Null(MonsterTactics.Next(Board(monster, player), monster, new[] { Hit(perTurn: 2) }));
        }

        /// <summary>Taking an enemy's AP and MP is worth a cast when he still has them.</summary>
        [Fact]
        public void It_takes_points_away()
        {
            var monster = Monster();
            var player = Player(1, CellAt(Centre, 3));
            var drain = new MonsterTactics.Spell { Id = 5, Grade = 1, Cost = 3, MinRange = 1, MaxRange = 5, Removal = 2 };

            Assert.Equal(5, MonsterTactics.Next(Board(monster, player), monster, new[] { drain })!.Spell.Id);
        }

        /// <summary>
        /// A spell of pure mechanics -- a state on itself, a glyph -- is cast too, once a turn, and
        /// a free one as well: the boss's own moves scored nothing and were never cast.
        /// </summary>
        [Fact]
        public void It_casts_its_mechanics_once_a_turn()
        {
            var monster = Monster(ap: 0);
            var player = Player(1, CellAt(Centre, 6));
            var clock = new MonsterTactics.Spell { Id = 9, Grade = 1, Cost = 0, MinRange = 0, MaxRange = 0, Utility = 40 };

            var action = MonsterTactics.Next(Board(monster, player), monster, new[] { clock });
            Assert.Equal(9, action!.Spell.Id);
            Assert.Same(monster, action.Target);

            monster.LanzadosEsteTurno[9] = 1;
            Assert.Null(MonsterTactics.Next(Board(monster, player), monster, new[] { clock }));
        }

        /// <summary>One whose rows are for enemies goes at an enemy in reach.</summary>
        [Fact]
        public void It_aims_mechanics_for_enemies_at_an_enemy()
        {
            var monster = Monster();
            var player = Player(1, CellAt(Centre, 2));
            var mark = new MonsterTactics.Spell { Id = 10, Grade = 1, Cost = 2, MinRange = 1, MaxRange = 4, Utility = 40, UtilityOnEnemies = true };

            var action = MonsterTactics.Next(Board(monster, player), monster, new[] { mark });
            Assert.Same(player, action!.Target);
        }

        /// <summary>
        /// Placed at the end: a ranged one at its spell's reach, a melee one against the weakest, and
        /// one about to die as far away as it can get.
        /// </summary>
        [Fact]
        public void It_places_itself_by_what_it_is()
        {
            var player = Player(1, CellAt(Centre, 3));

            var archer = Monster(mp: 4);
            var bow = Hit(min: 2, max: 5);
            var stand = MonsterTactics.Reposition(Board(archer, player), archer, new[] { bow });
            Assert.InRange(MapGeometry.Distance(stand[^1], player.CellId), 4, 5);

            var brute = Monster(mp: 4);
            var fist = Hit(max: 1);
            stand = MonsterTactics.Reposition(Board(brute, player), brute, new[] { fist });
            Assert.Equal(1, MapGeometry.Distance(stand[^1], player.CellId));

            var dying = Monster(mp: 4, hp: 1000);
            dying.CurrentHP = 100;
            stand = MonsterTactics.Reposition(Board(dying, player), dying, new[] { fist });
            Assert.True(MapGeometry.Distance(stand[^1], player.CellId) > 3);
        }
    }
}

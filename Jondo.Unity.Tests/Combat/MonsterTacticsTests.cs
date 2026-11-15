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

        /// <summary>The cell at these offsets from another, on the diamond grid's own axes.</summary>
        private static int Offset(int from, int dx, int dy)
        {
            var (x, y) = MapGeometry.CellToPoint(from);
            foreach (int cell in Enumerable.Range(0, 560))
                if (MapGeometry.CellToPoint(cell) == (x + dx, y + dy)) return cell;
            return -1;
        }

        /// <summary>
        /// A spell cast in diagonal reaches only the diagonals: Dark Vlad's Llamas Diagonales does
        /// not touch whoever stands straight in front of him, and does whoever stands on one.
        /// </summary>
        [Fact]
        public void A_diagonal_spell_reaches_only_the_diagonals()
        {
            var diagonal = new MonsterTactics.Spell
            {
                Id = 4309, Grade = 1, Cost = 2, MinRange = 2, MaxRange = 63, Damage = 1800, InDiagonal = true,
            };

            var monster = Monster(mp: 0);
            var straight = Player(1, Offset(Centre, 0, -5));
            Assert.Null(MonsterTactics.Next(Board(monster, straight), monster, new[] { diagonal }));

            monster = Monster(mp: 0);
            var aslant = Player(2, Offset(Centre, 3, -3));
            var action = MonsterTactics.Next(Board(monster, aslant), monster, new[] { diagonal });
            Assert.NotNull(action);
            Assert.Same(aslant, action!.Target);

            // A diagonal cell is as far as one of its offsets: three, inside a range of 2 to 3.
            var short_ = new MonsterTactics.Spell
            {
                Id = 4309, Grade = 1, Cost = 2, MinRange = 2, MaxRange = 3, Damage = 1800, InDiagonal = true,
            };
            monster = Monster(mp: 0);
            Assert.NotNull(MonsterTactics.Next(Board(monster, aslant), monster, new[] { short_ }));

            // In line AND in diagonal: either.
            var both = new MonsterTactics.Spell
            {
                Id = 4309, Grade = 1, Cost = 2, MinRange = 2, MaxRange = 63, Damage = 1800, InDiagonal = true, InLine = true,
            };
            monster = Monster(mp: 0);
            Assert.NotNull(MonsterTactics.Next(Board(monster, straight), monster, new[] { both }));
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

        /// <summary>
        /// A boost for allied summons only («j») goes to the summon, never to a companion that
        /// is no summon: the Minotoror's Semillas Mágicas, cast on his Scaratos every turn.
        /// </summary>
        [Fact]
        public void A_boost_for_summons_goes_to_a_summon_only()
        {
            var monster = Monster();
            var companion = new Fighter { Id = -2, TeamId = 1, CellId = CellAt(Centre, 1), MaxHP = 1000, CurrentHP = 1000, IsMonster = true };
            var player = Player(1, CellAt(Centre, 8));
            var seeds = new MonsterTactics.Spell
            {
                Id = 246, Grade = 1, Cost = 6, MinRange = 1, MaxRange = 5, Buff = 150,
                SupportMasks = new[] { "j" },
            };

            Assert.Null(MonsterTactics.Next(Board(monster, companion, player), monster, new[] { seeds }));

            var tofu = new Fighter
            {
                Id = -3, TeamId = 1, CellId = CellAt(Centre, 2), MaxHP = 99, CurrentHP = 99,
                IsMonster = true, Invocador = monster.Id,
            };
            var action = MonsterTactics.Next(Board(monster, companion, tofu, player), monster, new[] { seeds });

            Assert.Same(tofu, action!.Target);
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

        /// <summary>
        /// A melee one that cannot reach its enemy this turn walks toward it the way it can go, not
        /// as the crow flies: the cell nearest as the crow flies is against a wall, a dead end it
        /// would never leave. Feu Follets stood there turn after turn until the player came to them.
        /// </summary>
        [Fact]
        public void A_melee_one_walks_around_a_wall_rather_than_into_it()
        {
            var (x0, y0) = MapGeometry.CellToPoint(Centre);
            var brute = Monster(mp: 3);
            var player = Player(1, MapGeometry.PointToCell(x0 + 6, y0));
            // A wall two columns ahead, open below it.
            bool Wall(int cell)
            {
                var (x, y) = MapGeometry.CellToPoint(cell);
                return x == x0 + 2 && y <= y0 + 1 && y >= y0 - 12;
            }
            var board = new MonsterTactics.Board { Fighters = new[] { brute, player }, Walkable = cell => MapGeometry.IsValid(cell) && !Wall(cell) };

            var stand = MonsterTactics.Reposition(board, brute, new[] { Hit(max: 1) })[^1];

            var deadEnd = MapGeometry.PointToCell(x0 + 1, y0);
            Assert.NotEqual(deadEnd, stand);
            var walk = MonsterTactics.WalkingDistances(board, player.CellId);
            Assert.True(walk[stand] < walk[deadEnd]);
        }

        /// <summary>
        /// A ring round the caster (the surprised Tigredón's Salto del Grillete, O2) does not reach
        /// the cell beside it: with its only foe there, it is not worth casting; two cells off, it is.
        /// </summary>
        [Fact]
        public void A_ring_round_itself_misses_the_foe_beside_it()
        {
            var ring = new MonsterTactics.Spell
            {
                Id = 4082, Grade = 1, Cost = 2, MinRange = 0, MaxRange = 0, Damage = 65, Zone = 2,
                ZoneRow = new SpellEffect { EffectId = 96, Forma = 'O', Tamano = 2 },
            };

            var beside = Monster(mp: 0);
            var next = MonsterTactics.Next(Board(beside, Player(1, CellAt(Centre, 1))), beside, new[] { ring });
            Assert.True(next == null || next.Spell == null || next.Spell.Id != ring.Id);

            var twoOff = Monster(mp: 0);
            var hit = MonsterTactics.Next(Board(twoOff, Player(2, CellAt(Centre, 2))), twoOff, new[] { ring });
            Assert.Equal(ring.Id, hit?.Spell?.Id);
        }
    }
}

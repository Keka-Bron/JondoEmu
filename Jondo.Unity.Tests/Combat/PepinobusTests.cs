using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Kwapa of the Valley of the Water Lady (6203, 6229, 6247) carry Pepinobus (16721). Its
    /// own summon row (181 for monster 50000) is for the client only: the water bombombre (6233)
    /// comes out of what it casts on its caster, 2794 down to 16741's fourth grade, at the grade
    /// of the caster's kind. The monsters weighed the spell by its own rows, saw no summon and
    /// never cast it, so the bombombre that takes a fighter to Nagate's islet never came.
    /// </summary>
    [Collection("MapManager")]
    public sealed class PepinobusTests
    {
        private const int Pepinobus = 16721, WaterBombombre = 6233;

        private static (Fighter Kwapa, Fighter Enemy, FightInstance Fight, MonsterTactics.Board Board) Duel(int monster)
        {
            var stats = DatabaseManager.GetMonsterGradeStats(monster, 0)!;
            var kwapa = new Fighter
            {
                Id = -1, TeamId = 1, IsMonster = true, MonsterId = monster, Level = stats.Level, CellId = 300,
                MaxHP = 3000, CurrentHP = 3000, MaxAP = 8, CurrentAP = 8, MaxMP = 3, CurrentMP = 3,
                SpellIds = stats.SpellIds, SpellGrades = stats.SpellGrades,
            };
            int far = Enumerable.Range(0, MapGeometry.MaxCells).First(c => MapGeometry.Distance(300, c) == 6);
            var enemy = new Fighter { Id = 10, TeamId = 0, CellId = far, Level = 200, MaxHP = 5000, CurrentHP = 5000,
                                      MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var fight = new FightInstance(1, 1);
            fight.AddPlayer(enemy);
            fight.AddMonster(kwapa);
            var board = new MonsterTactics.Board
            {
                Fighters = new[] { kwapa, enemy },
                Reach = (row, caster, from, aim) => EffectEngine.ReachOf(fight, caster, row, from, aim),
            };
            return (kwapa, enemy, fight, board);
        }

        [Theory]
        [InlineData(6203)]
        [InlineData(6229)]
        [InlineData(6247)]
        public void A_kwapa_weighs_pepinobus_as_a_summon(int monster)
        {
            if (SpellEffects.De(Pepinobus, 1).Count == 0) return;   // no spell data
            var (kwapa, _, _, board) = Duel(monster);
            var tactics = FightHandler.TacticsOf(kwapa);
            var spell = tactics.FirstOrDefault(s => s.Id == Pepinobus);
            Assert.NotNull(spell);
            Assert.True(spell!.Summons);
            Assert.Contains(MonsterTactics.Options(board, kwapa, tactics), o => o.Spell.Id == Pepinobus && o.Score > 0);
        }

        [Fact]
        public async Task Pepinobus_brings_out_a_water_bombombre()
        {
            if (SpellEffects.De(Pepinobus, 1).Count == 0) return;
            var (kwapa, enemy, fight, _) = Duel(6247);
            fight.StartFight();
            int aim = Enumerable.Range(0, MapGeometry.MaxCells)
                .First(c => MapGeometry.Distance(kwapa.CellId, c) == 4 && c != enemy.CellId);

            await FightHandler.AplicarEfectosAsync(null, fight, kwapa, Pepinobus, 1, null, EffectEngine.AlLanzar, aim);

            Assert.Contains(fight.Todos, f => f.IsAlive && f.MonsterId == WaterBombombre);
        }

        /// <summary>
        /// Born on a cell 16747 names, with Nagate's 1888 on its Kwapa, the bombombre turns (6234):
        /// 16747 wants it without 1901, and the row after it gives it that state. Done at birth,
        /// the 1901 came first and none ever turned.
        /// </summary>
        [Fact]
        public async Task Born_on_a_named_cell_the_bombombre_turns()
        {
            if (SpellEffects.De(16747, 1).Count == 0) return;
            const int Named = 256;
            var (kwapa, enemy, fight, _) = Duel(6247);
            kwapa.CellId = Enumerable.Range(0, MapGeometry.MaxCells).First(c => MapGeometry.Distance(c, Named) == 4);
            enemy.CellId = Enumerable.Range(0, MapGeometry.MaxCells)
                .First(c => MapGeometry.Distance(c, Named) == 8 && MapGeometry.Distance(c, kwapa.CellId) > 4);
            kwapa.Buffs.PonerEstado(1888);
            fight.StartFight();

            var tactics = FightHandler.TacticsOf(kwapa);
            Assert.Contains(Named, tactics.First(s => s.Id == Pepinobus).SummonCellsFor(kwapa));

            await FightHandler.AplicarEfectosAsync(null, fight, kwapa, Pepinobus, 1, null, EffectEngine.AlLanzar, Named);

            Assert.Contains(fight.Todos, f => f.IsAlive && f.MonsterId == 6234);
        }

        /// <summary>
        /// The turned bombombre (6234) hit from next door: whoever hit it casts 16771 at it, which
        /// pushes it a cell back, and when it cannot go back he casts 16745 at it -- "4 on L63" --
        /// and lands on the first free cell past it. That is the way onto Nagate's islet.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Hit_from_next_door_the_bombombre_steps_back_or_lets_him_through(bool blocked)
        {
            if (SpellEffects.De(16771, 1).Count == 0 || SpellEffects.De(16745, 1).Count == 0) return;
            var (x, y) = MapGeometry.CellToPoint(300);
            int hitter = MapGeometry.PointToCell(x - 1, y), behind = MapGeometry.PointToCell(x + 1, y),
                further = MapGeometry.PointToCell(x + 2, y);
            var (kwapa, enemy, fight, _) = Duel(6247);
            kwapa.CellId = MapGeometry.PointToCell(x, y + 5);
            enemy.CellId = hitter;
            var bomb = new Fighter { Id = -2, TeamId = 1, IsMonster = true, MonsterId = 6234, Level = 200, CellId = 300,
                                     MaxHP = 500, CurrentHP = 500, MaxAP = 1, CurrentAP = 1 };
            fight.AddMonster(bomb);
            if (blocked)
            {
                var wall = new Fighter { Id = -3, TeamId = 1, IsMonster = true, MonsterId = 6247, Level = 200, CellId = behind,
                                         MaxHP = 500, CurrentHP = 500 };
                fight.AddMonster(wall);
            }
            fight.StartFight();

            await FightHandler.AplicarEfectosAsync(null, fight, enemy, 16771, 1, null, EffectEngine.AlLanzar, 300);

            if (blocked)
            {
                Assert.Equal(300, bomb.CellId);
                Assert.Equal(further, enemy.CellId);
            }
            else
            {
                Assert.Equal(behind, bomb.CellId);
                Assert.Equal(hitter, enemy.CellId);
            }
        }
    }
}

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
    /// Golosotrón Real (2864) starts with Golosorracho (2511): a "792 2513 under DM on C" -- hit in
    /// melee, he casts Hidromiel (2513), whose "push 4 on A, X4" sends the one who hit him four cells
    /// away -- and a "kill (141) under PD on A" armed on his enemies: whoever crashes on a push dies.
    /// </summary>
    /// <remarks>
    /// A live fight showed a summon that hit him crashing in place. It was the arena, not the push:
    /// on map 62135820 the boss stood on 175 and the summon on 188, and the cell behind 188 in that
    /// line, 202, is no fight cell there. From 189, next to him too, the line is open and the push
    /// runs its four cells.
    /// </remarks>
    public class GolosotronPushTests
    {
        private const int Golosotron = 2864, Golosorracho = 2511, Hidromiel = 2513;

        /// <summary>The live fight's arena, the boss's cell there and the two cells next to him.</summary>
        private const long Arena = 62135820;
        private const int BossOnTheArena = 175, AgainstTheEdge = 188, OpenLine = 189;

        private static bool HasData => SpellEffects.De(Golosorracho, 1).Count > 0
                                       && SpellEffects.De(Hidromiel, 1).Any(row => row.EffectId == 5);

        /// <summary>The boss's cell, the attacker's next to it, and the cells behind the attacker, in line.</summary>
        private static (int Boss, int Attacker, int[] Behind) ALine(int boss, int attacker)
        {
            var (bx, by) = MapGeometry.CellToPoint(boss);
            var (ax, ay) = MapGeometry.CellToPoint(attacker);
            var behind = Enumerable.Range(1, 6)
                                   .Select(k => MapGeometry.PointToCell(ax + k * (ax - bx), ay + k * (ay - by)))
                                   .ToArray();
            return (boss, attacker, behind);
        }

        private static async Task<(FightInstance Fight, Fighter Boss, Fighter Character)> BoardAsync(long map, int bossCell,
                                                                                                    int attackerCell)
        {
            var fight = new FightInstance(1, map);
            var character = new Fighter
            {
                Id = 10, CellId = attackerCell, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 10, CurrentAP = 10, MaxMP = 5, CurrentMP = 5,
            };
            fight.AddPlayer(character);
            var boss = new Fighter
            {
                Id = -1, CellId = bossCell, MaxHP = 20000, CurrentHP = 20000, Level = 180, IsMonster = true,
                MonsterId = Golosotron, MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4,
            };
            fight.AddMonster(boss);
            fight.StartFight();

            boss.Conducta = (Golosorracho, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);
            return (fight, boss, character);
        }

        private static Task MeleeBlowAsync(FightInstance fight, Fighter character, Fighter boss)
            => FightHandler.HurtAsync(null, fight, character, 1, 1, boss, boss.CellId,
                   tirada: new[] { new SpellEffect { EffectId = 97, EffectUid = 900201, DiceNum = 50, DiceSide = 50,
                                                     Element = 1, TargetMask = "A" } });

        [Fact]
        public async Task A_melee_blow_on_him_pushes_the_attacker_four_cells_away_from_him()
        {
            if (!HasData) return;
            var (bossCell, attackerCell, behind) = ALine(330, MapGeometry.GetNeighbors(330).First());
            Assert.True(behind.All(c => c >= 0), "the line stays on the board");
            var (fight, boss, character) = await BoardAsync(1, bossCell, attackerCell);

            await MeleeBlowAsync(fight, character, boss);

            Assert.True(character.IsAlive, "nothing to crash into: the 141 under PD stays put");
            Assert.Equal(behind[3], character.CellId);
            Assert.Equal(4, MapGeometry.Distance(attackerCell, character.CellId));
            Assert.Equal(bossCell, boss.CellId);
        }

        /// <summary>The live fight's arena: whether there is room behind the attacker is the arena's.</summary>
        private static bool HasTheArena()
        {
            if (!MapManager.WalkableCells.ContainsKey(Arena)) MapManager.Initialize();
            var cells = MapManager.GetFightWalkable(Arena);
            return cells != null && cells.Contains(BossOnTheArena) && cells.Contains(AgainstTheEdge)
                   && !cells.Contains(ALine(BossOnTheArena, AgainstTheEdge).Behind[0])
                   && ALine(BossOnTheArena, OpenLine).Behind.Take(4).All(cells.Contains);
        }

        [Fact]
        public async Task On_his_arena_the_one_with_no_fight_cell_behind_him_crashes_where_he_stands()
        {
            if (!HasData || !HasTheArena()) return;
            var (fight, boss, character) = await BoardAsync(Arena, BossOnTheArena, AgainstTheEdge);

            await MeleeBlowAsync(fight, character, boss);

            Assert.Equal(AgainstTheEdge, character.CellId);
            Assert.Equal(BossOnTheArena, boss.CellId);
        }

        [Fact]
        public async Task On_his_arena_the_one_with_an_open_line_behind_him_goes_four_cells()
        {
            if (!HasData || !HasTheArena()) return;
            var (_, _, behind) = ALine(BossOnTheArena, OpenLine);
            var (fight, boss, character) = await BoardAsync(Arena, BossOnTheArena, OpenLine);

            await MeleeBlowAsync(fight, character, boss);

            Assert.True(character.IsAlive);
            Assert.Equal(behind[3], character.CellId);
            Assert.Equal(BossOnTheArena, boss.CellId);
        }
    }
}

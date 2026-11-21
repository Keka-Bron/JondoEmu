using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A monster's summon that takes no turn of its own (no CanPlay) still does what its behaviour
    /// spell waits on at turn start, at its summoner's turn start: the Zarza carcelaria (7383) the
    /// Carcelero silvestre (7382) puts around Cornelia's rescuer in «El último de nosotros» (2486).
    /// Its 28432 is «792 under TB: its grade 2», a 1080 of 100 MP on the foes in its melee --
    /// «Elles vous retirent 100PM lorsqu'elles commencent leur tour à votre mêlée» (Dofus pour les
    /// noobs, «Les derniers d'entre nous»).
    /// </summary>
    [Collection("MapManager")]
    public class NonPlayingMonsterSummonTests
    {
        private const int Jailer = 7382, Bramble = 7383, BrambleBehaviour = 28432, MpRemoval = 1080;

        [Fact]
        public void The_bramble_takes_no_turn_and_comes_with_its_behaviour()
        {
            Assert.False(MonsterFlags.AllowsPlay(Bramble));
            var recipe = Summons.De(Bramble, 1);
            Assert.NotNull(recipe);
            Assert.False(recipe.Juega);
            Assert.Equal(BrambleBehaviour, recipe.HechizoPropio);
        }

        [Fact]
        public async Task At_its_summoners_turn_start_the_bramble_takes_the_movement_points_of_the_foe_in_its_melee()
        {
            const int brambleCell = 300;
            var (x, y) = MapGeometry.CellToPoint(brambleCell);
            int inItsMelee = MapGeometry.PointToCell(x + 1, y);
            int away = MapGeometry.PointToCell(x - 4, y);

            var fight = new FightInstance(1, 1);
            var foe = new Fighter
            {
                Id = 100, TeamId = 0, CellId = inItsMelee, Level = 200, Breed = 9,
                MaxHP = 5000, CurrentHP = 5000, MaxAP = 9, CurrentAP = 9, MaxMP = 5, CurrentMP = 5,
            };
            var jailer = new Fighter
            {
                Id = -1, TeamId = 1, CellId = away, Level = 180, IsMonster = true, MonsterId = Jailer, GradeIndex = 1,
                MaxHP = 6900, CurrentHP = 6900, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            fight.AddPlayer(foe);
            fight.AddMonster(jailer);
            fight.StartFight();

            var bramble = await FightHandler.SummonAsync(null, fight, jailer, Bramble, 1, brambleCell);
            Assert.False(bramble.JuegaTurno);
            Assert.Equal(BrambleBehaviour, bramble.Conducta.Spell);
            Assert.DoesNotContain(foe.Buffs.Puestos, b => b.HechizoOrigen == BrambleBehaviour && b.EffectId == MpRemoval);

            // As the jailer's turn starts (FightHandler.ConfirmAsync's turn start).
            var answer = typeof(FightHandler).GetMethod("NonPlayingSummonsAnswerAsync",
                                                        BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(answer);
            await (Task)answer.Invoke(null, new object[] { null, fight, jailer, EffectEngine.AlEmpezarElTurno });

            Assert.Contains(foe.Buffs.Puestos, b => b.HechizoOrigen == BrambleBehaviour && b.EffectId == MpRemoval);
        }
    }
}

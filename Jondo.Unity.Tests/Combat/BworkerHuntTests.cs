using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Bworker (478) behaves by 8675: at the end of each of his turns it casts 8676, whose
    /// 128 gives him 3 MP at the start of the next one -- and whose 2792 casts its grade 2 (406,
    /// the removal of 8676) on any enemy standing next to him. Hooked at his own turn end, the
    /// hook has to live through the round change to his next turn start.
    /// </summary>
    [Collection("MapManager")]
    public class BworkerHuntTests
    {
        private const int Bworker = 478, Behaviour = 8675, Hunt = 8676, MpGain = 128;

        private static async Task<Fighter> PlayRoundAsync(int enemyCell)
        {
            var boss = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 251, IsMonster = true, MonsterId = Bworker, Level = 180,
                MaxHP = 14000, CurrentHP = 14000, MaxAP = 12, CurrentAP = 12, MaxMP = 1, CurrentMP = 1,
            };
            var player = new Fighter
            {
                Id = 10, TeamId = 0, CellId = enemyCell, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            var fight = new FightInstance(Bworker, 990_000_478);
            fight.AddPlayer(player);
            fight.AddOpponent(boss);
            fight.StartFight();

            boss.Conducta = (Behaviour, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);
            while (fight.CurrentFighter != boss) fight.NextTurn();
            await FightHandler.EngancheAsync(null, fight, boss, EffectEngine.AlAcabarElTurno);
            fight.NextTurn();
            while (fight.CurrentFighter != boss) fight.NextTurn();
            await FightHandler.EngancheAsync(null, fight, boss, EffectEngine.AlEmpezarElTurno);
            return boss;
        }

        [Fact]
        public async Task Alone_he_starts_his_next_turn_with_three_more_MP()
        {
            if (!SpellEffects.De(Hunt, 1).Any(row => row.EffectId == MpGain)) return;   // no spell data
            var boss = await PlayRoundAsync(enemyCell: 400);
            Assert.Contains(boss.Buffs.Puestos, b => b.EffectId == MpGain && b.Cuanto == 3);
        }

        [Fact]
        public async Task An_enemy_at_his_side_takes_the_hunt_away()
        {
            if (!SpellEffects.De(Hunt, 1).Any(row => row.EffectId == MpGain)) return;   // no spell data
            var boss = await PlayRoundAsync(enemyCell: MapGeometry.GetNeighbors(251).First());
            Assert.DoesNotContain(boss.Buffs.Puestos, b => b.EffectId == MpGain);
        }
    }
}

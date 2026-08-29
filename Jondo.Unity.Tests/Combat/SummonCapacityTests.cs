using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Combat;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    public class SummonCapacityTests
    {
        [Fact]
        public async Task A_summon_at_capacity_is_rejected_before_AP_is_spent_and_warns_the_client()
        {
            var fight = new FightInstance(1, 1);
            var caster = new Fighter { Id = 10, CurrentHP = 100, CurrentAP = 6 };
            fight.AddPlayer(caster);
            fight.AddPlayer(new Fighter
            {
                Id = -2,
                CurrentHP = 100,
                Invocador = caster.Id,
            });

            var summonEffect = new SpellEffect
            {
                EffectId = EffectSupport.Summon,
                DiceNum = 123,
                Triggers = EffectEngine.AlLanzar,
            };
            var sent = new List<byte[]>();

            bool paid = await FightHandler.TryPayCastCostAsync(
                fight, caster, new[] { summonEffect }, cost: 3,
                packet =>
                {
                    sent.Add(packet);
                    return Task.CompletedTask;
                });

            Assert.False(paid);
            Assert.Equal(6, caster.CurrentAP);
            Assert.Single(sent);
            Assert.Equal(
                ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(
                        InfoMessages.Warning,
                        InfoMessages.SummonLimitReached,
                        FightHandler.BasePlayerSummonLimit.ToString())),
                sent[0]);
        }

        [Fact]
        public void Player_capacity_combines_base_equipment_and_buffs_once()
        {
            var fighter = new Fighter { Id = 10 };
            fighter.Otras[26] = 3;
            fighter.Buffs.Poner(new Buff
            {
                EffectId = 1,
                EffectUid = 1,
                Caracteristica = 26,
                Cuanto = 2,
                EmpiezaEnRonda = 0,
                CaducaEnRonda = -1,
                Quien = fighter.Id,
            }, () => 1);

            Assert.Equal((1L, 3L), FightHandler.SummonCharacteristicFor(fighter));
            Assert.Equal(6, FightHandler.SummonLimitFor(fighter, round: 0));
        }

        [Fact]
        public void A_monster_reads_zero_and_that_is_why_it_has_no_cap()
        {
            // The obvious reading of SummonCharacteristicFor is that a monster takes the number
            // from its template. It does not, and pinning it here is the point: Fighter.Otras is
            // written in exactly one place, RellenarLaFicha, and that runs for the player fighter
            // and nobody else. So a monster reads 0, SummonLimitFor returns 0, and the `limit > 0`
            // guard reads 0 as "no cap".
            //
            // That is the behaviour this server already had. It is written down rather than fixed
            // because what a monster's real cap should be is in none of the data here, and
            // inventing a number would change every fight against a summoner.
            var monster = new Fighter { Id = 20, IsMonster = true };
            monster.Otras[26] = 4;

            Assert.Equal((0L, 4L), FightHandler.SummonCharacteristicFor(monster));
            Assert.Equal(4, FightHandler.SummonLimitFor(monster, round: 0));

            var untouched = new Fighter { Id = 21, IsMonster = true };
            Assert.Equal((0L, 0L), FightHandler.SummonCharacteristicFor(untouched));
            Assert.Equal(0, FightHandler.SummonLimitFor(untouched, round: 0));
        }

        [Fact]
        public void A_player_with_no_summon_gear_can_still_have_one()
        {
            // The quieter half of the bug this fixes. The old code added the equipment bonus a
            // second time on top of Otras[26], which already holds it, and for a character with no
            // summon gear the total came out 0 -- which the `> 0` guard reads as unlimited. The cap
            // had never once fired.
            var plain = new Fighter { Id = 11 };

            Assert.Equal((1L, 0L), FightHandler.SummonCharacteristicFor(plain));
            Assert.Equal(1, FightHandler.SummonLimitFor(plain, round: 0));
        }

        [Fact]
        public async Task A_summon_below_capacity_pays_its_AP_normally()
        {
            var fight = new FightInstance(1, 1);
            var caster = new Fighter { Id = 10, CurrentHP = 100, CurrentAP = 6 };
            fight.AddPlayer(caster);
            var summonEffect = new SpellEffect
            {
                EffectId = EffectSupport.Summon,
                DiceNum = 123,
                Triggers = EffectEngine.AlLanzar,
            };
            var sent = new List<byte[]>();

            bool paid = await FightHandler.TryPayCastCostAsync(
                fight, caster, new[] { summonEffect }, cost: 3,
                packet =>
                {
                    sent.Add(packet);
                    return Task.CompletedTask;
                });

            Assert.True(paid);
            Assert.Equal(3, caster.CurrentAP);
            Assert.Empty(sent);
        }
    }
}

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

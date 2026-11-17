using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Tengu Gélifux (2967) summons with Firuleta del Amor (2683): a Yokai or a Yomi at random
    /// while he carries neither 5331 nor 5332, the Yokai (2888) while he carries 5332, the Yomi
    /// (2891) while he carries 5331. The companions hand those states out themselves: the Yomi's
    /// starting spell, Pelotazo (669), has "950 5332 on a,P,F2967" over the whole board, the Yokai's
    /// (668) "950 5331" on the same mask. A Yomi placed with his group is no summon of his, and the
    /// P of that mask left him without the state, so he summoned a second Yomi next to it.
    /// </summary>
    public class TenguYomiTests
    {
        private const int Tengu = 2967, Yomi = 2891, Fuji = 2889;
        private const int Pelotazo = 669;
        private const int AYomiStands = 5332, AYokaiStands = 5331;

        private static bool HasData => SpellEffects.De(Pelotazo, 1).Any(row => row.Value == AYomiStands);

        private static Fighter Monster(long id, int template, int cell) => new()
        {
            Id = id, CellId = cell, MaxHP = 3000, CurrentHP = 3000, Level = 170, IsMonster = true,
            MonsterId = template, MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4,
        };

        private static (FightInstance Fight, Fighter Tengu, Fighter Fuji, Fighter Character) Board()
        {
            var fight = new FightInstance(1, 1);
            var character = new Fighter
            {
                Id = 10, CellId = 100, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 10, CurrentAP = 10, MaxMP = 5, CurrentMP = 5,
            };
            fight.AddPlayer(character);
            var tengu = Monster(-1, Tengu, 400);
            var fuji = Monster(-2, Fuji, 404);
            fight.AddMonster(tengu);
            fight.AddMonster(fuji);
            return (fight, tengu, fuji, character);
        }

        /// <summary>The Yomi of his own group, at the start of the fight: the Tengu knows one stands.</summary>
        [Fact]
        public async Task A_yomi_placed_with_his_group_gives_him_its_state_at_the_start()
        {
            if (!HasData) return;
            var (fight, tengu, fuji, character) = Board();
            var yomi = Monster(-3, Yomi, 408);
            fight.AddMonster(yomi);
            fight.StartFight();

            yomi.Conducta = (Pelotazo, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, yomi);

            Assert.True(tengu.Buffs.TieneEstado(AYomiStands));
            Assert.False(tengu.Buffs.TieneEstado(AYokaiStands));
            // F2967: the Tengu, and nobody else of the group, nor the Yomi itself.
            Assert.False(fuji.Buffs.TieneEstado(AYomiStands));
            Assert.False(yomi.Buffs.TieneEstado(AYomiStands));
            Assert.False(character.Buffs.TieneEstado(AYomiStands));
        }

        /// <summary>The state goes with the Yomi: dead, the Tengu is back to drawing.</summary>
        [Fact]
        public async Task The_state_goes_when_the_yomi_dies()
        {
            if (!HasData) return;
            var (fight, tengu, _, character) = Board();
            var yomi = Monster(-3, Yomi, 408);
            fight.AddMonster(yomi);
            fight.StartFight();
            yomi.Conducta = (Pelotazo, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, yomi);
            Assert.True(tengu.Buffs.TieneEstado(AYomiStands));

            await FightHandler.TestKillAsync(null, fight, yomi, character);

            Assert.False(tengu.Buffs.TieneEstado(AYomiStands));
        }

        /// <summary>The Yomi he summons himself gives it too, as it always did.</summary>
        [Fact]
        public async Task A_yomi_he_summons_gives_him_its_state()
        {
            if (!HasData) return;
            var (fight, tengu, fuji, _) = Board();
            fight.StartFight();
            var yomi = Monster(-3, Yomi, 408);
            yomi.SummonCost = 1;
            fight.Invocar(yomi, tengu);

            yomi.Conducta = (Pelotazo, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, yomi);

            Assert.True(tengu.Buffs.TieneEstado(AYomiStands));
            Assert.False(fuji.Buffs.TieneEstado(AYomiStands));
        }
    }
}

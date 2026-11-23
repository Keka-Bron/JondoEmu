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
    /// The Kéfriho mine's boss (2942) stands under "Hard as Stone" (2408, state 56). His Cream
    /// Ice (2401) marks a zombie (state 74) with a 792 on X: when it dies, it casts Instant
    /// (2404), whose 952 lifts state 56 from the boss if he stands in the cross next to it.
    /// </summary>
    [Collection("MapManager")]
    public class EsmigolZombieTests
    {
        private static Fighter Monster(int id, int monsterId, int hp) => new()
        {
            Id = id, Name = "M", IsMonster = true, Level = 200, MaxHP = hp, CurrentHP = hp,
            MaxAP = 12, CurrentAP = 12, MaxMP = 4, CurrentMP = 4, MonsterId = monsterId,
        };

        private static async Task<Fighter> KillTheZombieNextTo(int zombieCell)
        {
            var player = new Fighter
            {
                Id = 9_971_001, Name = "P", CellId = 250, Level = 200, MaxHP = 5000, CurrentHP = 5000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
            };
            var boss = Monster(-1, 2942, 20000);
            var zombie = Monster(-2, 2935, 10);
            var fight = new FightInstance(2942, 990_002_942);
            fight.AddPlayer(player);
            fight.AddMonster(boss);
            fight.AddMonster(zombie);
            player.CellId = 250; boss.CellId = 338; zombie.CellId = zombieCell;
            fight.StartFight();
            await FightHandler.AplicarEfectosAsync(null, fight, boss, 2408, 5, boss,
                                                   EffectEngine.AlLanzar, boss.CellId);
            Assert.True(boss.Buffs.TieneEstado(56));
            zombie.Buffs.PonerEstado(74);
            await FightHandler.AplicarEfectosAsync(null, fight, boss, 2401, 5, zombie,
                                                   EffectEngine.AlLanzar, zombie.CellId);

            var blow = SpellEffects.De(6773, 1).First(e => e.EffectId == 97);
            await FightHandler.UnGolpeAsync(null, fight, player, 6773, blow, 4, zombie, 95, 0, false);
            Assert.False(zombie.IsAlive);
            return boss;
        }

        [Fact]
        public async Task A_zombie_dying_next_to_the_boss_lifts_his_invulnerability()
        {
            if (!SpellEffects.De(2401, 5).Any(row => row.EffectId == 792)) return;   // no spell data
            Assert.Equal(1, MapGeometry.Distance(338, 352));
            var boss = await KillTheZombieNextTo(352);
            Assert.False(boss.Buffs.TieneEstado(56));
        }

        [Fact]
        public async Task A_zombie_dying_two_cells_off_leaves_it()
        {
            if (!SpellEffects.De(2401, 5).Any(row => row.EffectId == 792)) return;   // no spell data
            Assert.Equal(2, MapGeometry.Distance(338, 339));
            var boss = await KillTheZombieNextTo(339);
            Assert.True(boss.Buffs.TieneEstado(56));
        }
    }
}

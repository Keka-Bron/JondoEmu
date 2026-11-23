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
    /// Two of the Count's clockwork monsters answer a blow with a 793 on 'c': 3628 arms 3629
    /// under DR (a blow from afar), and 3415's behaviour 3639 arms 3640 under DM (a blow next
    /// door), which hands the damage on to its enemies.
    /// </summary>
    [Collection("MapManager")]
    public class CountHarebourgClockworkTests
    {
        private static Fighter Player() => new()
        {
            Id = 9_970_801, Name = "P", CellId = 250, Level = 200, MaxHP = 5000, CurrentHP = 5000,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
        };

        private static Fighter Monster(int monsterId) => new()
        {
            Id = -1, Name = "M", IsMonster = true, Level = 200, MaxHP = 6000, CurrentHP = 6000,
            MaxAP = 12, CurrentAP = 12, MaxMP = 4, CurrentMP = 4, MonsterId = monsterId,
        };

        // 3629's rows reach those OUTSIDE five cells of it (shape 'I'): a hitter two cells off
        // is untouched, one across the room loses half his life.
        [Fact]
        public async Task A_blow_from_afar_after_3628_takes_life_from_those_far_away()
        {
            if (!SpellEffects.De(3628, 1).Any(row => row.EffectId == 793)) return;   // no spell data
            var player = Player();
            var farAway = Player();
            farAway.Id = 9_970_802;
            var monster = Monster(3411);
            var fight = new FightInstance(3411, 990_003_411);
            fight.AddPlayer(player);
            fight.AddPlayer(farAway);
            fight.AddMonster(monster);
            player.CellId = 250; monster.CellId = 278; farAway.CellId = 500;
            Assert.Equal(2, MapGeometry.Distance(player.CellId, monster.CellId));
            Assert.True(MapGeometry.Distance(farAway.CellId, monster.CellId) > 5);
            fight.StartFight();
            await FightHandler.AplicarEfectosAsync(null, fight, monster, 3628, 1, monster,
                                                   EffectEngine.AlLanzar, monster.CellId);

            var blow = SpellEffects.De(6773, 1).First(e => e.EffectId == 97);
            await FightHandler.UnGolpeAsync(null, fight, player, 6773, blow, 4, monster, 95, 0, false);
            Assert.Equal(5000, player.CurrentHP);
            Assert.Equal(2500, farAway.CurrentHP);
        }

        [Fact]
        public async Task A_melee_blow_on_3415_hands_its_damage_on()
        {
            if (!SpellEffects.De(3639, 1).Any(row => row.EffectId == 793)) return;   // no spell data
            var player = Player();
            var monster = Monster(3415);
            var fight = new FightInstance(3415, 990_003_415);
            fight.AddPlayer(player);
            fight.AddMonster(monster);
            player.CellId = 250; monster.CellId = 264;   // next door
            fight.StartFight();
            monster.Conducta = (3639, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, monster);

            var blow = SpellEffects.De(6773, 1).First(e => e.EffectId == 97);
            await FightHandler.UnGolpeAsync(null, fight, player, 6773, blow, 4, monster, 95, 0, false);
            Assert.True(player.CurrentHP < 5000);
        }
    }
}

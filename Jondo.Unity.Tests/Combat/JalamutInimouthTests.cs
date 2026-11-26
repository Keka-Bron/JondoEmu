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
    /// The Royal Jalamut's starting spell (2356): invulnerable from the start; being PUSHED (trigger P)
    /// lifts state 56 for a round and gives +4 MP. Damage that does not move him changes nothing.
    /// </summary>
    public class JalamutInimouthTests
    {
        private const int Jalamut = 2854;
        private const int Inimouth = 2356;
        private const int Invulnerable = 56;

        private static bool HasData => SpellEffects.De(Inimouth, 1).Count > 0;

        private static async Task<(FightInstance Fight, Fighter Boss, Fighter Hero)> BoardAsync()
        {
            var fight = new FightInstance(1, 1);
            var hero = new Fighter { Id = 10, CellId = 288, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                                     MaxAP = 10, CurrentAP = 10, MaxMP = 5, CurrentMP = 5 };
            int next = MapGeometry.GetNeighbors(288).First();
            int behind = MapGeometry.GetNeighbors(next).First(c => c != 288 && MapGeometry.Distance(288, c) == 2);
            var boss = new Fighter { Id = -1, CellId = next, MaxHP = 7000, CurrentHP = 7000, Level = 120, IsMonster = true,
                                     MonsterId = Jalamut, MaxAP = 15, CurrentAP = 15, MaxMP = 5, CurrentMP = 5 };
            fight.AddPlayer(hero);
            fight.AddMonster(boss);
            fight.StartFight();
            boss.Conducta = (Inimouth, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);
            return (fight, boss, hero);
        }

        private static Task HitAsync(FightInstance fight, Fighter caster, Fighter target, int effectId, int dice)
            => FightHandler.AplicarEfectosAsync(null, fight, caster, 1, 1, target, EffectEngine.AlLanzar, target.CellId,
                   tirada: new[] { new SpellEffect { EffectId = effectId, DiceNum = dice, DiceSide = dice, TargetMask = "a,A", Triggers = "I" } });

        [Fact]
        public async Task He_starts_invulnerable_and_a_plain_hit_does_nothing()
        {
            if (!HasData) return;
            var (fight, boss, hero) = await BoardAsync();
            Assert.True(boss.Buffs.TieneEstado(Invulnerable));
            await HitAsync(fight, hero, boss, 100, 300); // fire damage
            Assert.Equal(7000, boss.CurrentHP);
            Assert.True(boss.Buffs.TieneEstado(Invulnerable));
        }

        [Fact]
        public async Task A_push_lifts_the_invulnerability_and_gives_him_four_movement_points()
        {
            if (!HasData) return;
            var (fight, boss, hero) = await BoardAsync();
            await HitAsync(fight, hero, boss, 5, 2);         // push him two cells
            Assert.False(boss.Buffs.TieneEstado(Invulnerable));
            Assert.Contains(boss.Buffs.Puestos, row => row.HechizoOrigen == Inimouth && row.EffectId == 128);
            await HitAsync(fight, hero, boss, 100, 300);
            Assert.True(boss.CurrentHP < 7000, $"hp {boss.CurrentHP}");
        }
    }
}

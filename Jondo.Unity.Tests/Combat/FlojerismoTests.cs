using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Pistiloso's Flojerismo (15401) hands its victim a disease (15305, then 15336) that the
    /// victim casts on himself (mask C) and that takes a share of his life (1048). It is still
    /// the Pistiloso's blow: an invulnerable fighter loses none of it, a bare one does.
    /// </summary>
    [Collection("MapManager")]
    public class FlojerismoTests
    {
        private const int Pistiloso = 6027, Flojerismo = 15401, Invulnerable = 56;

        private static (FightInstance, Fighter, Fighter) Forge()
        {
            var fight = new FightInstance(1, 1);
            var player = new Fighter
            {
                Id = 10, TeamId = 0, CellId = 300, MaxHP = 40000, CurrentHP = 40000, Level = 200,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            var monster = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 330, MaxHP = 9000, CurrentHP = 9000, Level = 200,
                IsMonster = true, MonsterId = Pistiloso, MaxAP = 8, CurrentAP = 8, MaxMP = 4, CurrentMP = 4,
            };
            fight.AddPlayer(player); fight.AddMonster(monster);
            return (fight, player, monster);
        }

        [Fact]
        public async Task The_disease_takes_no_life_from_an_invulnerable_fighter()
        {
            if (SpellEffects.De(Flojerismo, 1).Count == 0 || SpellStates.Of(Invulnerable) == null) return;
            var (fight, player, monster) = Forge();
            player.Buffs.PonerEstado(Invulnerable);

            await FightHandler.AplicarEfectosAsync(null, fight, monster, Flojerismo, 1, player,
                                                   EffectEngine.AlLanzar, player.CellId);

            Assert.Equal(40000, player.CurrentHP);
        }

        [Fact]
        public async Task The_disease_still_takes_its_share_from_a_bare_fighter()
        {
            if (SpellEffects.De(Flojerismo, 1).Count == 0) return;
            var (fight, player, monster) = Forge();

            await FightHandler.AplicarEfectosAsync(null, fight, monster, Flojerismo, 1, player,
                                                   EffectEngine.AlLanzar, player.CellId);

            Assert.True(player.CurrentHP < 40000);
        }
    }
}

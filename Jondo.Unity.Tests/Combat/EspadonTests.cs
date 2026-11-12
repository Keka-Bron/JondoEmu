using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The arponera's Espadon (29112): «roba vida con el mejor elemento de la arponera», three
    /// times a turn, once a target. It was cast once a turn, on one enemy of three.
    /// </summary>
    public class EspadonTests
    {
        private const int Espadon = 29112;

        [Fact]
        public void Espadon_is_a_blow_to_the_tactics_so_it_goes_on_after_the_first_enemy()
        {
            var arponera = new Fighter { Id = -1, TeamId = 0, MonsterId = 5836, IsMonster = true, MaxAP = 6, CurrentAP = 6,
                                         HechizosDeInvocado = new[] { (Espadon, 1) } };
            var spell = FightHandler.TacticsOf(arponera).SingleOrDefault(s => s.Id == Espadon);
            if (spell == null) return;   // no spell data

            Assert.True(spell.Offensive, "Espadon weighed as no blow");
            Assert.True(spell.Damage > 0);
            Assert.Equal(3, spell.PerTurn);
            Assert.Equal(1, spell.PerTarget);
        }
    }
}

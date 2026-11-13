using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// One turret at EvoluciÃ³n III at a time: 13852's Â«792 13851-4 on a,E135Â» over the whole map has
    /// the turret already at III fall back to II when another reaches it. Reached through the
    /// Turbine, that fall back is the chain's seventh spell, and the chain was cut at six.
    /// </summary>
    public class SteamerEvolutionTests
    {
        private const int Turbine = 13874, EvolutionII = 13851, EvolutionIII = 13852;
        private const int StateII = 134, StateIII = 135;
        private const int Harponneuse = 5836, Gardienne = 5835;

        private static Fighter Turret(long id, int template, int cell, Fighter steamer) => new Fighter
        {
            Id = id, TeamId = 0, CellId = cell, MaxHP = 600, CurrentHP = 600, IsMonster = true,
            MonsterId = template, GradeIndex = 6, Invocador = steamer.Id, SummonCost = 1, Level = 200,
        };

        [Fact]
        public void A_second_turret_at_Evolution_III_sends_the_first_back_to_II()
        {
            if (SpellEffects.De(Turbine, 1).Count == 0) return;   // no spell data

            var fight = new FightInstance(1, 1);
            var steamer = new Fighter { Id = 10, CellId = 300, MaxHP = 3000, CurrentHP = 3000, MaxAP = 12, CurrentAP = 12, Level = 200, Breed = 15 };
            var foe = new Fighter { Id = 20, CellId = 100, MaxHP = 3000, CurrentHP = 3000, Level = 200 };
            fight.AddPlayer(steamer); fight.AddOpponent(foe);
            steamer.CellId = 300; foe.CellId = 100;

            var atIII = Turret(-1, Harponneuse, 330, steamer);
            var atII = Turret(-2, Gardienne, 301, steamer);
            fight.Invocar(atIII, steamer);
            fight.Invocar(atII, steamer);
            atIII.CellId = 330; atII.CellId = 301;
            atIII.Buffs.PonerEstado(StateIII);
            atII.Buffs.PonerEstado(StateII);

            var outcomes = EffectEngine.Resolver(fight, steamer, Turbine, 1, atII,
                                                 EffectEngine.AlLanzar, fight.RoundNumber, celdaApuntada: atII.CellId);

            Assert.Contains(outcomes, o => o.HechizoOrigen == EvolutionIII && o.NivelOrigen == 1 && o.Sobre == atII);
            Assert.Contains(outcomes, o => o.HechizoOrigen == EvolutionII && o.NivelOrigen == 1 && o.Sobre == atIII);
            Assert.DoesNotContain(outcomes, o => o.HechizoOrigen == EvolutionII && o.NivelOrigen == 1 && o.Sobre == atII);
        }
    }
}

using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A spell that ends on a 406 on itself takes away what its earlier casts left, not what the
    /// cast under way has just put: Centinela, «aumenta los daños a distancia y el alcance del
    /// lanzador», lists "406 32475" last, and run on everything its buff never stayed.
    /// </summary>
    public class SelfRemovalTests
    {
        private const int Centinela = 32475;

        [Fact]
        public void Centinela_keeps_its_buff_and_a_recast_replaces_it()
        {
            var fight = new FightInstance(1, 1);
            var ocra = new Fighter { Id = 10, TeamId = 0, CellId = 300, MaxHP = 1000, CurrentHP = 1000, MaxAP = 12, CurrentAP = 12, Breed = 9, Level = 200 };
            fight.AddPlayer(ocra);
            fight.AddMonster(new Fighter { Id = -50, TeamId = 1, CellId = 200, MaxHP = 500, CurrentHP = 500 });

            EffectEngine.Resolver(fight, ocra, Centinela, 1, ocra, EffectEngine.AlLanzar, fight.RoundNumber, celdaApuntada: ocra.CellId);
            Assert.Single(ocra.Buffs.Puestos, r => r.HechizoOrigen == Centinela && r.EffectId == 117);
            Assert.Equal(10, ocra.Buffs.De(19, fight.RoundNumber));

            EffectEngine.Resolver(fight, ocra, Centinela, 1, ocra, EffectEngine.AlLanzar, fight.RoundNumber, celdaApuntada: ocra.CellId);
            Assert.Single(ocra.Buffs.Puestos, r => r.HechizoOrigen == Centinela && r.EffectId == 117);
        }
    }
}

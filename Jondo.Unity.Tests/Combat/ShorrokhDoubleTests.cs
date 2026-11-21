using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Shorrokh (4450) of the Kutulu temple: his hit (6736) has its victim cast 6735, whose
    /// "1189" is «Invoca un doble del lanzador» -- a double of the victim. 1189 had no handler and
    /// stuck an inert row on every fighter in its circle; it is the same double as 180, put on the
    /// nearest free cell when the aimed one is taken.
    /// </summary>
    [Collection("MapManager")]
    public class ShorrokhDoubleTests
    {
        private const int Shorrokh = 4450, Golpe = 6736;
        private const long Board = 121374214;

        [Fact]
        public void His_hit_brings_out_a_double_of_the_victim()
        {
            if (SpellEffects.De(Golpe, 1).Count == 0 || SpellEffects.De(6735, 1).Count == 0) return;   // no spell data
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            var walkable = MapManager.GetFightWalkable(Board);
            if (walkable == null || !walkable.Contains(315) || !walkable.Contains(229)) return;   // no map data

            var fight = new FightInstance(1, Board, Board);
            var me = new Fighter { Id = 10, TeamId = 0, CellId = 229, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                                   MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var shorrokh = new Fighter { Id = -1, TeamId = 1, CellId = 315, MaxHP = 3000, CurrentHP = 3000, Level = 200,
                                         IsMonster = true, MonsterId = Shorrokh, MaxAP = 8, CurrentAP = 8, MaxMP = 4, CurrentMP = 4 };
            fight.AddPlayer(me); fight.AddMonster(shorrokh);
            fight.StartFight();

            var outcomes = EffectEngine.Resolver(fight, shorrokh, Golpe, 1, me, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: me.CellId);

            var twin = Assert.Single(outcomes, o => o.Doble != null);
            Assert.Equal(me.Id, twin.Doble.DoubleOf);
            Assert.NotEqual(me.CellId, twin.CasillaDeLaInvocacion);
            Assert.NotEqual(shorrokh.CellId, twin.CasillaDeLaInvocacion);
            Assert.Contains(twin.CasillaDeLaInvocacion, walkable);
            Assert.DoesNotContain(outcomes, o => o.Efecto?.EffectId == EffectEngine.InvocaUnDobleDelLanzador && o.Doble == null);
        }
    }
}

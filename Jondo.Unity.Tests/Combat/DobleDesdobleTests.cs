using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Doble (3748) opens the fight with its behaviour, Desdoble (4777, grade 2): a double of
    /// itself. A behaviour is cast on the caster's own cell, which it stands on, so the double was
    /// refused for an occupied cell and never came out. It comes out on the nearest free cell.
    /// </summary>
    [Collection("MapManager")]
    public class DobleDesdobleTests
    {
        private const int Doble = 3748, Desdoble = 4777;
        private const long Board = 121374214;

        [Fact]
        public void The_Doble_s_behaviour_brings_its_double_out_beside_it()
        {
            if (SpellEffects.De(Desdoble, 2).Count == 0) return;   // no spell data
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            var walkable = MapManager.GetFightWalkable(Board);
            if (walkable == null || !walkable.Contains(315) || !walkable.Contains(229)) return;   // no map data

            var fight = new FightInstance(1, Board, Board);
            var me = new Fighter { Id = 10, TeamId = 0, CellId = 229, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                                   MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var doble = new Fighter { Id = -1, TeamId = 1, CellId = 315, MaxHP = 3000, CurrentHP = 3000, Level = 200,
                                      IsMonster = true, MonsterId = Doble, MaxAP = 8, CurrentAP = 8, MaxMP = 4, CurrentMP = 4 };
            fight.AddPlayer(me); fight.AddMonster(doble);
            fight.StartFight();

            var outcomes = EffectEngine.Resolver(fight, doble, Desdoble, 2, doble, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: doble.CellId);

            var twin = Assert.Single(outcomes, o => o.Doble != null);
            Assert.NotEqual(doble.CellId, twin.CasillaDeLaInvocacion);
            Assert.NotEqual(me.CellId, twin.CasillaDeLaInvocacion);
            Assert.Contains(twin.CasillaDeLaInvocacion, walkable);
        }
    }
}

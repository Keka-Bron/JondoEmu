using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Kutulu larva (4453) casts Vacilación del Universo (6752) at her turn start from her
    /// behaviour (6749, "792 under TB"): grade 1 casts grade 2 at every enemy player, which has
    /// him cast grade 3 and then grade 4 at her, "4 on C" -- a teleport of himself aimed at her
    /// own cell. It brings him to her contact; aimed at a taken cell, it used to be refused.
    /// </summary>
    [Collection("MapManager")]
    public class KutuluVacillationTests
    {
        private const int Larva = 4453, Vacilacion = 6752;
        private const long Board = 121374214;
        private const int LarvaCell = 315;

        private static (FightInstance, Fighter, Fighter) Fight(int myCell)
        {
            var fight = new FightInstance(1, Board, Board);
            var me = new Fighter { Id = 10, TeamId = 0, CellId = myCell, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                                   MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var larva = new Fighter { Id = -1, TeamId = 1, CellId = LarvaCell, MaxHP = 30000, CurrentHP = 30000, Level = 200,
                                      IsMonster = true, MonsterId = Larva, MaxAP = 8, CurrentAP = 8, MaxMP = 4, CurrentMP = 4 };
            fight.AddPlayer(me); fight.AddMonster(larva);
            fight.StartFight();
            return (fight, me, larva);
        }

        private static bool Ready()
        {
            if (SpellEffects.De(Vacilacion, 4).Count == 0) return false;   // no spell data
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            var walkable = MapManager.GetFightWalkable(Board);
            return walkable != null && walkable.Contains(LarvaCell) && walkable.Contains(229);
        }

        [Fact]
        public void It_brings_an_enemy_to_her_contact()
        {
            if (!Ready()) return;
            var (fight, me, larva) = Fight(229);
            EffectEngine.Resolver(fight, larva, Vacilacion, 1, larva, EffectEngine.AlLanzar,
                                  fight.RoundNumber, celdaApuntada: larva.CellId);
            Assert.Equal(1, MapGeometry.Distance(me.CellId, larva.CellId));
            Assert.Equal(LarvaCell, larva.CellId);
        }

        [Fact]
        public void One_already_in_her_contact_stays_where_he_is()
        {
            if (!Ready()) return;
            var (fight, me, larva) = Fight(300);
            Assert.Equal(1, MapGeometry.Distance(300, LarvaCell));
            EffectEngine.Resolver(fight, larva, Vacilacion, 1, larva, EffectEngine.AlLanzar,
                                  fight.RoundNumber, celdaApuntada: larva.CellId);
            Assert.Equal(300, me.CellId);
        }
    }
}

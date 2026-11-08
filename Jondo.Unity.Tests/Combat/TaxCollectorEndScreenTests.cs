using System;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A beaten tax collector among the enemies of the end screen: the client draws an enemy's
    /// face only for a monster, so it is listed again as monster 3859 just before the end, and
    /// its jyg entry no longer carries the alliance that made it an empty head, a «?».
    /// </summary>
    public class TaxCollectorEndScreenTests
    {
        [Fact]
        public void The_tax_collector_is_listed_again_as_the_monster_it_is()
        {
            var fight = new FightInstance(1, 1) { Reglas = FightRules.ContraRecaudador };
            var attacker = new Fighter { Id = -2, TeamId = 0, CellId = 300, MaxHP = 500, CurrentHP = 500, Level = 60,
                                         IsMonster = true, MonsterId = 494, Look = "{1|}" };
            var collector = new Fighter { Id = -1, TeamId = 1, CellId = 301, MaxHP = 0, CurrentHP = 0, Level = 120,
                                          IsMonster = true, MonsterId = TaxCollectorProtocol.Monster,
                                          TaxCollectorFirstName = 35, TaxCollectorLastName = 109, Look = "{1|}" };
            fight.AddPlayer(attacker);
            fight.AddOpponent(collector);

            string refresh = Convert.ToHexString(FightHandler.CollectorAsMonsterRefresh(fight));

            string asMonster = Convert.ToHexString(FightProtocol.MonsterIdentity(1, TaxCollectorProtocol.Monster, 120).Build());
            string byItsNames = Convert.ToHexString(FightProtocol.TaxCollectorIdentity(35, 109, TaxCollectorProtocol.Monster).Build());
            Assert.Contains(asMonster, refresh);
            Assert.DoesNotContain(byItsNames, refresh);
        }

        [Fact]
        public void A_fight_without_a_tax_collector_needs_no_refresh()
        {
            var fight = new FightInstance(1, 1);
            fight.AddPlayer(new Fighter { Id = -2, TeamId = 0, CellId = 300, IsMonster = true, MonsterId = 494, Look = "{1|}" });
            fight.AddOpponent(new Fighter { Id = -1, TeamId = 1, CellId = 301, IsMonster = true, MonsterId = 494, Look = "{1|}" });

            Assert.Null(FightHandler.CollectorAsMonsterRefresh(fight));
        }
    }
}

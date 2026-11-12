using System;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The tax collector of the end screen is a row with its level, on its side, for both sides:
    /// the client names it after its first and last name. It is no longer told again as monster
    /// 3859 before the end: the screen drew every monster as an enemy's icon on the losers' side,
    /// under the defenders' winners and the attackers' losers alike.
    /// </summary>
    public class TaxCollectorEndScreenTests
    {
        [Fact]
        public void The_tax_collector_stays_known_by_its_names()
        {
            var fight = new FightInstance(1, 1) { Reglas = FightRules.ContraRecaudador };
            var attacker = new Fighter { Id = -2, TeamId = 0, CellId = 300, MaxHP = 500, CurrentHP = 500, Level = 60,
                                         IsMonster = true, MonsterId = 494, Look = "{1|}" };
            var collector = new Fighter { Id = -1, TeamId = 1, CellId = 301, MaxHP = 0, CurrentHP = 0, Level = 120,
                                          IsMonster = true, MonsterId = TaxCollectorProtocol.Monster,
                                          TaxCollectorFirstName = 35, TaxCollectorLastName = 109, Look = "{1|}" };
            fight.AddPlayer(attacker);
            fight.AddOpponent(collector);

            string block = Convert.ToHexString(FightHandler.BloqueDe(fight, collector).Build());

            string asMonster = Convert.ToHexString(FightProtocol.MonsterIdentity(1, TaxCollectorProtocol.Monster, 120).Build());
            string byItsNames = Convert.ToHexString(FightProtocol.TaxCollectorIdentity(35, 109, TaxCollectorProtocol.Monster).Build());
            Assert.Contains(byItsNames, block);
            Assert.DoesNotContain(asMonster, block);
        }

        /// <summary>
        /// Its row carries its level, as a person's does, on whichever side it ended: a row with none
        /// is a monster's, which the screen only draws as an enemy's icon.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void The_tax_collector_s_row_carries_its_level_on_either_side(bool won)
        {
            byte[] jyg = FightProtocol.BuildFightResults(new[]
            {
                new FightProtocol.FightResult { Fighter = -1, Winner = won, Level = 200, Xp = ExperienceTable.LevelFloor(200) },
            }, durationMs: 1000);

            var row = ProtoMessage.Parse(ProtoMessage.Parse(jyg).Fields.Single(f => f.FieldNumber == 2).BytesValue);
            var who = ProtoMessage.Parse(row.Fields.Single(f => f.FieldNumber == 3).BytesValue);
            var person = ProtoMessage.Parse(who.Fields.Single(f => f.FieldNumber == 2).BytesValue);
            Assert.Equal(200, person.Fields.Single(f => f.FieldNumber == 2).VarIntValue);
            Assert.Equal(won, row.Fields.Any(f => f.FieldNumber == 4 && f.VarIntValue == 2));
        }
    }
}

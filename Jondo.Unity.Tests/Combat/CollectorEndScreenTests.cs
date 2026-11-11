using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A tax collector's row of the end screen (jyg). The 3.6.10.11 client (Cpp2IL, fkv.bgdw) copies
    /// the oneof case of the row's «who» (lmk) into the row's kind: 2 a person, 4 a tax collector
    /// (lmi { f1 the alliance's len }); its name is read off the len. Without the row, a collector that
    /// gathered on the map never showed among the winners.
    /// </summary>
    public class CollectorEndScreenTests
    {
        private static List<ProtoField> Fields(byte[] message) => ProtoMessage.Parse(message).Fields.ToList();

        [Fact]
        public void A_collector_row_is_a_who_of_kind_4_with_its_alliance_and_what_it_took()
        {
            var spoils = new FightProtocol.Spoils();
            spoils.Items.Add((2, 16663));
            var alliance = Pb.New().Str(3, "Les Percepteurs").Str(5, "PERC");
            var jyg = FightProtocol.BuildFightResults(new[]
            {
                new FightProtocol.FightResult { Fighter = -900001, Winner = true, Spoils = spoils, Collector = alliance },
            }, 1000);

            var row = Fields(Assert.Single(Fields(jyg), f => f.FieldNumber == 2).BytesValue);
            var who = Fields(Assert.Single(row, f => f.FieldNumber == 3).BytesValue);
            Assert.DoesNotContain(who, f => f.FieldNumber == 2);                       // not a person
            var lmi = Fields(Assert.Single(who, f => f.FieldNumber == 4).BytesValue);   // a tax collector
            Assert.Equal(alliance.Build(), Assert.Single(lmi, f => f.FieldNumber == 1).BytesValue);
            Assert.Equal(2L, Assert.Single(row, f => f.FieldNumber == 4).VarIntValue);  // among the winners

            var loot = Fields(Assert.Single(row, f => f.FieldNumber == 2).BytesValue);
            var item = Fields(Assert.Single(Fields(Assert.Single(loot, f => f.FieldNumber == 2).BytesValue)).BytesValue);
            Assert.Equal((2L, 16663L), (item.Single(f => f.FieldNumber == 2).VarIntValue, item.Single(f => f.FieldNumber == 4).VarIntValue));
        }
    }
}

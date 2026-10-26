using System;
using System.Linq;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A summon sent again whole -- a reconnection's or a spectator's jxg and jxb -- carries its
    /// owner in its sheet, f1 with f4 set, as its own jwe 181 does (FightProtocol.BuildSummon).
    /// Without them it came back as a monster of nobody's.
    /// </summary>
    public class SummonBlockTests
    {
        private static ProtoMessage SheetOf(Pb block)
        {
            var body = ProtoMessage.Parse(block.Build()).Fields.Single(f => f.FieldNumber == 2).BytesValue;
            var fighter = ProtoMessage.Parse(body).Fields.Single(f => f.FieldNumber == 2).BytesValue;
            var sheet = ProtoMessage.Parse(fighter).Fields.Single(f => f.FieldNumber == 2).BytesValue;
            return ProtoMessage.Parse(sheet);
        }

        [Fact]
        public void A_summon_s_sheet_names_its_owner()
        {
            var sheet = SheetOf(FightProtocol.FighterBlock(300, 1, -5, Array.Empty<(int, long, long)>(),
                Array.Empty<byte>(), FightProtocol.MonsterIdentity(3, 5144, 1), isMonster: true, summoner: 990_000_041));
            Assert.Equal(990_000_041, sheet.Fields.Single(f => f.FieldNumber == 1).VarIntValue);
            Assert.Equal(1, sheet.Fields.Single(f => f.FieldNumber == 4).VarIntValue);
        }

        [Fact]
        public void A_creature_s_missing_life_goes_empty_as_in_every_captured_jxb()
        {
            Assert.Equal(new byte[] { 0x08, 0x61, 0x12, 0x00 },
                FightProtocol.SheetEntry(FightProtocol.TemporaryLifeMalus, 0, -126, isMonster: true).Build());
            Assert.NotEqual(new byte[] { 0x08, 0x61, 0x22, 0x00 },
                FightProtocol.SheetEntry(FightProtocol.TemporaryLifeMalus, -104, -208, isMonster: false).Build());
        }

        [Fact]
        public void A_monster_s_sheet_names_nobody()
        {
            var sheet = SheetOf(FightProtocol.FighterBlock(300, 1, -1, Array.Empty<(int, long, long)>(),
                Array.Empty<byte>(), FightProtocol.MonsterIdentity(1, 494, 50), isMonster: true));
            Assert.DoesNotContain(sheet.Fields, f => f.FieldNumber is 1 or 4);
        }
    }
}

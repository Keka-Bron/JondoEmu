using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A Sram's double sent again whole -- the jxb of a wave, a reconnection, a spectator -- is
    /// the character it copies, not a monster: its { name, level } in the creature case made the
    /// client read the level as a template, and a level-200 Sram's double came back as monster
    /// 200, a wild dragoturkey, in the turn bar and on hovering it.
    /// </summary>
    public class DoubleBlockTests
    {
        [Fact]
        public void A_double_in_a_fighter_block_is_dressed_as_its_original()
        {
            var fight = new FightInstance(1, 1);
            var sram = new Fighter { Id = 1, Name = "Copied", TeamId = 0, CellId = 300, MaxHP = 1150, CurrentHP = 1150, Level = 200,
                                     MaxAP = 7, CurrentAP = 7, MaxMP = 3, CurrentMP = 3, IsBot = true, Breed = 4, Sex = 1 };
            var foe = new Fighter { Id = -1, TeamId = 1, CellId = 400, MaxHP = 50, CurrentHP = 50, Level = 50, MaxAP = 6, CurrentAP = 6,
                                    MaxMP = 3, CurrentMP = 3, IsMonster = true, MonsterId = 494 };
            fight.AddPlayer(sram); fight.AddOpponent(foe);
            var doble = EffectEngine.DobleDe(fight, sram, 314);
            fight.Invocar(doble, sram);

            foreach (byte[] block in new[] { FightHandler.BloqueDe(fight, doble).Build(),
                                             ProtoMessage.Parse(FightHandler.CreatureFighter(fight, doble)).Fields.Single(f => f.FieldNumber == 2).BytesValue })
            {
                var top = ProtoMessage.Parse(block).Fields;
                Assert.Equal(doble.Id, top.Single(f => f.FieldNumber == 3).VarIntValue);
                var body = ProtoMessage.Parse(top.Single(f => f.FieldNumber == 2).BytesValue).Fields;
                var fighter = ProtoMessage.Parse(body.Single(f => f.FieldNumber == 2).BytesValue).Fields;

                // No creature case: that is where { name, level } read as { grade, template }.
                Assert.DoesNotContain(fighter, f => f.FieldNumber == 3);
                Assert.Equal(doble.Id, fighter.Single(f => f.FieldNumber == 1).VarIntValue);

                // The person's identity: his class and sex, the double's name.
                var who = ProtoMessage.Parse(fighter.Single(f => f.FieldNumber == 6).BytesValue).Fields;
                Assert.Equal("Copied", System.Text.Encoding.UTF8.GetString(who.Single(f => f.FieldNumber == 7).BytesValue));
                var breed = ProtoMessage.Parse(who.Single(f => f.FieldNumber == 2).BytesValue).Fields;
                Assert.Equal(4, breed.Single(f => f.FieldNumber == 1).VarIntValue);
                Assert.Equal(1, breed.Single(f => f.FieldNumber == 2).VarIntValue);

                // Still its owner's summon, by the sheet.
                var sheet = ProtoMessage.Parse(fighter.Single(f => f.FieldNumber == 2).BytesValue).Fields;
                Assert.Equal(sram.Id, sheet.Single(f => f.FieldNumber == 1).VarIntValue);
            }
        }
    }
}

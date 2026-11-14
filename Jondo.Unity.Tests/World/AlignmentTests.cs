using System;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Protocol;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The alignment: Chantal Inea's oath, Morry Gann's way back to neutral, and the side as the
    /// actor, the sheet and the criteria carry it.
    /// </summary>
    public class AlignmentTests
    {
        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        [Fact]
        public void The_fee_doubles_through_its_ten_steps_and_a_change_waits_two_weeks()
        {
            Assert.Equal(500, Alignments.LeaveCost(0));
            Assert.Equal(1000, Alignments.LeaveCost(1));
            Assert.Equal(256_000, Alignments.LeaveCost(9));
            Assert.Equal(256_000, Alignments.LeaveCost(12));

            var lately = new Alignments.Alignment(Alignments.Bonta, 0, Now - (long)TimeSpan.FromDays(13).TotalMilliseconds);
            var long_ago = lately with { ChangedMs = Now - (long)TimeSpan.FromDays(15).TotalMilliseconds };
            Assert.False(Alignments.MayChange(lately, Now));
            Assert.True(Alignments.MayChange(long_ago, Now));
            Assert.True(Alignments.MayChange(new Alignments.Alignment(Alignments.Neutral, 0, 0), Now));
        }

        [Fact]
        public async Task Chantal_has_one_swear_loyalty_to_a_city()
        {
            const long me = 9_400_300_001;
            Alignments.Set(me, Alignments.Neutral, 0);
            await using var pipe = await ClientPipe.OpenAsync(me, me, 153356294, "Jurador");
            using (SessionContext.Push(pipe.Session))
            {
                var (question, replies) = AlignmentHandler.Opening(AlignmentHandler.Chantal);
                Assert.Equal(36023, question);
                Assert.Equal(new long[] { 47609, 47612, 47616 }, replies);

                (question, replies) = await AlignmentHandler.ReplyAsync(pipe.ToClient, AlignmentHandler.Chantal, 47609);
                Assert.Equal(36024, question);
                Assert.Equal(new long[] { 47611, 47610 }, replies);

                (question, _) = await AlignmentHandler.ReplyAsync(pipe.ToClient, AlignmentHandler.Chantal, 47611);
                Assert.Equal(0, question);
                Assert.Equal(Alignments.Bonta, Alignments.Of(me).Side);
                Assert.Equal(1, Alignments.CriterionSide(me));
                await pipe.Next(Op.Kub);

                Assert.Equal(new long[] { 47615 }, AlignmentHandler.Opening(AlignmentHandler.Chantal).Replies);
            }
            Alignments.Forget(me);
            Assert.Equal(Alignments.Bonta, Alignments.Of(me).Side);   // kept in the base
        }

        [Fact]
        public async Task Morry_Gann_takes_his_fee_and_makes_one_neutral()
        {
            const long me = 9_400_300_002;
            Alignments.Set(me, Alignments.Brakmar, Now - (long)TimeSpan.FromDays(15).TotalMilliseconds);
            await using var pipe = await ClientPipe.OpenAsync(me, me, 95685125, "Arrepentido");
            using (SessionContext.Push(pipe.Session))
            {
                pipe.Session.State.Kamas = 1000;
                var opening = AlignmentHandler.Opening(AlignmentHandler.Morry);
                Assert.Equal(57403, opening.Question);
                Assert.Equal(new long[] { 78119, 78120 }, opening.Replies);

                async Task Expect(long reply, long question, params long[] replies)
                {
                    var next = await AlignmentHandler.ReplyAsync(pipe.ToClient, AlignmentHandler.Morry, reply);
                    Assert.Equal(question, next.Question);
                    Assert.Equal(replies, next.Replies);
                }

                await Expect(78119, 57404, 78117);
                await Expect(78117, 57405, 78114, 78115, 78113);
                await Expect(78115, 57406, 78122, 78116);          // his step, the first: 500 kamas
                await Expect(78122, 57408, 78121);
                await Expect(78114, 57418, 78144, 78142);          // «seguir siendo leal a Brakmar»
                await Expect(78144, 57419, 78171, 78166, 78167);   // the Brakmarian's «Pagar 500 kamas...»
                await Expect(78171, 57433, 78170);                 // «Una firma aquí...»

                Assert.Equal(500, pipe.Session.State.Kamas);
                Assert.Equal(Alignments.Neutral, Alignments.Of(me).Side);
                Assert.Equal(new long[] { 78120 }, AlignmentHandler.Opening(AlignmentHandler.Morry).Replies);

                // Sworn again at once, he has to wait two weeks to leave again.
                Alignments.Set(me, Alignments.Bonta, Now);
                await Expect(78119, 57407, 78118);

                // And without the kamas there is no signing.
                Alignments.Set(me, Alignments.Bonta, 0);
                pipe.Session.State.Kamas = 100;
                await Expect(78144, 57420, 78145);
            }
        }

        [Fact]
        public async Task The_pvp_switch_shows_on_the_sheet_and_the_wings()
        {
            const long me = 9_400_300_004;
            Alignments.Set(me, Alignments.Bonta, 0);
            await using var pipe = await ClientPipe.OpenAsync(me, me, 990_100_131, "Alado");
            using (SessionContext.Push(pipe.Session))
            {
                // hnu { 1 true }: the sheet's honour block says «PvP enabled, aggressable», and a grade.
                await AlignmentHandler.PvpAsync(pipe.ToClient, ConnectionProtocol.Push("hnu", Pb.New().Var(1, 1).Build()));
                var sheet = Sub(Sub(ProtoMessage.Parse(await pipe.Next(Op.Kub)), 2), 9);
                Assert.Equal(1, Sub(sheet, 3).Fields.First(f => f.FieldNumber == 2).VarIntValue);
                Assert.Equal(1, sheet.Fields.First(f => f.FieldNumber == 4).VarIntValue);
                Alignments.Forget(me);
                Assert.True(Alignments.Of(me).PvpOn);   // kept in the base

                // And the others see it: the actor's block carries the status and the grade.
                var character = new DbCharacter { Id = me, Name = "Alado", Level = 120, Breed = 1 };
                var actor = MapActorMessages.Player(character, 300, 1, 1);
                var block = MapActorAssertions.Human(actor).AlignmentInformation!;
                Assert.Equal(AttackableStatus.PvpEnabledAttackable, block.ExtendedInformation!.Attackable);
                Assert.Equal(1, block.AlignmentGrade);

                // hnu {}: off again, the block as every player of the captures has it.
                await AlignmentHandler.PvpAsync(pipe.ToClient, ConnectionProtocol.Push("hnu", Array.Empty<byte>()));
                sheet = Sub(Sub(ProtoMessage.Parse(await pipe.Next(Op.Kub)), 2), 9);
                Assert.DoesNotContain(Sub(sheet, 3).Fields, f => f.FieldNumber == 2);
                Assert.DoesNotContain(sheet.Fields, f => f.FieldNumber == 4);

                // A neutral one has nothing to switch; swearing again starts with it off.
                Alignments.SetPvp(me, true);
                Alignments.Set(me, Alignments.Neutral, 0);
                Assert.False(Alignments.Of(me).PvpOn);
                await AlignmentHandler.PvpAsync(pipe.ToClient, ConnectionProtocol.Push("hnu", Pb.New().Var(1, 1).Build()));
                Assert.False(Alignments.Of(me).Pvp);
            }
        }

        [Fact]
        public void Only_the_other_city_s_winged_may_be_attacked_and_not_fifty_levels_up()
        {
            const long angel = 9_400_300_005, demon = 9_400_300_006, other_angel = 9_400_300_007;
            Alignments.Set(angel, Alignments.Bonta, 0);
            Alignments.Set(demon, Alignments.Brakmar, 0);
            Alignments.Set(other_angel, Alignments.Bonta, 0);

            // Nobody's PvP on: nothing.
            Assert.False(Alignments.MayAggress(angel, 100, demon, 100));

            // His own on, the target's off: the target shows no grade, no wings.
            Alignments.SetPvp(angel, true);
            Assert.False(Alignments.MayAggress(angel, 100, demon, 100));

            Alignments.SetPvp(demon, true);
            Alignments.SetPvp(other_angel, true);
            Assert.True(Alignments.MayAggress(angel, 100, demon, 150));
            Assert.False(Alignments.MayAggress(angel, 100, demon, 151));     // more than fifty up
            Assert.True(Alignments.MayAggress(angel, 160, demon, 230));      // both counted up to 200
            Assert.False(Alignments.MayAggress(angel, 100, other_angel, 100)); // his own city
        }

        [Fact]
        public void The_actor_carries_the_side()
        {
            const long me = 9_400_300_003;
            Alignments.Set(me, Alignments.Brakmar, Now);
            var character = new DbCharacter { Id = me, Name = "Negro", Level = 120, Breed = 1 };
            var actor = MapActorMessages.Player(character, 300, 1, 1);
            Assert.Equal(Alignment.Evil, MapActorAssertions.Human(actor).AlignmentInformation!.Alignment);
        }

        private static ProtoMessage Sub(ProtoMessage message, int field)
            => ProtoMessage.Parse(message.Fields.First(f => f.FieldNumber == field && f.WireType == 2).BytesValue);
    }
}

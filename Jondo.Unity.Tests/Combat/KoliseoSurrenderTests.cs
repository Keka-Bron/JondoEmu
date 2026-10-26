using System;
using System.Linq;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Koliseo's giving up, as the client reads it: the ltm that opens the surrender window
    /// (the 2v2 capture's "0801", with the leave button's f2), the window's luw, the ballot's lsp
    /// and the vote's lsq and ltn.
    /// </summary>
    public class KoliseoSurrenderTests
    {
        [Fact]
        public void The_window_opens_with_the_capture_s_field_and_the_button_s()
            => Assert.Equal(new byte[] { 0x08, 0x01, 0x10, 0x01 }, FightProtocol.BuildSurrenderOpen());

        [Fact]
        public void The_window_offers_leaving_and_voting()
        {
            var fields = ProtoMessage.Parse(FightProtocol.BuildSurrenderOptions()).Fields;
            Assert.DoesNotContain(fields, f => f.FieldNumber == 1);

            // lui's case 1 (lue) lets him leave, luu's case 1 (lun) lets him vote: the client takes
            // each oneof's case for the button's state (flv.bgjw → gu).
            var leaving = fields.Single(f => f.FieldNumber == 3);
            var voting = fields.Single(f => f.FieldNumber == 4);
            Assert.Equal(new byte[] { 0x0A, 0x00 }, leaving.BytesValue);
            Assert.Equal(new byte[] { 0x0A, 0x00 }, voting.BytesValue);
        }

        [Fact]
        public void A_ballot_says_yes_by_its_second_field()
        {
            Assert.True(FightProtocol.ReadSurrenderBallot(ConnectionProtocol.Push(Op.Lsp, new byte[] { 0x10, 0x01 })));
            Assert.False(FightProtocol.ReadSurrenderBallot(ConnectionProtocol.Push(Op.Lsp, Array.Empty<byte>())));
            Assert.False(FightProtocol.ReadSurrenderBallot(ConnectionProtocol.Push(Op.Lsp, new byte[] { 0x08, 0x01 })));
        }

        [Fact]
        public void The_vote_counts_its_voters_and_its_yes()
        {
            Assert.Equal(new byte[] { 0x10, 0x02, 0x18, 0x01 }, FightProtocol.BuildSurrenderVote(2, 1));
            Assert.Equal(new byte[] { 0x08, 0x01 }, FightProtocol.BuildSurrenderResult(true));
            Assert.Empty(FightProtocol.BuildSurrenderResult(false));
        }
    }
}

using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;
using Wire = Jondo.Unity.Tests.Combat.PortalTests.Wire;

namespace Jondo.Unity.Tests.Network
{
    /// <summary>
    /// An invitation withdrawn (inq → ilo to the invitee, ind to the party) and the party window's
    /// lock (ila → ilj), as the client's party frames read them.
    /// </summary>
    public sealed class PartyCancelTests
    {
        [Fact]
        public async Task A_withdrawn_invitation_closes_the_invitee_s_popup_and_leaves_the_list()
        {
            const long leader = 9_300_000_011, member = 9_300_000_012, guest = 9_300_000_013;
            await using var lead = await Wire.Open(leader);
            await using var inside = await Wire.Open(member);
            await using var invited = await Wire.Open(guest);
            var party = Parties.Create(leader);
            Assert.True(Parties.Invite(party, member, leader));
            Assert.True(Parties.Accept(party, member));
            Assert.True(Parties.Invite(party, guest, leader));
            try
            {
                // A member who did not invite him and does not lead cannot withdraw it.
                byte[] inq = ConnectionProtocol.Push("inq", Pb.New().Var(1, party.Id).Var(2, guest).Build());
                using (SessionContext.Push(inside.Session))
                    await PartyHandler.CancelInvitationAsync(inside.Session.Stream!, inq);
                Assert.Empty(await invited.Drain());
                Assert.True(party.Pending.ContainsKey(guest));

                using (SessionContext.Push(lead.Session))
                    await PartyHandler.CancelInvitationAsync(lead.Session.Stream!, inq);

                // ilo { 1 party, 2 who withdrew } to him.
                var ilo = (await invited.Drain()).Single();
                Assert.Equal("ilo", ilo.Op);
                Assert.Equal(Pb.New().Var(1, party.Id).Var(2, leader).Build(), ilo.Payload);

                // ind { 1 party, 2 invitee, 3 who withdrew } to the party, as the client reads it:
                // the other way round it took the leader off the list and named him the invitee (#216).
                byte[] ind = Pb.New().Var(1, party.Id).Var(2, guest).Var(3, leader).Build();
                Assert.Equal(ind, (await inside.Drain()).Single(f => f.Op == "ind").Payload);
                Assert.Equal(ind, (await lead.Drain()).Single(f => f.Op == "ind").Payload);
                Assert.False(party.Pending.ContainsKey(guest));
                Assert.False(Parties.IsInParty(guest));
            }
            finally
            {
                Parties.Dissolve(party);
            }
        }

        [Fact]
        public async Task The_lock_is_answered_with_the_loyalty()
        {
            const long leader = 9_300_000_021;
            await using var lead = await Wire.Open(leader);
            var party = Parties.Create(leader);
            try
            {
                using (SessionContext.Push(lead.Session))
                    await PartyHandler.PledgeLoyaltyAsync(lead.Session.Stream!,
                        ConnectionProtocol.Push("ila", Pb.New().Var(2, party.Id).Var(4, 1).Build()));
                var ilj = (await lead.Drain()).Single();
                Assert.Equal("ilj", ilj.Op);
                Assert.Equal(Pb.New().Var(1, 1).Var(2, party.Id).Build(), ilj.Payload);
                Assert.Contains(leader, party.Loyal);

                using (SessionContext.Push(lead.Session))
                    await PartyHandler.PledgeLoyaltyAsync(lead.Session.Stream!,
                        ConnectionProtocol.Push("ila", Pb.New().Var(2, party.Id).Build()));
                Assert.Equal(Pb.New().Var(2, party.Id).Build(), (await lead.Drain()).Single().Payload);
                Assert.DoesNotContain(leader, party.Loyal);
            }
            finally
            {
                Parties.Dissolve(party);
            }
        }
    }
}

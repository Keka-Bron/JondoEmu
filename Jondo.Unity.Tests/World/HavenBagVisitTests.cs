using System;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Another's haven bag: open to his friends or his guild by the menu's «Compartir» (jau → jaz, as
    /// «hacer accesible merkasako a amigos y gente del gremio» measures it), entered with the jbn
    /// carrying its owner, and the visitor in it with the owner's room and furniture, apart from
    /// every other bag, and without the owner's chest. And no bag at all under level 10.
    /// </summary>
    [Collection("MapManager")]
    public class HavenBagVisitTests : IDisposable
    {
        private const long OwnerAccount = 8_960_000_001, VisitorAccount = 8_960_000_002;
        private readonly long _owner, _visitor;

        public HavenBagVisitTests()
        {
            HavenBagStore.Initialize();
            _owner = DatabaseManager.CreateCharacter(OwnerAccount, 1, "Anfitrion-Saco", 8, 0, 1, new long[] { -1, -1, -1, -1, -1 },
                                                     191105026, 50, 0, 0, Array.Empty<(int, int)>());
            _visitor = DatabaseManager.CreateCharacter(VisitorAccount, 1, "Visita-Saco", 9, 1, 1, new long[] { -1, -1, -1, -1, -1 },
                                                       191105026, 50, 0, 0, Array.Empty<(int, int)>());
        }

        public void Dispose()
        {
            Contacts.Remove(OwnerAccount, VisitorAccount, Contacts.Contact);
            Contacts.Remove(VisitorAccount, OwnerAccount, Contacts.Contact);
            if (_owner > 0) { HavenBagStore.SetShared(_owner, 0); DatabaseManager.DeleteCharacter(_owner, OwnerAccount); }
            if (_visitor > 0) DatabaseManager.DeleteCharacter(_visitor, VisitorAccount);
        }

        private static byte[] Jbn(long owner) => ConnectionProtocol.Push(Op.Jbn, Pb.New().Var(2, owner).Build());

        [Fact]
        public void A_bag_is_open_to_mutual_friends_once_shared_with_them()
        {
            Assert.True(_owner > 0 && _visitor > 0);
            Assert.NotNull(MerkasakoHandler.WhyNotVisit(_visitor, VisitorAccount, _owner));

            Contacts.Add(OwnerAccount, VisitorAccount, Contacts.Contact);
            Contacts.Add(VisitorAccount, OwnerAccount, Contacts.Contact);
            Assert.NotNull(MerkasakoHandler.WhyNotVisit(_visitor, VisitorAccount, _owner));   // not shared yet

            HavenBagStore.SetShared(_owner, HavenBagStore.SharedWithGuild);
            Assert.NotNull(MerkasakoHandler.WhyNotVisit(_visitor, VisitorAccount, _owner));   // the guild's, and none
            HavenBagStore.SetShared(_owner, HavenBagStore.SharedWithFriends | HavenBagStore.SharedWithGuild);
            Assert.Equal(3, HavenBagStore.SharedOf(_owner));
            Assert.Null(MerkasakoHandler.WhyNotVisit(_visitor, VisitorAccount, _owner));

            Contacts.Remove(OwnerAccount, VisitorAccount, Contacts.Contact);
            Assert.NotNull(MerkasakoHandler.WhyNotVisit(_visitor, VisitorAccount, _owner));   // friends no more
        }

        [Fact]
        public async Task The_visitor_is_in_the_owner_s_bag_and_only_its_people_see_him()
        {
            if (!MapManager.WalkableCells.ContainsKey(Jail.MapId)) MapManager.Initialize();
            Merkasako.Initialize();
            long bagMap = Merkasako.MapOfTheme(HavenBagStore.ThemeOfRoom(_owner, HavenBagStore.CurrentRoom(_owner)));
            if (bagMap == 0 || MapManager.GetMapInfo(bagMap) == null) return;   // no client data

            Contacts.Add(OwnerAccount, VisitorAccount, Contacts.Contact);
            Contacts.Add(VisitorAccount, OwnerAccount, Contacts.Contact);
            HavenBagStore.SetShared(_owner, HavenBagStore.SharedWithFriends);

            const long street = 191105026;
            await using var host = await ClientPipe.OpenAsync(OwnerAccount, _owner, street, "Anfitrion-Saco");
            await using var guest = await ClientPipe.OpenAsync(VisitorAccount, _visitor, street, "Visita-Saco");
            host.Session.State.CharacterLevel = 50;
            guest.Session.State.CharacterLevel = 50;

            using (SessionContext.Push(host.Session))
                await MerkasakoHandler.EnterFromOutsideAsync(host.ToClient, ConnectionProtocol.Push(Op.Jbn));
            Assert.Equal(bagMap, host.Session.State.MapId);
            Assert.Equal(0, host.Session.State.HavenBagOwnerId);

            using (SessionContext.Push(guest.Session))
                await MerkasakoHandler.EnterFromOutsideAsync(guest.ToClient, Jbn(_owner));
            Assert.Equal(bagMap, guest.Session.State.MapId);
            Assert.Equal(_owner, guest.Session.State.HavenBagOwnerId);
            Assert.Equal(_owner, MerkasakoHandler.BagOwnerOf(guest.Session.State));

            using (SessionContext.Push(host.Session))
                Assert.Contains(SessionRegistry.OnMap(bagMap), s => s.CharacterId == _visitor);

            // Someone in his own bag of the same theme sees neither of them.
            await using var stranger = await ClientPipe.OpenAsync(8_960_000_003, 8_960_000_103, bagMap, "Otro-Saco");
            using (SessionContext.Push(stranger.Session))
                Assert.DoesNotContain(SessionRegistry.OnMap(bagMap), s => s.CharacterId == _owner || s.CharacterId == _visitor);

            // Not his chest, nor his furniture to place, nor his rooms.
            using (SessionContext.Push(guest.Session))
            {
                await MerkasakoHandler.OpenEditorAsync(guest.ToClient);
                Assert.False(guest.Session.State.IsHavenBagEditing);
            }

            // The button again: out, where he came from, and no longer the owner's guest.
            using (SessionContext.Push(guest.Session))
                await MerkasakoHandler.EnterFromOutsideAsync(guest.ToClient, ConnectionProtocol.Push(Op.Jbn));
            Assert.Equal(street, guest.Session.State.MapId);
            Assert.Equal(0, guest.Session.State.HavenBagOwnerId);
        }

        /// <summary>
        /// «Invitar a mi merkasako» (jbb) → the guest's jbh; his yes (jas) → the host's jat and the guest
        /// in the host's bag, shared or not; «Expulsar» (jay) → the guest out, told 0/627.
        /// </summary>
        [Fact]
        public async Task An_invited_guest_comes_in_and_can_be_put_out()
        {
            if (!MapManager.WalkableCells.ContainsKey(Jail.MapId)) MapManager.Initialize();
            Merkasako.Initialize();
            MerkasakoHandler.ForgetInvitations();
            long bagMap = Merkasako.MapOfTheme(HavenBagStore.ThemeOfRoom(_owner, HavenBagStore.CurrentRoom(_owner)));
            if (bagMap == 0 || MapManager.GetMapInfo(bagMap) == null) return;   // no client data

            const long street = 191105026;
            await using var host = await ClientPipe.OpenAsync(OwnerAccount, _owner, street, "Anfitrion-Saco");
            await using var guest = await ClientPipe.OpenAsync(VisitorAccount, _visitor, street, "Visita-Saco");
            host.Session.State.CharacterLevel = guest.Session.State.CharacterLevel = 50;

            using (SessionContext.Push(host.Session))
                await MerkasakoHandler.InviteAsync(host.ToClient, ConnectionProtocol.Push(Op.Jbb, Pb.New().Var(1, _visitor).Build()));
            var jbh = ProtoMessage.Parse(await guest.Next(Op.Jbh));
            Assert.Equal(_owner, ProtoMessage.Parse(jbh.Fields.Single(f => f.FieldNumber == 1).BytesValue)
                                     .Fields.Single(f => f.FieldNumber == 2).VarIntValue);
            Assert.Equal(MerkasakoHandler.InvitationSeconds, jbh.Fields.Single(f => f.FieldNumber == 2).VarIntValue);

            using (SessionContext.Push(guest.Session))
                await MerkasakoHandler.AnswerInvitationAsync(guest.ToClient, ConnectionProtocol.Push(Op.Jas, Pb.New().Var(2, 1).Build()));
            var jat = ProtoMessage.Parse(await host.Next(Op.Jat));
            Assert.Equal(1, jat.Fields.Single(f => f.FieldNumber == 2).VarIntValue);
            Assert.Equal(bagMap, guest.Session.State.MapId);
            Assert.Equal(_owner, guest.Session.State.HavenBagOwnerId);   // in, though the bag is shared with nobody

            using (SessionContext.Push(host.Session))
                await MerkasakoHandler.KickAsync(host.ToClient, ConnectionProtocol.Push(Op.Jay, Pb.New().Var(2, _visitor).Build()));
            Assert.Equal(street, guest.Session.State.MapId);
            Assert.Equal(0, guest.Session.State.HavenBagOwnerId);
        }

        [Fact]
        public async Task Under_level_ten_there_is_no_bag()
        {
            await using var low = await ClientPipe.OpenAsync(8_960_000_004, 8_960_000_104, 191105026, "Bajo-Saco");
            low.Session.State.CharacterLevel = MerkasakoHandler.MinimumLevel - 1;
            using (SessionContext.Push(low.Session))
                await MerkasakoHandler.EnterFromOutsideAsync(low.ToClient, ConnectionProtocol.Push(Op.Jbn));

            var lqn = ProtoMessage.Parse(await low.Next(Op.Lqn));
            Assert.Equal((1L, 468L), (lqn.Fields.Single(f => f.FieldNumber == 1).VarIntValue,
                                        lqn.Fields.Single(f => f.FieldNumber == 2).VarIntValue));
            Assert.False(Merkasako.IsHavenBag(low.Session.State.MapId));
        }

        /// <summary>The friends' and the guild's lists show the «Visitar» button from the share.</summary>
        [Fact]
        public void The_guild_list_carries_the_bag_open_to_the_guild()
        {
            var member = new GuildStore.Member { CharacterId = _owner, Rank = 1 };
            Jondo.Unity.Protocol.Game.GuildMember Entry()
            {
                var row = Assert.Single(GuildRosterMessages.Live.Snapshot(new[] { member }).Members);
                var basic = Assert.IsType<Jondo.Unity.Protocol.Game.CharacterComplementCaseCharacterBasicInformation>(row.ComplementCase).Value;
                return Assert.IsType<Jondo.Unity.Protocol.Game.CharacterBasicInformationComplementCaseSocialMember>(basic.ComplementCase).Value.GuildMember!;
            }

            HavenBagStore.SetShared(_owner, HavenBagStore.SharedWithFriends);
            Assert.False(Entry().HavenBagShared);
            HavenBagStore.SetShared(_owner, HavenBagStore.SharedWithGuild);
            Assert.True(Entry().HavenBagShared);
        }
    }
}

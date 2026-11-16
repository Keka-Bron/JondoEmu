using System.Linq;
using System.Threading.Tasks;
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
    /// A dream in a group: the owner's «Invitar al grupo» (iwt) brings each party member the
    /// invitation (iyd { f1 his name, f2 -300 }, «recibir invitacion a sueños»), the yes (ixk) takes
    /// him into the owner's room with the owner's state, only the owner starts the fights, and the
    /// owner leaving takes his guests out.
    /// </summary>
    [Collection("MapManager")]
    public class GroupDreamTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task An_invited_member_follows_the_owner_and_exits_only_after_the_owners_typed_mark_clear(bool fail)
        {
            const long owner = 900_000_000_011, guest = 900_000_000_012, street = 191105026;
            if (!MapManager.WalkableCells.ContainsKey(Jail.MapId)) MapManager.Initialize();
            Interactives.Initialize();
            Dreams.OlvidarTodo();
            DreamHandler.ForgetGroups();
            var dream = Dreams.Crear(owner, "Dueno", 200, 2, street, 300);
            Dreams.Enter(dream, 0, out _);
            var room = dream.SalaActual;
            Assert.NotNull(room);
            Assert.NotEqual(0, room.MapaDeLaSala);
            Assert.NotNull(MapManager.GetMapInfo(room.MapaDeLaSala));

            var party = Parties.Create(owner);
            Parties.Invite(party, guest, owner);
            Parties.Accept(party, guest);
            var codec = new MarkCodec(fail);
            await using var host = await ClientPipe.OpenAsync(990_000_301, owner, room.MapaDeLaSala, "Dueno", codec);
            await using var visitor = await ClientPipe.OpenAsync(990_000_302, guest, street, "Invitado");
            try
            {
                using (SessionContext.Push(host.Session)) await DreamHandler.InviteGroupAsync(host.ToClient);
                var iyd = ProtoMessage.Parse(await visitor.Next(Op.Iyd));
                Assert.Equal("Dueno", System.Text.Encoding.UTF8.GetString(iyd.Fields.Single(f => f.FieldNumber == 1).BytesValue));
                Assert.Equal(-300, (int)iyd.Fields.Single(f => f.FieldNumber == 2).VarIntValue);

                using (SessionContext.Push(visitor.Session))
                    await DreamHandler.AnswerInvitationAsync(visitor.ToClient, ConnectionProtocol.Push(Op.Ixk, Pb.New().Var(1, 1).Build()));
                Assert.Equal(owner, DreamHandler.HostOf(guest));
                Assert.Equal(Op.Izg, (await visitor.NextAny()).Opcode);
                Assert.Equal(room.MapaDeLaSala, visitor.Session.State.MapId);

                using (SessionContext.Push(visitor.Session))
                    Assert.True(await DreamHandler.GuestMayNotFightAsync(visitor.ToClient));
                using (SessionContext.Push(host.Session))
                    Assert.False(await DreamHandler.GuestMayNotFightAsync(host.ToClient));

                using (SessionContext.Push(visitor.Session))
                using (await host.Session.EnterTurnAsync())
                {
                    if (fail) Assert.Same(codec.Failure, await Assert.ThrowsAsync<System.IO.IOException>(() =>
                        DreamHandler.LeaveAsync(host.Session, ConnectionProtocol.Push(Op.Iyx))));
                    else await DreamHandler.LeaveAsync(host.Session, ConnectionProtocol.Push(Op.Iyx));
                    Assert.Same(visitor.Session, SessionContext.Actual);
                    Assert.Equal(0, host.Session.UnoCadaVez.CurrentCount);
                }
                var clear = Assert.Single(codec.Frames, frame => frame.Content is NpcsMapQuestStatusUpdateEvent);
                Assert.Empty(Assert.IsType<NpcsMapQuestStatusUpdateEvent>(clear.Content).MapInformation);
                Assert.Null(clear.Uid);
                // Skip earlier teleport/presence frames, retaining the clear → marks → acknowledgment boundary.
                for (int i = 0; i < 64; i++)
                {
                    byte[] packet = await Read(host);
                    if (packet[0] != 0xC7 && ConnectionProtocol.ReadOpcode(packet) == Op.Ixg) break;
                    Assert.True(i < 63, "Dream-state clear did not arrive.");
                }
                if (!fail)
                {
                    Assert.Equal(MarkCodec.Token(codec.Frames.IndexOf(clear) + 1), await Read(host));
                    await host.Next(Op.Iyb);
                }
                Assert.False(host.FromServer.DataAvailable);
                Assert.Equal(fail, DreamHandler.IsGuest(guest));
                Assert.Equal(fail ? room.MapaDeLaSala : street, visitor.Session.State.MapId);
                Assert.Equal(0, codec.Decodes);
            }
            finally
            {
                Parties.Dissolve(party);
                Dreams.OlvidarTodo();
                DreamHandler.ForgetGroups();
            }
        }

        [Fact]
        public async Task A_no_leaves_the_member_where_he_is()
        {
            const long owner = 900_000_000_021, guest = 900_000_000_022, street = 191105026;
            Interactives.Initialize();
            Dreams.OlvidarTodo();
            DreamHandler.ForgetGroups();
            var dream = Dreams.Crear(owner, "Dueno", 200, 2, street, 300);
            Dreams.Enter(dream, 0, out _);
            var room = dream.SalaActual;
            if (room == null || room.MapaDeLaSala == 0) return;

            var party = Parties.Create(owner);
            Parties.Invite(party, guest, owner);
            Parties.Accept(party, guest);
            await using var host = await ClientPipe.OpenAsync(990_000_311, owner, room.MapaDeLaSala, "Dueno");
            await using var visitor = await ClientPipe.OpenAsync(990_000_312, guest, street, "Invitado");
            try
            {
                using (SessionContext.Push(host.Session)) await DreamHandler.InviteGroupAsync(host.ToClient);
                await visitor.Next(Op.Iyd);
                using (SessionContext.Push(visitor.Session))
                    await DreamHandler.AnswerInvitationAsync(visitor.ToClient, ConnectionProtocol.Push(Op.Ixk));
                Assert.False(DreamHandler.IsGuest(guest));
                Assert.Equal(street, visitor.Session.State.MapId);
            }
            finally
            {
                Parties.Dissolve(party);
                Dreams.OlvidarTodo();
                DreamHandler.ForgetGroups();
            }
        }

        private static Task<byte[]> Read(ClientPipe pipe)
            => Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(System.TimeSpan.FromSeconds(5));

        private sealed class MarkCodec(bool fail) : IGameCodec
        {
            internal readonly System.Collections.Generic.List<GameFrame> Frames = [];
            internal readonly System.IO.IOException Failure = new("dream mark encoding failed");
            internal int Decodes;
            internal static byte[] Token(int index) => [0xC7, 0x67, checked((byte)index)];
            public byte[] Encode(GameFrame frame)
            {
                Frames.Add(frame);
                if (fail && frame.Content is NpcsMapQuestStatusUpdateEvent) throw Failure;
                return Token(Frames.Count);
            }
            public GameFrame Decode(System.ReadOnlySpan<byte> packet)
            {
                Decodes++;
                throw new System.InvalidOperationException("Dream mark clearing must not decode a packet.");
            }
        }
    }
}

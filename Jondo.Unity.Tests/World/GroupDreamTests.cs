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
        public enum Exit { Owner, OwnerSendFails, GuestMapChange, GuestRequest }

        [Theory]
        [InlineData(Exit.Owner)]
        [InlineData(Exit.OwnerSendFails)]
        [InlineData(Exit.GuestMapChange)]
        [InlineData(Exit.GuestRequest)]
        public async Task A_dream_exit_clears_the_interface_in_order_without_replaying_it_on_later_loads(Exit exit)
        {
            // A street of its own: 191105026 is where most tests stand, and the arrivals of those
            // running alongside reached the owner once he was back on it. [5,-19] would not do
            // either: the readable tests open a board there.
            const long owner = 900_000_000_011, guest = 900_000_000_012, street = 191106050;
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
            bool fail = exit == Exit.OwnerSendFails;
            var codec = new MarkCodec(fail);
            await using var host = await ClientPipe.OpenAsync(990_000_301, owner, room.MapaDeLaSala, "Dueno", codec);
            await using var visitor = await ClientPipe.OpenAsync(990_000_302, guest, street, "Invitado");
            try
            {
                await DreamHandler.InviteGroupAsync(host.Session);
                var iyd = ProtoMessage.Parse(await visitor.Next(Op.Iyd));
                Assert.Equal("Dueno", System.Text.Encoding.UTF8.GetString(iyd.Fields.Single(f => f.FieldNumber == 1).BytesValue));
                Assert.Equal(-300, (int)iyd.Fields.Single(f => f.FieldNumber == 2).VarIntValue);

                int hostCell = host.Session.State.CellId;
                using (SessionContext.Push(host.Session))
                    await DreamHandler.AnswerInvitationAsync(visitor.Session, ConnectionProtocol.Push(Op.Ixk, Pb.New().Var(1, 1).Build()));
                Assert.Equal(owner, DreamHandler.HostOf(guest));
                var received = new Jondo.Unity.Protocol.Game.Wire.GameCodec().Decode(await Read(visitor));
                var snapshot = Assert.IsType<InfiniteDreamStateEvent>(received.Content);
                Assert.Null(received.Uid);
                Assert.Equal(owner, snapshot.Dreamer!.CharacterId);
                Assert.Equal(dream.Arena, snapshot.Life);
                Assert.Equal(room.MapaDeLaSala, visitor.Session.State.MapId);
                Assert.Equal(hostCell, host.Session.State.CellId);

                // A guest-triggered refresh is encoded by the owner's session.
                using (SessionContext.Push(visitor.Session)) await DreamHandler.SendStateToOwnerAsync(dream);
                var refresh = Assert.Single(codec.Frames, f => f.Content is InfiniteDreamStateEvent);
                Assert.Null(refresh.Uid);
                // The guest's arrival in the room reaches the owner too, encoded on its own; it may
                // land before the refresh.
                await ReadToken(host, MarkCodec.Token(codec.Frames.IndexOf(refresh) + 1), "Dream-state refresh did not arrive.");

                Assert.True(await DreamHandler.GuestMayNotFightAsync(visitor.Session));
                Assert.False(await DreamHandler.GuestMayNotFightAsync(host.Session));

                if (exit is Exit.GuestMapChange or Exit.GuestRequest)
                {
                    // The earlier GuestMayNotFight warning is unrelated to the map transition.
                    Assert.NotNull(await RecyclingTests.Until(visitor, Op.Lqn));
                    if (exit == Exit.GuestRequest)
                    {
                        // Restore the guest's view of the host's dream without binding either session.
                        Assert.Null(SessionContext.Actual);
                        await DreamHandler.OnWorldEntryAsync(visitor.Session);
                        var restored = new Jondo.Unity.Protocol.Game.Wire.GameCodec().Decode(await Read(visitor));
                        Assert.Equal(owner, Assert.IsType<InfiniteDreamStateEvent>(restored.Content).Dreamer!.CharacterId);
                        using (SessionContext.Push(host.Session))
                            await DreamHandler.LeaveAsync(visitor.Session, ConnectionProtocol.Push(Op.Iyx));
                        Assert.Empty(await RecyclingTests.Until(visitor, Op.Ixg));
                        await visitor.Next(Op.Iyb);
                    }
                    else visitor.Session.State.MapId = street;
                    using (SessionContext.Push(host.Session))
                    {
                        await DreamHandler.OnMapLoadedAsync(visitor.Session);
                        await DreamHandler.OnMapLoadedAsync(visitor.Session);
                    }
                    if (exit == Exit.GuestMapChange) Assert.Empty(await visitor.Next(Op.Ixg));
                    Assert.False(DreamHandler.IsGuest(guest));
                    Assert.Equal(street, visitor.Session.MapId);
                    Assert.Equal(room.MapaDeLaSala, host.Session.MapId);
                    Assert.Equal(hostCell, host.Session.State.CellId);
                    Assert.DoesNotContain(codec.Frames, frame => frame.Content is InfiniteDreamClearEvent);
                    await visitor.Quiet();
                    return;
                }

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
                // The opaque codec emits a token for the typed clear, before the marks and answer.
                var dreamClear = Assert.Single(codec.Frames, frame => frame.Content is InfiniteDreamClearEvent);
                Assert.Null(dreamClear.Uid);
                int clearIndex = codec.Frames.IndexOf(dreamClear);
                Assert.Equal(clearIndex + 1, codec.Frames.IndexOf(clear));
                await ReadToken(host, MarkCodec.Token(clearIndex + 1), "Dream-state clear did not arrive.");
                if (!fail)
                {
                    Assert.Equal(MarkCodec.Token(codec.Frames.IndexOf(clear) + 1), await Read(host));
                    await host.Next(Op.Iyb);
                }
                if (host.FromServer.DataAvailable)
                    Assert.Fail($"the owner heard {(await host.NextAny()).Opcode} after his dream exit");
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
            const long owner = 900_000_000_021, guest = 900_000_000_022, street = 191106050;
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
                await DreamHandler.InviteGroupAsync(host.Session);
                await visitor.Next(Op.Iyd);
                await DreamHandler.AnswerInvitationAsync(visitor.Session, ConnectionProtocol.Push(Op.Ixk));
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

        /// <summary>Reads the owner's packets until the codec's token, past what reached him encoded elsewhere.</summary>
        private static async Task ReadToken(ClientPipe pipe, byte[] token, string missing)
        {
            for (int i = 0; i < 64; i++)
            {
                if ((await Read(pipe)).SequenceEqual(token)) return;
            }
            Assert.Fail(missing);
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

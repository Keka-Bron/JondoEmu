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
    [Collection("MapManager")]
    public sealed class IncarnamPortalTests
    {
        private const long Incarnam = 153880835, Astrub = 192416776;
        private const int Portal = 4398, Passage = 806080;

        private static void Load()
        {
            if (MapManager.GetMapInfo(Incarnam) == null) MapManager.Initialize();
            Interactives.Initialize();
            TeleportManager.Initialize();
            Npcs.Initialize();
        }

        [Fact]
        public void The_startup_registry_guard_accepts_the_actor_owned_portal_route()
        {
            Load();
            InteractiveRegistry.Initialize();
            RegressionGuardTests.AssertInteractiveRegistry();
        }

        [Fact]
        public void The_portal_is_an_actor_and_not_a_graphical_interactive_or_floor_trigger()
        {
            Load();
            var portal = Assert.Single(Npcs.OnMap(Incarnam), n => n.NpcId == Portal);
            Assert.Equal((430, 3, 4892L), (portal.Cell, portal.Orientation, portal.Bones));
            Assert.Contains(Npcs.Talk, Npcs.TemplateOf(Portal)!.Actions);

            Assert.True(TeleportManager.TryGetNpcPassage(Incarnam, Passage, Portal, out var route));
            Assert.Equal((Astrub, 483), (route.DestinationMapId, route.DestinationCellId));
            Assert.False(TeleportManager.TryGetNpcPassage(Incarnam, Passage, 4360, out _));
            Assert.False(TeleportManager.TryGetNpcPassage(Astrub, Passage, Portal, out _));
            Assert.False(TeleportManager.TryGetNpcPassage(Astrub, 515866, Portal, out _));
            Assert.False(TeleportManager.TryGetCellTrigger(Incarnam, 430, out _));
            InteractiveRegistry.Initialize();
            Assert.DoesNotContain(InteractiveRegistry.OnMap(Incarnam), i => i.Element.Id == Passage);
        }

        [Theory]
        [InlineData(36977, true)]
        [InlineData(36978, false)]
        public async Task The_captured_dialogue_crosses_only_when_the_player_confirms(long reply, bool crosses)
        {
            Load();
            var portal = Assert.Single(Npcs.OnMap(Incarnam), n => n.NpcId == Portal);
            long who = 8_923_100_100 + reply;
            await using var pipe = await ClientPipe.OpenAsync(who, who, Incarnam, "Portail-Test");
            using (SessionContext.Push(pipe.Session))
            {
                await NpcHandler.InteractAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Iov,
                    Pb.New().Var(1, Npcs.Talk).Var(2, Incarnam).Var(3, portal.ContextualId).Build()));
                await pipe.Next(Op.Ioc);
                AssertQuestion(await pipe.Next(Op.Ios), 30637, 36979);

                await NpcHandler.ReplyAsync(pipe.ToClient,
                    ConnectionProtocol.Push(Op.Ioy, Pb.New().Var(1, 36979).Build()));
                AssertQuestion(await pipe.Next(Op.Ios), 30638, 36977, 36978);
                Assert.Equal(Incarnam, pipe.Session.State.MapId);

                await NpcHandler.ReplyAsync(pipe.ToClient,
                    ConnectionProtocol.Push(Op.Ioy, Pb.New().Var(1, reply).Build()));
                Assert.Equal(1, Number(await pipe.Next(Op.Kld), 1));
                Assert.False(NpcHandler.IsDialogueOpen);
                if (crosses)
                {
                    await pipe.Next(Op.Jsd);
                    Assert.Equal(Astrub, Number(await pipe.Next(Op.Jru), 2));
                    Assert.Equal(Astrub, pipe.Session.State.MapId);
                    Assert.Equal(483, pipe.Session.State.CellId);
                }
                else
                {
                    Assert.Equal(Incarnam, pipe.Session.State.MapId);
                    Assert.False(pipe.FromServer.DataAvailable);
                }
            }
        }

        private static long Number(byte[] body, int field)
            => ProtoMessage.Parse(body).Fields.FirstOrDefault(f => f.FieldNumber == field && f.WireType == 0)?.VarIntValue ?? 0;

        private static void AssertQuestion(byte[] body, long message, params long[] replies)
        {
            Assert.Equal(message, Number(body, 1));
            Assert.Equal(replies, ProtoMessage.Parse(body).Fields.Where(f => f.FieldNumber == 2)
                .Select(f => Number(f.BytesValue, 1)).ToArray());
        }
    }
}

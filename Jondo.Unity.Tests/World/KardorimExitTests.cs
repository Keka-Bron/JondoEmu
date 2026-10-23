using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    [Collection("MapManager")]
    public sealed class KardorimExitTests
    {
        private const long BossRoom = 152834048, ExitRoom = 152835072, Outside = 153881600;

        private static void Load()
        {
            if (MapManager.GetMapInfo(ExitRoom) == null) MapManager.Initialize();
            DungeonManager.Initialize();
        }

        [Fact]
        public async Task Winning_the_boss_loads_the_exit_room_on_cell_382()
        {
            Load();
            const long account = 8_923_000_031;
            long character = DatabaseManager.CreateCharacter(account, 1, "Kardorim-Salida", 5, 0, 1,
                new long[] { -1, -1, -1, -1, -1 }, BossRoom, 300, 0, 0, Array.Empty<(int, int)>());
            Assert.True(character > 0);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(account, character, BossRoom);
                var fight = new FightInstance(character, BossRoom);
                fight.AddPlayer(new Fighter { Id = character, TeamId = 0, CellId = 300,
                    Level = 1, MaxHP = 100, CurrentHP = 100 });
                fight.AddMonster(new Fighter { Id = -1, TeamId = 1, CellId = 314,
                    MonsterId = 4051, IsMonster = true, Level = 1, MaxHP = 1, CurrentHP = 0 });
                pipe.Session.State.IsInFight = true;
                pipe.Session.State.RoleplayMapId = BossRoom;
                pipe.Session.State.RoleplayCellId = 300;

                using (SessionContext.Push(pipe.Session))
                {
                    var finish = typeof(FightHandler).GetMethod("TerminarParaUnoAsync",
                        BindingFlags.Static | BindingFlags.NonPublic)!;
                    await (Task)finish.Invoke(null, new object[] { pipe.ToClient, fight, true, true })!;
                }

                Assert.False(pipe.Session.State.IsInFight);
                Assert.Equal(ExitRoom, pipe.Session.State.MapId);
                Assert.Equal(382, pipe.Session.State.CellId);
                while (true)
                {
                    var (op, body) = await pipe.NextAny();
                    if (op != Op.Jru) continue;
                    Assert.Equal(ExitRoom, Number(body, 2));
                    break;
                }
                Assert.False(DungeonManager.IsRoom(ExitRoom));
                Assert.False(DungeonHandler.IsBossRoom(ExitRoom));
                Assert.Equal(Outside, DungeonManager.WayOut(DungeonManager.Get(90)!));
            }
            finally
            {
                DatabaseManager.DeleteCharacter(character, account);
            }
        }

        [Fact]
        public async Task The_npcs_explicit_exit_reply_closes_the_dialogue_then_loads_the_outside()
        {
            const long reply = 24997; // The farewell currently offered without a written tree.
            Load();
            Npcs.Initialize();
            var npc = Assert.Single(Npcs.OnMap(ExitRoom), n => n.NpcId == 2936);
            Assert.Contains(reply, Npcs.TemplateOf(npc.NpcId)!.Replies);
            await using var pipe = await ClientPipe.OpenAsync(8_923_000_032, 8_923_100_032, ExitRoom);
            using (SessionContext.Push(pipe.Session))
            {
                await NpcHandler.InteractAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Iov,
                    Pb.New().Var(1, Npcs.Talk).Var(2, ExitRoom).Var(3, npc.ContextualId).Build()));
                await pipe.Next(Op.Ioc);
                var question = await pipe.Next(Op.Ios);
                Assert.Contains(ProtoMessage.Parse(question).Fields, f => f.FieldNumber == 2
                    && Number(f.BytesValue, 1) == reply);
                Assert.Equal(ExitRoom, pipe.Session.State.MapId);

                await NpcHandler.ReplyAsync(pipe.ToClient,
                    ConnectionProtocol.Push(Op.Ioy, Pb.New().Var(1, reply).Build()));
                Assert.Equal(1, Number(await pipe.Next(Op.Kld), 1));
                await pipe.Next(Op.Jsd);
                Assert.Equal(Outside, Number(await pipe.Next(Op.Jru), 2));
                Assert.Equal(Outside, pipe.Session.State.MapId);
                Assert.False(NpcHandler.IsDialogueOpen);
            }
        }

        [Theory]
        [InlineData(24994)]
        [InlineData(24997)]
        [InlineData(25000)]
        [InlineData(25001)]
        public void All_four_template_exit_replies_lead_outside(long reply)
        {
            Load();
            Npcs.Initialize();
            Assert.Contains(reply, Npcs.TemplateOf(2936)!.Replies);
            Assert.Equal(Outside, DungeonHandler.ExitForReply(2936, ExitRoom, reply));
        }

        [Theory]
        [InlineData(2936, ExitRoom, 24993)] // Learn about him.
        [InlineData(2936, ExitRoom, 25015)] // Milimilou's crypt.
        [InlineData(2907, ExitRoom, 25001)] // Another NPC.
        [InlineData(2936, BossRoom, 25001)]
        [InlineData(2936, Outside, 25001)]
        public void Only_Kardorims_exit_replies_in_his_exit_room_choose_the_outside(int npc, long map, long reply)
        {
            Load();
            Assert.Equal(0, DungeonHandler.ExitForReply(npc, map, reply));
        }

        [Fact]
        public async Task The_map_panels_exit_also_leaves_the_post_boss_room()
        {
            Load();
            await using var pipe = await ClientPipe.OpenAsync(8_923_000_033, 8_923_100_033, ExitRoom);
            using (SessionContext.Push(pipe.Session))
            {
                pipe.Session.State.IsInFight = true;
                Assert.False(await DungeonHandler.LeaveAsync(pipe.ToClient));
                Assert.Equal(ExitRoom, pipe.Session.State.MapId);
                pipe.Session.State.IsInFight = false;
                Assert.True(await DungeonHandler.LeaveAsync(pipe.ToClient));
                Assert.Equal(Outside, pipe.Session.State.MapId);
            }
        }

        [Theory]
        [InlineData(152833024, BossRoom)] // Kardorim's fourth room still leads to the boss.
        [InlineData(121374211, 120063489)] // Royal Gobball still leads outside after its boss.
        [InlineData(Outside, 0)]
        public void Other_victory_routes_keep_their_destination_and_default_cell(long room, long expected)
        {
            Load();
            Assert.Equal((expected, TeleportHandler.MapCentre), DungeonHandler.AfterAWinIn(room));
        }

        private static long Number(byte[] body, int field)
            => ProtoMessage.Parse(body).Fields.FirstOrDefault(f => f.FieldNumber == field && f.WireType == 0)?.VarIntValue ?? 0;
    }
}

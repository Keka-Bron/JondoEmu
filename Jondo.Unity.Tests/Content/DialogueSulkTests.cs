using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// A reply the NPC sulks at: a wrong step at Gorco's cauldron in «A la leche caliente, batir de
    /// repente» and he will not talk for a while (Dofus pour les noobs: «Si vous vous trompez dans
    /// les choix de réponse, il faudra attendre 10 minutes environ pour lui reparler»), his «¡Yo no
    /// hablar a ti!» meanwhile.
    /// </summary>
    [Collection("NpcSulks")]
    public class DialogueSulkTests
    {
        private const string Tree = @"{ ""dialogues"": [ {
            ""npc"": 2239, ""map"": 122421248, ""opening"": 18350, ""sulkLine"": 18345,
            ""lines"": [ { ""message"": 18350, ""choices"": [
                { ""reply"": 21976, ""next"": 18351, ""sulks"": 10 },
                { ""reply"": 21979, ""next"": 18352 }
            ] } ] } ] }";

        private static NpcDialogue Read(string json)
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, json);
                return NpcDialogueContent.Load(path).Rows.Single().Value.Value;
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void The_sulk_is_read_and_written_back()
        {
            var tree = Read(Tree);
            Assert.Equal(18345, tree.SulkLine);
            Assert.Equal(10, tree.Lines.Single().Choices[0].Sulks);
            Assert.Equal(0, tree.Lines.Single().Choices[1].Sulks);

            string path = Path.GetTempFileName();
            try
            {
                NpcDialogueContent.Save(path, new[] { tree });
                var again = NpcDialogueContent.Load(path).Rows.Single().Value.Value;
                Assert.Equal(18345, again.SulkLine);
                Assert.Equal(10, again.Lines.Single().Choices[0].Sulks);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void The_npc_sulks_for_the_minutes_and_only_at_that_character()
        {
            var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
            var clock = NpcSulks.Clock;
            NpcSulks.Clock = () => now;
            try
            {
                NpcSulks.Forget();
                Assert.False(NpcSulks.Sulking(7, 2239));

                NpcSulks.Start(7, 2239, 10);
                Assert.True(NpcSulks.Sulking(7, 2239));
                Assert.False(NpcSulks.Sulking(8, 2239));
                Assert.False(NpcSulks.Sulking(7, 2240));

                now = now.AddMinutes(9);
                Assert.True(NpcSulks.Sulking(7, 2239));
                now = now.AddMinutes(1);
                Assert.False(NpcSulks.Sulking(7, 2239));
            }
            finally
            {
                NpcSulks.Clock = clock;
                NpcSulks.Forget();
            }
        }

        [Fact]
        public void Gorcos_cauldron_sulks_at_every_wrong_step_and_gives_the_pearl_at_the_end()
        {
            var gorco = NpcDialogueContent.Load(Paths.ContentFile(NpcDialogueContent.AuthoredFile)).Rows
                .Select(r => r.Value.Value).Single(d => d.NpcId == 2239 && d.MapId == 122421248);
            Assert.Equal(18345, gorco.SulkLine);

            var sulking = gorco.Lines.SelectMany(l => l.Choices).Where(c => c.Sulks > 0).Select(c => c.Reply).OrderBy(r => r);
            Assert.Equal(new long[] { 21976, 21978, 21984, 21990 }, sulking);

            var lid = gorco.Lines.Single(l => l.Message == 18359).Choices.Single();
            Assert.Equal(8678, lid.Completes);
            Assert.Equal(15418, lid.Gives);
            Assert.Equal(new[] { 15593, 15421, 15599 }, lid.TakesAll);
        }

        /// <summary>
        /// Sulking, he says his line and offers nothing of his: the client's own «Marcharte.» closes
        /// it. His template's last reply went with it, «Tomar el pedazo de papel y darle las gracias
        /// al goblin».
        /// </summary>
        [Fact]
        public async Task A_sulking_npc_offers_no_reply_of_his_template()
        {
            const long Cabin = 122421248, Character = 8_923_100_071;
            if (MapManager.GetMapInfo(Cabin) == null) MapManager.Initialize();
            Npcs.Initialize();
            var gorco = Npcs.OnMap(Cabin).FirstOrDefault(n => n.NpcId == 2239);
            if (gorco == null) return;                       // no map data
            NpcSulks.Start(Character, 2239, 10);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(8_923_000_071, Character, Cabin);
                using (SessionContext.Push(pipe.Session))
                {
                    await NpcHandler.InteractAsync(pipe.Session, ConnectionProtocol.Push(Op.Iov,
                        Pb.New().Var(1, Npcs.Talk).Var(2, Cabin).Var(3, gorco.ContextualId).Build()));
                    await pipe.Next(Op.Ioc);
                    var question = ProtoMessage.Parse(await pipe.Next(Op.Ios)).Fields;
                    Assert.Contains(question, f => f.FieldNumber == 1 && f.WireType == 0 && f.VarIntValue == 18345);
                    Assert.DoesNotContain(question, f => f.FieldNumber == 2);
                }
            }
            finally
            {
                NpcSulks.Forget();
            }
        }
    }
}

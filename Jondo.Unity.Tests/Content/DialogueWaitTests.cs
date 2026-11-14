using System;
using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// A reply that waits some minutes after an objective: Trimpize forges the pendulum of «Un
    /// péndulo para guiar sus pasos» in three minutes once he has the materials, and asked too early
    /// he says «Dame unos minutos» (Dofus pour les noobs: «Vous devez donc attendre au minimum 3
    /// minutes avant de pouvoir reparler à Lanseuft de Troille»).
    /// </summary>
    public class DialogueWaitTests
    {
        private const string Tree = @"{ ""dialogues"": [ {
            ""npc"": 6709, ""map"": 0, ""opening"": 50473,
            ""lines"": [ { ""message"": 50473, ""choices"": [
                { ""reply"": 67671, ""next"": 50483, ""quest"": 2450, ""waited"": [18545, 3], ""completesAll"": [18546, 18547] },
                { ""reply"": 67671, ""next"": 50486, ""quest"": 2450, ""stillWaiting"": [18545, 3], ""beforeObjective"": 18546 }
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
        public void The_wait_is_read_and_written_back()
        {
            var choices = Read(Tree).Lines.Single().Choices;
            Assert.Equal(new[] { 18545, 3 }, choices[0].Waited);
            Assert.Empty(choices[0].StillWaiting);
            Assert.Equal(new[] { 18545, 3 }, choices[1].StillWaiting);
            Assert.Empty(choices[1].Waited);

            string path = Path.GetTempFileName();
            try
            {
                NpcDialogueContent.Save(path, new[] { Read(Tree) });
                var again = NpcDialogueContent.Load(path).Rows.Single().Value.Value.Lines.Single().Choices;
                Assert.Equal(new[] { 18545, 3 }, again[0].Waited);
                Assert.Equal(new[] { 18545, 3 }, again[1].StillWaiting);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// The minutes count from the moment the objective was ticked off. One done with no time
        /// against it -- the character logged out in between -- has waited long enough.
        /// </summary>
        [Fact]
        public void The_minutes_count_from_when_the_objective_was_done()
        {
            if (!File.Exists(Paths.QuestsJson)) return;

            var log = new QuestLog(new QuestCatalogue(), () => 50, () => 221511680);
            Assert.NotNull(log.Start(2450));
            Assert.False(log.WaitedSince(18545, 3));   // not even done

            log.Tick(2450, 18543);
            log.Tick(2450, 18544);
            log.Tick(2450, 18545);
            Assert.False(log.WaitedSince(18545, 3));   // just handed over

            var run = log.Run(2450)!;
            run.DoneAt[18545] = DateTime.Now.AddMinutes(-2);
            Assert.False(log.WaitedSince(18545, 3));
            run.DoneAt[18545] = DateTime.Now.AddMinutes(-3).AddSeconds(-1);
            Assert.True(log.WaitedSince(18545, 3));

            run.DoneAt.Remove(18545);
            Assert.True(log.WaitedSince(18545, 3));
        }

        /// <summary>
        /// Trimpize's tree: the pendulum is asked for once with each side of the wait, and the five
        /// ways into the maze stay shut until the quest is done.
        /// </summary>
        [Fact]
        public void Trimpize_hands_the_pendulum_over_after_three_minutes_and_the_maze_waits_for_it()
        {
            var store = NpcDialogueContent.Load(Paths.ContentFile(NpcDialogueContent.AuthoredFile), _ => { });
            var trimpize = NpcDialogueContent.For(store, 6709, 221511680);
            Assert.NotNull(trimpize);

            var asks = trimpize!.Lines.SelectMany(l => l.Choices).Where(c => c.Reply == 67671).ToList();
            Assert.Contains(asks, c => c.Waited.SequenceEqual(new[] { 18545, 3 }) && c.Gives == 26670);
            Assert.Contains(asks, c => c.StillWaiting.SequenceEqual(new[] { 18545, 3 }) && c.Gives == 0);
        }
    }
}

using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Minilubo's lair (152836096): Kardorim's replies and the graves' grate let anybody in,
    /// and its only way out is the reply of whoever stands on 344 -- the Minilubo (2911) while «Dans
    /// la gueule du Milimilou» (1635) has him to beat (9785), Likantrabioso (2898) otherwise. Someone
    /// without the quest found neither, and could not leave.
    /// </summary>
    public class MilimilouLairTests
    {
        private const long Lair = 152836096;
        private const int Quest = 1635, BeatHim = 9785, Minilubo = 2911, Likantrabioso = 2898;

        private sealed class Facts : IQuestFacts
        {
            public bool Has, Done, Beaten;
            public int Level => 10;
            public long MapId => Lair;
            public bool Finished(int questId) => questId == Quest && Done;
            public bool Active(int questId) => questId == Quest && Has && !Done;
            public bool ObjectiveDone(int objectiveId) => objectiveId == BeatHim && (Beaten || Done);
            public bool ObjectivePending(int objectiveId) => objectiveId == BeatHim && Has && !Done && !Beaten;
        }

        [Theory]
        [InlineData(false, false, false, Likantrabioso)]   // never had the quest: came by Kardorim or the grate
        [InlineData(true, false, false, Minilubo)]         // under way, the Minilubo to beat
        [InlineData(true, false, true, Likantrabioso)]     // under way, the Minilubo beaten
        [InlineData(true, true, true, Likantrabioso)]      // finished
        public void Somebody_always_stands_there_to_let_one_out(bool has, bool done, bool beaten, int expected)
        {
            Npcs.Initialize();
            var spawns = Npcs.Of(Lair).Where(s => s.NpcId is Minilubo or Likantrabioso).ToList();
            if (spawns.Count == 0) return;   // no world data

            var facts = new Facts { Has = has, Done = done, Beaten = beaten };
            var there = spawns.Where(s =>
            {
                var verdict = QuestCriterion.Judge(s.Criterion, facts);
                return !verdict.Broke && verdict.Met;
            }).Select(s => s.NpcId).ToList();

            Assert.Equal(expected, Assert.Single(there));
        }

        /// <summary>
        /// «Demander où est passé le Milimilou» does objective 9786 and carries its «?» only for the
        /// quest under way; to whoever came without it, it is only a question.
        /// </summary>
        [Fact]
        public void Asking_Likantrabioso_does_the_objective_only_for_the_quest()
        {
            const long ask = 25013;
            var store = NpcDialogueContent.Load(Paths.ContentFile(NpcDialogueContent.AuthoredFile), _ => { });
            var tree = store.Rows.Where(r => r.Key.NpcId == Likantrabioso).Select(r => r.Value.Value).FirstOrDefault();
            if (tree == null) return;   // no content
            var opening = tree.Lines[0];

            DialogueChoice Shown(bool has) => opening.Choice(ask, c => DialogueLine.Shows(c,
                q => q == Quest && has, _ => false, (_, _) => true, (_, o) => false))!;

            var stranger = Shown(false);
            Assert.Equal(0, stranger.Completes);
            Assert.Empty(stranger.Parameters);

            var onTheQuest = Shown(true);
            Assert.Equal(9786, onTheQuest.Completes);
            Assert.NotEmpty(onTheQuest.Parameters);
        }
    }
}

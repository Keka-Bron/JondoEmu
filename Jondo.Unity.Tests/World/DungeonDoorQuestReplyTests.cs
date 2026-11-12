using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// A guardian whose written tree hands its quest over on the door's own replies: Klasmor's
    /// «Criptología» goes with the keyring (24973) and the key (24970). A reply that starts a quest
    /// is offered only while it can be started, so once the quest was under way or done the door
    /// was gone, and the tree's taking it over keeps the guardian's own door replies off
    /// (NpcHandler). The tree has to offer it again for those, without the quest.
    /// </summary>
    [Collection("MapManager")]
    public class DungeonDoorQuestReplyTests
    {
        [Fact]
        public void A_door_reply_that_starts_a_quest_is_still_offered_once_it_is_under_way_or_done()
        {
            Npcs.Initialize();
            DungeonDoor.Initialize();
            if (!DungeonDoor.For(6674).Asks) return;   // no world base to read

            var store = NpcDialogueContent.Load(Paths.ContentFile(NpcDialogueContent.AuthoredFile), _ => { });
            var missing = new List<string>();
            foreach (var row in store.Rows)
            {
                var door = DungeonDoor.For(row.Key.NpcId);
                if (!door.Asks) continue;
                var doorReplies = new[] { door.Keyring, door.Key }.Where(r => r != 0).ToHashSet();

                foreach (var line in row.Value.Value.Lines)
                foreach (var starts in line.Choices.Where(c => c.StartsQuest != 0 && doorReplies.Contains(c.Reply)))
                {
                    int quest = starts.StartsQuest;
                    foreach (bool done in new[] { false, true })
                    {
                        bool offered = line.Choices.Any(c => c.Reply == starts.Reply && c.StartsQuest == 0 && c.BeforeQuest == 0
                            && DialogueLine.Shows(c, q => q == quest && !done, q => q == quest && done,
                                                  (_, _) => true, (_, _) => false));
                        if (!offered) missing.Add($"{row.Key.NpcId} reply {starts.Reply} with quest {quest} {(done ? "done" : "under way")}");
                    }
                }
            }

            Assert.Empty(missing);
        }
    }
}

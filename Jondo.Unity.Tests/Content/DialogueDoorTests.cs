using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// A reply that opens a dungeon door in its own words: Virgil's «Mostrarle las plumas y usar la
    /// llave» in Lord Crow's library, which the guardians' wording search cannot read.
    /// </summary>
    public class DialogueDoorTests
    {
        private const string Tree = """
            { "dialogues": [
              { "npc": 774, "map": 159121408, "opening": 3166, "lines": [
                { "message": 3166, "choices": [
                  { "reply": 78002, "criterion": "HA=632&HA=633&HA=634", "door": "key" },
                  { "reply": 78005, "criterion": "HA=632&HA=633&HA=634", "door": "keyring" },
                  { "reply": 77995 } ] } ] } ] }
            """;

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
        public void The_door_is_read_and_written_back()
        {
            var tree = Read(Tree);
            var choices = tree.Lines.Single().Choices;
            Assert.Equal(DialogueChoice.KeyDoor, choices[0].Door);
            Assert.Equal(DialogueChoice.KeyringDoor, choices[1].Door);
            Assert.Equal("", choices[2].Door);
            Assert.True(tree.OpensADoor);
            Assert.Empty(NpcDialogueContent.Complaints(tree));

            string path = Path.GetTempFileName();
            try
            {
                NpcDialogueContent.Save(path, new[] { tree });
                var again = NpcDialogueContent.Load(path).Rows.Single().Value.Value;
                Assert.Equal(new[] { "key", "keyring", "" }, again.Lines.Single().Choices.Select(c => c.Door));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void A_door_that_is_neither_the_key_nor_the_keyring_is_complained_about()
        {
            var tree = Read(Tree.Replace("\"door\": \"keyring\"", "\"door\": \"gate\""));
            Assert.Contains(NpcDialogueContent.Complaints(tree), complaint => complaint.Contains("«gate»"));
        }

        [Fact]
        public void A_tree_without_a_door_opens_none()
        {
            var tree = Read(Tree.Replace(", \"door\": \"key\"", "").Replace(", \"door\": \"keyring\"", ""));
            Assert.False(tree.OpensADoor);
        }

        [Fact]
        public void The_written_door_names_the_way_in()
        {
            Assert.Equal(DungeonHandler.EntryMethod.Key, DungeonHandler.WrittenDoor(DialogueChoice.KeyDoor));
            Assert.Equal(DungeonHandler.EntryMethod.Keyring, DungeonHandler.WrittenDoor(DialogueChoice.KeyringDoor));
            Assert.Null(DungeonHandler.WrittenDoor(""));
            Assert.Null(DungeonHandler.WrittenDoor("gate"));
        }
    }
}

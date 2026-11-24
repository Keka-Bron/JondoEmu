using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Skeleton Dungeon (47): once the Chafer Ronin is beaten, the winners stay in the boss
    /// room, where Chaferfu (6459) teaches «Invocation de Chaferfu» (373) and shows the way out.
    /// The win used to go straight to the exit, and nobody met him.
    /// </summary>
    public sealed class SkeletonDungeonExitTests
    {
        private const long BossRoom = 87033346;
        private const int Chaferfu = 6459;
        private const int SummonChaferfu = 373;

        [Fact]
        public void The_winners_stay_in_the_boss_room_with_chaferfu()
        {
            if (!File.Exists(Paths.ContentFile(DungeonExitRooms.File))) return;   // no data
            DungeonExitRooms.Forget();

            var after = DungeonExitRooms.AfterBoss(BossRoom);
            Assert.NotNull(after);
            Assert.Equal(47, after!.Dungeon);
            Assert.Equal(BossRoom, after.MapId);
            Assert.Equal(Chaferfu, after.Npc);
            Assert.Equal(new long[] { 65567 }, after.Replies.ToArray());
        }

        [Fact]
        public void Chaferfu_teaches_his_summon_and_his_tree_is_whole()
        {
            string path = Paths.ContentFile(NpcDialogueContent.AuthoredFile);
            if (!File.Exists(path)) return;   // no data

            var tree = NpcDialogueContent.For(NpcDialogueContent.Load(path), Chaferfu, BossRoom);
            Assert.NotNull(tree);
            Assert.Empty(NpcDialogueContent.Complaints(tree!));
            Assert.Equal(49101, tree!.Opening);

            var teach = tree.Lines.SelectMany(l => l.Choices).Single(c => c.TeachesSpell != 0);
            Assert.Equal(65565, teach.Reply);
            Assert.Equal(SummonChaferfu, teach.TeachesSpell);
            Assert.Equal(49103, teach.Next);

            // Every line is reached from the opening.
            var reached = tree.Lines.SelectMany(l => l.Choices).Where(c => c.Next != 0).Select(c => c.Next).ToHashSet();
            Assert.All(tree.Lines.Where(l => l.Message != tree.Opening), l => Assert.Contains(l.Message, reached));
        }

        [Fact]
        public void Chaferfu_stands_on_open_floor_of_the_boss_room()
        {
            string path = Paths.ContentFile("npcs/spawns.json");
            if (!File.Exists(path)) return;   // no data

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var row = doc.RootElement.GetProperty("spawns").EnumerateArray()
                .Single(r => r.GetProperty("npc").GetInt32() == Chaferfu);
            Assert.Equal(BossRoom, row.GetProperty("map").GetInt64());

            int cell = row.GetProperty("cell").GetInt32();
            string walkable = Path.Combine(Path.GetDirectoryName(Paths.DungeonsJson)!, "map_walkable_cells.json");
            if (!File.Exists(walkable)) return;
            using var cells = JsonDocument.Parse(File.ReadAllText(walkable));
            var floor = cells.RootElement.GetProperty(BossRoom.ToString()).EnumerateArray().Select(c => c.GetInt32()).ToList();
            Assert.Contains(cell, floor);
            DungeonExitRooms.Forget();
            Assert.Contains(DungeonExitRooms.AfterBoss(BossRoom)!.Cell, floor);
        }
    }
}

using System;
using System.IO;
using System.Linq;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// Floor passages: a cell that moves whoever stops on it, for the maps with no element to hang
    /// a passage off.
    /// </summary>
    public class FloorPassagesTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(),
                                                     "jondo-floor-" + Guid.NewGuid().ToString("N") + ".json");

        public void Dispose()
        {
            try { File.Delete(_path); } catch (IOException) { }
        }

        [Fact]
        public void A_passage_is_read_with_its_two_ends()
        {
            File.WriteAllText(_path, @"{ ""passages"": [ { ""map"": 100, ""cell"": 0, ""toMap"": 200, ""toCell"": 559 } ] }");

            var passage = Assert.Single(FloorPassages.Load(_path));
            Assert.Equal((100L, 0, 200L, 559), (passage.SourceMapId, passage.SourceCell,
                                                passage.DestinationMapId, passage.DestinationCell));
        }

        [Fact]
        public void A_passage_carries_its_criterion()
        {
            File.WriteAllText(_path, @"{ ""passages"": [ { ""map"": 188484104, ""cell"": 190, ""toMap"": 188483080, ""toCell"": 280,
                                                           ""criterion"": ""(Qo>9521&Qo<9526)"" },
                                                         { ""map"": 100, ""cell"": 1, ""toMap"": 200, ""toCell"": 2 } ] }");

            var passages = FloorPassages.Load(_path);
            Assert.Equal("(Qo>9521&Qo<9526)", passages[0].Criterion);
            Assert.Equal("", passages[1].Criterion);
        }

        /// <summary>The 2.73 world graph's cell passages, the dungeons' and caves' stairs and holes.</summary>
        [Fact]
        public void The_graph_s_cell_passages_are_read()
        {
            string path = Jondo.Unity.Launcher.Paths.WorldGraphFloorPassagesJson;
            if (!File.Exists(path)) return;
            var passages = FloorPassages.Load(path);
            Assert.True(passages.Count > 500);
            Assert.Contains(passages, p => p.SourceMapId == 72090624 && p.SourceCell == 402);   // the Dragocerdo's labyrinth
            Assert.Contains(passages, p => p.Criterion.Length > 0);
        }

        [Fact]
        public void One_with_no_map_or_off_the_board_is_left_out_and_said()
        {
            File.WriteAllText(_path, @"{ ""passages"": [
                { ""cell"": 10, ""toMap"": 200, ""toCell"": 20 },
                { ""map"": 100, ""toMap"": 200, ""toCell"": 20 },
                { ""map"": 100, ""cell"": 560, ""toMap"": 200, ""toCell"": 20 },
                { ""map"": 100, ""cell"": 10, ""toMap"": 200, ""toCell"": 20 } ] }");

            int said = 0;
            var passages = FloorPassages.Load(_path, _ => said++);
            Assert.Equal(10, Assert.Single(passages).SourceCell);
            Assert.Equal(3, said);
        }

        [Fact]
        public void No_file_is_no_passages_and_a_broken_one_is_said()
        {
            Assert.Empty(FloorPassages.Load(Path.Combine(Path.GetTempPath(), "no-such-floor.json")));

            File.WriteAllText(_path, "{ not json");
            string? complaint = null;
            Assert.Empty(FloorPassages.Load(_path, said => complaint = said));
            Assert.NotNull(complaint);
        }

        [Fact]
        public void The_removed_cells_are_read_and_a_bad_one_is_said()
        {
            File.WriteAllText(_path, @"{ ""passages"": [], ""removed"": [ { ""map"": 130023937, ""cell"": 493, ""why"": ""x"" },
                                                                         { ""map"": 100, ""cell"": 900 } ] }");
            int said = 0;
            var removed = FloorPassages.LoadRemoved(_path, _ => said++);
            Assert.Equal(new[] { (130023937L, 493) }, removed.ToArray());
            Assert.Equal(1, said);
        }

        [Fact]
        public void The_shipped_file_takes_the_tower_s_old_stairs_away()
        {
            // The Travelers' Tower's rooms are joined by teleporters in Dofus 3; the 2.73 graph's
            // stairs in the Prisión stood on the cell its teleporter is used from.
            var removed = FloorPassages.LoadRemoved(Jondo.Unity.Launcher.Paths.ContentFile(FloorPassages.AuthoredFile));
            Assert.Contains((130023937L, 493), removed);
            Assert.Contains((130155009L, 493), removed);
        }

        [Fact]
        public void The_shipped_file_reads_whole()
        {
            int said = 0;
            var passages = FloorPassages.Load(Jondo.Unity.Launcher.Paths.ContentFile(FloorPassages.AuthoredFile), _ => said++);
            Assert.Equal(0, said);
            Assert.NotEmpty(passages);
            Assert.Equal(passages.Count, passages.Select(p => (p.SourceMapId, p.SourceCell)).Distinct().Count());
        }
    }
}

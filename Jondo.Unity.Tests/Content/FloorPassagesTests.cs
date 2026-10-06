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

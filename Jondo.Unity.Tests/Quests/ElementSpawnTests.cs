using System.IO;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>
    /// What one element of a «givesEach» row wakes up: two of the four eggs of «Épidémiologie» may set
    /// a monster on whoever samples them, fought alone, and the other two never do.
    /// </summary>
    public class ElementSpawnTests
    {
        private static QuestBinding Read(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
            File.WriteAllText(path, json);
            try { return QuestBindingContent.Load(path).Of(1)!; }
            finally { File.Delete(path); }
        }

        [Fact]
        public void An_element_brings_out_its_own_monster_and_the_others_nothing()
        {
            var binding = Read("""
            { "bindings": [
              { "objective": 1, "quest": 2, "kind": "click", "givesEach": true, "onlyGives": true,
                "elements": [ {"map": 10, "element": 100, "item": 7},
                              {"map": 11, "element": 101, "item": 8,
                               "spawns": {"monster": 3780, "chance": 50, "attacks": true, "alone": true}} ] }
            ] }
            """);

            Assert.Null(binding.SpawnAt(10, 100));
            var wakes = binding.SpawnAt(11, 101);
            Assert.NotNull(wakes);
            Assert.Equal(3780, wakes!.Monster);
            Assert.Equal(1, wakes.Count);
            Assert.Equal(50, wakes.Chance);
            Assert.True(wakes.Attacks);
            Assert.True(wakes.Alone);
        }

        [Fact]
        public void Without_its_own_an_element_brings_out_the_rows()
        {
            var binding = Read("""
            { "bindings": [
              { "objective": 1, "quest": 2, "kind": "click", "givesEach": true,
                "elements": [ {"map": 10, "element": 100, "item": 7} ],
                "spawns": {"monster": 55, "count": 2, "with": [56], "chance": 20} }
            ] }
            """);

            var wakes = binding.SpawnAt(10, 100);
            Assert.NotNull(wakes);
            Assert.Equal(55, wakes!.Monster);
            Assert.Equal(2, wakes.Count);
            Assert.Equal(new[] { 56 }, wakes.With);
            Assert.False(wakes.Attacks);
            Assert.False(wakes.Alone);
        }
    }
}

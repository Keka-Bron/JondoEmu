using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Quests;
using Xunit;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>
    /// «Une voix de crystal» (1912): the barracks piano (511306) brings the ghost in only once the
    /// room is empty of monsters, so its click row asks for a cleared map.
    /// </summary>
    public sealed class CrystalVoicePianoTests
    {
        [Fact]
        public void The_barracks_piano_waits_for_a_cleared_map()
        {
            QuestEngine.Load();
            var piano = QuestEngine.Bindings.Of(13125);
            if (piano == null) return;   // no quest data
            Assert.Equal(QuestBindingKind.Click, piano.Kind);
            Assert.True(piano.MapCleared);
            Assert.Contains(piano.Elements, e => e.MapId == 183502850 && e.ElementId == 511306);

            // The other pianos of the quest have no such condition.
            Assert.False(QuestEngine.Bindings.Of(13131)!.MapCleared);
        }

        [Fact]
        public void A_map_nobody_put_a_group_on_has_none()
        {
            Assert.False(MobSpawnManager.HasGroups(-987_654_321));
        }
    }
}

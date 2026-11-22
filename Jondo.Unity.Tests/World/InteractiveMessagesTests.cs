using System;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Interactives that only speak when pressed, as the official server answered them (packet
    /// captures, 2026-11-20/21): tombstones, statues, mirrors, clocks, the hourglass that moves you,
    /// and the few that open another element of the map (a lever, the castle's piano).
    /// </summary>
    [Collection("MapManager")]
    public sealed class InteractiveMessagesTests
    {
        private static void Load()
        {
            if (MapManager.GetMapInfo(190972421) == null) MapManager.Initialize();
            Assert.NotNull(MapManager.GetMapInfo(190972421));
            InteractiveMessages.Forget();
        }

        [Theory]
        [InlineData(190972421, 515587, 436)]    // Álgata's tombstone
        [InlineData(190842884, 518049, 1162)]   // Vírgula's tomb
        [InlineData(188743687, 540439, 131)]    // Hector's tomb
        [InlineData(192417794, 537425, 128)]    // the mirror
        public void The_element_says_the_official_message(long map, int element, int message)
        {
            Load();
            var said = InteractiveMessages.Of(map, element);
            Assert.NotNull(said);
            Assert.Equal(message, said!.Message);
        }

        [Fact]
        public void The_mirror_names_the_character()
        {
            Load();
            var said = InteractiveMessages.Of(192417794, 537425)!;
            Assert.Equal(new[] { "Tester" }, InteractiveMessages.Fill(said, "Tester", DateTimeOffset.UnixEpoch));
        }

        [Fact]
        public void The_piano_opens_the_hidden_door_for_ten_seconds()
        {
            Load();
            var piano = InteractiveMessages.Of(95685123, 471368)!;
            Assert.Equal((471367, 291, 1, 10), (piano.Opens, piano.OpensCell, piano.OpensState, piano.ClosesAfter));
            Assert.Equal(new[] { 277 }, piano.Frees);
        }

        [Fact]
        public void The_bridge_lever_frees_and_blocks_in_one_answer()
        {
            Load();
            var lever = InteractiveMessages.Of(99090955, 474854)!;
            Assert.Equal(new[] { 298, 313, 312, 326 }, lever.Frees);
            Assert.Equal(new[] { 272, 244, 258, 259 }, lever.Blocks);
        }

        /// <summary>Declared on the map with «Usar», or the client draws it as decor and no click comes.</summary>
        [Theory]
        [InlineData(190972421, 515587)]
        [InlineData(188743687, 540439)]
        public void The_element_is_declared_usable(long map, int element)
        {
            Load();
            Interactives.Initialize();
            TeleportManager.Initialize();
            Resources.Initialize();
            InteractiveRegistry.Initialize();
            var declared = MapInteractiveMessages.Snapshot(map, 0).Elements.Single(e => e.ElementId == element);
            Assert.Contains(InteractiveMessages.Skill, declared.EnabledSkills.Select(s => s.SkillId));
        }
    }
}

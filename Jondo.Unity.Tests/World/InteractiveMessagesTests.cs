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

        /// <summary>
        /// The urn of Kanniball Village's exit room hands the Kannibulle spell scroll (Dofus pour les
        /// noobs, «Village Kanniboul»), under the scroll's own criterion, and no second one while one
        /// is carried.
        /// </summary>
        [Fact]
        public void The_kanniball_exit_urn_gives_the_kannibubble_scroll_once_carried()
        {
            Load();
            var urn = InteractiveMessages.Of(157553664, 528873);
            Assert.NotNull(urn);
            Assert.Equal(new[] { (27416, 1) }, urn!.Gives);
            Assert.Equal("PT!24015&PT!24016", urn.Criterion);
            Assert.Equal(new[] { (27416, 1) }, InteractiveMessages.HandOut(urn, _ => 0));
            Assert.Empty(InteractiveMessages.HandOut(urn, item => item == 27416 ? 1 : 0));
        }

        /// <summary>What the urn gives is the scroll that teaches Kannibubble (24015).</summary>
        [Fact]
        public void The_kanniball_exit_urn_scroll_teaches_kannibubble()
        {
            if (!SpellTable.IsLoaded || DatabaseManager.GetItemTemplateType(27416) == 0) return;
            Load();
            var (item, _) = InteractiveMessages.Of(157553664, 528873)!.Gives.Single();
            Assert.Equal(24015, LearnedSpells.TaughtBy(item));
        }

        /// <summary>Declared on the map with «Usar», or the client draws it as decor and no click comes.</summary>
        [Theory]
        [InlineData(190972421, 515587)]
        [InlineData(188743687, 540439)]
        [InlineData(157553664, 528873)]
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

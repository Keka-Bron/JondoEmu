using System.Collections.Generic;
using Jondo.Unity.Server.Handlers;
using Xunit;

namespace Jondo.Unity.Tests.Movement
{
    public sealed class LandingCellTests
    {
        private static readonly HashSet<int> Fight = new() { 319, 555 };
        private static readonly HashSet<int> Roleplay = new() { 319 };

        private static int Nearest(int cell) => 319;

        [Fact]
        public void A_border_cell_that_is_only_fight_walkable_falls_back_to_the_roleplay_cell()
        {
            Assert.Equal(319, WorldMoveHandler.ChooseLandingCell(555, Fight, Roleplay, Nearest));
        }

        [Fact]
        public void A_trimmed_border_cell_with_a_fight_walkable_way_inland_is_kept()
        {
            // 555 -> 541 -> 527 walk over fight cells into the roleplay set: an ordinary border.
            var fight = new HashSet<int> { 555, 541, 527 };
            var roleplay = new HashSet<int> { 527 };
            Assert.Equal(555, WorldMoveHandler.ChooseLandingCell(555, fight, roleplay, _ => 527));
        }

        [Fact]
        public void A_cell_in_both_sets_is_kept()
        {
            Assert.Equal(319, WorldMoveHandler.ChooseLandingCell(319, Fight, Roleplay, _ => 0));
        }

        [Fact]
        public void Fight_data_alone_keeps_the_old_behaviour()
        {
            Assert.Equal(555, WorldMoveHandler.ChooseLandingCell(555, Fight, null, Nearest));
        }

        [Fact]
        public void Missing_fight_data_keeps_the_arrival_unchanged()
        {
            Assert.Equal(555, WorldMoveHandler.ChooseLandingCell(555, null, Roleplay, Nearest));
        }
    }
}

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

using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// An arena is never where a character is saved: while a fight keeps him on one, his sheet
    /// keeps the map he left to fight, so a server or a client closing mid-fight brings him back
    /// there and not to a board with no way out.
    /// </summary>
    [Collection("MapManager")]
    public class ArenaExitTests
    {
        private const long Kanojedo = 99090957, KanojedoArena = 99222029;

        [Fact]
        public void In_a_fight_the_sheet_keeps_the_map_he_left()
        {
            var state = new SessionState { MapId = KanojedoArena, CellId = 368, RoleplayMapId = Kanojedo, RoleplayCellId = 286 };
            var (map, cell) = DatabaseManager.PlaceToSave(state);
            Assert.Equal(Kanojedo, map);
            Assert.True(cell >= 0);
        }

        [Fact]
        public void Out_of_a_fight_the_sheet_keeps_where_he_stands()
        {
            var state = new SessionState { MapId = Kanojedo, CellId = 286 };
            Assert.Equal((Kanojedo, 286), DatabaseManager.PlaceToSave(state));

            // Back from the fight, the roleplay map he stands on again.
            state.RoleplayMapId = Kanojedo;
            Assert.Equal((Kanojedo, 286), DatabaseManager.PlaceToSave(state));
        }

        [Fact]
        public void The_kanojedo_s_arena_leads_back_to_the_kanojedo()
        {
            Assert.Equal(KanojedoArena, MeasuredArenas.Of(Kanojedo));
            Assert.Equal(Kanojedo, MeasuredArenas.MapOf(KanojedoArena));
            Assert.Equal(0, MeasuredArenas.MapOf(Kanojedo));
        }
    }
}

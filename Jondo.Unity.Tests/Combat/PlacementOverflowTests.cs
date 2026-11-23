using System.Collections.Generic;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Ugah temple's fourth room is fought on a board that flags two cells a side, and the
    /// room sends four monsters. The ones past the flagged cells all started on the first of them.
    /// </summary>
    [Collection("MapManager")]
    public class PlacementOverflowTests
    {
        private const long Board = 182324231;

        [Fact]
        public void Monsters_past_the_flagged_cells_start_on_free_cells_of_their_own()
        {
            var fight = new FightInstance(990_182_324, Board);
            fight.RedPlacementCells.AddRange(new[] { 174, 304 });
            fight.BluePlacementCells.AddRange(new[] { 309, 439 });
            fight.AddMonster(new Fighter { Id = -1, Name = "A", IsMonster = true, TeamId = 1, MaxHP = 10, CurrentHP = 10, CellId = 174 });
            fight.AddMonster(new Fighter { Id = -2, Name = "B", IsMonster = true, TeamId = 1, MaxHP = 10, CurrentHP = 10, CellId = 304 });

            var taken = new HashSet<int> { 174, 304 };
            for (int i = 3; i <= 4; i++)
            {
                int cell = FightHandler.CasillaDeColocacionSobrante(fight, fight.RedPlacementCells[0]);
                Assert.True(taken.Add(cell), $"cell {cell} already taken");
                Assert.DoesNotContain(cell, fight.BluePlacementCells);
                fight.AddMonster(new Fighter { Id = -i, Name = "M", IsMonster = true, TeamId = 1, MaxHP = 10, CurrentHP = 10, CellId = cell });
            }
        }
    }
}

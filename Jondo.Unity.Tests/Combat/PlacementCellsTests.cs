using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Where the two teams stand at the start of a fight, and the day five monsters shared one cell.
    /// </summary>
    /// <remarks>
    /// On Astrub map 188744196 the player started a fight and every monster was placed on the same
    /// red square; aiming a weapon at it hit one of them and left the rest untouched. The server
    /// said so in its own log: "1 contra 5, 1 casillas azules y 1 rojas."
    ///
    /// The arena was fine — 82 fight-walkable cells. The placement was being fed from
    /// <c>MobSpawnManager.GetInnerWalkableCells</c>, which is a ROLEPLAY filter: it keeps only
    /// cells whose twelve neighbours within radius 2 are all walkable and that sit away from the
    /// border, which is what plants a monster group on an open map and not what places two teams
    /// in an arena. On that arena it passed 2 of the 77 cells.
    ///
    /// Its safety net was <c>if none survived, use them all</c> — and two is not none, so the net
    /// did not fire. Incarnam only ever worked by luck: there the same filter returns zero, the net
    /// fires, and all 65 cells get used.
    /// </remarks>
    /// <summary>
    /// Las clases que tocan el estado estatico de MapManager, en una sola coleccion.
    /// </summary>
    /// <remarks>
    /// xUnit corre las clases en PARALELO, y estas se pisan: ReturnFromFightCellTests instala un
    /// diccionario de casillas con un solo mapa, y MonsterVetoTests llama a MapManager.Initialize(),
    /// que lo reconstruye entero desde la base. Si eso cae entre el constructor de la primera y su
    /// asercion, la herreria deja de tener las casillas que el test acaba de poner.
    ///
    /// Se vio como lo que es: la suite fallaba una vez de cada siete en
    /// An_arena_cell_is_pulled_onto_a_real_one, y en aislado pasaba siempre. Un test que falla a
    /// veces es peor que uno que falla: se aprende a ignorarlo.
    /// </remarks>
    [CollectionDefinition("MapManager")]
    public class MapManagerCollection { }

    [Collection("MapManager")]
    public class PlacementCellsTests
    {
        // The real fight-walkable cells of arena 188752387, the one Astrub 188744196 resolves to.
        // Trimmed to the ordered list that matters: the halving split takes the first eight of each
        // half, so the whole set is not needed to pin the outcome.
        private static List<int> AstrubArena()
        {
            var cells = new List<int>();
            for (int cell = 233; cells.Count < 82; cell++) cells.Add(cell);
            return cells;
        }

        [Fact]
        public void A_real_arena_gives_eight_places_to_each_team()
        {
            var fight = new FightInstance(1, 188744196, 188752387);

            fight.GeneratePlacementCells(AstrubArena());

            Assert.Equal(8, fight.BluePlacementCells.Count);
            Assert.Equal(8, fight.RedPlacementCells.Count);
        }

        [Fact]
        public void And_the_two_teams_never_share_a_square()
        {
            var fight = new FightInstance(1, 188744196, 188752387);

            fight.GeneratePlacementCells(AstrubArena());

            Assert.Empty(fight.BluePlacementCells.Intersect(fight.RedPlacementCells));
            Assert.Equal(fight.RedPlacementCells.Count, fight.RedPlacementCells.Distinct().Count());
            Assert.Equal(fight.BluePlacementCells.Count, fight.BluePlacementCells.Distinct().Count());
        }

        [Fact]
        public void Two_cells_is_not_enough_and_does_not_collapse_to_one_each()
        {
            // The exact bug. Two cells passed the old filter, the "none survived" guard did not
            // fire because two is not none, and halving gave one blue and one red — so every
            // monster in the fight was assigned the same square.
            var fight = new FightInstance(1, 188744196, 188752387);

            fight.GeneratePlacementCells(new List<int> { 384, 398 });

            Assert.Equal(8, fight.BluePlacementCells.Count);
            Assert.Equal(8, fight.RedPlacementCells.Count);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(15)]
        public void Anything_under_sixteen_falls_back_rather_than_squeezing(int howMany)
        {
            // Sixteen is not a taste: it is eight a side. Below it, halving the list always leaves
            // at least one team with fewer places than fighters, and fighters with no place of
            // their own end up stacked.
            var fight = new FightInstance(1, 1, 1);

            fight.GeneratePlacementCells(Enumerable.Range(200, howMany).ToList());

            Assert.Equal(8, fight.BluePlacementCells.Count);
            Assert.Equal(8, fight.RedPlacementCells.Count);
            Assert.Empty(fight.BluePlacementCells.Intersect(fight.RedPlacementCells));
        }

        [Fact]
        public void Nothing_at_all_still_gives_a_fight_to_stand_in()
        {
            var fight = new FightInstance(1, 1, 1);

            fight.GeneratePlacementCells(null!);

            Assert.Equal(8, fight.BluePlacementCells.Count);
            Assert.Equal(8, fight.RedPlacementCells.Count);
        }
    }

    /// <summary>
    /// The cell a player stands on when the fight is over.
    /// </summary>
    /// <remarks>
    /// He won a fight inside the Incarnam smithy and his own character was not drawn. Pressing H
    /// made him reappear, which was the clue: the haven-bag path runs its cell through
    /// <c>GetNearestWalkableCell</c> and the end-of-fight path did not.
    ///
    /// No frame was missing. The one we sent carried a cell from the ARENA — 189 — and the smithy
    /// (map 153355264) has 34 walkable cells running from 244 to 414. There is no cell 189 on that
    /// map at all, so the client had nowhere to draw him.
    /// </remarks>
    [Collection("MapManager")]
    public class ReturnFromFightCellTests
    {
        // The smithy's real walkable cells, from datos/map_walkable_cells.json: 34 of them, none
        // below 244. Reproduced as the two facts the bug turns on rather than all 34.
        private const long Smithy = 153355264;

        public ReturnFromFightCellTests()
        {
            MapManager.WalkableCells = new Dictionary<long, List<int>>
            {
                [Smithy] = new List<int> { 244, 258, 259, 273, 287, 299, 300, 314, 328, 414 },
            };
        }

        [Fact]
        public void The_smithy_has_no_cell_189()
        {
            // The premise. If this ever stops being true the bug below cannot happen and the test
            // above it is measuring nothing.
            Assert.DoesNotContain(189, MapManager.WalkableCells[Smithy]);
            Assert.Contains(299, MapManager.WalkableCells[Smithy]);
        }

        [Fact]
        public void An_arena_cell_is_pulled_onto_a_real_one()
        {
            // 189 is four rows above the top of the room. Whatever it lands on, it has to be a cell
            // the map actually has, or the player is drawn nowhere.
            int landed = MapManager.GetNearestWalkableCell(Smithy, 189);

            Assert.Contains(landed, MapManager.WalkableCells[Smithy]);
        }

        [Fact]
        public void A_cell_that_is_already_good_is_left_alone()
        {
            // The clamp must not move somebody who was standing somewhere real: after the first
            // fix the stored cell is the one he walked in on, and rounding it would teleport him a
            // few squares every fight for no reason.
            Assert.Equal(299, MapManager.GetNearestWalkableCell(Smithy, 299));
        }
    }
}

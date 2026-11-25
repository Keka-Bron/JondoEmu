using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The wanted posters on every board, from Dofus pour les noobs' pages and the client's map
    /// elements: each poster opens its criminal's document, whose link starts his quest.
    /// </summary>
    public sealed class WantedPosterBoardTests
    {
        [Theory]
        [InlineData(192413706, 516014, 100)]   // Astrub's militia, measured on the official server
        [InlineData(192413706, 516005, 67)]    // (2026-11-20, each of the eleven clicked): Armyna,
        [InlineData(192413706, 516013, 61)]    // Qil Bil, Nondome Dula
        [InlineData(95686149, 484126, 365)]    // Amakna castle, first room: Ali Grothor
        [InlineData(95687173, 488238, 410)]    // Amakna castle, second room: Tyranne la Terrible
        [InlineData(121899010, 483922, 368)]   // the Justiciers' base: Amy, top left
        [InlineData(54536209, 457616, 166)]    // Frigost's Bourgade: Bouflouth, leftmost
        [InlineData(54171466, 458396, 173)]    // the buried village: Fuji Givrefoux, leftmost
        [InlineData(111411713, 480610, 172)]   // Harebourg's tower: the count himself
        [InlineData(170133504, 509179, 473)]   // Sufokia: the Homard Medali, at the top
        [InlineData(173938178, 509666, 481)]   // Saharach: the Khepricorne, right wall
        [InlineData(234751491, 539149, 686)]   // Osavora: Ganos
        [InlineData(217449476, 522395, 456)]   // Bonta's militia, measured (2026-11-21, every poster
        [InlineData(217449476, 522397, 453)]   // clicked: 17 of the 19 were crossed): Nonô o Wobot,
        [InlineData(217449476, 523690, 454)]   // Mestre Bolota, Ladi Nosbago
        [InlineData(216663298, 522373, 452)]   // Brâkmar's militia, measured (2026-11-21, every poster
        [InlineData(216663298, 522377, 514)]   // clicked: 18 of the 19 were crossed): Sam Sagaz
        [InlineData(216663298, 523662, 453)]
        [InlineData(137363456, 485515, 385)]   // Srambad, measured: Gein
        public void Each_board_s_poster_opens_its_criminal(long map, int element, int document)
        {
            Readables.Load();
            Assert.Equal(document, Readables.Of(map, element)?.Document);
        }

        [Fact]
        public void Every_board_has_all_its_posters()
        {
            Readables.Load();
            Assert.Equal(11, Readables.OnMap(192413706).Count());
            Assert.Equal(19, Readables.OnMap(217449476).Count());
            Assert.Equal(19, Readables.OnMap(216663298).Count());
            Assert.Equal(8, Readables.OnMap(54536209).Count());   // measured 2026-11-23: eight posters, each its own criminal
            Assert.Equal(6, Readables.OnMap(111411713).Count());
        }
    }

    /// <summary>Every criminal can be handed over: each jailer of the data stands on some map.</summary>
    [Collection("MapManager")]
    public sealed class WantedJailerPlacementTests
    {
        [Fact]
        public void Every_jailer_stands_somewhere()
        {
            Npcs.Initialize();
            WantedPosters.Load();
            var placed = Npcs.AllSpawns.Select(s => s.NpcId).ToHashSet();
            var jailers = WantedPosters.All.SelectMany(c => c.Jailers).Select(j => j.Npc).Distinct().ToList();
            Assert.NotEmpty(jailers);
            Assert.All(jailers, npc => Assert.Contains(npc, placed));
        }
    }
}

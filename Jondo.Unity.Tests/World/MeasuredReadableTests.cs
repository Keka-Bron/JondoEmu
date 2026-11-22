using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Signs, books and wanted posters open the document the official server opened for them
    /// (packet captures, 2026-11-17 to 11-21: every poster of the Bonta and Brâkmar militia boards
    /// clicked, and the books and boards met on the way). 17 of Bonta's 19 posters and 18 of
    /// Brâkmar's opened another criminal before.
    /// </summary>
    public sealed class MeasuredReadableTests
    {
        [Theory]
        [InlineData(217449476, 522397, 453)]
        [InlineData(217449476, 522398, 452)]
        [InlineData(217449476, 522369, 509)]
        [InlineData(217449476, 522358, 513)]
        [InlineData(217449476, 522356, 507)]
        [InlineData(217449476, 522353, 506)]
        [InlineData(217449476, 523691, 508)]
        [InlineData(217449476, 522348, 514)]
        [InlineData(217449476, 522349, 511)]
        [InlineData(217449476, 522395, 456)]
        [InlineData(217449476, 522399, 457)]
        [InlineData(217449476, 522391, 460)]
        [InlineData(217449476, 522388, 459)]
        [InlineData(217449476, 522389, 458)]
        [InlineData(217449476, 523692, 505)]
        [InlineData(217449476, 523695, 512)]
        [InlineData(217449476, 523696, 455)]
        [InlineData(217449476, 523690, 454)]
        [InlineData(217449476, 523694, 510)]
        [InlineData(216663298, 522373, 452)]
        [InlineData(216663298, 522377, 514)]
        [InlineData(216663298, 522378, 507)]
        [InlineData(216663298, 523663, 454)]
        [InlineData(216663298, 522379, 512)]
        [InlineData(216663298, 522380, 511)]
        [InlineData(216663298, 523666, 508)]
        [InlineData(216663298, 523665, 510)]
        [InlineData(216663298, 523664, 505)]
        [InlineData(216663298, 523662, 453)]
        [InlineData(216663298, 522357, 460)]
        [InlineData(216663298, 522351, 459)]
        [InlineData(216663298, 522355, 458)]
        [InlineData(216663298, 522359, 457)]
        [InlineData(216663298, 522362, 456)]
        [InlineData(216663298, 522363, 455)]
        [InlineData(216663298, 522374, 509)]
        [InlineData(216663298, 522375, 506)]
        [InlineData(216663298, 522376, 513)]
        [InlineData(17048576, 461843, 216)]    // Enutrof temple lore book: «Os Dedos do Enutrof», was the god's
        [InlineData(183762948, 511846, 219)]   // Osamodas temple lore book
        [InlineData(17048582, 461844, 236)]    // Enutrof temple, the book about the god: new
        [InlineData(95686149, 510952, 499)]    // Amakna castle hall's books were rotated
        [InlineData(191105024, 515954, 71)]    // the «Avisos de procurado» board of [5,-19]: new
        [InlineData(103547392, 479316, 342)]   // the Ecaflip house of games' book: new
        public void The_element_opens_the_official_document(long map, int element, int document)
        {
            Readables.Forget();
            Assert.Equal(document, Readables.Of(map, element)?.Document);
        }
    }
}

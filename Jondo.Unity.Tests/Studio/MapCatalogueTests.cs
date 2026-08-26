using Jondo.Unity.Studio.Data;
using Xunit;

namespace Jondo.Unity.Tests.Studio
{
    /// <summary>
    /// Finding a map from what somebody typed.
    /// </summary>
    /// <remarks>
    /// A map id is a number nobody carries in their head. The coordinate is what is on screen while
    /// you play, and it is what people say to each other, so the field has to take it in every
    /// shape a person might write it — including the one with the brackets, which is how the game
    /// itself shows it.
    /// </remarks>
    public class MapCatalogueTests
    {
        [Theory]
        [InlineData("4,-18")]
        [InlineData("4, -18")]
        [InlineData("4 -18")]
        [InlineData("[4, -18]")]
        [InlineData("  4;-18 ")]
        [InlineData("4/-18")]
        [InlineData("4|-18")]
        public void A_coordinate_is_read_however_it_is_written(string typed)
        {
            Assert.True(MapCatalogue.TryCoordinates(typed, out int x, out int y));
            Assert.Equal(4, x);
            Assert.Equal(-18, y);
        }

        [Fact]
        public void The_origin_reads_as_the_origin()
        {
            Assert.True(MapCatalogue.TryCoordinates("0,0", out int x, out int y));
            Assert.Equal(0, x);
            Assert.Equal(0, y);
        }

        /// <summary>
        /// A map id has to stay a map id. It is eight digits with no separator in it, so reading it
        /// as a coordinate would send every search for a map to nowhere.
        /// </summary>
        [Theory]
        [InlineData("241438721")]
        [InlineData("")]
        [InlineData("Amakna")]
        [InlineData("4,")]
        [InlineData("4,5,6")]
        [InlineData("a,b")]
        public void Anything_that_is_not_a_coordinate_says_so(string typed)
            => Assert.False(MapCatalogue.TryCoordinates(typed, out _, out _));
    }
}

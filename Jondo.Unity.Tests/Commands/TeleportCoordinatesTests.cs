using Jondo.Unity.Server.Handlers;
using Xunit;

namespace Jondo.Unity.Tests.Commands
{
    /// <summary>
    /// The coordinates of .teleport, as they really arrive: the client turns what the
    /// player types into a map link before sending it.
    /// </summary>
    public class TeleportCoordinatesTests
    {
        /// <summary>
        /// Measured in the log: «.teleport [0,-8]» is typed and the server receives
        /// «.teleport {{map,0,-8,1}}». Three times in a row it answered with its usage.
        /// </summary>
        [Theory]
        [InlineData("{{map,0,-8,1}}", 0, -8)]
        [InlineData("{{map,-5,54,1}}", -5, 54)]
        [InlineData("[0,-8]", 0, -8)]
        [InlineData("(-1, 0)", -1, 0)]
        [InlineData("13 35", 13, 35)]
        [InlineData("[14;36]", 14, 36)]
        public void The_clients_map_link_is_read_as_coordinates(string typed, int x, int y)
        {
            Assert.True(CommandHandler.ParseCoordinates(typed, out int gotX, out int gotY));
            Assert.Equal(x, gotX);
            Assert.Equal(y, gotY);
        }

        /// <summary>A single number is not coordinates: it is a map id, and another branch handles it.</summary>
        [Theory]
        [InlineData("")]
        [InlineData("106169344")]
        [InlineData("[0]")]
        [InlineData("{{item,1575}}")]
        [InlineData("a,b")]
        public void Anything_else_is_not_coordinates(string typed)
        {
            Assert.False(CommandHandler.ParseCoordinates(typed, out _, out _));
        }
    }
}

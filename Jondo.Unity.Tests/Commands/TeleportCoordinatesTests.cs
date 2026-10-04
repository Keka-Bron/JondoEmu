using Jondo.Unity.Server.Handlers;
using Xunit;

namespace Jondo.Unity.Tests.Commands
{
    /// <summary>
    /// Las coordenadas de .teleport, tal como llegan de verdad: el cliente convierte lo que el
    /// jugador teclea en un enlace de mapa antes de mandarlo.
    /// </summary>
    public class TeleportCoordinatesTests
    {
        /// <summary>
        /// Medido en el registro: se teclea «.teleport [0,-8]» y el servidor recibe
        /// «.teleport {{map,0,-8,1}}». Tres veces seguidas contestó con su uso.
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

        /// <summary>Un solo número no son coordenadas: es un id de mapa, y lo atiende otra rama.</summary>
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

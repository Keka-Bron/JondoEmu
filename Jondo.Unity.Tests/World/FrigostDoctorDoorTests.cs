using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    [Collection("MapManager")]
    public sealed class FrigostDoctorDoorTests
    {
        private const long Street = 54173997;
        private const long House = 54534177;
        private const long Cellar = 54535201;
        private const int StreetDoor = 457182;

        [Fact]
        public void Albert_Pess_door_enters_the_doctor_s_house_and_the_cellar()
        {
            if (!MapManager.WalkableCells.ContainsKey(Street)) MapManager.Initialize();
            Interactives.Initialize();
            Houses.Initialize();
            TeleportManager.Initialize();

            Assert.False(Houses.TryGetDoor(Street, StreetDoor, out _));

            Assert.True(TeleportManager.TryGet(Street, StreetDoor, out var entrance));
            Assert.Equal(House, entrance.DestinationMapId);
            Assert.Equal(382, entrance.DestinationCellId);   // measured on the official server, 2026-11-23

            Assert.True(TeleportManager.TryGet(House, 504218, out var exit));
            Assert.Equal(Street, exit.DestinationMapId);
            Assert.Equal(410, exit.DestinationCellId);

            Assert.True(TeleportManager.TryGet(House, 457181, out var down));
            Assert.Equal(Cellar, down.DestinationMapId);
            Assert.Equal(352, down.DestinationCellId);

            Assert.True(TeleportManager.TryGet(Cellar, 457180, out var up));
            Assert.Equal(House, up.DestinationMapId);
            Assert.Equal(310, up.DestinationCellId);   // measured on the official server, 2026-11-23
        }
    }
}

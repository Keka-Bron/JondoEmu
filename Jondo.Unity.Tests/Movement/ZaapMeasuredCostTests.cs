using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Movement
{
    /// <summary>
    /// The zaap's price, against the 32 prices the official server listed (packet capture,
    /// 2026-11-21, the zaap destination list) for a character whose origin was [9,-20] (193331714 is
    /// a map there): ten kamas per whole step of the straight line, and Incarnam's three 1000 more.
    /// </summary>
    [Collection("MapManager")]
    public sealed class ZaapMeasuredCostTests
    {
        private const long Origin = 193331714;   // [9,-20]

        [Theory]
        [InlineData(88085249, 420)]
        [InlineData(88212481, 450)]
        [InlineData(147590153, 370)]
        [InlineData(88212746, 340)]
        [InlineData(84806401, 140)]
        [InlineData(88082704, 270)]
        [InlineData(154642, 660)]
        [InlineData(156762120, 410)]
        [InlineData(171967506, 460)]
        [InlineData(156240386, 250)]
        [InlineData(54172969, 890)]
        [InlineData(68552706, 160)]
        [InlineData(88213271, 220)]
        [InlineData(185860609, 180)]
        [InlineData(173278210, 380)]
        [InlineData(68419587, 160)]
        [InlineData(223349001, 680)]
        [InlineData(165153537, 250)]
        [InlineData(164364304, 290)]
        [InlineData(165152263, 230)]
        [InlineData(73400320, 320)]
        [InlineData(207619076, 140)]
        [InlineData(191105026, 40)]
        [InlineData(153879813, 1160)]
        [InlineData(120062979, 140)]
        [InlineData(153880064, 1200)]
        [InlineData(154010371, 1190)]
        [InlineData(212600323, 530)]
        [InlineData(212861955, 660)]
        [InlineData(95422468, 460)]
        [InlineData(99615238, 220)]
        [InlineData(142087694, 390)]
        public void The_price_is_the_official_one(long destination, long price)
        {
            if (MapManager.GetMapInfo(Origin) == null) MapManager.Initialize();
            if (MapManager.GetMapInfo(Origin) == null || MapManager.GetMapInfo(destination) == null) return;   // no world data
            if (Interactives.AreaOfSubArea(MapManager.GetMapInfo(destination)!.SubAreaId) < 0) Interactives.Initialize();

            Assert.Equal(price, ZaapTravelHandler.CostBetween(Origin, destination));
        }
    }
}

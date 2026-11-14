using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The compasses of the bag (effect 1162, ChangeCompassByRef): the flag they put on the world
    /// map, and where Meriana's points.
    /// </summary>
    public class CompassItemTests
    {
        private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));

        /// <summary>
        /// lsd { f2: type, f4: { f1: x, f2: y } }: the plain compass (0) leaves its type out, as proto3
        /// does with a zero, and a negative coordinate goes as a ten-byte varint, as an int32 does.
        /// </summary>
        [Fact]
        public void The_flag_carries_the_type_and_the_coordinates()
        {
            Assert.Equal("220408051001", Hex(CompassItemHandler.BuildCompass(0, 5, 1)));
            Assert.Equal("1004220408051001", Hex(CompassItemHandler.BuildCompass(4, 5, 1)));
            Assert.Equal("220d08faffffffffffffffff011003",
                         Hex(CompassItemHandler.BuildCompass(0, -6, 3)));
        }

        /// <summary>
        /// Meriana's first compass of «Volver a ver a Dark Vlad, y seguir sin morirse» (14978) points
        /// through map reference 186 at Bowis' workshop, 99356682, which the quest's next objective names.
        /// </summary>
        [Fact]
        public void Merianas_compass_points_at_Bowis_workshop()
        {
            if (!TravelData.TryMapReference(186, out long map, out _)) return;   // no datos/travel_*.json
            Assert.Equal(99356682L, map);

            var effects = DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(14978));
            if (effects.Count == 0) return;   // no world base
            var compass = Assert.Single(effects, e => e.EffectId == CompassItemHandler.CompassByReferenceEffect);
            Assert.Equal(186, compass.DiceSide);
        }
    }
}

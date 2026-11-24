using System;
using System.Linq;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The life regeneration switch the client runs on: ktz starts its counter, kuq stops it.
    /// </summary>
    /// <remarks>
    /// The kuq is what was missing at fight entry. The counter the world entry started ran on
    /// through every fight, adding a point every half second to the local player's own bar,
    /// which read as "he recovers life tick by tick in the middle of the fight".
    /// </remarks>
    public class LifeRegenerationTests
    {
        /// <summary>The ktz behind every kml kmp: 0805.</summary>
        [Fact]
        public void Regeneration_starts_at_the_measured_rate()
        {
            byte[] packet = ConnectionProtocol.BuildRegenerationStarted(ConnectionProtocol.RegenerationRate);

            Assert.Equal("0805", Hex(ConnectionProtocol.ReadPayload(packet, Op.Ktz)));
        }

        /// <summary>
        /// 76 seconds between the ktz of one fight's end and the kuq of the next fight's entry
        /// gave f2 = 151 in the capture: half-second ticks.
        /// </summary>
        [Fact]
        public void Ticks_are_half_seconds_since_the_counter_started()
        {
            var started = new DateTime(2026, 8, 9, 21, 23, 34, DateTimeKind.Utc);

            Assert.Equal(152, ConnectionProtocol.RegenerationTicksSince(started, started.AddSeconds(76)));
            Assert.Equal(151, ConnectionProtocol.RegenerationTicksSince(started, started.AddSeconds(75.9)));
            Assert.Equal(0, ConnectionProtocol.RegenerationTicksSince(started, started.AddMilliseconds(499)));
        }

        [Fact]
        public void A_counter_that_never_started_ran_for_nothing()
        {
            Assert.Equal(0, ConnectionProtocol.RegenerationTicksSince(default, DateTime.UtcNow));
        }

        private static string Hex(byte[]? bytes)
            => bytes == null ? "<null>" : string.Concat(bytes.Select(b => b.ToString("x2")));
    }
}

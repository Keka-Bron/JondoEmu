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
        /// <summary>The kuq of the 9th of August challenge, frame for frame: {5211, 151, 5307}.</summary>
        [Fact]
        public void Regeneration_ends_with_life_ticks_and_maximum()
        {
            byte[] packet = ConnectionProtocol.BuildRegenerationEnded(life: 5211, ticks: 151, maxLife: 5307);

            Assert.Equal("08db2810970120bb29", Hex(ConnectionProtocol.ReadPayload(packet, Op.Kuq)));
        }

        /// <summary>And the one of the fight before it, with a full bar: {5307, 1079, 5307}.</summary>
        [Fact]
        public void A_full_bar_still_reports_the_ticks()
        {
            byte[] packet = ConnectionProtocol.BuildRegenerationEnded(life: 5307, ticks: 1079, maxLife: 5307);

            Assert.Equal("08bb2910b70820bb29", Hex(ConnectionProtocol.ReadPayload(packet, Op.Kuq)));
        }

        /// <summary>A counter that never ran leaves f2 out, as proto3 does with a zero.</summary>
        [Fact]
        public void No_ticks_means_no_f2()
        {
            byte[] packet = ConnectionProtocol.BuildRegenerationEnded(life: 2400, ticks: 0, maxLife: 2400);

            Assert.Equal("08e01220e012", Hex(ConnectionProtocol.ReadPayload(packet, Op.Kuq)));
        }

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

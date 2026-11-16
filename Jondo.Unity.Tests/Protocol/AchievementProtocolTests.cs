using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Achievements;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// The achievement messages, byte for byte against what Ankama's server sent.
    /// </summary>
    /// <remarks>
    /// Every payload below is copied out of a capture, with the capture and the frame named. The
    /// claim sequence of <c>Logros\aceptar recompensas de un logro</c> is the whole of that file:
    /// <c>mga {8990}</c> up, then <c>kub</c>, <c>kuf {545}</c> and <c>mfs</c> down. The window is
    /// the end of <c>Chats\usando todos los chats</c>, frames 103 to 108.
    /// </remarks>
    public class AchievementProtocolTests
    {
        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        private static byte[] Bytes(string hex) => Convert.FromHexString(hex);

        // ─── Earned and paid ──────────────────────────────────────────────────────

        [Fact]
        public void Paid_for_is_the_captured_mfs()
        {
            // Logros\aceptar recompensas de un logro, frame 3, and the rat hunt, frame 445.
            Assert.Equal("1001209e46", Hex(AchievementProtocol.BuildRewarded(8990)));
            Assert.Equal("100120a703", Hex(AchievementProtocol.BuildRewarded(423)));
        }

        [Fact]
        public void The_claim_is_read_as_the_capture_sends_it()
        {
            // One achievement (frame 0 of the Logros capture), and -1 for "all of them" as the
            // tutorial, the rat hunt and the guild capture send it.
            Assert.Equal(8990, AchievementProtocol.ReadClaim(Bytes("089e46")));
            Assert.Equal(-1, AchievementProtocol.ReadClaim(Bytes("08ffffffffffffffffff01")));
        }

        [Fact]
        public void The_experience_gained_is_the_captured_kuf()
        {
            // Frame 2 of the Logros capture: 545, exactly what the kub before it went up by.
            Assert.Equal("08a104", Hex(CharacterRewards.BuildExperienceGained(545)));
            // The tutorial: 115 for 8518, 27 for 424.
            Assert.Equal("0873", Hex(CharacterRewards.BuildExperienceGained(115)));
            Assert.Equal("081b", Hex(CharacterRewards.BuildExperienceGained(27)));
        }

        [Fact]
        public void The_kamas_of_a_claim_are_the_captured_ivf_and_lqn()
        {
            // Tutorial, frames 1861 and 1862: achievement 120 pays 2 kamas.
            Assert.Equal("0802", Hex(ConnectionProtocol.BuildKamas(2)));
            Assert.Equal("102d220132", Hex(ConnectionProtocol.BuildInfoMessage(
                InfoMessages.Info, InfoMessages.KamasGained, "2")));
        }

        // ─── The window ───────────────────────────────────────────────────────────

        [Fact]
        public void The_window_requests_are_the_captured_ones()
        {
            // Frames 103-105: mfe and mfp empty, mff {40}.
            Assert.Equal(Array.Empty<byte>(), ConnectionProtocol.ReadPayload(
                Bytes("12220a150a13747970652e616e6b616d612e636f6d2f6d666510ffffffffffffffffff01"), Op.Mfe));
            byte[]? mff = ConnectionProtocol.ReadPayload(
                Bytes("12260a190a13747970652e616e6b616d612e636f6d2f6d66661202082810ffffffffffffffffff01"), Op.Mff);
            Assert.NotNull(mff);
            Assert.Equal(40, AchievementProtocol.ReadCategory(mff!));
        }

        [Fact]
        public void The_answer_to_the_second_request_is_the_captured_mfx()
        {
            // Frame 107: an empty mfx on root 3, echoing the request's -1.
            byte[] request = Bytes("12220a150a13747970652e616e6b616d612e636f6d2f6d667010ffffffffffffffffff01");
            Assert.Equal("1a220a150a13747970652e616e6b616d612e636f6d2f6d667810ffffffffffffffffff01",
                Hex(ConnectionProtocol.Answer(Op.Mfx, null, ConnectionProtocol.RequestId(request))));
        }

        /// <summary>Six achievements, as frame 106 lists them.</summary>
        private static List<(int, IReadOnlyList<ObjectiveProgress>)> AlmostFinishedOfTheCapture()
        {
            static ObjectiveProgress Open(int id, long current, long max) => new ObjectiveProgress(id, current, max);
            static ObjectiveProgress Done(int id) => new ObjectiveProgress(id, 1, 1);

            return new List<(int, IReadOnlyList<ObjectiveProgress>)>
            {
                (8900, new[] { Open(17869, 91, 100) }),
                (602, new[] { Open(1539, 0, 1), Done(1538), Done(1540), Done(1541), Done(1542), Done(1543) }),
                (8877, new[] { Open(17825, 892, 1000) }),
                (1045, new[] { Open(3185, 0, 1), Done(3186), Done(3187), Done(3188), Done(3189), Done(3190) }),
                (227, new[] { Open(834, 0, 1), Done(832), Done(833) }),
                (233, new[] { Open(5065, 0, 1), Open(865, 0, 1), Done(867), Done(863), Done(866), Done(864),
                              Done(861), Done(5059), Done(18250), Done(5071), Done(5333) }),
            };
        }

        [Fact]
        public void The_almost_finished_list_is_the_captured_mgb()
        {
            // Frame 106, all 245 bytes. Note the "20 00": an objective not started carries its
            // count even at zero, and one that is done carries none.
            const string captured =
                "0a0d10c4451a0808cd8b011064205b0a2f10da041a0708830c100120001a0508820c10011a0508840c10011a0508850c10011a0508860c10011a0508870c10010a0f10ad451a0a08a18b0110e80720fc060a2f1095081a0708f118100120001a0508f21810011a0508f31810011a0508f41810011a0508f51810011a0508f61810010a1a10e3011a0708c206100120001a0508c00610011a0508c10610010a5510e9011a0708c927100120001a0708e106100120001a0508e30610011a0508df0610011a0508e20610011a0508e00610011a0508dd0610011a0508c32710011a0608ca8e0110011a0508cf2710011a0508d5291001";

            Assert.Equal(captured, Hex(AchievementProtocol.BuildAlmostFinished(AlmostFinishedOfTheCapture())));
        }

        [Fact]
        public void A_category_is_the_captured_mfo_record_for_record()
        {
            // Frame 108 opens with 5222 (three objectives not started), 556 (one) and 559.
            var objectives = new List<(int, IReadOnlyList<ObjectiveProgress>)>
            {
                (5222, new[] { new ObjectiveProgress(11278, 0, 1), new ObjectiveProgress(11279, 0, 1),
                               new ObjectiveProgress(11280, 0, 1) }),
                (556, new[] { new ObjectiveProgress(1375, 0, 1) }),
            };

            Assert.Equal("121e10e6281a07088e58100120001a07088f58100120001a0708905810012000120c10ac041a0708df0a10012000",
                Hex(AchievementProtocol.BuildDetailedList(objectives)));
        }
    }
}

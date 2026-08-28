using System;
using System.IO;
using Google.Protobuf;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// The one varint writer, checked against Google's.
    /// </summary>
    /// <remarks>
    /// There were five hand-written copies of this in the server project -- in NetworkEnvelope, Pb,
    /// ProtoMessage, CaptureRewriter and MapLoadHandler -- in three spellings: a
    /// <c>while (true)</c> with the break at the bottom, a <c>while (value >= 0x80)</c> with the
    /// last byte after the loop, and one that masked the final byte with 0x7F for no reason (a
    /// no-op there, since the loop only exits below 0x80). All five agreed. That is the point: five
    /// copies of something correct is four chances for the next edit to land on the wrong one, and
    /// a protocol writer that is subtly wrong produces frames the client silently drops.
    ///
    /// They now all go through <see cref="NetworkEnvelope.WriteVarInt"/>, and this pins that one
    /// against <c>CodedOutputStream</c> -- the encoder Google ships, already referenced by this
    /// project. Comparing our writer with our reader would only show that the two agree with each
    /// other, which is exactly the thing five copies were already good at.
    /// </remarks>
    public class VarIntWriterTests
    {
        private static byte[] Ours(ulong value)
        {
            using var stream = new MemoryStream();
            NetworkEnvelope.WriteVarInt(stream, value);
            return stream.ToArray();
        }

        private static byte[] Google_(ulong value)
        {
            using var stream = new MemoryStream();
            var coded = new CodedOutputStream(stream);
            coded.WriteUInt64(value);
            coded.Flush();
            return stream.ToArray();
        }

        [Theory]
        [InlineData(0UL)]
        [InlineData(1UL)]
        [InlineData(127UL)]                    // the last one-byte value
        [InlineData(128UL)]                    // the first two-byte one
        [InlineData(300UL)]
        [InlineData(2427UL)]                   // the quest id from the captured iom
        [InlineData(16383UL)]
        [InlineData(16384UL)]
        [InlineData(219417090UL)]              // the map id from the same frame
        [InlineData(uint.MaxValue)]
        [InlineData(long.MaxValue)]
        [InlineData(ulong.MaxValue)]           // ten bytes
        [InlineData(18446744073709531616UL)]   // -20000 as an int64: the NPC actor id
        public void Ours_encodes_exactly_as_google_does(ulong value)
        {
            Assert.Equal(Google_(value), Ours(value));
        }

        [Fact]
        public void Every_boundary_between_byte_widths_agrees()
        {
            // The ten places the length changes, plus the value on each side. Every off-by-one a
            // varint writer can have lives at one of these.
            for (int shift = 7; shift < 64; shift += 7)
            {
                ulong edge = 1UL << shift;
                Assert.Equal(Google_(edge - 1), Ours(edge - 1));
                Assert.Equal(Google_(edge), Ours(edge));
                Assert.Equal(Google_(edge + 1), Ours(edge + 1));
            }
        }

        [Fact]
        public void A_negative_actor_id_takes_the_ten_bytes_the_capture_shows()
        {
            // e0e3feffffffffffff01, straight out of the iom frame. NPC ids are negative, and an
            // int64 cast to ulong is always ten bytes -- which is why a writer that stopped early,
            // or a caller that used a 32-bit overload, would produce a frame the client cannot read.
            Assert.Equal("e0e3feffffffffffff01",
                         Convert.ToHexString(Ours(unchecked((ulong)-20000L))).ToLowerInvariant());
        }
    }
}

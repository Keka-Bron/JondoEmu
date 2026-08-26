using System;
using Jondo.Unity.Protocol.Wire;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// The reader everything else in the editor stands on.
    /// </summary>
    /// <remarks>
    /// It is fed bytes that came off a socket, so it is fed rubbish on purpose sooner or later, and
    /// the one thing it may never do is throw: it runs inside a list box redrawing sixty times a
    /// second and inside the server's diagnostic path, and an exception in either is a crash caused
    /// by looking at something.
    /// </remarks>
    public class WireMessageTests
    {
        private static byte[] Bytes(params int[] values)
        {
            var bytes = new byte[values.Length];
            for (int i = 0; i < values.Length; i++) bytes[i] = (byte)values[i];
            return bytes;
        }

        [Fact]
        public void A_varint_field_reads_back()
        {
            // field 1, wire type 0, value 300
            var read = WireMessage.Read(Bytes(0x08, 0xAC, 0x02));

            Assert.True(read.Complete);
            var field = Assert.Single(read.Fields);
            Assert.Equal(1, field.Number);
            Assert.Equal(0, field.Type);
            Assert.Equal(300ul, field.Value);
        }

        [Fact]
        public void A_length_delimited_field_keeps_its_bytes()
        {
            // field 2, wire type 2, "hi"
            var read = WireMessage.Read(Bytes(0x12, 0x02, (byte)'h', (byte)'i'));

            var field = Assert.Single(read.Fields);
            Assert.Equal(2, field.Number);
            Assert.Equal(new byte[] { (byte)'h', (byte)'i' }, field.Bytes);
        }

        // ─── What must not happen ─────────────────────────────────────────────────

        [Fact]
        public void Nothing_throws_on_rubbish()
        {
            var random = new Random(20260826);
            for (int i = 0; i < 500; i++)
            {
                var bytes = new byte[random.Next(0, 64)];
                random.NextBytes(bytes);

                // The assertion is that these two return at all.
                var read = WireMessage.Read(bytes);
                ProtoShape.Of(bytes);
                Assert.True(read.BytesRead <= bytes.Length);
            }
        }

        [Fact]
        public void Null_and_empty_are_fine()
        {
            Assert.Empty(WireMessage.Read(null).Fields);
            Assert.Empty(WireMessage.Read(Array.Empty<byte>()).Fields);
        }

        /// <summary>
        /// A field that says it is longer than what is left is the shape a truncated frame takes,
        /// and it is also the shape a hostile one takes. Reading it as if the length were real is
        /// how a five-byte header ends up asking for a two-gigabyte array.
        /// </summary>
        [Fact]
        public void A_length_past_the_end_stops_the_read_rather_than_being_believed()
        {
            // field 1, wire type 2, says 100 bytes, has 2
            var read = WireMessage.Read(Bytes(0x0A, 0x64, 0x01, 0x02));

            Assert.False(read.Complete);
            Assert.Empty(read.Fields);
        }

        [Fact]
        public void A_varint_that_runs_off_the_end_stops_the_read()
        {
            // every byte has the continuation bit set and then the message ends
            var read = WireMessage.Read(Bytes(0x08, 0x80, 0x80, 0x80));

            Assert.False(read.Complete);
            Assert.Empty(read.Fields);
        }

        /// <summary>
        /// A run of zero bytes is what padding looks like, and field 0 does not exist in protobuf.
        /// Without the check it reads as an endless list of perfectly valid empty fields.
        /// </summary>
        [Fact]
        public void Field_zero_is_not_a_field()
        {
            var read = WireMessage.Read(Bytes(0x00, 0x00, 0x00, 0x00));

            Assert.False(read.Complete);
            Assert.Empty(read.Fields);
        }

        [Fact]
        public void Wire_types_three_four_six_and_seven_end_the_read()
        {
            foreach (int type in new[] { 3, 4, 6, 7 })
            {
                var read = WireMessage.Read(Bytes(0x08, 0x01, (byte)((1 << 3) | type)));

                Assert.False(read.Complete);
                Assert.Single(read.Fields);
            }
        }

        [Fact]
        public void What_was_read_and_what_was_left_add_up()
        {
            var read = WireMessage.Read(Bytes(0x08, 0x01, 0x0A, 0x64, 0x02));

            Assert.Equal(2, read.BytesRead);
            Assert.Equal(3, read.TrailingBytes);
            Assert.Equal(5, read.TotalBytes);
        }

        // ─── Telling a message from data that looks like one ──────────────────────

        /// <summary>
        /// This is the check that made the unknown-packet list usable. The walking packet carries
        /// its path as a block of bytes, and that block parses as a structure with fields 1024,
        /// 1025, 1566, 1600 — different on every step the player takes. Before the ceiling on field
        /// numbers, one message produced 307 distinct "shapes" out of 1,798 captured.
        ///
        /// The ceiling is measured, not chosen: across the whole 3.6.10.10 protocol there are 6,186
        /// declared message fields and the highest number is 40.
        /// </summary>
        [Fact]
        public void A_block_of_data_that_parses_by_accident_is_not_a_message()
        {
            byte[] block = Bytes(0x82, 0x40, 0x02, 0x11, 0x22, 0xC2, 0x60, 0x01, 0x33);

            // It parses cleanly, which is exactly the trap.
            Assert.True(WireMessage.Read(block).Complete);

            // And it is still not a message, because of the field numbers it would need.
            Assert.False(WireMessage.LooksLikeMessage(block));
        }

        [Fact]
        public void A_real_nested_message_is_recognised()
        {
            Assert.True(WireMessage.LooksLikeMessage(Bytes(0x08, 0x01, 0x10, 0x02)));
        }

        [Fact]
        public void A_message_that_does_not_read_whole_is_not_one()
        {
            Assert.False(WireMessage.LooksLikeMessage(Bytes(0x0A, 0x64, 0x01)));
        }
    }
}

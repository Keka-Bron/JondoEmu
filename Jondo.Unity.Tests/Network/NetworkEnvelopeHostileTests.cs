using System;
using System.IO;
using System.Linq;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Network
{
    /// <summary>
    /// What the frame parser does with bytes a hostile client sends on purpose.
    /// </summary>
    /// <remarks>
    /// <c>NetworkEnvelope</c> is 590 lines and no test named it. It is also the first thing every
    /// byte from every client goes through, so a lie in a length field reaches it before any of the
    /// handlers, before authentication, before anything.
    ///
    /// The failures this is looking for are not exceptions — an exception is caught and logged. They
    /// are the two that have no symptom until the machine is on fire: a cursor that stops advancing,
    /// which is a socket loop at 100% of a core for as long as the connection is held open, and a
    /// cursor that goes backwards or past the end, which turns a read into whatever memory follows.
    /// <c>SkipField</c>'s own comment says both have happened.
    /// </remarks>
    public class NetworkEnvelopeHostileTests
    {
        private static byte[] Varint(ulong value)
        {
            using var stream = new MemoryStream();
            NetworkEnvelope.WriteVarInt(stream, value);
            return stream.ToArray();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(5)]
        [InlineData(3)]   // start-group, which this protocol never uses
        [InlineData(4)]   // end-group
        [InlineData(7)]   // not a wire type at all
        public void Skipping_a_field_always_leaves_the_cursor_inside_the_buffer(int wireType)
        {
            // Every wire type, over a buffer that ends in the middle of whatever it claims to be.
            // The rule that matters is the same for all of them: land somewhere between where we
            // started and the end, never before, never after.
            byte[] frame = { 0xFF, 0xFF, 0xFF, 0x7F, 0x01, 0x02 };

            for (int start = 0; start < frame.Length; start++)
            {
                int pos = start;
                NetworkEnvelope.SkipField(frame, wireType, ref pos);

                Assert.True(pos >= start, $"tipo {wireType} desde {start}: el cursor retrocedió a {pos}");
                Assert.True(pos <= frame.Length, $"tipo {wireType} desde {start}: el cursor se fue a {pos}");
            }
        }

        [Fact]
        public void A_length_that_lies_about_the_rest_of_the_frame_ends_the_scan()
        {
            // Wire type 2 with a length of 2 billion over a six-byte buffer. Adding it to the
            // cursor without checking is how `pos` used to come out NEGATIVE, and every caller
            // walks with `while (pos < bytes.Length)`: the loop then never ends.
            byte[] frame = new byte[] { 0x02 }.Concat(Varint(2_000_000_000)).Concat(new byte[] { 0x41, 0x42 }).ToArray();

            int pos = 1;
            NetworkEnvelope.SkipField(frame, 2, ref pos);

            Assert.Equal(frame.Length, pos);
        }

        [Fact]
        public void An_unknown_wire_type_ends_the_scan_instead_of_throwing()
        {
            // Wire types 3, 4, 6 and 7 do not appear in this protocol. Throwing here would take the
            // exception out through the routing loop for a packet nobody has to understand; the
            // documented behaviour is to give up on the rest of the message.
            byte[] frame = { 0x01, 0x02, 0x03, 0x04 };

            int pos = 0;
            NetworkEnvelope.SkipField(frame, 6, ref pos);

            Assert.Equal(frame.Length, pos);
        }

        [Fact]
        public void A_varint_that_never_ends_stops_at_the_end_of_the_buffer()
        {
            // Every byte with its continuation bit set. The read must stop when the bytes do,
            // rather than walk past them, and it must not spin: this is thirty-two bytes of 0xFF,
            // which is what an attacker sends when the goal is a busy loop rather than a crash.
            byte[] frame = Enumerable.Repeat((byte)0xFF, 32).ToArray();

            int pos = 0;
            Assert.Throws<InvalidDataException>(() => NetworkEnvelope.ReadVarInt(frame, ref pos));
            Assert.Equal(frame.Length, pos);
        }

        [Fact]
        public void A_truncated_varint_throws_rather_than_returning_a_number()
        {
            // The last byte still asks for another one that is not there. Returning the partial
            // value would be worse than throwing: the caller would use it as a length.
            byte[] frame = { 0x80, 0x80 };

            int pos = 0;
            Assert.Throws<InvalidDataException>(() => NetworkEnvelope.ReadVarInt(frame, ref pos));
        }

        [Fact]
        public void A_frame_claiming_more_than_it_carries_unpacks_to_nothing()
        {
            // The length prefix says 200 bytes and three follow. Allocating what it asks for is
            // the shape of an out-of-memory sent from outside; returning null is the answer.
            byte[] frame = new byte[] { 200 }.Concat(new byte[] { 1, 2, 3 }).ToArray();

            Assert.Null(NetworkEnvelope.UnpackLengthPrefixed(frame));
        }

        [Fact]
        public void An_empty_frame_unpacks_to_nothing_and_does_not_throw()
        {
            Assert.Null(NetworkEnvelope.UnpackLengthPrefixed(Array.Empty<byte>()));
        }

        [Theory]
        [InlineData(new byte[0])]
        [InlineData(new byte[] { 0x0A })]                      // a tag with nothing behind it
        [InlineData(new byte[] { 0x0A, 0xFF, 0xFF, 0xFF })]    // a length that runs off the end
        [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })]    // not a message at all
        [InlineData(new byte[] { 0x0A, 0x02, 0x0A, 0x7F })]    // nested, and the inside lies too
        public void Reading_a_broken_frame_gives_nothing_back_and_never_throws(byte[] frame)
        {
            // These four go through the same door as every real packet. Whatever they are, the
            // answer is "no payload" — not an exception climbing out of the routing loop.
            Exception? thrownPayload = Record.Exception(
                () => NetworkEnvelope.ExtractMessagePayload(frame, "type.ankama.com/kqy"));
            Assert.Null(thrownPayload);

            Exception? thrownUrl = Record.Exception(() => NetworkEnvelope.GetMessageTypeUrl(frame));
            Assert.Null(thrownUrl);

            Exception? thrownRoot = Record.Exception(() => NetworkEnvelope.ExtractGameNodePayload(frame));
            Assert.Null(thrownRoot);
        }

        [Fact]
        public void A_well_formed_packet_still_round_trips()
        {
            // The other half of the deal: hardening the parser is only worth anything if it still
            // reads what the server itself writes.
            //
            // And it takes the RIGHT reader, which is the thing worth pinning here. The root field
            // number says who is talking — the client's frames arrive on field 1 and the server's
            // replies go out on field 3 — so BuildGameNodePacket and ExtractMessagePayload are not
            // inverses of each other, they are opposite directions. Asking the wrong one gives a
            // silent null, which is how somebody spends an afternoon.
            byte[] payload = { 0x08, 0x2A };
            byte[] packet = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/kqy", payload);

            Assert.Equal("type.ankama.com/kqy", NetworkEnvelope.GetMessageTypeUrl(packet));
            Assert.Equal(payload, NetworkEnvelope.ExtractGameNodePayload(packet));

            // The client-side reader on a server-side packet: null, not an exception.
            Assert.Null(NetworkEnvelope.ExtractMessagePayload(packet, "type.ankama.com/kqy"));
        }

        [Fact]
        public void An_inner_length_bigger_than_the_frame_allocates_nothing()
        {
            // The one the earlier tests missed. The 8 MB cap in NetworkMessage.MaxFrameLength is on
            // the OUTER frame; the lengths nested inside it were read from the client and handed
            // straight to `new byte[len]` with nothing comparing them to what was left of the
            // buffer. Their sibling loops in ExtractGameNodePayload always had that check, which is
            // what made the gap easy to miss: the file looked like it was already careful.
            //
            // Reached before any authentication: ReadFrameAsync calls GetMessageTypeUrl on the raw
            // payload before the session handler ever sees it.
            //
            // f1, length = 4 billion, and eleven bytes actually present.
            byte[] frame = new byte[] { 0x0A }
                .Concat(Varint(4_000_000_000))
                .Concat(new byte[] { 0x0A, 0x02, 0x08, 0x01 })
                .ToArray();

            Assert.Null(NetworkEnvelope.GetMessageTypeUrl(frame));
            Assert.Null(NetworkEnvelope.ExtractGameNodePayload(frame));
            Assert.Null(NetworkEnvelope.ExtractMessagePayload(frame, "type.ankama.com/kqy"));
        }

        [Theory]
        [InlineData(3)]   // f3, the root the server replies on
        [InlineData(1)]   // f1, the root the client sends on
        public void A_lying_length_at_any_nesting_level_is_refused(int rootField)
        {
            // Same lie, one level deeper: the outer wrapper is honest and the inner one is not.
            // Worth both roots, because GetMessageTypeUrl walks a different branch for each and the
            // check had to be added to both.
            byte[] inner = new byte[] { 0x0A }.Concat(Varint(3_000_000_000)).ToArray();
            byte[] frame = new byte[] { (byte)((rootField << 3) | 2), (byte)inner.Length }
                .Concat(inner).ToArray();

            Exception? thrown = Record.Exception(() => NetworkEnvelope.GetMessageTypeUrl(frame));
            Assert.Null(thrown);
            Assert.Null(NetworkEnvelope.GetMessageTypeUrl(frame));
        }

    }
}

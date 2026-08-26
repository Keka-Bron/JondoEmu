using System;
using System.Collections.Generic;
using System.Text;
using Jondo.Unity.Protocol.Wire;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// Opening a frame: which way it went, which message it is, where its bytes start.
    /// </summary>
    /// <remarks>
    /// These exist because this was wrong for months in a way nothing noticed. The unknown-packet
    /// registry opened frames with a helper that only looks at root field 3, while client frames
    /// sit at root field 2, so every packet it was asked to write down went in with no opcode and
    /// an empty body: after weeks of play the table held two rows, both of them useless. The
    /// dispatcher never noticed because it finds opcodes by searching the frame as text, which
    /// works whatever the envelope looks like.
    ///
    /// So the golden case here is a real frame, byte for byte out of the traffic log, and the rest
    /// cover the layouts measured across all 72,879 of them.
    /// </remarks>
    public class EnvelopeTests
    {
        // ─── Building frames ──────────────────────────────────────────────────────

        private static byte[] VarInt(ulong value)
        {
            var bytes = new List<byte>();
            do
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0) b |= 0x80;
                bytes.Add(b);
            }
            while (value != 0);

            return bytes.ToArray();
        }

        private static byte[] Field(int number, byte[] body)
        {
            var bytes = new List<byte>();
            bytes.AddRange(VarInt((ulong)((number << 3) | 2)));
            bytes.AddRange(VarInt((ulong)body.Length));
            bytes.AddRange(body);
            return bytes.ToArray();
        }

        private static byte[] Any(string opcode, byte[] payload)
        {
            var bytes = new List<byte>();
            bytes.AddRange(Field(1, Encoding.ASCII.GetBytes(Envelope.TypePrefix + opcode)));
            if (payload.Length > 0) bytes.AddRange(Field(2, payload));
            return bytes.ToArray();
        }

        private static byte[] Frame(int rootField, string opcode, byte[] payload)
            => Field(rootField, Field(1, Any(opcode, payload)));

        private static readonly byte[] Body = { 0x08, 0x2A };   // field 1 = 42

        // ─── The three layouts, measured ──────────────────────────────────────────

        [Fact]
        public void Root_field_two_is_the_client_asking()
        {
            var frame = Envelope.Read(Frame(2, "kqz", Body));

            Assert.True(frame.Found);
            Assert.Equal("kqz", frame.Opcode);
            Assert.Equal(2, frame.RootField);
            Assert.Equal(FrameDirection.ClientRequest, frame.Direction);
            Assert.Equal(Body, frame.Payload);
        }

        [Fact]
        public void Root_field_one_is_the_server_saying_something()
        {
            var frame = Envelope.Read(Frame(1, "idu", Body));

            Assert.Equal(FrameDirection.ServerPush, frame.Direction);
            Assert.Equal("idu", frame.Opcode);
        }

        [Fact]
        public void Root_field_three_is_the_server_answering()
        {
            var frame = Envelope.Read(Frame(3, "jto", Body));

            Assert.Equal(FrameDirection.ServerReply, frame.Direction);
            Assert.Equal("jto", frame.Opcode);
        }

        /// <summary>
        /// 4,646 of the log's rows are an Any without its outer envelope, or one layer short of it.
        /// A reader that only knew the three full layouts would drop every one of them.
        /// </summary>
        [Fact]
        public void An_any_on_its_own_is_still_opened()
        {
            var frame = Envelope.Read(Field(1, Any("jwe", Body)));

            Assert.True(frame.Found);
            Assert.Equal("jwe", frame.Opcode);
            Assert.Equal(Body, frame.Payload);
        }

        [Fact]
        public void A_message_with_no_body_is_normal()
        {
            var frame = Envelope.Read(Frame(2, "kra", Array.Empty<byte>()));

            Assert.True(frame.Found);
            Assert.Equal("kra", frame.Opcode);
            Assert.Empty(frame.Payload);
            Assert.Equal(ProtoShape.Empty, ProtoShape.Of(frame.Payload));
        }

        // ─── The golden case ──────────────────────────────────────────────────────

        /// <summary>
        /// A real client frame, byte for byte out of the traffic log. It is <c>kqz</c>, the message
        /// where the client states its language, and it is the one that settled that the two-letter
        /// code travels in field 3 — which is also a small demonstration of why the declared types
        /// matter: those two bytes parse perfectly well as a submessage with a field 12 in it.
        /// </summary>
        [Fact]
        public void A_real_captured_frame_comes_apart_correctly()
        {
            byte[] captured = Convert.FromHexString(
                "124a0a3d0a13747970652e616e6b616d612e636f6d2f6b717a12261220626265" +
                "3066393462303561643132656238386432653835343737363863326166" +
                "1a026573" +
                "10ffffffffffffffffff01");

            var frame = Envelope.Read(captured);

            Assert.True(frame.Found);
            Assert.Equal("kqz", frame.Opcode);
            Assert.Equal(FrameDirection.ClientRequest, frame.Direction);
            Assert.Equal("2:s,3:s", ProtoShape.Of(frame.Payload));

            var body = WireMessage.Read(frame.Payload);
            Assert.Equal(2, body.Fields.Count);
            Assert.Equal("bbe0f94b05ad12eb88d2e8547768c2af",
                         Encoding.ASCII.GetString(body.Fields[0].Bytes));
            Assert.Equal("es", Encoding.ASCII.GetString(body.Fields[1].Bytes));
        }

        // ─── The length prefix ────────────────────────────────────────────────────

        /// <summary>
        /// The traffic log is written from two places that do not agree about the prefix: 27,565 of
        /// the 72,879 rows carry one and the rest do not. Reading only one of the two throws away a
        /// third of the log.
        /// </summary>
        [Fact]
        public void A_logged_frame_reads_with_or_without_its_length_prefix()
        {
            byte[] bare = Frame(1, "jxw", Body);

            var prefixed = new List<byte>();
            prefixed.AddRange(VarInt((ulong)bare.Length));
            prefixed.AddRange(bare);

            var withoutIt = Envelope.ReadLogged(bare);
            var withIt = Envelope.ReadLogged(prefixed.ToArray());

            Assert.Equal("jxw", withoutIt.Opcode);
            Assert.False(withoutIt.HadLengthPrefix);

            Assert.Equal("jxw", withIt.Opcode);
            Assert.True(withIt.HadLengthPrefix);
            Assert.Equal(withoutIt.Payload, withIt.Payload);
        }

        /// <summary>
        /// The prefix is only stripped when reading straight through failed AND the varint is
        /// exactly the length of what follows. Without that second condition a good frame whose
        /// first byte happens to match its own length loses that byte.
        /// </summary>
        [Fact]
        public void A_frame_that_reads_straight_through_keeps_its_first_byte()
        {
            byte[] bare = Frame(2, "kqo", Body);
            var frame = Envelope.ReadLogged(bare);

            Assert.False(frame.HadLengthPrefix);
            Assert.Equal("kqo", frame.Opcode);
        }

        // ─── Not finding anything ─────────────────────────────────────────────────

        [Fact]
        public void Bytes_with_no_message_in_them_say_so_rather_than_guessing()
        {
            Assert.False(Envelope.Read(new byte[] { 0x04, 0x05, 0x06 }).Found);
            Assert.False(Envelope.Read(Array.Empty<byte>()).Found);
            Assert.False(Envelope.Read(null).Found);
        }

        [Fact]
        public void A_type_url_for_something_else_is_not_ours()
        {
            byte[] frame = Field(2, Field(1, Field(1, Encoding.ASCII.GetBytes("type.googleapis.com/x"))));

            Assert.False(Envelope.Read(frame).Found);
        }

        [Fact]
        public void Nothing_throws_on_rubbish()
        {
            var random = new Random(11);
            for (int i = 0; i < 500; i++)
            {
                var bytes = new byte[random.Next(0, 96)];
                random.NextBytes(bytes);

                Envelope.Read(bytes);
                Envelope.ReadLogged(bytes);
            }
        }
    }
}

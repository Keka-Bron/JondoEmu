using System;
using System.Collections.Generic;
using System.Text;
using Jondo.Unity.Protocol.Wire;
using Jondo.Unity.Studio.Data;
using Xunit;

namespace Jondo.Unity.Tests.Studio
{
    /// <summary>
    /// Reading a frame against the protocol the client declares.
    /// </summary>
    /// <remarks>
    /// This is where the editor stops guessing and starts reading, and it is also where a wrong
    /// answer is hardest to catch: a zigzagged integer read as a plain one is still a number, and a
    /// packed array read as a blob is still a length. Looking at the window tells you nothing.
    /// </remarks>
    public class FrameDecoderTests
    {
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

        private static byte[] Var(int number, ulong value)
        {
            var bytes = new List<byte>();
            bytes.AddRange(VarInt((ulong)(number << 3)));
            bytes.AddRange(VarInt(value));
            return bytes.ToArray();
        }

        private static byte[] Bytes(int number, byte[] body)
        {
            var bytes = new List<byte>();
            bytes.AddRange(VarInt((ulong)((number << 3) | 2)));
            bytes.AddRange(VarInt((ulong)body.Length));
            bytes.AddRange(body);
            return bytes.ToArray();
        }

        private static byte[] Join(params byte[][] parts)
        {
            var all = new List<byte>();
            foreach (var part in parts) all.AddRange(part);
            return all.ToArray();
        }

        private static ProtoSchema Schema(params string[] lines) => ProtoSchema.Parse(lines, "test");

        // ─── With the protocol in hand ────────────────────────────────────────────

        [Fact]
        public void A_declared_field_gets_its_name_and_type()
        {
            var schema = Schema("message kqz {", "  int32 fytj = 1;", "  string fytl = 3;", "}");
            var lines = FrameDecoder.Decode(
                Join(Var(1, 7), Bytes(3, Encoding.UTF8.GetBytes("es"))), "kqz", schema);

            Assert.Equal(2, lines.Count);
            Assert.Equal("fytj", lines[0].Name);
            Assert.Equal("int32", lines[0].Type);
            Assert.Equal("7", lines[0].Value);
            Assert.False(lines[0].Guessed);

            Assert.Equal("fytl", lines[1].Name);
            Assert.Equal("\"es\"", lines[1].Value);
        }

        /// <summary>
        /// The case that makes the declared types worth loading a 17,000-line file for. Those two
        /// bytes parse perfectly well as a submessage with a field 12 in it, and the guess is what
        /// the shape algorithm has to make. With <c>string</c> in front of you there is nothing to
        /// guess.
        /// </summary>
        [Fact]
        public void A_two_letter_string_is_not_read_as_a_submessage()
        {
            byte[] payload = Bytes(3, Encoding.UTF8.GetBytes("es"));

            var guessed = FrameDecoder.Decode(payload, "kqz", ProtoSchema.Empty);
            var declared = FrameDecoder.Decode(payload, "kqz",
                                               Schema("message kqz {", "  string fytl = 3;", "}"));

            Assert.True(guessed[0].Guessed);
            Assert.Equal("\"es\"", declared[0].Value);
            Assert.Equal("string", declared[0].Type);
        }

        [Fact]
        public void A_nested_message_is_walked_into_with_its_own_declaration()
        {
            var schema = Schema(
                "message outer {", "  inner thing = 2;", "}",
                "message inner {", "  int64 count = 1;", "}");

            var lines = FrameDecoder.Decode(Bytes(2, Var(1, 42)), "outer", schema);

            Assert.Equal(2, lines.Count);
            Assert.Equal("thing", lines[0].Name);
            Assert.Equal(0, lines[0].Depth);
            Assert.Equal("count", lines[1].Name);
            Assert.Equal(1, lines[1].Depth);
            Assert.Equal("42", lines[1].Value);
        }

        /// <summary>
        /// 210 fields in the protocol are <c>repeated int32</c> and 84 more are
        /// <c>repeated int64</c>, and they carry the things most often being chased: cell lists,
        /// spell ids, effect ids. Shown as a byte count they are worthless.
        /// </summary>
        [Fact]
        public void A_packed_repeated_number_is_unpacked()
        {
            var schema = Schema("message a {", "  repeated int32 cells = 1;", "}");
            var lines = FrameDecoder.Decode(
                Bytes(1, Join(VarInt(260), VarInt(261), VarInt(275))), "a", schema);

            var line = Assert.Single(lines);
            Assert.Contains("260", line.Value);
            Assert.Contains("261", line.Value);
            Assert.Contains("275", line.Value);
        }

        [Fact]
        public void A_negative_int32_reads_as_negative()
        {
            var schema = Schema("message a {", "  int32 x = 1;", "}");
            var lines = FrameDecoder.Decode(Var(1, unchecked((ulong)(long)-1)), "a", schema);

            Assert.Equal("-1", Assert.Single(lines).Value);
        }

        [Fact]
        public void A_zigzagged_number_is_unzigzagged()
        {
            var schema = Schema("message a {", "  sint32 x = 1;", "}");

            // zigzag: -1 travels as 1, 1 travels as 2
            Assert.Equal("-1", FrameDecoder.Decode(Var(1, 1), "a", schema)[0].Value);
            Assert.Equal("1", FrameDecoder.Decode(Var(1, 2), "a", schema)[0].Value);
        }

        [Fact]
        public void A_bool_reads_as_a_bool()
        {
            var schema = Schema("message a {", "  bool x = 1;", "  bool y = 2;", "}");
            var lines = FrameDecoder.Decode(Join(Var(1, 1), Var(2, 0)), "a", schema);

            Assert.Equal("true", lines[0].Value);
            Assert.Equal("false", lines[1].Value);
        }

        [Fact]
        public void A_map_entry_reads_as_a_pair()
        {
            var schema = Schema("message a {", "  map<int32, int32> m = 5;", "}");
            var lines = FrameDecoder.Decode(Bytes(5, Join(Var(1, 7), Var(2, 9))), "a", schema);

            Assert.Contains("7", Assert.Single(lines).Value);
            Assert.Contains("9", lines[0].Value);
        }

        // ─── Without it ───────────────────────────────────────────────────────────

        /// <summary>
        /// A guess has to look like a guess. It is the same distinction the provenance column makes
        /// everywhere else: measured and invented must never end up looking alike.
        /// </summary>
        [Fact]
        public void An_undeclared_field_is_marked_as_a_guess()
        {
            var lines = FrameDecoder.Decode(Var(1, 5), "nobody", ProtoSchema.Empty);

            Assert.True(Assert.Single(lines).Guessed);
        }

        [Fact]
        public void A_field_the_message_does_not_declare_is_still_shown()
        {
            var schema = Schema("message a {", "  int32 x = 1;", "}");
            var lines = FrameDecoder.Decode(Join(Var(1, 5), Var(9, 6)), "a", schema);

            Assert.Equal(2, lines.Count);
            Assert.False(lines[0].Guessed);
            Assert.True(lines[1].Guessed);
            Assert.Equal(9, lines[1].Number);
        }

        [Fact]
        public void Leftover_bytes_are_said_out_loud()
        {
            var lines = FrameDecoder.Decode(Join(Var(1, 1), new byte[] { 0x0A, 0x64, 0x02 }),
                                            "a", ProtoSchema.Empty);

            Assert.Contains(lines, line => line.Type == "unread");
        }

        // ─── Not falling over ─────────────────────────────────────────────────────

        [Fact]
        public void Nothing_throws_on_rubbish()
        {
            var random = new Random(3);
            for (int i = 0; i < 400; i++)
            {
                var bytes = new byte[random.Next(0, 80)];
                random.NextBytes(bytes);
                FrameDecoder.Decode(bytes, "whatever", ProtoSchema.Empty);
            }
        }

        [Fact]
        public void An_empty_payload_decodes_to_nothing()
        {
            Assert.Empty(FrameDecoder.Decode(Array.Empty<byte>(), "a", ProtoSchema.Empty));
            Assert.Empty(FrameDecoder.Decode(null, "a", ProtoSchema.Empty));
        }

        /// <summary>
        /// A message declared as containing itself is a legal declaration and an infinite frame is
        /// not needed to hit it — a hostile payload can nest as deep as it likes.
        /// </summary>
        [Fact]
        public void Deep_nesting_stops_rather_than_running_out_of_stack()
        {
            var schema = Schema("message a {", "  a self = 1;", "}");
            byte[] payload = Var(2, 1);
            for (int i = 0; i < 60; i++) payload = Bytes(1, payload);

            var lines = FrameDecoder.Decode(payload, "a", schema);

            Assert.NotEmpty(lines);
            Assert.True(lines.Count < 60);
        }

        [Fact]
        public void Hex_says_how_much_it_left_out()
        {
            var bytes = new byte[100];
            string hex = FrameDecoder.Hex(bytes, 8);

            Assert.Contains("…", hex);
            Assert.Contains("(100 b)", hex);
        }
    }
}

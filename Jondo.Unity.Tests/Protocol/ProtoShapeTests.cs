using System;
using System.Collections.Generic;
using System.Text;
using Jondo.Unity.Protocol.Wire;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// The key the whole packet registry hangs off.
    /// </summary>
    /// <remarks>
    /// Two things have to hold at once, and they pull against each other. Two messages that are
    /// different have to give different shapes, or the list groups together things that have
    /// nothing to do with each other. And the same message with different numbers in it has to give
    /// the same shape, or every keystroke the player makes creates a new row and the list becomes a
    /// log.
    ///
    /// The same string is computed in two places now — the server writing <c>paquetes.db</c> and
    /// the editor reading it — which is exactly why the algorithm was moved into one shared file
    /// rather than copied.
    /// </remarks>
    public class ProtoShapeTests
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

        // ─── Telling things apart ─────────────────────────────────────────────────

        [Theory]
        [InlineData(1, "1:v")]
        [InlineData(3, "3:v")]
        public void A_single_number_is_its_field_and_a_v(int number, string expected)
            => Assert.Equal(expected, ProtoShape.Of(Var(number, 5)));

        [Fact]
        public void The_field_numbers_are_part_of_the_shape()
        {
            Assert.Equal("1:v,3:v", ProtoShape.Of(Join(Var(1, 5), Var(3, 9))));
            Assert.NotEqual(ProtoShape.Of(Join(Var(1, 5), Var(2, 9))),
                            ProtoShape.Of(Join(Var(1, 5), Var(3, 9))));
        }

        [Fact]
        public void A_submessage_is_looked_at_from_the_inside()
            => Assert.Equal("1:v,2:{3:v}",
                            ProtoShape.Of(Join(Var(1, 5), Bytes(2, Var(3, 7)))));

        [Fact]
        public void Two_layers_of_submessage_still_are()
            => Assert.Equal("1:{2:{4:v}}",
                            ProtoShape.Of(Bytes(1, Bytes(2, Var(4, 1)))));

        [Fact]
        public void A_string_is_not_a_structure()
            => Assert.Equal("1:s", ProtoShape.Of(Bytes(1, Encoding.UTF8.GetBytes("just some text"))));

        /// <summary>
        /// The case that motivates the whole thing: on a real server queue one opcode turned up
        /// with 32 different payloads. Measured here, 59 of the 242 opcodes in the traffic log
        /// carry more than one shape and <c>jss</c> alone has 185.
        /// </summary>
        [Fact]
        public void Two_payloads_of_one_opcode_do_not_collapse()
        {
            string one = ProtoShape.Of(Join(Var(1, 1), Bytes(2, Join(Var(1, 1), Bytes(3, new byte[] { (byte)'x' })))));
            string two = ProtoShape.Of(Join(Var(1, 1), Bytes(4, Var(2, 1))));

            Assert.NotEqual(one, two);
        }

        // ─── Grouping what belongs together ───────────────────────────────────────

        [Fact]
        public void The_same_shape_with_other_numbers_in_it_is_the_same_shape()
        {
            string once = ProtoShape.Of(Join(Var(1, 5), Bytes(2, Var(3, 7))));
            string again = ProtoShape.Of(Join(Var(1, 999_999), Bytes(2, Var(3, 1))));

            Assert.Equal(once, again);
        }

        /// <summary>
        /// The walking packet carries its path as a block of bytes, and that block parses as a
        /// structure with fields 1024, 1566, 1600 — different on every step. Without the ceiling on
        /// field numbers it produced 307 shapes of one message out of 1,798 captured, which is the
        /// unusable registry this was built to avoid.
        /// </summary>
        [Fact]
        public void A_block_of_data_does_not_become_a_structure()
        {
            byte[] block = { 0x82, 0x40, 0x02, 0x11, 0x22, 0xC2, 0x60, 0x01, 0x33 };
            string shape = ProtoShape.Of(Join(Var(1, 1), Bytes(2, block)));

            Assert.Equal("1:v,2:s", shape);
            Assert.DoesNotContain("{", shape);
        }

        // ─── Saying when it does not know ─────────────────────────────────────────

        [Fact]
        public void Nothing_at_all_is_empty_not_unreadable()
        {
            Assert.Equal(ProtoShape.Empty, ProtoShape.Of(Array.Empty<byte>()));
            Assert.Equal(ProtoShape.Empty, ProtoShape.Of(null));
        }

        [Fact]
        public void Bytes_that_are_not_a_message_say_so()
            => Assert.Equal(ProtoShape.Unreadable, ProtoShape.Of(new byte[] { 0xFF, 0xFF, 0xFF }));

        /// <summary>
        /// A message that stops making sense halfway is worth telling apart from one that read
        /// whole: "three fields" and "three fields and then something I could not read" are
        /// different findings and want different work.
        /// </summary>
        [Fact]
        public void Leftover_bytes_are_counted_in_the_shape()
        {
            string shape = ProtoShape.Of(Join(Var(1, 1), new byte[] { 0x0A, 0x64, 0x02 }));

            Assert.StartsWith("1:v", shape);
            Assert.Contains("+3b", shape);
        }

        [Fact]
        public void The_shape_is_stable_across_calls()
        {
            byte[] payload = Join(Var(1, 5), Bytes(2, Var(3, 7)), Bytes(4, Encoding.UTF8.GetBytes("x")));

            Assert.Equal(ProtoShape.Of(payload), ProtoShape.Of(payload));
        }

        /// <summary>
        /// A message nested inside itself for ever is a shape a hostile frame can have. The depth
        /// limit is what stops describing it rather than recursing to the bottom of the stack.
        /// </summary>
        [Fact]
        public void Deep_nesting_stops_rather_than_running_out_of_stack()
        {
            byte[] payload = Var(1, 1);
            for (int i = 0; i < 40; i++) payload = Bytes(1, payload);

            string shape = ProtoShape.Of(payload);

            Assert.Contains("…", shape);
        }

        // ─── The one-line summary, which is only for looking at ───────────────────

        [Fact]
        public void The_summary_shows_values_where_the_shape_shows_types()
        {
            string summary = ProtoShape.Summarise(Join(Var(1, 42), Bytes(2, Encoding.UTF8.GetBytes("es"))));

            Assert.Contains("1=42", summary);
            Assert.Contains("\"es\"", summary);
        }

        [Fact]
        public void The_summary_never_throws()
        {
            var random = new Random(7);
            for (int i = 0; i < 200; i++)
            {
                var bytes = new byte[random.Next(0, 40)];
                random.NextBytes(bytes);
                ProtoShape.Summarise(bytes);
            }
        }
    }
}

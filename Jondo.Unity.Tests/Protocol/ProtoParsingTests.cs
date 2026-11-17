using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// A malformed protobuf neither throws nor eats the memory.
    /// </summary>
    /// <remarks>
    /// What goes into ProtoMessage.Parse comes off the socket, so it can be wrong on purpose. A
    /// field claiming to be longer than what is left used to allocate the array anyway — up to 4 GB
    /// from five bytes — and an unused wire type threw an exception nobody catches, in the middle
    /// of routing.
    /// </remarks>
    public class ProtoParsingTests
    {
        [Fact]
        public void A_field_longer_than_the_message_is_dropped()
        {
            // f1, length 0x7FFFFFFF, two bytes behind it.
            var lie = new byte[] { 0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0x07, 0x41, 0x42 };

            Assert.Empty(ProtoMessage.Parse(lie).Fields);
        }

        [Fact]
        public void A_wire_type_that_does_not_exist_is_survivable()
        {
            // Wire type 6. The parser must come back rather than throw into the router.
            var invented = new byte[] { 0x0E, 0x01, 0x02 };

            ProtoMessage.Parse(invented);
        }

        [Fact]
        public void Empty_input_parses_to_nothing()
        {
            Assert.Empty(ProtoMessage.Parse(new byte[0]).Fields);
        }

        [Fact]
        public void A_truncated_varint_reads_as_zero_rather_than_throwing()
        {
            // The tag says varint and then the message ends, which is what a frame cut short looks
            // like. The parser deliberately keeps what it read and lets the field come out as
            // zero: every handler walks Fields looking for its own and leaves when it is not
            // there, so a zero is handled and an exception in the middle of routing is not.
            var parsed = ProtoMessage.Parse(new byte[] { 0x08 });

            Assert.Single(parsed.Fields);
            Assert.Equal(1, parsed.Fields[0].FieldNumber);
            Assert.Equal(0L, parsed.Fields[0].VarIntValue);
        }

        [Fact]
        public void A_well_formed_message_is_still_read_whole()
        {
            // The other half: bounds that eat a correct message are worse than no bounds at all.
            var good = Pb.New().Var(1, 7).Var(2, 9).Build();

            var parsed = ProtoMessage.Parse(good);

            Assert.Equal(2, parsed.Fields.Count);
            Assert.Equal(7L, parsed.Fields[0].VarIntValue);
            Assert.Equal(9L, parsed.Fields[1].VarIntValue);
        }

        [Fact]
        public void A_number_written_as_text_is_shown_as_text()
        {
            // The quest reward's lqn: «Has ganado 50960 kamas». The five ASCII bytes also parse as
            // a field 6 of four fixed bytes, and the log showed { 2: 45 4: { 6: 0x } }.
            var lqn = Pb.New().Var(2, 45).Str(4, "50960").Build();

            Assert.Equal("{ 2: 45 4: \"50960\" }", ProtoMessage.Parse(lqn).Compact());
        }

        [Fact]
        public void A_fixed_field_shows_its_value()
        {
            var fixed32 = new byte[] { 0x0D, 0x78, 0x56, 0x34, 0x12 };

            Assert.Equal("{ 1: 0x12345678 }", ProtoMessage.Parse(fixed32).Compact());
        }
    }
}

using System;
using System.IO;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol.Wire;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// Reading the protocol the client declares, so a frame can be read instead of guessed at.
    /// </summary>
    public class ProtoSchemaTests
    {
        private static ProtoSchema Parse(params string[] lines) => ProtoSchema.Parse(lines, "test");

        [Fact]
        public void A_message_and_its_fields_come_out()
        {
            var schema = Parse(
                "message kqz {",
                "  int32 fytj = 1;",
                "  string fytk = 2;",
                "  string fytl = 3;",
                "}");

            var message = schema.Message("kqz");
            Assert.NotNull(message);
            Assert.Equal(3, message!.Fields.Count);

            var third = message.Field(3);
            Assert.NotNull(third);
            Assert.Equal("fytl", third!.Name);
            Assert.Equal("string", third.Type);
            Assert.True(third.IsScalar);
            Assert.False(third.Repeated);
        }

        [Fact]
        public void Repeated_is_kept()
        {
            var schema = Parse("message a {", "  repeated int32 cells = 4;", "}");

            var field = schema.Message("a")!.Field(4);
            Assert.True(field!.Repeated);
            Assert.Equal("int32", field.Type);
        }

        /// <summary>
        /// The type has a space inside it, so splitting on the first space rather than the last one
        /// gives the field a type of "map&lt;int32," and a name of "int32&gt;".
        /// </summary>
        [Fact]
        public void A_map_type_survives_having_a_space_in_it()
        {
            var schema = Parse("message a {", "  map<int32, int32> fmgs = 5;", "}");

            var field = schema.Message("a")!.Field(5);
            Assert.Equal("map<int32, int32>", field!.Type);
            Assert.Equal("fmgs", field.Name);
            Assert.True(field.IsMap);
        }

        [Fact]
        public void A_field_whose_type_is_another_message_is_not_scalar()
        {
            var schema = Parse("message a {", "  lba thing = 1;", "}", "message lba {", "  int32 x = 1;", "}");

            Assert.False(schema.Message("a")!.Field(1)!.IsScalar);
            Assert.NotNull(schema.Message("lba"));
        }

        [Fact]
        public void Enums_are_counted_and_not_mistaken_for_messages()
        {
            var schema = Parse("enum hdz {", "  ebfq = 0;", "  ebfr = 1;", "}", "message a {", "}");

            Assert.True(schema.IsEnum("hdz"));
            Assert.Null(schema.Message("hdz"));
            Assert.Equal(1, schema.MessageCount);
            Assert.Equal(1, schema.EnumCount);
        }

        [Fact]
        public void A_message_with_no_fields_is_still_a_message()
        {
            var schema = Parse("message kra {", "}");

            Assert.NotNull(schema.Message("kra"));
            Assert.Empty(schema.Message("kra")!.Fields);
        }

        [Fact]
        public void Comments_and_blank_lines_are_ignored()
        {
            var schema = Parse(
                "// a comment",
                "",
                "message a {   // and one here",
                "  int32 x = 1;  // and here",
                "}");

            Assert.Equal("x", schema.Message("a")!.Field(1)!.Name);
        }

        // ─── Not being there is a normal state ────────────────────────────────────

        /// <summary>
        /// The editor is meant to open on a machine that has never run the extraction tools. A
        /// frame view showing field numbers instead of names is still worth having, and a hard
        /// failure here would take the whole section down with it.
        /// </summary>
        [Fact]
        public void A_missing_file_is_reported_and_not_thrown()
        {
            string complaint = "";
            var schema = ProtoSchema.Load(Path.Combine(Path.GetTempPath(), "no-such-protocol.proto"),
                                          message => complaint = message);

            Assert.Equal(0, schema.MessageCount);
            Assert.Contains("not there", complaint);
        }

        [Fact]
        public void A_null_path_is_survivable()
            => Assert.Equal(0, ProtoSchema.Load(null).MessageCount);

        // ─── Against the real file, when it is here ───────────────────────────────

        /// <summary>
        /// The parser is deliberately written for this one file rather than for the proto language,
        /// so it is worth checking against the file itself: 2,169 messages and 550 enums, and
        /// <c>kqz</c> declaring the language code as a string in field 3 — the measurement the
        /// per-session command language rests on.
        /// </summary>
        [Fact]
        public void The_real_protocol_file_parses_if_it_is_here()
        {
            string path = Paths.ProtocolProto;
            if (!File.Exists(path)) return;   // a checkout without datos/ is a normal thing

            var schema = ProtoSchema.Load(path);

            Assert.True(schema.MessageCount > 2000,
                        $"the protocol should hold over two thousand messages, it holds {schema.MessageCount}");

            var kqz = schema.Message("kqz");
            Assert.NotNull(kqz);
            Assert.Equal("string", kqz!.Field(3)!.Type);
        }

        /// <summary>
        /// The ceiling the shape algorithm uses to tell a nested message from a block of data is
        /// measured off this file: the highest declared field number in the whole protocol is 40.
        /// If a patch ever declares a higher one, this says so instead of the registry quietly
        /// filling with rubbish.
        /// </summary>
        [Fact]
        public void No_declared_field_number_comes_near_the_ceiling()
        {
            string path = Paths.ProtocolProto;
            if (!File.Exists(path)) return;

            var schema = ProtoSchema.Load(path);
            int highest = 0;
            int fields = 0;
            foreach (var message in schema.Messages.Values)
            {
                foreach (var field in message.Fields)
                {
                    fields++;
                    if (field.Number > highest) highest = field.Number;
                }
            }

            // 6,186 message fields, measured. The 8,972 this used to say was that plus the 2,786
            // enum members, which are values and not field numbers and have nothing to do with the
            // ceiling.
            Assert.True(fields > 6000, $"expected the whole protocol, found {fields} fields");
            Assert.True(highest <= WireMessage.HighestFieldNumber,
                        $"a field is declared as number {highest}, which is past the {WireMessage.HighestFieldNumber} " +
                        "the shape algorithm accepts as part of a real structure. Raise the ceiling, " +
                        "or blocks of data will start being read as nested messages.");
        }
    }
}

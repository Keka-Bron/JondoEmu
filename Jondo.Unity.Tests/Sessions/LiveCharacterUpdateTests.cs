using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Sessions
{
    public class LiveCharacterUpdateTests
    {
        [Fact]
        public void One_request_can_describe_every_supported_live_action()
        {
            const string json = """
                {
                  "personaje": "Ty",
                  "nivel": 200,
                  "mapa": 88212759,
                  "celda": 321,
                  "objeto": 1234,
                  "cantidad": 3,
                  "montura": 5678,
                  "sabiduria": 500,
                  "kamas": 50000
                }
                """;

            bool parsed = LiveCharacterUpdate.TryParse(json, out var update, out string error);

            Assert.True(parsed, error);
            Assert.NotNull(update);
            Assert.True(update.HasChanges);
            Assert.Equal("Ty", update.Character);
            Assert.Equal(200, update.Level);
            Assert.Equal(88212759, update.MapId);
            Assert.Equal(321, update.Cell);
            Assert.Equal(1234, update.ItemGid);
            Assert.Equal(3, update.Quantity);
            Assert.Equal(5678, update.MountGid);
            Assert.Equal(500, update.Wisdom);
            Assert.Equal(50000, update.Kamas);
        }

        [Fact]
        public void Numeric_fields_do_not_accept_strings()
        {
            bool parsed = LiveCharacterUpdate.TryParse(
                "{\"personaje\":\"Ty\",\"niveau\":\"200\",\"nivel\":\"200\"}",
                out _, out string error);

            Assert.False(parsed);
            Assert.Equal("campo-invalido-nivel", error);
        }

        [Theory]
        [InlineData("{\"personaje\":\"Ty\",\"celda\":1}", "celda-sin-mapa")]
        [InlineData("{\"personaje\":\"Ty\",\"cantidad\":1}", "cantidad-sin-objeto")]
        [InlineData("{\"personaje\":\"Ty\",\"objeto\":1,\"cantidad\":0}", "objeto-invalido")]
        [InlineData("{\"personaje\":\"Ty\",\"montura\":0}", "montura-invalida")]
        [InlineData("{\"personaje\":\"Ty\",\"mapa\":0}", "mapa-invalido")]
        public void Dependent_and_identifier_fields_are_validated(string json, string expected)
        {
            bool parsed = LiveCharacterUpdate.TryParse(json, out _, out string error);

            Assert.False(parsed);
            Assert.Equal(expected, error);
        }

        [Fact]
        public void An_empty_patch_is_valid_but_has_no_changes()
        {
            bool parsed = LiveCharacterUpdate.TryParse(
                "{\"personaje\":\"Ty\",\"token\":\"not-read-by-this-parser\"}",
                out var update, out string error);

            Assert.True(parsed, error);
            Assert.NotNull(update);
            Assert.False(update.HasChanges);
        }
    }
}

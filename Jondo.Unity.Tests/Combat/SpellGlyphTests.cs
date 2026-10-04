using System.Linq;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A glyph laid by a spell, as the client is told of it: the jwe 401 of the captures.
    /// </summary>
    public class SpellGlyphTests
    {
        private static ProtoField Field(byte[] message, int number)
            => ProtoMessage.Parse(message).Fields.Single(f => f.FieldNumber == number);

        /// <summary>
        /// Against the Xelor's glyph in "xelor-reloj de bolsillo-arenas del tiempo": owner
        /// 53720973411, one cell 245 in colour 6231403, casting 13312 at grade 1, glyph 1, laid by
        /// 13301. The cell's own f1 (4 in this one) is not known and not sent.
        /// </summary>
        [Fact]
        public void A_spell_glyph_says_what_the_capture_says()
        {
            const long owner = 53720973411;
            byte[] jwe = FightProtocol.BuildSpellGlyph(owner, glyphId: 1, cells: new[] { 245 }, aimedCell: 245,
                                                       castSpell: 13312, layingSpell: 13301, grade: 1, colour: 6231403);

            Assert.Equal(owner, (long)Field(jwe, 3).VarIntValue);
            Assert.Equal(401, (int)Field(jwe, 14).VarIntValue);

            byte[] body = Field(Field(jwe, 32).BytesValue, 1).BytesValue;
            var fields = ProtoMessage.Parse(body).Fields;
            var cell = ProtoMessage.Parse(fields.Single(f => f.FieldNumber == 1).BytesValue).Fields;
            Assert.Equal(6231403, (int)cell.Single(f => f.FieldNumber == 2).VarIntValue);
            Assert.Equal(245, (int)cell.Single(f => f.FieldNumber == 3).VarIntValue);

            int Var(int n) => (int)fields.Single(f => f.FieldNumber == n).VarIntValue;
            Assert.Equal(1, Var(2));
            Assert.Equal(13312, Var(3));
            Assert.Equal(1, Var(4));
            Assert.Equal(1, Var(6));
            Assert.Equal(6231403, Var(7));
            Assert.Equal(13301, Var(9));
            Assert.Equal(245, Var(10));
            Assert.Equal(1, Var(11));
            Assert.Equal(owner, (long)fields.Single(f => f.FieldNumber == 12).VarIntValue);
        }

        /// <summary>Every cell of the footprint goes, and a black glyph -- colour 0 -- carries none.</summary>
        [Fact]
        public void Every_cell_goes_and_black_is_no_colour()
        {
            byte[] jwe = FightProtocol.BuildSpellGlyph(-1, 7, new[] { 300, 314, 286, 301, 299 }, 300, 3645, 3644, 1, 0);

            byte[] body = Field(Field(jwe, 32).BytesValue, 1).BytesValue;
            var fields = ProtoMessage.Parse(body).Fields;
            var cells = fields.Where(f => f.FieldNumber == 1).ToList();
            Assert.Equal(5, cells.Count);
            Assert.All(cells, c => Assert.DoesNotContain(ProtoMessage.Parse(c.BytesValue).Fields, f => f.FieldNumber == 2));
            Assert.DoesNotContain(fields, f => f.FieldNumber == 7);
        }
    }
}

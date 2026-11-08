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
        /// 53000000025, one cell 245 in colour 6231403, casting 13312 at grade 1, glyph 1, laid by
        /// 13301. The cell's own f1 (4 in this one) is not known and not sent.
        /// </summary>
        [Fact]
        public void A_spell_glyph_says_what_the_capture_says()
        {
            const long owner = 53000000025;
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

        /// <summary>
        /// A trap is no glyph: jwe 400, f2 3 and f5 1, as all the traps of the Sram captures
        /// («sram-trampa de inmovilizacion»: Inmovilización 12923 laid by 12910 at grade 3 on 215,
        /// colour 3222918, trap 8). Its circle of three goes as one cell, the centre, size 3.
        /// </summary>
        [Fact]
        public void A_trap_is_a_jwe_400_and_its_circle_is_its_centre()
        {
            const long owner = 53000000025;
            var cells = FightProtocol.TrapCells('C', 3, 215, Enumerable.Range(0, 25).Select(i => 200 + i));
            byte[] jwe = FightProtocol.BuildTrap(owner, 8, cells, 215, 12923, 12910, 3, 3222918);

            Assert.Equal(400, (int)Field(jwe, 14).VarIntValue);
            var fields = ProtoMessage.Parse(Field(Field(jwe, 32).BytesValue, 1).BytesValue).Fields;
            int Var(int n) => (int)fields.Single(f => f.FieldNumber == n).VarIntValue;
            Assert.Equal(3, Var(2));
            Assert.Equal(12923, Var(3));
            Assert.Equal(8, Var(4));
            Assert.Equal(1, Var(5));
            Assert.Equal(3, Var(6));
            Assert.Equal(12910, Var(9));
            Assert.Equal(215, Var(10));

            // { f1: 3, f2: 3222918, f3: 215 }, byte for byte the capture's marked cell.
            var cell = Assert.Single(fields, f => f.FieldNumber == 1);
            Assert.Equal("08031086dbc40118d701", System.Convert.ToHexString(cell.BytesValue).ToLowerInvariant());
        }

        /// <summary>A cross (Repulsiva's X1) is its centre with f4 1; Deriva's diagonal cross goes cell by cell.</summary>
        [Fact]
        public void A_cross_is_kind_one_and_any_other_shape_goes_cell_by_cell()
        {
            Assert.Equal(new[] { (229, 1, 1) }, FightProtocol.TrapCells('X', 1, 229, new[] { 229, 215, 243, 228, 230 }));
            Assert.Equal(5, FightProtocol.TrapCells('+', 1, 217, new[] { 245, 216, 217, 218, 189 }).Count);
            Assert.Equal(new[] { (144, 0, 0) }, FightProtocol.TrapCells('P', 1, 144, new[] { 144 }));
        }
    }
}

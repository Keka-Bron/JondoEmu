using System.Linq;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat;

public class TypedMarkTests
{
    private static FightMark Check(GameActionFightEvent placement, byte[] august)
    {
        var codec = new GameCodec();
        var bytes = codec.Encode(new(placement));
#if !JONDO_PROTOCOL_3_8_1_2
        // Compare protobuf objects (including presence), not serialization order.
        Assert.Equal(codec.DecodeRawPayload("jwe", august), codec.DecodeRawPayload("jwe", codec.ReadEnvelope(bytes).Content.Value.Span));
#endif
        var action = Assert.IsType<GameActionFightEvent>(codec.Decode(bytes).Content);
        Assert.Equal(placement.ActionType, action.ActionType);
        var original = Assert.IsType<FightActionCaseMarkCells>(placement.Payload).Value.Mark;
        var mark = Assert.IsType<FightActionCaseMarkCells>(action.Payload).Value.Mark;
        Assert.Equal(original with { Cells = mark.Cells }, mark);
        Assert.Equal(original.Cells.Select(c => GameCodec.JondoVersion == "3.8.1.2" ? c with { Color = 0 } : c), mark.Cells);
        return mark;
    }

    [Fact]
    public void Wall_portal_and_glyph_keep_distinct_mark_parameters()
    {
        const int cell = 245, color = 6231403;
        var glyph = new Glifo(-11, [cell, 260], 13312, 3, color, 0, "", Disparo.AlPisar) { Id = 8 };
        var wall = Check(CombatMarks.Wall(glyph, cell), FightProtocol.BuildGlyph(-11, 8, cell, 13312, 3, 2, color));
        Assert.Equal((FightMarkType.Wall, 0, 0, 0), (wall.MarkType, wall.Param1, wall.Param2, wall.Param3));
        var spell = Check(CombatMarks.Glyph(glyph, cell, 13301, 1), FightProtocol.BuildSpellGlyph(-11, 8, glyph.Casillas, cell, 13312, 13301, 1, color));
        Assert.Equal((FightMarkType.Glyph, 13312, 1, color), (spell.MarkType, spell.Param1, spell.Param2, spell.Param3));
        Assert.Equal(new[] { cell, 260 }, spell.Cells.Select(c => c.Id));
        var portal = new Portal { Id = 7, Owner = 42, Team = 1, Cell = cell, DiceNum = 2, DiceSide = 44338, LayingSpell = 14574, Grade = 3 };
        var laid = Check(CombatMarks.Portal(portal), FightProtocol.BuildPortal(42, 7, cell, 2, 44338, 3, 14574, false));
        Assert.Equal((FightMarkType.Portal, 2, 44338, 0), (laid.MarkType, laid.Param1, laid.Param2, laid.Param3));
        Assert.False(laid.Active);
    }

    [Theory]
    [InlineData('C', 3, FightMarkCellsType.CellsCircle)]
    [InlineData('X', 1, FightMarkCellsType.CellsCross)]
    [InlineData('D', 2, FightMarkCellsType.CellsCircle)]
    public void Trap_uses_a_sized_center_or_explicit_cells(char shape, int size, FightMarkCellsType type)
    {
        var trap = new Glifo(4, [215, 201, 229], 12923, 1, 3222918, 0, "", Disparo.AlPisar) { Id = 8, ZoneShape = shape, ZoneSize = size };
        var mark = Check(CombatMarks.Trap(trap, 215, 12910, 3), FightProtocol.BuildTrap(4, 8,
            FightProtocol.TrapCells(shape, size, 215, trap.Casillas), 215, 12923, 12910, 3, trap.Color));
        Assert.Equal(FightMarkType.Trap, mark.MarkType);
        Assert.Equal(shape == 'D' ? new[] { 215, 201, 229 } : new[] { 215 }, mark.Cells.Select(c => c.Id));
        Assert.All(mark.Cells, c => Assert.Equal((shape == 'D' ? 0 : size, type), (c.ZoneSize, c.CellsType)));
    }

    [Theory]
    [InlineData(true, 2, 2)]
    [InlineData(true, 3, 0)]
    [InlineData(false, 1, 1)]
    public void Auras_preserve_empty_explicit_and_sized_footprints(bool lance, int rank, int count)
    {
        var aura = new Glifo(-2, [314, 330], 23847, rank, 123, 0, "", Disparo.AlPonerse)
            { Id = 9, Centro = 314, HechizoQueLoPuso = lance ? ForgelanceCombat.LanceAura : ForgelanceCombat.Holmgang };
        var mark = Check(CombatMarks.Aura(aura, 3), ForgelanceProtocol.Aura(aura, 3));
        Assert.Equal(count, mark.Cells.Count);
        if (!lance) Assert.Equal(2, Assert.Single(mark.Cells).ZoneSize);
    }
}

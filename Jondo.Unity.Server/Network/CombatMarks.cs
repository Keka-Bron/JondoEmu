using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Network;

/// <summary>Placement visuals from resolved fight state. Recipients are chosen by the fight handler.</summary>
internal static class CombatMarks
{
    private static GameActionFightEvent Place(int action, long author, int id, FightMarkType kind, int spell,
        int level, int cell, IReadOnlyList<FightMarkedCell> cells, int param1 = 0, int param2 = 0,
        int param3 = 0, bool active = true)
        => new(author, action, new FightActionCaseMarkCells(new(new(cells, id, level, spell, cell,
            active, author, kind, Team.Challenger, param1, param2, param3)))); // Team is the captured default, independent of audience.

    private static FightMarkedCell[] Footprint(Glifo glyph)
        => glyph.Casillas.Select(cell => new FightMarkedCell(cell, glyph.Color)).ToArray();

    internal static GameActionFightEvent Wall(Glifo wall, int cell)
        => Place(FightProtocol.PlacedGlyph, wall.Dueno, wall.Id, FightMarkType.Wall, wall.Hechizo,
            wall.Grado, cell, [new(cell, wall.Color)]);

    internal static GameActionFightEvent Portal(Portal portal)
        => Place(FightProtocol.PlacedGlyph, portal.Owner, portal.Id, FightMarkType.Portal, portal.LayingSpell,
            portal.Grade, portal.Cell, [new(portal.Cell, FightProtocol.PortalColour)],
            param1: portal.DiceNum, param2: portal.DiceSide, active: portal.Active);

    internal static GameActionFightEvent Glyph(Glifo glyph, int cell, int spell, int grade)
        => Place(FightProtocol.PlacedGlyph, glyph.Dueno, glyph.Id, FightMarkType.Glyph, spell, grade,
            cell, Footprint(glyph), param1: glyph.Hechizo, param2: 1, param3: glyph.Color);

    internal static GameActionFightEvent Trap(Glifo trap, int cell, int spell, int grade)
    {
        FightMarkedCell[] cells = trap.ZoneSize > 0 && trap.ZoneShape is 'C' or 'X'
            ? [new(cell, trap.Color, trap.ZoneSize, trap.ZoneShape == 'X' ? FightMarkCellsType.CellsCross : FightMarkCellsType.CellsCircle)]
            : Footprint(trap);
        return Place(FightProtocol.PlacedTrap, trap.Dueno, trap.Id, FightMarkType.Trap, spell, grade, cell,
            cells, param1: trap.Hechizo, param2: 3, param3: trap.Color);
    }

    internal static GameActionFightEvent Aura(Glifo glyph, int grade)
    {
        bool lance = glyph.HechizoQueLoPuso == ForgelanceCombat.LanceAura;
        FightMarkedCell[] cells = lance && glyph.Grado == 2 ? Footprint(glyph)
            : !lance && glyph.Hechizo == 23847 ? [new(glyph.Centro, glyph.Color, ZoneSize: 2)] : [];
        return Place(1091, glyph.Dueno, glyph.Id, FightMarkType.Glyph, glyph.HechizoQueLoPuso, grade,
            glyph.Centro, cells, param1: glyph.Hechizo, param2: lance ? glyph.Grado : 1, param3: glyph.Color);
    }
}

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The bits of a spell level's <c>m_flags</c>, the SpellLevels.Flags column: the client's
    /// <c>SpellLevelFlags</c> enum, whose names its metadata lists in this order -- CastInLine,
    /// CastInDiagonal, CastTestLos, NeedFreeCell, NeedTakenCell, NeedFreeTrapCell,
    /// RangeCanBeBoosted, HideEffects, PlayAnimation, NeedVisibleEntity, NeedCellWithoutPortal,
    /// PortalProjectionForbidden, ProportionalDiagonal.
    /// </summary>
    /// <remarks>
    /// The bit of each name is read off the 34,685 levels of the data. The first five match the
    /// columns already split out of it (CastInLine, CastTestLos, NeedFreeCell...). Bit 9 is on
    /// 28,960 levels, so it is PlayAnimation, and bit 8 is on one only, so it is not in the enum.
    /// That leaves the two portal bits, 11 and 12. Bit 11 is on the spells that lay a portal or a
    /// trap: Portal, Errancia, Exilio, Trampa Dimensional, Raratrampa. Bit 12 is on six spells,
    /// and on no Selatrop spell.
    /// </remarks>
    public static class SpellLevelFlags
    {
        /// <summary>It can be cast along a diagonal (the client's CastInDiagonal, 2).</summary>
        public const int CastInDiagonal = 1 << 1;

        /// <summary>
        /// Its diagonal counts as far as walking it (the client's ProportionalDiagonal, 8192): two
        /// steps for each diagonal cell, not one. See MapGeometry.CastDistance.
        /// </summary>
        public const int ProportionalDiagonal = 1 << 13;

        /// <summary>
        /// The caster's range adds to the spell's: Casca's 1-6 reaches farther with +6 range, Ira de
        /// Yopuka's 1-1 and Conquista's 1-6 do not.
        /// </summary>
        public const int RangeCanBeBoosted = 1 << 6;

        /// <summary>The cell it lands on must hold no portal: Portal, Errancia, Exilio.</summary>
        public const int NeedCellWithoutPortal = 1 << 11;

        /// <summary>Aimed at a portal, it does not go through: it is cast at the portal's cell.</summary>
        public const int PortalProjectionForbidden = 1 << 12;
    }
}

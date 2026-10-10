namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The eight damage percentages of the sheet: what a fighter deals with spells or weapons, at
    /// melee or at range, and what he takes the same four ways. Each is a multiplier with its base at
    /// 100, the way the real server sends them -- "f2: 100" in every sheet of the captures, and what
    /// the gear and the states move it by beside it.
    /// </summary>
    /// <remarks>
    /// <para>The ones a fighter TAKES go the other way round from their effects: "+X % resistencia"
    /// lowers the multiplier by X. The shields' "+3 a 5 % resistencia distancia" (2807) and "+5 a 7 %
    /// resistencia CaC" (2803) come out as -10 and -7 on 121 and 124 in the sheet of "equipando todas
    /// los escudos nivel 160 o superior"; a Feca's state of +30 % ranged resistance is "121, f8: -30" in
    /// "feca-todos los hechizos variantes". The catalogue reads the effects so
    /// (DatabaseManager.EffectMeta), and the fight multiplies by them as they are.</para>
    /// <para>A blow is a spell's or a weapon's (spell 0), and at melee when the two stand side by
    /// side, at range otherwise: the same split the "DCAC" and "DR" triggers make.</para>
    /// </remarks>
    public static class DamagePercentages
    {
        public const int Ranged = 120;
        public const int RangedTaken = 121;
        public const int Weapons = 122;
        public const int Spells = 123;
        public const int MeleeTaken = 124;
        public const int Melee = 125;
        public const int SpellsTaken = 141;
        public const int WeaponsTaken = 142;

        /// <summary>The eight, in the client's order.</summary>
        public static readonly int[] All = { Ranged, RangedTaken, Weapons, Spells, MeleeTaken, Melee, SpellsTaken, WeaponsTaken };

        /// <summary>Whether it is one a fighter takes damage by: its effects' resistances lower it.</summary>
        public static bool IsTaken(int characteristic)
            => characteristic is RangedTaken or MeleeTaken or SpellsTaken or WeaponsTaken;

        /// <summary>What a blow is multiplied by on the way out and on the way in, for its kind and reach.</summary>
        public static (int Kind, int Reach) Dealt(bool spell, bool melee) => (spell ? Spells : Weapons, melee ? Melee : Ranged);

        /// <summary>See <see cref="Dealt"/>.</summary>
        public static (int Kind, int Reach) Taken(bool spell, bool melee)
            => (spell ? SpellsTaken : WeaponsTaken, melee ? MeleeTaken : RangedTaken);
    }
}

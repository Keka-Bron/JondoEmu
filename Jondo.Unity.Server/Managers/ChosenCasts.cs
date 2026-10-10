using System;
using System.Linq;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Spells that cast ONE of the spells they list, the server choosing which. The client's data
    /// gives every candidate as a plain "cast this" (792) with nothing in it -- no chance, no
    /// group, no state criterion -- that picks one; the choice is a server rule, and these are
    /// the ones the content needs.
    /// </summary>
    /// <remarks>
    ///   Pensamientos Oscuros (32741) casts its four tiers: only the fight floor's light's goes
    ///   through (<see cref="GuildRaidDarkness"/>).
    ///
    ///   The Cangrancio's four "Clean Seuil" spells cast the four forms of that step: only one,
    ///   at random among those the fight has not shown yet (<see cref="GuildRaidExecrabe"/>).
    ///
    ///   The Santuario's colour versions -- "Check color == 2", "color == 3 / eau" -- go through
    ///   only for the raid's colour (<see cref="GuildRaidGuardians"/>).
    ///
    ///   The Vigilante's and the Guardián's checks -- "mob == 1" against "mob != 1", "o_hint_3 &lt; 5"
    ///   against "o_hint_3 &gt; 4" -- go through as the right companion and the right glyph, any
    ///   of them, once a fight (<see cref="GuildRaidGuardians"/>).
    /// </remarks>
    public static class ChosenCasts
    {
        /// <summary>Whether a dispatcher spell's cast of this candidate goes through.</summary>
        public static bool Allows(FightInstance fight, int dispatcher, int candidate)
        {
            if (dispatcher == GuildRaidDarkness.DarkThoughts) return fight != null && candidate == fight.DarknessTier;
            if (GuildRaidExecrabe.ThresholdSpells.Contains(dispatcher))
                return GuildRaidExecrabe.Allows(fight, dispatcher, candidate, Random.Shared);
            if (GuildRaidGuardians.CheckVerdict(candidate) != null) return GuildRaidGuardians.Passes(fight, candidate);
            int colour = GuildRaidGuardians.ColourOf(candidate);
            if (colour != 0) return colour == GuildRaidGuardians.ColourOf(fight);
            return true;
        }
    }
}

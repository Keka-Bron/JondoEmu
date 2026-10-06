using System.Collections.Generic;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The single source of truth for the player's combat spell list and shortcuts. Both the
    /// placement message (jvn) and the live combat bar (jyy) must carry the same variant choices.
    /// </summary>
    public static class FightSpellLayout
    {
        public const int SlotCount = 40;

        public sealed class Layout
        {
            public List<(int Spell, int Grade)> Spells { get; } = new();
            public List<(int Slot, int Spell)> Bar { get; } = new();
        }

        public static Layout Current(int breed, int level)
            => Current(breed, level, 0);

        /// <summary>
        /// The in-fight bar, with the administration spells if the account is one.
        /// </summary>
        /// <remarks>
        /// It goes apart from the list outside the fight: they are two different messages -- the hms of entering
        /// the world and the jyy of the fight's start -- and adding the spell only to the first leaves it visible
        /// on the walking panel and missing exactly where it is needed.
        /// </remarks>
        public static Layout Current(int breed, int level, long accountId)
        {
            var conocidos = new List<SpellTable.KnownSpell>(
                SpellTable.KnownFor(breed, level, SpellChoices.Chosen));

            if (AdminSpells.Para(accountId))
            {
                conocidos.Add(new SpellTable.KnownSpell(
                    AdminSpells.DoomDeMasas, AdminSpells.DoomDeMasas, AdminSpells.GradoDeDoom));
            }

            return Build(conocidos, SpellChoices.Bar);
        }

        /// <summary>
        /// Keeps valid saved shortcuts, drops spells lost after a level or variant change, fills
        /// newly opened spells into free slots, and reserves one slot for close combat (spell 0).
        /// </summary>
        public static Layout Build(IEnumerable<SpellTable.KnownSpell> known,
                                   IReadOnlyDictionary<int, int> savedBar)
        {
            var layout = new Layout();
            var available = new HashSet<int>();
            foreach (var spell in known)
            {
                layout.Spells.Add((spell.SpellId, spell.Grade));
                available.Add(spell.SpellId);
            }

            var occupiedSlots = new HashSet<int>();
            var placedSpells = new HashSet<int>();
            foreach (var saved in savedBar)
            {
                if (saved.Key < 0 || saved.Key >= SlotCount || !available.Contains(saved.Value))
                    continue;

                layout.Bar.Add((saved.Key, saved.Value));
                occupiedSlots.Add(saved.Key);
                placedSpells.Add(saved.Value);
            }

            // Starting at ONE: slot zero is where the client draws the weapon, and in 37 of the
            // 51 bars of the captures it is empty for that very reason.
            int next = 1;
            foreach (var spell in layout.Spells)
            {
                if (placedSpells.Contains(spell.Spell)) continue;
                while (next < SlotCount && occupiedSlots.Contains(next)) next++;
                if (next >= SlotCount) break;

                layout.Bar.Add((next, spell.Spell));
                occupiedSlots.Add(next);
                placedSpells.Add(spell.Spell);
                next++;
            }

            // The melee, in the first free slot and ALWAYS. It is in the 13 player bars of the
            // captures, including the tutorial character's, who carries not a single item: the
            // slot is the fist's and does not depend on having a weapon.
            //
            // With no cap, which is how it was before this class. Cutting at SlotCount left it out
            // when the forty slots were taken, and then the player goes into the fight unable to
            // throw a punch. The captured bar goes up to 48, so there is plenty of room above the
            // forty that fill themselves.
            int weaponSlot = 0;
            while (occupiedSlots.Contains(weaponSlot)) weaponSlot++;
            layout.Bar.Add((weaponSlot, Network.FightProtocol.HechizoCuerpoACuerpo));

            layout.Bar.Sort((left, right) => left.Slot.CompareTo(right.Slot));
            return layout;
        }
    }
}

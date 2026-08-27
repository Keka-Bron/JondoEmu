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
            => Build(SpellTable.KnownFor(breed, level, SpellChoices.Chosen), SpellChoices.Bar);

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

            int next = 1; // Slot zero is normally the close-combat shortcut.
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

            int weaponSlot = 0;
            while (weaponSlot < SlotCount && occupiedSlots.Contains(weaponSlot)) weaponSlot++;
            if (weaponSlot < SlotCount) layout.Bar.Add((weaponSlot, Network.FightProtocol.HechizoCuerpoACuerpo));

            layout.Bar.Sort((left, right) => left.Slot.CompareTo(right.Slot));
            return layout;
        }
    }
}

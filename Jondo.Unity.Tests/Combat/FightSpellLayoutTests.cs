using System.Collections.Generic;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    public class FightSpellLayoutTests
    {
        [Fact]
        public void A_stale_base_shortcut_is_replaced_by_the_available_variant()
        {
            var known = new[] { new SpellTable.KnownSpell(7, 200, 4) };
            var saved = new Dictionary<int, int> { [6] = 100 };

            var layout = FightSpellLayout.Build(known, saved);

            Assert.Equal(new[] { (200, 4) }, layout.Spells);
            Assert.DoesNotContain(layout.Bar, entry => entry.Spell == 100);
            Assert.Contains((1, 200), layout.Bar);
            Assert.Contains((0, FightProtocol.HechizoCuerpoACuerpo), layout.Bar);
        }

        [Fact]
        public void Valid_custom_slots_are_preserved_and_new_spells_fill_free_slots()
        {
            var known = new[]
            {
                new SpellTable.KnownSpell(1, 101, 2),
                new SpellTable.KnownSpell(2, 202, 3),
                new SpellTable.KnownSpell(3, 303, 1),
            };
            var saved = new Dictionary<int, int> { [5] = 202, [9] = 101 };

            var layout = FightSpellLayout.Build(known, saved);

            Assert.Contains((5, 202), layout.Bar);
            Assert.Contains((9, 101), layout.Bar);
            Assert.Contains((1, 303), layout.Bar);
            Assert.Contains((0, FightProtocol.HechizoCuerpoACuerpo), layout.Bar);
        }

        [Fact]
        public void Close_combat_uses_the_first_free_slot_when_zero_is_occupied()
        {
            var known = new[] { new SpellTable.KnownSpell(1, 101, 1) };
            var saved = new Dictionary<int, int> { [0] = 101 };

            var layout = FightSpellLayout.Build(known, saved);

            Assert.Equal(new[] { (0, 101), (1, FightProtocol.HechizoCuerpoACuerpo) }, layout.Bar);
        }

        [Fact]
        public void Shortcuts_outside_the_protocol_bar_are_ignored()
        {
            var known = new[] { new SpellTable.KnownSpell(1, 101, 1) };
            var saved = new Dictionary<int, int> { [FightSpellLayout.SlotCount] = 101 };

            var layout = FightSpellLayout.Build(known, saved);

            Assert.Contains((1, 101), layout.Bar);
            Assert.DoesNotContain(layout.Bar, entry => entry.Slot >= FightSpellLayout.SlotCount);
        }
    }
}

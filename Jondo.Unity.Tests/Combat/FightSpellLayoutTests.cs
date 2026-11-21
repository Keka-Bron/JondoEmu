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
            // Empty saved bar: first seed still auto-fills the available variant.
            var saved = new Dictionary<int, int>();

            var layout = FightSpellLayout.Build(known, saved);

            Assert.Equal(new[] { (200, 4) }, layout.Spells);
            Assert.Contains((1, 200), layout.Bar);
            Assert.Contains((0, FightProtocol.HechizoCuerpoACuerpo), layout.Bar);
        }

        [Fact]
        public void An_empty_saved_bar_auto_fills_known_spells()
        {
            var known = new[]
            {
                new SpellTable.KnownSpell(1, 101, 2),
                new SpellTable.KnownSpell(2, 202, 3),
                new SpellTable.KnownSpell(3, 303, 1),
            };
            var saved = new Dictionary<int, int>();

            var layout = FightSpellLayout.Build(known, saved);

            Assert.Contains((1, 101), layout.Bar);
            Assert.Contains((2, 202), layout.Bar);
            Assert.Contains((3, 303), layout.Bar);
            Assert.Contains((0, FightProtocol.HechizoCuerpoACuerpo), layout.Bar);
        }

        [Fact]
        public void A_customized_bar_keeps_holes_and_does_not_auto_fill()
        {
            // The player removed 303 (or never placed it). Rebuilding must not put it back.
            var known = new[]
            {
                new SpellTable.KnownSpell(1, 101, 2),
                new SpellTable.KnownSpell(2, 202, 3),
                new SpellTable.KnownSpell(3, 303, 1),
            };
            var saved = new Dictionary<int, int> { [0] = FightProtocol.HechizoCuerpoACuerpo, [5] = 202, [9] = 101 };

            var layout = FightSpellLayout.Build(known, saved);

            Assert.Contains((5, 202), layout.Bar);
            Assert.Contains((9, 101), layout.Bar);
            Assert.DoesNotContain(layout.Bar, entry => entry.Spell == 303);
            Assert.Contains((0, FightProtocol.HechizoCuerpoACuerpo), layout.Bar);
        }

        [Fact]
        public void Close_combat_stays_in_the_slot_it_was_saved_in()
        {
            var known = new[] { new SpellTable.KnownSpell(1, 101, 1) };
            var saved = new Dictionary<int, int> { [0] = 101, [1] = FightProtocol.HechizoCuerpoACuerpo };

            var layout = FightSpellLayout.Build(known, saved);

            Assert.Equal(new[] { (0, 101), (1, FightProtocol.HechizoCuerpoACuerpo) }, layout.Bar);
        }

        [Fact]
        public void Shortcuts_outside_the_protocol_bar_are_ignored()
        {
            var known = new[] { new SpellTable.KnownSpell(1, 101, 1) };
            var saved = new Dictionary<int, int> { [FightSpellLayout.SlotCount] = 101 };

            var layout = FightSpellLayout.Build(known, saved);

            // Saved bar is non-empty (even though the only entry is out of range), so no auto-fill.
            Assert.DoesNotContain(layout.Bar, entry => entry.Spell == 101);
            Assert.DoesNotContain(layout.Bar, entry => entry.Slot >= FightSpellLayout.SlotCount);
        }
    }
}

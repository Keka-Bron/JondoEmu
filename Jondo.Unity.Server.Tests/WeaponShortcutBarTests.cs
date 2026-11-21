using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class WeaponShortcutBarTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public WeaponShortcutBarTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE CharacterSpellBar (
                CharacterId INTEGER NOT NULL,
                Slot INTEGER NOT NULL,
                SpellId INTEGER NOT NULL,
                PRIMARY KEY (CharacterId, Slot)
            );
            CREATE TABLE CharacterSpellBarLevel (
                CharacterId INTEGER PRIMARY KEY,
                Level INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void Weapon_can_be_placed_at_any_arbitrary_slot_and_is_retained_on_build()
    {
        // Edge Case: Weapon is at slot 5, slot 0 is empty or has another spell.
        var known = new[] { new SpellTable.KnownSpell(1, 1001, 1), new SpellTable.KnownSpell(2, 1002, 1) };
        var saved = new Dictionary<int, int>
        {
            [0] = 1001, // Slot 0 has a class spell
            [5] = FightProtocol.Weapon, // Slot 5 has the weapon (spell 0)
            [8] = 1002  // Slot 8 has a class spell
        };

        var layout = FightSpellLayout.Build(known, saved, seed: false);

        // Slot 5 MUST be the weapon
        var weaponEntry = Assert.Single(layout.Bar, b => b.Spell == FightProtocol.Weapon);
        Assert.Equal(5, weaponEntry.Slot);

        // Slot 0 MUST remain the class spell, NOT overridden by weapon
        var slot0Entry = Assert.Single(layout.Bar, b => b.Slot == 0);
        Assert.Equal(1001, slot0Entry.Spell);
    }

    [Fact]
    public void Swapping_weapon_from_slot_zero_to_an_empty_slot_persists_the_weapon_at_new_slot()
    {
        // Edge case: Weapon starts at slot 0, dragged into empty slot 7.
        // It should NOT become an empty slot or vanish from the database.
        var barStore = new Dictionary<int, int>
        {
            [0] = FightProtocol.Weapon,
            [1] = 1001
        };

        // When swapping 0 and 7:
        bool hasLeft = barStore.TryGetValue(0, out int spellLeft);
        bool hasRight = barStore.TryGetValue(7, out int spellRight);

        if (hasLeft) barStore[7] = spellLeft; else barStore.Remove(7);
        if (hasRight) barStore[0] = spellRight; else barStore.Remove(0);

        Assert.False(barStore.ContainsKey(0), "Slot 0 should now be empty after moving weapon out.");
        Assert.True(barStore.ContainsKey(7), "Slot 7 must now contain the weapon.");
        Assert.Equal(FightProtocol.Weapon, barStore[7]);

        // Rebuilding layout with this saved bar must keep weapon at slot 7
        var known = new[] { new SpellTable.KnownSpell(1, 1001, 1) };
        var layout = FightSpellLayout.Build(known, barStore, seed: false);

        var weaponEntry = Assert.Single(layout.Bar, b => b.Spell == FightProtocol.Weapon);
        Assert.Equal(7, weaponEntry.Slot);
        Assert.DoesNotContain(layout.Bar, b => b.Slot == 0);
    }

    [Fact]
    public void Swapping_weapon_with_another_spell_exchanges_their_positions_cleanly()
    {
        // Edge case: Weapon at slot 0, Spell 1002 at slot 3.
        // Swap 0 and 3 -> Slot 0 must have Spell 1002, Slot 3 must have Weapon.
        var saved = new Dictionary<int, int>
        {
            [0] = FightProtocol.Weapon,
            [3] = 1002
        };

        int left = 0, right = 3;
        bool hasLeft = saved.TryGetValue(left, out int spellLeft);
        bool hasRight = saved.TryGetValue(right, out int spellRight);
        if (hasLeft) saved[right] = spellLeft; else saved.Remove(right);
        if (hasRight) saved[left] = spellRight; else saved.Remove(left);

        Assert.Equal(1002, saved[0]);
        Assert.Equal(FightProtocol.Weapon, saved[3]);

        var known = new[] { new SpellTable.KnownSpell(1, 1002, 1) };
        var layout = FightSpellLayout.Build(known, saved, seed: false);

        Assert.Equal(1002, layout.Bar.Single(b => b.Slot == 0).Spell);
        Assert.Equal(FightProtocol.Weapon, layout.Bar.Single(b => b.Slot == 3).Spell);
    }

    [Fact]
    public void Removing_weapon_from_bar_keeps_it_removed_without_auto_reinserting()
    {
        // Edge case: Player deliberately right-clicks and removes the weapon from the bar.
        // Subsequent build MUST NOT force the weapon back into slot 0.
        var saved = new Dictionary<int, int>
        {
            [2] = 1001,
            [4] = 1002
        };

        var known = new[] { new SpellTable.KnownSpell(1, 1001, 1), new SpellTable.KnownSpell(2, 1002, 1) };

        // seed == false represents an already touched/saved bar
        var layout = FightSpellLayout.Build(known, saved, seed: false);

        Assert.DoesNotContain(layout.Bar, b => b.Spell == FightProtocol.Weapon);
        Assert.Equal(new[] { 2, 4 }, layout.Bar.Select(b => b.Slot).OrderBy(s => s));
    }

    [Fact]
    public void Deliberately_cleared_bar_stays_completely_empty_and_does_not_reinsert_weapon()
    {
        // Edge case: Player clicks 'clear bar' (vaciar barra).
        // Bar should be completely empty, with 0 shortcuts.
        var emptySaved = new Dictionary<int, int>();
        var known = new[] { new SpellTable.KnownSpell(1, 1001, 1) };

        var layout = FightSpellLayout.Build(known, emptySaved, seed: false);

        Assert.Empty(layout.Bar);
    }

    [Fact]
    public void Only_virgin_never_written_bar_seeds_weapon_at_slot_zero()
    {
        // When seed is true (first time login on a new character):
        var known = new[] { new SpellTable.KnownSpell(1, 1001, 1), new SpellTable.KnownSpell(2, 1002, 1) };
        var emptySaved = new Dictionary<int, int>();

        var layout = FightSpellLayout.Build(known, emptySaved, seed: true);

        Assert.Equal(FightProtocol.Weapon, layout.Bar.Single(b => b.Slot == 0).Spell);
        Assert.Contains(layout.Bar, b => b.Spell == 1001);
        Assert.Contains(layout.Bar, b => b.Spell == 1002);
    }
}

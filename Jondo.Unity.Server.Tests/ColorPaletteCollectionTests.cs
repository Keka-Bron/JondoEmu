using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class ColorPaletteCollectionTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public ColorPaletteCollectionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        Jondo.Unity.Server.Database.Migrations.ColorPalettePalettesMigration.Apply(_connection);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void Palettes_are_strictly_isolated_between_different_characters()
    {
        // Edge Case: Character 101 saves a red palette at slot 1.
        // Character 102 must have NO palettes in slot 1 and cannot see Character 101's colors.
        long charA = 101;
        long charB = 102;

        ColorPaletteManager.SavePaletteDirect(_connection, charA, 0, new[] { 0xFF0000, 0, 0, 0, 0, 0 });

        var palettesA = ColorPaletteManager.GetPalettesDirect(_connection, charA);
        var palettesB = ColorPaletteManager.GetPalettesDirect(_connection, charB);

        Assert.Single(palettesA);
        Assert.Empty(palettesB);
    }

    [Fact]
    public void Maximum_unlocked_palette_slots_is_strictly_capped_at_twenty()
    {
        // Edge Case: Character already has 20 slots. Using a potion must NOT increment to 21.
        long charId = 200;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO CharacterColorSlots (CharacterId, SlotsUnlocked) VALUES ($id, 20);";
        cmd.Parameters.AddWithValue("$id", charId);
        cmd.ExecuteNonQuery();

        int before = ColorPaletteManager.GetSlotsUnlockedDirect(_connection, charId);
        Assert.Equal(20, before);

        bool unlocked = ColorPaletteManager.TryUnlockNextSlotDirect(_connection, charId);

        Assert.False(unlocked, "Unlocking beyond slot 20 must return false and fail gracefully.");
        int after = ColorPaletteManager.GetSlotsUnlockedDirect(_connection, charId);
        Assert.Equal(20, after);
    }

    [Fact]
    public void Saving_palette_persists_exact_six_rgb_colors_at_specified_slot()
    {
        long charId = 300;
        int slot = 2; // third slot
        int[] expectedColors = [0x112233, 0x445566, 0x778899, 0xAABBCC, 0xDDEEFF, 0x123456];

        ColorPaletteManager.SavePaletteDirect(_connection, charId, slot, expectedColors);

        var palettes = ColorPaletteManager.GetPalettesDirect(_connection, charId);
        Assert.True(palettes.ContainsKey(slot));
        Assert.Equal(expectedColors, palettes[slot]);
    }

    [Fact]
    public void Modifying_an_existing_palette_replaces_old_colors_without_affecting_other_slots()
    {
        long charId = 400;
        int[] originalSlot0 = [0x111111, 0x222222, 0x333333, 0x444444, 0x555555, 0x666666];
        int[] originalSlot1 = [0xAAAAAA, 0xBBBBBB, 0xCCCCCC, 0xDDDDDD, 0xEEEEEE, 0xFFFFFF];
        int[] modifiedSlot0 = [0x000000, 0x000000, 0x000000, 0x000000, 0x000000, 0x000000];

        ColorPaletteManager.SavePaletteDirect(_connection, charId, 0, originalSlot0);
        ColorPaletteManager.SavePaletteDirect(_connection, charId, 1, originalSlot1);

        // Modify slot 0
        ColorPaletteManager.SavePaletteDirect(_connection, charId, 0, modifiedSlot0);

        var palettes = ColorPaletteManager.GetPalettesDirect(_connection, charId);
        Assert.Equal(2, palettes.Count);
        Assert.Equal(modifiedSlot0, palettes[0]);
        Assert.Equal(originalSlot1, palettes[1]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(20)]
    [InlineData(99)]
    public void Saving_to_an_invalid_slot_index_throws_argument_out_of_range(int invalidSlot)
    {
        long charId = 500;
        int[] colors = [1, 2, 3, 4, 5, 6];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ColorPaletteManager.SavePaletteDirect(_connection, charId, invalidSlot, colors));
    }

    [Fact]
    public void Saving_invalid_color_array_length_throws_argument_exception()
    {
        long charId = 600;
        // Only 5 colors instead of 6
        int[] invalidLength = [1, 2, 3, 4, 5];

        Assert.Throws<ArgumentException>(() =>
            ColorPaletteManager.SavePaletteDirect(_connection, charId, 0, invalidLength));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(0x1000000)] // > 24-bit RGB
    public void Colors_outside_valid_24bit_rgb_range_throw_argument_out_of_range(int invalidColor)
    {
        long charId = 700;
        int[] colors = [invalidColor, 0, 0, 0, 0, 0];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ColorPaletteManager.SavePaletteDirect(_connection, charId, 0, colors));
    }

    private static DbCharacter Character(long id) => new() { Id = id, Breed = 18, Sex = 1,
        Colors = new(0x112233, 0x223344, 0x334455, 0x445566, 0x556677, 0x667788) };

    private static int[] Plain(int color) => [color, color, color, color, color, color];

    [Fact]
    public void A_character_without_rows_has_one_slot_and_its_own_colours_as_the_only_palette()
    {
        var character = Character(800);
        var colors = ColorPaletteManager.BuildAppearanceEvent(_connection, character).CharacterColors!;

        Assert.Equal(ColorPaletteManager.StartingSlots, colors.SlotsUnlocked);
        Assert.Equal(ColorPaletteManager.TotalSlots, colors.SlotsTotal);
        Assert.Equal(new[] { 0x112233, 0x223344, 0x334455, 0x445566, 0x556677, 0x667788 },
            Assert.Single(colors.ColorPalettes).Colors);
    }

    [Fact]
    public void The_first_potion_opens_a_second_slot_and_each_one_after_opens_one_more()
    {
        Assert.True(ColorPaletteManager.TryUnlockNextSlotDirect(_connection, 810));
        Assert.Equal(2, ColorPaletteManager.GetSlotsUnlockedDirect(_connection, 810));
        Assert.True(ColorPaletteManager.TryUnlockNextSlotDirect(_connection, 810));
        Assert.Equal(3, ColorPaletteManager.GetSlotsUnlockedDirect(_connection, 810));
    }

    [Fact]
    public void A_saved_first_slot_is_shown_as_saved_and_not_replaced_by_the_worn_colours()
    {
        var character = Character(820);
        ColorPaletteManager.SavePaletteDirect(_connection, character.Id, 0, Plain(0xABCDEF));

        var shown = ColorPaletteManager.Shown(character, 1, ColorPaletteManager.GetPalettesDirect(_connection, character.Id));

        Assert.Equal(Plain(0xABCDEF), Assert.Single(shown).Colors);
    }

    [Fact]
    public void A_gap_between_saved_slots_is_skipped_and_the_later_palettes_are_still_listed()
    {
        var character = Character(830);
        using (var slots = _connection.CreateCommand())
        {
            slots.CommandText = "INSERT INTO CharacterColorSlots VALUES (830, 5);";
            slots.ExecuteNonQuery();
        }
        ColorPaletteManager.SavePaletteDirect(_connection, character.Id, 0, Plain(0x000000));
        ColorPaletteManager.SavePaletteDirect(_connection, character.Id, 3, Plain(0x333333));

        var shown = ColorPaletteManager.Shown(character, 5, ColorPaletteManager.GetPalettesDirect(_connection, character.Id));

        Assert.Equal(new[] { 0, 3 }, shown.Select(p => p.Slot));
        Assert.Equal(Plain(0), shown[0].Colors);
        Assert.Equal(Plain(0x333333), shown[1].Colors);
    }

    [Fact]
    public void Saved_palettes_of_slots_no_longer_unlocked_are_not_listed()
    {
        var character = Character(840);
        ColorPaletteManager.SavePaletteDirect(_connection, character.Id, 4, Plain(0x444444));

        var shown = ColorPaletteManager.Shown(character, 2, ColorPaletteManager.GetPalettesDirect(_connection, character.Id));

        Assert.Equal(0, Assert.Single(shown).Slot);
    }

    [Fact]
    public void A_list_position_maps_to_its_listed_slot_or_to_the_first_free_one_and_never_past_the_unlocked()
    {
        var character = Character(850);
        var saved = new Dictionary<int, int[]> { [0] = Plain(1), [2] = Plain(2) };
        var shown = ColorPaletteManager.Shown(character, 4, saved);

        Assert.Equal(0, ColorPaletteManager.SlotAt(shown, 4, 0));
        Assert.Equal(2, ColorPaletteManager.SlotAt(shown, 4, 1));
        Assert.Equal(1, ColorPaletteManager.SlotAt(shown, 4, 2));
        Assert.Null(ColorPaletteManager.SlotAt(shown, 4, 4));
        Assert.Null(ColorPaletteManager.SlotAt(shown, 4, -1));
    }

    [Fact]
    public void Without_a_saved_first_slot_a_new_palette_never_takes_the_slot_of_the_worn_colours()
    {
        var shown = ColorPaletteManager.Shown(Character(860), 2, new Dictionary<int, int[]>());

        Assert.Equal(0, ColorPaletteManager.SlotAt(shown, 2, 0));
        Assert.Equal(1, ColorPaletteManager.SlotAt(shown, 2, 1));
    }

    [Fact]
    public void Black_is_a_valid_palette_colour()
    {
        Assert.True(ColorPaletteManager.IsPalette(Plain(0)));
        Assert.False(ColorPaletteManager.IsPalette([0, 0, 0, 0, 0]));
        Assert.False(ColorPaletteManager.IsPalette([-1, 0, 0, 0, 0, 0]));
    }
}

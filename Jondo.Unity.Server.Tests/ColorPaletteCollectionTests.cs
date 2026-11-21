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
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE CharacterColorSlots (
                CharacterId INTEGER PRIMARY KEY,
                SlotsUnlocked INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE CharacterColorPalettes (
                CharacterId INTEGER NOT NULL,
                PaletteIndex INTEGER NOT NULL,
                ColorsJson TEXT NOT NULL,
                PRIMARY KEY (CharacterId, PaletteIndex)
            );
            CREATE TABLE Characters (
                Id INTEGER PRIMARY KEY,
                AccountId INTEGER NOT NULL,
                Name TEXT NOT NULL,
                Color1 INTEGER, Color2 INTEGER, Color3 INTEGER,
                Color4 INTEGER, Color5 INTEGER, Color6 INTEGER
            );
            """;
        cmd.ExecuteNonQuery();
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
}

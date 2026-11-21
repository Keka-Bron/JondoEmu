using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Protocol.Game;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The colour palettes of the appearance window's colours tab (CharacterAppearanceCollectionEvent).
    /// </summary>
    /// <remarks>
    /// Every character starts with one slot and each colour change potion (effect 851) unlocks one
    /// more, up to twenty. A slot holds six RGB colours once a palette is saved in it
    /// (<c>palette/save</c> of the control API, which JondoFix calls from the window). Slot 0, while
    /// nothing is saved there, shows the character's own colours, as the single palette did before
    /// palettes were kept. The window lists the palettes in slot order, a gap skipped and never
    /// ending the list, and draws the remaining unlocked slots as empty ones to fill. The tables
    /// come from <see cref="Database.Migrations.ColorPalettePalettesMigration"/>.
    /// </remarks>
    public static class ColorPaletteManager
    {
        public const int TotalSlots = 20;
        public const int StartingSlots = 1;
        public const int ColorsPerPalette = 6;

        /// <summary>Six colours, each a 24-bit RGB value.</summary>
        public static bool IsPalette(IReadOnlyList<int>? colors)
            => colors != null && colors.Count == ColorsPerPalette && colors.All(c => c >= 0 && c <= 0xFFFFFF);

        // ─── Against an open connection ─────────────────────────────────────────────────────

        public static void SavePaletteDirect(SqliteConnection connection, long characterId, int slot, int[] colors)
        {
            if (slot < 0 || slot >= TotalSlots)
                throw new ArgumentOutOfRangeException(nameof(slot), $"Palette slot must be between 0 and {TotalSlots - 1}.");
            if (colors == null || colors.Length != ColorsPerPalette)
                throw new ArgumentException($"A palette has exactly {ColorsPerPalette} colours.", nameof(colors));
            if (!IsPalette(colors))
                throw new ArgumentOutOfRangeException(nameof(colors), "Every colour must be a 24-bit RGB value.");

            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO CharacterColorPalettes (CharacterId, PaletteIndex, ColorsJson)
                VALUES ($id, $idx, $json)
                ON CONFLICT(CharacterId, PaletteIndex) DO UPDATE SET ColorsJson = $json;";
            command.Parameters.AddWithValue("$id", characterId);
            command.Parameters.AddWithValue("$idx", slot);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(colors));
            command.ExecuteNonQuery();
        }

        /// <summary>The palettes saved for a character, by slot; rows that do not read as a list are left out.</summary>
        public static Dictionary<int, int[]> GetPalettesDirect(SqliteConnection connection, long characterId)
        {
            var palettes = new Dictionary<int, int[]>();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT PaletteIndex, ColorsJson FROM CharacterColorPalettes WHERE CharacterId = $id;";
            command.Parameters.AddWithValue("$id", characterId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    if (JsonSerializer.Deserialize<int[]>(reader.GetString(1)) is { } colors)
                        palettes[reader.GetInt32(0)] = colors;
                }
                catch (JsonException) { }
            }
            return palettes;
        }

        public static int GetSlotsUnlockedDirect(SqliteConnection connection, long characterId)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT SlotsUnlocked FROM CharacterColorSlots WHERE CharacterId = $id;";
            command.Parameters.AddWithValue("$id", characterId);
            var value = command.ExecuteScalar();
            return value == null || value == DBNull.Value
                ? StartingSlots
                : Math.Clamp(Convert.ToInt32(value), StartingSlots, TotalSlots);
        }

        /// <summary>One more slot; false, and nothing changed, when all twenty are already open.</summary>
        public static bool TryUnlockNextSlotDirect(SqliteConnection connection, long characterId)
        {
            using var command = connection.CreateCommand();
            // One statement, so that two potions used at once cannot both read the same count.
            command.CommandText = @"
                INSERT INTO CharacterColorSlots (CharacterId, SlotsUnlocked) VALUES ($id, $first)
                ON CONFLICT(CharacterId) DO UPDATE SET SlotsUnlocked = MAX(SlotsUnlocked, $starting) + 1
                WHERE SlotsUnlocked < $total;";
            command.Parameters.AddWithValue("$id", characterId);
            command.Parameters.AddWithValue("$first", StartingSlots + 1);
            command.Parameters.AddWithValue("$starting", StartingSlots);
            command.Parameters.AddWithValue("$total", TotalSlots);
            return command.ExecuteNonQuery() > 0;
        }

        /// <summary>The window's collection, read from the given connection.</summary>
        public static CharacterAppearanceCollectionEvent BuildAppearanceEvent(SqliteConnection connection, DbCharacter character)
            => Collection(character, GetSlotsUnlockedDirect(connection, character.Id), GetPalettesDirect(connection, character.Id));

        // ─── What the window shows ──────────────────────────────────────────────────────────

        /// <summary>
        /// The palettes the window lists, in order, with the slot each one is kept in: the valid
        /// saved ones of the unlocked slots, a gap skipped, and slot 0 as the character's own
        /// colours while nothing is saved there.
        /// </summary>
        public static List<(int Slot, IReadOnlyList<int> Colors)> Shown(
            DbCharacter character, int unlocked, IReadOnlyDictionary<int, int[]> saved)
        {
            var shown = new List<(int, IReadOnlyList<int>)>();
            for (int slot = 0; slot < Math.Clamp(unlocked, StartingSlots, TotalSlots); slot++)
            {
                if (saved.TryGetValue(slot, out var colors) && IsPalette(colors)) shown.Add((slot, colors));
                else if (slot == 0) shown.Add((0, OwnColors(character)));
            }
            return shown;
        }

        /// <summary>
        /// The slot a palette chosen at a position of the window's list is kept in: the one listed
        /// there, or for an empty slot after the list the first slot with nothing kept in it.
        /// Null past the unlocked slots.
        /// </summary>
        public static int? SlotAt(IReadOnlyList<(int Slot, IReadOnlyList<int> Colors)> shown, int unlocked, int position)
        {
            if (position < 0 || position >= Math.Min(unlocked, TotalSlots)) return null;
            if (position < shown.Count) return shown[position].Slot;
            var taken = shown.Select(p => p.Slot).ToHashSet();
            for (int slot = 0; slot < Math.Min(unlocked, TotalSlots); slot++)
                if (!taken.Contains(slot)) return slot;
            return null;
        }

        public static CharacterAppearanceCollectionEvent Collection(
            DbCharacter character, int unlocked, IReadOnlyDictionary<int, int[]> saved)
            => new(new Jondo.Unity.Protocol.Game.CharacterColors(Math.Clamp(unlocked, StartingSlots, TotalSlots),
                Shown(character, unlocked, saved).Select(p => new ColorPalette(p.Colors)).ToArray(), TotalSlots));

        private static IReadOnlyList<int> OwnColors(DbCharacter character)
            => Network.InitialPresentationMessages.Palette(CharacterLookBuilder.SavedColors(character));

        // ─── Against the world database ─────────────────────────────────────────────────────

        private static T World<T>(Func<SqliteConnection, T> read, T fallback, string what)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                return read(connection);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ColorPalettes] Could not {what}: {ex.Message}");
                return fallback;
            }
        }

        public static int GetSlotsUnlocked(long characterId)
            => World(c => GetSlotsUnlockedDirect(c, characterId), StartingSlots, $"read the slots of {characterId}");

        /// <summary>One more slot for a colour change potion; false when none is left to open.</summary>
        public static bool UnlockSlot(long characterId)
            => World(c => TryUnlockNextSlotDirect(c, characterId), false, $"unlock a slot for {characterId}");

        /// <summary>
        /// A palette saved from the window's list position: the slot it went to, or null when
        /// the position is past the unlocked slots or the colours are not six RGB values.
        /// </summary>
        public static int? SaveShown(DbCharacter character, int position, int[] colors)
        {
            if (!IsPalette(colors)) return null;
            return World(connection =>
            {
                int unlocked = GetSlotsUnlockedDirect(connection, character.Id);
                var shown = Shown(character, unlocked, GetPalettesDirect(connection, character.Id));
                if (SlotAt(shown, unlocked, position) is not int slot) return (int?)null;
                SavePaletteDirect(connection, character.Id, slot, colors);
                return slot;
            }, null, $"save a palette for {character.Id}");
        }

        public static CharacterAppearanceCollectionEvent BuildAppearanceEvent(DbCharacter character)
            => World(c => BuildAppearanceEvent(c, character),
                     Collection(character, StartingSlots, new Dictionary<int, int[]>()),
                     $"read the palettes of {character.Id}");
    }
}

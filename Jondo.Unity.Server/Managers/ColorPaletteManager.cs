using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Protocol.Game;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Manages character color palettes and unlocked palette slots for Dofus Unity.
    /// Supports up to 20 color palette slots per character.
    /// Using color change potions unlocks additional palette slots.
    /// </summary>
    public static class ColorPaletteManager
    {
        public const int TotalSlots = 20;
        private static readonly object _lock = new object();
        private static bool _initialized;

        public static void SavePaletteDirect(SqliteConnection connection, long characterId, int slot, int[] colors)
        {
            if (slot < 0 || slot >= TotalSlots)
                throw new ArgumentOutOfRangeException(nameof(slot), $"Palette slot must be between 0 and {TotalSlots - 1}.");

            if (colors == null || colors.Length != 6)
                throw new ArgumentException("Palette must contain exactly 6 colors.", nameof(colors));

            foreach (var color in colors)
            {
                if (color < -1 || color > 0xFFFFFF)
                    throw new ArgumentOutOfRangeException(nameof(colors), $"Color {color} is outside valid 24-bit RGB range [0..0xFFFFFF] or -1.");
            }

            string json = JsonSerializer.Serialize(colors);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO CharacterColorPalettes (CharacterId, PaletteIndex, ColorsJson)
                VALUES ($id, $idx, $json)
                ON CONFLICT(CharacterId, PaletteIndex) DO UPDATE SET ColorsJson = $json;
            ";
            cmd.Parameters.AddWithValue("$id", characterId);
            cmd.Parameters.AddWithValue("$idx", slot);
            cmd.Parameters.AddWithValue("$json", json);
            cmd.ExecuteNonQuery();
        }

        public static Dictionary<int, int[]> GetPalettesDirect(SqliteConnection connection, long characterId)
        {
            var palettes = new Dictionary<int, int[]>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT PaletteIndex, ColorsJson FROM CharacterColorPalettes WHERE CharacterId = $id ORDER BY PaletteIndex ASC;";
            cmd.Parameters.AddWithValue("$id", characterId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int idx = reader.GetInt32(0);
                string json = reader.GetString(1);
                var cols = JsonSerializer.Deserialize<int[]>(json);
                if (cols != null) palettes[idx] = cols;
            }
            return palettes;
        }

        public static int GetSlotsUnlockedDirect(SqliteConnection connection, long characterId)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT SlotsUnlocked FROM CharacterColorSlots WHERE CharacterId = $id;";
            cmd.Parameters.AddWithValue("$id", characterId);
            var val = cmd.ExecuteScalar();
            if (val != null && val != DBNull.Value)
            {
                return Math.Clamp(Convert.ToInt32(val), 1, TotalSlots);
            }
            return 1;
        }

        public static bool TryUnlockNextSlotDirect(SqliteConnection connection, long characterId)
        {
            int current = GetSlotsUnlockedDirect(connection, characterId);
            if (current >= TotalSlots) return false;

            int next = current + 1;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO CharacterColorSlots (CharacterId, SlotsUnlocked)
                VALUES ($id, $slots)
                ON CONFLICT(CharacterId) DO UPDATE SET SlotsUnlocked = $slots;
            ";
            cmd.Parameters.AddWithValue("$id", characterId);
            cmd.Parameters.AddWithValue("$slots", next);
            cmd.ExecuteNonQuery();
            return true;
        }

        private static void EnsureTables()
        {
            if (_initialized) return;
            lock (_lock)
            {
                if (_initialized) return;
                try
                {
                    using var conn = new SqliteConnection(DatabaseManager.WorldConnectionString);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS CharacterColorSlots (
                            CharacterId INTEGER PRIMARY KEY,
                            SlotsUnlocked INTEGER NOT NULL DEFAULT 1
                        );
                        CREATE TABLE IF NOT EXISTS CharacterColorPalettes (
                            CharacterId INTEGER NOT NULL,
                            PaletteIndex INTEGER NOT NULL,
                            ColorsJson TEXT NOT NULL,
                            PRIMARY KEY(CharacterId, PaletteIndex)
                        );
                    ";
                    cmd.ExecuteNonQuery();
                    _initialized = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ColorPalettes] Failed to ensure tables: {ex.Message}");
                }
            }
        }

        public static int GetSlotsUnlocked(long characterId)
        {
            EnsureTables();
            try
            {
                using var conn = new SqliteConnection(DatabaseManager.WorldConnectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT SlotsUnlocked FROM CharacterColorSlots WHERE CharacterId = $id;";
                cmd.Parameters.AddWithValue("$id", characterId);
                var val = cmd.ExecuteScalar();
                if (val != null && val != DBNull.Value)
                {
                    return Math.Clamp(Convert.ToInt32(val), 1, TotalSlots);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ColorPalettes] Error getting unlocked slots for {characterId}: {ex.Message}");
            }
            return 1;
        }

        public static int UnlockSlot(long characterId, DbCharacter character)
        {
            EnsureTables();
            int current = GetSlotsUnlocked(characterId);
            if (current >= TotalSlots) return TotalSlots;

            int next = current + 1;
            try
            {
                using var conn = new SqliteConnection(DatabaseManager.WorldConnectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO CharacterColorSlots (CharacterId, SlotsUnlocked)
                    VALUES ($id, $slots)
                    ON CONFLICT(CharacterId) DO UPDATE SET SlotsUnlocked = $slots;
                ";
                cmd.Parameters.AddWithValue("$id", characterId);
                cmd.Parameters.AddWithValue("$slots", next);
                cmd.ExecuteNonQuery();

                Console.WriteLine($"[ColorPalettes] Character {character.Name} ({characterId}) unlocked palette slot {next}/{TotalSlots}.");
                return next;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ColorPalettes] Error unlocking slot for {characterId}: {ex.Message}");
                return current;
            }
        }

        public static void SavePalette(long characterId, int paletteIndex, IReadOnlyList<int> colors)
        {
            EnsureTables();
            try
            {
                string json = JsonSerializer.Serialize(colors);
                using var conn = new SqliteConnection(DatabaseManager.WorldConnectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO CharacterColorPalettes (CharacterId, PaletteIndex, ColorsJson)
                    VALUES ($id, $idx, $json)
                    ON CONFLICT(CharacterId, PaletteIndex) DO UPDATE SET ColorsJson = $json;
                ";
                cmd.Parameters.AddWithValue("$id", characterId);
                cmd.Parameters.AddWithValue("$idx", paletteIndex);
                cmd.Parameters.AddWithValue("$json", json);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ColorPalettes] Error saving palette {paletteIndex} for {characterId}: {ex.Message}");
            }
        }

        public static int FindMatchingPaletteIndex(long characterId, IReadOnlyList<long> colors)
        {
            EnsureTables();
            try
            {
                using var conn = new SqliteConnection(DatabaseManager.WorldConnectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT PaletteIndex, ColorsJson FROM CharacterColorPalettes WHERE CharacterId = $id ORDER BY PaletteIndex ASC;";
                cmd.Parameters.AddWithValue("$id", characterId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int idx = reader.GetInt32(0);
                    string json = reader.GetString(1);
                    var cols = JsonSerializer.Deserialize<List<int>>(json);
                    if (cols != null && cols.Count == colors.Count)
                    {
                        bool match = true;
                        for (int i = 0; i < cols.Count; i++)
                        {
                            if (cols[i] != (int)colors[i]) { match = false; break; }
                        }
                        if (match) return idx;
                    }
                }
            }
            catch { }
            return -1;
        }

        public static List<ColorPalette> GetPalettes(DbCharacter character)
        {
            EnsureTables();
            int unlocked = GetSlotsUnlocked(character.Id);
            var result = new List<ColorPalette>();

            var defaultColors = Network.InitialPresentationMessages.Palette(CharacterLookBuilder.SavedColors(character));

            // Load saved palettes from database
            var savedPalettes = new Dictionary<int, List<int>>();
            try
            {
                using var conn = new SqliteConnection(DatabaseManager.WorldConnectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT PaletteIndex, ColorsJson FROM CharacterColorPalettes WHERE CharacterId = $id ORDER BY PaletteIndex ASC;";
                cmd.Parameters.AddWithValue("$id", character.Id);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int idx = reader.GetInt32(0);
                    string json = reader.GetString(1);
                    var cols = JsonSerializer.Deserialize<List<int>>(json);
                    if (cols != null && cols.Count > 0) savedPalettes[idx] = cols;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ColorPalettes] Error reading palettes for {character.Id}: {ex.Message}");
            }

            // Slot 0 always reflects the character's active look colors.
            savedPalettes[0] = defaultColors.ToList();

            for (int i = 0; i < unlocked; i++)
            {
                if (savedPalettes.TryGetValue(i, out var cols) && cols != null && cols.Count > 0 && !cols.Contains(-1))
                {
                    result.Add(new ColorPalette(cols));
                }
                else
                {
                    // An unlocked slot that has not been configured yet must NOT be sent in ColorPalettes!
                    // Dofus client handles unconfigured slots as draftable slots (data = null).
                    break;
                }
            }

            return result;
        }

        public static CharacterAppearanceCollectionEvent BuildAppearanceEvent(DbCharacter character)
        {
            int unlocked = GetSlotsUnlocked(character.Id);
            var palettes = GetPalettes(character);
            return new CharacterAppearanceCollectionEvent(new Jondo.Unity.Protocol.Game.CharacterColors(unlocked, palettes, TotalSlots));
        }
    }
}

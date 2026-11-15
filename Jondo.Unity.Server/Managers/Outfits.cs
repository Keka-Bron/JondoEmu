using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The appearance window's outfits: whole looks kept under a name and an icon, put on with one
    /// click of their tile.
    /// </summary>
    /// <remarks>
    /// The outfit being WORN is not kept here but in the live tables everything else reads -- the
    /// head in Characters, the body, idle, title and ornament in CharacterWardrobe, the garments in
    /// CharacterAppearance, the colours in Color1 through Color6 --; its row here holds only its name, icon and
    /// number. The OTHER outfits are kept whole in their rows. Putting one on swaps: what is worn
    /// is written into its own row, and the chosen row into the live tables.
    /// </remarks>
    public static class Outfits
    {
        /// <summary>No icon picked: the f15 the worn outfit carries.</summary>
        public const int NoIcon = -1;

        /// <summary>Longest name kept; the window's own field is shorter.</summary>
        public const int MaxNameLength = 50;

        /// <summary>How many outfits a character may keep, the worn one included.</summary>
        public const int MaxOutfits = 100;

        /// <summary>What an outfit puts on: everything the appearance window's tabs change.</summary>
        public sealed record Look(int Head, int Body, int Idle, List<long> Colors, int Title, int Ornament,
                                  List<Wardrobe.Worn> Garments, int Aura = 0);

        /// <summary>
        /// One outfit. <see cref="Kept"/> is its look when it is not the one worn; the worn one's is
        /// read from the live tables.
        /// </summary>
        public sealed record Outfit(string Uuid, int Number, string Name, int Icon, bool Favorite,
                                    DateTime Created, Look? Kept);

        /// <summary>The tables, made the first time they are needed: a base the server never opened has none.</summary>
        private static readonly Lazy<bool> _tables = new(() => { Wardrobe.Initialize(); Initialize(); return true; });

        public static void Initialize()
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CharacterOutfits (
                        Uuid        TEXT PRIMARY KEY,
                        CharacterId INTEGER NOT NULL,
                        Number      INTEGER NOT NULL,
                        Name        TEXT NOT NULL DEFAULT '',
                        Icon        INTEGER NOT NULL DEFAULT -1,
                        Favorite    INTEGER NOT NULL DEFAULT 0,
                        Created     TEXT NOT NULL,
                        Look        TEXT NOT NULL DEFAULT '');";
                command.ExecuteNonQuery();

                // Which outfit is worn; empty is the character's first one.
                try
                {
                    var puesto = connection.CreateCommand();
                    puesto.CommandText =
                        "ALTER TABLE CharacterWardrobe ADD COLUMN EquippedOutfit TEXT NOT NULL DEFAULT '';";
                    puesto.ExecuteNonQuery();
                }
                catch (SqliteException)
                {
                    // it was already there
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Outfits] Could not create the tables: {ex.Message}");
            }
        }

        /// <summary>The worn outfit's uuid; the character's first one until another is put on.</summary>
        public static string EquippedOf(long characterId)
        {
            _ = _tables.Value;
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT EquippedOutfit FROM CharacterWardrobe WHERE CharacterId = $id;";
                command.Parameters.AddWithValue("$id", characterId);
                if (command.ExecuteScalar() is string uuid && uuid.Length > 0) return uuid;
            }
            catch (Exception)
            {
                // the column comes with Initialize; before it the first outfit is the worn one
            }
            return Network.ConnectionProtocol.OutfitIdOf(characterId);
        }

        /// <summary>
        /// Every outfit of the character, the worn one first. The worn one gets its row the first
        /// time it is asked for.
        /// </summary>
        public static List<Outfit> Of(long characterId)
        {
            string worn = EquippedOf(characterId);
            var outfits = Read(characterId);
            if (!outfits.Any(o => o.Uuid == worn))
            {
                var first = new Outfit(worn, NextNumber(outfits), "", NoIcon, false, DateTime.UtcNow, null);
                Insert(characterId, first);
                outfits.Add(first);
            }
            return outfits.OrderBy(o => o.Uuid == worn ? 0 : 1).ThenBy(o => o.Number).ToList();
        }

        /// <summary>The look the character wears now, read from the live tables.</summary>
        public static Look Worn(DbCharacter character)
        {
            var (title, ornament) = Wardrobe.Of(character.Id);
            return new Look(character.HeadId, Wardrobe.BodyOf(character.Id), Wardrobe.IdleOf(character.Id),
                            CharacterLookBuilder.SavedColors(character), title, ornament,
                            Wardrobe.AppearanceOf(character.Id), Wardrobe.AuraOf(character.Id));
        }

        /// <summary>A new outfit holding that look; null when the character has no room for another.</summary>
        public static Outfit? Create(long characterId, string name, int icon, Look look)
        {
            var outfits = Of(characterId);
            if (outfits.Count >= MaxOutfits) return null;

            var outfit = new Outfit(Guid.NewGuid().ToString(), NextNumber(outfits), Clean(name), icon, false,
                                    DateTime.UtcNow, look);
            return Insert(characterId, outfit) ? outfit : null;
        }

        /// <summary>The name, icon and favourite mark of one of the character's outfits.</summary>
        public static bool Update(long characterId, string uuid, string name, int icon, bool favorite)
        {
            Of(characterId);   // the worn one may not have its row yet
            return Execute("UPDATE CharacterOutfits SET Name = $name, Icon = $icon, Favorite = $fav " +
                           "WHERE Uuid = $uuid AND CharacterId = $id;",
                           ("$name", Clean(name)), ("$icon", icon), ("$fav", favorite ? 1 : 0),
                           ("$uuid", uuid), ("$id", characterId)) > 0;
        }

        /// <summary>Throws away one of the character's outfits; never the one worn.</summary>
        public static bool Delete(long characterId, string uuid)
        {
            if (uuid == EquippedOf(characterId)) return false;
            return Execute("DELETE FROM CharacterOutfits WHERE Uuid = $uuid AND CharacterId = $id;",
                           ("$uuid", uuid), ("$id", characterId)) > 0;
        }

        /// <summary>
        /// Puts on one of the character's kept outfits: what is worn goes into its own row, the
        /// chosen one into the live tables. Garments the account no longer owns are left off.
        /// </summary>
        public static bool Equip(DbCharacter character, string uuid, Func<int, bool> owns)
        {
            var outfits = Of(character.Id);
            string worn = outfits[0].Uuid;
            if (uuid == worn) return true;

            var chosen = outfits.FirstOrDefault(o => o.Uuid == uuid);
            if (chosen?.Kept == null) return false;

            if (!Keep(character.Id, worn, Worn(character))) return false;
            Apply(character, chosen.Kept, owns);
            Keep(character.Id, uuid, null);
            Execute("INSERT INTO CharacterWardrobe (CharacterId, EquippedOutfit) VALUES ($id, $uuid) " +
                    "ON CONFLICT(CharacterId) DO UPDATE SET EquippedOutfit = $uuid;",
                    ("$id", character.Id), ("$uuid", uuid));
            return true;
        }

        /// <summary>Writes a look into the live tables, the colours last so the look is built whole.</summary>
        private static void Apply(DbCharacter character, Look look, Func<int, bool> owns)
        {
            var colors = CharacterColors.FromIndexed(look.Colors);
            if (look.Head > 0) DatabaseManager.SetCharacterHead(character.Id, look.Head);
            Wardrobe.SaveBody(character.Id, look.Body);
            Wardrobe.SaveIdle(character.Id, look.Idle);
            Wardrobe.SaveTitle(character.Id, look.Title);
            Wardrobe.SaveOrnament(character.Id, look.Ornament);
            Wardrobe.SaveAura(character.Id, look.Aura == 0 || Emotes.IsAura(look.Aura) ? look.Aura : 0);

            Wardrobe.TakeOff(character.Id, -1);
            foreach (var garment in look.Garments)
            {
                if (garment.Gid != 0)
                {
                    if (!owns(garment.Gid)) continue;
                    Wardrobe.Wear(character.Id, garment.Slot, garment.Uid, garment.Gid);
                }
                if (garment.Hidden) Wardrobe.SetHidden(character.Id, garment.Slot, true);
            }

            DatabaseManager.SaveCharacterColors(character.Id, colors);
        }

        private static List<Outfit> Read(long characterId)
        {
            var outfits = new List<Outfit>();
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT Uuid, Number, Name, Icon, Favorite, Created, Look " +
                                      "FROM CharacterOutfits WHERE CharacterId = $id;";
                command.Parameters.AddWithValue("$id", characterId);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    DateTime.TryParse(reader.GetString(5), null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var created);
                    outfits.Add(new Outfit(reader.GetString(0), reader.GetInt32(1), reader.GetString(2),
                                           reader.GetInt32(3), reader.GetInt32(4) != 0, created,
                                           Parse(reader.GetString(6))));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Outfits] Could not read the outfits of {characterId}: {ex.Message}");
            }
            return outfits;
        }

        private static bool Insert(long characterId, Outfit outfit)
            => Execute("INSERT INTO CharacterOutfits (Uuid, CharacterId, Number, Name, Icon, Favorite, Created, Look) " +
                       "VALUES ($uuid, $id, $n, $name, $icon, $fav, $at, $look);",
                       ("$uuid", outfit.Uuid), ("$id", characterId), ("$n", outfit.Number),
                       ("$name", outfit.Name), ("$icon", outfit.Icon), ("$fav", outfit.Favorite ? 1 : 0),
                       ("$at", outfit.Created.ToString("O")), ("$look", Serialize(outfit.Kept))) > 0;

        private static bool Keep(long characterId, string uuid, Look? look)
            => Execute("UPDATE CharacterOutfits SET Look = $look WHERE Uuid = $uuid AND CharacterId = $id;",
                       ("$look", Serialize(look)), ("$uuid", uuid), ("$id", characterId)) > 0;

        private static int NextNumber(List<Outfit> outfits) => outfits.Count == 0 ? 1 : outfits.Max(o => o.Number) + 1;

        private static string Clean(string name)
        {
            name = (name ?? "").Trim();
            return name.Length > MaxNameLength ? name[..MaxNameLength] : name;
        }

        private sealed record Stored(int Head, int Body, int Idle, List<long> Colors, int Title, int Ornament,
                                     List<StoredGarment> Garments, int Aura = 0);

        private sealed record StoredGarment(int Slot, long Uid, int Gid, bool Hidden);

        private static string Serialize(Look? look)
            => look == null ? "" : JsonSerializer.Serialize(new Stored(look.Head, look.Body, look.Idle, look.Colors,
                look.Title, look.Ornament,
                look.Garments.Select(g => new StoredGarment(g.Slot, g.Uid, g.Gid, g.Hidden)).ToList(), look.Aura));

        private static Look? Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                var stored = JsonSerializer.Deserialize<Stored>(text);
                if (stored == null) return null;
                return new Look(stored.Head, stored.Body, stored.Idle, stored.Colors ?? new List<long>(),
                                stored.Title, stored.Ornament,
                                (stored.Garments ?? new List<StoredGarment>())
                                    .Select(g => new Wardrobe.Worn(g.Slot, g.Uid, g.Gid, g.Hidden)).ToList(),
                                stored.Aura);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static int Execute(string sql, params (string Name, object Value)[] parameters)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
                return command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Outfits] {ex.Message}");
                return 0;
            }
        }
    }
}

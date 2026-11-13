using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The titles and ornaments that exist in the game.
    ///
    /// The title is the text shown under the name; the ornament, the frame around it. The
    /// client already has the whole catalogue in its data —539 titles and 167 ornaments— and what it
    /// expects from the server is only the list of the ones one HAS, which it sends once on entering:
    ///
    ///   hhy { f1: [titles], f2: [ornaments] }     both packed
    ///
    /// What is not in that list it draws in grey. Here they all go, which is what is asked.
    ///
    /// The ids come from titles_ornaments.json, which tools/extract_titulos.py generates by reading the
    /// client's `titles` and `ornaments` tables.
    /// </summary>
    public static class Titles
    {
        private static readonly List<long> _titles = new List<long>();
        private static readonly List<long> _ornaments = new List<long>();

        /// <summary>
        /// Whether the lists are read in and safe to use. Volatile because the fast path in
        /// <see cref="Ensure"/> reads it outside the lock, and raised LAST.
        /// </summary>
        private static volatile bool _loaded;
        private static readonly object _lock = new object();
        private static volatile bool _ownershipReady;

        public static IReadOnlyList<long> All { get { Ensure(); return _titles; } }
        public static IReadOnlyList<long> AllOrnaments { get { Ensure(); return _ornaments; } }

        /// <summary>
        /// Reads the file, once per run. Kept as a separate call so the server pays for it at
        /// boot, with its log line, rather than on the first title somebody puts on.
        /// </summary>
        /// <remarks>
        /// Calling it again does nothing, on purpose: titles_ornaments.json comes out of the
        /// client and does not change while the server is up.
        /// </remarks>
        public static void Initialize()
        {
            Ensure();
            InitializeOwnership();
        }

        private static void InitializeOwnership()
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CharacterTitles (
                        CharacterId INTEGER NOT NULL,
                        TitleId INTEGER NOT NULL,
                        PRIMARY KEY (CharacterId, TitleId));

                    INSERT OR IGNORE INTO CharacterTitles (CharacterId, TitleId)
                    SELECT CharacterId, TitleId FROM CharacterWardrobe
                    WHERE TitleId != 0;";
                command.ExecuteNonQuery();
                _ownershipReady = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Titles] Could not initialize title ownership: {ex.Message}");
            }
        }

        /// <summary>Whether this character knows a title; null means ownership data is unavailable.</summary>
        public static bool? Knows(long characterId, int titleId)
        {
            if (!_ownershipReady || characterId == 0 || titleId <= 0) return null;
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1 FROM CharacterTitles WHERE CharacterId = $c AND TitleId = $t;";
                command.Parameters.AddWithValue("$c", characterId);
                command.Parameters.AddWithValue("$t", titleId);
                return command.ExecuteScalar() != null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Titles] Could not read title ownership: {ex.Message}");
                return null;
            }
        }

        /// <summary>Records titles granted by an authoritative character reward.</summary>
        public static void Grant(long characterId, IEnumerable<int> titleIds)
        {
            if (!_ownershipReady || characterId == 0) return;
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var transaction = connection.BeginTransaction();
                foreach (int titleId in titleIds)
                {
                    if (titleId <= 0) continue;
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "INSERT OR IGNORE INTO CharacterTitles (CharacterId, TitleId) VALUES ($c, $t);";
                    command.Parameters.AddWithValue("$c", characterId);
                    command.Parameters.AddWithValue("$t", titleId);
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Titles] Could not grant title ownership: {ex.Message}");
            }
        }

        /// <summary>The owned title ids, for the character's wardrobe response.</summary>
        public static IReadOnlyList<long> OwnedBy(long characterId)
        {
            var owned = new List<long>();
            if (!_ownershipReady || characterId == 0) return owned;
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT TitleId FROM CharacterTitles WHERE CharacterId = $c ORDER BY TitleId;";
                command.Parameters.AddWithValue("$c", characterId);
                using var reader = command.ExecuteReader();
                while (reader.Read()) owned.Add(reader.GetInt64(0));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Titles] Could not list owned titles: {ex.Message}");
            }
            return owned;
        }

        private static void Ensure()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try
                {
                    Load();
                }
                finally
                {
                    _loaded = true;   // in a finally so a missing file counts as tried
                }
            }
        }

        private static void Load()
        {
            string path = Paths.TitlesOrnamentsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Titles] {Path.GetFileName(path)} is missing; there will be neither titles nor " +
                                  "ornaments. Generate it with tools/extract_titulos.py.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                Read(doc.RootElement, "titles", _titles);
                Read(doc.RootElement, "ornaments", _ornaments);

                Console.WriteLine($"[Titles] {_titles.Count} titles and {_ornaments.Count} ornaments.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Titles] Could not read {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        private static void Read(JsonElement root, string name, List<long> into)
        {
            if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return;
            foreach (var entry in array.EnumerateArray())
            {
                if (entry.TryGetInt64(out long id)) into.Add(id);
            }
        }

        public static bool HasTitle(int id)
        {
            if (id == Wardrobe.None) return true;
            Ensure();
            return _titles.Contains(id);
        }

        public static bool HasOrnament(int id)
        {
            if (id == Wardrobe.None) return true;
            Ensure();
            return _ornaments.Contains(id);
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The character's builds, the «Conjuntos» window (the client's CharacterPresetUi): each one a
    /// name and an icon, the items it wears, the characteristic points it spends and the spells it
    /// puts on the bar.
    /// </summary>
    /// <remarks>
    /// MEASURED in «Conjuntos/crear nuevo conjunto…» and «Conjuntos/eliminar conjunto»: the window
    /// edits a copy and only its «Guardar» (ihs) keeps it; closing or resetting without saving sends
    /// igo, which throws the copy away. That is why every edit here goes to a draft held for the
    /// session, and only <see cref="Save"/> writes. Creating (igp), updating from what the character
    /// wears (iin) and deleting (iiy) are written at once. Kept in world.db:
    /// <code>
    ///   CharacterPresets (CharacterId, Uuid, Position, Data)   Data is the preset as JSON
    /// </code>
    /// What a preset looks like and what its sheet says are not kept: they are worked out from its
    /// items and points each time it travels (PresetProtocol).
    /// </remarks>
    public static class CharacterPresets
    {
        /// <summary>No more than this many builds a character: the window has no limit of its own we know of.</summary>
        public const int MaxPerCharacter = 100;

        /// <summary>The name of the character's own state, the first card of the window (ifz).</summary>
        public const string PlayerStateName = "PLAYER_STATE";

        public sealed class Preset
        {
            public string Uuid { get; set; } = "";
            public string Name { get; set; } = "";
            public int Icon { get; set; }
            public string Created { get; set; } = "";
            public int Position { get; set; }

            /// <summary>Equipment slot -> item uid.</summary>
            public SortedDictionary<int, long> Items { get; set; } = new();

            /// <summary>Characteristic -> allocated stat value, before scrolls and equipment; cost is calculated separately.</summary>
            public SortedDictionary<int, int> Points { get; set; } = new();

            /// <summary>Spell -> the bar slots it sits in; 0 is the weapon.</summary>
            public SortedDictionary<int, List<int>> Spells { get; set; } = new();

            /// <summary>The outfit put on with the build (its uuid); empty for none.</summary>
            public string Outfit { get; set; } = "";

            public Preset Copy() => new Preset
            {
                Uuid = Uuid, Name = Name, Icon = Icon, Created = Created, Position = Position, Outfit = Outfit,
                Items = new SortedDictionary<int, long>(Items),
                Points = new SortedDictionary<int, int>(Points),
                Spells = new SortedDictionary<int, List<int>>(Spells.ToDictionary(s => s.Key, s => s.Value.ToList())),
            };
        }

        /// <summary>The window's edits not saved yet, by character and preset.</summary>
        private static readonly ConcurrentDictionary<(long Character, string Uuid), Preset> _drafts = new();

        private static readonly object _gate = new object();
        private static volatile bool _tableReady;

        /// <summary>An ISO date as the window's f6 carries it: 2026-08-09T18:20:27.527350937Z.</summary>
        public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffff") + "Z";

        /// <summary>The character's saved builds, in the order they were made.</summary>
        public static List<Preset> Of(long characterId)
        {
            var presets = new List<Preset>();
            if (characterId <= 0) return presets;
            try
            {
                EnsureTable();
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Data FROM CharacterPresets WHERE CharacterId = $c ORDER BY Position;";
                command.Parameters.AddWithValue("$c", characterId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var preset = JsonSerializer.Deserialize<Preset>(reader.GetString(0));
                    if (preset != null && !string.IsNullOrEmpty(preset.Uuid)) presets.Add(preset);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Presets] Could not read {characterId}'s builds: {ex.Message}");
            }
            return presets;
        }

        /// <summary>One saved build, or null.</summary>
        public static Preset? Find(long characterId, string uuid)
            => Of(characterId).FirstOrDefault(p => p.Uuid == uuid);

        /// <summary>The build as the window is editing it: its draft, or a fresh copy of the saved one.</summary>
        public static Preset? ForEdit(long characterId, string uuid)
        {
            if (_drafts.TryGetValue((characterId, uuid), out var draft)) return draft;
            var saved = Find(characterId, uuid);
            if (saved == null) return null;
            return _drafts.GetOrAdd((characterId, uuid), saved.Copy());
        }

        /// <summary>Whether the window has edits of this build not saved yet.</summary>
        public static bool HasDraft(long characterId, string uuid) => _drafts.ContainsKey((characterId, uuid));

        /// <summary>A new build, written at once. Null when the character already has too many.</summary>
        public static Preset? Create(long characterId, Preset preset)
        {
            var existing = Of(characterId);
            if (existing.Count >= MaxPerCharacter) return null;
            preset.Uuid = Guid.NewGuid().ToString();
            preset.Created = Now();
            preset.Position = existing.Count == 0 ? 0 : existing.Max(p => p.Position) + 1;
            return Write(characterId, preset) ? preset : null;
        }

        /// <summary>The window's «Guardar»: the draft becomes the build. False with nothing to save.</summary>
        public static bool Save(long characterId, string uuid)
        {
            if (!_drafts.TryRemove((characterId, uuid), out var draft))
                return Find(characterId, uuid) != null;
            return Write(characterId, draft);
        }

        /// <summary>
        /// The outfit a build puts on, on the saved build and on its draft alike: it is picked
        /// from the outfits list, not edited with the rest. Null when there is no such build.
        /// </summary>
        public static Preset? SetOutfit(long characterId, string uuid, string outfit)
        {
            var saved = Find(characterId, uuid);
            if (saved == null) return null;
            saved.Outfit = outfit;
            if (_drafts.TryGetValue((characterId, uuid), out var draft)) draft.Outfit = outfit;
            return Write(characterId, saved) ? saved : null;
        }

        /// <summary>The window's edits thrown away (igo).</summary>
        public static void Discard(long characterId, string uuid) => _drafts.TryRemove((characterId, uuid), out _);

        /// <summary>Overwrites a saved build, the draft with it.</summary>
        public static bool Replace(long characterId, Preset preset)
        {
            _drafts.TryRemove((characterId, preset.Uuid), out _);
            return Write(characterId, preset);
        }

        /// <summary>Deletes a build. False when there was none.</summary>
        public static bool Delete(long characterId, string uuid)
        {
            _drafts.TryRemove((characterId, uuid), out _);
            try
            {
                EnsureTable();
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM CharacterPresets WHERE CharacterId = $c AND Uuid = $u;";
                command.Parameters.AddWithValue("$c", characterId);
                command.Parameters.AddWithValue("$u", uuid);
                return command.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Presets] Could not delete {characterId}'s build {uuid}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Forgets a character's builds and drafts, for the tests and a deleted character.</summary>
        internal static void Forget(long characterId)
        {
            foreach (var key in _drafts.Keys.Where(k => k.Character == characterId).ToList())
                _drafts.TryRemove(key, out _);
            try
            {
                EnsureTable();
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM CharacterPresets WHERE CharacterId = $c;";
                command.Parameters.AddWithValue("$c", characterId);
                command.ExecuteNonQuery();
            }
            catch (Exception)
            {
                // nothing saved, nothing to forget
            }
        }

        private static bool Write(long characterId, Preset preset)
        {
            try
            {
                EnsureTable();
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT OR REPLACE INTO CharacterPresets (CharacterId, Uuid, Position, Data) " +
                                      "VALUES ($c, $u, $p, $d);";
                command.Parameters.AddWithValue("$c", characterId);
                command.Parameters.AddWithValue("$u", preset.Uuid);
                command.Parameters.AddWithValue("$p", preset.Position);
                command.Parameters.AddWithValue("$d", JsonSerializer.Serialize(preset));
                command.ExecuteNonQuery();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Presets] Could not save {characterId}'s build {preset.Uuid}: {ex.Message}");
                return false;
            }
        }

        private static void EnsureTable()
        {
            if (_tableReady) return;
            lock (_gate)
            {
                if (_tableReady) return;
                using var connection = Open();
                using var create = connection.CreateCommand();
                create.CommandText = "CREATE TABLE IF NOT EXISTS CharacterPresets (" +
                                     "CharacterId INTEGER NOT NULL, Uuid TEXT NOT NULL, Position INTEGER NOT NULL, " +
                                     "Data TEXT NOT NULL, PRIMARY KEY (CharacterId, Uuid));";
                create.ExecuteNonQuery();
                _tableReady = true;
            }
        }

        private static SqliteConnection Open()
        {
            var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            return connection;
        }
    }
}

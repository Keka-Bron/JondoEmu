using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What each item effect actually does to the character sheet.
    ///
    /// The Effects table of world.db, which comes from the client's own data, says it outright:
    ///
    ///   125 -> characteristic 11, bonus     "+N vitalidad"
    ///   111 -> characteristic  1, bonus     "+N PA"
    ///   755 -> characteristic 79, MALUS     "-N placaje"
    ///
    /// BonusType is 1 or -1 and it is not decoration: an item that takes lock away carries a
    /// positive number under an effect that subtracts, and adding it would read as a bonus.
    /// </summary>
    public static class EffectTable
    {
        public readonly struct Effect
        {
            public Effect(int characteristic, int sign) { Characteristic = characteristic; Sign = sign; }
            public int Characteristic { get; }
            /// <summary>1 when the effect adds, -1 when it takes away.</summary>
            public int Sign { get; }
        }

        private static readonly Dictionary<int, Effect> _byId = new Dictionary<int, Effect>();

        /// <summary>
        /// Whether the table is read in and safe to use. Volatile because the fast path in
        /// <see cref="Ensure"/> reads it outside the lock, and raised LAST.
        /// </summary>
        private static volatile bool _loaded;
        private static readonly object _lock = new object();

        /// <summary>
        /// Reads the effects, once per run. Kept as a separate call so the server pays for the
        /// query at boot, with its log line, and not on the first item somebody equips.
        /// </summary>
        /// <remarks>
        /// Calling it again does nothing, on purpose: the Effects table is client data sitting in
        /// world.db and nothing writes to it while the server is up.
        /// </remarks>
        public static void Initialize() => Ensure();

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
                    // In a finally so a database that would not open counts as tried: otherwise
                    // every effect looked up from here on would try to open it again.
                    _loaded = true;
                }
            }
        }

        private static void Load()
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, Characteristic, BonusType FROM Effects;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    int characteristic = reader.IsDBNull(1) ? -1 : reader.GetInt32(1);
                    if (characteristic < 0) continue;

                    int bonus = reader.IsDBNull(2) ? 1 : reader.GetInt32(2);
                    // Zero describes an action (for example a weapon's AP cost), not a bonus.
                    if (bonus == 0) continue;
                    _byId[reader.GetInt32(0)] = new Effect(characteristic, bonus < 0 ? -1 : 1);
                }

                Console.WriteLine($"[EffectTable] {_byId.Count} effects that move a characteristic.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EffectTable] Could not read the effects: {ex.Message}");
            }
        }

        public static bool TryGet(int effectId, out Effect effect)
        {
            Ensure();
            return _byId.TryGetValue(effectId, out effect);
        }
    }
}

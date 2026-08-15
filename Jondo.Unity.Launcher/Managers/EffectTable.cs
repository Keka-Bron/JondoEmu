using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Launcher.Managers
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

        public static int Count => _byId.Count;

        public static void Initialize()
        {
            _byId.Clear();
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
                    _byId[reader.GetInt32(0)] = new Effect(characteristic, bonus < 0 ? -1 : 1);
                }

                Console.WriteLine($"[EffectTable] {_byId.Count} effects that move a characteristic.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EffectTable] Could not read the effects: {ex.Message}");
            }
        }

        public static bool TryGet(int effectId, out Effect effect) => _byId.TryGetValue(effectId, out effect);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The item bar, the second of the two shortcut bars (bar 0): whatever the player drags
    /// onto it, kept as the client sent it.
    /// </summary>
    /// <remarks>
    /// MEASURED in the entry capture's item itg: a slot { 2 slot } holds one of
    /// <code>
    ///   9 { 2 template, 3 uid }    an item
    ///   1 { 2 emote }              an emote
    ///   4 { 2 uuid }               a build (CharacterPresets; «Conjuntos/crear nuevo conjunto…»
    ///                              drags one there and back: itz -> ivk, iul -> ivr)
    /// </code>
    /// The client builds the shortcut and the server only keeps and echoes it, so it is stored
    /// whole rather than read: whatever else the bar can hold comes back the same. Until now it
    /// was echoed and forgotten, and the bar came up empty on every entry. Kept in world.db:
    /// <code>
    ///   CharacterItemShortcuts (CharacterId, Slot, Shortcut)
    /// </code>
    /// </remarks>
    public static class ItemShortcuts
    {
        /// <summary>The item bar's number; the spell one is ConnectionProtocol.SpellBar.</summary>
        public const int Bar = 0;

        private static readonly object _gate = new object();
        private static volatile bool _tableReady;

        /// <summary>The bar's slots: slot -> the shortcut as the client sent it.</summary>
        public static SortedDictionary<int, byte[]> Of(long characterId)
        {
            try
            {
                return ReadForSnapshot(characterId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Item bar] Could not read {characterId}'s bar: {ex.Message}");
            }
            return new();
        }

        /// <summary>Strict ordered read: a failed query must not advertise an empty or partial bar.</summary>
        internal static SortedDictionary<int, byte[]> ReadForSnapshot(long characterId)
        {
            var slots = new SortedDictionary<int, byte[]>();
            if (characterId <= 0) return slots;
            EnsureTable();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Slot, Shortcut FROM CharacterItemShortcuts WHERE CharacterId = $c;";
            command.Parameters.AddWithValue("$c", characterId);
            using var reader = command.ExecuteReader();
            while (reader.Read()) slots.Add(reader.GetInt32(0), (byte[])reader.GetValue(1));
            return slots;
        }

        /// <summary>Keeps a shortcut in its slot, the one it says.</summary>
        public static void Put(long characterId, int slot, byte[] shortcut)
        {
            if (characterId <= 0 || slot < 0) return;
            Run(characterId, "INSERT OR REPLACE INTO CharacterItemShortcuts (CharacterId, Slot, Shortcut) VALUES ($c, $s, $b);",
                ("$s", slot), ("$b", WithSlot(shortcut, slot)));
        }

        /// <summary>Empties a slot.</summary>
        public static void Remove(long characterId, int slot)
            => Run(characterId, "DELETE FROM CharacterItemShortcuts WHERE CharacterId = $c AND Slot = $s;", ("$s", slot));

        /// <summary>Swaps two slots, either may be empty. What each ends up holding, or null for empty.</summary>
        public static (byte[]? First, byte[]? Second) Swap(long characterId, int first, int second)
        {
            var slots = Of(characterId);
            slots.TryGetValue(first, out var atFirst);
            slots.TryGetValue(second, out var atSecond);
            Remove(characterId, first);
            Remove(characterId, second);
            if (atSecond != null) Put(characterId, first, atSecond);
            if (atFirst != null) Put(characterId, second, atFirst);
            return (atSecond == null ? null : WithSlot(atSecond, first), atFirst == null ? null : WithSlot(atFirst, second));
        }

        /// <summary>The slot a shortcut says, field 2; 0 when it is left out.</summary>
        public static int SlotOf(byte[] shortcut)
            => (int)(ProtoMessage.Parse(shortcut).Fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 0)?.VarIntValue ?? 0);

        /// <summary>Whether a shortcut holds anything but its slot: one that does not empties it.</summary>
        public static bool HoldsSomething(byte[] shortcut)
            => ProtoMessage.Parse(shortcut).Fields.Any(f => f.FieldNumber != 2);

        /// <summary>The same shortcut moved to another slot: field 2 rewritten, the rest as it was.</summary>
        internal static byte[] WithSlot(byte[] shortcut, int slot)
        {
            var pb = Pb.New();
            foreach (var field in ProtoMessage.Parse(shortcut).Fields)
            {
                if (field.FieldNumber == 2 && field.WireType == 0) continue;
                if (field.WireType == 0) pb.Var(field.FieldNumber, (long)field.VarIntValue);
                else if (field.WireType == 2) pb.Bytes(field.FieldNumber, field.BytesValue);
            }
            return pb.VarIfNotZero(2, slot).Build();
        }

        /// <summary>Forgets a character's bar, for the tests.</summary>
        internal static void Forget(long characterId)
            => Run(characterId, "DELETE FROM CharacterItemShortcuts WHERE CharacterId = $c;");

        private static void Run(long characterId, string sql, params (string Name, object Value)[] parameters)
        {
            try
            {
                EnsureTable();
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("$c", characterId);
                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Item bar] Could not write {characterId}'s bar: {ex.Message}");
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
                create.CommandText = "CREATE TABLE IF NOT EXISTS CharacterItemShortcuts (" +
                                     "CharacterId INTEGER NOT NULL, Slot INTEGER NOT NULL, Shortcut BLOB NOT NULL, " +
                                     "PRIMARY KEY (CharacterId, Slot));";
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

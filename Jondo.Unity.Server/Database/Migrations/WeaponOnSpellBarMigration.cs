using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Database.Migrations;

/// <summary>
/// The weapon used to be drawn on the bar every time and never saved, so every saved bar lacks it.
/// Now the weapon is saved like any spell and can be moved or taken off, so a saved bar without it
/// would leave the player unable to throw a punch. Once, each saved bar gets the weapon in its first
/// free slot, where it was drawn before. Bars written later are left as the player keeps them.
/// </summary>
internal static class WeaponOnSpellBarMigration
{
    internal const string Name = "20261121-weapon-on-spell-bar";
    private const int Weapon = Network.FightProtocol.Weapon;

    /// <summary>Applies to an open world connection; false when already recorded.</summary>
    internal static bool Apply(SqliteConnection connection)
    {
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException("The world database connection must already be open.");

        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS JondoMigrations (Name TEXT PRIMARY KEY, AppliedAt TEXT NOT NULL);
            SELECT 1 FROM JondoMigrations WHERE Name = $name;";
        command.Parameters.AddWithValue("$name", Name);
        if (command.ExecuteScalar() != null)
        {
            transaction.Commit();
            return false;
        }

        var used = new Dictionary<long, HashSet<int>>();
        command.CommandText = "SELECT CharacterId, Slot, SpellId FROM CharacterSpellBar;";
        var armed = new HashSet<long>();
        using (var rows = command.ExecuteReader())
            while (rows.Read())
            {
                long character = rows.GetInt64(0);
                if (!used.TryGetValue(character, out var slots)) used[character] = slots = new HashSet<int>();
                slots.Add(rows.GetInt32(1));
                if (rows.GetInt32(2) == Weapon) armed.Add(character);
            }

        command.CommandText = "INSERT INTO CharacterSpellBar (CharacterId, Slot, SpellId) VALUES ($c, $t, $s);";
        command.Parameters.Clear();
        var c = command.Parameters.Add("$c", SqliteType.Integer);
        var t = command.Parameters.Add("$t", SqliteType.Integer);
        command.Parameters.AddWithValue("$s", Weapon);
        foreach (var (character, slots) in used)
        {
            if (armed.Contains(character)) continue;
            int slot = 0;
            while (slots.Contains(slot)) slot++;
            c.Value = character;
            t.Value = slot;
            command.ExecuteNonQuery();
        }

        command.Parameters.Clear();
        command.CommandText = "INSERT INTO JondoMigrations (Name, AppliedAt) VALUES ($name, $at);";
        command.Parameters.AddWithValue("$name", Name);
        command.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }
}

using Jondo.Unity.Server.Database.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Server.Tests;

// Each migration runs against an isolated in-memory database.
public sealed class WeaponOnSpellBarMigrationTests
{
    private const int Weapon = Network.FightProtocol.Weapon;

    [Fact]
    public void A_bar_saved_without_the_weapon_gets_it_in_its_first_free_slot()
    {
        using var connection = Open();
        Put(connection, 1, 1, 161);
        Put(connection, 1, 2, 162);
        Put(connection, 2, 0, 171);
        Put(connection, 2, 2, 172);

        Assert.True(WeaponOnSpellBarMigration.Apply(connection));

        Assert.Equal(0L, SlotOfWeapon(connection, 1));
        Assert.Equal(1L, SlotOfWeapon(connection, 2));
    }

    [Fact]
    public void A_bar_that_already_has_the_weapon_is_left_alone_and_the_second_run_does_nothing()
    {
        using var connection = Open();
        Put(connection, 3, 5, Weapon);
        Put(connection, 3, 0, 181);

        Assert.True(WeaponOnSpellBarMigration.Apply(connection));
        Put(connection, 4, 1, 191);
        Assert.False(WeaponOnSpellBarMigration.Apply(connection));

        Assert.Equal(5L, SlotOfWeapon(connection, 3));
        Assert.Null(SlotOfWeapon(connection, 4));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM CharacterSpellBar WHERE SpellId = 0;"));
    }

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"CREATE TABLE CharacterSpellBar (CharacterId INTEGER NOT NULL, Slot INTEGER NOT NULL,
                                SpellId INTEGER NOT NULL, PRIMARY KEY (CharacterId, Slot));";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void Put(SqliteConnection connection, long character, int slot, int spell)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO CharacterSpellBar VALUES ($c, $t, $s);";
        command.Parameters.AddWithValue("$c", character);
        command.Parameters.AddWithValue("$t", slot);
        command.Parameters.AddWithValue("$s", spell);
        command.ExecuteNonQuery();
    }

    private static long? SlotOfWeapon(SqliteConnection connection, long character)
        => Scalar(connection, $"SELECT Slot FROM CharacterSpellBar WHERE CharacterId = {character} AND SpellId = 0;");

    private static long? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as long?;
    }
}

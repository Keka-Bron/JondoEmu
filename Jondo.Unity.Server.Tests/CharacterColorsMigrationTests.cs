using Jondo.Unity.Server.Database.Migrations;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Server.Tests;

// Each migration runs against an isolated in-memory database.
public sealed class CharacterColorsMigrationTests
{
    [Fact]
    public void Migration_preserves_palettes_removes_the_blob_and_records_one_completion()
    {
        using var connection = Open();
        string bare = Hex(0x112233, 0x445566, 0, 0xABCDEF, 0xFFFFFF, 0x010203);
        string indexed = Hex(0x06ABCDEF, 0x02000000, 0x04112233);
        Insert(connection, 1, bare);
        Insert(connection, 2, indexed);
        Insert(connection, 3, "0A0180"); // Truncated packed varint: use breed defaults.
        Insert(connection, 4, "080118032218A28B9B0FCBE5F615A4E1B91992A6C820888CA028F5B7CB34");
        Insert(connection, 5, "not hex");
        Insert(connection, 6, "");
        Insert(connection, 7, "0A00" + bare); // Skip an empty packed palette.
        Insert(connection, 8, Hex(-1, 0x445566, -1, 0, 0xFFFFFF, 0x123456));
        Insert(connection, 9, Hex(0x01112233, -1, 0x03ABCDEF));

        Assert.True(CharacterColorsMigration.Apply(connection));
        AssertPalette(connection, 1, 0x112233, 0x445566, 0, 0xABCDEF, 0xFFFFFF, 0x010203);
        AssertPalette(connection, 2, null, 0, null, 0x112233, null, 0xABCDEF);
        AssertPalette(connection, 4, 0xE6C5A2, 0xBDB2CB, 0x2E70A4, 0x121312, 0x080608, 0x92DBF5);
        AssertPalette(connection, 7, 0x112233, 0x445566, 0, 0xABCDEF, 0xFFFFFF, 0x010203);
        AssertPalette(connection, 8, null, 0x445566, null, 0, 0xFFFFFF, 0x123456);
        AssertPalette(connection, 9, 0x112233, null, 0xABCDEF, null, null, null);
        foreach (int id in new[] { 3, 5, 6 })
            AssertPalette(connection, id, null, null, null, null, null, null);
        Assert.Equal(6L, Scalar(connection,
            "SELECT COUNT(*) FROM pragma_table_info('Characters') WHERE name GLOB 'Color[1-6]';"));
        Assert.Equal(0L, Scalar(connection,
            "SELECT COUNT(*) FROM pragma_table_info('Characters') WHERE name = 'Colors';"));
        Assert.Equal(0L, Scalar(connection,
            "SELECT COUNT(*) FROM pragma_table_info('Characters') WHERE name = 'Look';"));

        Execute(connection, "UPDATE Characters SET Color3 = 42 WHERE Id = 1;");
        Assert.False(CharacterColorsMigration.Apply(connection));
        AssertPalette(connection, 1, 0x112233, 0x445566, 42, 0xABCDEF, 0xFFFFFF, 0x010203);
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM JondoMigrations;"));
        Assert.Equal(CharacterColorsMigration.Name, Scalar(connection, "SELECT Name FROM JondoMigrations;"));
    }

    [Theory]
    [InlineData("080118032218A28B9B0FCBE5F615A4E1B91992A6C820888CA028F5B7CB342A035BE410420134320220013809")]
    [InlineData("0801120CF5B7CB34888CA02892A6C82018032218A28B9B0FCBE5F615A4E1B91992A6C820888CA028F5B7CB342A035BE410320220013809420134")]
    public void Historical_seed_palettes_survive_migration_and_restart(string look)
    {
        using var connection = Open();
        Insert(connection, 13825558, look);

        Assert.True(CharacterColorsMigration.Apply(connection));
        AssertPalette(connection, 13825558, 0xE6C5A2, 0xBDB2CB, 0x2E70A4, 0x121312, 0x080608, 0x92DBF5);
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM pragma_table_info('Characters') WHERE name = 'Look';"));

        Execute(connection, "UPDATE Characters SET Color1 = 0, Color6 = NULL WHERE Id = 13825558;");
        Assert.False(CharacterColorsMigration.Apply(connection));
        AssertPalette(connection, 13825558, 0, 0xBDB2CB, 0x2E70A4, 0x121312, 0x080608, null);
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM JondoMigrations;"));
    }

    [Fact]
    public void Historical_sparse_palette_keeps_slot_indices_black_and_inheritance()
    {
        using var connection = Open();
        Insert(connection, 1, PaletteHex(4, 0x06123456, -1, 0x02000000));
        Assert.True(CharacterColorsMigration.Apply(connection));
        AssertPalette(connection, 1, null, 0, null, null, null, 0x123456);
    }

    [Fact]
    public void Update_failure_rolls_back_all_six_columns_backfill_and_migration_marker()
    {
        using var connection = Open();
        string look = Hex(0x112233);
        Insert(connection, 1, look);
        Execute(connection, """
            CREATE TRIGGER RejectCharacterUpdate BEFORE UPDATE ON Characters
            BEGIN
                SELECT RAISE(ABORT, 'test backfill failure');
            END;
            """);

        Assert.Throws<SqliteException>(() => CharacterColorsMigration.Apply(connection));

        Assert.Equal(0L, Scalar(connection,
            "SELECT COUNT(*) FROM pragma_table_info('Characters') WHERE name GLOB 'Color[1-6]';"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'JondoMigrations';"));
        Assert.Equal(look, Scalar(connection, "SELECT Look FROM Characters WHERE Id = 1;"));

        Execute(connection, "DROP TRIGGER RejectCharacterUpdate;");
        Assert.True(CharacterColorsMigration.Apply(connection));
        AssertPalette(connection, 1, 0x112233, null, null, null, null, null);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Each_slot_accepts_null_and_RGB_boundaries_but_rejects_other_values(int slot)
    {
        using var connection = Open();
        Insert(connection, 1, "");
        CharacterColorsMigration.Apply(connection);
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE Characters SET Color{slot} = $color WHERE Id = 1;";
        var parameter = command.Parameters.AddWithValue("$color", DBNull.Value);
        foreach (object value in new object[] { DBNull.Value, 0, 0xFFFFFF })
        {
            parameter.Value = value;
            command.ExecuteNonQuery();
            Assert.Equal(value is int rgb ? (object)(long)rgb : value,
                Scalar(connection, $"SELECT Color{slot} FROM Characters WHERE Id = 1;"));
        }
        foreach (object value in new object[] { -1, 0x1000000, 1.5, "not RGB" })
        {
            parameter.Value = value;
            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        }
    }

    [Theory]
    [InlineData("duplicate", 1)]
    [InlineData("slot-zero", 1)]
    [InlineData("slot-seven", 1)]
    [InlineData("too-many", 1)]
    [InlineData("duplicate", 4)]
    [InlineData("slot-zero", 4)]
    [InlineData("slot-seven", 4)]
    [InlineData("too-many", 4)]
    public void Unrepresentable_palettes_abort_without_partial_schema_or_data(string scenario, int fieldNumber)
    {
        using var connection = Open();
        Insert(connection, 1, Hex(0x112233));
        string look = scenario switch
        {
            "duplicate" => PaletteHex(fieldNumber, 0x01112233, 0x01445566),
            "slot-zero" => PaletteHex(fieldNumber, 0x01112233, 0x445566),
            "slot-seven" => PaletteHex(fieldNumber, 0x07112233),
            _ => PaletteHex(fieldNumber, 1, 2, 3, 4, 5, 6, 7),
        };
        Insert(connection, 2, look);

        Assert.Throws<SqliteException>(() => CharacterColorsMigration.Apply(connection));

        Assert.Equal(0L, Scalar(connection,
            "SELECT COUNT(*) FROM pragma_table_info('Characters') WHERE name GLOB 'Color[1-6]';"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'JondoMigrations';"));
        Assert.Equal(look, Scalar(connection, "SELECT Look FROM Characters WHERE Id = 2;"));
    }

    [Fact]
    public void Restart_keeps_new_RGB_choices_after_the_single_upgrade()
    {
        using var connection = Open();
        Insert(connection, 1, Hex(0x06123456, 0x02000000));
        CharacterColorsMigration.Apply(connection);
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM pragma_table_info('Characters') WHERE name = 'Look';"));
        AssertPalette(connection, 1, null, 0, null, null, null, 0x123456);
        Execute(connection, "UPDATE Characters SET Color6 = 42 WHERE Id = 1;");
        Assert.False(CharacterColorsMigration.Apply(connection));
        AssertPalette(connection, 1, null, 0, null, null, null, 42);
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM JondoMigrations;"));
    }

    [Fact]
    public void Fresh_schema_records_the_same_single_migration_without_modifying_colours()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, """
            CREATE TABLE Characters (Id INTEGER PRIMARY KEY,
                Color1 INTEGER, Color2 INTEGER, Color3 INTEGER,
                Color4 INTEGER, Color5 INTEGER, Color6 INTEGER);
            INSERT INTO Characters VALUES (1, 0, NULL, 42, NULL, 16777215, NULL);
            """);
        Assert.True(CharacterColorsMigration.Apply(connection));
        AssertPalette(connection, 1, 0, null, 42, null, 0xFFFFFF, null);
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM JondoMigrations;"));
        Assert.False(CharacterColorsMigration.Apply(connection));
    }

    [Fact]
    public void Drop_failure_rolls_back_the_columns_backfill_and_completion_together()
    {
        using var connection = Open();
        Insert(connection, 1, Hex(0x112233));
        Execute(connection, "CREATE VIEW LegacyLooks AS SELECT Look FROM Characters;");
        Assert.Throws<SqliteException>(() => CharacterColorsMigration.Apply(connection));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM pragma_table_info('Characters') WHERE name = 'Look';"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM pragma_table_info('Characters') WHERE name GLOB 'Color[1-6]';"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'JondoMigrations';"));
        Assert.Equal(Hex(0x112233), Scalar(connection, "SELECT Look FROM Characters WHERE Id = 1;"));
        Execute(connection, "DROP VIEW LegacyLooks;");
        Assert.True(CharacterColorsMigration.Apply(connection));
        AssertPalette(connection, 1, 0x112233, null, null, null, null, null);
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM JondoMigrations;"));
    }

    [Fact]
    public void Incomplete_current_schema_is_not_recorded_as_completed()
    {
        using var incomplete = new SqliteConnection("Data Source=:memory:");
        incomplete.Open();
        Execute(incomplete, "CREATE TABLE Characters (Id INTEGER PRIMARY KEY, Color1 INTEGER);");
        Assert.Throws<SqliteException>(() => CharacterColorsMigration.Apply(incomplete));
        Assert.Equal(0L, Scalar(incomplete, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'JondoMigrations';"));
    }

    [Fact]
    public void Completion_marker_failure_restores_the_original_schema_and_data()
    {
        using var connection = Open();
        string look = Hex(0x112233);
        Insert(connection, 1, look);
        Execute(connection, """
            CREATE TABLE JondoMigrations (Name TEXT PRIMARY KEY, AppliedAt TEXT NOT NULL);
            CREATE TRIGGER RejectMigration BEFORE INSERT ON JondoMigrations
            BEGIN SELECT RAISE(ABORT, 'completion failed'); END;
            """);
        Assert.Throws<SqliteException>(() => CharacterColorsMigration.Apply(connection));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM JondoMigrations;"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM pragma_table_info('Characters') WHERE name GLOB 'Color[1-6]';"));
        Assert.Equal(look, Scalar(connection, "SELECT Look FROM Characters WHERE Id = 1;"));
        Execute(connection, "DROP TRIGGER RejectMigration;");
        Assert.True(CharacterColorsMigration.Apply(connection));
        AssertPalette(connection, 1, 0x112233, null, null, null, null, null);
    }

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "CREATE TABLE Characters (Id INTEGER PRIMARY KEY, Look TEXT NOT NULL);");
        return connection;
    }

    private static string Hex(params long[] colors) => Convert.ToHexString(Pb.New().Packed(1, colors).Build());

    private static string PaletteHex(int fieldNumber, params long[] colors)
    {
        var look = Pb.New();
        if (fieldNumber == 4) look.Var(1, 1).Var(3, 3);
        return Convert.ToHexString(look.Packed(fieldNumber, colors).Build());
    }

    private static void Insert(SqliteConnection connection, long id, string look)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Characters (Id, Look) VALUES ($id, $look);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$look", look);
        command.ExecuteNonQuery();
    }

    private static void AssertPalette(SqliteConnection connection, int id,
        int? color1, int? color2, int? color3, int? color4, int? color5, int? color6)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Color1, Color2, Color3, Color4, Color5, Color6 FROM Characters WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(new int?[] { color1, color2, color3, color4, color5, color6 },
            Enumerable.Range(0, 6).Select(index => reader.IsDBNull(index) ? (int?)null : reader.GetInt32(index)).ToArray());
        Assert.False(reader.Read());
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

using System.Data;
using System.Globalization;
using Google.Protobuf;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Database.Migrations;

/// <summary>One-time world migration registered in DatabaseManager.Initialize after character setup.</summary>
internal static class CharacterColorsMigration
{
    internal const string Name = "20261112-character-colors";
    private const string ResourceName = "Jondo.Unity.Server.Database.Migrations.CharacterColors.sql";
    private const string HistorySection = "-- Migration history";
    private const string DecoderName = "jondo_look_color";
    private const int SlotCount = 6;

    /// <summary>
    /// Applies to an explicitly supplied, open world connection; returns false if
    /// already recorded. This method opens no database and creates no backup.
    /// DatabaseManager.Initialize calls this after the existing verified backup.
    /// </summary>
    internal static bool Apply(SqliteConnection connection)
    {
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException("The world database connection must already be open.");

        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'JondoMigrations';";
        if (command.ExecuteScalar() != null)
        {
            command.CommandText = "SELECT 1 FROM JondoMigrations WHERE Name = $migrationName;";
            command.Parameters.AddWithValue("$migrationName", Name);
            if (command.ExecuteScalar() != null)
            {
                transaction.Commit();
                return false;
            }
        }

        command.Parameters.Clear();
        command.CommandText = "PRAGMA table_info(Characters);";
        bool hasLegacyLook = false;
        using (var columns = command.ExecuteReader())
            while (columns.Read())
                hasLegacyLook |= string.Equals(columns.GetString(1), "Look", StringComparison.OrdinalIgnoreCase);

        if (!hasLegacyLook)
        {
            // Fresh databases already have the final schema. Validate it before
            // recording this same migration without a legacy backfill or drop.
            command.CommandText = "SELECT Color1, Color2, Color3, Color4, Color5, Color6 FROM Characters LIMIT 0;";
            using var columns = command.ExecuteReader();
        }

        using var script = typeof(CharacterColorsMigration).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The character-colors migration SQL is missing.");
        using var reader = new StreamReader(script);
        string sql = reader.ReadToEnd();
        int historyStart = sql.IndexOf(HistorySection, StringComparison.Ordinal);
        if (historyStart < 0)
            throw new InvalidOperationException("The character-colors migration history section is missing.");
        if (!hasLegacyLook) sql = sql[historyStart..];

        connection.CreateFunction<string?, int, int?>(DecoderName, DecodeColor, isDeterministic: true);
        try
        {
            command.Parameters.Clear();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$migrationName", Name);
            command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            // Batched schema changes can expose result sets. Explicitly finish every
            // statement: ExecuteNonQuery drains the rest during reader disposal,
            // where Microsoft.Data.Sqlite suppresses errors from later statements.
            using (var results = command.ExecuteReader())
            {
                do
                {
                    while (results.Read()) { }
                } while (results.NextResult());
            }
            transaction.Commit();
            return true;
        }
        finally
        {
            connection.CreateFunction<string?, int, int?>(DecoderName, null);
        }
    }

    /// <summary>
    /// Extracts one of the six fixed RGB slots without materializing breed defaults
    /// or reading gameplay state. NULL leaves that slot at its breed/sex default.
    /// </summary>
    internal static int? DecodeColor(string? lookHex, int slot)
    {
        if (slot is < 1 or > SlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
        return DecodeColors(lookHex)[slot];
    }

    private static CharacterColors DecodeColors(string? lookHex)
    {
        if (string.IsNullOrEmpty(lookHex)) return default;
        try
        {
            var look = ProtoMessage.Parse(Convert.FromHexString(lookHex));
            if (ReadPalette(look.Fields, 1) is { } colors) return colors;

            // The original default-character seed and its stored variant have scalar
            // field 1 = 1, field 3 = 3 and indexed colours in packed field 4.
            // Only recognize that historical layout; arbitrary field 4 data is not a palette.
            if (look.Fields.Any(field => field.FieldNumber == 1 && field.WireType == 0 && field.VarIntValue == 1)
                && look.Fields.Any(field => field.FieldNumber == 3 && field.WireType == 0 && field.VarIntValue == 3))
                return ReadPalette(look.Fields, 4) ?? default;
        }
        catch (FormatException) { }
        catch (InvalidProtocolBufferException) { }
        return default;
    }

    private static CharacterColors? ReadPalette(IEnumerable<ProtoField> fields, int fieldNumber)
    {
        foreach (var field in fields)
        {
            if (field.FieldNumber != fieldNumber || field.WireType != 2) continue;
            var encoded = new long[SlotCount];
            int count = 0;
            using var input = new CodedInputStream(field.BytesValue);
            while (!input.IsAtEnd)
            {
                if (count == SlotCount)
                    throw new InvalidOperationException("A saved palette contains more than six colours.");
                encoded[count++] = input.ReadInt64();
            }
            if (count == 0) continue;

            return CharacterColors.FromIndexed(new ArraySegment<long>(encoded, 0, count));
        }
        return null;
    }
}

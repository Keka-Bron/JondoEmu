using System;
using System.IO;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Sessions
{
    /// <summary>
    /// Scrolls go apart from points: in their own field of the frame, in their own column of the base,
    /// and out of the count of what was spent.
    /// </summary>
    /// <remarks>
    /// A freshly made level 200 had 183 points to distribute instead of 995 because creation
    /// put the scrolls in the base, and the base is the points spent. The field is the f3 of
    /// each characteristic, measured in 156 captures of real characters -always 100, never 101-,
    /// next to the points' f2 and the equipment's f7.
    /// </remarks>
    public class ScrolledCharacteristicsTests
    {
        /// <summary>
        /// The sheet carries the points in f2 and the scrolls in f3, like the capture's real strength:
        /// <c>f4 { f2: 398, f3: 100, f7: 499 }</c>.
        /// </summary>
        [Fact]
        public void The_sheet_sends_points_and_scrolls_in_different_fields()
        {
            var session = GameSession.SinSocket();
            using (SessionContext.Push(session))
            {
                session.State.CharacterLevel = 200;
                session.State.StatStrength = 398;
                session.State.ScrolledStrength = 100;
                session.State.StatVitality = 0;
                session.State.ScrolledVitality = 100;
                session.State.StatWisdom = 0;
                session.State.ScrolledWisdom = 0;

                var kub = ProtoMessage.Parse(ConnectionProtocol.BuildCharacteristics());
                var body = ProtoMessage.Parse(kub.Fields.Single(f => f.FieldNumber == 2).BytesValue);

                var strength = Detail(body, ConnectionProtocol.Stat.Strength);
                Assert.Equal(398, Value(strength, 2));
                Assert.Equal(100, Value(strength, 3));

                // No points and with scrolls: only f3, which is what a proto3 does with a zero.
                var vitality = Detail(body, ConnectionProtocol.Stat.Vitality);
                Assert.Equal(0, Value(vitality, 2));
                Assert.Equal(100, Value(vitality, 3));

                // And with neither of the two, nothing at all.
                var wisdom = Detail(body, ConnectionProtocol.Stat.Wisdom);
                Assert.Equal(0, Value(wisdom, 2));
                Assert.Equal(0, Value(wisdom, 3));

                // What the game uses is the sum: 498 strength is 3,490 pods.
                Assert.Equal(498, session.State.TotalStrength);
                Assert.Equal(1000 + 5 * 498, Value(Detail(body, ConnectionProtocol.Stat.Pods), 2));
            }
        }

        /// <summary>
        /// The migration: those born with 101 in the six of the base lose them, keep
        /// the scrolls in their column and get all their capital back. The rest are not touched.
        /// </summary>
        [Fact]
        public void The_migration_moves_the_scrolls_out_of_the_base_once()
        {
            string file = Path.Combine(Path.GetTempPath(), $"jondo-pergaminos-{Guid.NewGuid():N}.db");
            try
            {
                using (var connection = new SqliteConnection($"Data Source={file}"))
                {
                    connection.Open();
                    var create = connection.CreateCommand();
                    create.CommandText = @"
                        CREATE TABLE Characters (
                            Id INTEGER PRIMARY KEY, Name TEXT, Level INTEGER, RemainingPoints INTEGER,
                            Vitality INTEGER, Wisdom INTEGER, Strength INTEGER,
                            Intelligence INTEGER, Chance INTEGER, Agility INTEGER);
                        INSERT INTO Characters VALUES (1, 'Test',      200, 183, 101, 101, 101, 101, 101, 101);
                        INSERT INTO Characters VALUES (2, 'Terceron',    3,  10, 101, 101, 101, 101, 101, 101);
                        INSERT INTO Characters VALUES (3, 'Keka',      201,   0,   3,   0, 398,   0,   0,   0);
                        INSERT INTO Characters VALUES (4, 'Dragon',    200, 825,   0,   0,  50,  40,  30,  50);";
                    create.ExecuteNonQuery();

                    DatabaseManager.MoveScrollsOutOfTheBase(connection);

                    var read = connection.CreateCommand();
                    read.CommandText = "SELECT Id, RemainingPoints, Strength, Vitality, ScrolledStrength, ScrolledVitality " +
                                       "FROM Characters ORDER BY Id;";
                    using var rows = read.ExecuteReader();

                    // Test: the 101s out, 995 to distribute, scrolls at 100.
                    Assert.True(rows.Read());
                    Assert.Equal(995, rows.GetInt32(1));
                    Assert.Equal(0, rows.GetInt32(2));
                    Assert.Equal(0, rows.GetInt32(3));
                    Assert.Equal(100, rows.GetInt32(4));
                    Assert.Equal(100, rows.GetInt32(5));

                    // Terceron, level 3: ten points, which are his 5 x 2.
                    Assert.True(rows.Read());
                    Assert.Equal(10, rows.GetInt32(1));
                    Assert.Equal(0, rows.GetInt32(2));

                    // Keka, who really distributed: the base stays, and scrolls at 100 all the same, which
                    // is what her capture shows in f3.
                    Assert.True(rows.Read());
                    Assert.Equal(0, rows.GetInt32(1));
                    Assert.Equal(398, rows.GetInt32(2));
                    Assert.Equal(3, rows.GetInt32(3));
                    Assert.Equal(100, rows.GetInt32(4));

                    Assert.True(rows.Read());
                    Assert.Equal(825, rows.GetInt32(1));
                    Assert.Equal(50, rows.GetInt32(2));
                    rows.Close();

                    // The second time it does nothing: the columns are already there and the cleanup is tied to
                    // creating them. A character who later distributes up to 101 in all six does not
                    // see them erased.
                    var later = connection.CreateCommand();
                    later.CommandText = "UPDATE Characters SET Vitality = 101, Wisdom = 101, Strength = 101, " +
                                        "Intelligence = 101, Chance = 101, Agility = 101, RemainingPoints = 7 WHERE Id = 4;";
                    later.ExecuteNonQuery();

                    DatabaseManager.MoveScrollsOutOfTheBase(connection);

                    var again = connection.CreateCommand();
                    again.CommandText = "SELECT Strength, RemainingPoints FROM Characters WHERE Id = 4;";
                    using var row = again.ExecuteReader();
                    Assert.True(row.Read());
                    Assert.Equal(101, row.GetInt32(0));
                    Assert.Equal(7, row.GetInt32(1));
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { File.Delete(file); } catch (IOException) { }
            }
        }

        private static ProtoMessage Detail(ProtoMessage body, int statId)
        {
            foreach (var field in body.Fields)
            {
                if (field.FieldNumber != 11 || field.WireType != 2) continue;
                var entry = ProtoMessage.Parse(field.BytesValue);
                long id = entry.Fields.FirstOrDefault(f => f.FieldNumber == 1 && f.WireType == 0)?.VarIntValue ?? 0;
                if (id != statId) continue;
                var detail = entry.Fields.FirstOrDefault(f => f.FieldNumber == 4 && f.WireType == 2);
                return detail == null ? new ProtoMessage() : ProtoMessage.Parse(detail.BytesValue);
            }

            return new ProtoMessage();
        }

        private static long Value(ProtoMessage detail, int field)
            => detail.Fields.FirstOrDefault(f => f.FieldNumber == field && f.WireType == 0)?.VarIntValue ?? 0;
    }
}

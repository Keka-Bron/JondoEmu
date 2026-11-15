using System;
using System.IO;
using System.Linq;
using Jondo.Unity.Protocol.Game;
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

                var body = CharacteristicsMessages.Live.Snapshot(session).Stats;

                var strength = Detail(body, Stat.Strength);
                Assert.Equal(398, strength.Base);
                Assert.Equal(100, strength.Additional);

                // No points and with scrolls: only f3, which is what a proto3 does with a zero.
                var vitality = Detail(body, Stat.Vitality);
                Assert.Equal(0, vitality.Base);
                Assert.Equal(100, vitality.Additional);

                // And with neither of the two, nothing at all.
                var wisdom = Detail(body, Stat.Wisdom);
                Assert.Equal(0, wisdom.Base);
                Assert.Equal(0, wisdom.Additional);

                // What the game uses is the sum: 498 strength is 2,490 pods, on top of the
                // thousand and the five a level (ConnectionProtocol.Capacity).
                Assert.Equal(498, session.State.TotalStrength);
                Assert.Equal(1000 + 5 * (session.State.CharacterLevel - 1) + 5 * 498, Detail(body, Stat.Pods).Base);
            }
        }

        /// <summary>
        /// What the characteristics give counts the equipment's: the capture's 100 scrolled and 254
        /// worn wisdom are 35 dodge in the kub's base, and its elements make the initiative.
        /// </summary>
        [Fact]
        public void The_sheet_gives_what_the_characteristics_give_with_the_equipment()
        {
            var session = GameSession.SinSocket();
            using (SessionContext.Push(session))
            {
                session.State.CharacterLevel = 200;
                session.State.ScrolledWisdom = 100;
                session.State.ScrolledChance = 100;
                session.State.StatStrength = 50;
                Equipment.Add(9_000_000_123, 17626, 1, 0, "[[124,254,0,0]]");

                var body = CharacteristicsMessages.Live.Snapshot(session).Stats;

                Assert.Equal(35, Detail(body, Stat.DodgeActionPoints).Base);
                Assert.Equal(35, Detail(body, Stat.WithdrawMovementPoints).Base);
                Assert.Equal(50 + 100, Detail(body, Stat.Initiative).Base);
                Assert.Equal(100 + 10, Detail(body, Stat.Prospecting).Base);
                Assert.Equal(1000 + 5 * 199 + 5 * 50, Detail(body, Stat.Pods).Base);
            }
        }

        [Fact]
        public void Semantic_sheet_reads_equipment_and_scrolled_points_from_its_explicit_owner()
        {
            var own = GameSession.SinSocket();
            own.State.CharacterLevel = 200;
            own.State.StatStrength = 50;
            own.State.ScrolledStrength = 100;
            own.State.ScrolledWisdom = 100;
            var item = new Equipment.Item { Position = 0, Template = 17626 };
            item.Effects.Add(new Equipment.ItemEffect(124, 254, 0, 0));
            own.State.EquipmentItems[9_000_000_124] = item;
            var wrong = GameSession.SinSocket();
            wrong.State.StatStrength = 999;
            using var context = SessionContext.Push(wrong);

            var sheet = CharacteristicsMessages.Live.Snapshot(own).Stats;
            CharacterCharacteristicDetailed Stat(int id) => Assert.IsType<CharacterCharacteristicCaseDetailed>(
                sheet.Characteristics.Single(entry => entry.CharacteristicId == id).CharacteristicCase).Value;
            Assert.Equal(new CharacterCharacteristicDetailed(50, 100, 0, 0, 0, 0), Stat(10));
            Assert.Equal(new CharacterCharacteristicDetailed(0, 100, 0, 0, 254, 0), Stat(12));
            Assert.Equal(35, Stat(27).Base);
            Assert.Equal(1750, Stat(40).Base);
            Assert.Same(wrong, SessionContext.Actual);
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
                TestDatabases.ReleasePool(file);
                try { File.Delete(file); } catch (IOException) { }
            }
        }

        private static CharacterCharacteristicDetailed Detail(CharacterCharacteristics body, int statId)
            => Assert.IsType<CharacterCharacteristicCaseDetailed>(
                body.Characteristics.Single(entry => entry.CharacteristicId == statId).CharacteristicCase).Value;
    }
}

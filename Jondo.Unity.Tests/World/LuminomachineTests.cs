using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Content;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The luminomachine, against the client's own catalogue: the salt ladder, which replies
    /// are offered with how much light, and that each id sent draws on screen the sentence
    /// we say it draws.
    /// </summary>
    public class LuminomachineTests
    {
        /// <summary>What the client calls each band of light.</summary>
        private static readonly string[] Bands = { "", "primera", "segunda", "tercera", "última" };

        /// <summary>
        /// The ladder: one more band costs 1, 3, 6 and 10, and a jump pays the sum.
        /// </summary>
        /// <remarks>
        /// It is not an invented table. It is written in the item's own notice —«Cada tramo de luz
        /// adicional requiere más sal (1-3-6-10)»— and the machine's ten deposit replies
        /// say exactly these ten sums.
        /// </remarks>
        [Fact]
        public void The_ladder_is_one_three_six_ten()
        {
            Assert.Equal(new[] { 1, 3, 6, 10 }, Luminomachine.Steps);

            Assert.Equal(1, Luminomachine.Cost(0, 1));
            Assert.Equal(4, Luminomachine.Cost(0, 2));
            Assert.Equal(10, Luminomachine.Cost(0, 3));
            Assert.Equal(20, Luminomachine.Cost(0, 4));
            Assert.Equal(3, Luminomachine.Cost(1, 2));
            Assert.Equal(9, Luminomachine.Cost(1, 3));
            Assert.Equal(19, Luminomachine.Cost(1, 4));
            Assert.Equal(6, Luminomachine.Cost(2, 3));
            Assert.Equal(16, Luminomachine.Cost(2, 4));
            Assert.Equal(10, Luminomachine.Cost(3, 4));

            // One does not go backwards, and one does not go beyond the last.
            Assert.Equal(0, Luminomachine.Cost(2, 1));
            Assert.Equal(0, Luminomachine.Cost(3, 5));
        }

        /// <summary>
        /// Only what can be paid is offered: a button that cannot work is worse than not
        /// having a button. And when it does not reach even one band, the machine says how much salt is missing.
        /// </summary>
        [Fact]
        public void Only_what_can_be_paid_is_offered()
        {
            Assert.Equal(new[] { Luminomachine.FetchReply(1, 0), Luminomachine.DontTouchReply },
                         Luminomachine.RepliesFor(1, 0, 0));

            Assert.Equal(new[]
            {
                Luminomachine.DepositReply(1, 0, 1),
                Luminomachine.DepositReply(1, 0, 2),
                Luminomachine.DontTouchReply,
            }, Luminomachine.RepliesFor(1, 0, 4));

            // With twenty salts the four bands fit in one go.
            Assert.Equal(5, Luminomachine.RepliesFor(1, 0, 20).Count);

            // From the second only two jumps are left, and with nine salts both go in.
            Assert.Equal(new[]
            {
                Luminomachine.DepositReply(4, 2, 3),
                Luminomachine.DepositReply(4, 2, 4),
                Luminomachine.DontTouchReply,
            }, Luminomachine.RepliesFor(4, 2, 16));

            // Fully lit it asks for nothing, and says so with the already-burning sentence.
            Assert.Equal(new[] { Luminomachine.DontTouchReply }, Luminomachine.RepliesFor(1, 4, 100));
            Assert.Equal(Luminomachine.LitMessage, Luminomachine.MessageAt(1, 4));
            Assert.Equal(60276, Luminomachine.MessageAt(1, 0));
            Assert.Equal(60280, Luminomachine.MessageAt(5, 3));
        }

        /// <summary>
        /// A reply says which machine it comes from and which jump it buys, and the ones that buy nothing
        /// are recognised too: it is needed to close the conversation instead of letting it fall into
        /// the normal path, which would look for a dialogue tree this machine does not have.
        /// </summary>
        [Fact]
        public void A_reply_says_which_machine_and_which_jump()
        {
            for (int floor = 1; floor <= Luminomachine.Machines; floor++)
            {
                for (int from = 0; from < Luminomachine.MostLight; from++)
                {
                    for (int to = from + 1; to <= Luminomachine.MostLight; to++)
                    {
                        var read = Luminomachine.Read(Luminomachine.DepositReply(floor, from, to));
                        Assert.NotNull(read);
                        Assert.Equal(floor, read!.Value.Floor);
                        Assert.Equal(from, read.Value.From);
                        Assert.Equal(to, read.Value.To);
                        Assert.Equal(Luminomachine.Cost(from, to), read.Value.Cost);
                        Assert.True(read.Value.Buys);
                    }

                    var fetch = Luminomachine.Read(Luminomachine.FetchReply(floor, from));
                    Assert.Equal(floor, fetch!.Value.Floor);
                    Assert.Equal(Luminomachine.Steps[from], fetch.Value.Cost);
                    Assert.False(fetch.Value.Buys);
                }
            }

            Assert.False(Luminomachine.Read(Luminomachine.DontTouchReply)!.Value.Buys);

            // «Hasta luego.», the farewell sixty-two NPCs carry, is not its own.
            Assert.Null(Luminomachine.Read(7846));
            Assert.False(Luminomachine.Owns(7846));
        }

        /// <summary>
        /// And the real one: each id we send draws on screen the sentence we say.
        /// </summary>
        /// <remarks>
        /// The client resolves a reply to a text by its id, so a table shifted by one
        /// slot gives an error nowhere: the player reads «Dejar 10 sales» and is charged 4.
        /// This reads again the seventy-six replies of 8007's template in world.db and
        /// compares them with what the code says.
        /// </remarks>
        [Fact]
        public void The_reply_table_matches_what_the_client_will_draw()
        {
            if (!File.Exists(Jondo.Unity.Launcher.Paths.WorldDb)) return;

            var (messages, replies) = MachineTexts();
            Assert.Equal(76, replies.Count);

            for (int floor = 1; floor <= Luminomachine.Machines; floor++)
            {
                for (int from = 0; from < Luminomachine.MostLight; from++)
                {
                    for (int to = from + 1; to <= Luminomachine.MostLight; to++)
                    {
                        string text = replies[Luminomachine.DepositReply(floor, from, to)];
                        Assert.StartsWith("Dejar " + Luminomachine.Cost(from, to) + " sal", text);
                        Assert.Contains(Bands[to] + " franja", text);
                    }

                    string fetch = replies[Luminomachine.FetchReply(floor, from)];
                    Assert.StartsWith("Ir a recoger " + Luminomachine.Steps[from] + " sal", fetch);
                    Assert.Contains("franja de luz siguiente", fetch);
                }
            }

            Assert.Equal("No tocar la máquina.", replies[Luminomachine.DontTouchReply]);

            // And the sentences: the already-burning one is the only one different from the six, which is what
            // makes it the one of a floor that lacks no salt.
            Assert.Contains("alimentada por la energía de la sal", messages[Luminomachine.LitMessage]);
            for (int floor = 1; floor <= Luminomachine.Machines; floor++)
            {
                Assert.Equal("*se pone a temblar y a zumbar*", messages[Luminomachine.MessageFor(floor)]);
            }
        }

        /// <summary>
        /// The raid's loot is not in the monster's table, it is in the GLOBAL one: the Madrepeora
        /// has not a single row of its own and has nine there, with the salt at 30 % and the seven gems.
        /// </summary>
        [Fact]
        public void The_raid_loot_lives_in_the_global_table()
        {
            if (!File.Exists(Jondo.Unity.Launcher.Paths.WorldDb)) return;

            Assert.Empty(DatabaseManager.GetMonsterDrops(8324, 0));

            var global = DatabaseManager.GetMonsterGlobalDrops(8324);
            Assert.Equal(9, global.Count);

            var salt = global.Single(d => d.ObjectId == Luminomachine.SaltItem);
            Assert.Equal(30, salt.PercentDrop);
            Assert.Equal("", salt.ReceiverCriterion);

            // The seven gems, from the most common to the rarest, with no criterion at all.
            Assert.Equal(new[] { 30.0, 20.0, 10.0, 5.0, 1.0, 0.1, 0.5 },
                         new[] { 32465, 32466, 32467, 32468, 32469, 32470, 32471 }
                             .Select(id => global.Single(d => d.ObjectId == id).PercentDrop));

            // The Willorque, who guards the last floor, always drops it.
            var guard = DatabaseManager.GetMonsterGlobalDrops(8252);
            Assert.Equal(100, guard.Single(d => d.ObjectId == Luminomachine.SaltItem).PercentDrop);
        }

        /// <summary>
        /// And what is NOT known does not drop. Almost the whole game carries the anomaly fragment in the
        /// same table, with a criterion of which we cannot answer a single letter; without this rule, the
        /// day the global table was read it would start dropping everywhere.
        /// </summary>
        [Fact]
        public void What_cannot_be_answered_does_not_drop()
        {
            if (!File.Exists(Jondo.Unity.Launcher.Paths.WorldDb)) return;

            var fragment = DatabaseManager.GetMonsterGlobalDrops(8324).Single(d => d.ObjectId == 26870);
            Assert.Contains("HA=50", fragment.ReceiverCriterion);

            Assert.Equal(Answer.Unknown,
                Criterion.Evaluate(fragment.ReceiverCriterion, _ => Answer.Unknown));
            Assert.False(Criterion.Met(fragment.ReceiverCriterion, _ => Answer.Unknown));
        }

        /// <summary>8007's sentences and replies, just as the base has them.</summary>
        private static (Dictionary<long, string> Messages, Dictionary<long, string> Replies) MachineTexts()
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();

            var template = connection.CreateCommand();
            template.CommandText = "SELECT Data FROM NpcTemplates WHERE Id = $id;";
            template.Parameters.AddWithValue("$id", Luminomachine.NpcId);
            using var doc = JsonDocument.Parse((string)template.ExecuteScalar()!);

            string Text(long key)
            {
                var read = connection.CreateCommand();
                read.CommandText = "SELECT Text FROM Translations WHERE Key = $k;";
                read.Parameters.AddWithValue("$k", key.ToString());
                return read.ExecuteScalar() as string ?? "";
            }

            Dictionary<long, string> Pairs(string field)
            {
                var found = new Dictionary<long, string>();
                var list = doc.RootElement.GetProperty(field).GetProperty("Array");
                foreach (var entry in list.EnumerateArray())
                {
                    var values = entry.GetProperty("values").GetProperty("Array");
                    long id = values[0].GetInt64();
                    found[id] = Text(values[1].GetInt64());
                }

                return found;
            }

            return (Pairs("dialogMessages"), Pairs("dialogReplies"));
        }
    }

    /// <summary>
    /// Where the machines end up placed. It touches MapManager's static state, so it goes with
    /// the others that touch it.
    /// </summary>
    [Collection("MapManager")]
    public class LuminomachinePlacementTests
    {
        [Fact]
        public void One_machine_on_every_floor_that_has_light()
        {
            if (!File.Exists(Jondo.Unity.Launcher.Paths.WorldDb)) return;

            var antes = MapManager.WalkableCells;
            try
            {
                var sima = Raids.Of(Raids.Gigalodon);

                // Cells that do NOT include the centre, so that it shows the closest one is looked for
                // and the one asked for is not taken as good.
                var casillas = new Dictionary<long, List<int>>();
                foreach (int floor in sima.Floors)
                {
                    casillas[DatabaseManager.MapsOfSubArea(floor).Min()] = new List<int> { 7, 296 };
                }

                MapManager.WalkableCells = casillas;
                Luminomachines.Place();

                Assert.Equal(5, Luminomachines.Placed.Count);
                for (int i = 0; i < Luminomachines.Placed.Count; i++)
                {
                    var machine = Luminomachines.Placed[i];
                    Assert.Equal(i + 1, machine.Floor);
                    Assert.Equal(sima.Floors[i], machine.SubArea);
                    Assert.Equal(sima.Floors[i], DatabaseManager.SubAreaOfMap(machine.MapId));
                    Assert.Contains(machine.Cell, casillas[machine.MapId]);
                    Assert.Equal(i + 1, Luminomachines.FloorOn(machine.MapId));
                }

                // The sixth floor, the Gigalodón's, has no light variable and has no machine.
                Assert.Equal(6, sima.Floors.Count);
                Assert.Equal(0, Luminomachines.FloorOn(DatabaseManager.MapsOfSubArea(sima.Floors[5]).Min()));
            }
            finally
            {
                MapManager.WalkableCells = antes;
            }
        }
    }

    /// <summary>
    /// Pouring the salt: the light it buys is the INSTANCE's, so a raid in
    /// progress is needed and being inside it.
    /// </summary>
    [Collection("guild raids")]
    public class LuminomachineDepositTests : IDisposable
    {
        private readonly string _file;

        public LuminomachineDepositTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-luz-{Guid.NewGuid():N}.db");
            GuildStore.ConnectionStringOverride = $"Data Source={_file}";
            GuildRaidManager.Forget();
        }

        public void Dispose()
        {
            GuildRaidManager.Forget();
            GuildStore.ConnectionStringOverride = null;
            SqliteConnection.ClearAllPools();
            try { File.Delete(_file); } catch (IOException) { }
        }

        [Fact]
        public void Salt_buys_the_floor_its_light()
        {
            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            var raid = new RaidInstance(1, Raids.Gigalodon, guild.Id, 7001,
                                        DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
            raid.Add(7001);
            GuildRaidManager.Remember(raid);

            Assert.Equal(0, Luminomachines.LightOn(7001, 1));

            // Cuatro sales compran dos franjas de golpe.
            Assert.Equal(2, Luminomachines.Deposit(7001, 1, 0, 2));
            Assert.Equal(2, Luminomachines.LightOn(7001, 1));
            Assert.Equal(2, raid.Get(RaidInstance.LightVariable(1)));

            // And it can no longer be paid from where the floor is no longer there: it is the race of two who
            // talk to the same machine at once.
            Assert.Equal(-1, Luminomachines.Deposit(7001, 1, 0, 2));
            Assert.Equal(-1, Luminomachines.Deposit(7001, 1, 2, 2));

            Assert.Equal(4, Luminomachines.Deposit(7001, 1, 2, 4));
            Assert.Equal(-1, Luminomachines.Deposit(7001, 1, 4, 4));

            // Each floor carries its own: lighting the first does not light the second.
            Assert.Equal(0, Luminomachines.LightOn(7001, 2));
            Assert.Equal(0, raid.Get(RaidInstance.LightVariable(2)));

            // And the light bought is the one the content's criteria see. The Madrepeora roams
            // both floors with the same criterion written in world.db: on the first, already
            // lit, it stops aggressing; on the second, in the dark, it keeps jumping on you.
            if (File.Exists(Jondo.Unity.Launcher.Paths.WorldDb))
            {
                string criterion = DatabaseManager.MonsterAggressiveImmunity(8324);
                Assert.True(Criterion.Met(criterion, raid.ResolverFor(1131)));
                Assert.False(Criterion.Met(criterion, raid.ResolverFor(1132)));
            }
        }

        [Fact]
        public void Outside_a_raid_there_is_no_light_to_buy()
        {
            GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);

            Assert.Equal(-1, Luminomachines.LightOn(7001, 1));
            Assert.Equal(-1, Luminomachines.Deposit(7001, 1, 0, 1));

            // Not even with a guild: without a raid in progress there is no instance to light.
            Assert.Equal(-1, Luminomachines.LightOn(9999, 1));
        }
    }
}

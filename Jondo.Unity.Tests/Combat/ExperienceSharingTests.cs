using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    [Collection("guild raids")]
    public sealed class ExperienceSharingTests : IDisposable
    {
        private readonly string file = Path.Combine(Path.GetTempPath(), $"jondo-sharing-{Guid.NewGuid():N}.db");
        private const int Ebony = 33020;

        public ExperienceSharingTests() => GuildStore.ConnectionStringOverride = $"Data Source={file}";

        public void Dispose()
        {
            GuildStore.ConnectionStringOverride = null;
            TestDatabases.ReleasePool(file);
            // A concurrent session can still be closing a connection to the temporary guild store.
            // Match the other guild fixtures: a Windows cleanup lock must not fail the test.
            try { File.Delete(file); } catch (IOException) { }
        }

        [Fact]
        public void Guild_donation_is_taken_once_before_conversion()
        {
            var split = ExperienceSharing.Distribute(100000, 100, 0, 1, 10, 100);
            Assert.Equal(90000, split.Character); // Giny's extra deduction would leave 89100.
            Assert.Equal(10000, split.GuildTaken);
            Assert.Equal(1000, split.Guild);
        }

        [Fact]
        public void Mount_is_taken_first_and_guild_is_taken_from_the_remainder()
        {
            var split = ExperienceSharing.Distribute(100000, 100, 20, 100, 10, 100);
            Assert.Equal(20000, split.MountTaken);
            Assert.Equal(2000, split.Mount);
            Assert.Equal(8000, split.GuildTaken);
            Assert.Equal(800, split.Guild); // No Stump custom x10.
            Assert.Equal(72000, split.Character);
            Assert.Equal(100000, split.Character + split.MountTaken + split.GuildTaken);
        }

        [Theory]
        [InlineData(0, 1000)]
        [InlineData(10, 1000)]
        [InlineData(11, 800)]
        [InlineData(20, 800)]
        [InlineData(21, 600)]
        [InlineData(30, 600)]
        [InlineData(31, 400)]
        [InlineData(40, 400)]
        [InlineData(41, 300)]
        [InlineData(50, 300)]
        [InlineData(51, 200)]
        [InlineData(60, 200)]
        [InlineData(61, 150)]
        [InlineData(70, 150)]
        [InlineData(71, 100)]
        public void Conversion_uses_strict_reference_thresholds(int gap, long expected)
            => Assert.Equal(expected, ExperienceSharing.Convert(10000, gap + 1, 1));

        [Fact]
        public void Small_credits_do_not_return_the_donation_to_the_character()
        {
            var split = ExperienceSharing.Distribute(19, 200, 0, 1, 10, 1);
            Assert.Equal(18, split.Character);
            Assert.Equal(1, split.GuildTaken);
            Assert.Equal(0, split.Guild);
        }

        [Fact]
        public void Rates_persist_and_guild_rates_belong_to_membership()
        {
            var guild = GuildStore.Create(1, "Sharing", 1, 1, 1, 1);
            Assert.Equal(0, GuildStore.MemberOf(1).ExperiencePercent);
            Assert.Equal(0, ExperienceSharing.MountPercent(1));
            Assert.True(GuildStore.SetExperiencePercent(1, 25));
            Assert.True(ExperienceSharing.SetMountPercent(1, 40));
            Assert.False(GuildStore.SetExperiencePercent(1, 91));
            Assert.False(ExperienceSharing.SetMountPercent(1, -1));
            Assert.Equal(25, GuildStore.MemberOf(1).ExperiencePercent);
            Assert.Equal(25, Assert.Single(GuildStore.Members(guild.Id)).ExperiencePercent);
            Assert.Equal(40, ExperienceSharing.MountPercent(1));
            GuildStore.Leave(1);
            GuildStore.Join(1, guild.Id);
            Assert.Equal(0, GuildStore.MemberOf(1).ExperiencePercent);
            Assert.Equal(40, ExperienceSharing.MountPercent(1));
        }

        private static GameSession Session(long character, int level)
        {
            var session = GameSession.SinSocket();
            session.State.CharacterId = character;
            session.State.CharacterLevel = level;
            return session;
        }

        private Equipment.Item Mount(GameSession session, long uid, int position = Mounts.Slot)
        {
            Assert.True(Rides.IsRide(Ebony), "The versioned breeding catalogue must contain the ebony ride.");
            var effects = Rides.WithExperience(Ebony, Rides.Fresh(Ebony, new Random(3)), Breeding.ExperienceAt(20));
            string json = Forgemagic.Serialize(effects);
            using (var connection = GuildStore.Open())
            {
                using var command = connection.CreateCommand();
                command.CommandText = @"CREATE TABLE IF NOT EXISTS CharacterItems (
                    CharacterId INTEGER, Uid INTEGER PRIMARY KEY, Gid INTEGER, Quantity INTEGER,
                    Position INTEGER, Effects TEXT);
                    INSERT INTO CharacterItems VALUES ($c, $u, $gid, 1, $slot, $e);";
                command.Parameters.AddWithValue("$c", session.State.CharacterId);
                command.Parameters.AddWithValue("$u", uid);
                command.Parameters.AddWithValue("$gid", Ebony);
                command.Parameters.AddWithValue("$slot", position);
                command.Parameters.AddWithValue("$e", json);
                command.ExecuteNonQuery();
            }
            using var context = SessionContext.Push(session);
            return Equipment.Add(uid, Ebony, 1, position, json);
        }

        [Fact]
        public void Mount_and_guild_receive_real_persistent_XP_without_double_credit()
        {
            var guild = GuildStore.Create(11, "Sharing", 1, 1, 1, 1);
            GuildStore.SetExperiencePercent(11, 10);
            ExperienceSharing.SetMountPercent(11, 20);
            var session = Session(11, 20);
            var mount = Mount(session, 12001);
            long before = Rides.ExperienceOf(mount.Effects);
            var plan = ExperienceSharing.Prepare(session, 100000);
            Assert.Equal(72000, plan.Amounts.Character);
            Assert.Equal(2000, plan.Amounts.Mount);
            Assert.Equal(640, plan.Amounts.Guild); // Character 20, guild 1: gap 19 -> 8% of 8000.
            Assert.Equal(before, Rides.ExperienceOf(mount.Effects)); // Preparing does not credit.
            ExperienceSharing.Credit(new[] { plan });
            ExperienceSharing.Credit(new[] { plan });
            Assert.Equal(before + 2000, Rides.ExperienceOf(mount.Effects));
            Assert.True(Rides.LevelOf(mount.Effects) > 20);
            Assert.Equal(640, GuildStore.GuildOf(11).Experience);
            Assert.Equal(640, GuildStore.MemberOf(11).Experience);
            Assert.Equal(GuildLevels.LevelFor(640), GuildStore.GuildOf(11).Level);
            using var connection = GuildStore.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Effects FROM CharacterItems WHERE Uid = 12001;";
            var stored = Equipment.ParseEffects((string)command.ExecuteScalar()!);
            Assert.Equal(before + 2000, Rides.ExperienceOf(stored));
            Assert.Equal(Rides.LevelOf(mount.Effects), Rides.LevelOf(stored));
        }

        [Fact]
        public void An_unequipped_mount_and_a_missing_guild_take_nothing()
        {
            var session = Session(12, 20);
            Mount(session, 12002, Equipment.Bag);
            ExperienceSharing.SetMountPercent(12, 90);
            var plan = ExperienceSharing.Prepare(session, 10000);
            Assert.Equal(10000, plan.Amounts.Character);
            Assert.Equal(0, plan.Amounts.MountTaken);
            Assert.Equal(0, plan.Amounts.GuildTaken);
        }

        [Fact]
        public void Group_contributions_use_the_guild_level_before_any_credit()
        {
            var guild = GuildStore.Create(21, "Sharing", 1, 1, 1, 1);
            GuildStore.Join(22, guild.Id);
            GuildStore.SetExperiencePercent(21, 90);
            GuildStore.SetExperiencePercent(22, 90);
            var a = ExperienceSharing.Prepare(Session(21, 20), 100000);
            var b = ExperienceSharing.Prepare(Session(22, 20), 100000);
            Assert.Equal(7200, a.Amounts.Guild);
            Assert.Equal(a.Amounts.Guild, b.Amounts.Guild);
            ExperienceSharing.Credit(new[] { a, b });
            Assert.Equal(14400, GuildStore.GuildOf(21).Experience);
        }

        [Fact]
        public async Task A_member_cannot_change_someone_elses_share()
        {
            // Do not share IDs with concurrent network tests' registered sessions.
            const long leader = 9_761_331, member = 9_761_332, stranger = 9_761_399;
            var guild = GuildStore.Create(leader, "Sharing", 1, 1, 1, 1);
            GuildStore.Join(member, guild.Id);
            Assert.False(await GuildHandler.SetExperienceShareAsync(member, leader, 90));
            Assert.False(await GuildHandler.SetExperienceShareAsync(member, stranger, 90));
            Assert.True(await GuildHandler.SetExperienceShareAsync(member, member, 25));
            Assert.Equal(25, GuildStore.MemberOf(member).ExperiencePercent);
            Assert.Equal(0, GuildStore.MemberOf(leader).ExperiencePercent);
        }

        [Fact]
        public async Task A_native_contribution_request_changes_XP_without_changing_rank()
        {
            const long leader = 9_761_341, member = 9_761_342;
            var guild = GuildStore.Create(leader, "Sharing", 1, 1, 1, 1);
            var joined = GuildStore.Join(member, guild.Id);
            await using var wire = await PortalTests.Wire.Open(member);
            using (SessionContext.Push(wire.Session))
            {
                await GuildHandler.AssignRankAsync(wire.Session.Stream, ConnectionProtocol.Push(Op.Jgv,
                    Pb.New().Var(2, member).Var(3, 40).Build()));
                Assert.Equal(40, GuildStore.MemberOf(member).ExperiencePercent);
                Assert.Equal(joined.Rank, GuildStore.MemberOf(member).Rank);
                // A proto3 zero share may be omitted entirely.
                await GuildHandler.AssignRankAsync(wire.Session.Stream, ConnectionProtocol.Push(Op.Jgv,
                    Pb.New().Var(2, member).Build()));
                Assert.Equal(0, GuildStore.MemberOf(member).ExperiencePercent);
                Assert.Equal(joined.Rank, GuildStore.MemberOf(member).Rank);
            }
        }
    }
}

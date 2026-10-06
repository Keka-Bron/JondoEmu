using System;
using System.IO;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Buying a raid, launching it and the clock, against a pass-through base; and the resolver reading
    /// the criterion the monster itself brings written in world.db.
    /// </summary>
    [Collection("guild raids")]
    public class GuildRaidTests : IDisposable
    {
        private readonly string _file;

        public GuildRaidTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-raid-{Guid.NewGuid():N}.db");
            GuildStore.ConnectionStringOverride = $"Data Source={_file}";
            GuildRaidManager.Forget();
        }

        public void Dispose()
        {
            GuildRaidManager.Forget();
            GuildStore.ConnectionStringOverride = null;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(_file); } catch (IOException) { }
        }

        private static GuildStore.Guild Guild() => GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);

        /// <summary>
        /// Buying costs the guild's kamas, and one does not buy twice nor without having them. The
        /// prices are those of the game's sheet: 360 the Sima and 480 the Santuario.
        /// </summary>
        [Fact]
        public void Buying_a_raid_costs_the_guild_its_kamas()
        {
            var guild = Guild();
            Assert.Equal("raid.nokamas", GuildRaidManager.Buy(7001, Raids.Gigalodon));

            for (int i = 0; i < 5; i++) GuildStore.Contribute(7001, guild.Id);   // 50 guild kamas
            Assert.Equal("raid.nokamas", GuildRaidManager.Buy(7001, Raids.Gigalodon));

            // By hand, which is what 36 contributions would cost.
            GuildStore.SpendGuildKamas(guild.Id, -400);
            Assert.Equal(450, GuildStore.GuildOf(7001).GuildKamas);

            Assert.Null(GuildRaidManager.Buy(7001, Raids.Gigalodon));
            Assert.Equal(90, GuildStore.GuildOf(7001).GuildKamas);      // 450 - 360
            Assert.True(GuildStore.OwnsRaid(guild.Id, Raids.Gigalodon));
            Assert.Equal(new[] { Raids.Gigalodon }, GuildStore.OwnedRaids(guild.Id));

            Assert.Equal("raid.owned", GuildRaidManager.Buy(7001, Raids.Gigalodon));
            Assert.Equal("raid.unknown", GuildRaidManager.Buy(7001, 99));
            Assert.Equal(360, Raids.Of(Raids.Gigalodon).Price);
            Assert.Equal(480, Raids.Of(Raids.EternalGardens).Price);
        }

        /// <summary>Without a guild there is no raid to buy or to launch.</summary>
        [Fact]
        public void With_no_guild_there_is_no_raid()
        {
            Assert.Equal("raid.noguild", GuildRaidManager.Buy(7001, Raids.Gigalodon));
            Assert.Null(GuildRaidManager.RunningOf(7001));
            Assert.Null(GuildRaidManager.RaidOf(7001));
        }

        /// <summary>
        /// The clock: the Sima lasts an hour and the Santuario two, and a raid closed by the captain
        /// keeps whatever score it had.
        /// </summary>
        [Fact]
        public void The_clock_runs_for_what_the_raid_lasts()
        {
            Assert.Equal(TimeSpan.FromHours(1), Raids.Of(Raids.Gigalodon).RunsFor);
            Assert.Equal(TimeSpan.FromHours(2), Raids.Of(Raids.EternalGardens).RunsFor);

            var empezo = DateTimeOffset.UtcNow;
            var raid = new RaidInstance(1, Raids.Gigalodon, 42043, 7001, empezo, TimeSpan.FromHours(1));
            raid.Add(7001);
            raid.Add(RaidInstance.ScoreVariable, 0);
            raid.Add(RaidInstance.ScoreVariable, 12000);
            GuildRaidManager.Remember(raid);

            var queda = raid.Left(empezo.AddMinutes(1));
            Assert.InRange(queda, TimeSpan.FromMinutes(58.9), TimeSpan.FromMinutes(59.1));

            raid.Finish(RaidInstance.Ending.Captain, empezo.AddMinutes(10));
            Assert.False(raid.Running);
            Assert.Equal(12000, raid.Score);
        }

        /// <summary>
        /// The resolver, with the criterion the monster brings in world.db: the Sima's Madrepeora (8324)
        /// is immune to aggression while its floor has light, and stops being so in
        /// the dark. There is no hand-written rule: the criterion comes from the base.
        /// </summary>
        [Fact]
        public void The_monsters_criterion_comes_out_of_the_database()
        {
            if (!File.Exists(Jondo.Unity.Launcher.Paths.WorldDb)) return;

            string criterion = DatabaseManager.MonsterAggressiveImmunity(8324);
            Assert.Contains("RV!7,n1_worldlight,0", criterion);
            Assert.Contains("PB=1131", criterion);

            var raid = new RaidInstance(1, Raids.Gigalodon, 42043, 7001, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
            raid.Set(RaidInstance.LightVariable(1), 4);
            Assert.True(Criterion.Met(criterion, raid.ResolverFor(1131)));       // lit, at peace

            raid.Set(RaidInstance.LightVariable(1), 0);
            Assert.False(Criterion.Met(criterion, raid.ResolverFor(1131)));      // a oscuras, encima
        }

        /// <summary>
        /// The floors and the map one enters through come from the base: the Sima's first
        /// floor is subarea 1131 and its maps are all there.
        /// </summary>
        [Fact]
        public void The_floors_and_their_maps_are_in_the_database()
        {
            if (!File.Exists(Jondo.Unity.Launcher.Paths.WorldDb)) return;

            var sima = Raids.Of(Raids.Gigalodon);
            var primera = DatabaseManager.MapsOfSubArea(sima.Floors[0]);
            Assert.Equal(14, primera.Count);

            long entrada = GuildRaidManager.EntryMapOf(sima);
            Assert.Contains(entrada, primera);
            Assert.Equal(1131, DatabaseManager.SubAreaOfMap(entrada));
            Assert.Equal(1, sima.FloorOf(GuildRaidManager.SubAreaOf(entrada)));

            // And the six floors add up to the 73 maps the client brings.
            int total = 0;
            foreach (int floor in sima.Floors) total += DatabaseManager.MapsOfSubArea(floor).Count;
            Assert.Equal(73, total);

            int jardines = 0;
            foreach (int zone in Raids.Of(Raids.EternalGardens).Floors)
                jardines += DatabaseManager.MapsOfSubArea(zone).Count;
            Assert.Equal(51, jardines);
        }

        /// <summary>Outside a raid, a raid criterion is unknown: it is neither met nor failed.</summary>
        [Fact]
        public void Outside_a_raid_the_resolver_knows_nothing()
        {
            Guild();
            var resolver = GuildRaidManager.ResolverFor(7001);
            Assert.Equal(Answer.Unknown, Criterion.Evaluate("RV<7,Raid_Score,5000", resolver));
            Assert.False(GuildRaidManager.MonsterWouldAggress(7001, 8324));
        }
    }
}

using System;
using System.IO;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Comprar una raid, lanzarla y el reloj, contra una base de paso; y el resolvedor leyendo
    /// el criterio que el propio monstruo trae escrito en world.db.
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
        /// Comprar cuesta los kamas del gremio, y no se compra dos veces ni sin tenerlos. Los
        /// precios son los de la ficha del juego: 360 la Sima y 480 el Santuario.
        /// </summary>
        [Fact]
        public void Buying_a_raid_costs_the_guild_its_kamas()
        {
            var guild = Guild();
            Assert.Equal("raid.nokamas", GuildRaidManager.Buy(7001, Raids.Gigalodon));

            for (int i = 0; i < 5; i++) GuildStore.Contribute(7001, guild.Id);   // 50 kamas de gremio
            Assert.Equal("raid.nokamas", GuildRaidManager.Buy(7001, Raids.Gigalodon));

            // A mano, que es lo que costarían 36 contribuciones.
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

        /// <summary>Sin gremio no hay raid que comprar ni que lanzar.</summary>
        [Fact]
        public void With_no_guild_there_is_no_raid()
        {
            Assert.Equal("raid.noguild", GuildRaidManager.Buy(7001, Raids.Gigalodon));
            Assert.Null(GuildRaidManager.RunningOf(7001));
            Assert.Null(GuildRaidManager.RaidOf(7001));
        }

        /// <summary>
        /// El reloj: la Sima dura una hora y el Santuario dos, y una raid cerrada por el capitán
        /// se queda con la puntuación que llevara.
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
        /// El resolvedor, con el criterio que el monstruo trae en world.db: la Madrepeora (8324)
        /// de la Sima es inmune a la agresión mientras su planta tenga luz, y deja de serlo a
        /// oscuras. No hay ninguna regla escrita a mano: el criterio sale de la base.
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
            Assert.True(Criterion.Met(criterion, raid.ResolverFor(1131)));       // con luz, en paz

            raid.Set(RaidInstance.LightVariable(1), 0);
            Assert.False(Criterion.Met(criterion, raid.ResolverFor(1131)));      // a oscuras, encima
        }

        /// <summary>
        /// Las plantas y el mapa por el que se entra salen de la base: la primera planta de la
        /// Sima es la subárea 1131 y sus mapas están todos ahí.
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

            // Y las seis plantas suman los 73 mapas que trae el cliente.
            int total = 0;
            foreach (int floor in sima.Floors) total += DatabaseManager.MapsOfSubArea(floor).Count;
            Assert.Equal(73, total);

            int jardines = 0;
            foreach (int zone in Raids.Of(Raids.EternalGardens).Floors)
                jardines += DatabaseManager.MapsOfSubArea(zone).Count;
            Assert.Equal(51, jardines);
        }

        /// <summary>Fuera de una raid, un criterio de raid no se sabe: ni se cumple ni se incumple.</summary>
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

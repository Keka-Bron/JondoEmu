using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World;

[Collection("Heroes gameplay")]
public sealed class HeroZaapDiscoveryTests
{
    [Fact]
    public async Task A_hero_beside_his_master_discovers_the_zaap_with_him()
    {
        using var package = new HeroesTestPackage();
        const long master = 8_861_100_011, hero = 8_861_100_012, stranger = 8_861_100_013;
        MapManager.Initialize();
        Interactives.Initialize();

        using var database = new SqliteConnection(DatabaseManager.WorldConnectionString);
        database.Open();
        ZaapDiscoveries.EnsureTable(database);
        using var insert = database.CreateCommand();
        insert.CommandText = """
            INSERT INTO Characters (Id, AccountId, Name, Breed, Sex) VALUES
                ($master, $master, 'ZaapMasterTest', 1, 0),
                ($hero, $master, 'ZaapHeroTest', 2, 0),
                ($stranger, $stranger, 'ZaapStrangerTest', 3, 0);
            """;
        insert.Parameters.AddWithValue("$master", master);
        insert.Parameters.AddWithValue("$hero", hero);
        insert.Parameters.AddWithValue("$stranger", stranger);
        insert.ExecuteNonQuery();

        var settings = ServerSettings.Current;
        bool previous = settings.RequireZaapDiscovery;
        try
        {
            settings.RequireZaapDiscovery = false;
            long zaap = ZaapDiscoveries.KnownMaps(master).First(map => MapManager.GetMapInfo(map) != null);
            settings.RequireZaapDiscovery = true;
            Assert.Empty(ZaapDiscoveries.KnownMaps(hero));

            await using var pipe = await ClientPipe.OpenAsync(master, master, zaap);
            Assert.Equal("ok", package.Groups.Connect(pipe.Session, hero));

            Assert.Equal(zaap, CompanionSessions.SessionOf(hero)?.MapId);
            Assert.Equal(zaap, Assert.Single(ZaapDiscoveries.KnownMaps(hero)));
            Assert.Empty(ZaapDiscoveries.KnownMaps(stranger));
        }
        finally
        {
            package.Groups.DisconnectAll(master);
            settings.RequireZaapDiscovery = previous;
            using var delete = database.CreateCommand();
            delete.CommandText = """
                DELETE FROM CharacterZaapDiscoveries WHERE CharacterId IN ($master, $hero, $stranger);
                DELETE FROM Characters WHERE Id IN ($master, $hero, $stranger);
                """;
            delete.Parameters.AddWithValue("$master", master);
            delete.Parameters.AddWithValue("$hero", hero);
            delete.Parameters.AddWithValue("$stranger", stranger);
            delete.ExecuteNonQuery();
        }
    }
}

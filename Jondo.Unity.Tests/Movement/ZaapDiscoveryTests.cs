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

namespace Jondo.Unity.Tests.Movement;

[Collection("MapManager")]
public sealed class ZaapDiscoveryTests
{
    [Fact]
    public async Task Ordinary_zaap_discovery_is_character_owned_and_controls_travel()
    {
        const long character = 8_861_100_001, other = 8_861_100_002;
        Assert.True(new ServerSettings().RequireZaapDiscovery);
        MapManager.Initialize();
        Interactives.Initialize();

        using var database = new SqliteConnection(DatabaseManager.WorldConnectionString);
        database.Open();
        ZaapDiscoveries.EnsureTable(database);
        using var insert = database.CreateCommand();
        insert.CommandText = """
            INSERT INTO Characters (Id, AccountId, Name, Breed, Sex)
            VALUES ($character, $character, 'ZaapDiscoveryTest', 1, 0);
            """;
        insert.Parameters.AddWithValue("$character", character);
        insert.ExecuteNonQuery();

        var settings = ServerSettings.Current;
        bool previous = settings.RequireZaapDiscovery;
        try
        {
            settings.RequireZaapDiscovery = false;
            var destinations = ZaapDiscoveries.KnownMaps(character)
                .Where(map => MapManager.GetMapInfo(map) != null).Take(2).ToArray();
            Assert.Equal(2, destinations.Length);
            long origin = destinations[0], destination = destinations[1];

            settings.RequireZaapDiscovery = true;
            Assert.Empty(ZaapDiscoveries.KnownMaps(character));
            await using var pipe = await ClientPipe.OpenAsync(character, character, origin);
            using var context = SessionContext.Push(pipe.Session);
            pipe.Session.State.OpenZaapMapId = origin;
            pipe.Session.State.Kamas = 10_000;
            byte[] travel = ConnectionProtocol.Push(Op.Hjc, Pb.New().Var(3, destination).Build());
            await ZaapTravelHandler.TravelAsync(pipe.ToClient, travel);
            Assert.Equal(origin, pipe.Session.State.MapId);
            Assert.Equal(10_000, pipe.Session.State.Kamas);

            settings.RequireZaapDiscovery = false;
            Assert.Contains(destination, ZaapDiscoveries.KnownMaps(other));
            Assert.True(ZaapDiscoveries.Discover(character, destination));
            Assert.False(ZaapDiscoveries.Discover(character, destination));
            Assert.False(ZaapDiscoveries.Discover(other, destination));

            settings.RequireZaapDiscovery = true;
            Assert.Equal(destination, Assert.Single(ZaapDiscoveries.KnownMaps(character)));
            Assert.Empty(ZaapDiscoveries.KnownMaps(other));
            await ZaapTravelHandler.TravelAsync(pipe.ToClient, travel);
            Assert.Equal(destination, pipe.Session.State.MapId);
            Assert.True(pipe.Session.State.Kamas < 10_000);
        }
        finally
        {
            settings.RequireZaapDiscovery = previous;
            using var delete = database.CreateCommand();
            delete.CommandText = """
                DELETE FROM CharacterZaapDiscoveries WHERE CharacterId = $character;
                DELETE FROM Characters WHERE Id = $character;
                """;
            delete.Parameters.AddWithValue("$character", character);
            delete.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task A_hero_beside_his_master_discovers_the_zaap_with_him()
    {
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
            var follower = new Heroes.Hero { CharacterId = hero, MasterId = master, Session = GameSession.SinSocket() };
            follower.Session.State.CharacterId = hero;
            await HeroPresence.ArriveAsync(pipe.Session, follower);

            Assert.Equal(zaap, follower.Session.State.MapId);
            Assert.Equal(zaap, Assert.Single(ZaapDiscoveries.KnownMaps(hero)));
            Assert.Empty(ZaapDiscoveries.KnownMaps(stranger));
        }
        finally
        {
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

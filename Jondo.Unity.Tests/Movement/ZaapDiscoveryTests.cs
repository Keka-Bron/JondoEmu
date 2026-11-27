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
}

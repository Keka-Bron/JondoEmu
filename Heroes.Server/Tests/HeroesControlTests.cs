using System.Net;
using System.Security.Cryptography;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Mods;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World;

[CollectionDefinition("Heroes gameplay", DisableParallelization = true)]
public sealed class HeroesGameplayCollection;

[Collection("Heroes gameplay")]
public sealed class HeroesControlTests
{
    private const long Account = 8_900_000_312, StrangerAccount = Account + 1;
    private const long Map = 990_312_001;
    private static readonly string[] Routes =
    [
        "list", "connect", "disconnect", "portrait", "quests", "autopass",
        "loot", "sheet", "switch", "create", "name", "preview",
    ];

    [Fact]
    public async Task Package_owns_the_authenticated_Heroes_HTTP_routes()
    {
        string packages = HeroesTestPackage.Directory;
        var settings = ServerSettings.Read("""{"ServerMods":["HEROES","heroes"]}""");
        var messages = new List<string>();
        using var enabled = ServerModLoader.LoadConfigured(packages, settings, messages.Add);
        Assert.Contains("heroes (heroes) initialized", Assert.Single(messages));
        using var disabled = ServerModLoader.LoadConfigured(packages,
            ServerSettings.Read("""{"HeroMode":false,"ServerMods":["HEROES"]}"""), _ => { });
        var catalog = new ClientModCatalog(packages, []);

        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        using var listener = new HttpListener();
        string address = $"http://localhost:{port}/";
        listener.Prefixes.Add(address);
        listener.Start();
        using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
        string token = Guid.NewGuid().ToString("N");
        ClientLaunchRegistry.RegisterToken(Account, token);

        async Task<string> Send(string route, object payload, int status, ServerModControlRoutes? routes,
            HttpMethod? method = null)
        {
            using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/heroes/" + route)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            var pending = http.SendAsync(request);
            var context = await listener.GetContextAsync().WaitAsync(http.Timeout);
            await HaapiServer.HandleHaapiRequestAsync(context, catalog, routes).WaitAsync(http.Timeout);
            using var reply = await pending;
            Assert.Equal(status, (int)reply.StatusCode);
            Assert.Equal("application/json; charset=utf-8", reply.Content.Headers.ContentType?.ToString());
            return await reply.Content.ReadAsStringAsync();
        }

        Task<string> Ask(string route, object payload, int status = 200, HttpMethod? method = null)
            => Send(route, payload, status, enabled.ControlRoutes, method);

        foreach (string route in Routes)
        {
            Assert.Equal("""{"error":"sesion"}""", await Ask(route, new { }, 401));
            Assert.Equal("""{"error":"ruta"}""", await Send(route, new { token }, 404, disabled.ControlRoutes));
            Assert.Equal("""{"error":"ruta"}""", await Send(route, new { token }, 404, null));
        }
        Assert.Equal("""{"error":"sesion"}""", await Ask("list", new { token = Guid.NewGuid().ToString("N") }, 401));
        Assert.Equal("""{"error":"world"}""", await Ask("list", new { token }, 409));
        using (var name = JsonDocument.Parse(await Ask("name", new { token }, method: HttpMethod.Put)))
            Assert.False(string.IsNullOrWhiteSpace(name.RootElement.GetProperty("name").GetString()));
        Assert.Equal("""{"error":"breed"}""", await Ask("preview", new { token, breed = 0 }, 400));

        long master = Create(Account, "Maestro");
        long hero = Create(Account, "Heroe");
        long stranger = Create(StrangerAccount, "Ajeno");
        var previousSettings = ServerSettings.Current;
        ServerSettings.UseForTests(settings);
        try
        {
            await using var pipe = await ClientPipe.OpenAsync(Account, master, Map, "Maestro");
            Assert.Equal("""{"error":"account"}""", await Ask("connect", new { token, id = stranger, accountId = StrangerAccount }, 409));
            Assert.Equal("""{"error":"character"}""", await Ask("portrait", new { token, id = stranger }, 404));
            Assert.Equal("""{"error":"account"}""", await Ask("connect", new { token, id = hero.ToString() }, 409));
            Assert.Equal("""{"error":"empty"}""", await Ask("create", new { token, name = " " }, 409));
            Assert.Equal("""{"connected":true}""", await Ask("connect", new { token, id = hero }));
            Assert.Equal("""{"error":"already"}""", await Ask("connect", new { token, id = hero }, 409));
            Assert.Equal("""{"error":"none"}""", await Ask("autopass", new { token, id = stranger, on = 1 }, 409));
            Assert.Equal("""{"error":"none"}""", await Ask("loot", new { token, id = stranger }, 409));
            Assert.Equal($"{{\"lootTo\":{hero}}}", await Ask("loot", new { token, id = hero }));
            Assert.Equal("""{"error":"hero"}""", await Ask("sheet", new { token, id = stranger }, 404));
            await using (var other = await ClientPipe.OpenAsync(StrangerAccount, stranger, Map + 1, "Ajeno"))
            {
                string otherToken = Guid.NewGuid().ToString("N");
                ClientLaunchRegistry.RegisterToken(StrangerAccount, otherToken);
                Assert.Equal("""{"error":"hero"}""", await Ask("sheet", new { token = otherToken, id = hero }, 404));
                Assert.Equal("""{"error":"none"}""", await Ask("disconnect", new { token = otherToken, id = hero }, 409));
            }
            Assert.Equal("""{"autoPass":true}""", await Ask("autopass", new { token, id = hero, on = 1 }));
            using (var list = JsonDocument.Parse(await Ask("list", new { token })))
            {
                Assert.True(list.RootElement.GetProperty("on").GetBoolean());
                Assert.Equal(2, list.RootElement.GetProperty("connected").GetInt32());
                var characters = list.RootElement.GetProperty("characters");
                Assert.Equal(master, characters[0].GetProperty("id").GetInt64());
                Assert.Equal(hero, characters[1].GetProperty("id").GetInt64());
                Assert.True(characters[1].GetProperty("autoPass").GetBoolean());
            }
            Assert.Equal("""{"disconnected":true}""", await Ask("disconnect", new { token, id = hero }));
            Assert.Equal("""{"error":"none"}""", await Ask("disconnect", new { token, id = hero }, 409));
            Assert.Equal("""{"connected":true}""", await Ask("connect", new { token, id = hero }));
            Assert.True(Parties.IsInParty(hero));
            Assert.True(SessionRegistry.IsOnline(hero));
            CompanionSessions.SessionOf(hero)!.State.CellId = 123;

            await SessionTeardown.LeaveAsync(pipe.Session, "Heroes test").WaitAsync(http.Timeout);
            Assert.Empty(CompanionSessions.Of(master));
            Assert.False(SessionRegistry.IsOnline(hero));
            Assert.Null(Parties.Of(hero));
            using var database = new SqliteConnection(DatabaseManager.WorldConnectionString);
            database.Open();
            using var cell = database.CreateCommand();
            cell.Parameters.AddWithValue("$hero", hero);
            cell.CommandText = "SELECT CellId FROM Characters WHERE Id = $hero";
            Assert.Equal(123L, cell.ExecuteScalar());

            cell.CommandText = "UPDATE Characters SET CellId = 124 WHERE Id = $hero";
            Assert.Equal(1, cell.ExecuteNonQuery());
            await SessionTeardown.LeaveAsync(pipe.Session, "Heroes test").WaitAsync(http.Timeout);
            cell.CommandText = "SELECT CellId FROM Characters WHERE Id = $hero";
            Assert.Equal(124L, cell.ExecuteScalar());

            await using var back = await ClientPipe.OpenAsync(Account, master, Map, "Maestro");
            CompanionSessions.Regroup(back.Session);
            CompanionSessions.OnSessionLeaving(pipe.Session);
            Assert.Equal(hero, Assert.Single(CompanionSessions.Of(master)).CharacterId);
            enabled.Dispose();
            enabled.Dispose();
            Assert.False(SessionRegistry.IsOnline(hero));
            Assert.Null(Parties.Of(hero));
        }
        finally
        {
            enabled.Dispose();
            DatabaseManager.DeleteCharacter(master, Account);
            DatabaseManager.DeleteCharacter(hero, Account);
            DatabaseManager.DeleteCharacter(stranger, StrangerAccount);
            ServerSettings.UseForTests(previousSettings);
        }

        enabled.Dispose();
        foreach (string route in Routes)
            Assert.Equal("""{"error":"ruta"}""", await Ask(route, new { token }, 404));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_sessions_work_and_only_the_active_package_exposes_routes_and_assets(bool heroes)
    {
        string packages = HeroesTestPackage.Directory;
        var settings = new ServerSettings { ServerMods = heroes ? ["heroes"] : [] };
        using var mods = ServerModLoader.LoadConfigured(packages, settings, _ => { });
        mods.StartGameplay();
        Assert.Equal(heroes, CompanionSessions.Policy != null);

        var catalog = new ClientModCatalog(Path.Combine(packages, "standalone"), settings.ClientMods, mods.ClientAssets);
        string[] expectedAssets = heroes ? ["Heroes.dll"] : [];
        Assert.Equal(expectedAssets, catalog.CreateManifest().Mods.Select(entry => entry.Name));
        using var download = catalog.OpenDownload("Heroes.dll");
        Assert.Equal(heroes, download != null);
        if (download != null)
            Assert.Equal(SHA256.HashData(File.ReadAllBytes(Path.Combine(packages, "heroes", "Heroes.dll"))),
                SHA256.HashData(download));

        foreach (string route in Routes)
            Assert.Equal(heroes ? 401 : 404,
                ControlApi.Responder("/api/heroes/" + route, "POST", "{}", "", catalog, mods.ControlRoutes)!.Value.Codigo);

        long character = Create(Account, "Solo");
        long other = Create(StrangerAccount, "Ajeno");
        try
        {
            await using var pipe = await ClientPipe.OpenAsync(Account, 0, Map, enterWorld: false);
            using (SessionContext.Push(pipe.Session))
            {
                Assert.False(CharacterSelectionHandler.SelectCharacterOnServer(other, pipe.Session));
                Assert.True(CharacterSelectionHandler.SelectCharacterOnServer(character, pipe.Session));
            }

            var entry = new GameEntryService(pipe.Session, new GameEntryBackend());
            Assert.True(await entry.EnterWorldAsync());
            Assert.Equal(character, pipe.Session.CharacterId);
            Assert.True(SessionRegistry.IsOnline(character));
            Assert.Empty(CompanionSessions.Of(character));
            Assert.Null(Parties.Of(character));

            await SessionTeardown.LeaveAsync(pipe.Session, "Heroes absent test");
            Assert.False(pipe.Session.IsInWorld);
            Assert.False(SessionRegistry.IsOnline(character));
            Assert.Equal(character, DatabaseManager.GetCharacterById(character)?.Id);

            mods.Dispose();
            Assert.Null(CompanionSessions.Policy);
            foreach (string route in Routes.Select(route => "/api/heroes/" + route))
                Assert.Equal(404, ControlApi.Responder(route, "POST", "{}", "", catalog, mods.ControlRoutes)!.Value.Codigo);

        }
        finally
        {
            DatabaseManager.DeleteCharacter(character, Account);
            DatabaseManager.DeleteCharacter(other, StrangerAccount);
        }
    }

    private static long Create(long account, string name)
        => DatabaseManager.CreateCharacter(account, 1, name + "-" + Guid.NewGuid().ToString("N")[..6], 8, 0, 1,
            [-1, -1, -1, -1, -1], Map, 50, 0, 0, []);
}

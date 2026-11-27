using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Cazacabezas.Server;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Mods;
using Jondo.Unity.Server.Network;
using Jondo.Unity.ServerMods;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Fights;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    [CollectionDefinition("HeadHunts", DisableParallelization = true)]
    public class HeadHuntsCollection { }

    /// <summary>
    /// The Cazacabezas end to end, with sessions in the world: the board, the order, the hunt that
    /// opens when both are in, the prey's wings held out, and the head paid -- or the prey paid.
    /// </summary>
    [Collection("HeadHunts")]
    public class HeadHuntTests
    {
        private const long HunterAccount = 8_980_000_001, PreyAccount = 8_980_000_002;
        private const long Map = 191105026;
        private static string Packages => Path.Combine(AppContext.BaseDirectory, "ServerModFixtures");

        private static HeadHunts OpenStorage()
        {
            var world = new AlignmentWorld();
            return new HeadHunts(world, world, world, new HuntSettings(), Console.WriteLine);
        }

        private static long Create(long account, string name, int level)
            => DatabaseManager.CreateCharacter(account, 1, name, 8, 0, 1, new long[] { -1, -1, -1, -1, -1 },
                                               Map, level, 0, 0, Array.Empty<(int, int)>());

        private static void Sworn(long id, int side)
        {
            Alignments.Set(id, side, 0);
            Alignments.SetPvp(id, true);
        }

        private static FightInstance Aggression(long blue, int blueLevel, long red, int redLevel)
        {
            var fight = new FightInstance(980_001, Map) { Reglas = FightRules.Agresion };
            fight.Azul.Add(new Fighter { Id = blue, Level = blueLevel, MaxHP = 100, CurrentHP = 100 });
            fight.Rojo.Add(new Fighter { Id = red, Level = redLevel, MaxHP = 100, CurrentHP = 0 });
            return fight;
        }

        private static async Task WithHunt(Func<HeadHunts, long, long, ClientPipe, ClientPipe, Task> test, string tag)
        {
            var mod = new CazacabezasMod();
            var settings = new ServerSettings { ServerMods = ["cazacabezas"] };
            using var loaded = ServerModLoader.LoadConfigured(Packages, settings, Console.WriteLine, _ => mod);
            var hunts = mod.Hunts!;
            long hunter = Create(HunterAccount, "Cazador-" + tag, 100);
            long prey = Create(PreyAccount, "Presa-" + tag, 110);
            try
            {
                // Ids come back after a character is deleted: nothing of an earlier one's hunts stays.
                hunts.CharacterDeleted(hunter);
                hunts.CharacterDeleted(prey);
                Sworn(hunter, Alignments.Bonta);
                Sworn(prey, Alignments.Brakmar);
                await using var h = await ClientPipe.OpenAsync(HunterAccount, hunter, Map, "Cazador-" + tag);
                h.Session.State.CharacterLevel = 100;

                var board = hunts.CandidatesFor(hunter, 100);
                Assert.Contains(board, c => c.Id == prey && !c.Online);
                Assert.Equal("ok", await hunts.TakeAsync(hunter, 100, prey));
                Assert.Equal("busy", await hunts.TakeAsync(hunter, 100, prey));

                // The prey is not in the world: no hunt.
                await hunts.TickAsync();
                Assert.False(hunts.HuntedNow(prey));

                await using var p = await ClientPipe.OpenAsync(PreyAccount, prey, Map, "Presa-" + tag);
                p.Session.State.CharacterLevel = 110;
                await hunts.TickAsync();
                Assert.True(hunts.HuntedNow(prey));

                await test(hunts, hunter, prey, h, p);
            }
            finally
            {
                hunts.CharacterDeleted(hunter);
                hunts.CharacterDeleted(prey);
                DatabaseManager.DeleteCharacter(hunter, HunterAccount);
                DatabaseManager.DeleteCharacter(prey, PreyAccount);
                Alignments.Forget(hunter);
                Alignments.Forget(prey);
            }
        }

        [Fact]
        public Task The_hunter_who_beats_his_prey_collects_the_bounty()
            => WithHunt(async (hunts, hunter, prey, h, p) =>
            {
                // While the hunt is open the prey cannot fold his wings.
                using (SessionContext.Push(p.Session))
                    await AlignmentHandler.PvpAsync(p.ToClient, ConnectionProtocol.Push("hnu", Array.Empty<byte>()));
                Assert.True(Alignments.Of(prey).PvpOn);

                int bounty = hunts.Bounty(prey, 110, 100);
                await AlignmentWar.AnnounceAsync(Aggression(hunter, 100, prey, 110), attackersWon: true);

                Assert.Equal(bounty, hunts.Of(hunter).Chapeuvepes);
                Assert.Equal(1, hunts.Of(hunter).Kills);
                Assert.Null(hunts.OrderOf(hunter));
                Assert.False(hunts.HuntedNow(prey));
                Assert.Equal(HeadHunts.KillHonour, Alignments.Of(hunter).Honour);
            }, "A");

        [Fact]
        public Task The_prey_who_beats_his_hunter_is_paid_and_the_hunter_waits()
            => WithHunt(async (hunts, hunter, prey, h, p) =>
            {
                await AlignmentWar.AnnounceAsync(Aggression(hunter, 100, prey, 110), attackersWon: false);

                Assert.Equal(HeadHunts.SurvivalPay, hunts.Of(prey).Chapeuvepes);
                Assert.Equal(1, hunts.Of(prey).Survivals);
                Assert.Null(hunts.OrderOf(hunter));
                Assert.Equal("locked", hunts.CannotHunt(hunter, 100));
            }, "B");

        /// <summary>
        /// While the hunt is open the prey fights nobody but his hunter, and goes into no place the
        /// hunter cannot follow him into: the Koliseo, a Merkasako, a dungeon, a house.
        /// </summary>
        [Fact]
        public Task The_prey_is_held_out_in_the_open_while_the_hunt_lasts()
            => WithHunt(async (hunts, hunter, prey, h, p) =>
            {
                const long Somebody = 8_980_000_099;
                using (SessionContext.Push(p.Session))
                {
                    foreach (var hold in Enum.GetValues<PlayerActivity>())
                        Assert.True(await PlayerPolicies.BlocksActivityAsync(p.ToClient, hold));
                    Assert.False(await PlayerPolicies.BlocksDuelAsync(p.ToClient, prey, hunter));
                    Assert.True(await PlayerPolicies.BlocksDuelAsync(p.ToClient, prey, Somebody));
                }
                using (SessionContext.Push(h.Session))
                {
                    // The hunter himself goes where he likes; and nobody else takes his prey.
                    Assert.False(await PlayerPolicies.BlocksActivityAsync(h.ToClient, PlayerActivity.Dungeon));
                    Assert.False(await PlayerPolicies.BlocksDuelAsync(h.ToClient, hunter, prey));
                }
                Assert.False(hunts.MayFight(Somebody, prey));
                Assert.True(hunts.MayFight(Somebody, hunter));

                // The hunt over, the prey is free again.
                await AlignmentWar.AnnounceAsync(Aggression(hunter, 100, prey, 110), attackersWon: false);
                using (SessionContext.Push(p.Session))
                    Assert.False(await PlayerPolicies.BlocksActivityAsync(p.ToClient, PlayerActivity.Koliseo));
            }, "D");

        [Fact]
        public Task The_hunt_closes_when_its_time_is_up_and_the_prey_rests()
            => WithHunt(async (hunts, hunter, prey, h, p) =>
            {
                var clock = hunts.Clock;
                try
                {
                    long start = clock();
                    hunts.Clock = () => start + (long)TimeSpan.FromMinutes(16).TotalMilliseconds;
                    await hunts.TickAsync();
                    Assert.False(hunts.HuntedNow(prey));

                    // Within the respite nothing opens; past it, the hunt is on again.
                    hunts.Clock = () => start + (long)TimeSpan.FromMinutes(30).TotalMilliseconds;
                    await hunts.TickAsync();
                    Assert.False(hunts.HuntedNow(prey));
                    hunts.Clock = () => start + (long)TimeSpan.FromMinutes(47).TotalMilliseconds;
                    await hunts.TickAsync();
                    Assert.True(hunts.HuntedNow(prey));
                }
                finally
                {
                    hunts.Clock = clock;
                }
            }, "C");

        [Fact]
        public async Task The_loaded_package_serves_routes_settles_hunts_and_restores_saved_orders()
        {
            var disabledSettings = ServerSettings.Read("""
                {"ServerMods":["cazacabezas"],"cazacabezas":{"Enabled":false}}
                """);
            using var disabled = ServerModLoader.LoadConfigured(Packages, disabledSettings, _ => { });
            Assert.Empty(disabled.ClientAssets);
            var catalog = new ClientModCatalog(Packages, []);
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

            var storage = OpenStorage();
            long hunter = Create(HunterAccount, "Cazador-D", 100);
            long prey = Create(PreyAccount, "Presa-D", 110);
            const string token = "head-hunt-test-token";
            try
            {
                storage.CharacterDeleted(hunter);
                storage.CharacterDeleted(prey);
                using var enabled = ServerModLoader.Load(Packages, ["cazacabezas"], _ => { });
                enabled.StartGameplay();
                Sworn(hunter, Alignments.Bonta);
                Sworn(prey, Alignments.Brakmar);
                ClientLaunchRegistry.RegisterToken(HunterAccount, token);
                string Body(string more = "") => "{\"token\":\"" + token + "\"" + more + "}";
                async Task<JsonElement> Send(string route, string body, int expected, ServerModControlRoutes routes,
                    HttpMethod? method = null)
                {
                    using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/headhunts/" + route)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    };
                    var pending = http.SendAsync(request);
                    var context = await listener.GetContextAsync().WaitAsync(http.Timeout);
                    await HaapiServer.HandleHaapiRequestAsync(context, catalog, routes).WaitAsync(http.Timeout);
                    using var answer = await pending;
                    Assert.Equal(expected, (int)answer.StatusCode);
                    Assert.Equal("application/json", answer.Content.Headers.ContentType?.MediaType);
                    using var document = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
                    return document.RootElement.Clone();
                }

                Task<JsonElement> Ask(string route, string more = "", int expected = 200, HttpMethod? method = null)
                    => Send(route, Body(more), expected, enabled.ControlRoutes, method);

                var noToken = await Send("status", "{}", 401, enabled.ControlRoutes);
                Assert.Equal("sesion", noToken.GetProperty("error").GetString());
                var badToken = await Send("status", """{"token":"nobody"}""", 401, enabled.ControlRoutes);
                Assert.Equal("sesion", badToken.GetProperty("error").GetString());
                Assert.Equal("world", (await Ask("status", expected: 409)).GetProperty("error").GetString());

                await using var h = await ClientPipe.OpenAsync(HunterAccount, hunter, Map, "Cazador-D");
                h.Session.State.CharacterLevel = 100;
                var status = await Ask("status", ",\"accountId\":" + PreyAccount, method: HttpMethod.Put);
                Assert.Equal("Cazador-D", status.GetProperty("character").GetString());
                Assert.True(status.GetProperty("on").GetBoolean());
                Assert.Equal(JsonValueKind.Null, status.GetProperty("order").ValueKind);

                var board = (await Ask("board")).GetProperty("prey").EnumerateArray().ToList();
                Assert.Contains(board, p => p.GetProperty("id").GetInt64() == prey);

                var taken = await Ask("take", ",\"target\":" + prey);
                Assert.Equal(prey, taken.GetProperty("taken").GetInt64());
                Assert.Equal("busy", (await Ask("take", ",\"target\":" + prey, 409)).GetProperty("error").GetString());
                var order = (await Ask("status")).GetProperty("order");
                Assert.Equal(prey, order.GetProperty("target").GetInt64());
                Assert.False(order.GetProperty("open").GetBoolean());

                await using var p = await ClientPipe.OpenAsync(PreyAccount, prey, Map, "Presa-D");
                p.Session.State.CharacterLevel = 110;
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
                    while (!PlayerPolicies.BlocksAutomaticEncounter(prey, AutomaticEncounter.TurnOffPvp))
                        await Task.Delay(50, timeout.Token);
                await AlignmentWar.AnnounceAsync(Aggression(hunter, 100, prey, 110), attackersWon: true);
                status = await Ask("status");
                Assert.Equal(1, status.GetProperty("kills").GetInt32());
                Assert.Equal(order.GetProperty("bounty").GetInt32(), status.GetProperty("chapeuvepes").GetInt32());
                Assert.Equal(JsonValueKind.Null, status.GetProperty("order").ValueKind);

                await Ask("take", ",\"target\":" + prey);
                Assert.True((await Ask("abandon")).GetProperty("abandoned").GetBoolean());
                Assert.Equal(JsonValueKind.Null, (await Ask("status")).GetProperty("order").ValueKind);

                foreach (string route in new[] { "status", "board", "take", "abandon" })
                {
                    var absent = await Send(route, Body(), 404, disabled.ControlRoutes);
                    Assert.Equal("ruta", absent.GetProperty("error").GetString());
                }

                enabled.Dispose();
                var stopped = await Send("status", Body(), 404, enabled.ControlRoutes);
                Assert.Equal("ruta", stopped.GetProperty("error").GetString());

                // Simulate a row left by the old built-in implementation when it shut down.
                using var database = new SqliteConnection(DatabaseManager.WorldConnectionString);
                database.Open();
                using var saved = database.CreateCommand();
                saved.CommandText = """
                    UPDATE HeadHunters SET Chapeuvepes = 19, Kills = 3, LockedUntilMs = 0 WHERE CharacterId = $hunter;
                    INSERT INTO HeadHuntOrders (Hunter, Target, TakenMs, OpenUntilMs, RestUntilMs)
                    VALUES ($hunter, $prey, $now, $now + 120000, $now + 60000);
                    """;
                saved.Parameters.AddWithValue("$hunter", hunter);
                saved.Parameters.AddWithValue("$prey", prey);
                saved.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                saved.ExecuteNonQuery();

                using (var restarted = ServerModLoader.Load(Packages, ["cazacabezas"], _ => { }))
                {
                    restarted.StartGameplay();
                    var restored = await Send("status", Body(), 200, restarted.ControlRoutes);
                    Assert.Equal(19, restored.GetProperty("chapeuvepes").GetInt32());
                    Assert.Equal(3, restored.GetProperty("kills").GetInt32());
                    var restoredOrder = restored.GetProperty("order");
                    Assert.Equal(prey, restoredOrder.GetProperty("target").GetInt64());
                    Assert.False(restoredOrder.GetProperty("open").GetBoolean());
                    Assert.InRange(restoredOrder.GetProperty("restSeconds").GetInt32(), 110, 120);

                    // The Brakmarian signs Morry Gann's first-tier renunciation.
                    p.Session.State.Kamas = 500;
                    using (SessionContext.Push(p.Session))
                        await AlignmentHandler.ReplyAsync(p.ToClient, AlignmentHandler.Morry, 78171);
                    Assert.Equal(Alignments.Neutral, Alignments.Of(prey).Side);
                    restored = await Send("status", Body(), 200, restarted.ControlRoutes);
                    Assert.Equal(JsonValueKind.Null, restored.GetProperty("order").ValueKind);

                    // Leave the package with a saved order that can open as soon as the fight clears.
                    Sworn(prey, Alignments.Brakmar);
                    p.Session.State.IsInFight = true;
                    await Send("take", Body(",\"target\":" + prey), 200, restarted.ControlRoutes);
                }

                // A due order with both characters present stays closed without the package.
                p.Session.State.IsInFight = false;
                using (SessionContext.Push(p.Session))
                    await AlignmentHandler.PvpAsync(p.ToClient, ConnectionProtocol.Push("hnu", Array.Empty<byte>()));
                Assert.False(Alignments.Of(prey).PvpOn);
                Assert.Empty(ServerModHooks.All<IPlayerActivityPolicy>());
                await Task.Delay(TimeSpan.FromSeconds(6));
                saved.CommandText = "SELECT OpenUntilMs FROM HeadHuntOrders WHERE Hunter = $hunter;";
                Assert.Equal(0L, saved.ExecuteScalar());
            }
            finally
            {
                var cleanup = OpenStorage();
                cleanup.CharacterDeleted(hunter);
                cleanup.CharacterDeleted(prey);
                DatabaseManager.DeleteCharacter(hunter, HunterAccount);
                DatabaseManager.DeleteCharacter(prey, PreyAccount);
                Alignments.Forget(hunter);
                Alignments.Forget(prey);
            }
        }

        [Fact]
        public void A_chapeuvepe_trade_needs_the_chapeuvepes()
        {
            const long someone = 8_980_000_099;
            var hunts = OpenStorage();
            try
            {
                Assert.False(hunts.Pay(someone, -2));
                Assert.True(hunts.Pay(someone, 5));
                Assert.True(hunts.Pay(someone, -2));
                Assert.Equal(3, hunts.Of(someone).Chapeuvepes);
            }
            finally
            {
                hunts.CharacterDeleted(someone);
            }
        }
    }
}

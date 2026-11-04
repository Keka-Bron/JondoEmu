using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Fights;
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
            var fight = new FightInstance(980_001, Map);
            fight.Azul.Add(new Fighter { Id = blue, Level = blueLevel, MaxHP = 100, CurrentHP = 100 });
            fight.Rojo.Add(new Fighter { Id = red, Level = redLevel, MaxHP = 100, CurrentHP = 0 });
            return fight;
        }

        private static async Task WithHunt(Func<long, long, ClientPipe, ClientPipe, Task> test, string tag)
        {
            ServerSettings.UseForTests(new ServerSettings { HeadHunters = true, HeadHuntWindowMinutes = 15, HeadHuntRestMinutes = 30 });
            HeadHunts.Reset();
            long hunter = Create(HunterAccount, "Cazador-" + tag, 100);
            long prey = Create(PreyAccount, "Presa-" + tag, 110);
            try
            {
                // Ids come back after a character is deleted: nothing of an earlier one's hunts stays.
                HeadHunts.Erase(hunter);
                HeadHunts.Erase(prey);
                Sworn(hunter, Alignments.Bonta);
                Sworn(prey, Alignments.Brakmar);
                await using var h = await ClientPipe.OpenAsync(HunterAccount, hunter, Map, "Cazador-" + tag);
                h.Session.State.CharacterLevel = 100;

                var board = HeadHunts.CandidatesFor(hunter, 100);
                Assert.Contains(board, c => c.Id == prey && !c.Online);
                Assert.Equal("ok", await HeadHunts.TakeAsync(hunter, 100, prey));
                Assert.Equal("busy", await HeadHunts.TakeAsync(hunter, 100, prey));

                // The prey is not in the world: no hunt.
                await HeadHunts.TickAsync();
                Assert.False(HeadHunts.HuntedNow(prey));

                await using var p = await ClientPipe.OpenAsync(PreyAccount, prey, Map, "Presa-" + tag);
                p.Session.State.CharacterLevel = 110;
                await HeadHunts.TickAsync();
                Assert.True(HeadHunts.HuntedNow(prey));

                await test(hunter, prey, h, p);
            }
            finally
            {
                HeadHunts.Erase(hunter);
                HeadHunts.Erase(prey);
                HeadHunts.Reset();
                ServerSettings.UseForTests(new ServerSettings());
                DatabaseManager.DeleteCharacter(hunter, HunterAccount);
                DatabaseManager.DeleteCharacter(prey, PreyAccount);
                Alignments.Forget(hunter);
                Alignments.Forget(prey);
            }
        }

        [Fact]
        public Task The_hunter_who_beats_his_prey_collects_the_bounty()
            => WithHunt(async (hunter, prey, h, p) =>
            {
                // While the hunt is open the prey cannot fold his wings.
                using (SessionContext.Push(p.Session))
                    await AlignmentHandler.PvpAsync(p.ToClient, ConnectionProtocol.Push("hnu"));
                Assert.True(Alignments.Of(prey).PvpOn);

                int bounty = HeadHunts.Bounty(prey, 110, 100);
                await HeadHunts.AggressionEndedAsync(Aggression(hunter, 100, prey, 110), attackersWon: true);

                Assert.Equal(bounty, HeadHunts.Of(hunter).Chapeuvepes);
                Assert.Equal(1, HeadHunts.Of(hunter).Kills);
                Assert.Null(HeadHunts.OrderOf(hunter));
                Assert.False(HeadHunts.HuntedNow(prey));
                Assert.Equal(HeadHunts.KillHonour, Alignments.Of(hunter).Honour);
            }, "A");

        [Fact]
        public Task The_prey_who_beats_his_hunter_is_paid_and_the_hunter_waits()
            => WithHunt(async (hunter, prey, h, p) =>
            {
                await HeadHunts.AggressionEndedAsync(Aggression(hunter, 100, prey, 110), attackersWon: false);

                Assert.Equal(HeadHunts.SurvivalPay, HeadHunts.Of(prey).Chapeuvepes);
                Assert.Equal(1, HeadHunts.Of(prey).Survivals);
                Assert.Null(HeadHunts.OrderOf(hunter));
                Assert.Equal("locked", HeadHunts.CannotHunt(hunter, 100));
            }, "B");

        /// <summary>
        /// While the hunt is open the prey fights nobody but his hunter, and goes into no place the
        /// hunter cannot follow him into: the Koliseo, a Merkasako, a dungeon, a house.
        /// </summary>
        [Fact]
        public Task The_prey_is_held_out_in_the_open_while_the_hunt_lasts()
            => WithHunt(async (hunter, prey, h, p) =>
            {
                const long Somebody = 8_980_000_099;
                using (SessionContext.Push(p.Session))
                {
                    foreach (var hold in Enum.GetValues<HeadHunts.Hold>())
                        Assert.True(await HeadHunts.HoldsAsync(p.ToClient, hold));
                    Assert.False(await HeadHunts.RefusesFightAsync(p.ToClient, prey, hunter));
                    Assert.True(await HeadHunts.RefusesFightAsync(p.ToClient, prey, Somebody));
                }
                using (SessionContext.Push(h.Session))
                {
                    // The hunter himself goes where he likes; and nobody else takes his prey.
                    Assert.False(await HeadHunts.HoldsAsync(h.ToClient, HeadHunts.Hold.Dungeon));
                    Assert.False(await HeadHunts.RefusesFightAsync(h.ToClient, hunter, prey));
                }
                Assert.False(HeadHunts.MayFight(Somebody, prey));
                Assert.True(HeadHunts.MayFight(Somebody, hunter));

                // The hunt over, the prey is free again.
                await HeadHunts.AggressionEndedAsync(Aggression(hunter, 100, prey, 110), attackersWon: false);
                using (SessionContext.Push(p.Session))
                    Assert.False(await HeadHunts.HoldsAsync(p.ToClient, HeadHunts.Hold.Koliseo));
            }, "D");

        [Fact]
        public Task The_hunt_closes_when_its_time_is_up_and_the_prey_rests()
            => WithHunt(async (hunter, prey, h, p) =>
            {
                var clock = HeadHunts.Clock;
                try
                {
                    long start = clock();
                    HeadHunts.Clock = () => start + (long)TimeSpan.FromMinutes(16).TotalMilliseconds;
                    await HeadHunts.TickAsync();
                    Assert.False(HeadHunts.HuntedNow(prey));

                    // Within the respite nothing opens; past it, the hunt is on again.
                    HeadHunts.Clock = () => start + (long)TimeSpan.FromMinutes(30).TotalMilliseconds;
                    await HeadHunts.TickAsync();
                    Assert.False(HeadHunts.HuntedNow(prey));
                    HeadHunts.Clock = () => start + (long)TimeSpan.FromMinutes(47).TotalMilliseconds;
                    await HeadHunts.TickAsync();
                    Assert.True(HeadHunts.HuntedNow(prey));
                }
                finally
                {
                    HeadHunts.Clock = clock;
                }
            }, "C");

        [Fact]
        public async Task The_mod_asks_with_the_players_own_session()
        {
            ServerSettings.UseForTests(new ServerSettings { HeadHunters = true });
            HeadHunts.Reset();
            long hunter = Create(HunterAccount, "Cazador-D", 100);
            long prey = Create(PreyAccount, "Presa-D", 110);
            const string token = "head-hunt-test-token";
            try
            {
                HeadHunts.Erase(hunter);
                HeadHunts.Erase(prey);
                Sworn(hunter, Alignments.Bonta);
                Sworn(prey, Alignments.Brakmar);
                ClientLaunchRegistry.RegisterToken(HunterAccount, token);
                string Body(string more = "") => "{\"token\":\"" + token + "\"" + more + "}";
                JsonElement Ask(string route, string more = "", int expected = 200)
                {
                    var answer = ControlApi.Responder("/api/headhunts/" + route, "POST", Body(more));
                    Assert.NotNull(answer);
                    Assert.Equal(expected, answer!.Value.Codigo);
                    return JsonDocument.Parse(answer.Value.Json).RootElement.Clone();
                }

                // Without a session, nothing; without a character in the world, nothing either.
                Assert.Equal(401, ControlApi.Responder("/api/headhunts/status", "POST", "{\"token\":\"nobody\"}")!.Value.Codigo);
                Assert.Equal("world", Ask("status", expected: 409).GetProperty("error").GetString());

                await using var h = await ClientPipe.OpenAsync(HunterAccount, hunter, Map, "Cazador-D");
                h.Session.State.CharacterLevel = 100;
                var status = Ask("status");
                Assert.True(status.GetProperty("on").GetBoolean());
                Assert.Equal(JsonValueKind.Null, status.GetProperty("order").ValueKind);

                var board = Ask("board").GetProperty("prey").EnumerateArray().ToList();
                Assert.Contains(board, p => p.GetProperty("id").GetInt64() == prey);

                Ask("take", ",\"target\":" + prey);
                Assert.Equal("busy", Ask("take", ",\"target\":" + prey, 409).GetProperty("error").GetString());
                var order = Ask("status").GetProperty("order");
                Assert.Equal(prey, order.GetProperty("target").GetInt64());
                Assert.False(order.GetProperty("open").GetBoolean());

                Ask("abandon");
                Assert.Null(HeadHunts.OrderOf(hunter));
            }
            finally
            {
                HeadHunts.Erase(hunter);
                HeadHunts.Erase(prey);
                HeadHunts.Reset();
                ServerSettings.UseForTests(new ServerSettings());
                DatabaseManager.DeleteCharacter(hunter, HunterAccount);
                DatabaseManager.DeleteCharacter(prey, PreyAccount);
                Alignments.Forget(hunter);
                Alignments.Forget(prey);
            }
        }

        [Fact]
        public async Task A_chapeuvepe_trade_needs_the_chapeuvepes()
        {
            const long someone = 8_980_000_099;
            HeadHunts.Reset();
            try
            {
                Assert.False(HeadHunts.Pay(someone, -2));
                Assert.True(HeadHunts.Pay(someone, 5));
                Assert.True(HeadHunts.Pay(someone, -2));
                Assert.Equal(3, HeadHunts.Of(someone).Chapeuvepes);
                await Task.CompletedTask;
            }
            finally
            {
                HeadHunts.Pay(someone, -HeadHunts.Of(someone).Chapeuvepes);
                HeadHunts.Reset();
            }
        }
    }
}

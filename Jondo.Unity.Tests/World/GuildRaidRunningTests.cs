using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Content;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The running raid and its tracking panel (RaidTrackingUI): what the panel is sent, its goals
    /// and score, and its buttons -- finish, restart, leave, captain -- with the result codes the
    /// client's raid frame translates. Read from the client; no capture has a raid.
    /// </summary>
    [Collection("guild raids")]
    public class GuildRaidRunningTests : IDisposable
    {
        private readonly string _file;
        private readonly List<GameSession> _sessions = new();

        public GuildRaidRunningTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-raidrun-{Guid.NewGuid():N}.db");
            GuildStore.ConnectionStringOverride = $"Data Source={_file}";
            GuildRaidBoard.Forget();
            GuildRaidManager.Forget();
        }

        public void Dispose()
        {
            foreach (var session in _sessions) SessionRegistry.Unregister(session);
            GuildRaidManager.Forget();
            GuildRaidBoard.Forget();
            GuildStore.ConnectionStringOverride = null;
            SqliteConnection.ClearAllPools();
            try { File.Delete(_file); } catch (IOException) { }
        }

        /// <summary>This character connected, with no socket, until the test ends.</summary>
        private GameSession Online(long id)
        {
            var session = GameSession.SinSocket();
            session.State.CharacterId = id;
            Assert.True(SessionRegistry.Register(session));
            _sessions.Add(session);
            return session;
        }

        /// <summary>A raid of the guild of 7001, 7002 and 7003, all three in it, launched.</summary>
        private static async Task<(GuildRaidBoard.Raid Board, RaidInstance Raid)> Launched(int raidId)
        {
            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            GuildStore.SpendGuildKamas(guild.Id, -1000);
            GuildStore.Join(7002, guild.Id);
            GuildStore.Join(7003, guild.Id);
            var board = GuildRaidBoard.Purchase(7001, raidId).Raid;
            GuildRaidBoard.Join(7002, board.Uuid);
            GuildRaidBoard.Join(7003, board.Uuid);
            return (board, await GuildRaidManager.LaunchAsync(board));
        }

        private static List<ProtoField> Fields(byte[] message) => ProtoMessage.Parse(message).Fields.ToList();

        private static List<ProtoField> Inner(List<ProtoField> fields, int number)
            => Fields(fields.First(f => f.FieldNumber == number && f.WireType == 2).BytesValue);

        private static string Text(List<ProtoField> fields, int number)
            => Encoding.UTF8.GetString(fields.First(f => f.FieldNumber == number).BytesValue);

        /// <summary>
        /// The running raid (hvt → ibj): f1 its participants by key, each with his group, card and
        /// state; f2 the raid; f3 its end, which the panel counts down to; f4 the captain's key, the
        /// same string as his card's f6; f5 its state.
        /// </summary>
        [Fact]
        public async Task The_panel_gets_the_running_raid()
        {
            var (board, raid) = await Launched(Raids.Gigalodon);
            Online(7002).State.IsInFight = true;
            Online(7003);
            raid.Leave(7003);

            var frame = Fields(GuildRaidProtocol.BuildRunningRaid(board, raid, GuildRaidManager.VariablesOf(raid)));
            Assert.Equal(board.Uuid, Text(frame, 2));
            var ibj = Inner(frame, 1);
            Assert.Equal(Raids.Gigalodon, ibj.First(f => f.FieldNumber == 2).VarIntValue);
            Assert.Equal(raid.EndsUtc.ToUnixTimeSeconds(), DateTimeOffset.Parse(Text(ibj, 3)).ToUnixTimeSeconds());
            Assert.Equal("7001", Text(ibj, 4));

            var participants = ibj.Where(f => f.FieldNumber == 1).Select(f => Fields(f.BytesValue))
                                  .ToDictionary(e => Text(e, 1), e => Inner(e, 2));
            Assert.Equal(new[] { "7001", "7002", "7003" }, participants.Keys);
            int StateOf(string key) => (int)participants[key].First(f => f.FieldNumber == 5).VarIntValue;
            Assert.Equal((int)GuildRaidProtocol.RunningState.Offline, StateOf("7001"));
            Assert.Equal((int)GuildRaidProtocol.RunningState.Fighting, StateOf("7002"));
            Assert.Equal((int)GuildRaidProtocol.RunningState.Left, StateOf("7003"));
            Assert.Equal(2, participants["7002"].First(f => f.FieldNumber == 1).VarIntValue);   // the group
            Assert.Equal("7002", Text(Inner(participants["7002"], 4), 6));                         // the card's key

            // The Gigalodón's one variable, the salt, is there even at nothing.
            var variables = Inner(ibj, 5).Where(f => f.FieldNumber == 4).Select(f => Fields(f.BytesValue)).ToList();
            Assert.Single(variables);
            Assert.Equal(Luminomachine.SaltVariable, variables[0].First(f => f.FieldNumber == 1).VarIntValue);
        }

        /// <summary>
        /// A goal met adds its score once, and the state (iau) shows it ticked (case 1); one under
        /// way shows how far it has gone (case 2), which the panel writes as "n/value".
        /// </summary>
        [Fact]
        public async Task Goals_add_their_score_once_and_show_how_far_they_went()
        {
            var (_, raid) = await Launched(Raids.EternalGardens);

            await GuildRaidManager.AdvanceGoalAsync(raid, 3, 4);           // Belladona's enigma: 4 of 6
            Assert.Equal(0, raid.Score);
            await GuildRaidManager.AdvanceGoalAsync(raid, 1, 1);           // the monochrome work: met
            await GuildRaidManager.AdvanceGoalAsync(raid, 1, 1);
            Assert.Equal(2000, raid.Score);
            Assert.Equal(new[] { 1 }, GuildRaidManager.MetGoals(raid));

            var state = Fields(GuildRaidProtocol.RunningStateBlock(raid, GuildRaidManager.VariablesOf(raid)).Build());
            Assert.Equal(2000, state.First(f => f.FieldNumber == 1).VarIntValue);
            var goals = state.Where(f => f.FieldNumber == 2).Select(f => Fields(f.BytesValue))
                             .ToDictionary(e => e.First(f => f.FieldNumber == 1).VarIntValue, e => Inner(e, 2));
            Assert.Contains(goals[1], f => f.FieldNumber == 1);
            Assert.Equal(4, Inner(goals[3], 2).First(f => f.FieldNumber == 1).VarIntValue);
            Assert.DoesNotContain(state, f => f.FieldNumber == 4);         // the Santuario has no variables
        }

        /// <summary>
        /// The goals a fight moves, from the monsters their names name: the Santuario's four
        /// guardians, its Princesa and Reina and Belladona; the Gigalodón's Mureine ("Morreina"). The
        /// Exécrabe ("Cangrancio") is named by goal 16, which its statues meet, and the Gigalodón by
        /// fifteen damage goals: beating either moves none.
        /// </summary>
        [Fact]
        public void A_goal_s_monsters_come_from_its_name()
        {
            var gardens = GuildRaidManager.KillGoals(Raids.EternalGardens);
            Assert.Equal(new[] { 5 }, gardens[8316]);
            Assert.Equal(new[] { 10 }, gardens[8279]);
            Assert.Equal(new[] { 11 }, gardens[8278]);
            Assert.Equal(new[] { 3 }, gardens[8246]);
            var abyss = GuildRaidManager.KillGoals(Raids.Gigalodon);
            Assert.Equal(new[] { 14 }, abyss[8333]);
            Assert.False(abyss.ContainsKey(8332));          // the Cangrancio's goal is its statues' (GuildRaidExecrabe)
            Assert.False(abyss.ContainsKey(8314));
        }

        /// <summary>
        /// A fight won moves the goals of what it beat: the Princesa Maldita's, and in the castle,
        /// the Santuario's last zone, the corridor's count of monsters.
        /// </summary>
        [Fact]
        public async Task Fights_won_move_the_goals()
        {
            var (_, gardens) = await Launched(Raids.EternalGardens);
            await GuildRaidManager.AdvanceByFightAsync(gardens, 5, new Dictionary<int, int> { [8285] = 3, [8279] = 1 });
            Assert.Equal(4, gardens.GoalAt(GuildRaidManager.CorridorGoal));
            Assert.Equal(1, gardens.GoalAt(10));
            Assert.Equal(10_000, gardens.Score);

            await GuildRaidManager.AdvanceByFightAsync(gardens, 2, new Dictionary<int, int> { [8285] = 5 });
            Assert.Equal(4, gardens.GoalAt(GuildRaidManager.CorridorGoal));      // not the castle
            await GuildRaidManager.FinishAsync(gardens, RaidInstance.Ending.TimeUp);
            GuildRaidBoard.Forget();

            var board = GuildRaidBoard.Purchase(7001, Raids.Gigalodon).Raid;
            var abyss = await GuildRaidManager.LaunchAsync(board);
            await GuildRaidManager.AdvanceByFightAsync(abyss, 2, new Dictionary<int, int> { [8333] = 1, [8365] = 2 });
            Assert.Equal(new[] { 14 }, GuildRaidManager.MetGoals(abyss));
        }

        /// <summary>
        /// The Gigalodón's light fades a band every two minutes from where it was set (the guides);
        /// its first floor starts fully lit, and a floor opened by its goal starts at one band.
        /// </summary>
        [Fact]
        public async Task The_light_fades_and_floors_open_lit_at_one()
        {
            var (_, raid) = await Launched(Raids.Gigalodon);
            var t = raid.StartedUtc;
            Assert.Equal(4, raid.LightAt(1, t + TimeSpan.FromSeconds(119)));
            Assert.Equal(3, raid.LightAt(1, t + TimeSpan.FromMinutes(2)));
            Assert.Equal(0, raid.LightAt(1, t + TimeSpan.FromMinutes(9)));

            Assert.False(raid.IsOpen(2));
            await GuildRaidManager.AdvanceGoalAsync(raid, GuildRaidManager.ClearFirstFloorGoal, 1);
            Assert.True(raid.IsOpen(2));
            Assert.Equal(1, raid.LightAt(2, DateTimeOffset.UtcNow));
            Assert.False(raid.IsOpen(3));
            Assert.Equal(GuildRaidManager.ClearFirstFloorGoal, GuildRaidManager.OpenerOf(Raids.Gigalodon, 2).Id);
        }

        /// <summary>A floor its goal has not opened keeps the raid out, on every road; an open one does not.</summary>
        [Fact]
        public async Task A_closed_floor_keeps_the_raid_out()
        {
            var (_, raid) = await Launched(Raids.Gigalodon);
            raid.Add(7001);
            var second = DatabaseManager.MapsOfSubArea(Raids.Of(Raids.Gigalodon).Floors[1]);
            var first = DatabaseManager.MapsOfSubArea(Raids.Of(Raids.Gigalodon).Floors[0]);
            if (second.Count == 0 || first.Count == 0) return;      // no world base on this machine

            Assert.True(await GuildRaidManager.KeepsOutAsync(null, 7001, second.First()));
            Assert.False(await GuildRaidManager.KeepsOutAsync(null, 7001, first.First()));
            Assert.False(await GuildRaidManager.KeepsOutAsync(null, 7002, second.First()));     // not inside
            raid.Open(2);
            Assert.False(await GuildRaidManager.KeepsOutAsync(null, 7001, second.First()));
        }

        /// <summary>
        /// A Gigalodón fight's salt is the raid's: every winner sees the same amount, the most any
        /// rolled, it goes into the pool once, and it does not go into the bags.
        /// </summary>
        [Fact]
        public async Task The_salt_is_the_raid_s_pool()
        {
            var (_, raid) = await Launched(Raids.Gigalodon);
            raid.Add(7001);
            raid.Add(7002);
            var loot = new Dictionary<long, Dictionary<int, int>>
            {
                [7001] = new() { [Luminomachine.SaltItem] = 2, [32465] = 1 },
                [7002] = new() { [Luminomachine.SaltItem] = 3 },
            };
            Assert.Equal(3, GuildRaidManager.ShareFightSalt(1, loot));
            Assert.Equal(3, loot[7001][Luminomachine.SaltItem]);
            Assert.Equal(3, GuildRaidManager.SaltOf(7002));
            Assert.Equal(new[] { 32465 }, GuildRaidManager.ForTheBag(7001, loot[7001]).Keys);

            Assert.False(GuildRaidManager.SpendSalt(7001, 6));
            Assert.True(GuildRaidManager.SpendSalt(7001, 3));
            Assert.Equal(0, GuildRaidManager.SaltOf(7001));
            Assert.Equal(0, GuildRaidManager.VariablesOf(raid)[Luminomachine.SaltVariable]);
        }

        /// <summary>
        /// The Santuario has 20 health (the guides): a fight lost costs one per character, the
        /// panel shows it in f3, and at none the raid is over. Beating its two final bosses ends it
        /// too. The Gigalodón has no health and its panel no hearts.
        /// </summary>
        [Fact]
        public async Task The_Santuario_runs_on_its_health_and_its_bosses()
        {
            var (_, raid) = await Launched(Raids.EternalGardens);
            Assert.Equal(20, raid.Health);
            Assert.Equal(new[] { 10, 11 }, GuildRaidManager.FinalGoals(Raids.EternalGardens));
            await GuildRaidManager.LoseHealthAsync(raid, 3);
            Assert.Equal(17, Fields(GuildRaidProtocol.RunningStateBlock(raid, GuildRaidManager.VariablesOf(raid)).Build())
                .Single(f => f.FieldNumber == 3).VarIntValue);
            await GuildRaidManager.LoseHealthAsync(raid, 17);
            Assert.Equal(RaidInstance.Ending.OutOfHealth, raid.Over);

            var again = await GuildRaidManager.LaunchAsync(GuildRaidBoard.Purchase(7001, Raids.EternalGardens).Raid);
            await GuildRaidManager.AdvanceGoalAsync(again, 10, 1);
            Assert.True(again.Running);
            await GuildRaidManager.AdvanceGoalAsync(again, 11, 1);
            Assert.Equal(RaidInstance.Ending.Beaten, again.Over);

            GuildStore.SpendGuildKamas(GuildStore.GuildOf(7001).Id, -1000);
            var abyss = await GuildRaidManager.LaunchAsync(GuildRaidBoard.Purchase(7001, Raids.Gigalodon).Raid);
            Assert.Null(abyss.Health);
            Assert.DoesNotContain(Fields(GuildRaidProtocol.RunningStateBlock(abyss, GuildRaidManager.VariablesOf(abyss)).Build()),
                                  f => f.FieldNumber == 3);
        }

        /// <summary>
        /// The Gigalodón's escape: the chest's fight runs three rounds and ends at the fourth, costs
        /// nothing to lose, and its damage scores the highest goal it reaches -- 55,006 is 3,000.
        /// </summary>
        [Fact]
        public async Task The_Gigalodon_scores_its_damage()
        {
            var rules = Jondo.Unity.World.Fights.FightRules.GigalodonEscape;
            Assert.Equal(4, rules.EndsAtRound);
            Assert.False(rules.DefeatCosts);
            Assert.Equal(1800, rules.RelojDeColocacion);
            Assert.Equal(8314, GuildRaidManager.GigalodonMonster);

            var (_, raid) = await Launched(Raids.Gigalodon);
            await GuildRaidManager.DamageDealtAsync(raid, 55_006);
            Assert.Equal(3_000, raid.Score);
            Assert.Equal(new[] { 19 }, GuildRaidManager.MetGoals(raid));
            await GuildRaidManager.DamageDealtAsync(raid, 9_999);
            Assert.Equal(3_000, raid.Score);
        }

        /// <summary>
        /// Finishing is the captain's, and only where the raid's data allows it: the Santuario; the
        /// Gigalodón ends by its clock or its chest. Over, the board keeps how long it ran, its
        /// score and its goals, which the tab's final card reads from f4, f6 and f5.
        /// </summary>
        [Fact]
        public async Task Only_the_captain_finishes_and_only_the_Santuario()
        {
            var (abyss, abyssRaid) = await Launched(Raids.Gigalodon);
            Assert.Equal(GuildRaidProtocol.RunningResult.ImpossibleForThisRaid, await GuildRaidManager.CloseAsync(7001));
            await GuildRaidManager.FinishAsync(abyssRaid, RaidInstance.Ending.TimeUp);
            GuildRaidBoard.Forget();

            var guild = GuildStore.GuildOf(7001);
            var board = GuildRaidBoard.Purchase(7001, Raids.EternalGardens).Raid;
            GuildRaidBoard.Join(7002, board.Uuid);
            var raid = await GuildRaidManager.LaunchAsync(board);
            await GuildRaidManager.AdvanceGoalAsync(raid, 2, 2);

            Assert.Equal(GuildRaidProtocol.RunningResult.NotCaptain, await GuildRaidManager.CloseAsync(7002));
            Assert.Equal(GuildRaidProtocol.RunningResult.NotInRaid, await GuildRaidManager.CloseAsync(9999));
            Assert.Equal(GuildRaidProtocol.RunningResult.Done, await GuildRaidManager.CloseAsync(7001));

            Assert.Equal(GuildRaidBoard.State.Finished, board.State);
            Assert.Equal(2000, board.Score);
            Assert.Equal(new[] { 2 }, board.ValidatedGoals);
            var over = Inner(Fields(GuildRaidProtocol.BuildRaidUpdated(board)), 4);
            Assert.Equal(2000, over.First(f => f.FieldNumber == 6).VarIntValue);
            Assert.Equal(2, over.First(f => f.FieldNumber == 5).VarIntValue);
            Assert.Equal(2000, GuildStore.BestRaidScore(guild.Id, Raids.EternalGardens, DateTimeOffset.UtcNow));
        }

        /// <summary>
        /// The final score (hyg): f1 the seconds it ran, f2 the score, f5 the goals met, f6 whether
        /// it beat the guild's best of the week.
        /// </summary>
        [Fact]
        public void The_final_score_frame()
        {
            var frame = Fields(GuildRaidProtocol.BuildRunningFinished(3600, 12345, new[] { 12, 13 }, newHighScore: true));
            Assert.Equal(3600, frame.Single(f => f.FieldNumber == 1).VarIntValue);
            Assert.Equal(12345, frame.Single(f => f.FieldNumber == 2).VarIntValue);
            Assert.Equal(new long[] { 12, 13 }, frame.Where(f => f.FieldNumber == 5).Select(f => f.VarIntValue));
            Assert.Equal(1, frame.Single(f => f.FieldNumber == 6).VarIntValue);
            Assert.DoesNotContain(Fields(GuildRaidProtocol.BuildRunningFinished(60, 0, null, false)), f => f.FieldNumber == 6);
        }

        /// <summary>Taking the Gigalodón's chest out meets its goal of escaping with it.</summary>
        [Fact]
        public async Task Escaping_with_the_chest_is_a_goal_met()
        {
            var (board, raid) = await Launched(Raids.Gigalodon);
            await GuildRaidManager.FinishAsync(raid, RaidInstance.Ending.Beaten);
            Assert.Equal(new[] { RaidChest.EscapeGoal }, board.ValidatedGoals);
        }

        /// <summary>
        /// Leaving is for good: the panel shows him gone, he does not come back in, and leaving
        /// twice is "not in a raid". Someone fighting cannot leave.
        /// </summary>
        [Fact]
        public async Task Leaving_the_running_raid_is_for_good()
        {
            var (_, raid) = await Launched(Raids.Gigalodon);
            var fighting = Online(7003);
            fighting.State.IsInFight = true;
            Assert.Equal(GuildRaidProtocol.LeaveOccupied, await GuildRaidManager.LeaveAsync(7003));

            Assert.Equal(0, await GuildRaidManager.LeaveAsync(7002));
            Assert.True(raid.HasLeft(7002));
            Assert.Equal(GuildRaidProtocol.LeaveNotInRaid, await GuildRaidManager.LeaveAsync(7002));
            Online(7002);
            Assert.False(await GuildRaidManager.EnterAsync(7002));
            Assert.Equal(GuildRaidProtocol.LeaveNotInRaid, await GuildRaidManager.LeaveAsync(9999));
        }

        /// <summary>
        /// The captain hands the running raid on, to someone connected who has not left; the board
        /// follows, so the tab shows the new captain too.
        /// </summary>
        [Fact]
        public async Task The_captain_hands_the_running_raid_on()
        {
            var (board, raid) = await Launched(Raids.Gigalodon);

            Assert.Equal(GuildRaidProtocol.RunningCaptainResult.NotCaptain, await GuildRaidManager.HandCaptaincyAsync(7002, 7003));
            Assert.Equal(GuildRaidProtocol.RunningCaptainResult.InvalidTarget, await GuildRaidManager.HandCaptaincyAsync(7001, 9999));
            Assert.Equal(GuildRaidProtocol.RunningCaptainResult.TargetDisconnected, await GuildRaidManager.HandCaptaincyAsync(7001, 7002));
            Online(7002);
            Assert.Equal(GuildRaidProtocol.RunningCaptainResult.Done, await GuildRaidManager.HandCaptaincyAsync(7001, 7002));
            Assert.Equal(7002, raid.CaptainId);
            Assert.Equal(7002, board.Captain.CharacterId);

            Online(7003);
            raid.Leave(7003);
            Assert.Equal(GuildRaidProtocol.RunningCaptainResult.TargetHasLeft, await GuildRaidManager.HandCaptaincyAsync(7002, 7003));
            Assert.Equal("7002", Text(Fields(GuildRaidProtocol.BuildRunningCaptain(board.Uuid, raid.CaptainId)), 1));
        }

        /// <summary>
        /// Restarting is the captain's where the data allows it -- the Santuario --: the score and
        /// the goals back at nothing, and the whole time again.
        /// </summary>
        [Fact]
        public async Task The_Santuario_starts_over()
        {
            var (_, abyss) = await Launched(Raids.Gigalodon);
            Assert.Equal(GuildRaidProtocol.RunningResult.ImpossibleForThisRaid, await GuildRaidManager.RestartAsync(7001));
            await GuildRaidManager.FinishAsync(abyss, RaidInstance.Ending.TimeUp);

            var board = GuildRaidBoard.Purchase(7001, Raids.EternalGardens).Raid;
            var raid = await GuildRaidManager.LaunchAsync(board);
            await GuildRaidManager.AdvanceGoalAsync(raid, 1, 1);
            Assert.Equal(2000, raid.Score);

            Assert.Equal(GuildRaidProtocol.RunningResult.Done, await GuildRaidManager.RestartAsync(7001));
            Assert.Equal(0, raid.Score);
            Assert.Empty(raid.Goals);
            Assert.True(raid.Running);
            Assert.InRange(raid.EndsUtc - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(119), TimeSpan.FromMinutes(121));
        }
    }
}

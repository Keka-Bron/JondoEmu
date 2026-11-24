using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Server;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Spectator admission and the map fight window's requests.
    /// </summary>
    public class SpectatorTests
    {
        private const long Map = 990_100_087;
        private const int FightId = 7_100_087;
        private static readonly ConcurrentDictionary<long, FightInstance> Active =
            (ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
                .GetField("_activeFights", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        private static FightInstance Running()
        {
            var fight = new FightInstance(FightId, Map, Map + 1) { RoleplayMapId = Map, DefenderLeaderId = -20002 };
            fight.AddPlayer(new Fighter { Id = 9_100_001, TeamId = 0, CellId = 200, MaxHP = 500, CurrentHP = 500,
                Level = 50, Name = "Uno", Breed = 8, IsBot = true, Sex = 1 });
            fight.AddMonster(new Fighter { Id = -1, TeamId = 1, CellId = 300, MaxHP = 100, CurrentHP = 100, IsMonster = true,
                                           MonsterId = 494, Level = 10 });
            return fight;
        }

        private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));

        [Fact]
        public void Only_a_running_fight_open_to_spectators_is_watched()
        {
            var fight = Running();
            var who = new SessionState { CharacterId = 9_100_002, MapId = Map };

            Assert.Equal("no such fight", FightHandler.WhyNotWatch(null, who, fromTheMap: true));
            Assert.Equal("the fight has not started", FightHandler.WhyNotWatch(fight, who, fromTheMap: true));

            fight.StartFight();
            Assert.Null(FightHandler.WhyNotWatch(fight, who, fromTheMap: true));

            var elsewhere = new SessionState { CharacterId = 9_100_003, MapId = Map + 1 };
            Assert.Equal("not on the map of the fight", FightHandler.WhyNotWatch(fight, elsewhere, fromTheMap: true));
            Assert.Null(FightHandler.WhyNotWatch(fight, elsewhere, fromTheMap: false));   // a friend's fight, from afar

            var watching = new SessionState { CharacterId = 9_100_004, MapId = Map, SpectatingFightId = 12 };
            Assert.Equal("already in a fight", FightHandler.WhyNotWatch(fight, watching, fromTheMap: true));

            fight.SetOption(FightInstance.Azules, FightInstance.OptionSecret, true);
            Assert.Equal("the fight takes no spectators", FightHandler.WhyNotWatch(fight, who, fromTheMap: true));
        }

        /// <summary>kab { f3 kaa }: the kaa of a fight in progress, without the ready flags.</summary>
        [Fact]
        public void The_spectator_join_carries_a_running_fight_s_kaa()
        {
            Assert.Equal("1a0408013004", Hex(FightProtocol.BuildSpectatorJoined(4)));
            Assert.Equal("1a020801", Hex(FightProtocol.BuildSpectatorJoined(0)));
        }

        [Fact]
        public async Task The_map_fight_list_uses_light_teams_and_the_signed_request_id()
        {
            var fight = Running();
            var (owner, frames) = SessionOn(Map);
            fight.StartedAt = new(2026, 10, 22, 9, 30, 0, DateTimeKind.Utc);
            fight.SetOption(FightInstance.Azules, FightInstance.OptionPartyOnly, true);
            fight.SetOption(FightInstance.Rojos, FightInstance.OptionSecret, true);
            fight.Azul.Add(new() { Id = 9_100_008, CurrentHP = 100, Level = 101 });
            fight.Azul.Add(new() { Id = -3, CurrentHP = 100, Level = 200, IsMonster = true, Invocador = 9_100_001 });
            fight.Azul.Add(new() { Id = 9_100_009, CurrentHP = 0, Level = 200 });
            Assert.True(Active.TryAdd(FightId, fight));
            try
            {
                using var ambient = SessionContext.Push(GameSession.SinSocket());
                await Dispatch(owner, new MapRunningFightsRequest(), int.MinValue);
                var frame = Assert.Single(frames);
                Assert.Equal(int.MinValue, frame.Uid);
                var listed = Assert.Single(Assert.IsType<MapRunningFightsResponse>(frame.Content).Fights);
                Assert.Null(listed.ArenaType);
                var info = Assert.IsType<FightExternalInformation>(listed.FightExternalInformation);
                Assert.Equal(FightId, info.FightId);
                Assert.Equal("2026-10-22T09:30:00Z", info.FightStart);
                Assert.True(info.FightSpectatorLocked);
                var blue = info.FightTeamsWithOptions[0];
                Assert.True(blue.FightTeamOptions!.IsRestrictedToParty);
                Assert.Equal(9_100_001, blue.FightTeam!.LeaderId);
                Assert.Equal(Team.Challenger, blue.FightTeam.Team);
                var light = Assert.IsType<FightTeamInformationCaseLightInformation>(blue.FightTeam.TeamInformationCase).Value;
                Assert.Equal(2, light.TeamMembersCount);
                Assert.Equal(75, light.AverageLevel);
                var red = info.FightTeamsWithOptions[1].FightTeam!;
                Assert.Equal(TeamType.Monster, red.TeamType);
                Assert.Equal(-20002, red.LeaderId);
            }
            finally { Active.TryRemove(FightId, out _); }
        }

        [Fact]
        public async Task A_fight_s_details_keep_both_sides_portraits_and_waves()
        {
            var (owner, frames) = SessionOn(Map);
            var fight = Running();
            fight.Rojo[0].Wave = 3;
            fight.Rojo.Add(new() { Id = -2, Level = 60, CurrentHP = 100, TaxCollectorFirstName = 12, TaxCollectorLastName = 34 });
            fight.Azul.Add(new() { Id = -3, IsMonster = true, CompanionId = 17, DrivenBy = 9_100_001,
                Level = 50, CurrentHP = 100 });
            Assert.True(Active.TryAdd(FightId, fight));
            try
            {
                await Dispatch(owner, new MapRunningFightDetailsRequest(int.MaxValue), -1);
                Assert.Empty(frames);
                await Dispatch(owner, new MapRunningFightDetailsRequest(FightId), 0);
                var frame = Assert.Single(frames);
                Assert.Equal(0, frame.Uid);
                var details = Assert.IsType<MapRunningFightDetailsResponse>(frame.Content);
                Assert.Collection(details.Attackers,
                    player => Assert.Equal(new FighterLightInformation(Gender.Female, true, 9_100_001, 8, 50, 0,
                        new FighterLightInformationCaseNamedInformation(new("Uno"))), player),
                    companion => Assert.Equal(new FighterLightInformation(Gender.Male, true, -3, 0, 50, 0,
                        new FighterLightInformationCaseEntityInformation(new(9_100_001, 17))), companion));
                Assert.Collection(details.Defenders,
                    monster => Assert.Equal(new FighterLightInformation(Gender.Male, true, -1, -2, 10, 3,
                        new FighterLightInformationCaseMonsterInformation(new(494))), monster),
                    collector => Assert.Equal(new FighterTaxCollectorLightInformation(12, 34),
                        Assert.IsType<FighterLightInformationCaseTaxCollectorInformation>(collector.FighterInfoCase).Value));
            }
            finally { Active.TryRemove(FightId, out _); }
        }

        [Fact]
        public async Task The_placement_preview_returns_the_next_fights_cells_without_consuming_them()
        {
            const long map = Map + 100;
            var (owner, frames) = SessionOn(map);
            var arena = MapManager.ResolveArenaMapId(map);
            try
            {
                owner.LeaveWorld();
                await Dispatch(owner, new FightMapRequest(), -1);
                Assert.Empty(frames);
                owner.EnterWorld();
                await Dispatch(owner, new FightMapRequest(), -1);
                var (attackers, defenders) = FightHandler.NextPlacement(map, arena);
                var frame = Assert.Single(frames);
                Assert.Equal(-1, frame.Uid);
                var preview = Assert.IsType<FightMapInformationResponse>(frame.Content);
                Assert.Equal(map, preview.MapId);
                Assert.Equal(arena, preview.FightMapId);
                Assert.NotEmpty(attackers);
                Assert.NotEmpty(defenders);
                Assert.Equal(attackers, preview.FightStartPositions!.ChallengersPositions);
                Assert.Equal(defenders, preview.FightStartPositions.DefendersPositions);
            }
            finally { FightHandler.TakePlacement(map, arena); }
        }

        [Fact]
        public async Task Watching_a_character_from_another_map_opens_a_spectator_context_once()
        {
            const long elsewhere = Map + 60;
            var (owner, frames) = SessionOn(elsewhere);
            owner.State.CellId = 123;
            var fight = Running();
            fight.StartFight();
            Assert.True(Active.TryAdd(FightId, fight));
            try
            {
                await Dispatch(owner, new FightSpectatePlayerRequest(9_100_001), -99);
#if JONDO_PROTOCOL_3_8_1_2
                Assert.Empty(frames);
                Assert.Equal(0, owner.State.SpectatingFightId);
#else
                Assert.Equal(FightId, owner.State.SpectatingFightId);
                Assert.True(owner.State.SpectatorBoardPending);
                Assert.Equal(elsewhere, owner.State.RoleplayMapId);
                Assert.Equal(123, owner.State.RoleplayCellId);
                Assert.Equal(fight.MapId, owner.MapId);
                Assert.Equal(new ContextRemoveElementEvent(owner.CharacterId), frames[0].Content);
                Assert.IsType<ContextDestroyEvent>(frames[1].Content);
                Assert.Equal(new ContextCreationEvent(GameContext.Fight), frames[2].Content);
                Assert.Equal(new MapCurrentEvent(fight.MapId), frames[3].Content);
                Assert.All(frames, frame => Assert.Null(frame.Uid));
                int sent = frames.Count;
                await Dispatch(owner, new FightSpectatePlayerRequest(9_100_001), -100);
                Assert.Equal(sent, frames.Count);
#endif
            }
            finally
            {
                FightHandler.ForgetSpectator(owner);
                Active.TryRemove(FightId, out _);
            }
        }

        private static async Task Dispatch(GameSession owner, GameRequest request, int uid)
        {
            using var turn = await owner.EnterTurnAsync();
            Assert.Equal(GameEntryResult.Handled, await GameNodeProxy.DispatchTypedAsync(owner, new(request, uid),
                new GameEntryService(owner, new GameEntryBackend())));
        }

        private static (GameSession Owner, List<GameFrame> Frames) SessionOn(long map)
        {
            var frames = new List<GameFrame>();
            var owner = new GameSession(new RecordingCodec(frames), _ => Task.CompletedTask);
            owner.BindAccount(9_100_002, 1);
            owner.State.CharacterId = 9_100_002;
            owner.State.MapId = map;
            owner.EnterWorld();
            return (owner, frames);
        }

        private sealed class RecordingCodec(List<GameFrame> frames) : IGameCodec
        {
            public GameFrame Decode(ReadOnlySpan<byte> payload) => throw new NotSupportedException();
            public byte[] Encode(GameFrame message)
            {
                frames.Add(message);
                return [];
            }
        }
    }
}

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
    /// Spectator admission, board delivery and the map fight window's requests.
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
            }
            finally
            {
                FightHandler.ForgetSpectator(owner);
                Active.TryRemove(FightId, out _);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Both_admission_paths_send_the_current_public_board_once(bool byPlayer)
        {
            var (owner, frames) = SessionOn(byPlayer ? Map + 60 : Map);
            var (bystander, otherFrames) = SessionOn(Map + 61);
            var fight = Running();
            var player = fight.Azul[0];
            var monster = fight.Rojo[0];
            monster.Initiative = 1000;
            monster.Wave = 3;
            monster.CurrentHP = 38;
            monster.LifeTopTold = 150;
            fight.UpdateTurnOrder();
            fight.StartFight();
            fight.PlayTwiceARound(monster.Id, 100);
            fight.NextTurn();
            fight.NextTurn();
            // Keep the fallen summon in the roster; it must not occupy a cell or turn slot.
            var fallen = new Fighter { Id = -3, TeamId = 0, MonsterId = 495, IsMonster = true,
                Invocador = player.Id, CellId = 222, MaxHP = 50, CurrentHP = 0 };
            fight.Azul.Add(fallen);
            player.Buffs.Poner(new() { EffectId = 950, Estado = 3793, Quien = monster.Id, EffectUid = 299043,
                HechizoOrigen = 25188, NivelOrigen = 2, Critico = true, CaducaEnRonda = -1 }, () => 1);
            player.Buffs.Poner(new() { EffectId = 952, Pendiente = true }, () => 2);
            fallen.Buffs.Poner(new() { EffectId = 950, Estado = 10 }, () => 3);
            if (byPlayer)
                fight.LastAnnouncedTurn = new(monster.Id, 0, fight.RoundNumber, 1000, DateTime.UtcNow, Carried: 41);

            Assert.True(Active.TryAdd(FightId, fight));
            try
            {
                using var ambient = SessionContext.Push(bystander);
                GameRequest watch = byPlayer ? new FightSpectatePlayerRequest(player.Id) : new FightJoinRequest(0, FightId);
                await Dispatch(owner, watch, -3);
                Assert.True(owner.State.SpectatorBoardPending);
                Assert.False(owner.State.IsInFight);
                frames.Clear();

                await Dispatch(owner, new ContextReadyRequest(owner.State.RoleplayMapId), -4);
                Assert.Empty(frames);
                Assert.True(owner.State.SpectatorBoardPending);

                GameRequest ready = byPlayer ? new FightPingsRequest() : new ContextReadyRequest(fight.MapId);
                await Dispatch(owner, ready, -5);
                Assert.False(owner.State.SpectatorBoardPending);

                var board = frames.Select(frame => frame.Content).ToArray();
                var prefix = new[] { typeof(FightPingsEvent), typeof(FightStartingEvent), typeof(FightSpectatorJoinEvent),
                    typeof(FightFighterShowEvent), typeof(FightFighterShowEvent), typeof(FightFighterShowEvent),
                    typeof(EntitiesDispositionEvent), typeof(FightSynchronizeEvent) };
                Type[] tail = [typeof(FightNewRoundEvent), typeof(FightOptionUpdateEvent), typeof(FightTurnListEvent),
                    typeof(FightEffectsRefreshEvent), typeof(FightMapInformation), typeof(AreaFightModifierUpdateEvent)];
                Assert.Equal(prefix.Concat(byPlayer ? [typeof(FightTurnEvent)] : Array.Empty<Type>()).Concat(tail),
                    board.Select(message => message.GetType()));
                Assert.All(frames, frame => Assert.Null(frame.Uid));
                Assert.Empty(otherFrames);
                Assert.Same(bystander, SessionContext.Actual);

                var start = Assert.IsType<FightStartingEvent>(board[1]);
                Assert.Equal(FightId, start.FightId);
                Assert.Equal([494], start.MonsterIds);
                Assert.Equal(new FightJoinEvent(true, false, 0, FightType.PvM),
                    Assert.IsType<FightSpectatorJoinEvent>(board[2]).Join);
                Assert.Equal(new[] { player.Id, fallen.Id, monster.Id },
                    board.OfType<FightFighterShowEvent>().Select(show => show.Information.ActorId));
                Assert.Equal(new[] { (player.Id, 200), (monster.Id, 300) },
                    Assert.IsType<EntitiesDispositionEvent>(board[6]).Added.Select(spot => (spot.EntityId!.Value, spot.CellId!.Value)));

                var sync = Assert.IsType<FightSynchronizeEvent>(board[7]).Fighters;
                Assert.Equal(new[] { monster.Id, player.Id, fallen.Id }, sync.Select(actor => actor.ActorId));
                var creature = Assert.IsType<ActorInformationCaseFightActor>(sync[0].ActorInformation!.ActorCase).Value;
                Assert.Equal(3, creature.Wave);
                Assert.Equal(-62, Assert.IsType<CharacterCharacteristicCaseValue>(creature.FightCharacteristics.Characteristics
                    .Single(stat => stat.CharacteristicId == 97).CharacteristicCase).Value.Total);
                var summon = Assert.IsType<ActorInformationCaseFightActor>(sync[2].ActorInformation!.ActorCase).Value;
                Assert.False(summon.SpawnInformation.Alive);
                Assert.Equal(Team.Challenger, summon.SpawnInformation.Team);
                Assert.True(summon.FightCharacteristics.Summoned);
                Assert.Equal(player.Id, summon.FightCharacteristics.Summoner);
                Assert.Equal(150, monster.LifeTopTold);
                Assert.Equal(new FightTurnEntity[] { new FightTurnEntityCaseWithFlag(new(false, monster.Id)),
                    new FightTurnEntityCaseWithFlag(new(false, player.Id)), new FightTurnEntityCaseWithAdditionalId(new(monster.Id, "-1_2")) },
                    Assert.Single(board.OfType<FightTurnListEvent>()).Turns);
                Assert.Equal(new FightNewRoundEvent(0, 2), Assert.Single(board.OfType<FightNewRoundEvent>()));

                if (byPlayer)
                {
                    var turn = Assert.Single(board.OfType<FightTurnEvent>());
                    Assert.Equal(monster.Id, turn.CharacterId);
                    Assert.Equal(1000, turn.WaitDeciseconds);
                    Assert.Equal(41, turn.RemainingDeciseconds);
                    Assert.InRange(turn.TurnTimeOverrideDeciseconds!.Value, 1, 1041);
                }
                var effect = Assert.Single(Assert.Single(board.OfType<FightEffectsRefreshEvent>()).Effects);
                Assert.Equal(monster.Id, effect.SourceId);
                Assert.Equal(950, effect.ActionId);
                Assert.Equal(player.Id, effect.Effect.TargetId);
                Assert.Equal(3793, effect.Effect.Param3);
                Assert.Equal(FightEffectKind.State, effect.Effect.Kind);
                Assert.True(effect.Effect.Critical);
                Assert.Equal(new FightTurn(-1, 0), effect.Effect.EndTurn);
                Assert.Equal(fight.MapId, Assert.Single(board.OfType<FightMapInformation>()).MapId);
                Assert.Empty(Assert.IsType<AreaFightModifierUpdateEvent>(board[^1]).SpellPairs);

                await Dispatch(owner, new ContextReadyRequest(fight.MapId), -6);
                await Dispatch(owner, new FightPingsRequest(), -7);
                await Dispatch(owner, new MapInformationRequest(checked((int)fight.MapId)), -8);
                Assert.Equal(board.Length, frames.Count);
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
            var owner = new GameSession(new CombatRecordingCodec(frames.Add), CombatRecordingCodec.RequireTypedPacketAsync);
            owner.BindAccount(9_100_002, 1);
            owner.State.CharacterId = 9_100_002;
            owner.State.MapId = map;
            owner.EnterWorld();
            return (owner, frames);
        }
    }
}

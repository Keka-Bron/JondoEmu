using System.Collections.Concurrent;
using System.Reflection;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat;

[Collection("Challenges table")]
public sealed class GameEntryFightMapTests
{
    internal sealed class Board : IDisposable
    {
        private readonly long Id;
        private static readonly ConcurrentDictionary<long, FightInstance> Active = (ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
            .GetField("_activeFights", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        internal readonly FightInstance Fight;
        internal readonly List<GameFrame> Frames = [];
        internal readonly GameSession Session;

        private readonly bool _ownsSession;

        /// <param name="shift">Moves every id this board uses, for a test class that runs beside this one.</param>
        internal Board(GameSession? opener = null, long shift = 0)
        {
            Id = 7_100_852 + shift;
            Fight = new(Id, 990_100_851 + shift, 990_100_852 + shift) { OpenerId = 990_000_852 + shift };
            _ownsSession = opener == null;
            Session = opener ?? new(new CombatRecordingCodec(Frames.Add), CombatRecordingCodec.RequireTypedPacketAsync);
            if (_ownsSession)
            {
                Session.BindAccount(Id, 1);
                Session.State.CharacterId = 990_000_852 + shift;
                Session.EnterWorld();
                Assert.True(SessionRegistry.Register(Session));
            }
            Session.State.FightId = Id;
            Session.State.MapId = Fight.MapId;
            Session.State.IsInFight = true;
            Fight.Azul.Add(new() { Id = Session.CharacterId, Name = "Opener", Breed = 8, Level = 123,
                CellId = 300, CurrentHP = 90, MaxHP = 100, MaxAP = 7, CurrentAP = 7 });
            Fight.Rojo.Add(new() { Id = -1, IsMonster = true, MonsterId = 492, TeamId = 1, LookBoneId = 101,
                GradeIndex = 2, Level = 80, CellId = 271, CurrentHP = 75, MaxHP = 75 });
            Assert.True(Active.TryAdd(Id, Fight));
        }

        internal async Task Send(GameRequest request)
        {
            using var turn = await Session.EnterTurnAsync();
            Assert.Equal(GameEntryResult.Handled, await GameNodeProxy.DispatchTypedAsync(Session, new(request, -1),
                new GameEntryService(Session, new GameEntryBackend())));
        }

        public void Dispose()
        {
            Active.TryRemove(Id, out _);
            Fight.CancelTurnTimer();
            if (_ownsSession) SessionRegistry.Unregister(Session);
        }
    }

    [Fact]
    public async Task Placement_board_and_challenge_selection_use_the_recipient_codec()
    {
        Challenges.Initialize();
        Challenges.OnlyOffer(ChallengeWatcher.Watched);
        using var board = new Board();
        var fight = board.Fight;
        await board.Send(new ContextReadyRequest(fight.RoleplayMapId));
        Assert.Empty(board.Frames);
        await board.Send(new ContextReadyRequest(fight.MapId));
        Assert.True(fight.HasPrepared(board.Session.CharacterId));
        Assert.All(board.Frames, f => Assert.Null(f.Uid));
        var events = board.Frames.Select(f => f.Content).ToList();
        Assert.Equal(new long?[] { board.Session.CharacterId, -1 }, events.Take(2).Select(e =>
            Assert.Single(Assert.IsType<EntitiesDispositionEvent>(e).Added).EntityId));
        Assert.IsType<ChallengeNumberEvent>(events[2]);
        Assert.Equal(new ChallengeModSelectedEvent(ChallengeMod.Choice), events[3]);
        var shows = events.OfType<FightFighterShowEvent>().Select(e => e.Information).ToArray();
        Assert.Equal(new[] { board.Session.CharacterId, -1L }, shows.Select(p => p.ActorId));
        var human = Assert.IsType<ActorInformationCaseFightActor>(shows[0].ActorInformation.ActorCase).Value;
        Assert.Equal("Opener", Assert.IsType<FightFighterInformationCaseNamedFighter>(human.FighterCase).Value.Name);
        Assert.Equal(Team.Challenger, human.SpawnInformation.Team);
        Assert.Contains(human.FightCharacteristics.Characteristics, c => c.CharacteristicId == 1 && c.CharacteristicCase is CharacterCharacteristicCaseUsable);
        var monster = Assert.IsType<ActorInformationCaseFightActor>(shows[1].ActorInformation.ActorCase).Value;
        var ai = Assert.IsType<FightFighterInformationCaseAiFighter>(monster.FighterCase).Value;
        Assert.Equal(new FightMonsterInformation(3, 492, 80), Assert.IsType<FightAIFighterInformationCaseMonster>(ai.FighterCase).Value);
        Assert.Equal(101, shows[1].ActorInformation.Look.Bones);
        Assert.Equal(Team.Defender, monster.SpawnInformation.Team);
        Assert.Equal(events[2], events[events.FindIndex(e => e is FightPlacementPossiblePositionsEvent) + 1]);
        Assert.Equal(fight.MapId, Assert.IsType<FightMapInformation>(events[^2]).MapId);
        Assert.IsType<AreaFightModifierUpdateEvent>(events[^1]);
        await board.Send(new MapInformationRequest((int)fight.MapId));
        Assert.Equal(events.Count, board.Frames.Count);

        board.Frames.Clear();
        await board.Send(new ChallengeBonusChoiceRequest(ChallengeBonus.Drop));
        Assert.Equal(new ChallengeBonusChoiceSelectedEvent(ChallengeBonus.Drop), Assert.Single(board.Frames).Content);
        await board.Send(new ChallengeReadyRequest());
        var offers = Assert.IsType<ChallengeProposalEvent>(board.Frames[^1].Content).ChallengeProposals;
        Assert.NotEmpty(offers);
        var selected = offers[0];
        await board.Send(new ChallengeSelectionRequest(selected.ChallengeId));
        Assert.Equal(selected.ChallengeId, Assert.IsType<ChallengeSelectedEvent>(board.Frames[^1].Content).Challenge.ChallengeId);
        Assert.Empty(fight.ChallengesFixed);
        await board.Send(new ChallengeValidateRequest(0));
        var added = Assert.IsType<ChallengeAddEvent>(board.Frames[^1].Content).Challenge;
        Assert.Equal(selected.ChallengeId, added.ChallengeId);
        Assert.Equal(selected.ExperienceBonus, added.DropBonus);
        Assert.Contains((added.ChallengeId, added.DropBonus), fight.ChallengesFixed);
    }

    [Theory]
    [InlineData("before-join", false)]
    [InlineData("loading", false)]
    [InlineData("loaded", false)]
    [InlineData("during-offer", false)]
    [InlineData("loaded", true)]  // A second slot is filled by the server at fight start.
    public async Task Joiner_sees_each_locked_challenge_once_and_the_same_final_list(string whenChosen, bool twoSlots)
    {
        Challenges.Initialize();
        Challenges.OnlyOffer(ChallengeWatcher.Watched);
        const long id = 990_000_852;
        var a = new ChallengeCodec();
        var b = new ChallengeCodec();
        await using var opener = await ClientPipe.OpenAsync(id + 100, id, 990_100_851, codec: a.Recorder);
        using var setup = new Board(opener.Session);
        var fight = setup.Fight;
        fight.BluePlacementCells.AddRange([300, 314]);
        // Let at least one decisecond elapse before loading the joiner's board.
        await Task.Delay(150);
        int placementLimit = fight.Reglas.RelojDeColocacion;
        await using var joiner = await ClientPipe.OpenAsync(id + 101, id + 1, fight.RoleplayMapId, codec: b.Recorder);
        async Task Send(ClientPipe pipe, GameRequest request)
        {
            using var turn = await pipe.Session.EnterTurnAsync();
            Assert.Equal(GameEntryResult.Handled, await GameNodeProxy.DispatchTypedAsync(pipe.Session, new(request, -1),
                new GameEntryService(pipe.Session, new GameEntryBackend())));
        }
        var load = new ContextReadyRequest(fight.MapId);
        var join = new FightJoinRequest(id, (int)fight.FightId);
        await Send(opener, load);
        if (whenChosen != "before-join") await Send(joiner, join);
        if (whenChosen == "loaded") await Send(joiner, load);
        if (twoSlots) fight.ChallengesToPick = 2;
        if (whenChosen == "loaded")
        {
            await Send(joiner, new ChallengeReadyRequest());
            Assert.Empty(fight.ChallengesOffered);
        }
        await Send(opener, new ChallengeReadyRequest());
        var offer = Assert.Single(a.Events.OfType<ChallengeProposalEvent>());
        var choice = offer.ChallengeProposals[0];
        await Send(opener, new ChallengeSelectionRequest(choice.ChallengeId));
        if (whenChosen == "during-offer") await Send(joiner, load);
        if (whenChosen is "loaded" or "during-offer")
        {
            var observed = Assert.Single(b.Events.OfType<ChallengeProposalEvent>());
            Assert.Equal(offer.ChallengeProposals.Select(c => c.ChallengeId), observed.ChallengeProposals.Select(c => c.ChallengeId));
            Assert.InRange(observed.Timer, 0, offer.Timer);
            Assert.Equal(choice.ChallengeId, Assert.Single(b.Events.OfType<ChallengeSelectedEvent>()).Challenge.ChallengeId);
            var other = offer.ChallengeProposals[1];
            await Send(joiner, new ChallengeSelectionRequest(other.ChallengeId));
            Assert.Equal(choice.ChallengeId, fight.ChallengeMarked);
            await Send(joiner, new ChallengeValidateRequest(choice.ChallengeId));
            Assert.Empty(fight.ChallengesFixed);
            choice = other;
            await Send(opener, new ChallengeSelectionRequest(choice.ChallengeId));
            Assert.Equal(choice.ChallengeId, b.Events.OfType<ChallengeSelectedEvent>().Last().Challenge.ChallengeId);
        }
        await Send(opener, new ChallengeValidateRequest(choice.ChallengeId));
        if (whenChosen == "before-join") await Send(joiner, join);
        if (whenChosen is not ("loaded" or "during-offer"))
        {
            Assert.Empty(b.Events.OfType<ChallengeAddEvent>());
            await Send(joiner, load);
        }
        await Send(joiner, load); // Duplicate map-ready must not replay the choice again.
        var start = Assert.Single(b.Events.OfType<FightStartingEvent>());
        Assert.Equal(id, start.AttackerId);
        Assert.Equal(FightType.PvM, start.FightType);
        Assert.Equal((int)fight.FightId, start.FightId);
        var joinState = Assert.Single(b.Events.OfType<FightJoinEvent>());
        Assert.InRange(joinState.TimeMaxBeforeFightStart, 1, placementLimit - 1);
        var ownSide = Assert.Single(b.Events.Skip(b.Events.IndexOf(start)).OfType<FightTeamUpdateEvent>()).Team;
        Assert.Equal(Team.Challenger, ownSide.Team);
        Assert.Equal(id, ownSide.LeaderId);
        Assert.Empty(Assert.IsType<FightTeamInformationCaseMembers>(ownSide.TeamInformationCase).Value.Members);
        if (!twoSlots) await Send(joiner, new ChallengeReadyRequest());
        foreach (var codec in new[] { a, b })
            Assert.Equal(choice.ChallengeId, Assert.Single(codec.Events.OfType<ChallengeAddEvent>()).Challenge.ChallengeId);
        Assert.True(b.Events.FindLastIndex(e => e is ChallengeNumberEvent) < b.Events.FindIndex(e => e is ChallengeAddEvent));

        await Send(joiner, new FightReadyRequest(true));
        await Send(opener, new FightReadyRequest(true));
        Assert.Equal(FightState.Ongoing, fight.State);
        Assert.Equal(twoSlots ? 2 : 1, fight.ChallengesFixed.Count);
        foreach (var codec in new[] { a, b })
        {
            Assert.Equal(fight.ChallengesFixed, codec.Events.OfType<ChallengeAddEvent>().Select(e => (e.Challenge.ChallengeId, e.Challenge.DropBonus)));
            Assert.Equal(fight.ChallengesFixed, Assert.Single(codec.Events.OfType<ChallengeListEvent>()).Challenges.Select(c => (c.ChallengeId, c.DropBonus)));
        }
    }

    private sealed class ChallengeCodec
    {
        internal readonly List<GameEvent> Events = [];
        internal IGameCodec Recorder => new CombatRecordingCodec(frame => Events.Add(Assert.IsAssignableFrom<GameEvent>(frame.Content)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Queued_board_is_not_sent_after_departure_or_start(bool leaves)
    {
        using var board = new Board();
        var fight = board.Fight;
        await fight.Turno.WaitAsync();
        Task queued;
        try
        {
            queued = board.Send(new ContextReadyRequest(fight.MapId));
            Assert.False(queued.IsCompleted);
            if (leaves) fight.Gone.Add(board.Session.CharacterId);
            else fight.StartFight();
        }
        finally { fight.Turno.Release(); }
        await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(board.Frames);
        Assert.False(fight.HasPrepared(board.Session.CharacterId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_roleplay_map_requests_are_consumed_without_spending_fight_preparation_or_closing_the_session(bool contextReady)
    {
        const long characterId = 990_000_851, fightId = 7_100_851, arena = 990_100_851;
        var active = (ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
            .GetField("_activeFights", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var fight = new FightInstance(fightId, arena);
        fight.Azul.Add(new Fighter { Id = characterId, CurrentHP = 100 });
        await using var pipe = await ClientPipe.OpenAsync(8_900_000_851, characterId, arena);
        pipe.Session.State.IsInFight = true;
        pipe.Session.State.FightId = fightId;
        Assert.True(active.TryAdd(fightId, fight));
        try
        {
            GameRequest request = contextReady
                ? new ContextReadyRequest(arena - 1)
                : new MapInformationRequest((int)arena - 1);
            var entry = new GameEntryService(pipe.Session, new GameEntryBackend());
            using var turn = await pipe.Session.EnterTurnAsync();

            Assert.Equal(GameEntryResult.Handled, await entry.HandleAsync(new(request, -1)));

            Assert.False(fight.HasPrepared(characterId));
            Assert.Same(fight, FightHandler.PendingPreparation(pipe.Session));
            Assert.True(pipe.Session.IsInWorld);
            Assert.Equal(arena, pipe.Session.MapId);
            await pipe.Quiet();
        }
        finally { active.TryRemove(fightId, out _); }
    }
}

/// <summary>
/// The board's map lookup reads <see cref="MapManager.Maps"/>, which the "MapManager" collection swaps
/// while it reloads the world, so a test that plants a map there runs in that collection.
/// </summary>
[Collection("MapManager")]
public sealed class GameEntryFightMapInfoTests
{
    [Theory]
    [InlineData(FightType.PvpArena)]
    [InlineData(FightType.PvT)]
    public async Task Timed_board_keeps_the_fight_type_and_the_source_coordinates_at_the_origin(FightType type)
    {
        using var board = new GameEntryFightMapTests.Board(shift: 5_000);
        var fight = board.Fight;
        fight.Reglas = type == FightType.PvpArena ? FightRules.Koliseo : FightRules.ContraRecaudador;
        if (type == FightType.PvT)
        {
            fight.TaxCollectorUuid = "collector";
            fight.DefenderLeaderId = -20_074;
        }
        Assert.True(MapManager.Maps.TryAdd(fight.RoleplayMapId, new() { SubAreaId = 77, PosX = 0, PosY = 0 }));
        try { await board.Send(new ContextReadyRequest(fight.MapId)); }
        finally { MapManager.Maps.Remove(fight.RoleplayMapId); }

        var events = board.Frames.Select(f => f.Content).ToArray();
        var start = Assert.Single(events.OfType<FightStartingEvent>());
        Assert.Equal(type, start.FightType);
        Assert.Empty(start.MonsterIds);
        var join = Assert.Single(events.OfType<FightJoinEvent>());
        Assert.Equal(type, join.FightType);
        Assert.InRange(join.TimeMaxBeforeFightStart, 1, fight.Reglas.RelojDeColocacion);
        Assert.Equal(new FightMapInformation(77, fight.MapId, new FightMapInformationPositionCaseCoords(new(0, 0))),
            Assert.Single(events.OfType<FightMapInformation>()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Defender_gets_own_placement_cells_and_distinct_companion_and_collector_identities(bool duel)
    {
        using var board = new GameEntryFightMapTests.Board(shift: 5_000);
        var fight = board.Fight;
        var defender = fight.Azul[0];
        fight.Azul.Remove(defender);
        fight.AddOpponent(defender);
        fight.AddPlayer(new() { Id = -5, CurrentHP = 40, Level = 50, IsMonster = true,
            DrivenBy = defender.Id, CompanionId = 9, LookBoneId = 101 });
        var collector = fight.Rojo[0];
        collector.TaxCollectorFirstName = 35;
        collector.TaxCollectorLastName = 109;
        if (duel) fight.Reglas = FightRules.Desafio;
        fight.BluePlacementCells.Add(314);
        fight.RedPlacementCells.Add(271);
        Assert.True(MapManager.Maps.TryAdd(fight.RoleplayMapId, new() { SubAreaId = 77, PosX = -4, PosY = 10 }));
        try { await board.Send(new ContextReadyRequest(fight.MapId)); }
        finally { MapManager.Maps.Remove(fight.RoleplayMapId); }
        var events = board.Frames.Select(f => f.Content).ToArray();
        var positions = Assert.Single(events.OfType<FightPlacementPossiblePositionsEvent>());
        Assert.Equal(Team.Defender, positions.Team);
        Assert.Equal(314, Assert.Single(positions.StartingPositions.ChallengersPositions));
        Assert.Equal(271, Assert.Single(positions.StartingPositions.DefendersPositions));
        var actors = events.OfType<FightFighterShowEvent>().ToDictionary(e => e.Information.ActorId,
            e => Assert.IsType<ActorInformationCaseFightActor>(e.Information.ActorInformation.ActorCase).Value);
        Assert.Equal(9, Assert.IsType<FightFighterInformationCaseEntityFighter>(actors[-5].FighterCase).Value.EntityModelId);
        var ai = Assert.IsType<FightFighterInformationCaseAiFighter>(actors[collector.Id].FighterCase).Value;
        Assert.Equal(new FightTaxCollectorInformation(35, 109, 492), Assert.IsType<FightAIFighterInformationCaseTaxCollector>(ai.FighterCase).Value);
        var turns = Assert.Single(events.OfType<FightTurnListEvent>()).Turns;
        Assert.Equal(new[] { -5L, -1L, defender.Id }.Order(),
            turns.Select(turn => Assert.IsType<FightTurnEntityCaseWithFlag>(turn).Value.EntityId).Order());
        Assert.Equal(new FightMapInformation(77, fight.MapId, new FightMapInformationPositionCaseCoords(new(-4, 10))),
            Assert.Single(events.OfType<FightMapInformation>()));
        if (duel)
        {
            Assert.Empty(events.OfType<ChallengeNumberEvent>());
            Assert.Equal(0, Assert.Single(events.OfType<FightJoinEvent>()).TimeMaxBeforeFightStart);
        }
    }
}

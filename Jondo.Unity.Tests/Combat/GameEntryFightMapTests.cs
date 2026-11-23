using System.Collections.Concurrent;
using System.Reflection;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
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
    private sealed class Board : IDisposable
    {
        private const long Id = 7_100_852;
        private static readonly ConcurrentDictionary<long, FightInstance> Active = (ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
            .GetField("_activeFights", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        internal readonly FightInstance Fight = new(Id, 990_100_851, 990_100_852);
        internal readonly List<GameFrame> Frames = [];
        internal readonly GameSession Session;

        internal Board()
        {
            var codec = new GameCodec();
            Session = new(codec, packet => { Frames.Add(codec.Decode(packet)); return Task.CompletedTask; });
            Session.BindAccount(Id, 1);
            Session.State.CharacterId = 990_000_852;
            Session.State.FightId = Id;
            Session.State.MapId = Fight.MapId;
            Session.State.IsInFight = true;
            Session.EnterWorld();
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

        public void Dispose() => Active.TryRemove(Id, out _);
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
        Assert.Equal(2, board.Frames.Count);
        Assert.Empty(fight.ChallengesFixed);
        await board.Send(new ChallengeValidateRequest(0));
        var added = Assert.IsType<ChallengeAddEvent>(board.Frames[2].Content).Challenge;
        Assert.Equal(selected.ChallengeId, added.ChallengeId);
        Assert.Equal(selected.ExperienceBonus, added.DropBonus);
        Assert.Contains((added.ChallengeId, added.DropBonus), fight.ChallengesFixed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Defender_gets_own_placement_cells_and_distinct_companion_and_collector_identities(bool duel)
    {
        using var board = new Board();
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
        Assert.Equal(new FightMapInformation(77, fight.MapId, new FightMapInformationPositionCaseCoords(new(-4, 10))),
            Assert.Single(events.OfType<FightMapInformation>()));
        if (duel)
        {
            Assert.Empty(events.OfType<ChallengeNumberEvent>());
            Assert.Equal(0, Assert.Single(events.OfType<FightJoinEvent>()).TimeMaxBeforeFightStart);
        }
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

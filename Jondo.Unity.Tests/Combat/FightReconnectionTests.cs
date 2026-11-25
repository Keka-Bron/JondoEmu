using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat;

public sealed class FightReconnectionTests
{
    private const int FightId = 7_400_901;
    private const int Arena = 990_400_901;
    private const int Roleplay = Arena - 1;
    private const long PlayerId = 9_400_901;
    private static readonly ConcurrentDictionary<long, FightInstance> Active =
        (ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
            .GetField("_activeFights", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    [Fact]
    public async Task Reconnect_during_a_turn_sends_the_current_board_and_private_controls_once()
    {
        using var returning = new Client(PlayerId, reconnecting: true);
        using var teammate = new Client(PlayerId + 1, reconnecting: false);
        var fight = new FightInstance(FightId, Roleplay, Arena);
        var player = Person(PlayerId, 200);
        player.Name = "Copied";
        player.Breed = 4;
        player.Sex = 1;
        player.IsBot = true; // Resolve the double's original identity from the fighter, without a character row.
        player.GrantedSpells.Add((370, 1));
        player.Recarga[370] = 3;
        var ally = Person(PlayerId + 2, 201);
        ally.IsPlayedAlly = true;
        ally.DrivenBy = player.Id;
        ally.HechizosDeInvocado = [(755, 1)];
        ally.Recarga[755] = 1;
        var summon = new Fighter { Id = -2, Invocador = player.Id, IsMonster = true, MonsterId = 495,
            CellId = 210, CurrentHP = 50, MaxHP = 60, HechizosDeInvocado = [(754, 2)] };
        summon.Recarga[754] = 2;
        var enemy = new Fighter { Id = -1, IsMonster = true, MonsterId = 494, Initiative = 1000,
            CellId = 300, CurrentHP = 38, MaxHP = 100, Wave = 3 };
        fight.AddPlayer(player);
        fight.AddPlayer(Person(teammate.Session.CharacterId, 203));
        fight.AddPlayer(ally);
        fight.AddPlayer(summon);
        fight.AddMonster(enemy);
        fight.SetOption(FightInstance.Azules, FightInstance.OptionSecret, true);
        fight.ChallengesFixed.Add((ChallengeWatcher.AsesinoASueldo, 70));
        fight.StartFight();
        var fallen = new Fighter { Id = -3, Invocador = player.Id, IsMonster = true, MonsterId = 495,
            CellId = 222, CurrentHP = 0, MaxHP = 50 };
        fight.Azul.Add(fallen);
        var doubleOfPlayer = new Fighter { Id = -4, Invocador = player.Id, DoubleOf = player.Id, IsMonster = true,
            Name = player.Name, Level = player.Level, CellId = 223, CurrentHP = 50, MaxHP = 50, JuegaTurno = false };
        fight.Azul.Add(doubleOfPlayer);
        var otherSummon = new Fighter { Id = -5, Invocador = teammate.Session.CharacterId, IsMonster = true,
            MonsterId = 495, CellId = 224, CurrentHP = 50, MaxHP = 50 };
        fight.Azul.Add(otherSummon);
        player.Buffs.Poner(new() { EffectId = 950, Estado = 3793, Quien = enemy.Id, EffectUid = 299043,
            HechizoOrigen = 25188, NivelOrigen = 2, Critico = true, CaducaEnRonda = -1 }, () => 1);
        player.Buffs.Poner(new() { EffectId = 952, Pendiente = true }, () => 2);
        fallen.Buffs.Poner(new() { EffectId = 950, Estado = 10 }, () => 3);
        enemy.Buffs.Poner(new() { EffectId = Buffs.DanoSufridoPorCiento, Cuanto = 104, Quien = player.Id,
            HechizoOrigen = 31860, NivelOrigen = 1, Disparador = "D", CaducaEnRonda = 2 }, () => 4);
        enemy.Buffs.Poner(new() { EffectId = 776, Caracteristica = 75, Cuanto = 4, Quien = player.Id,
            HechizoOrigen = 31860, NivelOrigen = 1, CaducaEnRonda = 2, AnnouncedParameters = (4, 0, 0) }, () => 5);
        var announced = new FightInstance.AnnouncedTurn(enemy.Id, fight.CurrentTurnIndex, fight.RoundNumber,
            1000, DateTime.UtcNow.AddSeconds(-5), Carried: 41);
        fight.LastAnnouncedTurn = announced;
        Assert.True(fight.AtenderElTurnoUnaVez(fight.RoundNumber, fight.TurnSlot, enemy.Id));

        Assert.True(Active.TryAdd(FightId, fight));
        try
        {
            using var ambient = SessionContext.Push(teammate.Session);
            await returning.Request(new ContextReadyRequest(Roleplay));
            await returning.Request(new MapInformationRequest(Arena));
            Assert.Empty(returning.Frames);
            Assert.True(returning.Session.State.FightRejoinPending);

            int before = announced.RemainingDeciseconds(DateTime.UtcNow);
            await returning.Request(new FightPingsRequest());
            Assert.Same(teammate.Session, SessionContext.Actual);
            Assert.False(returning.Session.State.FightRejoinPending);
            Assert.Empty(teammate.Frames);
            Assert.All(returning.Frames, frame => Assert.Null(frame.Uid));
            var board = returning.Frames.Select(frame => frame.Content).ToArray();
            Type[] prefix = [typeof(FightPingsEvent), typeof(FightStartingEvent), typeof(FightJoinEvent)];
            Type[] tail = [typeof(FightActorContextEvent), typeof(FightSpellCooldownEvent), typeof(FightActorContextEvent),
                typeof(FightSpellCooldownEvent), typeof(FightActorContextEvent), typeof(EntitiesDispositionEvent),
                typeof(FightSynchronizeEvent), typeof(FightTurnEvent), typeof(ChallengeListEvent), typeof(FightNewRoundEvent),
                typeof(FightOptionUpdateEvent), typeof(FightTurnListEvent), typeof(FightEffectsRefreshEvent),
                typeof(FightMapInformation), typeof(FightSpellCooldownEvent)];
            Assert.Equal(prefix.Concat(Enumerable.Repeat(typeof(FightFighterShowEvent), 8)).Concat(tail),
                board.Select(message => message.GetType()));

            var start = Assert.IsType<FightStartingEvent>(board[1]);
            Assert.Equal(FightId, start.FightId);
            Assert.Equal(PlayerId, start.AttackerId);
            Assert.Equal([494], start.MonsterIds);
            Assert.Equal(new FightJoinEvent(true, true, 0, FightType.PvM), board[2]);
            long[] roster = [player.Id, teammate.Session.CharacterId, ally.Id, summon.Id, fallen.Id, doubleOfPlayer.Id,
                otherSummon.Id, enemy.Id];
            var shown = board.OfType<FightFighterShowEvent>().Select(show => show.Information).ToArray();
            Assert.Equal(roster, shown.Select(actor => actor.ActorId));
            var positions = Assert.Single(board.OfType<EntitiesDispositionEvent>());
            Assert.Equal(roster.Where(id => id != fallen.Id), positions.Added.Select(spot => spot.EntityId!.Value));
            Assert.Equal(300, Assert.Single(positions.Added, spot => spot.EntityId == enemy.Id).CellId);
            var sync = Assert.Single(board.OfType<FightSynchronizeEvent>()).Fighters;
            Assert.Equal(fight.TurnOrder.Select(f => f.Id).Concat([fallen.Id, doubleOfPlayer.Id, otherSummon.Id]),
                sync.Select(actor => actor.ActorId));
            var dead = Assert.IsType<ActorInformationCaseFightActor>(sync.Single(actor => actor.ActorId == fallen.Id)
                .ActorInformation!.ActorCase).Value;
            Assert.False(dead.SpawnInformation.Alive);
            Assert.Equal(player.Id, dead.FightCharacteristics.Summoner);
            foreach (var view in new[] { shown, sync })
            {
                var actor = Assert.Single(view, a => a.ActorId == doubleOfPlayer.Id);
                var copy = Assert.IsType<ActorInformationCaseFightActor>(actor.ActorInformation!.ActorCase).Value;
                Assert.Equal(doubleOfPlayer.Id, copy.FighterId);
                Assert.True(copy.FightCharacteristics.Summoned);
                Assert.Equal(player.Id, copy.FightCharacteristics.Summoner);
                var named = Assert.IsType<FightFighterInformationCaseNamedFighter>(copy.FighterCase).Value;
                Assert.Equal("Copied", named.Name);
                var identity = Assert.IsType<FightNamedFighterInformationCharacterCaseCharacter>(named.CharacterCase).Value;
                Assert.Equal(4, identity.BreedId);
                Assert.Equal(Gender.Female, identity.Gender);
                Assert.Equal(player.Level, identity.Level);
            }
            Assert.Equal(enemy.MaxHP, enemy.LifeTopTold);

            var bars = board.OfType<FightActorContextEvent>().ToArray();
            Assert.Equal(new[] { player.Id, ally.Id, summon.Id }, bars.Select(bar => bar.ActorId));
            Assert.All(bars, bar => Assert.Equal(player.Id, bar.ContextId));
            Assert.Contains(new SpellItem(1, 370, SpellType.Unknown1), bars[0].Spells);
            Assert.Contains(new SpellItem(1, 0, SpellType.Unknown2), bars[1].Spells);
            Assert.Contains(new SpellItem(1, 755, SpellType.Unknown1), bars[1].Spells);
            Assert.Equal(new SpellItem(2, 754, SpellType.Unknown6), Assert.Single(bars[2].Spells));
            var cooldowns = board.OfType<FightSpellCooldownEvent>().ToArray();
            Assert.Equal(new[] { ally.Id, summon.Id, player.Id }, cooldowns.Select(c => c.CharacterId));
            Assert.Equal(new FightSpellCooldown(755, 1), Assert.Single(cooldowns[0].Cooldowns));
            Assert.Equal(new FightSpellCooldown(754, 2), Assert.Single(cooldowns[1].Cooldowns));
            Assert.Equal(new FightSpellCooldown(370, 3), Assert.Single(cooldowns[2].Cooldowns));

            var turn = Assert.Single(board.OfType<FightTurnEvent>());
            Assert.Equal(announced.FighterId, turn.CharacterId);
            Assert.Equal(announced.Deciseconds, turn.WaitDeciseconds);
            Assert.Equal(announced.Carried, turn.RemainingDeciseconds);
            Assert.InRange(turn.TurnTimeOverrideDeciseconds!.Value, announced.RemainingDeciseconds(DateTime.UtcNow), before);
            Assert.Equal(announced, fight.LastAnnouncedTurn);
            Assert.False(fight.TurnAwaitingConfirmation);
            var challenge = Assert.Single(Assert.Single(board.OfType<ChallengeListEvent>()).Challenges);
            Assert.Equal(ChallengeWatcher.AsesinoASueldo, challenge.ChallengeId);
            Assert.Equal(70, challenge.DropBonus);
            Assert.Equal(new FightNewRoundEvent(0, fight.RoundNumber), Assert.Single(board.OfType<FightNewRoundEvent>()));
            Assert.Equal(new FightOptionUpdateEvent(Team.Challenger, (FightOption)FightInstance.OptionSecret, true, FightId),
                Assert.Single(board.OfType<FightOptionUpdateEvent>()));
            var timeline = Assert.Single(board.OfType<FightTurnListEvent>()).Turns;
            Assert.Equal(fight.TurnOrder.Select(f => f.Id).Append(otherSummon.Id),
                timeline.Select(entry => Assert.IsType<FightTurnEntityCaseWithFlag>(entry).Value.EntityId));
            var effects = Assert.Single(board.OfType<FightEffectsRefreshEvent>()).Effects;
            Assert.Equal(3, effects.Count);
            var effect = Assert.Single(effects, row => row.ActionId == 950);
            Assert.Equal(enemy.Id, effect.SourceId);
            Assert.Equal(player.Id, effect.Effect.TargetId);
            Assert.Equal(3793, effect.Effect.Param3);
            Assert.Equal(new FightTurn(-1, 0), effect.Effect.EndTurn);
            var damageTaken = Assert.Single(effects, row => row.ActionId == Buffs.DanoSufridoPorCiento);
            Assert.Equal(enemy.Id, damageTaken.Effect.TargetId);
            Assert.Equal(104, damageTaken.Effect.Param1);
            Assert.Equal(0, damageTaken.Effect.Param3);
            var erosion = Assert.Single(effects, row => row.ActionId == 776).Effect;
            Assert.Equal((4, 0, 0), (erosion.Param1, erosion.Param2, erosion.Param3));
            var map = Assert.Single(board.OfType<FightMapInformation>());
            Assert.Equal(Arena, map.MapId);
            Assert.IsType<FightMapInformationPositionCaseCoords>(map.PositionCase);

            await returning.Request(new ContextReadyRequest(Arena));
            await returning.Request(new FightPingsRequest());
            await returning.Request(new MapInformationRequest(Arena));
            Assert.Equal(board.Length, returning.Frames.Count);
        }
        finally { Active.TryRemove(FightId, out _); }
    }

    [Fact]
    public async Task Reconnect_waiting_for_the_first_turn_sends_surrender_state_then_the_prompt()
    {
        using var returning = new Client(PlayerId, reconnecting: true);
        var fight = new FightInstance(FightId, Roleplay, Arena) { Reglas = FightRules.Koliseo, KoliseoMode = 2 };
        fight.AddPlayer(Person(PlayerId, 200));
        fight.AddOpponent(Person(PlayerId + 1, 300));
        fight.StartFight();
        Assert.True(Active.TryAdd(FightId, fight));
        try
        {
            await returning.Request(new ContextReadyRequest(Arena));

            var board = returning.Frames.Select(frame => frame.Content).ToArray();
            Assert.Equal(new FightJoinEvent(true, true, 0, FightType.PvpArena), Assert.Single(board.OfType<FightJoinEvent>()));
            Assert.DoesNotContain(board, message => message is FightTurnEvent or FightTurnStartPlayingEvent);
            Assert.Equal(new SurrenderStateEvent(true, true), board[^2]);
            Assert.IsType<FightIsTurnReadyEvent>(board[^1]);
            Assert.True(fight.TurnAwaitingConfirmation);
        }
        finally { Active.TryRemove(FightId, out _); }
    }

    [Fact]
    public async Task Placement_reconnect_consumes_only_the_placement_board()
    {
        using var returning = new Client(PlayerId, reconnecting: true);
        var fight = new FightInstance(FightId, Roleplay, Arena) { Reglas = FightRules.Desafio };
        fight.AddPlayer(Person(PlayerId, 200));
        fight.AddOpponent(Person(PlayerId + 1, 300));
        Assert.True(Active.TryAdd(FightId, fight));
        try
        {
            await returning.Request(new FightPingsRequest());
            await returning.Request(new ContextReadyRequest(Roleplay));
            Assert.Empty(returning.Frames);
            Assert.True(returning.Session.State.FightRejoinPending);

            await returning.Request(new ContextReadyRequest(Arena));
            var board = returning.Frames.Select(frame => frame.Content).ToArray();
            Assert.Equal(new FightJoinEvent(false, true, 0, FightType.Challenge), Assert.Single(board.OfType<FightJoinEvent>()));
            Assert.IsType<AreaFightModifierUpdateEvent>(board[^1]);
            Assert.True(fight.HasPrepared(PlayerId));
            Assert.False(returning.Session.State.FightRejoinPending);

            fight.StartFight();
            await returning.Request(new ContextReadyRequest(Arena));
            Assert.Equal(board.Length, returning.Frames.Count);
        }
        finally { Active.TryRemove(FightId, out _); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Declining_reentry_restores_roleplay_and_only_remaining_fighters_see_the_death_sequence(
        bool ongoing, bool endsFight)
    {
        var sent = new List<(long Recipient, GameFrame Frame)>();
        void Record(long id, GameFrame frame) => sent.Add((id, frame));
        using var leaving = new Client(PlayerId, reconnecting: false, inWorld: false, record: Record);
        using var first = new Client(PlayerId + 1, reconnecting: false, record: Record);
        using var second = new Client(PlayerId + 2, reconnecting: false, record: Record);
        using var watcher = new Client(PlayerId + 3, reconnecting: false, record: Record);
        watcher.Session.State.IsInFight = false;
        watcher.Session.State.FightId = 0;
        watcher.Session.State.MapId = Roleplay;
        // A character being selected has loaded their saved stats but has not entered the world.
        leaving.Session.State.IsInFight = false;
        leaving.Session.State.FightId = 0;

        var fight = new FightInstance(FightId, Roleplay, Arena) { Reglas = FightRules.Desafio };
        var quitter = Person(PlayerId, 200);
        var author = Person(first.Session.CharacterId, 300);
        author.Initiative = 1000;
        var peer = Person(second.Session.CharacterId, 301);
        var monster = new Fighter { Id = -2, IsMonster = true, CellId = 302, CurrentHP = 50, MaxHP = 50 };
        fight.AddPlayer(quitter);
        fight.AddOpponent(author);
        if (endsFight) fight.AddOpponent(peer);
        else fight.AddPlayer(peer);
        fight.AddMonster(monster);
        fight.DeDondeVenian[quitter.Id] = (Roleplay, 72);
        if (ongoing) fight.StartFight();
        var turnAuthor = fight.CurrentFighter?.Id;
        var survivingOrder = fight.TurnOrder.Where(f => f != quitter).Select(f => f.Id).ToArray();

        Assert.True(Active.TryAdd(FightId, fight));
        try
        {
            if (ongoing)
            {
                Assert.NotEqual(quitter.Id, turnAuthor);
                using var watchingTurn = await watcher.Session.EnterTurnAsync();
                using var watching = SessionContext.Push(watcher.Session);
                await FightHandler.WatchCharacterAsync(watcher.Session, new(author.Id));
                Assert.Contains(watcher.Session, FightHandler.SpectatorsOf(fight));
                watcher.Frames.Clear();
                sent.Clear();
            }

            using var turn = await leaving.Session.EnterTurnAsync();
            using var context = SessionContext.Push(leaving.Session);
            await new GameEntryBackend().AbandonFightAsync(quitter.Id);

            Assert.Same(leaving.Session, SessionContext.Actual);
            Assert.Equal(Roleplay, leaving.Session.MapId);
            Assert.Equal(72, leaving.Session.State.CellId);
            Assert.False(leaving.Session.State.IsInFight);
            Assert.Equal(0, leaving.Session.State.FightId);
            Assert.Equal(0, leaving.Session.State.RoleplayMapId);
            Assert.False(quitter.IsAlive);
            Assert.Null(FightHandler.FightToRejoin(quitter.Id));
            Assert.All(sent, row => Assert.Null(row.Frame.Uid));
            Assert.Equal(endsFight ? FightInstance.WaitsForTheJwz : 0, fight.FinPendiente);

            if (!ongoing)
            {
                Assert.Equal(FightState.Placement, fight.State);
                Assert.Empty(sent);
                return;
            }

            var bursts = sent.Where(row => row.Frame.Content is not FightIsTurnReadyEvent).ToArray();
            Assert.Equal(8, bursts.Length);
            int? lastAction = null;
            foreach (var burst in bursts.Chunk(4))
            {
                long recipient = burst[0].Recipient;
                Assert.Contains(recipient, new[] { first.Session.CharacterId, second.Session.CharacterId });
                Assert.All(burst, row => Assert.Equal(recipient, row.Recipient));
                Assert.Collection(burst.Select(row => row.Frame.Content),
                    start => Assert.Equal(new SequenceStartEvent(SequenceType.CharacterDeath), start),
                    death => Assert.Equal(new GameActionFightEvent(quitter.Id, 103,
                        new FightActionCaseDeath(new FightActionDeath(quitter.Id))), death),
                    update => Assert.Equal(survivingOrder, Assert.IsType<FightTurnListEvent>(update).Turns.Select(
                        entry => Assert.IsType<FightTurnEntityCaseWithFlag>(entry).Value.EntityId)),
                    end =>
                    {
                        var closure = Assert.IsType<SequenceEndEvent>(end);
                        Assert.Equal(turnAuthor, closure.AuthorId);
                        if (lastAction is int previous) Assert.Equal(previous + 1, closure.ActionId);
                        lastAction = closure.ActionId;
                    });
            }
            Assert.NotEqual(bursts[0].Recipient, bursts[4].Recipient);
            if (endsFight)
            {
                // The terminal prompt uses the established broadcast audience, unlike the direct sequence.
                Assert.Equal(new[] { quitter.Id, author.Id, peer.Id, watcher.Session.CharacterId }.Order(),
                    sent.Skip(8).Select(row => row.Recipient).Order());
                Assert.All(sent.Skip(8), row => Assert.IsType<FightIsTurnReadyEvent>(row.Frame.Content));
            }
            else Assert.Equal(bursts, sent);
        }
        finally
        {
            Active.TryRemove(FightId, out _);
            watcher.Session.State.SpectatingFightId = 0;
            FightHandler.SpectatorsOf(fight); // Prune the watcher from the shared index.
        }
    }

    [Fact]
    public void What_is_left_of_a_turn_is_counted_in_tenths()
    {
        var started = new DateTime(2026, 8, 9, 21, 0, 0, DateTimeKind.Utc);
        var turn = new FightInstance.AnnouncedTurn(PlayerId, 0, 1, 350, started);
        Assert.Equal(132, turn.RemainingDeciseconds(started.AddMilliseconds(21800)));
        Assert.Equal(350, turn.RemainingDeciseconds(started));
        Assert.Equal(0, turn.RemainingDeciseconds(started.AddSeconds(60)));
        Assert.False(default(FightInstance.AnnouncedTurn).Announced);
    }

    [Fact]
    public void A_turn_waits_for_every_clients_confirmation()
    {
        var fight = new FightInstance(1, 100, 100);
        long[] awaited = [10, 20];
        Assert.False(fight.ConfirmTurn(1, 0, -1, 10, awaited));
        Assert.False(fight.ConfirmTurn(1, 0, -1, 10, awaited));
        Assert.True(fight.ConfirmTurn(1, 0, -1, 20, awaited));
        Assert.False(fight.ConfirmTurn(1, 1, 10, 20, awaited));
        Assert.True(fight.ConfirmTurn(1, 1, 10, 10, awaited));
        Assert.True(fight.ConfirmTurn(1, 2, 20, 20, [20]));
    }

    private static Fighter Person(long id, int cell) => new()
    {
        Id = id, CellId = cell, Breed = 1, MaxHP = 500, CurrentHP = 425, Level = 50, MaxAP = 6, MaxMP = 3,
    };

    private sealed class Client : IDisposable
    {
        private readonly GameEntryService _entry;
        public List<GameFrame> Frames { get; } = [];
        public GameSession Session { get; }

        public Client(long id, bool reconnecting, bool inWorld = true, Action<long, GameFrame>? record = null)
        {
            Session = new(new CombatRecordingCodec(frame =>
            {
                Frames.Add(frame);
                record?.Invoke(id, frame);
            }), CombatRecordingCodec.RequireTypedPacketAsync);
            Session.BindAccount(id + 1_000_000_000, 1);
            Session.State.CharacterId = id;
            Session.State.CharacterLevel = 50;
            Session.State.MapId = Arena;
            Session.State.RoleplayMapId = Roleplay;
            Session.State.IsInFight = true;
            Session.State.FightId = FightId;
            Session.State.FightRejoinPending = reconnecting;
            if (inWorld) Session.EnterWorld();
            _entry = new GameEntryService(Session, new GameEntryBackend());
            Assert.True(SessionRegistry.Register(Session));
        }

        public async Task Request(GameRequest request)
        {
            using var turn = await Session.EnterTurnAsync();
            Assert.Equal(GameEntryResult.Handled, await GameNodeProxy.DispatchTypedAsync(Session, new(request, -43), _entry));
        }

        public void Dispose() => SessionRegistry.Unregister(Session);
    }
}

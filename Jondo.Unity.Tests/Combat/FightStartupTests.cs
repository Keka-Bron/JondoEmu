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

public sealed class FightStartupTests
{
    private const int FightId = 7_300_901;
    private const long Arena = 990_300_901;
    private static readonly ConcurrentDictionary<long, FightInstance> Active =
        (ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
            .GetField("_activeFights", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    [Fact]
    public async Task Each_opening_burst_is_private_and_both_finish_before_the_shared_prompt()
    {
        var sent = new List<(long Recipient, GameEvent Event)>();
        using var first = new Client(9_300_901, sent);
        using var second = new Client(9_300_902, sent);
        using var outsider = new Client(9_300_903, sent, inFight: false);
        var fight = new FightInstance(FightId, Arena - 1, Arena) { KoliseoMode = 2 };
        var player = Player(first, 200, 1000);
        player.GrantedSpells.Add((370, 1));
        player.Recarga[370] = 3;
        player.EverySpell[SpellAspect.AlcanceMaximo] = 1;
        var ally = new Fighter { Id = -2, CellId = 210, MaxHP = 60, CurrentHP = 60,
            IsMonster = true, IsPlayedAlly = true, DrivenBy = player.Id, HechizosDeInvocado = [(754, 2)] };
        ally.Recarga[754] = 2;
        var enemy = new Fighter { Id = -1, CellId = 300, MaxHP = 150, CurrentHP = 99, IsMonster = true, Level = 10 };
        fight.AddPlayer(player);
        fight.AddPlayer(Player(second, 201, 500));
        fight.AddPlayer(ally);
        fight.AddMonster(enemy);
        fight.ChallengesFixed.Add((ChallengeWatcher.AsesinoASueldo, 70));
        fight.ChallengeTargets[ChallengeWatcher.AsesinoASueldo] = enemy.Id;
        Assert.True(Active.TryAdd(FightId, fight));
        try
        {
            using var ambient = SessionContext.Push(outsider.Session);
            await FightHandler.InTurnAsync(fight, () => FightHandler.StartFightAsync(fight));
            await FightHandler.InTurnAsync(fight, () => FightHandler.StartFightAsync(fight));

            Assert.Same(outsider.Session, SessionContext.Actual);
            Assert.Equal(FightState.Ongoing, fight.State);
            Assert.True(fight.TurnAwaitingConfirmation);
            Assert.Equal(enemy.MaxHP, enemy.LifeTopTold);
            Assert.DoesNotContain(sent, item => item.Recipient == outsider.Session.CharacterId);

            var firstBurst = Burst(first);
            var secondBurst = Burst(second);
            Assert.Equal(firstBurst.Concat(secondBurst), sent.Where(item => item.Event is not FightIsTurnReadyEvent)
                .SkipWhile(item => item.Event is not FightStartEvent).Select(item => item.Event));
            Assert.Equal(new[] { first.Session.CharacterId, second.Session.CharacterId },
                sent.TakeLast(2).Where(item => item.Event is FightIsTurnReadyEvent).Select(item => item.Recipient));

            Assert.Equal(new FightNewRoundEvent(0, 1), firstBurst[3]);
            Assert.IsType<ChallengeListEvent>(firstBurst[1]);
            Assert.IsType<FightActorContextEvent>(firstBurst[2]);
            const int cooldownIndex = 5;
            Assert.IsType<ChallengeTargetsEvent>(firstBurst[cooldownIndex - 1]);
            foreach (var burst in new[] { firstBurst, secondBurst })
            {
                var challenge = Assert.Single(burst.OfType<ChallengeTargetsEvent>()).Challenge!;
                Assert.Equal(ChallengeWatcher.AsesinoASueldo, challenge.ChallengeId);
                var target = Assert.Single(challenge.Targets);
                Assert.Equal(enemy.CellId, target.CellId);
                Assert.Equal(enemy.Id, target.FighterId);
                Assert.Empty(target.AttackersId);
            }
            Assert.IsType<FightSpellCooldownEvent>(firstBurst[cooldownIndex]);
            Assert.IsType<FightSpellCooldownEvent>(firstBurst[cooldownIndex + 1]);
            Assert.IsType<FightActorContextEvent>(firstBurst[cooldownIndex + 2]);
            Assert.IsType<SequenceStartEvent>(firstBurst[cooldownIndex + 3]);
            var bars = firstBurst.OfType<FightActorContextEvent>().ToArray();
            Assert.Equal(new SpellItem(1, 0, SpellType.Unknown2), bars[0].Spells[0]);
            var weapon = Assert.Single(bars[0].Shortcuts, s => s.SlotId == 0);
            Assert.Equal(0, Assert.IsType<ShortcutCaseShortcutSpell>(weapon.ShortcutCase).Value.SpellId);
            Assert.Contains(new SpellItem(1, 370, SpellType.Unknown1), bars[0].Spells);
            Assert.Equal(ally.Id, bars[1].ActorId);
            Assert.Equal(player.Id, bars[1].ContextId);
            Assert.Equal(new SpellItem(2, 754, SpellType.Unknown6), Assert.Single(bars[1].Spells));
            Assert.Equal(second.Session.CharacterId, Assert.Single(secondBurst.OfType<FightActorContextEvent>()).ActorId);
            Assert.Equal(new[] { player.Id, ally.Id }, firstBurst.OfType<FightSpellCooldownEvent>().Select(c => c.CharacterId));
            Assert.Contains(new FightSpellCooldown(370, player.Recarga[370]), firstBurst.OfType<FightSpellCooldownEvent>().First().Cooldowns);
            Assert.Equal(new FightSpellCooldown(754, 2), Assert.Single(firstBurst.OfType<FightSpellCooldownEvent>().Last().Cooldowns));
            Assert.Equal(second.Session.CharacterId, Assert.Single(secondBurst.OfType<FightSpellCooldownEvent>()).CharacterId);
            Assert.Contains(new ApplySpellModifierEvent(new(0, SpellModifierActionType.ActionBoost, 1,
                SpellModifierType.RangeMax, 370), player.Id), firstBurst);
            Assert.Empty(secondBurst.OfType<ApplySpellModifierEvent>());

            foreach (var (client, burst) in new[] { (first, firstBurst), (second, secondBurst) })
            {
                int index = Array.FindIndex(burst, message => message is SequenceStartEvent);
                Assert.Equal(new SequenceStartEvent(SequenceType.FightStart), burst[index]);
                var roster = Assert.IsType<FightSynchronizeEvent>(burst[index + 1]);
                Assert.Equal(fight.TurnOrder.Select(f => f.Id), roster.Fighters.Select(f => f.ActorId));
                Assert.Equal(client.Session.CharacterId, Assert.IsType<SequenceEndEvent>(burst[index + 2]).AuthorId);
                Assert.Equal(new SurrenderStateEvent(true, true), burst[^1]);
                Assert.All(burst[(index + 3)..^1], message => Assert.IsType<ApplySpellModifierEvent>(message));
            }
            Assert.NotEqual(Assert.Single(firstBurst.OfType<SequenceEndEvent>()).ActionId,
                Assert.Single(secondBurst.OfType<SequenceEndEvent>()).ActionId);

            GameEvent[] Burst(Client client) => sent.Where(item => item.Recipient == client.Session.CharacterId)
                .Select(item => item.Event).SkipWhile(message => message is not FightStartEvent)
                .TakeWhile(message => message is not FightIsTurnReadyEvent).ToArray();
        }
        finally { Active.TryRemove(FightId, out _); }
    }

    [Fact]
    public async Task Typed_confirmation_waits_for_both_players_and_gives_control_only_to_the_active_player()
    {
        var sent = new List<(long Recipient, GameEvent Event)>();
        using var first = new Client(9_300_901, sent);
        using var second = new Client(9_300_902, sent);
        using var outsider = new Client(9_300_903, sent, inFight: false);
        var fight = new FightInstance(FightId, Arena - 1, Arena) { Reglas = FightRules.Desafio };
        fight.AddPlayer(Player(first, 200, 1000));
        fight.AddOpponent(Player(second, 300, 500));
        fight.StartFight();
        Assert.True(Active.TryAdd(FightId, fight));
        try
        {
            await outsider.Request(new FightTurnReadyRequest(true));
            await first.Request(new GameActionAcknowledgementRequest(true, 1));
            await first.Request(new FightTurnReadyRequest(true));
            await first.Request(new FightTurnReadyRequest(true));
            Assert.True(fight.TurnAwaitingConfirmation);
            Assert.Empty(sent);

            await second.Request(new FightTurnReadyRequest(true));
            await second.Request(new FightTurnReadyRequest(true));
            Assert.False(fight.TurnAwaitingConfirmation);
            Assert.Equal(first.Session.CharacterId, fight.LastAnnouncedTurn.FighterId);
            var turns = sent.Where(item => item.Event is FightTurnEvent).ToArray();
            Assert.Equal(new[] { first.Session.CharacterId, second.Session.CharacterId }, turns.Select(item => item.Recipient));
            var turn = Assert.IsType<FightTurnEvent>(turns[0].Event);
            Assert.Equal(first.Session.CharacterId, turn.CharacterId);
            Assert.Equal(fight.LastAnnouncedTurn.Deciseconds, turn.WaitDeciseconds);
            Assert.Equal(first.Session.CharacterId, Assert.Single(sent, item => item.Event is FightTurnStartPlayingEvent).Recipient);
        }
        finally
        {
            fight.CancelTurnTimer();
            Active.TryRemove(FightId, out _);
        }
    }

    private static Fighter Player(Client client, int cell, int initiative) => new()
    {
        Id = client.Session.CharacterId, CellId = cell, Initiative = initiative, Level = 50,
        MaxHP = 500, CurrentHP = 425, MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3,
    };

    private sealed class Client : IDisposable
    {
        public GameSession Session { get; }

        public Client(long id, List<(long Recipient, GameEvent Event)> sent, bool inFight = true)
        {
            // Startup and confirmation can also run legacy combat effects; observe only the typed messages here.
            Session = new(new CombatRecordingCodec(frame => sent.Add((id, Assert.IsAssignableFrom<GameEvent>(frame.Content)))),
                _ => Task.CompletedTask);
            Session.BindAccount(id + 1_000_000_000, 1);
            Session.State.CharacterId = id;
            Session.State.CharacterLevel = 50;
            Session.State.MapId = inFight ? Arena : Arena + 1;
            Session.State.IsInFight = inFight;
            Session.State.FightId = inFight ? FightId : 0;
            Session.EnterWorld();
            Assert.True(SessionRegistry.Register(Session));
        }

        public async Task Request(GameRequest request)
        {
            using var turn = await Session.EnterTurnAsync();
            Assert.Equal(GameEntryResult.Handled, await GameNodeProxy.DispatchTypedAsync(Session,
                new(request, -42), new GameEntryService(Session, new GameEntryBackend())));
        }

        public void Dispose() => SessionRegistry.Unregister(Session);
    }
}

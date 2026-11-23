using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat;

public sealed partial class MapFightPresenceTests
{
    [Fact]
    public async Task Party_switches_are_private_correlated_responses_on_the_active_profile()
    {
        var codec = new GameCodec();
        await using var pipe = await ClientPipe.OpenAsync(Person + 1_000_000_000, Person, Map);
        using var peer = new Recipient(Person + 1, Map);
        (GameRequest Request, GameResponse Response, bool Join, bool Ready, int Uid)[] steps = [
            (new FightAutoJoinActivationRequest(), new FightAutoJoinActivationResponse(), true, false, 0),
            (new FightAutoReadyActivationRequest(), new FightAutoReadyActivationResponse(), true, true, -17),
            (new FightAutoJoinDeactivationRequest(), new FightAutoJoinDeactivationResponse(), false, true, 42),
            (new FightAutoReadyDeactivationRequest(), new FightAutoReadyDeactivationResponse(), false, false, 1)];
        try
        {
            foreach (var step in steps)
            {
                using var turn = await pipe.Session.EnterTurnAsync();
                var request = GameNodeProxy.DecodeRequest(pipe.Session, codec.Encode(new(step.Request, step.Uid)));
                Assert.Equal(GameEntryResult.Handled, await GameNodeProxy.DispatchTypedAsync(pipe.Session, request,
                    new GameEntryService(pipe.Session, new GameEntryBackend())));
                var answer = codec.Decode(await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(new GameFrame(step.Response, step.Uid), answer);
                Assert.Equal(step.Join, FightHandler.AutoJoinOn(Person));
                Assert.Equal(step.Ready, FightHandler.AutoReadyOn(Person));
            }
            Assert.Empty(peer.Sequence);
        }
        finally { FightHandler.SetAutoOptions(Person, false, false); }
    }

    [Fact]
    public async Task Party_restriction_then_last_slot_contention_place_only_one_joiner()
    {
        using var fixture = new FightFixture();
        var fight = fixture.Fight;
        fight.BluePlacementCells.Add(314);
        fight.PeopleCap = 2;
        using var leader = new Recipient(Person, Arena, Id);
        leader.Session.State.IsInFight = true;
        using var one = new Recipient(Person + 1, Map);
        using var two = new Recipient(Person + 2, Map);
        var join = new FightJoinRequest(Person, (int)Id);
        var restrict = new FightOptionToggleRequest(FightOption.Unknown1);
        await DispatchPlacement(leader.Session, restrict);
        await DispatchPlacement(one.Session, join);
        Assert.Equal(new FightChallengeJoinRefuseEvent(FighterRefusedReason.TeamLimitedByMainCharacter), one.Sequence.Last());
        Assert.DoesNotContain(two.Sequence, e => e is FightChallengeJoinRefuseEvent);
        Assert.False(one.Session.State.IsInFight);
        await DispatchPlacement(leader.Session, restrict);

        await fight.Turno.WaitAsync();
        Task first, second;
        try
        {
            first = DispatchPlacement(one.Session, join);
            second = DispatchPlacement(two.Session, join);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally { fight.Turno.Release(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        var winner = Assert.Single(new[] { one, two }, p => p.Session.State.IsInFight);
        Assert.Equal(2, fight.PeopleIn(FightInstance.Azules));
        Assert.Equal(314, fight.Buscar(winner.Session.CharacterId).CellId);
        var placed = Assert.Single(leader.Sequence.OfType<EntitiesDispositionEvent>());
        Assert.Equal(new long?[] { -1, -2, winner.Session.CharacterId }, placed.Added.Select(p => p.EntityId));
        Assert.Single(leader.Sequence.OfType<FightTurnListEvent>());
        await DispatchPlacement(winner.Session, restrict); // The joiner does not control the leader's switch.
        Assert.False(fight.OptionOn(FightInstance.Azules, FightInstance.OptionPartyOnly));
        await DispatchPlacement(winner.Session, new ContextQuitRequest());
        Assert.False(winner.Session.State.IsInFight);
        Assert.Equal(FightState.Placement, fight.State);
        Assert.Equal(new FightLeaveEvent(winner.Session.CharacterId), Assert.Single(leader.Sequence.OfType<FightLeaveEvent>()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Waiting_quit_or_option_cannot_affect_a_replacement_duel(bool quit)
    {
        using var original = new FightFixture();
        using var player = new Recipient(Person, Arena, Id);
        player.Session.State.IsInFight = true;
        await original.Fight.Turno.WaitAsync();
        Task queued;
        FightFixture next;
        try
        {
            queued = DispatchPlacement(player.Session, quit ? new ContextQuitRequest() : new FightOptionToggleRequest(FightOption.Unknown1));
            Assert.False(queued.IsCompleted);
            original.Unregister();
            next = new FightFixture(); // Same player and fight ID; only the instance changed.
        }
        finally { original.Fight.Turno.Release(); }
        using (next)
        {
            await queued.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(FightState.Placement, next.Fight.State);
            Assert.True(player.Session.State.IsInFight);
            Assert.False(next.Fight.OptionOn(FightInstance.Azules, FightInstance.OptionPartyOnly));
            Assert.Empty(player.Sequence);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Waiting_join_cannot_enter_an_ended_or_replaced_fight(bool replace)
    {
        using var original = new FightFixture();
        var fight = original.Fight;
        fight.BluePlacementCells.Add(314);
        using var joiner = new Recipient(Person + 1, Map);
        await fight.Turno.WaitAsync();
        Task queued;
        FightFixture? next = null;
        try
        {
            queued = DispatchPlacement(joiner.Session, new FightJoinRequest(Person, (int)Id));
            Assert.False(queued.IsCompleted);
            if (replace)
            {
                original.Unregister();
                next = new FightFixture();
                next.Fight.BluePlacementCells.Add(314);
            }
            else Assert.True(fight.LeavePlacement(Person));
        }
        finally { fight.Turno.Release(); }
        using (next)
        {
            await queued.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(fight.Buscar(joiner.Session.CharacterId));
            Assert.Null(next?.Fight.Buscar(joiner.Session.CharacterId));
            Assert.Empty(joiner.Sequence);
        }
    }
}

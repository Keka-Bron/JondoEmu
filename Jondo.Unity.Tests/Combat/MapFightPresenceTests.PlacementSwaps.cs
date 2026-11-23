using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat;

public sealed partial class MapFightPresenceTests
{
    private sealed class SwapBoard : IDisposable
    {
        private readonly FightFixture _fixture = new();
        internal FightInstance Fight => _fixture.Fight;
        internal Recipient Requester { get; } = new(Person, Arena, Id);
        internal Recipient Target { get; } = new(Person + 1, Arena, Id);
        internal Recipient Other { get; } = new(Person + 2, Arena, Id);

        internal SwapBoard()
        {
            Fight.BluePlacementCells.AddRange([300, 314]);
            Fight.Buscar(Person).CellId = 300;
            Fight.Azul.Add(new() { Id = Person + 1, CurrentHP = 100, CellId = 314 });
            Fight.Rojo.Add(new() { Id = Person + 2, CurrentHP = 100, CellId = 100, TeamId = 1 });
            foreach (var peer in new[] { Requester, Target, Other }) peer.Session.State.IsInFight = true;
        }

        internal async Task<int> Offer(long entity = Person + 1)
        {
            await Send(Requester, new FightPlacementPositionRequest(entity, 314));
            var offer = Assert.IsType<FightPlacementSwapPositionsOfferEvent>(Assert.Single(Requester.Sequence));
            Assert.Equal(new FightPlacementSwapPositionsOfferEvent(Person + 1, offer.RequestId, Person), Assert.Single(Target.Sequence));
            Assert.Empty(Other.Sequence);
            return offer.RequestId;
        }

        internal (int, int) Cells => (Fight.Buscar(Person).CellId, Fight.Buscar(Person + 1).CellId);

        public void Dispose()
        {
            Other.Dispose();
            Target.Dispose();
            Requester.Dispose();
            _fixture.Dispose();
        }
    }

    private static async Task Send(Recipient from, GameRequest request)
        => Assert.Equal(GameEntryResult.Handled, await DispatchPlacement(from.Session, request));

    [Fact]
    public async Task Placement_swap_survives_a_third_fighters_answers_and_is_accepted_once()
    {
        using var board = new SwapBoard();
        int id = await board.Offer();
        await Send(board.Other, new FightPlacementSwapPositionsCancelRequest(id));
        await Send(board.Other, new FightPlacementSwapPositionsAcceptRequest(id));
        Assert.IsType<FightPlacementSwapPositionsErrorEvent>(Assert.Single(board.Other.Sequence));
        Assert.Single(board.Requester.Sequence);
        Assert.Single(board.Target.Sequence);
        Assert.Equal((300, 314), board.Cells);

        await Send(board.Target, new FightPlacementSwapPositionsAcceptRequest(id));
        Assert.Equal((314, 300), board.Cells);
        foreach (var peer in new[] { board.Requester, board.Target, board.Other })
        {
            var update = Assert.IsType<EntitiesDispositionEvent>(Assert.Single(peer.Sequence.Skip(1)));
            Assert.Contains(update.Added, p => p.EntityId == Person && p.CellId == 314);
            Assert.Contains(update.Added, p => p.EntityId == Person + 1 && p.CellId == 300);
        }
        await Send(board.Target, new FightPlacementSwapPositionsAcceptRequest(id));
        Assert.IsType<FightPlacementSwapPositionsErrorEvent>(Assert.Single(board.Target.Sequence.Skip(2)));
        Assert.Equal((314, 300), board.Cells);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Placement_swap_withdrawal_or_refusal_only_notifies_the_pair(bool withdraw)
    {
        using var board = new SwapBoard();
        int id = await board.Offer(Person); // Picking the occupied cell asks the same ally.
        var caller = withdraw ? board.Requester : board.Target;
        await Send(caller, new FightPlacementSwapPositionsCancelRequest(id));
        await Send(caller, new FightPlacementSwapPositionsCancelRequest(id));
        var cancelled = new FightPlacementSwapPositionsCancelledEvent(caller.Session.CharacterId, id);
        Assert.Equal(cancelled, Assert.Single(board.Requester.Sequence.Skip(1)));
        Assert.Equal(cancelled, Assert.Single(board.Target.Sequence.Skip(1)));
        await Send(board.Target, new FightPlacementSwapPositionsAcceptRequest(id));
        Assert.IsType<FightPlacementSwapPositionsErrorEvent>(Assert.Single(board.Target.Sequence.Skip(2)));
        Assert.Empty(board.Other.Sequence);
        Assert.Equal((300, 314), board.Cells);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Queued_placement_swap_answer_cannot_move_fighters_or_flush_an_end(bool accept, bool pendingEnd)
    {
        using var board = new SwapBoard();
        int id = await board.Offer();
        await board.Fight.Turno.WaitAsync();
        Task queued;
        try
        {
            queued = Send(board.Target, accept ? new FightPlacementSwapPositionsAcceptRequest(id) : new FightPlacementSwapPositionsCancelRequest(id));
            Assert.False(queued.IsCompleted);
            if (pendingEnd) board.Fight.FinPendiente = 1;
            else board.Fight.StartFight();
        }
        finally { board.Fight.Turno.Release(); }
        await queued.WaitAsync(TimeSpan.FromSeconds(5));
        if (accept) Assert.IsType<FightPlacementSwapPositionsErrorEvent>(Assert.Single(board.Target.Sequence.Skip(1)));
        else Assert.Single(board.Target.Sequence);
        Assert.Single(board.Requester.Sequence);
        Assert.Empty(board.Other.Sequence);
        Assert.Equal((300, 314), board.Cells);
        Assert.Equal(pendingEnd ? 1 : 0, board.Fight.FinPendiente);
    }

    [Fact]
    public async Task Placement_swap_with_a_bot_moves_both_immediately()
    {
        using var board = new SwapBoard();
        board.Fight.Buscar(Person + 1).IsBot = true;
        await Send(board.Requester, new FightPlacementPositionRequest(Person + 1, 314));
        Assert.IsType<EntitiesDispositionEvent>(Assert.Single(board.Requester.Sequence));
        Assert.Equal((314, 300), board.Cells);
    }
}

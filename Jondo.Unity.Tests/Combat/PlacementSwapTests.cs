using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jondo.Protocol;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Two allies swapping cells during the placement: the ally's «Cambiar de sitio» sends jzy with
    /// his id, both are told kad, and the one asked accepts (kap) or refuses (kav).
    /// </summary>
    public sealed class PlacementSwapTests
    {
        private const long Me = 990_000_051, Friend = 990_000_052, Enemy = 990_000_053;

        private static ConcurrentDictionary<long, FightInstance> Active
            => (ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
                .GetField("_activeFights", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        private static FightInstance NewFight(long id)
        {
            var fight = new FightInstance(id, 1);
            fight.BluePlacementCells.AddRange(new[] { 300, 314 });
            fight.RedPlacementCells.Add(100);
            fight.Azul.Add(new Fighter { Id = Me, CurrentHP = 100, TeamId = 0, CellId = 300 });
            fight.Azul.Add(new Fighter { Id = Friend, CurrentHP = 100, TeamId = 0, CellId = 314 });
            fight.Rojo.Add(new Fighter { Id = Enemy, CurrentHP = 100, TeamId = 1, CellId = 100 });
            Active[fight.FightId] = fight;
            return fight;
        }

        private static long Field(byte[] body, int number)
            => ProtoMessage.Parse(body).Fields.First(f => f.FieldNumber == number).VarIntValue;

        private static async Task Ask(ClientPipe pipe, long ally, int cell)
        {
            pipe.Session.State.IsInFight = true;
            using var turn = await pipe.Session.EnterTurnAsync();
            Assert.Equal(GameEntryResult.Handled, await GameNodeProxy.DispatchTypedAsync(pipe.Session,
                new(new FightPlacementPositionRequest(ally, cell), 0), new GameEntryService(pipe.Session, new GameEntryBackend())));
        }

        [Fact]
        public async Task The_ally_asked_accepts_and_both_change_places()
        {
            var fight = NewFight(7_100_051);
            await using var mine = await ClientPipe.OpenAsync(8_900_000_151, Me, 990_100_151, "Cambio-Uno");
            await using var theirs = await ClientPipe.OpenAsync(8_900_000_152, Friend, 990_100_151, "Cambio-Dos");
            await using var other = await ClientPipe.OpenAsync(8_900_000_153, Enemy, 990_100_151, "Cambio-Tres");
            try
            {
                using (SessionContext.Push(mine.Session)) await Ask(mine, Friend, 314);

                // kad { 2 the one asked, 3 the request, 5 who asks }, to the two of them only.
                byte[] offer = await theirs.Next(Op.Kad);
                Assert.Equal(offer, await mine.Next(Op.Kad));
                Assert.Equal(Friend, Field(offer, 2));
                Assert.Equal(Me, Field(offer, 5));
                int request = (int)Field(offer, 3);
                Assert.Equal(300, fight.Buscar(Me)!.CellId);

                using (SessionContext.Push(theirs.Session))
                    await FightHandler.AcceptSwapAsync(theirs.ToClient, ConnectionProtocol.Push(Op.Kap, Pb.New().Var(1, request).Build()));

                Assert.Equal(314, fight.Buscar(Me)!.CellId);
                Assert.Equal(300, fight.Buscar(Friend)!.CellId);
                // The enemy's first frame is the kmk: no kad reached him.
                var placed = ProtoMessage.Parse(await other.Next(Op.Kmk));
                Assert.Contains(placed.Fields, f => f.FieldNumber == 2
                    && Field(f.BytesValue, 1) == 314 && Field(f.BytesValue, 3) == Me);
                await mine.Next(Op.Kmk);
                await theirs.Next(Op.Kmk);

                // A request accepted is gone: accepting it again is the error.
                using (SessionContext.Push(theirs.Session))
                    await FightHandler.AcceptSwapAsync(theirs.ToClient, ConnectionProtocol.Push(Op.Kap, Pb.New().Var(1, request).Build()));
                await theirs.Next(Op.Jzz);
            }
            finally { Active.TryRemove(fight.FightId, out _); }
        }

        [Fact]
        public async Task A_refusal_reaches_who_asked_and_nobody_moves()
        {
            var fight = NewFight(7_100_052);
            await using var mine = await ClientPipe.OpenAsync(8_900_000_161, Me, 990_100_152, "Cambio-Uno");
            await using var theirs = await ClientPipe.OpenAsync(8_900_000_162, Friend, 990_100_152, "Cambio-Dos");
            try
            {
                // Picking the cell the ally stands on asks him the same.
                using (SessionContext.Push(mine.Session)) await Ask(mine, Me, 314);
                int request = (int)Field(await mine.Next(Op.Kad), 3);
                await theirs.Next(Op.Kad);

                using (SessionContext.Push(theirs.Session))
                    await FightHandler.RefuseSwapAsync(theirs.ToClient, ConnectionProtocol.Push(Op.Kav, Pb.New().Var(1, request).Build()));

                // kac { 1 who refused, 2 the request }.
                byte[] refused = Pb.New().Var(1, Friend).Var(2, request).Build();
                Assert.Equal(refused, await mine.Next(Op.Kac));
                Assert.Equal(refused, await theirs.Next(Op.Kac));
                Assert.Equal(300, fight.Buscar(Me)!.CellId);
                Assert.Equal(314, fight.Buscar(Friend)!.CellId);
            }
            finally { Active.TryRemove(fight.FightId, out _); }
        }

        [Fact]
        public async Task An_ally_nobody_plays_swaps_at_once()
        {
            var fight = NewFight(7_100_053);
            fight.Buscar(Friend)!.IsBot = true;
            await using var mine = await ClientPipe.OpenAsync(8_900_000_171, Me, 990_100_153, "Cambio-Uno");
            try
            {
                using (SessionContext.Push(mine.Session)) await Ask(mine, Friend, 314);
                await mine.Next(Op.Kmk);
                Assert.Equal(314, fight.Buscar(Me)!.CellId);
                Assert.Equal(300, fight.Buscar(Friend)!.CellId);
            }
            finally { Active.TryRemove(fight.FightId, out _); }
        }
    }
}

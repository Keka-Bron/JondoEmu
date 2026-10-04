using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Xunit;
using Effect = Jondo.Unity.Server.Managers.Equipment.ItemEffect;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// A trade between two players, against the two captures of Intercambio/: the one asking
    /// (302677754146) and the one asked (293213045026).
    /// </summary>
    [Collection("forgemagic")]
    public class TradeTests
    {
        private static byte[] Hex(string hex) => Convert.FromHexString(hex);
        private const long Asker = 302677754146, Asked = 293213045026;

        [Fact]
        public void Asking_and_opening_are_the_captures()
        {
            Assert.Equal(Hex("08a28280c8e70810a282f0a6c4082001"), TradeProtocol.BuildRequested(Asker, Asked));
            // The asker 25,738 pods of capacity and 16,677 carried, the asked 22,439 and 12,117.
            Assert.Equal(Hex("108ac901180120a7af0128a5820130a28280c8e70838a282f0a6c40840d55e"),
                         TradeProtocol.BuildStarted(Asker, 25738, 16677, Asked, 22439, 12117));
        }

        [Fact]
        public void Laying_down_and_kamas_are_the_captures()
        {
            // The other one's stack of 6902, one of it: the f2 says it is not ours.
            Assert.Equal(Hex("0a0f083f2a0b08f635180120b5cac8fd011001"),
                         TradeProtocol.BuildLaid(6902, new List<Effect>(), 1, 531768629, remote: true));
            Assert.Equal(Hex("0a0f083f2a0b08f735180120b4cac8fd01"),
                         TradeProtocol.BuildLaid(6903, new List<Effect>(), 1, 531768628, remote: false));
            Assert.Equal(Hex("088827"), TradeProtocol.BuildKamas(5000, remote: false));
            Assert.Equal(Hex("0888271801"), TradeProtocol.BuildKamas(5000, remote: true));
            Assert.Equal(Hex("180120a282f0a6c408"), WorkshopProtocol.BuildReady(true, Asked));
        }

        [Fact]
        public void The_end_is_the_captures()
        {
            Assert.Equal(Hex("1a0810ef94e2fc011803"), TradeProtocol.BuildQuantity(530090607, 3));
            Assert.Equal(Hex("20a28280c8e708"), WorkshopProtocol.BuildReady(false, Asker));
            Assert.Equal(Hex("088ac90118a48201"), TradeProtocol.BuildWeight(25738, 16676, remote: false));
            Assert.Equal(Hex("08a7af01100118d65e"), TradeProtocol.BuildWeight(22439, 12118, remote: true));
            Assert.Equal(Hex("0801180b"), TradeProtocol.BuildClosed(done: true));
            Assert.Equal(Hex("180b"), TradeProtocol.BuildClosed(done: false));
        }

        private static GameSession Player(long id, long kamas)
        {
            // The kamas are saved when they change hands, and a world.db fresh out of
            // datos/world.zip -- the CI's -- lacks the scroll columns the server adds at start.
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(Jondo.Unity.Server.DatabaseManager.WorldConnectionString))
            {
                connection.Open();
                Jondo.Unity.Server.DatabaseManager.MoveScrollsOutOfTheBase(connection);
            }

            var session = GameSession.SinSocket();
            session.BindAccount(id, 1);
            session.State.CharacterId = id;
            session.State.MapId = 191105026;
            session.State.Kamas = kamas;
            session.EnterWorld();
            Assert.True(SessionRegistry.Register(session));
            return session;
        }

        private static async Task As(GameSession session, Func<Task> action)
        {
            using (SessionContext.Push(session)) await action();
        }

        /// <summary>Asked, accepted, kamas on both sides, ready, and the kamas change hands.</summary>
        [Fact]
        public async Task A_trade_of_kamas_goes_through_when_both_are_ready()
        {
            var asker = Player(9_000_000_011, 10_000);
            var asked = Player(9_000_000_012, 3_000);
            try
            {
                await As(asker, () => TradeHandler.RequestAsync(null!, ConnectionProtocol.Push(Op.Keu,
                    Pb.New().Var(2, asked.CharacterId).Build())));
                var trade = asker.State.Trade;
                Assert.NotNull(trade);
                Assert.Same(trade, asked.State.Trade);
                Assert.False(trade!.Accepted);

                // The asker cannot accept his own request; the asked can.
                await As(asker, TradeHandler.AcceptAsync);
                Assert.False(trade.Accepted);
                await As(asked, TradeHandler.AcceptAsync);
                Assert.True(trade.Accepted);

                await As(asker, () => TradeHandler.KamasAsync(null!, ConnectionProtocol.Push(Op.Kee,
                    Pb.New().Var(1, 4_000).Build())));
                await As(asked, () => TradeHandler.KamasAsync(null!, ConnectionProtocol.Push(Op.Kee,
                    Pb.New().Var(1, 99_999).Build())));
                Assert.Equal(4_000, trade.KamasOf(asker.CharacterId));
                Assert.Equal(3_000, trade.KamasOf(asked.CharacterId));   // no more than one has

                byte[] ready = ConnectionProtocol.Push(Op.Kep, Pb.New().Var(1, 1).Var(2, 9).Build());
                await As(asker, () => TradeHandler.ReadyAsync(null!, ready));
                Assert.Contains(asker.CharacterId, trade.Ready);

                // A change on the table takes the "ready" back.
                await As(asked, () => TradeHandler.KamasAsync(null!, ConnectionProtocol.Push(Op.Kee,
                    Pb.New().Var(1, 2_000).Build())));
                Assert.Empty(trade.Ready);

                await As(asker, () => TradeHandler.ReadyAsync(null!, ready));
                await As(asked, () => TradeHandler.ReadyAsync(null!, ready));

                Assert.True(trade.Ended);
                Assert.Null(asker.State.Trade);
                Assert.Null(asked.State.Trade);
                Assert.Equal(10_000 - 4_000 + 2_000, asker.State.Kamas);
                Assert.Equal(3_000 - 2_000 + 4_000, asked.State.Kamas);
            }
            finally
            {
                SessionRegistry.Unregister(asker);
                SessionRegistry.Unregister(asked);
            }
        }

        /// <summary>Refusing ends it for both, and nothing moves.</summary>
        [Fact]
        public async Task Refusing_ends_it_for_both()
        {
            var asker = Player(9_000_000_013, 500);
            var asked = Player(9_000_000_014, 500);
            try
            {
                await As(asker, () => TradeHandler.RequestAsync(null!, ConnectionProtocol.Push(Op.Keu,
                    Pb.New().Var(2, asked.CharacterId).Build())));
                var trade = asker.State.Trade!;
                Assert.True(await CloseAs(asked));
                Assert.True(trade.Ended);
                Assert.Null(asker.State.Trade);
                Assert.Null(asked.State.Trade);
                Assert.Equal(500, asker.State.Kamas);

                // Someone busy in a trade cannot be asked again.
                await As(asker, () => TradeHandler.RequestAsync(null!, ConnectionProtocol.Push(Op.Keu,
                    Pb.New().Var(2, asked.CharacterId).Build())));
                var again = asker.State.Trade!;
                var third = Player(9_000_000_015, 0);
                try
                {
                    await As(third, () => TradeHandler.RequestAsync(null!, ConnectionProtocol.Push(Op.Keu,
                        Pb.New().Var(2, asked.CharacterId).Build())));
                    Assert.Null(third.State.Trade);
                    Assert.Same(again, asked.State.Trade);
                }
                finally { SessionRegistry.Unregister(third); }
            }
            finally
            {
                SessionRegistry.Unregister(asker);
                SessionRegistry.Unregister(asked);
            }
        }

        /// <summary>
        /// A stack changes hands: gone from the giver's bag, into the receiver's under a new uid,
        /// and part of a stack leaves the rest behind.
        /// </summary>
        [Fact]
        public async Task Items_change_hands_under_a_new_uid()
        {
            var asker = Player(9_000_000_016, 0);
            var asked = Player(9_000_000_017, 0);
            long whole = Jondo.Unity.Server.DatabaseManager.NextItemUid();
            long stack = Jondo.Unity.Server.DatabaseManager.NextItemUid();
            try
            {
                using (SessionContext.Push(asker))
                {
                    Assert.True(Jondo.Unity.Server.DatabaseManager.InsertCharacterItem(whole, asker.CharacterId, 6902, 1, Jondo.Unity.Server.Managers.Equipment.Bag, null));
                    Assert.True(Jondo.Unity.Server.DatabaseManager.InsertCharacterItem(stack, asker.CharacterId, 6903, 10, Jondo.Unity.Server.Managers.Equipment.Bag, null));
                    Jondo.Unity.Server.Managers.Equipment.Add(whole, 6902, 1, Jondo.Unity.Server.Managers.Equipment.Bag, null);
                    Jondo.Unity.Server.Managers.Equipment.Add(stack, 6903, 10, Jondo.Unity.Server.Managers.Equipment.Bag, null);
                }

                await As(asker, () => TradeHandler.RequestAsync(null!, ConnectionProtocol.Push(Op.Keu,
                    Pb.New().Var(2, asked.CharacterId).Build())));
                await As(asked, TradeHandler.AcceptAsync);
                await As(asker, () => TradeHandler.MoveAsync(null!, ConnectionProtocol.Push(Op.Kcr,
                    Pb.New().Var(1, 1).Var(2, whole).Build())));
                await As(asker, () => TradeHandler.MoveAsync(null!, ConnectionProtocol.Push(Op.Kcr,
                    Pb.New().Var(1, 4).Var(2, stack).Build())));

                byte[] ready = ConnectionProtocol.Push(Op.Kep, Pb.New().Var(1, 1).Var(2, 3).Build());
                await As(asker, () => TradeHandler.ReadyAsync(null!, ready));
                await As(asked, () => TradeHandler.ReadyAsync(null!, ready));

                using (SessionContext.Push(asker))
                {
                    Assert.Null(Jondo.Unity.Server.Managers.Equipment.ByUid(whole));
                    Assert.Equal(6, Jondo.Unity.Server.Managers.Equipment.ByUid(stack)!.Quantity);
                }
                using (SessionContext.Push(asked))
                {
                    var got = new List<Jondo.Unity.Server.Managers.Equipment.Item>(Jondo.Unity.Server.Managers.Equipment.All);
                    Assert.Equal(2, got.Count);
                    Assert.Contains(got, i => i.Template == 6902 && i.Quantity == 1 && i.Uid != whole);
                    Assert.Contains(got, i => i.Template == 6903 && i.Quantity == 4 && i.Uid != stack);
                    Assert.All(got, i => Assert.NotNull(Jondo.Unity.Server.Managers.HavenBagStore.FromInventory(asked.CharacterId, i.Uid)));
                }
                Assert.Null(Jondo.Unity.Server.Managers.HavenBagStore.FromInventory(asker.CharacterId, whole));
            }
            finally
            {
                foreach (var session in new[] { asker, asked })
                {
                    using (SessionContext.Push(session))
                        foreach (var item in new List<Jondo.Unity.Server.Managers.Equipment.Item>(Jondo.Unity.Server.Managers.Equipment.All))
                            Jondo.Unity.Server.DatabaseManager.DestroyCharacterItem(session.CharacterId, item.Uid, 0);
                    SessionRegistry.Unregister(session);
                }
            }
        }

        private static async Task<bool> CloseAs(GameSession session)
        {
            using (SessionContext.Push(session)) return await TradeHandler.CloseAsync();
        }
    }
}

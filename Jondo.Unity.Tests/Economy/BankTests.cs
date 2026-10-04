using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// The bank: its frames against "Interactivos varios/entrar en banco bonta-abrir cofre
    /// gremio-usarlo-abrir cofre personal del banco.pcapng" (and the bin in front of it), and its
    /// storage against world.db -- per account, shared, never below zero, safe from anywhere.
    /// </summary>
    [Collection("forgemagic")]
    public class BankTests
    {
        private static byte[] Hex(string hex) => Convert.FromHexString(hex);

        private static HavenBagStore.StoredItem Stack(int gid, int quantity, long uid)
            => new HavenBagStore.StoredItem { Gid = gid, Quantity = quantity, Uid = uid, Effects = "" };

        // ─── The frames ─────────────────────────────────────────────────────────

        [Fact]
        public void The_banker_s_greeting_is_frame_74()
        {
            var effects = new Dictionary<long, IReadOnlyList<long>> { [63535] = BankHandler.ConsultReplyEffects };
            Assert.Equal(Hex("08c2f502120908aff0031a0308c401120408b0f0031a0431333937"),
                         BankProtocol.BuildQuestion(47810, new long[] { 63535, 63536 }, effects, new[] { "1397" }));
        }

        [Fact]
        public void Opening_is_frames_80_to_84()
        {
            Assert.Equal(Hex("0801"), ConnectionProtocol.BuildDialogClosed(ConnectionProtocol.NpcDialogCloseReason));
            Assert.Equal(Hex("1014220431333937"), BankProtocol.BuildFeePaid(1397));
            Assert.Equal(Hex("08a6e1c41f"), ConnectionProtocol.BuildKamas(66138278));
            Assert.Equal(Hex("08ffffffff071810"), BankProtocol.BuildOpened());

            // The first of the 1,397 stacks of frame 84, and nothing behind it: that bank had no kamas.
            Assert.Equal(Hex("0a0f083f2a0b08f314180220f794e2fc01"),
                         BankProtocol.BuildContent(new[] { Stack(2675, 2, 530090615) }, 0));
            // With kamas, they go behind in f2.
            Assert.Equal(Hex("0a0f083f2a0b08f314180220f794e2fc01108827"),
                         BankProtocol.BuildContent(new[] { Stack(2675, 2, 530090615) }, 5000));

            Assert.Equal(Hex("180b"), ConnectionProtocol.BuildStorageClosed());
        }

        [Fact]
        public void Items_in_and_out_are_the_captures()
        {
            // In, frames 49-50 of the bank capture: itd with the new uid, ium with the old one.
            Assert.Equal(Hex("0a10083f2a0c08da890118012088c3988002"),
                         ConnectionProtocol.BuildItemArrived(1, Stack(17626, 1, 537272712)));
            Assert.Equal(Hex("0883acecfe01"), ConnectionProtocol.BuildItemGone(534451715));

            // Out, frames 62-63: iua with another new uid, itc with the bank's.
            Assert.Equal(Hex("1a10083f2a0c08da8901180120f698998002"),
                         ConnectionProtocol.BuildItemArrived(3, Stack(17626, 1, 537283702)));
            Assert.Equal(Hex("0888c3988002"), ConnectionProtocol.BuildItemGone(537272712));

            // An arrival on a stack already in the bag, frame 36 of the bin capture: ivj 2.
            Assert.Equal(Hex("1a0810aacaa080021802"), ConnectionProtocol.BuildItemQuantity(537404714, 2));
            // And what is left of the storage's stack, frame 37: itd with 2.
            Assert.Equal(Hex("0a0f083f2a0b088803180220d1cd938002"),
                         ConnectionProtocol.BuildItemArrived(1, Stack(392, 2, 537192145)));
        }

        // ─── The bankers ────────────────────────────────────────────────────────

        [Fact]
        public void The_bank_reply_is_the_last_one_with_its_wording()
        {
            // The Banquero bontariano, 6394, as his template declares them.
            var consult = new HashSet<long> { 913010, 913011 };
            var banker = Bankers.Pick(6394, new long[] { 63534, 63535, 63536 },
                                      new long[] { 913010, 913011, 913013 }, consult);

            Assert.NotNull(banker);
            Assert.Equal(63535, banker!.Consult);
            Assert.Equal(2, banker.Candidates);

            // With no written conversation he offers his last reply; with the bank in front, frame 74.
            Assert.Equal(new long[] { 63535, 63536 }, BankHandler.WithTheBankReply(banker, new long[] { 63536 }));
            Assert.Equal(new long[] { 63535, 63536 }, BankHandler.WithTheBankReply(banker, new long[] { 63535, 63536 }));

            Assert.Null(Bankers.Pick(1, new long[] { 5, 6 }, new long[] { 7, 8 }, consult));
        }

        [Fact]
        public void The_wording_is_in_the_client_s_texts()
        {
            var keys = Bankers.TextKeysSaying(Bankers.ConsultWording);
            Assert.Contains(913010L, keys);
            Assert.Contains(913011L, keys);
            Assert.Contains(909688L, keys);   // the Brakmarian banker's
        }

        // ─── Storage ────────────────────────────────────────────────────────────

        private static void Forget(long[] accounts, long[] characters)
        {
            // The tables are the bank's to create; a base fresh out of datos/world.zip has none.
            Bank.Initialize();

            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            foreach (long account in accounts)
            {
                using var items = connection.CreateCommand();
                items.CommandText = "DELETE FROM BankItems WHERE AccountId = $a; DELETE FROM BankAccounts WHERE AccountId = $a;";
                items.Parameters.AddWithValue("$a", account);
                items.ExecuteNonQuery();
            }
            foreach (long character in characters)
            {
                using var items = connection.CreateCommand();
                items.CommandText = "DELETE FROM CharacterItems WHERE CharacterId = $c;";
                items.Parameters.AddWithValue("$c", character);
                items.ExecuteNonQuery();
            }
        }

        private static long Give(long character, int gid, int quantity, int position = Equipment.Bag)
        {
            long uid = DatabaseManager.NextItemUid();
            Assert.True(DatabaseManager.InsertCharacterItem(uid, character, gid, quantity, position, null));
            return uid;
        }

        private static int InBag(long character, long uid)
            => HavenBagStore.FromInventory(character, uid)?.Quantity ?? 0;

        [Fact]
        public async Task Stacks_go_in_and_out_whole_or_in_part()
        {
            const long account = 8_800_000_001, character = 8_800_100_001;
            Bank.Initialize();
            Forget(new[] { account }, new[] { character });
            try
            {
                long single = Give(character, 17626, 1);
                long pile = Give(character, 392, 10);

                // The whole stack: gone from the bag, in the bank under a new uid.
                var whole = await Bank.DepositAsync(account, character, single, 1);
                Assert.NotNull(whole);
                Assert.True(whole!.NewStack);
                Assert.Equal(0, whole.LeftInBag);
                Assert.NotEqual(single, whole.InBank.Uid);
                Assert.Equal(0, InBag(character, single));

                // Four of ten: six stay behind.
                var four = await Bank.DepositAsync(account, character, pile, 4);
                Assert.Equal(6, four!.LeftInBag);
                Assert.Equal(4, four.InBank.Quantity);
                Assert.Equal(6, InBag(character, pile));

                // Three more land on the same bank stack.
                var three = await Bank.DepositAsync(account, character, pile, 3);
                Assert.False(three!.NewStack);
                Assert.Equal(four.InBank.Uid, three.InBank.Uid);
                Assert.Equal(7, three.InBank.Quantity);
                Assert.Equal(2, Bank.ItemsOf(account).Count);
                Assert.Equal(2, Bank.FeeOf(account));

                // Two out: they join what is left in the bag, and five stay in the bank.
                var two = await Bank.WithdrawAsync(account, character, three.InBank.Uid, 2);
                Assert.True(two!.Merged);
                Assert.Equal(pile, two.InBag.Uid);
                Assert.Equal(5, two.InBag.Quantity);
                Assert.Equal(5, two.LeftInBank!.Quantity);

                // Asking for more than there is takes what there is.
                var rest = await Bank.WithdrawAsync(account, character, three.InBank.Uid, 99);
                Assert.Equal(5, rest!.Moved);
                Assert.Null(rest.LeftInBank);
                Assert.Equal(10, InBag(character, pile));

                // The single one comes back under a uid of its own.
                var back = await Bank.WithdrawAsync(account, character, whole.InBank.Uid, 1);
                Assert.False(back!.Merged);
                Assert.NotEqual(whole.InBank.Uid, back.InBag.Uid);
                Assert.Equal(1, InBag(character, back.InBag.Uid));
                Assert.Empty(Bank.ItemsOf(account));

                // Nothing that is not there moves.
                Assert.Null(await Bank.WithdrawAsync(account, character, whole.InBank.Uid, 1));
                Assert.Null(await Bank.DepositAsync(account, character, single, 1));
            }
            finally
            {
                Forget(new[] { account }, new[] { character });
            }
        }

        [Fact]
        public async Task A_worn_item_does_not_go_in()
        {
            const long account = 8_800_000_002, character = 8_800_100_002;
            Forget(new[] { account }, new[] { character });
            try
            {
                long ring = Give(character, 2469, 1, position: 2);
                Assert.Null(await Bank.DepositAsync(account, character, ring, 1));
                Assert.Equal(1, InBag(character, ring));
                Assert.Empty(Bank.ItemsOf(account));
            }
            finally
            {
                Forget(new[] { account }, new[] { character });
            }
        }

        [Fact]
        public async Task Every_character_of_the_account_sees_the_same_bank()
        {
            const long account = 8_800_000_003, other = 8_800_000_004;
            const long first = 8_800_100_003, second = 8_800_100_004, stranger = 8_800_100_005;
            Forget(new[] { account, other }, new[] { first, second, stranger });
            try
            {
                long uid = Give(first, 6902, 3);
                var gone = await Bank.DepositAsync(account, first, uid, 3);

                // The second character of the account takes out what the first put in...
                var taken = await Bank.WithdrawAsync(account, second, gone!.InBank.Uid, 3);
                Assert.NotNull(taken);
                Assert.Equal(3, InBag(second, taken!.InBag.Uid));
                Assert.Equal(0, InBag(first, taken.InBag.Uid));

                // ...and another account's bank knows nothing of it.
                long mine = Give(stranger, 6902, 1);
                var theirs = await Bank.DepositAsync(other, stranger, mine, 1);
                Assert.Null(await Bank.WithdrawAsync(account, first, theirs!.InBank.Uid, 1));
                Assert.Single(Bank.ItemsOf(other));
                Assert.Empty(Bank.ItemsOf(account));

                // Kamas are the account's too.
                Assert.NotNull(await Bank.MoveKamasAsync(account, first, 1_000, 400));
                var out1 = await Bank.MoveKamasAsync(account, second, 0, -400);
                Assert.Equal(400, out1!.Value.Character);
                Assert.Equal(0, Bank.KamasOf(account));
            }
            finally
            {
                Forget(new[] { account, other }, new[] { first, second, stranger });
            }
        }

        [Fact]
        public async Task Kamas_go_in_and_out_and_never_below_zero()
        {
            const long account = 8_800_000_005, character = 8_800_100_006;
            Forget(new[] { account }, Array.Empty<long>());
            try
            {
                var put = await Bank.MoveKamasAsync(account, character, 1_500, 1_000);
                Assert.Equal(new Bank.KamasMove(500, 1_000, 1_000), put);

                // More than the purse holds puts the purse in, no more.
                var all = await Bank.MoveKamasAsync(account, character, 500, 9_999);
                Assert.Equal(new Bank.KamasMove(0, 1_500, 500), all);

                var some = await Bank.MoveKamasAsync(account, character, 0, -200);
                Assert.Equal(new Bank.KamasMove(200, 1_300, -200), some);

                // More than the bank holds takes the bank out, no more; then nothing moves.
                var rest = await Bank.MoveKamasAsync(account, character, 200, -99_999);
                Assert.Equal(new Bank.KamasMove(1_500, 0, -1_300), rest);
                Assert.Null(await Bank.MoveKamasAsync(account, character, 1_500, -1));
                Assert.Null(await Bank.MoveKamasAsync(account, character, 0, 1));
                Assert.Equal(0, Bank.KamasOf(account));
            }
            finally
            {
                Forget(new[] { account }, Array.Empty<long>());
            }
        }

        [Fact]
        public async Task Kamas_come_from_outside_with_nobody_online()
        {
            const long account = 8_800_000_006;
            Forget(new[] { account }, Array.Empty<long>());
            try
            {
                Assert.Null(SessionRegistry.InWorld().FirstOrDefault(s => s.AccountId == account));

                Assert.True(await Bank.AddKamasAsync(account, 5_000));
                Assert.Equal(5_000, Bank.KamasOf(account));

                // A take larger than the bank is refused whole.
                Assert.False(await Bank.AddKamasAsync(account, -6_000));
                Assert.Equal(5_000, Bank.KamasOf(account));

                Assert.True(await Bank.AddKamasAsync(account, -5_000));
                Assert.Equal(0, Bank.KamasOf(account));

                Assert.True(await Bank.AddKamasAsync(account, 0));
                Assert.False(await Bank.AddKamasAsync(0, 10));

                // No overflow either.
                Assert.True(await Bank.AddKamasAsync(account, long.MaxValue));
                Assert.False(await Bank.AddKamasAsync(account, 1));
                Assert.Equal(long.MaxValue, Bank.KamasOf(account));
            }
            finally
            {
                Forget(new[] { account }, Array.Empty<long>());
            }
        }

        [Fact]
        public async Task Kamas_from_many_threads_at_once_all_arrive()
        {
            const long account = 8_800_000_007;
            Forget(new[] { account }, Array.Empty<long>());
            try
            {
                var credits = Enumerable.Range(0, 50).Select(_ => Task.Run(() => Bank.AddKamasAsync(account, 100)));
                var debits = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
                {
                    // A debit may come before there is anything to take; it is then refused and retried.
                    while (!await Bank.AddKamasAsync(account, -10)) await Task.Yield();
                    return true;
                }));

                var results = await Task.WhenAll(credits.Concat(debits));
                Assert.All(results, Assert.True);
                Assert.Equal(50 * 100 - 20 * 10, Bank.KamasOf(account));
            }
            finally
            {
                Forget(new[] { account }, Array.Empty<long>());
            }
        }

        [Fact]
        public async Task Kamas_from_outside_reach_a_seller_standing_at_the_counter()
        {
            const long account = 8_800_000_008, character = 8_800_100_008;
            Forget(new[] { account }, Array.Empty<long>());
            var seller = GameSession.SinSocket();
            seller.BindAccount(account, 1);
            seller.State.CharacterId = character;
            seller.State.MapId = 217059328;
            seller.State.BankMapId = 217059328;
            seller.EnterWorld();
            Assert.True(SessionRegistry.Register(seller));
            try
            {
                // From another thread and no session at all, as a buyer's would be for this one.
                Assert.True(await Task.Run(() => Bank.AddKamasAsync(account, 12_345)));
                Assert.Equal(12_345, Bank.KamasOf(account));
                using (SessionContext.Push(seller)) Assert.True(BankHandler.IsOpen);
            }
            finally
            {
                SessionRegistry.Unregister(seller);
                Forget(new[] { account }, Array.Empty<long>());
            }
        }

        [Fact]
        public async Task Opening_costs_a_kama_a_stack()
        {
            const long account = 8_800_000_009, character = 8_800_100_009;
            Forget(new[] { account }, new[] { character });
            try
            {
                // An empty bank is free.
                var free = await Bank.OpenAsync(account, character, 0);
                Assert.True(free!.Paid);
                Assert.Equal(0, free.Fee);

                foreach (int gid in new[] { 6902, 6903, 392 })
                    await Bank.DepositAsync(account, character, Give(character, gid, 5), 5);

                var paid = await Bank.OpenAsync(account, character, 10);
                Assert.True(paid!.Paid);
                Assert.Equal(3, paid.Fee);
                Assert.Equal(7, paid.CharacterKamas);
                Assert.Equal(3, paid.Items.Count);

                var shortOf = await Bank.OpenAsync(account, character, 2);
                Assert.False(shortOf!.Paid);
                Assert.Equal(2, shortOf.CharacterKamas);
            }
            finally
            {
                Forget(new[] { account }, new[] { character });
            }
        }

        // ─── At the counter ─────────────────────────────────────────────────────

        [Fact]
        public async Task At_the_counter_a_player_pays_moves_things_and_closes()
        {
            const long account = 8_800_000_010, character = 8_800_100_010;
            Forget(new[] { account }, new[] { character });
            var player = GameSession.SinSocket();
            player.BindAccount(account, 1);
            player.State.CharacterId = character;
            player.State.MapId = 217059328;
            player.State.Kamas = 1_000;
            player.EnterWorld();
            Assert.True(SessionRegistry.Register(player));
            try
            {
                using (SessionContext.Push(player))
                {
                    long uid = Give(character, 17626, 1);
                    Equipment.Add(uid, 17626, 1, Equipment.Bag, null);

                    Assert.False(BankHandler.IsOpen);
                    Assert.False(await BankHandler.MoveAsync(null!, Kcr(1, uid)));

                    await BankHandler.OpenAsync(null!);
                    Assert.True(BankHandler.IsOpen);
                    Assert.Equal(1_000, player.State.Kamas);   // nothing inside yet, nothing to pay

                    // In: gone from the bag and from the session's inventory.
                    Assert.True(await BankHandler.MoveAsync(null!, Kcr(1, uid)));
                    Assert.Null(Equipment.ByUid(uid));
                    var stored = Assert.Single(Bank.ItemsOf(account));

                    // Out, with the -1 of frame 61: back under a new uid.
                    Assert.True(await BankHandler.MoveAsync(null!, Kcr(-1, stored.Uid)));
                    Assert.Empty(Bank.ItemsOf(account));
                    var back = Assert.Single(Equipment.All, i => i.Template == 17626);
                    Assert.NotEqual(stored.Uid, back.Uid);
                    Assert.Equal(1, InBag(character, back.Uid));

                    // Kamas in and out.
                    Assert.True(await BankHandler.KamasAsync(null!, Kee(300)));
                    Assert.Equal(700, player.State.Kamas);
                    Assert.Equal(300, Bank.KamasOf(account));
                    Assert.True(await BankHandler.KamasAsync(null!, Kee(-100)));
                    Assert.Equal(800, player.State.Kamas);
                    Assert.Equal(200, Bank.KamasOf(account));

                    await BankHandler.CloseAsync(null!);
                    Assert.False(BankHandler.IsOpen);
                    Assert.False(await BankHandler.KamasAsync(null!, Kee(1)));

                    // Put something in, and the next opening costs one kama.
                    await BankHandler.OpenAsync(null!);
                    Assert.True(await BankHandler.MoveAsync(null!, Kcr(1, back.Uid)));
                    await BankHandler.CloseAsync(null!);
                    await BankHandler.OpenAsync(null!);
                    Assert.Equal(799, player.State.Kamas);

                    // A window left open does not follow its owner to another map.
                    player.State.MapId = 191104002;
                    Assert.False(BankHandler.IsOpen);
                }
            }
            finally
            {
                SessionRegistry.Unregister(player);
                Forget(new[] { account }, new[] { character });
            }
        }

        private static byte[] Kcr(long quantity, long uid)
            => ConnectionProtocol.Push(Op.Kcr, Pb.New().Var(1, quantity).Var(2, uid).Build());

        private static byte[] Kee(long kamas)
            => ConnectionProtocol.Push(Op.Kee, Pb.New().Var(1, kamas).Build());
    }

    /// <summary>
    /// The whole visit to the Bontarian banker, through the real handlers and a real socket, against
    /// the capture's frames 72 to 89: what the client said goes in as it was captured, and what the
    /// server answers has to come out as it was captured.
    /// </summary>
    /// <remarks>
    /// In the MapManager collection because it loads the world's NPCs, which other tests of that
    /// collection load too; two loads at once would read each other's half-filled tables.
    /// </remarks>
    [Collection("MapManager")]
    public class BankVisitTests
    {
        private static byte[] Hex(string hex) => Convert.FromHexString(hex);

        [Fact]
        public async Task A_visit_to_the_Bontarian_banker_is_the_capture_s_traffic()
        {
            const long bankMap = 217059328, account = 8_800_000_020, character = 8_800_100_020;

            Npcs.Initialize();
            Bankers.Initialize();
            Bank.Initialize();

            // The banker stands where the capture's jss has him: -20000, template 6394, cell 289.
            var banker = Npcs.Find(bankMap, -20000);
            Assert.NotNull(banker);
            Assert.Equal(6394, banker!.NpcId);
            Assert.Equal(289, banker.Cell);
            Assert.Equal(63535, Bankers.Of(6394)!.Consult);

            // 1,397 stacks, the capture's, so the fee is its 1,397; and the purse that is left with
            // 66,138,278 once that is paid, frame 82.
            Forget(account);
            using (var connection = new SqliteConnection(DatabaseManager.WorldConnectionString))
            {
                connection.Open();
                using var transaction = connection.BeginTransaction();
                for (int i = 0; i < 1397; i++)
                {
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO BankItems (Uid, AccountId, Gid, Quantity, Effects) VALUES ($u, $a, 2675, 2, '');";
                    insert.Parameters.AddWithValue("$u", DatabaseManager.NextItemUid());
                    insert.Parameters.AddWithValue("$a", account);
                    insert.ExecuteNonQuery();
                }
                transaction.Commit();
            }

            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            GameSession? player = null;
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
                using var server = await listener.AcceptTcpClientAsync();
                var toClient = server.GetStream();
                var fromServer = client.GetStream();

                player = new GameSession(toClient);
                player.BindAccount(account, 1);
                player.State.CharacterId = character;
                player.State.MapId = bankMap;
                player.State.Kamas = 66_138_278 + 1_397;
                player.EnterWorld();
                Assert.True(SessionRegistry.Register(player));

                using (SessionContext.Push(player))
                {
                    // Frame 72, the click on the banker, byte for byte.
                    await NpcHandler.InteractAsync(toClient,
                        ConnectionProtocol.Push(Op.Iov, Hex("08031080a0c06718e0e3feffffffffffff01")));

                    Assert.Equal(Hex("2080a0c06728e0e3feffffffffffff01"), await Next(fromServer, Op.Ioc));                // 73
                    Assert.Equal(Hex("08c2f502120908aff0031a0308c401120408b0f0031a0431333937"), await Next(fromServer, Op.Ios)); // 74

                    // Frame 79, "Quiero consultar mi cofre."
                    await NpcHandler.ReplyAsync(toClient, ConnectionProtocol.Push(Op.Ioy, Hex("08aff003")));

                    Assert.Equal(Hex("0801"), await Next(fromServer, Op.Kld));                  // 80
                    Assert.Equal(Hex("1014220431333937"), await Next(fromServer, Op.Lqn));      // 81
                    Assert.Equal(Hex("08a6e1c41f"), await Next(fromServer, Op.Ivf));            // 82
                    Assert.Equal(Hex("08ffffffff071810"), await Next(fromServer, Op.Kci));      // 83

                    // 84: the 1,397 stacks, every one in the bag's position, and no f2 -- no kamas.
                    var content = ProtoMessage.Parse(await Next(fromServer, Op.Iwb)).Fields;
                    Assert.Equal(1397, content.Count(f => f.FieldNumber == 1));
                    Assert.DoesNotContain(content, f => f.FieldNumber == 2);

                    Assert.Equal(66_138_278, player.State.Kamas);
                    Assert.True(BankHandler.IsOpen);

                    // An item in, the way the guild chest of the same capture answers, frames 48-51:
                    // itd with a new uid, ium with the old one, the pods.
                    long uid = DatabaseManager.NextItemUid();
                    Assert.True(DatabaseManager.InsertCharacterItem(uid, character, 17626, 1, Equipment.Bag, null));
                    Equipment.Add(uid, 17626, 1, Equipment.Bag, null);

                    await BankHandler.MoveAsync(toClient, ConnectionProtocol.Push(Op.Kcr, Pb.New().Var(1, 1).Var(2, uid).Build()));
                    var inBank = ProtoMessage.Parse(await Next(fromServer, Op.Itd)).Fields.Single();
                    long bankUid = ProtoMessage.Parse(ProtoMessage.Parse(inBank.BytesValue).Fields.Single(f => f.FieldNumber == 5).BytesValue)
                                               .Fields.Single(f => f.FieldNumber == 4).VarIntValue;
                    Assert.NotEqual(uid, bankUid);
                    Assert.Equal(ConnectionProtocol.BuildItemGone(uid), await Next(fromServer, Op.Ium));
                    await Next(fromServer, Op.Iun);

                    // And out again with the -1 of frame 61: iua, itc, the pods.
                    await BankHandler.MoveAsync(toClient, ConnectionProtocol.Push(Op.Kcr, Pb.New().Var(1, -1).Var(2, bankUid).Build()));
                    await Next(fromServer, Op.Iua);
                    Assert.Equal(ConnectionProtocol.BuildItemGone(bankUid), await Next(fromServer, Op.Itc));
                    await Next(fromServer, Op.Iun);

                    // Kamas in: the purse, and the bank again with its total in f2.
                    await BankHandler.KamasAsync(toClient, ConnectionProtocol.Push(Op.Kee, Pb.New().Var(1, 278).Build()));
                    Assert.Equal(ConnectionProtocol.BuildKamas(66_138_000), await Next(fromServer, Op.Ivf));
                    var again = ProtoMessage.Parse(await Next(fromServer, Op.Iwb)).Fields;
                    Assert.Equal(1397, again.Count(f => f.FieldNumber == 1));
                    Assert.Equal(278, again.Single(f => f.FieldNumber == 2).VarIntValue);

                    // Frames 88-89: the cross.
                    await BankHandler.CloseAsync(toClient);
                    Assert.Equal(Hex("180b"), await Next(fromServer, Op.Khd));
                    Assert.False(BankHandler.IsOpen);

                    foreach (var item in Equipment.All.ToList())
                        DatabaseManager.DestroyCharacterItem(character, item.Uid, 0);
                }
            }
            finally
            {
                listener.Stop();
                if (player != null) SessionRegistry.Unregister(player);
                Forget(account);
            }
        }

        /// <summary>The next frame from the server, which has to be that opcode; its payload.</summary>
        private static async Task<byte[]> Next(System.IO.Stream stream, string opcode)
        {
            var read = Jondo.Protocol.NetworkMessage.ReadFrameAsync(stream);
            var first = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(first == read, $"nothing arrived where {opcode} was expected");

            byte[] frame = await read;
            Assert.NotNull(frame);
            byte[]? payload = ConnectionProtocol.ReadPayload(frame, opcode);
            Assert.True(payload != null, $"{opcode} was expected and {NetworkEnvelope.GetMessageTypeUrl(frame)} came");
            return payload!;
        }

        private static void Forget(long account)
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM BankItems WHERE AccountId = $a; DELETE FROM BankAccounts WHERE AccountId = $a;";
            command.Parameters.AddWithValue("$a", account);
            command.ExecuteNonQuery();
        }
    }
}

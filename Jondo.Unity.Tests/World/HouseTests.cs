using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// A house's frames, byte for byte against the captures of "Casas/" -- Sacrogrito69's house
    /// 522652, door 522653 of map 212601864, instance 22 -- and the pandala door that asked for a
    /// code; and the store behind them: owners per account, sale, codes, purchase.
    /// </summary>
    [Collection("forgemagic")]
    public class HouseTests
    {
        private static byte[] Hex(string hex) => Convert.FromHexString(hex);

        private const int House = 522652, Door = 522653, Instance = 22;

        private static HouseProtocol.Plaque Sacrogrito(long price = 0, bool locked = false, byte[]? guild = null)
            => new HouseProtocol.Plaque
            {
                Instance = Instance, OwnerName = "Sacrogrito69", OwnerTag = "4234",
                Price = price, Locked = locked, Rooms = 3, GuildBlock = guild,
            };

        // ─── The sale, "poner casa en venta por 20.500.000" ─────────────────────

        [Fact]
        public void The_sale_window_and_the_plaque_are_frames_3_and_5()
        {
            // khr: the Casa grande de Bonta's own price, 2,500,000, while not on sale.
            Assert.Equal(Hex("109cf31f1816200128a0cb9801"), HouseProtocol.BuildSaleWindow(false, House, Instance, 2_500_000));
            // izr: the full plaque, rooms, f10 and f11 included.
            Assert.Equal(Hex("0a242001300342140a0c536163726f677269746f3639120434323334480150025a02d3026016"),
                         HouseProtocol.BuildInfo(Sacrogrito()));
        }

        [Fact]
        public void The_sale_burst_is_frames_13_to_21()
        {
            var off = new[] { (31094, 84), (31096, 100), (31098, 98) };
            var on = new[] { (31094, 84), (31096, 100), (31099, 108) };

            Assert.Equal(Hex("1a210801220608f6f2011054220608f8f2011064220608faf2011062289df31f30ac02"),
                         HouseProtocol.BuildElementChanged(Door, Houses.DoorType, off));                                   // 13
            Assert.Equal(Hex("0a1c200142140a0c536163726f677269746f363912043432333448016016109cf31f1a039df31f"),
                         HouseProtocol.BuildChanged(House, new long[] { Door }, Sacrogrito()));                           // 14
            Assert.Equal(Hex("1816209cf31f"), HouseProtocol.BuildPutOnSale(Instance, House));                              // 15
            Assert.Equal(Hex("1a210801220608f6f2011054220608f8f2011064220608fbf201106c289df31f30ac02"),
                         HouseProtocol.BuildElementChanged(Door, Houses.DoorType, on));                                    // 16
            Assert.Equal(Hex("0a21200138a09ce30942140a0c536163726f677269746f363912043432333448016016109cf31f1a039df31f"),
                         HouseProtocol.BuildChanged(House, new long[] { Door }, Sacrogrito(20_500_000)));                 // 17, 20

            var owned = new HouseProtocol.Owned
            {
                HouseId = House, Model = 442, StreetMapId = 212601864, WorldX = -30, WorldY = -53, SubAreaId = 973,
                Plaque = Sacrogrito(20_500_000),
            };
            Assert.Equal(Hex("0a54109cf31f20ba03324b0a1e088898b06510e2ffffffffffffffff0120cd0728cbffffffffffffffff0112292001300338a09ce30942140a0c536163726f677269746f3639120434323334480150025a02d3026016"),
                         HouseProtocol.BuildAccountHouses(new[] { owned }));                                               // 18
            Assert.Equal(Hex("120c536163726f677269746f3639189cf31f2a043432333430a09ce3094016"),
                         HouseProtocol.BuildSellingUpdate("Sacrogrito69", "4234", House, 20_500_000, Instance));           // 19
            Assert.Equal(Hex("0803"), ConnectionProtocol.BuildDialogClosed(HouseProtocol.SaleClosed));                    // 21
        }

        [Fact]
        public void Taking_it_off_sale_is_the_other_capture()
        {
            // "retirar casa de la venta": the window shows the price it is at, 21,500,000 (frame 3)...
            Assert.Equal(Hex("109cf31f1816200128e0a0a00a"), HouseProtocol.BuildSaleWindow(false, House, Instance, 21_500_000));
            Assert.Equal(Hex("0a292001300338e0a0a00a42140a0c536163726f677269746f3639120434323334480150025a02d3026016"),
                         HouseProtocol.BuildInfo(Sacrogrito(21_500_000)));                                                 // 5

            // ...and afterwards everything goes without a price (frames 22-23).
            var owned = new HouseProtocol.Owned
            {
                HouseId = House, Model = 442, StreetMapId = 212601864, WorldX = -30, WorldY = -53, SubAreaId = 973,
                Plaque = Sacrogrito(),
            };
            Assert.Equal(Hex("0a4f109cf31f20ba0332460a1e088898b06510e2ffffffffffffffff0120cd0728cbffffffffffffffff0112242001300342140a0c536163726f677269746f3639120434323334480150025a02d3026016"),
                         HouseProtocol.BuildAccountHouses(new[] { owned }));
            Assert.Equal(Hex("120c536163726f677269746f3639189cf31f2a04343233344016"),
                         HouseProtocol.BuildSellingUpdate("Sacrogrito69", "4234", House, 0, Instance));

            // An account with no house: jaa empty, as at the world entry of a new account.
            Assert.Empty(HouseProtocol.BuildAccountHouses(Array.Empty<HouseProtocol.Owned>()));
        }

        // ─── The plaque on the map ──────────────────────────────────────────────

        [Fact]
        public void A_locked_plaque_carries_f5()
        {
            // "desde dentro de casa salir a fuera", frame 14: a neighbour's instance 16.
            var plaque = new HouseProtocol.Plaque
            {
                Instance = 16, OwnerName = "UnDanielableToBeStopped", OwnerTag = "7888", Locked = true,
            };
            Assert.Equal(Hex("0a2920012801421f0a17556e44616e69656c61626c65546f426553746f7070656412043738383848016010109cf31f1a039df31f"),
                         HouseProtocol.BuildChanged(House, new long[] { Door }, plaque));
        }

        [Fact]
        public void A_house_on_its_street_is_the_jss_f9()
        {
            // Pandala, frame 3867: house 519513, model 407, door 520644, MerkShadow's instance 4,
            // shared with the guild "Arma de Dios" -- a block Jondo does not send, given here as captured.
            byte[] guild = Hex("0a0c1a0a08e7011020288fd2bf0710982c1a0c41726d612064652044696f73200f");
            var plaque = new HouseProtocol.Plaque
            {
                Instance = 4, OwnerName = "MerkShadow", OwnerTag = "4649", Locked = true, GuildBlock = guild,
            };
            Assert.Equal(Hex("10d9da1f2097033a460a03c4e31f123f1a210a0c1a0a08e7011020288fd2bf0710982c1a0c41726d612064652044696f73200f2001280142120a0a4d65726b536861646f7712043436343948016004"),
                         HouseProtocol.BuildOnMap(519513, 407, new long[] { 520644 }, new[] { plaque }).Build());
        }

        [Fact]
        public void The_house_one_is_inside_is_the_jss_f7()
        {
            // "entrar en mi casa", frame 16: the street's -30,-53, the plaque, the house and its model.
            byte[] guild = Hex("0a111a0f08ce0210221885e2ef072896d2820110be351a0748657a626f6c612004");
            Assert.Equal(Hex("0a640a5b0a1608e2ffffffffffffffff0110cbffffffffffffffff0112411a210a111a0f08ce0210221885e2ef072896d2820110be351a0748657a626f6c6120042001280142140a0c536163726f677269746f363912043432333448016016109cf31f20ba03"),
                         HouseProtocol.BuildInterior(House, 442, -30, -53, Sacrogrito(locked: true, guild: guild)).Build());
        }

        // ─── Codes ──────────────────────────────────────────────────────────────

        [Fact]
        public void The_keypads_and_their_answers_are_the_captures()
        {
            // The owner's keypad (door and chest alike), and a stranger's at a locked door (pandala 3884).
            Assert.Equal(Hex("1808"), HouseProtocol.BuildCodeKeypad(toGetIn: false));
            Assert.Equal(Hex("08011808"), HouseProtocol.BuildCodeKeypad(toGetIn: true));

            // A code set: khu empty and kld 2. A wrong one: kld 2 and khu { f2: 1 } (pandala 3889-3890).
            Assert.Empty(HouseProtocol.BuildCodeResult(wrong: false));
            Assert.Equal(Hex("1001"), HouseProtocol.BuildCodeResult(wrong: true));
            Assert.Equal(Hex("0802"), ConnectionProtocol.BuildDialogClosed(HouseProtocol.CodeClosed));

            // The chest's iwn on "Poner el cerrojo", "poner cerrojo al cofre", frame 3.
            Assert.Equal(Hex("080110edf11f206928a28280c8e708"),
                         ConnectionProtocol.BuildElementInUse(522477, Houses.ChestLockSkill, 302677754146));
        }

        [Theory]
        [InlineData("13581321", "13581321")]
        [InlineData("135813", "135813")]
        [InlineData("1358", "1358")]
        [InlineData("", "")]
        [InlineData(null, "")]
        [InlineData("123456789", null)]
        [InlineData("12a4", null)]
        public void A_code_is_up_to_eight_digits_and_empty_means_none(string? typed, string? kept)
        {
            Assert.Equal(kept, HouseStore.Normalize(typed));
        }

        [Fact]
        public void An_empty_code_opens_nothing()
        {
            Assert.True(HouseStore.Matches("1358", "1358"));
            Assert.False(HouseStore.Matches("1358", "1357"));
            Assert.False(HouseStore.Matches("", ""));
        }

        // ─── Who is offered what ────────────────────────────────────────────────

        [Fact]
        public void The_door_offers_each_viewer_his_own_skills()
        {
            var nobody = new HouseStore.House();
            var mine = new HouseStore.House { AccountId = 7 };
            var onSale = new HouseStore.House { AccountId = 7, Price = 20_500_000 };

            // Its owner: enter, code, sell -- and change the price once on sale (frames 13 and 16).
            Assert.Equal(new[] { 84, 100, 98 }, HouseHandler.DoorSkillsFor(mine, mine: true));
            Assert.Equal(new[] { 84, 100, 108 }, HouseHandler.DoorSkillsFor(onSale, mine: true));

            // A stranger: enter alone at a house not on sale (pandala); enter and buy otherwise.
            Assert.Equal(new[] { 84 }, HouseHandler.DoorSkillsFor(mine, mine: false));
            Assert.Equal(new[] { 84, 97 }, HouseHandler.DoorSkillsFor(onSale, mine: false));
            Assert.Equal(new[] { 84, 97 }, HouseHandler.DoorSkillsFor(nobody, mine: false));

            // Five skills, five instances, none shared with another element's.
            var instances = Houses.OwnableDoorSkills.Select(s => HouseHandler.DoorSkillInstance(Door, s)).ToList();
            Assert.Equal(5, instances.Distinct().Count());
            Assert.Equal(Interactives.SkillInstanceOf(Door), HouseHandler.DoorSkillInstance(Door, Houses.EnterSkill));

            // Every element's own instance is below 910,000; the extra ones are all above a million.
            Assert.All(instances.Skip(1), i => Assert.True(i >= 1_000_000));
        }

        // ─── The store ──────────────────────────────────────────────────────────

        private const long StoreMap = 8_820_000_001;
        private const int StoreDoor = 522653;

        private static void Forget(params long[] accounts)
        {
            HouseStore.Forget(StoreMap, StoreDoor);
            Bank.Initialize();
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            foreach (long account in accounts)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM BankItems WHERE AccountId = $a; DELETE FROM BankAccounts WHERE AccountId = $a;";
                command.Parameters.AddWithValue("$a", account);
                command.ExecuteNonQuery();
            }
        }

        [Fact]
        public async Task A_house_nobody_owns_is_bought_at_its_model_s_price()
        {
            const long buyer = 8_820_000_010, character = 8_820_100_010;
            Forget(buyer);
            try
            {
                Assert.False(HouseStore.Of(StoreMap, StoreDoor).Owned);
                Assert.Equal(2_500_000, HouseStore.AskingPrice(HouseStore.Of(StoreMap, StoreDoor), 2_500_000));

                // Short of kamas: nothing happens.
                var (none, why) = await HouseStore.BuyAsync(StoreMap, StoreDoor, 2_500_000, buyer, character, 1_000, 2_500_000);
                Assert.Null(none);
                Assert.Equal(HouseStore.Refusal.NotEnoughKamas, why);

                // At another price than the window showed: nothing either.
                (none, why) = await HouseStore.BuyAsync(StoreMap, StoreDoor, 2_500_000, buyer, character, 9_000_000, 2_000_000);
                Assert.Equal(HouseStore.Refusal.PriceChanged, why);

                var (done, ok) = await HouseStore.BuyAsync(StoreMap, StoreDoor, 2_500_000, buyer, character, 3_000_000, 2_500_000);
                Assert.Equal(HouseStore.Refusal.None, ok);
                Assert.Equal(500_000, done!.BuyerKamas);
                Assert.Equal(0, done.SellerAccountId);

                var house = HouseStore.Of(StoreMap, StoreDoor);
                Assert.Equal(buyer, house.AccountId);
                Assert.False(house.ForSale);
                Assert.Single(HouseStore.OwnedBy(buyer));

                // One's own house is not bought again, and a house not on sale is not bought at all.
                (none, why) = await HouseStore.BuyAsync(StoreMap, StoreDoor, 2_500_000, buyer, character, 9_000_000, 2_500_000);
                Assert.Equal(HouseStore.Refusal.NotForSale, why);
            }
            finally
            {
                Forget(buyer);
            }
        }

        [Fact]
        public async Task Selling_hands_the_money_and_the_chests_to_the_seller_s_bank()
        {
            const long seller = 8_820_000_020, buyer = 8_820_000_021;
            const long sellerCharacter = 8_820_100_020, buyerCharacter = 8_820_100_021;
            Forget(seller, buyer);
            try
            {
                await HouseStore.BuyAsync(StoreMap, StoreDoor, 2_500_000, seller, sellerCharacter, 2_500_000, 2_500_000);

                // Only the owner sets the price, the codes; a stranger changes nothing.
                Assert.False(await HouseStore.SetPriceAsync(StoreMap, StoreDoor, buyer, 1));
                Assert.False(await HouseStore.SetAccessCodeAsync(StoreMap, StoreDoor, buyer, "1234"));
                Assert.False(await HouseStore.SetChestCodeAsync(StoreMap, StoreDoor, 522477, buyer, "1234"));

                Assert.True(await HouseStore.SetAccessCodeAsync(StoreMap, StoreDoor, seller, "13581321"));
                Assert.True(await HouseStore.SetChestCodeAsync(StoreMap, StoreDoor, 522477, seller, "1358"));
                Assert.True(HouseStore.Of(StoreMap, StoreDoor).Locked);
                Assert.Equal("1358", HouseStore.ChestCodeOf(StoreMap, StoreDoor, 522477));

                // The chest's Accept with nothing typed takes its code off.
                Assert.True(await HouseStore.SetChestCodeAsync(StoreMap, StoreDoor, 522477, seller, ""));
                Assert.Equal("", HouseStore.ChestCodeOf(StoreMap, StoreDoor, 522477));
                Assert.True(await HouseStore.SetChestCodeAsync(StoreMap, StoreDoor, 522477, seller, "1358"));

                // Something in a chest.
                var chest = StorageStacks.HouseChest(StoreMap, StoreDoor, 522477);
                using (var connection = new SqliteConnection(DatabaseManager.WorldConnectionString))
                {
                    connection.Open();
                    using var insert = connection.CreateCommand();
                    insert.CommandText = "INSERT INTO StorageItems (Uid, Storage, Gid, Quantity, Effects) VALUES ($u, $s, 878, 5, '');";
                    insert.Parameters.AddWithValue("$u", DatabaseManager.NextItemUid());
                    insert.Parameters.AddWithValue("$s", chest.Owner);
                    insert.ExecuteNonQuery();
                }

                // On sale, off sale, on sale again: 20,500,000.
                Assert.True(await HouseStore.SetPriceAsync(StoreMap, StoreDoor, seller, 21_500_000));
                Assert.True(await HouseStore.SetPriceAsync(StoreMap, StoreDoor, seller, 0));
                Assert.False(HouseStore.Of(StoreMap, StoreDoor).ForSale);
                Assert.True(await HouseStore.SetPriceAsync(StoreMap, StoreDoor, seller, 20_500_000));
                Assert.Equal(20_500_000, HouseStore.AskingPrice(HouseStore.Of(StoreMap, StoreDoor), 2_500_000));

                var (done, why) = await HouseStore.BuyAsync(StoreMap, StoreDoor, 2_500_000, buyer, buyerCharacter, 30_000_000, 20_500_000);
                Assert.Equal(HouseStore.Refusal.None, why);
                Assert.Equal(seller, done!.SellerAccountId);
                Assert.Equal(1, done.LotsToSeller);

                // The seller's bank: the price and what the chest held.
                Assert.Equal(20_500_000, Bank.KamasOf(seller));
                var banked = Assert.Single(Bank.ItemsOf(seller));
                Assert.Equal(878, banked.Gid);
                Assert.Equal(5, banked.Quantity);
                Assert.Empty(StorageStacks.ItemsOf(chest));

                // The buyer's house: no codes, not on sale.
                var house = HouseStore.Of(StoreMap, StoreDoor);
                Assert.Equal(buyer, house.AccountId);
                Assert.False(house.Locked);
                Assert.False(house.ForSale);
                Assert.Equal("", HouseStore.ChestCodeOf(StoreMap, StoreDoor, 522477));
                Assert.Empty(HouseStore.OwnedBy(seller));
            }
            finally
            {
                Forget(seller, buyer);
            }
        }

        [Fact]
        public async Task A_seller_whose_bank_cannot_take_it_keeps_his_house()
        {
            const long seller = 8_820_000_030, buyer = 8_820_000_031;
            Forget(seller, buyer);
            try
            {
                await HouseStore.BuyAsync(StoreMap, StoreDoor, 2_500_000, seller, 8_820_100_030, 2_500_000, 2_500_000);
                Assert.True(await HouseStore.SetPriceAsync(StoreMap, StoreDoor, seller, 1_000));
                Assert.True(await Bank.AddKamasAsync(seller, long.MaxValue));

                var (done, why) = await HouseStore.BuyAsync(StoreMap, StoreDoor, 2_500_000, buyer, 8_820_100_031, 5_000, 1_000);
                Assert.Null(done);
                Assert.Equal(HouseStore.Refusal.SellerBankFull, why);

                var house = HouseStore.Of(StoreMap, StoreDoor);
                Assert.Equal(seller, house.AccountId);
                Assert.Equal(1_000, house.Price);
            }
            finally
            {
                Forget(seller, buyer);
            }
        }
    }

    /// <summary>
    /// The Casa grande de Bonta of the captures, through the real handlers and two real sockets:
    /// bought by one account, put on sale, locked; knocked at by another with the wrong code and
    /// the right one; and bought by it. What the captures show comes out as they show it.
    /// </summary>
    /// <remarks>
    /// In the MapManager collection because it loads the maps and the houses, which other tests of
    /// that collection load too.
    /// </remarks>
    [Collection("MapManager")]
    public class HouseVisitTests
    {
        private static byte[] Hex(string hex) => Convert.FromHexString(hex);

        private const long Street = 212601864;
        private const int DoorElement = 522653;

        private static RegisteredInteractive DoorOf(Houses.Door door)
        {
            var interactive = new RegisteredInteractive(Street, new Interactives.Element(door.ElementId, door.Cell, door.Gfx), Houses.DoorType);
            foreach (int skill in Houses.OwnableDoorSkills)
                interactive.Add(InteractiveActionKind.HouseDoor, skill, HouseHandler.DoorSkillInstance(door.ElementId, skill));
            return interactive;
        }

        private static InteractiveAction Skill(RegisteredInteractive door, int skill)
            => door.Actions.Single(a => a.SkillId == skill);

        private static byte[] Frame(string opcode, byte[] body) => ConnectionProtocol.Push(opcode, body);

        private static void Forget(params long[] accounts)
        {
            HouseStore.Forget(Street, DoorElement);
            Bank.Initialize();
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            foreach (long account in accounts)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM BankItems WHERE AccountId = $a; DELETE FROM BankAccounts WHERE AccountId = $a;";
                command.Parameters.AddWithValue("$a", account);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// The door is a real one, so whatever a developer's base keeps on it -- an owner, codes,
        /// chest stacks -- is put aside for the test and put back after it.
        /// </summary>
        private static List<(string Table, object?[] Row)> Save()
        {
            HouseStore.Of(Street, DoorElement);   // the tables
            StorageStacks.EnsureTables();
            var saved = new List<(string, object?[])>();
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            foreach (var (table, where) in new[]
            {
                ("Houses", "MapId = $m AND ElementId = $e"),
                ("HouseChestCodes", "MapId = $m AND ElementId = $e"),
                ("StorageItems", "substr(Storage, 1, length($p)) = $p"),
            })
            {
                using var read = connection.CreateCommand();
                read.CommandText = $"SELECT * FROM {table} WHERE {where};";
                read.Parameters.AddWithValue("$m", Street);
                read.Parameters.AddWithValue("$e", DoorElement);
                read.Parameters.AddWithValue("$p", StorageStacks.HouseChestsPrefix(Street, DoorElement));
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    var row = new object?[reader.FieldCount];
                    reader.GetValues(row!);
                    saved.Add((table, row));
                }
            }
            return saved;
        }

        private static void Restore(List<(string Table, object?[] Row)> saved)
        {
            HouseStore.Forget(Street, DoorElement);
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            foreach (var (table, row) in saved)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = $"INSERT INTO {table} VALUES ({string.Join(", ", row.Select((_, i) => "$v" + i))});";
                for (int i = 0; i < row.Length; i++) insert.Parameters.AddWithValue("$v" + i, row[i] ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }
        }

        [Fact]
        public async Task An_owner_sells_and_locks_and_a_stranger_knocks_and_buys()
        {
            MapManager.Initialize();
            Houses.Initialize();
            Assert.True(Houses.TryGetDoor(Street, DoorElement, out var door));
            Assert.True(door.IsOwnable);
            Assert.Equal(442, door.Model);
            Assert.Equal(2_500_000, door.Price);
            Assert.Equal(3, door.Rooms);

            const long ownerAccount = 8_840_000_001, strangerAccount = 8_840_000_002;
            const long owner = 8_840_100_001, stranger = 8_840_100_002;
            var saved = Save();
            Forget(ownerAccount, strangerAccount);

            int houseId = Houses.HouseIdOf(Street, DoorElement);
            var interactive = DoorOf(door);

            await using var a = await ClientPipe.OpenAsync(ownerAccount, owner, Street, "Owner");
            await using var b = await ClientPipe.OpenAsync(strangerAccount, stranger, Street, "Stranger");
            a.Session.State.Kamas = 3_000_000;
            b.Session.State.Kamas = 30_000_000;
            try
            {
                // ── A buys it, nobody's, at the model's price.
                using (SessionContext.Push(a.Session))
                {
                    Assert.Equal(new[] { 84, 97 }, HouseHandler.VisibleActions(interactive, ownerAccount).Select(x => x.SkillId));

                    await HouseHandler.UseDoorAsync(a.ToClient, interactive, Skill(interactive, Houses.BuySkill));
                    await a.Next(Op.Iwn);
                    Assert.Equal(HouseProtocol.BuildSaleWindow(true, houseId, Houses.Instance, 2_500_000), await a.Next(Op.Khr));

                    // A number that is not the price buys nothing.
                    Assert.False(await HouseHandler.BuyAsync(a.ToClient, Frame(Op.Jal, Pb.New().Var(1, 1234).Build()), Op.Jal));

                    Assert.True(await HouseHandler.BuyAsync(a.ToClient, Frame(Op.Jad, Pb.New().Var(1, 2_500_000).Build()), Op.Jad));
                    Assert.Equal(ConnectionProtocol.BuildKamas(500_000), await a.Next(Op.Ivf));
                    Assert.Equal(HouseHandler.DoorChanged(door, HouseStore.Of(Street, DoorElement), ownerAccount), await a.Next(Op.Iwm));
                    await a.Next(Op.Izz);
                    Assert.NotEmpty(await a.Next(Op.Jaa));
                    Assert.Equal(Hex("0803"), await a.Next(Op.Kld));
                    Assert.True(await HouseHandler.CloseDialogAsync(a.ToClient));   // the kla behind: nothing more
                    Assert.Equal(500_000, a.Session.State.Kamas);
                }
                Assert.Equal(ownerAccount, HouseStore.Of(Street, DoorElement).AccountId);
                // B, on the street, sees the door become somebody's: enter alone.
                Assert.Equal(HouseHandler.DoorChanged(door, HouseStore.Of(Street, DoorElement), strangerAccount), await b.Next(Op.Iwm));
                await b.Next(Op.Izz);

                // ── A puts it on sale: the sale capture's frames 2-21.
                using (SessionContext.Push(a.Session))
                {
                    Assert.Equal(new[] { 84, 100, 98 }, HouseHandler.VisibleActions(interactive, ownerAccount).Select(x => x.SkillId));

                    await HouseHandler.UseDoorAsync(a.ToClient, interactive, Skill(interactive, Houses.SellSkill));
                    Assert.Equal(ConnectionProtocol.BuildElementInUse(DoorElement, 98, owner), await a.Next(Op.Iwn));
                    Assert.Equal(HouseProtocol.BuildSaleWindow(false, houseId, Houses.Instance, 2_500_000), await a.Next(Op.Khr));

                    Assert.True(await HouseHandler.InfoAsync(a.ToClient, Frame(Op.Izv, Pb.New().Var(1, Houses.Instance).Var(2, houseId).Build())));
                    var info = ProtoMessage.Parse(await a.Next(Op.Izr)).Fields.Single();
                    Assert.Equal(1, info.FieldNumber);

                    Assert.True(await HouseHandler.SellAsync(a.ToClient, Frame(Op.Jan, Hex("08a09ce30910011801"))));
                    var burst = await a.Take(9);
                    Assert.Equal(new[] { Op.Iwm, Op.Izz, Op.Jjt, Op.Iwm, Op.Izz, Op.Jaa, Op.Izu, Op.Izz, Op.Kld },
                                 burst.Select(f => f.Opcode));
                    Assert.Equal(HouseProtocol.BuildPutOnSale(Houses.Instance, houseId), burst[2].Payload);
                    Assert.Equal(Hex("0803"), burst[8].Payload);
                    Assert.True(await HouseHandler.CloseDialogAsync(a.ToClient));
                }
                Assert.Equal(20_500_000, HouseStore.Of(Street, DoorElement).Price);
                await b.Take(2);   // iwm, izz

                // ── A sets the access code: frames 5-18 of "cambiar codigo acceso".
                using (SessionContext.Push(a.Session))
                {
                    Assert.Equal(new[] { 84, 100, 108 }, HouseHandler.VisibleActions(interactive, ownerAccount).Select(x => x.SkillId));

                    await HouseHandler.UseDoorAsync(a.ToClient, interactive, Skill(interactive, Houses.CodeSkill));
                    await a.Next(Op.Iwn);
                    Assert.Equal(Hex("1808"), await a.Next(Op.Kia));
                    Assert.True(await HouseHandler.ChangeCodeAsync(a.ToClient, Frame(Op.Khv, Hex("0a0431333538"))));
                    await a.Next(Op.Iwm);
                    var locked = ProtoMessage.Parse(ProtoMessage.Parse(await a.Next(Op.Izz)).Fields.First(f => f.FieldNumber == 1).BytesValue).Fields;
                    Assert.Contains(locked, f => f.FieldNumber == 5 && f.VarIntValue == 1);
                    Assert.Empty(await a.Next(Op.Khu));
                    Assert.Equal(Hex("0802"), await a.Next(Op.Kld));
                }
                Assert.Equal("1358", HouseStore.Of(Street, DoorElement).AccessCode);
                await b.Take(2);

                // ── B knocks: the keypad, a wrong code (pandala 3882-3890), then the right one.
                using (SessionContext.Push(b.Session))
                {
                    Assert.Equal(new[] { 84, 97 }, HouseHandler.VisibleActions(interactive, strangerAccount).Select(x => x.SkillId));

                    await HouseHandler.UseDoorAsync(b.ToClient, interactive, Skill(interactive, Houses.EnterSkill));
                    await b.Next(Op.Iwn);
                    Assert.Equal(Hex("08011808"), await b.Next(Op.Kia));
                    Assert.True(await HouseHandler.UseCodeAsync(b.ToClient, Frame(Op.Khw, Hex("0a0431323334"))));
                    Assert.Equal(Hex("0802"), await b.Next(Op.Kld));
                    Assert.Equal(Hex("1001"), await b.Next(Op.Khu));
                    Assert.Equal(Street, b.Session.State.MapId);

                    await HouseHandler.UseDoorAsync(b.ToClient, interactive, Skill(interactive, Houses.EnterSkill));
                    await b.Next(Op.Iwn);
                    await b.Next(Op.Kia);
                    Assert.True(await HouseHandler.UseCodeAsync(b.ToClient, Frame(Op.Khw, Hex("0a0431333538"))));
                    Assert.Equal(Hex("0802"), await b.Next(Op.Kld));
                    Assert.Empty(await b.Next(Op.Khu));
                    await b.Next(Op.Jsd);
                    Assert.Equal(Pb.New().Var(1, door.InteriorMapId).Build(), await b.Next(Op.Jqw));
                    await b.Next(Op.Lqu);
                    Assert.Equal(door.InteriorMapId, b.Session.State.MapId);
                    Assert.Equal(DoorElement, b.Session.State.HouseEntryElementId);
                }
                await a.Next(Op.Kmu);   // B is gone from A's street

                // ── B, back outside, buys it at 20,500,000: the money goes to A's bank.
                b.Session.State.MapId = Street;
                using (SessionContext.Push(b.Session))
                {
                    await HouseHandler.UseDoorAsync(b.ToClient, interactive, Skill(interactive, Houses.BuySkill));
                    await b.Next(Op.Iwn);
                    Assert.Equal(HouseProtocol.BuildSaleWindow(true, houseId, Houses.Instance, 20_500_000), await b.Next(Op.Khr));
                    Assert.True(await HouseHandler.BuyAsync(b.ToClient, Frame(Op.Jal, Pb.New().Var(1, 20_500_000).Build()), Op.Jal));
                    Assert.Equal(ConnectionProtocol.BuildKamas(9_500_000), await b.Next(Op.Ivf));
                    await b.Take(4);   // iwm, izz, jaa, kld
                }

                var sold = HouseStore.Of(Street, DoorElement);
                Assert.Equal(strangerAccount, sold.AccountId);
                Assert.False(sold.Locked);
                Assert.Equal(20_500_000, Jondo.Unity.Server.Managers.Bank.KamasOf(ownerAccount));

                // A hears of it: the door as he sees it now, the plaque, the client's own sentence
                // for a house bought (4/5), and his list of houses, empty.
                Assert.Equal(HouseHandler.DoorChanged(door, sold, ownerAccount), await a.Next(Op.Iwm));
                await a.Next(Op.Izz);
                Assert.Equal(ConnectionProtocol.BuildInfoMessage(4, 5, "20500000", "Stranger"), await a.Next(Op.Lqn));
                Assert.Empty(await a.Next(Op.Jaa));
            }
            finally
            {
                Forget(ownerAccount, strangerAccount);
                Restore(saved);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
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
    /// A house's frames, byte for byte against the captures of "Casas/" -- OwnerAccount's house
    /// 522652, door 522653 of map 212601864, instance 22 -- and the pandala door that asked for a
    /// code; and the store behind them: owners per account, sale, codes, purchase.
    /// </summary>
    [Collection("forgemagic")]
    public class HouseTests
    {
        private static byte[] Hex(string hex) => Convert.FromHexString(hex);

        private const int House = 522652, Door = 522653, Instance = 22;

        private static HouseProtocol.Plaque CapturedPlaque(long price = 0, bool locked = false, byte[]? guild = null)
            => new HouseProtocol.Plaque
            {
                Instance = Instance, OwnerName = "OwnerAccount", OwnerTag = "0001",
                Price = price, Locked = locked, Rooms = 3, Chests = 2, Skills = new long[] { 339 }, GuildBlock = guild,
            };

        // ─── The sale, "poner casa en venta por 20.500.000" ─────────────────────

        [Fact]
        public void The_sale_window_and_the_plaque_are_frames_3_and_5()
        {
            // khr: the Casa grande de Bonta's own price, 2,500,000, while not on sale.
            Assert.Equal(Hex("109cf31f1816200128a0cb9801"), HouseProtocol.BuildSaleWindow(false, House, Instance, 2_500_000));
            // izr: the full plaque, rooms, f10 and f11 included.
            Assert.Equal(Hex("0a242001300342140a0c4f776e65724163636f756e74120430303031480150025a02d3026016"),
                         HouseProtocol.BuildInfo(CapturedPlaque()));
        }

        [Fact]
        public void The_sale_burst_is_frames_13_to_21()
        {
            var off = new[] { (31094, 84), (31096, 100), (31098, 98) };
            var on = new[] { (31094, 84), (31096, 100), (31099, 108) };

            Assert.Equal(Hex("1a210801220608f6f2011054220608f8f2011064220608faf2011062289df31f30ac02"),
                         HouseProtocol.BuildElementChanged(Door, Houses.DoorType, off));                                   // 13
            Assert.Equal(Hex("0a1c200142140a0c4f776e65724163636f756e7412043030303148016016109cf31f1a039df31f"),
                         HouseProtocol.BuildChanged(House, new long[] { Door }, CapturedPlaque()));                           // 14
            Assert.Equal(Hex("1816209cf31f"), HouseProtocol.BuildPutOnSale(Instance, House));                              // 15
            Assert.Equal(Hex("1a210801220608f6f2011054220608f8f2011064220608fbf201106c289df31f30ac02"),
                         HouseProtocol.BuildElementChanged(Door, Houses.DoorType, on));                                    // 16
            Assert.Equal(Hex("0a21200138a09ce30942140a0c4f776e65724163636f756e7412043030303148016016109cf31f1a039df31f"),
                         HouseProtocol.BuildChanged(House, new long[] { Door }, CapturedPlaque(20_500_000)));                 // 17, 20

            var owned = new HouseProtocol.Owned
            {
                HouseId = House, Model = 442, StreetMapId = 212601864, WorldX = -30, WorldY = -53, SubAreaId = 973,
                Plaque = CapturedPlaque(20_500_000),
            };
            Assert.Equal(Hex("0a54109cf31f20ba03324b0a1e088898b06510e2ffffffffffffffff0120cd0728cbffffffffffffffff0112292001300338a09ce30942140a0c4f776e65724163636f756e74120430303031480150025a02d3026016"),
                         HouseProtocol.BuildAccountHouses(new[] { owned }));                                               // 18
            Assert.Equal(Hex("120c4f776e65724163636f756e74189cf31f2a043030303130a09ce3094016"),
                         HouseProtocol.BuildSellingUpdate("OwnerAccount", "0001", House, 20_500_000, Instance));           // 19
            Assert.Equal(Hex("0803"), ConnectionProtocol.BuildDialogClosed(HouseProtocol.SaleClosed));                    // 21
        }

        [Fact]
        public void Taking_it_off_sale_is_the_other_capture()
        {
            // "retirar casa de la venta": the window shows the price it is at, 21,500,000 (frame 3)...
            Assert.Equal(Hex("109cf31f1816200128e0a0a00a"), HouseProtocol.BuildSaleWindow(false, House, Instance, 21_500_000));
            Assert.Equal(Hex("0a292001300338e0a0a00a42140a0c4f776e65724163636f756e74120430303031480150025a02d3026016"),
                         HouseProtocol.BuildInfo(CapturedPlaque(21_500_000)));                                                 // 5

            // ...and afterwards everything goes without a price (frames 22-23).
            var owned = new HouseProtocol.Owned
            {
                HouseId = House, Model = 442, StreetMapId = 212601864, WorldX = -30, WorldY = -53, SubAreaId = 973,
                Plaque = CapturedPlaque(),
            };
            Assert.Equal(Hex("0a4f109cf31f20ba0332460a1e088898b06510e2ffffffffffffffff0120cd0728cbffffffffffffffff0112242001300342140a0c4f776e65724163636f756e74120430303031480150025a02d3026016"),
                         HouseProtocol.BuildAccountHouses(new[] { owned }));
            Assert.Equal(Hex("120c4f776e65724163636f756e74189cf31f2a04303030314016"),
                         HouseProtocol.BuildSellingUpdate("OwnerAccount", "0001", House, 0, Instance));

            // An account with no house: jaa empty, as at the world entry of a new account.
            Assert.Empty(HouseProtocol.BuildAccountHouses(Array.Empty<HouseProtocol.Owned>()));
        }

        /// <summary>
        /// The guild's houses as the world entry of «desde eleccion servidor...» lists them (jjs):
        /// in the oneof's f5, the guild's rights after the plaque -- guildHouse's 505 -- and the plaque
        /// without the guild, though the house is shared with it.
        /// </summary>
        [Fact]
        public void The_guild_s_houses_are_the_capture()
        {
            var guildHouse = new HouseProtocol.Owned
            {
                HouseId = 519601, Model = 424, StreetMapId = 219415040, WorldX = -25, WorldY = 34, SubAreaId = 977,
                GuildRights = 505,
                Plaque = new HouseProtocol.Plaque
                {
                    Instance = 16, OwnerName = "owner2", OwnerTag = "0002", Locked = true, Rooms = 3, Chests = 2, Skills = new long[] { 339 }, GuildBlock = new byte[] { 0x08, 0x01 },
                },
            };
            Assert.Equal(Hex("124510b1db1f20a8032a3c1220200128013003420e0a066f776e657232120430303032480150025a02d302601018f9032a15088084d06810e7ffffffffffffffff0120d1072822"),
                         HouseProtocol.BuildGuildHouses(new[] { guildHouse }));
        }

        /// <summary>«Ir» takes one to his own houses, and to his guild's when they share the teleport.</summary>
        [Fact]
        public void Going_to_a_house_is_for_its_owner_and_its_guild_with_the_teleport()
        {
            var own = new HouseStore.House { MapId = 1, ElementId = 2, Instance = 1, AccountId = 10 };
            Assert.True(HouseHandler.MayTeleportTo(own, 10, 0));
            Assert.False(HouseHandler.MayTeleportTo(own, 11, 0));

            var shared = new HouseStore.House
            {
                MapId = 1, ElementId = 2, Instance = 1, AccountId = 10, GuildId = 5,
                GuildRights = HouseStore.GuildRights.Share | HouseStore.GuildRights.Teleport,
            };
            Assert.True(HouseHandler.MayTeleportTo(shared, 11, 5));
            Assert.False(HouseHandler.MayTeleportTo(shared, 11, 6));
            var sharedWithoutTeleport = new HouseStore.House
            {
                MapId = 1, ElementId = 2, Instance = 1, AccountId = 10, GuildId = 5, GuildRights = HouseStore.GuildRights.Share,
            };
            Assert.False(HouseHandler.MayTeleportTo(sharedWithoutTeleport, 11, 5));
            Assert.False(HouseHandler.MayTeleportTo(new HouseStore.House { MapId = 1, ElementId = 2, Instance = 1 }, 0, 0));
        }

        // ─── The plaque on the map ──────────────────────────────────────────────

        [Fact]
        public void A_locked_plaque_carries_f5()
        {
            // "desde dentro de casa salir a fuera", frame 14: a neighbour's instance 16.
            var plaque = new HouseProtocol.Plaque
            {
                Instance = 16, OwnerName = "ExampleHouseOwnerNumber", OwnerTag = "0003", Locked = true,
            };
            Assert.Equal(Hex("0a2920012801421f0a174578616d706c65486f7573654f776e65724e756d62657212043030303348016010109cf31f1a039df31f"),
                         HouseProtocol.BuildChanged(House, new long[] { Door }, plaque));
        }

        /// <summary>
        /// A house nobody owns is declared on its street too, on sale at its model's price, with an
        /// empty owner block (f8) and neither f4 nor f9: a house its map never declared, the client
        /// drops the buyer's khr for, and the buy window did not open.
        /// </summary>
        [Fact]
        public void A_house_nobody_owns_is_on_sale_at_its_models_price()
        {
            var door = new Houses.Door(212601864, Door, 300, 0, 0, 407, "", 5_000_000, 3, 1);
            var plaque = HouseHandler.PlaqueOf(new HouseStore.House(), door);
            Assert.Equal(5_000_000, plaque.Price);
            Assert.False(plaque.HasOwner);

            var lnx = ProtoMessage.Parse(HouseProtocol.Build(plaque, false).Build());
            Assert.DoesNotContain(lnx.Fields, f => f.FieldNumber == 4 || f.FieldNumber == 9);
            Assert.Empty(Assert.Single(lnx.Fields, f => f.FieldNumber == 8).BytesValue);
            Assert.Equal(5_000_000, Assert.Single(lnx.Fields, f => f.FieldNumber == 7).VarIntValue);

            // One that has an owner and is not on sale asks nothing.
            Assert.Equal(0, HouseHandler.PlaqueOf(new HouseStore.House { AccountId = 7 }, door).Price);
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
            Assert.Equal(Hex("080110edf11f20692881f092cbdd08"),
                         ConnectionProtocol.BuildElementInUse(522477, Houses.ChestLockSkill, 300000000001));
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
            Assert.Equal(new[] { 84, 100, 98 }, HouseHandler.DoorSkillsFor(new[] { mine }, 7));
            Assert.Equal(new[] { 84, 100, 108 }, HouseHandler.DoorSkillsFor(new[] { onSale }, 7));

            // A stranger: enter alone at a house not on sale (pandala); enter and buy otherwise.
            Assert.Equal(new[] { 84 }, HouseHandler.DoorSkillsFor(new[] { mine }, 8));
            Assert.Equal(new[] { 84, 97 }, HouseHandler.DoorSkillsFor(new[] { onSale }, 8));
            Assert.Equal(new[] { 84, 97 }, HouseHandler.DoorSkillsFor(new[] { nobody }, 8));

            // A building every copy of which is owned -- the capture's eleven --: its owner is
            // offered no 97, and a copy somebody else sells is his to buy.
            var other = new HouseStore.House { Instance = 2, AccountId = 8 };
            Assert.Equal(new[] { 84, 100, 98 }, HouseHandler.DoorSkillsFor(new[] { mine, other }, 7));
            var otherOnSale = new HouseStore.House { Instance = 2, AccountId = 8, Price = 1 };
            Assert.Equal(new[] { 84, 100, 98, 97 }, HouseHandler.DoorSkillsFor(new[] { mine, otherOnSale }, 7));
            Assert.Equal(new[] { 84, 100, 108, 97 }, HouseHandler.DoorSkillsFor(new[] { onSale, otherOnSale }, 8));

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
                Assert.False(HouseStore.Of(StoreMap, StoreDoor, Houses.FirstInstance).Owned);
                Assert.Equal(2_500_000, HouseStore.AskingPrice(HouseStore.Of(StoreMap, StoreDoor, Houses.FirstInstance), 2_500_000));

                // Short of kamas: nothing happens.
                var (none, why) = await HouseStore.BuyAsync(StoreMap, StoreDoor, Houses.FirstInstance, 2_500_000, buyer, character, 1_000, 2_500_000);
                Assert.Null(none);
                Assert.Equal(HouseStore.Refusal.NotEnoughKamas, why);

                // At another price than the window showed: nothing either.
                (none, why) = await HouseStore.BuyAsync(StoreMap, StoreDoor, Houses.FirstInstance, 2_500_000, buyer, character, 9_000_000, 2_000_000);
                Assert.Equal(HouseStore.Refusal.PriceChanged, why);

                var (done, ok) = await HouseStore.BuyAsync(StoreMap, StoreDoor, Houses.FirstInstance, 2_500_000, buyer, character, 3_000_000, 2_500_000);
                Assert.Equal(HouseStore.Refusal.None, ok);
                Assert.Equal(500_000, done!.BuyerKamas);
                Assert.Equal(0, done.SellerAccountId);

                var house = HouseStore.Of(StoreMap, StoreDoor, Houses.FirstInstance);
                Assert.Equal(buyer, house.AccountId);
                Assert.False(house.ForSale);
                Assert.Single(HouseStore.OwnedBy(buyer));

                // One's own house is not bought again, and a house not on sale is not bought at all.
                (none, why) = await HouseStore.BuyAsync(StoreMap, StoreDoor, Houses.FirstInstance, 2_500_000, buyer, character, 9_000_000, 2_500_000);
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
                await HouseStore.BuyAsync(StoreMap, StoreDoor, Houses.FirstInstance, 2_500_000, seller, sellerCharacter, 2_500_000, 2_500_000);

                // Only the owner sets the price, the codes; a stranger changes nothing.
                Assert.False(await HouseStore.SetPriceAsync(StoreMap, StoreDoor, Houses.FirstInstance, buyer, 1));
                Assert.False(await HouseStore.SetAccessCodeAsync(StoreMap, StoreDoor, Houses.FirstInstance, buyer, "1234"));
                Assert.False(await HouseStore.SetChestCodeAsync(StoreMap, StoreDoor, Houses.FirstInstance, 522477, buyer, "1234"));

                Assert.True(await HouseStore.SetAccessCodeAsync(StoreMap, StoreDoor, Houses.FirstInstance, seller, "13581321"));
                Assert.True(await HouseStore.SetChestCodeAsync(StoreMap, StoreDoor, Houses.FirstInstance, 522477, seller, "1358"));
                Assert.True(HouseStore.Of(StoreMap, StoreDoor, Houses.FirstInstance).Locked);
                Assert.Equal("1358", HouseStore.ChestCodeOf(StoreMap, StoreDoor, Houses.FirstInstance, 522477));

                // The chest's Accept with nothing typed takes its code off.
                Assert.True(await HouseStore.SetChestCodeAsync(StoreMap, StoreDoor, Houses.FirstInstance, 522477, seller, ""));
                Assert.Equal("", HouseStore.ChestCodeOf(StoreMap, StoreDoor, Houses.FirstInstance, 522477));
                Assert.True(await HouseStore.SetChestCodeAsync(StoreMap, StoreDoor, Houses.FirstInstance, 522477, seller, "1358"));

                // Something in a chest.
                var chest = StorageStacks.HouseChest(StoreMap, StoreDoor, Houses.FirstInstance, 522477);
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
                Assert.True(await HouseStore.SetPriceAsync(StoreMap, StoreDoor, Houses.FirstInstance, seller, 21_500_000));
                Assert.True(await HouseStore.SetPriceAsync(StoreMap, StoreDoor, Houses.FirstInstance, seller, 0));
                Assert.False(HouseStore.Of(StoreMap, StoreDoor, Houses.FirstInstance).ForSale);
                Assert.True(await HouseStore.SetPriceAsync(StoreMap, StoreDoor, Houses.FirstInstance, seller, 20_500_000));
                Assert.Equal(20_500_000, HouseStore.AskingPrice(HouseStore.Of(StoreMap, StoreDoor, Houses.FirstInstance), 2_500_000));

                var (done, why) = await HouseStore.BuyAsync(StoreMap, StoreDoor, Houses.FirstInstance, 2_500_000, buyer, buyerCharacter, 30_000_000, 20_500_000);
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
                var house = HouseStore.Of(StoreMap, StoreDoor, Houses.FirstInstance);
                Assert.Equal(buyer, house.AccountId);
                Assert.False(house.Locked);
                Assert.False(house.ForSale);
                Assert.Equal("", HouseStore.ChestCodeOf(StoreMap, StoreDoor, Houses.FirstInstance, 522477));
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
                await HouseStore.BuyAsync(StoreMap, StoreDoor, Houses.FirstInstance, 2_500_000, seller, 8_820_100_030, 2_500_000, 2_500_000);
                Assert.True(await HouseStore.SetPriceAsync(StoreMap, StoreDoor, Houses.FirstInstance, seller, 1_000));
                Assert.True(await Bank.AddKamasAsync(seller, long.MaxValue));

                var (done, why) = await HouseStore.BuyAsync(StoreMap, StoreDoor, Houses.FirstInstance, 2_500_000, buyer, 8_820_100_031, 5_000, 1_000);
                Assert.Null(done);
                Assert.Equal(HouseStore.Refusal.SellerBankFull, why);

                var house = HouseStore.Of(StoreMap, StoreDoor, Houses.FirstInstance);
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
            HouseStore.Of(Street, DoorElement, Houses.FirstInstance);   // the tables
            StorageStacks.EnsureTables();
            var saved = new List<(string, object?[])>();
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            foreach (var (table, where) in new[]
            {
                ("Houses", "MapId = $m AND ElementId = $e"),
                ("HouseChestCodes", "MapId = $m AND ElementId = $e"),
                ("StorageItems", "substr(Storage, 1, length($p)) = $p OR substr(Storage, 1, length($c)) = $c"),
            })
            {
                using var read = connection.CreateCommand();
                read.CommandText = $"SELECT * FROM {table} WHERE {where};";
                read.Parameters.AddWithValue("$m", Street);
                read.Parameters.AddWithValue("$e", DoorElement);
                read.Parameters.AddWithValue("$p", StorageStacks.HouseChestsPrefix(Street, DoorElement, Houses.FirstInstance));
                read.Parameters.AddWithValue("$c", $"house:{Street}:{DoorElement}/");
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

        /// <summary>
        /// Door 522653 stands on three maps of Bonta: clicked from any, the same house, kept under
        /// the lowest, with one house id.
        /// </summary>
        [Fact]
        public void A_door_on_three_maps_is_one_house()
        {
            if (MapManager.GetMapInfo(212601864) == null) MapManager.Initialize();   // Bonta's street
            Houses.Initialize();
            var maps = Houses.MapsOfDoor(DoorElement);
            if (maps.Count < 2) return;
            Assert.All(maps, m => Assert.Equal(maps.Min(), Houses.CanonicalMapOf(m, DoorElement)));
            Assert.Single(maps.Select(m => Houses.HouseIdOf(m, DoorElement)).Distinct());
        }

        /// <summary>
        /// The Lacs enchantés' palace is its model, 314, and its plaque counts the chests of its
        /// fifteen rooms and the skills of their workshops: it said one room, 2 chests and none.
        /// </summary>
        [Fact]
        public void The_palace_plaque_is_its_fifteen_rooms()
        {
            if (MapManager.GetMapInfo(75629314) == null) MapManager.Initialize();
            Interactives.Initialize();
            Workshops.Initialize();
            Houses.Initialize();
            Assert.True(Houses.TryGetDoor(75629314, 475545, out var palace));
            Assert.Equal(314, palace.Model);

            var plaque = HouseHandler.PlaqueOf(new HouseStore.House(), palace);
            Assert.Equal(15, plaque.Rooms);
            Assert.Equal(60_000_000, plaque.Price);
            Assert.Equal(Houses.ChestCountOf(palace.InteriorMapId), plaque.Chests);
            Assert.True(plaque.Chests > 2, $"{plaque.Chests} chests");
            Assert.True(plaque.Skills.Count > 1, $"{plaque.Skills.Count} workshop skills");
            Assert.Equal(TeleportManager.ExitSkill, plaque.Skills[0]);
        }

        /// <summary>
        /// The house of the sale captures, from the data: its three rooms hold two chests and no
        /// workshop, so its plaque is 2 and [339] -- what the izr of «poner casa en venta» carries.
        /// </summary>
        [Fact]
        public void The_measured_house_plaque_comes_out_of_the_data()
        {
            if (MapManager.GetMapInfo(Street) == null) MapManager.Initialize();
            Interactives.Initialize();
            Workshops.Initialize();
            Houses.Initialize();
            Assert.True(Houses.TryGetDoor(Street, DoorElement, out var door));

            var plaque = HouseHandler.PlaqueOf(new HouseStore.House(), door);
            Assert.Equal(3, plaque.Rooms);
            Assert.Equal(2, plaque.Chests);
            Assert.Equal(new long[] { 339 }, plaque.Skills);
        }

        /// <summary>
        /// The doors lead to the client's own houses -- an indoor map with a house chest, and the
        /// rooms the world graph joins to it, of the door's own subarea or area --, and the two
        /// doors the captures go through to where they go in.
        /// </summary>
        [Fact]
        public void Doors_lead_to_the_client_s_own_houses()
        {
            if (MapManager.GetMapInfo(212601864) == null) MapManager.Initialize();   // Bonta's street
            // Read once: reloading it empties the table the MapManager collection's tests read.
            if (Interactives.ElementsOf(Street).Count == 0) Interactives.Initialize();
            Houses.Initialize();
            Assert.True(Houses.TryGetDoor(Street, DoorElement, out var bonta));
            Assert.Equal(213124106, bonta.InteriorMapId);                   // "entrar en mi casa"
            Assert.True(Houses.TryGetDoor(219415040, 522198, out var brakmar));
            Assert.Equal(212339722, brakmar.InteriorMapId);                 // the Brakmar guild house

            var doors = Houses.AllHouses().ToList();
            int withChest = doors.Count(d => Houses.ChestCountOf(d.InteriorMapId) > 0);
            Assert.True(withChest >= 740, $"{withChest} of {doors.Count} houses have a chest");

            // A Frigost house of the burgo: its own subarea, with the rooms behind its stairs.
            var frigost = doors.First(d => MapManager.GetMapInfo(d.MapId)?.SubAreaId == 601);
            Assert.Equal(601, MapManager.GetMapInfo(frigost.InteriorMapId)?.SubAreaId);
        }

        /// <summary>
        /// The «Casa mediana de Astrub» of 5,-17 is left by its sun, element 514729. With an f15
        /// the client did not draw it, and the house had no way out.
        /// </summary>
        [Fact]
        public void The_way_out_is_declared_without_a_state()
        {
            const long interior = 192021507;
            const int sun = 514729;
            if (MapManager.GetMapInfo(interior) == null) MapManager.Initialize();
            if (MapManager.GetMapInfo(interior) == null) return;   // no world data
            Interactives.Initialize();
            Houses.Initialize();
            InteractiveRegistry.Initialize();

            var me = new DbCharacter { Id = 990_000_801, Name = "Visita", Level = 200, Breed = 1 };
            var session = GameSession.SinSocket();
            session.BindAccount(990_000_801, 1);
            session.State.CharacterId = me.Id;
            session.State.MapId = interior;
            session.State.CellId = 429;
            session.State.Orientation = 1;
            var snapshot = MapInformationMessages.Live.Snapshot(session, me);
            Assert.Contains(snapshot.InteractiveElements, element => element.ElementId == sun);
            Assert.DoesNotContain(snapshot.StatedElements, element => element.ElementId == sun);
        }

        /// <summary>The small house of Sufokia, 17,29, has two suns: both lead out.</summary>
        [Fact]
        public void Every_sun_of_the_way_out_leads_out()
        {
            const long interior = 90966529;
            if (MapManager.GetMapInfo(interior) == null) MapManager.Initialize();
            if (MapManager.GetMapInfo(interior) == null) return;   // no world data
            Interactives.Initialize();
            Houses.Initialize();
            InteractiveRegistry.Initialize();

            foreach (int sun in new[] { 505761, 505762 })
            {
                var declared = Assert.Single(InteractiveRegistry.OnMap(interior), i => i.Element.Id == sun);
                Assert.Equal(InteractiveActionKind.HouseExit, Assert.Single(declared.Actions).Kind);
            }
        }

        [Fact]
        public async Task An_owner_sells_and_locks_and_a_stranger_knocks_and_buys()
        {
            if (MapManager.GetMapInfo(212601864) == null) MapManager.Initialize();   // Bonta's street
            Houses.Initialize();
            Assert.True(Houses.TryGetDoor(Street, DoorElement, out var door));
            Assert.True(door.IsOwnable);
            Assert.Equal(442, door.Model);
            Assert.Equal(2_500_000, door.Price);
            Assert.Equal(3, door.Rooms);
            Assert.Equal(24, door.Copies);   // the capture's eleven owners went up to copy 24

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

                    await HouseHandler.UseDoorAsync(a.ToClient, interactive, Skill(interactive, Houses.BuySkill), Houses.FirstInstance);
                    await a.Next(Op.Iwn);
                    Assert.Equal(HouseProtocol.BuildSaleWindow(true, houseId, Houses.FirstInstance, 2_500_000), await a.Next(Op.Khr));

                    // A number that is not the price buys nothing.
                    Assert.False(await HouseHandler.BuyAsync(a.ToClient, Frame(Op.Jal, Pb.New().Var(1, 1234).Build()), Op.Jal));

                    Assert.True(await HouseHandler.BuyAsync(a.ToClient, Frame(Op.Jad, Pb.New().Var(1, 2_500_000).Build()), Op.Jad));
                    Assert.Equal(ConnectionProtocol.BuildKamas(500_000), await a.Next(Op.Ivf));
                    // «Has perdido 2.500.000 kamas.» in the buyer's chat.
                    Assert.Equal(ConnectionProtocol.BuildSystemMessage(InfoMessages.KamasLost, "2500000"), await a.Next(Op.Lqn));
                    Assert.Equal(HouseHandler.DoorChanged(door, ownerAccount), await a.Next(Op.Iwm));
                    await a.Next(Op.Izz);
                    Assert.NotEmpty(await a.Next(Op.Jaa));
                    Assert.Equal(Hex("0803"), await a.Next(Op.Kld));
                    Assert.True(await HouseHandler.CloseDialogAsync(a.ToClient));   // the kla behind: nothing more
                    Assert.Equal(500_000, a.Session.State.Kamas);
                }
                Assert.Equal(ownerAccount, HouseStore.Of(Street, DoorElement, Houses.FirstInstance).AccountId);
                // B, on the street, sees the door become somebody's: enter alone.
                Assert.Equal(HouseHandler.DoorChanged(door, strangerAccount), await b.Next(Op.Iwm));
                await b.Next(Op.Izz);

                // ── A puts it on sale: the sale capture's frames 2-21. He is offered 97 too: the
                // building's second copy is free, which the capture's -- every copy owned -- was not.
                using (SessionContext.Push(a.Session))
                {
                    Assert.Equal(new[] { 84, 100, 98, 97 }, HouseHandler.VisibleActions(interactive, ownerAccount).Select(x => x.SkillId));

                    await HouseHandler.UseDoorAsync(a.ToClient, interactive, Skill(interactive, Houses.SellSkill), Houses.FirstInstance);
                    Assert.Equal(ConnectionProtocol.BuildElementInUse(DoorElement, 98, owner), await a.Next(Op.Iwn));
                    Assert.Equal(HouseProtocol.BuildSaleWindow(false, houseId, Houses.FirstInstance, 2_500_000), await a.Next(Op.Khr));

                    Assert.True(await HouseHandler.InfoAsync(a.ToClient, Frame(Op.Izv, Pb.New().Var(1, Houses.FirstInstance).Var(2, houseId).Build())));
                    var info = ProtoMessage.Parse(await a.Next(Op.Izr)).Fields.Single();
                    Assert.Equal(1, info.FieldNumber);

                    Assert.True(await HouseHandler.SellAsync(a.ToClient, Frame(Op.Jan, Hex("08a09ce30910011801"))));
                    var burst = await a.Take(9);
                    Assert.Equal(new[] { Op.Iwm, Op.Izz, Op.Jjt, Op.Iwm, Op.Izz, Op.Jaa, Op.Izu, Op.Izz, Op.Kld },
                                 burst.Select(f => f.Opcode));
                    Assert.Equal(HouseProtocol.BuildPutOnSale(Houses.FirstInstance, houseId), burst[2].Payload);
                    Assert.Equal(Hex("0803"), burst[8].Payload);
                    Assert.True(await HouseHandler.CloseDialogAsync(a.ToClient));
                }
                Assert.Equal(20_500_000, HouseStore.Of(Street, DoorElement, Houses.FirstInstance).Price);
                await b.Take(2);   // iwm, izz

                // ── A sets the access code: frames 5-18 of "cambiar codigo acceso".
                using (SessionContext.Push(a.Session))
                {
                    Assert.Equal(new[] { 84, 100, 108, 97 }, HouseHandler.VisibleActions(interactive, ownerAccount).Select(x => x.SkillId));

                    await HouseHandler.UseDoorAsync(a.ToClient, interactive, Skill(interactive, Houses.CodeSkill), Houses.FirstInstance);
                    await a.Next(Op.Iwn);
                    Assert.Equal(Hex("1808"), await a.Next(Op.Kia));
                    Assert.True(await HouseHandler.ChangeCodeAsync(a.ToClient, Frame(Op.Khv, Hex("0a0431333538"))));
                    await a.Next(Op.Iwm);
                    var locked = ProtoMessage.Parse(ProtoMessage.Parse(await a.Next(Op.Izz)).Fields.First(f => f.FieldNumber == 1).BytesValue).Fields;
                    Assert.Contains(locked, f => f.FieldNumber == 5 && f.VarIntValue == 1);
                    Assert.Empty(await a.Next(Op.Khu));
                    Assert.Equal(Hex("0802"), await a.Next(Op.Kld));
                }
                Assert.Equal("1358", HouseStore.Of(Street, DoorElement, Houses.FirstInstance).AccessCode);
                await b.Take(2);

                // ── B knocks: the keypad, a wrong code (pandala 3882-3890), then the right one.
                using (SessionContext.Push(b.Session))
                {
                    Assert.Equal(new[] { 84, 97 }, HouseHandler.VisibleActions(interactive, strangerAccount).Select(x => x.SkillId));

                    await HouseHandler.UseDoorAsync(b.ToClient, interactive, Skill(interactive, Houses.EnterSkill), Houses.FirstInstance);
                    await b.Next(Op.Iwn);
                    Assert.Equal(Hex("08011808"), await b.Next(Op.Kia));
                    Assert.True(await HouseHandler.UseCodeAsync(b.ToClient, Frame(Op.Khw, Hex("0a0431323334"))));
                    Assert.Equal(Hex("0802"), await b.Next(Op.Kld));
                    Assert.Equal(Hex("1001"), await b.Next(Op.Khu));
                    Assert.Equal(Street, b.Session.State.MapId);

                    await HouseHandler.UseDoorAsync(b.ToClient, interactive, Skill(interactive, Houses.EnterSkill), Houses.FirstInstance);
                    await b.Next(Op.Iwn);
                    await b.Next(Op.Kia);
                    Assert.True(await HouseHandler.UseCodeAsync(b.ToClient, Frame(Op.Khw, Hex("0a0431333538"))));
                    // The right code: the keypad closes and in he goes, no khu -- an empty one is
                    // the owner's «Código modificado».
                    Assert.Equal(Hex("0802"), await b.Next(Op.Kld));
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
                    await HouseHandler.UseDoorAsync(b.ToClient, interactive, Skill(interactive, Houses.BuySkill), Houses.FirstInstance);
                    await b.Next(Op.Iwn);
                    Assert.Equal(HouseProtocol.BuildSaleWindow(true, houseId, Houses.FirstInstance, 20_500_000), await b.Next(Op.Khr));
                    Assert.True(await HouseHandler.BuyAsync(b.ToClient, Frame(Op.Jal, Pb.New().Var(1, 20_500_000).Build()), Op.Jal));
                    Assert.Equal(ConnectionProtocol.BuildKamas(9_500_000), await b.Next(Op.Ivf));
                    Assert.Equal(ConnectionProtocol.BuildSystemMessage(InfoMessages.KamasLost, "20500000"), await b.Next(Op.Lqn));
                    await b.Take(4);   // iwm, izz, jaa, kld
                }

                var sold = HouseStore.Of(Street, DoorElement, Houses.FirstInstance);
                Assert.Equal(strangerAccount, sold.AccountId);
                Assert.False(sold.Locked);
                Assert.Equal(20_500_000, Jondo.Unity.Server.Managers.Bank.KamasOf(ownerAccount));

                // A hears of it: the door as he sees it now, the plaque, the client's own sentence
                // for a house bought (4/5), and his list of houses, empty.
                Assert.Equal(HouseHandler.DoorChanged(door, ownerAccount), await a.Next(Op.Iwm));
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

        /// <summary>
        /// Two owners of one building, as the Casa grande de Bonta had eleven: A buys its first copy,
        /// B the second -- the one the street offers him --, each goes into his own copy of the same
        /// interior with his own chests, and neither can sell the other's.
        /// </summary>
        [Fact]
        public async Task Two_accounts_own_two_copies_of_one_building()
        {
            if (MapManager.GetMapInfo(212601864) == null) MapManager.Initialize();   // Bonta's street
            Houses.Initialize();
            Assert.True(Houses.TryGetDoor(Street, DoorElement, out var door));

            const long accountA = 8_840_000_011, accountB = 8_840_000_012;
            const long characterA = 8_840_100_011, characterB = 8_840_100_012;
            var saved = Save();
            Forget(accountA, accountB);

            int houseId = Houses.HouseIdOf(Street, DoorElement);
            var interactive = DoorOf(door);

            await using var a = await ClientPipe.OpenAsync(accountA, characterA, Street, "First");
            await using var b = await ClientPipe.OpenAsync(accountB, characterB, Street, "Second");
            a.Session.State.Kamas = 3_000_000;
            b.Session.State.Kamas = 3_000_000;
            try
            {
                // Nobody's yet: the street shows its first copy alone, free.
                Assert.Equal(new[] { 1 }, HouseStore.CopiesOf(Street, DoorElement, door.Copies).Select(h => h.Instance));

                using (SessionContext.Push(a.Session))
                {
                    await HouseHandler.UseDoorAsync(a.ToClient, interactive, Skill(interactive, Houses.BuySkill), 1);
                    await a.Next(Op.Iwn);
                    Assert.Equal(HouseProtocol.BuildSaleWindow(true, houseId, 1, 2_500_000), await a.Next(Op.Khr));
                    Assert.True(await HouseHandler.BuyAsync(a.ToClient, Frame(Op.Jad, Pb.New().Var(1, 2_500_000).Build()), Op.Jad));
                    await a.Take(5);   // ivf, iwm, izz, jaa, kld
                }
                await b.Take(2);       // iwm, izz

                // A's copy and the next one, free: B is offered enter and buy.
                var copies = HouseStore.CopiesOf(Street, DoorElement, door.Copies);
                Assert.Equal(new[] { 1, 2 }, copies.Select(h => h.Instance));
                Assert.Equal(new[] { accountA, 0L }, copies.Select(h => h.AccountId));
                Assert.Equal(new[] { 84, 97 }, HouseHandler.VisibleActions(interactive, accountB).Select(x => x.SkillId));

                using (SessionContext.Push(b.Session))
                {
                    // A's copy is not on sale: buying it opens nothing.
                    await HouseHandler.UseDoorAsync(b.ToClient, interactive, Skill(interactive, Houses.BuySkill), 1);
                    Assert.Null(b.Session.State.HouseDialog);

                    await HouseHandler.UseDoorAsync(b.ToClient, interactive, Skill(interactive, Houses.BuySkill), 2);
                    await b.Next(Op.Iwn);
                    Assert.Equal(HouseProtocol.BuildSaleWindow(true, houseId, 2, 2_500_000), await b.Next(Op.Khr));
                    Assert.True(await HouseHandler.BuyAsync(b.ToClient, Frame(Op.Jad, Pb.New().Var(1, 2_500_000).Build()), Op.Jad));
                    await b.Take(5);

                    // Selling A's copy is not B's to do: no window opens.
                    var bought = b.Session.State.HouseDialog;
                    await HouseHandler.UseDoorAsync(b.ToClient, interactive, Skill(interactive, Houses.SellSkill), 1);
                    Assert.Same(bought, b.Session.State.HouseDialog);
                }
                await a.Take(2);

                Assert.Equal(accountA, HouseStore.Of(Street, DoorElement, 1).AccountId);
                Assert.Equal(accountB, HouseStore.Of(Street, DoorElement, 2).AccountId);
                Assert.Equal(new[] { 1, 2, 3 }, HouseStore.CopiesOf(Street, DoorElement, door.Copies).Select(h => h.Instance));

                // The typed street snapshot keeps independently owned copies and the next free one.
                var house = Assert.Single(MapHouseMessages.OnMap(Street), h => h.HouseId == houseId);
                var plaques = Assert.IsType<HouseInformationCaseHouseOnMap>(house.InformationCase).Value;
                Assert.Equal(new[] { DoorElement }, plaques.DoorsOnMap);
                Assert.Equal(new[] { 1, 2, 3 }, plaques.HousesInformation.Select(p => p.InstanceId));
                Assert.Equal(new[] { true, true, false }, plaques.HousesInformation.Select(p => p.HasOwner));
                Assert.Null(plaques.HousesInformation[0].Price);
                Assert.Null(plaques.HousesInformation[1].Price);
                Assert.Equal(2_500_000, plaques.HousesInformation[2].Price);

                // Each goes into his own copy: same interior map, different houses.
                using (SessionContext.Push(a.Session))
                {
                    await HouseHandler.UseDoorAsync(a.ToClient, interactive, Skill(interactive, Houses.EnterSkill), 1);
                }
                using (SessionContext.Push(b.Session))
                {
                    await HouseHandler.UseDoorAsync(b.ToClient, interactive, Skill(interactive, Houses.EnterSkill), 2);
                }
                Assert.Equal(door.InteriorMapId, a.Session.State.MapId);
                Assert.Equal(door.InteriorMapId, b.Session.State.MapId);
                Assert.Equal((Street, DoorElement, 1), HouseHandler.HouseOf(a.Session.State));
                Assert.Equal((Street, DoorElement, 2), HouseHandler.HouseOf(b.Session.State));
                using (SessionContext.Push(a.Session))
                {
                    Assert.DoesNotContain(SessionRegistry.OnMap(door.InteriorMapId), s => s.CharacterId == characterB);
                }

                // Their chests are not the same chests.
                Assert.NotEqual(StorageStacks.HouseChest(Street, DoorElement, 1, 522477).Key,
                                StorageStacks.HouseChest(Street, DoorElement, 2, 522477).Key);
            }
            finally
            {
                Forget(accountA, accountB);
                Restore(saved);
            }
        }
    }
}

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Who comes into a house and opens its chests: its owner; its guild when the house is shared
    /// and the rights say so; anybody else with the code -- and a house without one lets nobody
    /// else in.
    /// </summary>
    public class HouseAccessTests
    {
        private const long Owner = 10, Stranger = 20, Guild = 5;

        private static HouseStore.House Owned(string code = "", long guild = 0, int rights = 0)
            => new() { AccountId = Owner, AccessCode = code, GuildId = guild, GuildRights = rights };

        [Fact]
        public void Only_the_owner_walks_into_his_house()
        {
            Assert.Equal(HouseHandler.Access.In, HouseHandler.EnterAccess(new HouseStore.House(), Stranger, 0));
            Assert.Equal(HouseHandler.Access.In, HouseHandler.EnterAccess(Owned(), Owner, 0));
            Assert.Equal(HouseHandler.Access.Keypad, HouseHandler.EnterAccess(Owned(), Stranger, 0));
            Assert.Equal(HouseHandler.Access.Keypad, HouseHandler.EnterAccess(Owned("1234"), Stranger, Guild));
        }

        [Fact]
        public void A_house_without_a_code_opens_to_no_code()
        {
            Assert.False(HouseHandler.CodeOpens(Owned(), 0, ""));
            Assert.False(HouseHandler.CodeOpens(Owned(), 0, "1234"));
            Assert.True(HouseHandler.CodeOpens(Owned("1234"), 0, "1234"));
            Assert.False(HouseHandler.CodeOpens(Owned("1234"), 0, "4321"));
        }

        [Fact]
        public void The_guild_comes_in_as_its_rights_say()
        {
            var open = Owned(guild: Guild, rights: HouseStore.GuildRights.Share | HouseStore.GuildRights.HouseGuild);
            Assert.Equal(HouseHandler.Access.In, HouseHandler.EnterAccess(open, Stranger, Guild));
            Assert.Equal(HouseHandler.Access.Keypad, HouseHandler.EnterAccess(open, Stranger, Guild + 1));

            var closed = Owned("1234", Guild, HouseStore.GuildRights.Share | HouseStore.GuildRights.HouseNoGuild);
            Assert.Equal(HouseHandler.Access.Keypad, HouseHandler.EnterAccess(closed, Stranger, Guild));
            Assert.True(HouseHandler.CodeOpens(closed, Guild, "1234"));
            Assert.False(HouseHandler.CodeOpens(closed, Guild + 1, "1234"));
        }

        [Fact]
        public void The_chests_follow_the_guild_rights()
        {
            var shared = Owned(guild: Guild, rights: HouseStore.GuildRights.ChestGuild | HouseStore.GuildRights.ChestNoGuild);
            Assert.Equal(HouseHandler.ChestAccess.Open, HouseHandler.ChestAccessFor(shared, Owner, 0, "99"));
            Assert.Equal(HouseHandler.ChestAccess.Open, HouseHandler.ChestAccessFor(shared, Stranger, Guild, "99"));
            Assert.Equal(HouseHandler.ChestAccess.Refused, HouseHandler.ChestAccessFor(shared, Stranger, 0, ""));
            Assert.Equal(HouseHandler.ChestAccess.Keypad, HouseHandler.ChestAccessFor(Owned(), Stranger, 0, "99"));
            Assert.Equal(HouseHandler.ChestAccess.Open, HouseHandler.ChestAccessFor(Owned(), Stranger, 0, ""));
        }

        /// <summary>A house the guild loses (jjt): { f3: instance, f4: house }, as the sale capture's frame 15.</summary>
        [Fact]
        public void A_house_leaves_the_guild_with_jjt()
            => Assert.Equal(Convert.FromHexString("1816209cf31f"), HouseProtocol.BuildGuildHouseGone(522652, 22));

        /// <summary>jak { f1: instance, f3: rights, f4: house }, the owner's house 522652, instance 22.</summary>
        [Fact]
        public void The_guild_rights_travel_in_jak()
            => Assert.Equal(Convert.FromHexString("081618092" + "09cf31f"),
                            HouseProtocol.BuildGuildRights(522652, 22, null, 9));
    }
}

namespace Jondo.Unity.Tests.World
{
    /// <summary>The estate agency: the houses on sale, searched, ordered and paged as the client asks.</summary>
    public class EstateAgencyTests
    {
        private static EstateAgencyHandler.Listing House(int id, long price, int rooms, int chests, int area, string name)
            => new(id, Houses.FirstInstance, 400 + id, name, price, rooms, chests, 100 + area, area, 0, 0, 0, false);

        private static readonly EstateAgencyHandler.Listing[] Street =
        {
            House(1, 1_000_000, 1, 0, 7, "Casa pequeña de Bonta"),
            House(2, 2_500_000, 3, 2, 7, "Casa grande de Bonta"),
            House(3, 2_000_000, 2, 1, 48, "Casa mediana del burgo de Frigost"),
            House(4, 5_000_000, 4, 4, 11, "Casa gigantesca de herrero"),
        };

        [Fact]
        public void A_search_keeps_what_it_asks_for()
        {
            var any = EstateAgencyHandler.Search(Street, EstateAgencyHandler.Filter.Any);
            Assert.Equal(new[] { 1, 3, 2, 4 }, any.Select(h => h.HouseId));

            var bonta = EstateAgencyHandler.Search(Street, EstateAgencyHandler.Filter.Any with { Area = 7, Rooms = 2 });
            Assert.Equal(new[] { 2 }, bonta.Select(h => h.HouseId));

            var cheap = EstateAgencyHandler.Search(Street, EstateAgencyHandler.Filter.Any with { MaxPrice = 2_000_000, Order = EstateAgencyHandler.Order.PriceDown });
            Assert.Equal(new[] { 3, 1 }, cheap.Select(h => h.HouseId));

            Assert.Empty(EstateAgencyHandler.Search(Street, EstateAgencyHandler.Filter.Any with { Skill = 15 }));
        }

        [Fact]
        public void Pages_start_at_one_and_there_is_always_one()
        {
            var many = Enumerable.Range(1, 23).Select(i => House(i, i, 1, 0, 1, "x")).ToList();
            var (first, pages) = EstateAgencyHandler.PageOf(many, 1);
            Assert.Equal(3, pages);
            Assert.Equal(EstateAgencyHandler.PageSize, first.Count);
            Assert.Equal(3, EstateAgencyHandler.PageOf(many, 3).Page.Count);
            Assert.Equal(1, EstateAgencyHandler.PageOf(new List<EstateAgencyHandler.Listing>(), 5).Pages);
        }
    }
}

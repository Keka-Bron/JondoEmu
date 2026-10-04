using System;
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
    /// The storages that are not the bank -- bins, house chests, the haven bag chest -- against
    /// their captures: the frames that open them, and the moves, one unit per -1, a new uid on
    /// each side, the storage's stack sent again with what is left.
    /// </summary>
    [Collection("forgemagic")]
    public class StorageTests
    {
        private static byte[] Hex(string hex) => Convert.FromHexString(hex);

        private static HavenBagStore.StoredItem Stack(int gid, int quantity, long uid)
            => new HavenBagStore.StoredItem { Gid = gid, Quantity = quantity, Uid = uid, Effects = "" };

        // ─── The frames ─────────────────────────────────────────────────────────

        [Fact]
        public void Each_storage_opens_with_its_own_kind()
        {
            // House chest, frame 8 of "abrir cofre de la casa-mover items-cerrarlo".
            Assert.Equal(Hex("08641804"), StorageProtocol.BuildHouseChestOpened());
            // Bin, frame 12 of "abrir papelera frente a banco bonta y sacar cosas".
            Assert.Equal(Hex("08641811"), StorageProtocol.BuildBinOpened());
            // Haven bag, frame 6 of "abrir cofre de mi merkasako": not the house chest's 4.
            Assert.Equal(Hex("08ffffffff071813"), StorageProtocol.BuildHavenBagOpened());
        }

        [Fact]
        public void The_bin_s_frames_are_the_capture()
        {
            // Frames 22-23: the one unit of 410 arrives in the bag under a new uid, and goes.
            Assert.Equal(Hex("1a0f083f2a0b089a0318012098caa08002"), ConnectionProtocol.BuildItemArrived(3, Stack(410, 1, 537404696)));
            Assert.Equal(Hex("08918a948002"), ConnectionProtocol.BuildItemGone(537199889));

            // Frames 32-33: one of the four 392 out, three left in the bin.
            Assert.Equal(Hex("1a0f083f2a0b088803180120aacaa08002"), ConnectionProtocol.BuildItemArrived(3, Stack(392, 1, 537404714)));
            Assert.Equal(Hex("0a0f083f2a0b088803180320d1cd938002"), ConnectionProtocol.BuildItemArrived(1, Stack(392, 3, 537192145)));

            // Frames 45-46: the fourth joins the bag's stack, which makes 4, and the bin's goes.
            Assert.Equal(Hex("1a0810aacaa080021804"), ConnectionProtocol.BuildItemQuantity(537404714, 4));
            Assert.Equal(Hex("08d1cd938002"), ConnectionProtocol.BuildItemGone(537192145));
        }

        [Fact]
        public void The_kcr_count_is_signed()
        {
            // Frame 21 of the bin: -1 as ten bytes of ones, then the uid.
            byte[] kcr = ConnectionProtocol.Push(Op.Kcr, Hex("08ffffffffffffffffff0110918a948002"));
            Assert.True(StorageHandler.ReadMove(kcr, out int quantity, out long uid));
            Assert.Equal(-1, quantity);
            Assert.Equal(537199889, uid);

            // Frame 16 of the house chest: one in.
            kcr = ConnectionProtocol.Push(Op.Kcr, Hex("080110ffbe8d9302"));
            Assert.True(StorageHandler.ReadMove(kcr, out quantity, out uid));
            Assert.Equal(1, quantity);
            Assert.Equal(576937855, uid);
        }

        // ─── The stacks ─────────────────────────────────────────────────────────

        private static long Give(long character, int gid, int quantity, int position = Equipment.Bag)
        {
            long uid = DatabaseManager.NextItemUid();
            Assert.True(DatabaseManager.InsertCharacterItem(uid, character, gid, quantity, position, null));
            return uid;
        }

        private static int InBag(long character, long uid) => HavenBagStore.FromInventory(character, uid)?.Quantity ?? 0;

        private static void Forget(StorageStacks.Place place, params long[] characters)
        {
            StorageStacks.EnsureTables();
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using (var items = connection.CreateCommand())
            {
                items.CommandText = $"DELETE FROM {place.Table} WHERE {place.OwnerColumn} = $o;";
                items.Parameters.AddWithValue("$o", place.Owner);
                items.ExecuteNonQuery();
            }
            foreach (long character in characters)
            {
                using var bag = connection.CreateCommand();
                bag.CommandText = "DELETE FROM CharacterItems WHERE CharacterId = $c;";
                bag.Parameters.AddWithValue("$c", character);
                bag.ExecuteNonQuery();
            }
        }

        [Fact]
        public void One_unit_out_per_move_and_the_bag_s_stack_grows()
        {
            const long thrower = 8_810_100_001, picker = 8_810_100_002;
            var bin = StorageStacks.Bin(8_810_000_001, 523673);
            Forget(bin, thrower, picker);
            try
            {
                long four = Give(thrower, 392, 4);

                // Thrown in whole: a new uid in the bin, nothing left in the bag.
                var thrown = StorageStacks.Put(bin, thrower, four, 4);
                Assert.NotNull(thrown);
                Assert.True(thrown!.NewStack);
                Assert.NotEqual(four, thrown.InStorage.Uid);
                Assert.Equal(0, thrown.LeftInBag);
                Assert.Equal(0, InBag(thrower, four));

                // Somebody else takes them one by one, as frames 31-47 do.
                long binStack = thrown.InStorage.Uid;
                var first = StorageStacks.Take(bin, picker, binStack, 1)!;
                Assert.False(first.Merged);
                Assert.Equal(1, first.InBag.Quantity);
                Assert.Equal(3, first.LeftInStorage!.Quantity);
                Assert.Equal(binStack, first.LeftInStorage.Uid);

                var second = StorageStacks.Take(bin, picker, binStack, 1)!;
                Assert.True(second.Merged);
                Assert.Equal(first.InBag.Uid, second.InBag.Uid);
                Assert.Equal(2, second.InBag.Quantity);
                Assert.Equal(2, second.LeftInStorage!.Quantity);

                StorageStacks.Take(bin, picker, binStack, 1);
                var last = StorageStacks.Take(bin, picker, binStack, 1)!;
                Assert.Equal(4, last.InBag.Quantity);
                Assert.Null(last.LeftInStorage);
                Assert.Empty(StorageStacks.ItemsOf(bin));
                Assert.Equal(4, InBag(picker, first.InBag.Uid));

                // Nothing that is not there moves.
                Assert.Null(StorageStacks.Take(bin, picker, binStack, 1));
            }
            finally
            {
                Forget(bin, thrower, picker);
            }
        }

        [Fact]
        public void Part_of_a_stack_goes_in_and_joins_the_same_stack()
        {
            const long character = 8_810_100_003;
            var chest = StorageStacks.HouseChest(8_810_000_002, 522653, 522477);
            Forget(chest, character);
            try
            {
                long pile = Give(character, 878, 10);

                var four = StorageStacks.Put(chest, character, pile, 4)!;
                Assert.Equal(6, four.LeftInBag);
                Assert.Equal(6, InBag(character, pile));

                var three = StorageStacks.Put(chest, character, pile, 3)!;
                Assert.False(three.NewStack);
                Assert.Equal(four.InStorage.Uid, three.InStorage.Uid);
                Assert.Equal(7, three.InStorage.Quantity);

                // More than there is takes what there is.
                var rest = StorageStacks.Take(chest, character, three.InStorage.Uid, 99)!;
                Assert.Equal(7, rest.Moved);
                Assert.True(rest.Merged);
                Assert.Equal(10, InBag(character, pile));
            }
            finally
            {
                Forget(chest, character);
            }
        }

        [Fact]
        public void A_worn_item_does_not_go_into_a_chest()
        {
            const long character = 8_810_100_004;
            var chest = StorageStacks.HavenBag(character);
            Forget(chest, character);
            try
            {
                long ring = Give(character, 2469, 1, position: 2);
                Assert.Null(StorageStacks.Put(chest, character, ring, 1));
                Assert.Equal(1, InBag(character, ring));
            }
            finally
            {
                Forget(chest, character);
            }
        }

        [Fact]
        public void A_house_s_chests_are_emptied_together_and_only_them()
        {
            const long character = 8_810_100_005;
            var first = StorageStacks.HouseChest(8_810_000_003, 522653, 1);
            var second = StorageStacks.HouseChest(8_810_000_003, 522653, 2);
            var neighbour = StorageStacks.HouseChest(8_810_000_003, 5226530, 1);
            Forget(first, character);
            Forget(second);
            Forget(neighbour);
            try
            {
                StorageStacks.Put(first, character, Give(character, 392, 2), 2);
                StorageStacks.Put(second, character, Give(character, 6902, 1), 1);
                StorageStacks.Put(neighbour, character, Give(character, 6903, 1), 1);

                var taken = StorageStacks.TakeAll(StorageStacks.HouseChestsPrefix(8_810_000_003, 522653));
                Assert.Equal(2, taken.Count);
                Assert.Empty(StorageStacks.ItemsOf(first));
                Assert.Empty(StorageStacks.ItemsOf(second));
                Assert.Single(StorageStacks.ItemsOf(neighbour));
            }
            finally
            {
                Forget(first, character);
                Forget(second);
                Forget(neighbour);
            }
        }

        [Fact]
        public void A_uid_kept_in_a_chest_is_never_handed_out_again()
        {
            const long character = 8_810_100_006;
            var chest = StorageStacks.HavenBag(character);
            Forget(chest, character);
            try
            {
                // A stack in the haven bag chest with a uid above everything the dispenser knows,
                // as after a restart where the highest item of all was in the chest.
                long high = DatabaseManager.NextItemUid() + 1_000;
                using (var connection = new SqliteConnection(DatabaseManager.WorldConnectionString))
                {
                    connection.Open();
                    using var insert = connection.CreateCommand();
                    insert.CommandText = "INSERT INTO HavenBagChest (Uid, CharacterId, Gid, Quantity, Effects) VALUES ($u, $c, 392, 1, '');";
                    insert.Parameters.AddWithValue("$u", high);
                    insert.Parameters.AddWithValue("$c", character);
                    insert.ExecuteNonQuery();
                }

                StorageStacks.ProtectUids();
                Assert.True(DatabaseManager.NextItemUid() > high);
            }
            finally
            {
                Forget(chest, character);
            }
        }

        // ─── Through the windows ────────────────────────────────────────────────

        [Fact]
        public async Task A_bin_is_public_and_gives_one_unit_per_minus_one()
        {
            const long mapId = 8_810_000_010, account = 8_810_000_011, thrower = 8_810_100_010, picker = 8_810_100_011;
            const int element = 523673;
            var bin = StorageStacks.Bin(mapId, element);
            Forget(bin, thrower, picker);

            await using var first = await ClientPipe.OpenAsync(account, thrower, mapId, "Thrower");
            await using var second = await ClientPipe.OpenAsync(account + 1, picker, mapId, "Picker");
            try
            {
                long four;
                using (SessionContext.Push(first.Session))
                {
                    four = Give(thrower, 392, 4);
                    Equipment.Add(four, 392, 4, Equipment.Bag, null);

                    // Frames 11-13: iwn, kci 17, the content -- empty.
                    await BinHandler.OpenAsync(first.ToClient, element, Bins.UseSkill);
                    Assert.Equal(ConnectionProtocol.BuildElementInUse(element, Bins.UseSkill, thrower), await first.Next(Op.Iwn));
                    Assert.Equal(Hex("08641811"), await first.Next(Op.Kci));
                    Assert.Empty(await first.Next(Op.Iwb));

                    // Thrown in: itd, ium, iun, the chest's frames.
                    Assert.True(await StorageHandler.MoveAsync(first.ToClient, Kcr(4, four)));
                    await first.Next(Op.Itd);
                    Assert.Equal(ConnectionProtocol.BuildItemGone(four), await first.Next(Op.Ium));
                    await first.Next(Op.Iun);
                }

                long inBin = Assert.Single(StorageStacks.ItemsOf(bin)).Uid;
                using (SessionContext.Push(second.Session))
                {
                    await BinHandler.OpenAsync(second.ToClient, element, Bins.UseSkill);
                    await second.Next(Op.Iwn);
                    await second.Next(Op.Kci);
                    var content = ProtoMessage.Parse(await second.Next(Op.Iwb)).Fields;
                    Assert.Single(content, f => f.FieldNumber == 1);

                    // Frames 31-34: one out, iua with a new uid, itd with the three left.
                    Assert.True(await StorageHandler.MoveAsync(second.ToClient, Kcr(-1, inBin)));
                    await second.Next(Op.Iua);
                    Assert.Equal(ConnectionProtocol.BuildItemArrived(1, Stack(392, 3, inBin)), await second.Next(Op.Itd));
                    await second.Next(Op.Iun);

                    // The one who threw them, still at the bin, sees the three left.
                    Assert.Equal(ConnectionProtocol.BuildItemArrived(1, Stack(392, 3, inBin)), await first.Next(Op.Itd));

                    // Frames 35-38: the next joins the bag's stack, ivj 2, and two are left.
                    long bagStack = Equipment.All.Single(i => i.Template == 392).Uid;
                    Assert.True(await StorageHandler.MoveAsync(second.ToClient, Kcr(-1, inBin)));
                    Assert.Equal(ConnectionProtocol.BuildItemQuantity(bagStack, 2), await second.Next(Op.Ivj));
                    Assert.Equal(ConnectionProtocol.BuildItemArrived(1, Stack(392, 2, inBin)), await second.Next(Op.Itd));
                    await second.Next(Op.Iun);
                    await first.Next(Op.Itd);

                    await StorageHandler.MoveAsync(second.ToClient, Kcr(-1, inBin));
                    await second.Take(3);
                    await first.Next(Op.Itd);

                    // Frames 44-47: the last, ivj 4 and itc.
                    Assert.True(await StorageHandler.MoveAsync(second.ToClient, Kcr(-1, inBin)));
                    Assert.Equal(ConnectionProtocol.BuildItemQuantity(bagStack, 4), await second.Next(Op.Ivj));
                    Assert.Equal(ConnectionProtocol.BuildItemGone(inBin), await second.Next(Op.Itc));
                    await second.Next(Op.Iun);
                    Assert.Equal(ConnectionProtocol.BuildItemGone(inBin), await first.Next(Op.Itc));
                    Assert.Equal(4, InBag(picker, bagStack));

                    // Frames 83-84: the cross.
                    Assert.True(await StorageHandler.CloseAsync(second.ToClient));
                    Assert.Equal(Hex("180b"), await second.Next(Op.Khd));
                    Assert.False(StorageHandler.IsOpen);
                    Assert.False(await StorageHandler.MoveAsync(second.ToClient, Kcr(-1, inBin)));
                }
            }
            finally
            {
                Forget(bin, thrower, picker);
            }
        }

        [Fact]
        public async Task The_haven_bag_chest_takes_one_unit_and_sends_what_leaves_first()
        {
            const long account = 8_810_000_020, character = 8_810_100_020;
            var chest = StorageStacks.HavenBag(character);
            Forget(chest, character);

            await using var pipe = await ClientPipe.OpenAsync(account, character, 162795538);
            try
            {
                using (SessionContext.Push(pipe.Session))
                {
                    long three = Give(character, 17626, 3);
                    Equipment.Add(three, 17626, 3, Equipment.Bag, null);

                    await ChestHandler.OpenAsync(pipe.ToClient, 516924, Merkasako.ChestSkill);
                    await pipe.Next(Op.Iwn);
                    Assert.Equal(Hex("08ffffffff071813"), await pipe.Next(Op.Kci));
                    await pipe.Next(Op.Iwb);

                    // In, one of three: what leaves first (ivj, two left in the bag), then itd with a
                    // new uid -- the order of frames 18-20.
                    await ChestHandler.MoveAsync(pipe.ToClient, Kcr(1, three));
                    Assert.Equal(ConnectionProtocol.BuildItemQuantity(three, 2), await pipe.Next(Op.Ivj));
                    var inChest = Assert.Single(StorageStacks.ItemsOf(chest));
                    Assert.NotEqual(three, inChest.Uid);
                    Assert.Equal(ConnectionProtocol.BuildItemArrived(1, Stack(17626, 1, inChest.Uid)), await pipe.Next(Op.Itd));
                    await pipe.Next(Op.Iun);

                    // Out with -1: ONE unit, not the whole stack; itc first, then it lands on the
                    // bag's stack, which makes three again.
                    await ChestHandler.MoveAsync(pipe.ToClient, Kcr(1, three));
                    await pipe.Take(3);
                    Assert.Equal(2, StorageStacks.ItemsOf(chest).Single().Quantity);

                    await ChestHandler.MoveAsync(pipe.ToClient, Kcr(-1, inChest.Uid));
                    Assert.Equal(ConnectionProtocol.BuildItemArrived(1, Stack(17626, 1, inChest.Uid)), await pipe.Next(Op.Itd));
                    Assert.Equal(ConnectionProtocol.BuildItemQuantity(three, 2), await pipe.Next(Op.Ivj));
                    await pipe.Next(Op.Iun);
                    Assert.Equal(1, StorageStacks.ItemsOf(chest).Single().Quantity);

                    await ChestHandler.CloseAsync(pipe.ToClient);
                    Assert.Equal(Hex("180b"), await pipe.Next(Op.Khd));
                }
            }
            finally
            {
                Forget(chest, character);
            }
        }

        private static byte[] Kcr(long quantity, long uid)
            => ConnectionProtocol.Push(Op.Kcr, Pb.New().Var(1, quantity).Var(2, uid).Build());
    }
}

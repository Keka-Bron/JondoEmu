using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// The items of type 203, «Objeto invisible»: Ankama's quest flags («[!] Quête 2453 Objet 001»,
    /// 26687...). The server has them, counts them and tests them; the client is told nothing, or it
    /// lists them in the bag and says «Vous avez obtenu 1 [Quête 2453 Objet 001]».
    /// </summary>
    [Collection("forgemagic")]
    public class InvisibleItemsTests
    {
        // Declared by hand, so these tests do not depend on the item data.
        private const int Flag = 990_203_001, Plain = 990_006_001;
        private const long FlagUid = 990_203_101, PlainUid = 990_006_101;

        // Real data, for the ones that go through the database.
        private const int QuestFlag = 26687, Larva = 15128;
        private const long Character = 8_940_100_031, Account = 8_940_000_031;

        public InvisibleItemsTests()
        {
            Forgemagic.Declare(new Forgemagic.Template { Gid = Flag, Type = Equipment.InvisibleType });
            Forgemagic.Declare(new Forgemagic.Template { Gid = Plain, Type = 6 });
        }

        private static HavenBagStore.StoredItem Stack(int gid, long uid, int quantity = 1)
            => new() { Gid = gid, Uid = uid, Quantity = quantity, Effects = "[]" };

        [Fact]
        public void Only_type_203_is_invisible()
        {
            Assert.True(Equipment.IsInvisibleType(203));
            Assert.False(Equipment.IsInvisibleType(202));
            Assert.False(Equipment.IsInvisibleType(204));

            Assert.True(Equipment.IsInvisible(Flag));
            Assert.False(Equipment.IsInvisible(Plain));
            Assert.False(Equipment.IsInvisible(-5));   // no such template
        }

        [Fact]
        public void The_world_entry_inventory_leaves_the_flags_out_and_keeps_them_in_the_bag()
        {
            var state = new SessionState();
            state.EquipmentItems[FlagUid] = new Equipment.Item { Uid = FlagUid, Template = Flag, Position = Equipment.Bag };
            state.EquipmentItems[PlainUid] = new Equipment.Item { Uid = PlainUid, Template = Plain, Position = Equipment.Bag };

            var listed = InventoryMessages.FromCatalogue.Snapshot(state).Objects.Select(o => o.Item.Gid).ToArray();

            Assert.Equal(new[] { Plain }, listed);
            Assert.Contains(FlagUid, state.EquipmentItems.Keys);   // the server still has it
            Assert.True(InvisibleItems.IsHidden(FlagUid));
        }

        [Fact]
        public void A_chest_does_not_list_them_either()
        {
            byte[] both = ConnectionProtocol.BuildStorageContent(new[] { Stack(Flag, FlagUid + 1), Stack(Plain, PlainUid + 1) });
            byte[] plain = ConnectionProtocol.BuildStorageContent(new[] { Stack(Plain, PlainUid + 1) });
            Assert.Equal(plain, both);
        }

        [Fact]
        public void A_flag_arriving_changing_and_leaving_sends_nothing()
        {
            const long uid = FlagUid + 2;
            Assert.True(InvisibleItems.Hides(ConnectionProtocol.Push(Op.Iua, ConnectionProtocol.BuildItemArrived(3, Stack(Flag, uid)))));
            Assert.True(InvisibleItems.Hides(ConnectionProtocol.Push(Op.Itd, ConnectionProtocol.BuildItemArrived(1, Stack(Flag, uid)))));
            Assert.True(InvisibleItems.Hides(ConnectionProtocol.Push(Op.Ivj, ConnectionProtocol.BuildItemQuantity(uid, 4))));
            Assert.True(InvisibleItems.Hides(ConnectionProtocol.Push(Op.Ium, ConnectionProtocol.BuildItemGone(uid))));
            Assert.True(InvisibleItems.Hides(ConnectionProtocol.Push(Op.Itc, ConnectionProtocol.BuildItemGone(uid))));
        }

        [Fact]
        public void An_ordinary_item_is_told_as_ever()
        {
            const long uid = PlainUid + 2;
            Assert.False(InvisibleItems.Hides(ConnectionProtocol.Push(Op.Iua, ConnectionProtocol.BuildItemArrived(3, Stack(Plain, uid)))));
            Assert.False(InvisibleItems.Hides(ConnectionProtocol.Push(Op.Ivj, ConnectionProtocol.BuildItemQuantity(uid, 4))));
            Assert.False(InvisibleItems.Hides(ConnectionProtocol.Push(Op.Ium, ConnectionProtocol.BuildItemGone(uid))));
            Assert.False(InvisibleItems.Hides(ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(InfoMessages.ItemGained, "1", Plain.ToString()))));
            Assert.False(InvisibleItems.Hides(ConnectionProtocol.Push(Op.Iun, ConnectionProtocol.BuildPods(10, 1000))));
        }

        [Fact]
        public void The_you_got_notice_is_not_sent_for_a_flag()
        {
            Assert.True(InvisibleItems.Hides(ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildSystemMessage(InfoMessages.ItemGained, "1", Flag.ToString()))));

            // Nor the one for taking it back.
            Assert.True(InvisibleItems.Hides(ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildSystemMessage(InfoMessages.ItemLost, "1", Flag.ToString()))));
            Assert.False(InvisibleItems.Hides(ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildSystemMessage(InfoMessages.ItemLost, "1", Plain.ToString()))));

            // Another message that happens to carry the same number of parameters is not the notice.
            Assert.False(InvisibleItems.Hides(ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildSystemMessage(InfoMessages.ItemLost + 1, "1", Flag.ToString()))));
        }

        [Fact]
        public async Task The_frame_writer_drops_the_flag_frames_and_writes_the_rest()
        {
            var written = new MemoryStream();
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(written, ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildSystemMessage(InfoMessages.ItemGained, "1", Flag.ToString())));
            Assert.Equal(0, written.Length);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(written, ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildSystemMessage(InfoMessages.ItemGained, "1", Plain.ToString())));
            Assert.True(written.Length > 0);
        }

        /// <summary>The server gives, counts, tests and takes the flag; the client hears none of it.</summary>
        [Fact]
        public async Task A_flag_given_is_kept_counted_and_taken_back_without_a_word_to_the_client()
        {
            RecyclingTests.ClearBag(Character);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(Account, Character, 191105026, "Flag");
                using (SessionContext.Push(pipe.Session))
                {
                    Equipment.LoadFrom(Character);
                    var flag = await Equipment.GrantAsync(pipe.ToClient, QuestFlag, 2);
                    if (flag == null) return;   // no item data
                    await Equipment.GrantAsync(pipe.ToClient, QuestFlag, 1);   // onto the stack: an ivj

                    // Stored and counted, and a quest criterion finds it: PO=26687.
                    Assert.Equal(3, Equipment.HowMany(QuestFlag));
                    var facts = new PlayerCriteria.Facts(null, null, null, 0, Equipment.HowMany);
                    Assert.True(Criterion.Met($"PO={QuestFlag}", c => PlayerCriteria.Resolve(c, facts)));
                    Assert.False(Criterion.Met($"PO!{QuestFlag}", c => PlayerCriteria.Resolve(c, facts)));

                    // Taken back in two goes, the second being the whole stack: an ivj or an itd, then an ium.
                    Assert.True(await Equipment.TakeAsync(pipe.ToClient, QuestFlag, 1));
                    Assert.True(await Equipment.TakeAsync(pipe.ToClient, QuestFlag, 2));
                    Assert.Equal(0, Equipment.HowMany(QuestFlag));
                    Assert.True(Criterion.Met($"PO!{QuestFlag}", c => PlayerCriteria.Resolve(c, facts)));

                    // Nothing went out for any of it: the first thing the client hears is the larva.
                    await Equipment.GrantAsync(pipe.ToClient, Larva, 1);
                    Assert.Equal(Op.Iua, (await pipe.NextAny()).Opcode);
                    await pipe.Quiet();
                }
            }
            finally
            {
                RecyclingTests.ClearBag(Character);
            }
        }

        /// <summary>A flag may weigh a pod in the data (31780...), and the bag the client sees does not.</summary>
        [Fact]
        public void A_flag_weighs_nothing_in_the_pods()
        {
            const int weighingFlag = 31780;
            if (DatabaseManager.GetItemRealWeight(weighingFlag) == 0) return;   // no item data

            var state = new SessionState();
            state.EquipmentItems[1] = new Equipment.Item { Uid = 1, Template = weighingFlag, Quantity = 3, Position = Equipment.Bag };
            Assert.Equal(0, ConnectionProtocol.Carried(state));
            Assert.Equal(0, AfterSelectionMessages.Live.Weight(state).InventoryWeight);
        }
    }
}

using System;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// Equipping one of a stack: a pair of Gelano in the bag, one put on a finger. The whole stack
    /// went into the slot, both rings at once.
    /// </summary>
    [Collection("forgemagic")]
    public class EquipOneOfAStackTests
    {
        private const int Gelano = 2469, OfNoSet = 732, Ring = 2, OtherRing = 4;
        private const long Character = 8_940_100_011, Account = 8_940_000_011;

        [Fact]
        public async Task One_goes_on_and_the_other_stays_in_the_bag()
        {
            RecyclingTests.ClearBag(Character);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(Account, Character, 191105026, "Bague");
                using (SessionContext.Push(pipe.Session))
                {
                    pipe.Session.State.CharacterLevel = 200;
                    Equipment.LoadFrom(Character);
                    var pair = Equipment.Create(Gelano, 2, Array.Empty<Equipment.ItemEffect>())!;

                    Assert.True(await EquipmentHandler.MoveItemAsync(pipe.ToClient, pair.Uid, Ring, Account, sheet: false));

                    var worn = Equipment.InSlot(Ring)!;
                    Assert.NotEqual(pair.Uid, worn.Uid);
                    Assert.Equal((Gelano, 1), (worn.Template, worn.Quantity));
                    Assert.Equal((Equipment.Bag, 1), (pair.Position, pair.Quantity));
                    Assert.Equal(2, Equipment.HowMany(Gelano));

                    // The stack's new count, the new ring in the bag, then the ring onto the finger.
                    Assert.Equal(Op.Ivj, (await pipe.NextAny()).Opcode);
                    Assert.Equal(Op.Iua, (await pipe.NextAny()).Opcode);
                    Assert.Equal(Op.Ivq, (await pipe.NextAny()).Opcode);

                    // A set's piece is worn once: the other Gelano does not go on the other finger,
                    // and the client is told it stays in the bag.
                    if (ItemSets.SetsOf(Gelano).Count == 0) return;   // no set data
                    Assert.False(await EquipmentHandler.MoveItemAsync(pipe.ToClient, pair.Uid, OtherRing, Account, sheet: false));
                    Assert.Null(Equipment.InSlot(OtherRing));
                    Assert.Equal(Equipment.Bag, pair.Position);
                    (string Opcode, byte[] Payload) frame;
                    do frame = await pipe.NextAny();   // past the first ring's look and weight
                    while (frame.Opcode != Op.Ivq || frame.Payload.Length == 0 || !frame.Payload.AsSpan().SequenceEqual(
                               Pb.New().Var(1, pair.Uid).Var(2, Equipment.Bag).Build()));

                    // But moved from one finger to the other, it is the same ring.
                    Assert.True(await EquipmentHandler.MoveItemAsync(pipe.ToClient, worn.Uid, OtherRing, Account, sheet: false));
                    Assert.Equal(worn.Uid, Equipment.InSlot(OtherRing)!.Uid);
                }
            }
            finally
            {
                RecyclingTests.ClearBag(Character);
            }
        }

        /// <summary>A ring of no set (732) can be worn on both fingers.</summary>
        [Fact]
        public async Task A_ring_of_no_set_goes_on_both_fingers()
        {
            RecyclingTests.ClearBag(Character);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(Account, Character, 191105026, "Bague");
                using (SessionContext.Push(pipe.Session))
                {
                    pipe.Session.State.CharacterLevel = 200;
                    Equipment.LoadFrom(Character);
                    Assert.Empty(ItemSets.SetsOf(OfNoSet));
                    var pair = Equipment.Create(OfNoSet, 2, Array.Empty<Equipment.ItemEffect>())!;

                    Assert.True(await EquipmentHandler.MoveItemAsync(pipe.ToClient, pair.Uid, Ring, Account, sheet: false));
                    Assert.True(await EquipmentHandler.MoveItemAsync(pipe.ToClient, pair.Uid, OtherRing, Account, sheet: false));
                    Assert.Equal(OfNoSet, Equipment.InSlot(Ring)!.Template);
                    Assert.Equal(pair.Uid, Equipment.InSlot(OtherRing)!.Uid);
                }
            }
            finally
            {
                RecyclingTests.ClearBag(Character);
            }
        }
    }
}

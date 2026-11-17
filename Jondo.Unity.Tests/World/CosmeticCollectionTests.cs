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
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The cosmetics collection, with the messages of «Apariencias/añadir capa objeviva y anillo
    /// objevivo a coleccion cosmeticos» and «Apariencias/retirar varias cosas de coleccion…».
    /// </summary>
    [Collection("forgemagic")]
    public sealed class CosmeticCollectionTests
    {
        private const long Account = 8_900_000_051;

        /// <summary>The capture's living ring, with its level and looks.</summary>
        private const int Ring = 9256;

        private static List<ProtoField> Fields(byte[] message) => ProtoMessage.Parse(message).Fields;

        private static async Task<List<string>> Until(ClientPipe pipe, string last)
        {
            var opcodes = new List<string>();
            for (int i = 0; i < 20 && (opcodes.Count == 0 || opcodes[^1] != last); i++)
                opcodes.Add((await pipe.NextAny()).Opcode);
            return opcodes;
        }

        [Fact]
        public async Task An_appearance_item_goes_into_the_collection_and_back_out()
        {
            long character = DatabaseManager.CreateCharacter(Account, 1, "Coleccion-Uno", 8, 0, 1,
                new long[] { -1, -1, -1, -1, -1 }, 191105026, 50, 0, 0, Array.Empty<(int, int)>());
            Assert.True(character > 0);
            CosmeticCollection.Forget(Account);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(Account, character, 990_100_051, "Coleccion-Uno");
                using (SessionContext.Push(pipe.Session))
                {
                    const string effects = "[[971,1,0,0],[972,1,0,0]]";
                    long uid = DatabaseManager.NextItemUid();
                    Assert.True(DatabaseManager.InsertCharacterItem(uid, character, Ring, 1, Equipment.Bag, effects));
                    Equipment.Add(uid, Ring, 1, Equipment.Bag, effects);

                    // Into the collection: gone from the bag, kept with a new uid.
                    await CosmeticCollectionHandler.AddAsync(pipe.ToClient, ConnectionProtocol.Push("lyn",
                        Pb.New().Packed(2, new long[] { uid }).Build()));
                    Assert.Equal(uid, Fields(await pipe.Next(Op.Ium)).Single().VarIntValue);
                    var lyb = Fields(await pipe.Next("lyb"));
                    var kept = Fields(lyb.Single(f => f.FieldNumber == 3).BytesValue);
                    Assert.Equal(Ring, kept.Single(f => f.FieldNumber == 1).VarIntValue);
                    Assert.NotEqual(uid, kept.Single(f => f.FieldNumber == 4).VarIntValue);
                    await pipe.Next(Op.Iun);
                    await pipe.Next("lyg");
                    Assert.Null(Equipment.ByUid(uid));
                    Assert.True(CosmeticCollection.Has(Account, Ring));

                    // The entry's lwt carries it, effects and all.
                    var lwt = CosmeticCollectionMessages.Live.ForAccount(Account);
                    Assert.Equal(Ring, Assert.Single(lwt.Objects).Gid);

                    // A second ring of the same template stays in the bag.
                    long second = DatabaseManager.NextItemUid();
                    Assert.True(DatabaseManager.InsertCharacterItem(second, character, Ring, 1, Equipment.Bag, null));
                    Equipment.Add(second, Ring, 1, Equipment.Bag, null);
                    await CosmeticCollectionHandler.AddAsync(pipe.ToClient, ConnectionProtocol.Push("lyn",
                        Pb.New().Packed(2, new long[] { second }).Build()));
                    Assert.NotNull(Equipment.ByUid(second));

                    // Worn as an appearance, then taken out: back in the bag, and off the character.
                    Wardrobe.Wear(character, Cosmetics.SlotOf(Ring), Ring * 1000L, Ring);
                    await CosmeticCollectionHandler.RemoveAsync(pipe.ToClient, ConnectionProtocol.Push("lxh",
                        Pb.New().Packed(1, new long[] { Ring }).Build()), Account);
                    var opcodes = await Until(pipe, "lya");
                    Assert.Equal(new[] { "iua", "lym", "hie", "hii", "jsn", "lxc", "iun", "kmb", "lya" }, opcodes);
                    Assert.False(CosmeticCollection.Has(Account, Ring));
                    Assert.DoesNotContain(Wardrobe.AppearanceOf(character), w => w.Gid == Ring);
                    var back = Equipment.All.Single(i => i.Template == Ring && i.Uid != second);
                    Assert.Contains(back.Effects, e => e.Effect == 971);
                }
            }
            finally
            {
                CosmeticCollection.Forget(Account);
                DatabaseManager.DeleteCharacter(character, Account);
            }
        }
    }
}

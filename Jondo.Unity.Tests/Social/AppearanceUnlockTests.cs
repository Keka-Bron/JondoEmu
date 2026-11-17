using System;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.Social
{
    /// <summary>An item that unlocks a face: used, the account keeps it and the faces tab takes it.</summary>
    [Collection("forgemagic")]
    public sealed class AppearanceUnlockTests
    {
        private const long Account = 8_900_000_083;

        /// <summary>«Ocra - Cara vanguardista»: heads 616 (male) and 617 (female), both payable.</summary>
        private const int OcraFace = 34138, Male = 616, Female = 617, Ocra = 9;

        private static ProtoField? Field(byte[] message, int number)
            => ProtoMessage.Parse(message).Fields.FirstOrDefault(f => f.FieldNumber == number);

        private static long[] Packed(byte[] message, int number)
        {
            var field = Field(message, number);
            if (field == null) return Array.Empty<long>();
            if (field.WireType == 0) return new[] { (long)field.VarIntValue };
            var values = new System.Collections.Generic.List<long>();
            var input = new Google.Protobuf.CodedInputStream(field.BytesValue);
            while (!input.IsAtEnd) values.Add(input.ReadInt64());
            return values.ToArray();
        }

        private static long Give(long character, int template)
        {
            long uid = DatabaseManager.NextItemUid();
            Assert.True(DatabaseManager.InsertCharacterItem(uid, character, template, 1, Equipment.Bag, ""));
            Equipment.Add(uid, template, 1, Equipment.Bag, "");
            return uid;
        }

        private static async Task<byte[]?> Until(ClientPipe pipe, string opcode, string stop)
        {
            for (int i = 0; i < 10; i++)
            {
                var (got, payload) = await pipe.NextAny();
                if (got == opcode) return payload;
                if (got == stop) return null;
            }
            return null;
        }

        [Fact]
        public async Task A_face_item_unlocks_its_faces_for_the_tab()
        {
            AppearanceUnlocks.Forget(Account);
            long character = DatabaseManager.CreateCharacter(Account, 1, "Desbloqueo-Uno", Ocra, 0, 1,
                new long[] { -1, -1, -1, -1, -1 }, 191105026, 50, 0, 0, Array.Empty<(int, int)>());
            Assert.True(character > 0);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(Account, character, 990_100_083, "Desbloqueo-Uno");
                using (SessionContext.Push(pipe.Session))
                {
                    byte[] Lwl(int head) => ConnectionProtocol.Push(Op.Lwl, Pb.New().Var(1, head).Build());

                    // Not unlocked yet: the payable face is refused.
                    await AppearanceHandler.FaceAsync(pipe.ToClient, Lwl(Male));
                    Assert.Null(Field(await pipe.Next(Op.Lye), 2));

                    // Used: spent, and lyb puts both heads in the faces' list (kind 1).
                    long uid = Give(character, OcraFace);
                    await ItemUseHandler.UseAsync(pipe.Session, ConnectionProtocol.Push(Op.Iuu, Pb.New().Var(3, uid).Build()));
                    byte[] lyb = (await Until(pipe, "lyb", "none"))!;
                    Assert.Equal((long)AppearanceUnlocks.Kind.Head, (long)Field(lyb, 4)!.VarIntValue);
                    Assert.Equal(new long[] { Male, Female }, Packed(lyb, 1).OrderBy(x => x));
                    Assert.Null(Equipment.ByUid(uid));
                    Assert.Equal(new long[] { Male, Female },
                                 CosmeticCollectionMessages.Live.ForAccount(Account).Heads.Select(id => (long)id).OrderBy(x => x));

                    // The tab takes the male head now; the female one is not this character's sex.
                    await AppearanceHandler.FaceAsync(pipe.ToClient, Lwl(Male));
                    await pipe.Next(Op.Lxc);
                    Assert.Equal(1, (int)Field(await pipe.Next(Op.Lye), 2)!.VarIntValue);
                    await AppearanceHandler.FaceAsync(pipe.ToClient, Lwl(Female));
                    Assert.Null(Field(await pipe.Next(Op.Lye), 2));

                    // A second one unlocks nothing new: it stays in the bag.
                    long again = Give(character, OcraFace);
                    await ItemUseHandler.UseAsync(pipe.Session, ConnectionProtocol.Push(Op.Iuu, Pb.New().Var(3, again).Build()));
                    await pipe.Quiet();
                    Assert.NotNull(Equipment.ByUid(again));
                }
            }
            finally
            {
                AppearanceUnlocks.Forget(Account);
                DatabaseManager.DeleteCharacter(character, Account);
            }
        }
    }
}

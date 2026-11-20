using System;
using System.Linq;
using System.Text;
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
    /// <summary>The name and sex change potions: the change waits for the next connection.</summary>
    [Collection("forgemagic")]
    public sealed class RemodelTests
    {
        private const long Account = 8_900_000_084;
        private const int NamePotion = 10860, SexPotion = 10862;

        private static ProtoField? Field(byte[] message, int number)
            => ProtoMessage.Parse(message).Fields.FirstOrDefault(f => f.FieldNumber == number);

        private static long Give(long character, int template)
        {
            long uid = DatabaseManager.NextItemUid();
            Assert.True(DatabaseManager.InsertCharacterItem(uid, character, template, 1, Equipment.Bag, ""));
            Equipment.Add(uid, template, 1, Equipment.Bag, "");
            return uid;
        }

        /// <summary>The popup the next lqn carries, past what the item use sends before it.</summary>
        private static async Task<int> PopupOf(ClientPipe pipe)
        {
            for (int i = 0; i < 8; i++)
            {
                var (op, payload) = await pipe.NextAny();
                if (op != Op.Lqn) continue;
                Assert.Equal(4, (int)Field(payload, 1)!.VarIntValue);
                return (int)Field(payload, 2)!.VarIntValue;
            }
            throw new Xunit.Sdk.XunitException("no lqn came");
        }

        [Fact]
        public async Task A_name_potion_asks_for_the_name_on_the_next_entry()
        {
            long character = DatabaseManager.CreateCharacter(Account, 1, "Remodelo-Uno", 11, 0, 1,
                new long[] { -1, -1, -1, -1, -1 }, 191105026, 50, 0, 0, Array.Empty<(int, int)>());
            Assert.True(character > 0);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(Account, character, 990_100_084, "Remodelo-Uno");
                using (SessionContext.Push(pipe.Session))
                {
                    byte[] Use(long uid) => ConnectionProtocol.Push(Op.Iuu, Pb.New().Var(3, uid).Build());

                    // Used: spent, popup 41, the change waits.
                    long potion = Give(character, NamePotion);
                    await ItemUseHandler.UseAsync(pipe.Session, Use(potion));
                    Assert.Equal(41, await PopupOf(pipe));
                    Assert.Null(Equipment.ByUid(potion));
                    Assert.Equal(CharacterRemodel.Change.Name, CharacterRemodel.PendingOf(character));

                    // Another change while that one waits: popup 43, the potion stays.
                    long other = Give(character, SexPotion);
                    await ItemUseHandler.UseAsync(pipe.Session, Use(other));
                    Assert.Equal(43, await PopupOf(pipe));
                    Assert.NotNull(Equipment.ByUid(other));

                    // The next entry opens the Rename window with the name as it is.
                    var me = DatabaseManager.GetCharacterById(character)!;
                    await RemodelHandler.OnEnterWorldAsync(pipe.Session, me);
                    Assert.Equal("Remodelo-Uno", Encoding.UTF8.GetString(Field(await pipe.Next(Op.Kvk), 1)!.BytesValue));

                    // A badly made name is refused with popup 40; a good one is taken.
                    byte[] Lvw(string name) => ConnectionProtocol.Push("lvw", Pb.New().Str(1, name).Build());
                    await RemodelHandler.RenameAsync(pipe.ToClient, Lvw("no vale"), Account);
                    Assert.Equal(40, await PopupOf(pipe));
                    await RemodelHandler.RenameAsync(pipe.ToClient, Lvw("Remodelo-Dos"), Account);
                    Assert.Equal("Remodelo-Dos", Encoding.UTF8.GetString(Field(await pipe.Next("lwd"), 2)!.BytesValue));
                    Assert.Equal("Remodelo-Dos", DatabaseManager.GetCharacterById(character)!.Name);
                    Assert.Equal(CharacterRemodel.Change.None, CharacterRemodel.PendingOf(character));
                }
            }
            finally
            {
                DatabaseManager.DeleteCharacter(character, Account);
            }
        }

        [Fact]
        public void A_sex_potion_changes_sex_and_face_on_the_next_selection()
        {
            const int Sacrier = 11;
            int maleHead = HeadTable.DefaultHeadId(Sacrier, 0);
            long character = DatabaseManager.CreateCharacter(Account + 1, 1, "Remodelo-Tres", Sacrier, 0, maleHead,
                new long[] { -1, -1, -1, -1, -1 }, 191105026, 50, 0, 0, Array.Empty<(int, int)>());
            Assert.True(character > 0);
            try
            {
                Assert.True(CharacterRemodel.Request(character, CharacterRemodel.Change.Sex));
                Assert.True(CharacterRemodel.ApplySexChange(character));
                var changed = DatabaseManager.GetCharacterById(character)!;
                Assert.Equal(1, changed.Sex);
                Assert.Equal(HeadTable.Counterpart(maleHead, Sacrier, 0), changed.HeadId);
                Assert.True(HeadTable.Fits(changed.HeadId, Sacrier, 1));
                Assert.Equal(CharacterRemodel.Change.None, CharacterRemodel.PendingOf(character));
                Assert.False(CharacterRemodel.ApplySexChange(character));
            }
            finally
            {
                DatabaseManager.DeleteCharacter(character, Account + 1);
            }
        }
    }
}

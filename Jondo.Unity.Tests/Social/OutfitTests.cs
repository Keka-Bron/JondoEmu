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
    /// <summary>The appearance window's outfits list: keeping, renaming, putting on, copying and throwing away.</summary>
    [Collection("forgemagic")]
    public sealed class OutfitTests
    {
        private const long Account = 8_900_000_080;
        private const int Base = 26, Retro = 110, RetroIdle = 24;

        private static ProtoField? Field(byte[] message, int number)
            => ProtoMessage.Parse(message).Fields.FirstOrDefault(f => f.FieldNumber == number);

        private static string Text(byte[] message, int number) => Encoding.UTF8.GetString(Field(message, number)!.BytesValue);

        /// <summary>The next frame of that kind, past the redraws that come before it.</summary>
        private static async Task<byte[]> Until(ClientPipe pipe, string opcode)
        {
            for (int i = 0; i < 20; i++)
            {
                var (got, payload) = await pipe.NextAny();
                if (got == opcode) return payload;
            }
            throw new Xunit.Sdk.XunitException($"{opcode} never came");
        }

        [Fact]
        public async Task An_outfit_kept_is_renamed_put_on_copied_and_thrown_away()
        {
            Wardrobe.Initialize();
            Outfits.Initialize();
            long character = DatabaseManager.CreateCharacter(Account, 1, "Atuendo-Uno", 11, 0, 1,
                new long[] { -1, -1, -1, -1, -1 }, 191105026, 50, 0, 0, Array.Empty<(int, int)>());
            Assert.True(character > 0);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(Account, character, 990_100_080, "Atuendo-Uno");
                using (SessionContext.Push(pipe.Session))
                {
                    // The first outfit, the worn one: the breed's base body and the plain idle.
                    var me = DatabaseManager.GetCharacterById(character)!;
                    var first = Assert.Single(WardrobeMessages.Live.Snapshot(pipe.Session, me, Outfits.Of(character)).Outfits);
                    string firstId = first.Uuid;
                    Assert.Equal(ConnectionProtocol.OutfitIdOf(character), firstId);
                    Assert.Equal(Base, first.BodyId);
                    Assert.Equal(me.HeadId, first.HeadId);
                    Assert.Equal(IdleTable.Plain, first.IdleId);

                    // Dressed «retro», kept as a new outfit with its name and icon.
                    await AppearanceHandler.BodyAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Lyo, Pb.New().Var(2, Retro).Build()));
                    await Until(pipe, Op.Lyz);
                    await AppearanceHandler.IdleAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Lwu, Pb.New().Var(1, RetroIdle).Build()));
                    await Until(pipe, Op.Lwn);

                    await AppearanceHandler.CreateOutfitAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Lwk,
                        Pb.New().Var(1, 1).Var(2, 7).Str(4, "Ceremonia").Build()));
                    byte[] lxb = await pipe.Next(Op.Lxb);
                    Assert.Equal(1, (int)Field(lxb, 1)!.VarIntValue);
                    string kept = Text(lxb, 2);
                    byte[] outfit = Field(lxb, 3)!.BytesValue;
                    Assert.Equal(kept, Text(outfit, 7));
                    Assert.Equal("Ceremonia", Text(outfit, 13));
                    Assert.Equal(7, (int)Field(outfit, 15)!.VarIntValue);
                    Assert.Equal(Retro, (int)Field(outfit, 5)!.VarIntValue);
                    Assert.Equal(RetroIdle, (int)Field(outfit, 8)!.VarIntValue);

                    // Back to the base body; the kept outfit stays as it was kept.
                    await AppearanceHandler.BodyAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Lyo, Pb.New().Var(2, Base).Build()));
                    await Until(pipe, Op.Lyz);
                    await AppearanceHandler.IdleAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Lwu, Pb.New().Var(1, IdleTable.Plain).Build()));
                    await Until(pipe, Op.Lwn);

                    // Renamed, starred and given another icon.
                    await AppearanceHandler.UpdateOutfitAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Lxt,
                        Pb.New().Str(1, "Fiesta").Var(2, 1).Str(3, kept).Var(5, 9).Build()));
                    Assert.Equal(1, (int)Field(await pipe.Next(Op.Lxr), 1)!.VarIntValue);
                    var stored = Outfits.Of(character).Single(o => o.Uuid == kept);
                    Assert.Equal(("Fiesta", 9, true), (stored.Name, stored.Icon, stored.Favorite));
                    Assert.Equal(Retro, stored.Kept!.Body);

                    // Put on: the live tables take the kept look, the first outfit keeps what was worn.
                    await AppearanceHandler.EquipAsync(pipe.Session, ConnectionProtocol.Push(Op.Lyy, Pb.New().Str(1, kept).Build()));
                    Assert.Equal(1, (int)Field(await Until(pipe, Op.Lxo), 1)!.VarIntValue);
                    Assert.Equal(kept, Outfits.EquippedOf(character));
                    Assert.Equal(Retro, Wardrobe.BodyOf(character));
                    Assert.Equal(RetroIdle, Wardrobe.IdleOf(character));
                    var before = Outfits.Of(character).Single(o => o.Uuid == firstId);
                    Assert.Equal(Base, before.Kept!.Body);
                    Assert.StartsWith("5881,", CharacterLookBuilder.Drawable(DatabaseManager.GetCharacterById(character)!).Split('|')[1]);

                    // The worn one is not thrown away; the other one is.
                    await AppearanceHandler.DeleteOutfitAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Lyr, Pb.New().Str(2, kept).Build()));
                    Assert.Null(Field(await pipe.Next(Op.Lxl), 1));
                    await AppearanceHandler.DeleteOutfitAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Lyr, Pb.New().Str(2, firstId).Build()));
                    Assert.Equal(1, (int)Field(await pipe.Next(Op.Lxl), 1)!.VarIntValue);
                    Assert.Single(Outfits.Of(character));

                    // A copy of the worn one, under its own name.
                    await AppearanceHandler.DuplicateOutfitAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Lzd,
                        Pb.New().Var(1, 3).Str(2, kept).Str(3, "Copia").Build()));
                    byte[] copy = Field(await pipe.Next(Op.Lxb), 3)!.BytesValue;
                    Assert.Equal("Copia", Text(copy, 13));
                    Assert.Equal(Retro, (int)Field(copy, 5)!.VarIntValue);
                    Assert.Equal(2, Outfits.Of(character).Count);

                    // An outfit that is not one's own cannot be put on.
                    await AppearanceHandler.EquipAsync(pipe.Session, ConnectionProtocol.Push(Op.Lyy,
                        Pb.New().Str(1, Guid.NewGuid().ToString()).Build()));
                    Assert.Null(Field(await pipe.Next(Op.Lxo), 1));
                    Assert.Equal(kept, Outfits.EquippedOf(character));
                }
            }
            finally
            {
                DatabaseManager.DeleteCharacter(character, Account);
            }
        }

        /// <summary>
        /// A build given an outfit (ihg) names it in its f9 and puts it on with itself (iio); an
        /// outfit that is not the character's is not linked.
        /// </summary>
        [Fact]
        public async Task A_build_puts_on_the_outfit_it_was_given()
        {
            Wardrobe.Initialize();
            Outfits.Initialize();
            long character = DatabaseManager.CreateCharacter(Account + 1, 1, "Atuendo-Dos", 11, 0, 1,
                new long[] { -1, -1, -1, -1, -1 }, 191105026, 50, 0, 0, Array.Empty<(int, int)>());
            Assert.True(character > 0);
            CharacterPresets.Forget(character);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(Account + 1, character, 990_100_081, "Atuendo-Dos");
                using (SessionContext.Push(pipe.Session))
                {
                    pipe.Session.State.CharacterLevel = 50;
                    pipe.Session.State.Breed = 11;
                    var me = DatabaseManager.GetCharacterById(character)!;
                    var retro = Outfits.Create(character, "Retro", 2, Outfits.Worn(me) with { Body = Retro, Idle = RetroIdle })!;

                    await PresetHandler.CreateAsync(pipe.ToClient, ConnectionProtocol.Push("igp",
                        Pb.New().Msg(1, Pb.New().EmptyMsg(2)).Var(2, 4).Str(4, "Conjunto").Build()));
                    string build = Text(Field(Field(await pipe.Next("igk"), 1)!.BytesValue, 1)!.BytesValue, 10);

                    // Linked: the answer carries the build, naming the outfit.
                    await PresetHandler.OutfitAsync(pipe.ToClient, ConnectionProtocol.Push("ihg",
                        Pb.New().Str(1, retro.Uuid).Str(2, build).Build()));
                    byte[] ihu = await pipe.Next("ihu");
                    Assert.Equal(1, (int)Field(ihu, 1)!.VarIntValue);
                    Assert.Equal(retro.Uuid, Text(Field(ihu, 2)!.BytesValue, 9));

                    // Someone else's outfit is not linked.
                    await PresetHandler.OutfitAsync(pipe.ToClient, ConnectionProtocol.Push("ihg",
                        Pb.New().Str(1, Guid.NewGuid().ToString()).Str(2, build).Build()));
                    Assert.Null(Field(await pipe.Next("ihu"), 1));
                    Assert.Equal(retro.Uuid, CharacterPresets.Find(character, build)!.Outfit);

                    // Put on, the build puts the outfit on.
                    await PresetHandler.EquipAsync(pipe.Session, ConnectionProtocol.Push("iio", Pb.New().Str(1, build).Build()));
                    await Until(pipe, "ihq");
                    Assert.Equal(retro.Uuid, Outfits.EquippedOf(character));
                    Assert.Equal(Retro, Wardrobe.BodyOf(character));
                    Assert.Equal(RetroIdle, Wardrobe.IdleOf(character));
                }
            }
            finally
            {
                CharacterPresets.Forget(character);
                DatabaseManager.DeleteCharacter(character, Account + 1);
            }
        }
    }
}

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

namespace Jondo.Unity.Tests.Social
{
    /// <summary>The aura: one of the character's aura emotes, kept, told to him and hung on his map look.</summary>
    public sealed class AuraTests
    {
        private const long Account = 8_900_000_082;
        private const int Power = 22, PowerBones = 170, Other = 23, Sit = 1;

        private static ProtoField? Field(byte[] message, int number)
            => ProtoMessage.Parse(message).Fields.FirstOrDefault(f => f.FieldNumber == number);

        /// <summary>The bones hanging at binding point 6 of a look's root; 0 for none.</summary>
        private static long AuraBonesOf(byte[] look)
        {
            foreach (var sub in ProtoMessage.Parse(look).Fields.Where(f => f.FieldNumber == 7))
            {
                if (Field(sub.BytesValue, 4)?.VarIntValue != 6) continue;
                return (long)(Field(Field(sub.BytesValue, 1)!.BytesValue, 3)?.VarIntValue ?? 0);
            }
            return 0;
        }

        /// <summary>The aura option of an actor block's humanoid body (f2 f1 f5 f3 f5 { f1 { f1 } }); 0 for none.</summary>
        private static long AuraOptionOf(byte[] actor)
        {
            byte[] humanoid = Field(Field(Field(actor, 2)!.BytesValue, 1)!.BytesValue, 5)!.BytesValue;
            byte[] body = Field(humanoid, 3)!.BytesValue;
            var first = ProtoMessage.Parse(body).Fields.First(f => f.FieldNumber == 5);
            var aura = Field(first.BytesValue, 1);
            return aura == null ? 0 : (long)(Field(aura.BytesValue, 1)?.VarIntValue ?? 0);
        }

        private static async Task<byte[]> Until(ClientPipe pipe, string opcode)
        {
            for (int i = 0; i < 10; i++)
            {
                var (got, payload) = await pipe.NextAny();
                if (got == opcode) return payload;
            }
            throw new Xunit.Sdk.XunitException($"{opcode} never came");
        }

        [Fact]
        public async Task An_aura_of_ones_own_is_kept_and_hangs_on_the_map_look()
        {
            Wardrobe.Initialize();
            Emotes.Load();
            long character = DatabaseManager.CreateCharacter(Account, 1, "Aura-Uno", 11, 0, 1,
                new long[] { -1, -1, -1, -1, -1 }, 191105026, 100, 0, 0, Array.Empty<(int, int)>());
            Assert.True(character > 0);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(Account, character, 990_100_082, "Aura-Uno");
                using (SessionContext.Push(pipe.Session))
                {
                    pipe.Session.State.Emotes = new HashSet<int> { Sit, Power };
                    byte[] Lxw(int aura) => ConnectionProtocol.Push(Op.Lxw, Pb.New().VarIfNotZero(2, aura).Build());

                    // Taken: empty lym notification, answered lwx f1, and on the look others see.
                    // The unused lym scalar is omitted under
                    // docs/populated-unmapped-fields.md#after-selection-omission-policy.
                    await AppearanceHandler.AuraAsync(pipe.ToClient, Lxw(Power));
                    Assert.Empty(await pipe.Next(Op.Lym));
                    Assert.Equal(1, (int)Field(await pipe.Next(Op.Lwx), 1)!.VarIntValue);
                    await Until(pipe, Op.Jsn);
                    Assert.Equal(Power, Wardrobe.AuraOf(character));
                    var me = DatabaseManager.GetCharacterById(character)!;
                    Assert.Equal(PowerBones, AuraBonesOf(BreedLookTable.MapLook(me)));
                    Assert.Equal(0, AuraBonesOf(BreedLookTable.BuildLook(me, paraLaVentana: true)));
                    Assert.Equal(Power, AuraOptionOf(ConnectionProtocol.BuildPlayerActorBlock(me, 300, 1, Account)));

                    // An aura not known, and an emote that is no aura: refused, the aura as it was.
                    foreach (int refused in new[] { Other, Sit })
                    {
                        await AppearanceHandler.AuraAsync(pipe.ToClient, Lxw(refused));
                        Assert.Null(Field(await pipe.Next(Op.Lwx), 1));
                        Assert.Equal(Power, Wardrobe.AuraOf(character));
                    }

                    // The outfit carries it in its f9.
                    byte[] outfit = Field(ConnectionProtocol.BuildOutfits(me), 2)!.BytesValue;
                    Assert.Equal(Power, (int)Field(outfit, 9)!.VarIntValue);

                    // Taken off.
                    await AppearanceHandler.AuraAsync(pipe.ToClient, Lxw(0));
                    Assert.Empty(await pipe.Next(Op.Lym));
                    Assert.Equal(1, (int)Field(await pipe.Next(Op.Lwx), 1)!.VarIntValue);
                    Assert.Equal(0, Wardrobe.AuraOf(character));
                    Assert.Equal(0, AuraBonesOf(BreedLookTable.MapLook(DatabaseManager.GetCharacterById(character)!)));
                }
            }
            finally
            {
                DatabaseManager.DeleteCharacter(character, Account);
            }
        }
    }
}

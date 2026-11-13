using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The «Conjuntos» window: a build made, edited on a draft, saved, put on and deleted, with the
    /// messages of «Conjuntos/crear nuevo conjunto…».
    /// </summary>
    [Collection("forgemagic")]
    public sealed class PresetTests
    {
        private const long Account = 8_900_000_041;
        private const int Template = 17626;

        private static List<ProtoField> Fields(byte[] message) => ProtoMessage.Parse(message).Fields;

        private static byte[] Sub(byte[] message, int number)
            => Fields(message).First(f => f.FieldNumber == number && f.WireType == 2).BytesValue;

        private static string Text(byte[] message, int number)
            => System.Text.Encoding.UTF8.GetString(Sub(message, number));

        /// <summary>One characteristic's columns in a build's sheet: { column -> value }.</summary>
        private static Dictionary<int, long> Column(byte[] sheet, int stat)
        {
            foreach (var entry in Fields(sheet).Where(f => f.FieldNumber == 1))
            {
                var fields = Fields(entry.BytesValue);
                if (fields.First(f => f.FieldNumber == 1).VarIntValue != stat) continue;
                return Fields(fields.First(f => f.FieldNumber == 2).BytesValue).ToDictionary(f => f.FieldNumber, f => (long)f.VarIntValue);
            }
            throw new InvalidOperationException($"no characteristic {stat}");
        }

        private static long Give(long character, string effects)
        {
            long uid = DatabaseManager.NextItemUid();
            Assert.True(DatabaseManager.InsertCharacterItem(uid, character, Template, 1, Equipment.Bag, effects));
            Equipment.Add(uid, Template, 1, Equipment.Bag, effects);
            return uid;
        }

        [Fact]
        public async Task A_build_is_made_edited_saved_put_on_and_deleted()
        {
            long character = DatabaseManager.CreateCharacter(Account, 1, "Conjunto-Uno", 8, 0, 1,
                new long[] { -1, -1, -1, -1, -1 }, 191105026, 200, 0, 0, Array.Empty<(int, int)>());
            Assert.True(character > 0);
            CharacterPresets.Forget(character);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(Account, character, 990_100_041, "Conjunto-Uno");
                using (SessionContext.Push(pipe.Session))
                {
                    var state = pipe.Session.State;
                    state.CharacterLevel = 200;
                    state.Breed = 8;
                    state.ScrolledStrength = 100;
                    long amulet = Give(character, "[[118,30,0,0]]");

                    // The character as he is: the first card.
                    await PresetHandler.PlayerStateAsync(pipe.ToClient);
                    var own = Sub(await pipe.Next("ifz"), 1);
                    Assert.Equal(CharacterPresets.PlayerStateName, Text(own, 4));
                    Assert.Equal(100, Column(Sub(own, 3), ConnectionProtocol.Stat.Strength)[1]);
                    // Life travels with its key at zero, as the maps of the capture write it.
                    Assert.Contains(Fields(Sub(own, 3)), f => Fields(f.BytesValue).Any(g => g.FieldNumber == 1 && g.WireType == 0 && g.VarIntValue == 0));

                    // A new empty build.
                    await PresetHandler.CreateAsync(pipe.ToClient, ConnectionProtocol.Push("igp",
                        Pb.New().Msg(1, Pb.New().EmptyMsg(2)).Var(2, 4).Str(4, "Conjunto (1)").Build()));
                    var made = Sub(Sub(await pipe.Next("igk"), 1), 1);
                    string uuid = Text(made, 10);
                    Assert.Equal("Conjunto (1)", Text(made, 4));
                    Assert.Equal(4, Fields(made).First(f => f.FieldNumber == 2).VarIntValue);
                    Assert.DoesNotContain(Fields(made), f => f.FieldNumber == 5);
                    Assert.NotNull(CharacterPresets.Find(character, uuid));

                    // Renamed: only on the draft until «Guardar».
                    await PresetHandler.RenameAsync(pipe.ToClient, ConnectionProtocol.Push("ihf",
                        Pb.New().Str(3, uuid).Str(4, "Fuerza").Build()));
                    await pipe.Next("igc");
                    Assert.Equal("Conjunto (1)", CharacterPresets.Find(character, uuid)!.Name);

                    // The amulet in its slot: the sheet carries its strength, and the pods what it gives.
                    await PresetHandler.PlaceItemAsync(pipe.ToClient, ConnectionProtocol.Push("ify",
                        Pb.New().Str(1, uuid).Msg(3, Pb.New().Var(2, amulet)).Build()));
                    var iic = await pipe.Next("iic");
                    Assert.Equal(1, Fields(iic).First(f => f.FieldNumber == 4).VarIntValue);
                    var sheet = Sub(Sub(iic, 2), 2);
                    Assert.Equal(30, Column(sheet, ConnectionProtocol.Stat.Strength)[6]);
                    Assert.Equal(5 * 130, Column(sheet, ConnectionProtocol.Stat.Pods)[3]);
                    Assert.Equal(ConnectionProtocol.PodsBeforeStrength(state), Column(sheet, ConnectionProtocol.Stat.Pods)[4]);

                    // Points: fifty in strength fit a level 200; five thousand do not.
                    await PresetHandler.PointsAsync(pipe.ToClient, ConnectionProtocol.Push("iha",
                        Pb.New().Str(1, uuid).Msg(2, Pb.New().Var(1, 10).Var(2, 50)).Build()));
                    var ijd = await pipe.Next("ijd");
                    Assert.Equal(1, Fields(ijd).First(f => f.FieldNumber == 3).VarIntValue);
                    Assert.Equal(50, Column(Sub(ijd, 1), ConnectionProtocol.Stat.Strength)[2]);
                    await PresetHandler.PointsAsync(pipe.ToClient, ConnectionProtocol.Push("iha",
                        Pb.New().Str(1, uuid).Msg(2, Pb.New().Var(1, 10).Var(2, 5000)).Build()));
                    Assert.DoesNotContain(Fields(await pipe.Next("ijd")), f => f.FieldNumber == 3);

                    // «Guardar».
                    await PresetHandler.SaveAsync(pipe.ToClient, ConnectionProtocol.Push("ihs", Pb.New().Str(2, uuid).Build()));
                    Assert.Equal(1, Fields(await pipe.Next("iim")).First(f => f.FieldNumber == 2).VarIntValue);
                    var saved = CharacterPresets.Find(character, uuid)!;
                    Assert.Equal("Fuerza", saved.Name);
                    Assert.Equal(amulet, saved.Items[0]);
                    Assert.Equal(50, saved.Points[ConnectionProtocol.Stat.Strength]);

                    // An edit thrown away: the build stays as saved.
                    await PresetHandler.RenameAsync(pipe.ToClient, ConnectionProtocol.Push("ihf",
                        Pb.New().Str(3, uuid).Str(4, "Descartado").Build()));
                    await pipe.Next("igc");
                    await PresetHandler.DiscardAsync(pipe.ToClient, ConnectionProtocol.Push("igo", Pb.New().Str(1, uuid).Build()));
                    Assert.Equal("Fuerza", Text(Sub(await pipe.Next("ign"), 2), 4));
                    Assert.False(CharacterPresets.HasDraft(character, uuid));

                    // Put on: the amulet worn, the points spent, ihq last.
                    await PresetHandler.EquipAsync(pipe.ToClient, ConnectionProtocol.Push("iio", Pb.New().Str(1, uuid).Build()), Account);
                    var opcodes = new List<string>();
                    for (int i = 0; i < 80 && (opcodes.Count == 0 || opcodes[^1] != "ihq"); i++)
                        opcodes.Add((await pipe.NextAny()).Opcode);
                    Assert.Equal("kub", opcodes[0]);
                    Assert.Contains("ivq", opcodes);
                    Assert.Equal("ihq", opcodes[^1]);
                    Assert.Equal(0, Equipment.ByUid(amulet)!.Position);
                    Assert.Equal(50, state.StatStrength);

                    // On entering the world: the list.
                    await PresetHandler.SendListAsync(pipe.ToClient);
                    var ihb = await pipe.Next("ihb");
                    Assert.Equal(uuid, Text(Fields(ihb).Single(f => f.FieldNumber == 1).BytesValue, 1));

                    // Deleted.
                    await PresetHandler.DeleteAsync(pipe.ToClient, ConnectionProtocol.Push("iiy", Pb.New().Str(1, uuid).Build()));
                    Assert.Equal(1, Fields(await pipe.Next("ihc")).First(f => f.FieldNumber == 1).VarIntValue);
                    Assert.Empty(CharacterPresets.Of(character));
                }
            }
            finally
            {
                CharacterPresets.Forget(character);
                DatabaseManager.DeleteCharacter(character, Account);
            }
        }

        /// <summary>A client request on root 2, with its id beside the message.</summary>
        private static byte[] Request(string op, byte[] body, long id)
            => Pb.New().Msg(2, Pb.New().Bytes(1, Pb.New().Str(1, "type.ankama.com/" + op).Bytes(2, body).Build())
                                       .Var(2, id)).Build();

        /// <summary>The root field a frame goes in, and the request id beside it.</summary>
        private static (int Root, long Id) Envelope(byte[] frame)
        {
            var root = Fields(frame).Single();
            return (root.FieldNumber, (long)Fields(root.BytesValue).First(f => f.FieldNumber == 2).VarIntValue);
        }

        /// <summary>
        /// The new name and the new icon answered under the id they were asked with, as the capture
        /// answers ihf's 40 with igc: the client keeps the icon beside that id, and only then counts
        /// the build as changed and lights «Guardar».
        /// </summary>
        [Fact]
        public async Task The_new_name_and_icon_are_answered_under_their_request()
        {
            long character = DatabaseManager.CreateCharacter(Account, 1, "Conjunto-Dos", 8, 0, 1,
                new long[] { -1, -1, -1, -1, -1 }, 191105026, 200, 0, 0, Array.Empty<(int, int)>());
            Assert.True(character > 0);
            CharacterPresets.Forget(character);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(Account, character, 990_100_043, "Conjunto-Dos");
                using (SessionContext.Push(pipe.Session))
                {
                    pipe.Session.State.CharacterLevel = 200;
                    pipe.Session.State.Breed = 8;
                    await PresetHandler.CreateAsync(pipe.ToClient, Request("igp",
                        Pb.New().Msg(1, Pb.New().EmptyMsg(2)).Var(2, 4).Str(4, "Conjunto (1)").Build(), -1));
                    var made = Sub(Sub(await pipe.Next("igk"), 1), 1);
                    string uuid = Text(made, 10);

                    await PresetHandler.RenameAsync(pipe.ToClient, Request("ihf", Pb.New().Str(3, uuid).Str(4, "Fuerza").Build(), 40));
                    Assert.Equal((3, 40L), Envelope(await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer)));

                    await PresetHandler.IconAsync(pipe.ToClient, Request("iid", Pb.New().Str(2, uuid).Var(4, 7).Build(), 41));
                    byte[] iir = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer);
                    Assert.Equal((3, 41L), Envelope(iir));
                    Assert.Equal("iir", ConnectionProtocol.ReadOpcode(iir));
                    Assert.Equal(1, Fields(ConnectionProtocol.ReadPayload(iir, "iir")!).First(f => f.FieldNumber == 1).VarIntValue);

                    await PresetHandler.SaveAsync(pipe.ToClient, Request("ihs", Pb.New().Str(2, uuid).Build(), 42));
                    Assert.Equal((3, 42L), Envelope(await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer)));
                    Assert.Equal(7, CharacterPresets.Find(character, uuid)!.Icon);
                    Assert.Equal("Fuerza", CharacterPresets.Find(character, uuid)!.Name);
                }
            }
            finally
            {
                CharacterPresets.Forget(character);
                DatabaseManager.DeleteCharacter(character, Account);
            }
        }

        /// <summary>The item bar keeps what is dragged onto it: a build in slot 18, as in the capture.</summary>
        [Fact]
        public async Task The_item_bar_keeps_a_build_until_it_is_taken_off()
        {
            const long character = 8_900_100_041;
            ItemShortcuts.Forget(character);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(Account, character, 990_100_042, "Barra-Uno");
                using (SessionContext.Push(pipe.Session))
                {
                    // itz { 2 { 2 18, 4 { 2 uuid } } }, byte for byte the capture's, echoed in ivk.
                    byte[] itz = Convert.FromHexString(
                        "122a10122226122430303030303030302d303030302d343030302d383030302d303030303030303030303039");
                    await ItemBarHandler.SetAsync(pipe.ToClient, itz);
                    Assert.Equal(itz, await pipe.Next("ivk"));
                    Assert.Equal(new[] { 18 }, ItemShortcuts.Of(character).Keys);

                    // Swapped onto an empty slot: there, and gone from 18.
                    await ItemBarHandler.SwapAsync(pipe.ToClient, 18, 3);
                    Assert.Equal(new byte[] { 0x10, 0x12 }, await pipe.Next("ivr"));
                    var moved = Sub(await pipe.Next("ivk"), 2);
                    Assert.Equal(3, ItemShortcuts.SlotOf(moved));
                    Assert.Equal(new[] { 3 }, ItemShortcuts.Of(character).Keys);

                    // On entering the world: the bar, with the build in it.
                    var bar = ShortcutBarMessages.Live.GeneralBar(pipe.Session);
                    var saved = Assert.Single(bar.Shortcuts);
                    Assert.Equal(Jondo.Unity.Protocol.Game.ShortcutBar.General, bar.BarType);
                    Assert.Equal(3, saved.SlotId);
                    Assert.Equal("00000000-0000-4000-8000-000000000009",
                        Assert.IsType<Jondo.Unity.Protocol.Game.ShortcutCaseShortcutPreset>(saved.ShortcutCase).Value.PresetUuid);

                    // iul { 2 3 }: ivr { 2 3 }, the bar's 0 left out.
                    await ItemBarHandler.RemoveAsync(pipe.ToClient, 3);
                    Assert.Equal(new byte[] { 0x10, 0x03 }, await pipe.Next("ivr"));
                    Assert.Empty(ItemShortcuts.Of(character));
                }
            }
            finally
            {
                ItemShortcuts.Forget(character);
            }
        }
    }
}

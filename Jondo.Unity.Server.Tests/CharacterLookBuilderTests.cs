using System.Text.Json;
using Google.Protobuf;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class CharacterLookBuilderTests
{
    [Fact]
    public void Catalog_keeps_each_breed_sex_body_scale_and_exactly_six_RGB_defaults()
    {
        using var source = JsonDocument.Parse(File.ReadAllText(Paths.BreedLooksJson));
        foreach (var breed in source.RootElement.EnumerateObject())
        foreach (var sex in breed.Value.EnumerateObject())
        {
            var look = BreedLookCatalog.Get(int.Parse(breed.Name), sex.Name == "female" ? 1 : 0)!;
            Assert.Equal(sex.Value.GetProperty("bones").GetInt32(), look.Bones);
            Assert.Equal(sex.Value.GetProperty("skins").EnumerateArray().Select(item => item.GetInt64()), look.Skins);
            Assert.Equal(sex.Value.GetProperty("scales").EnumerateArray().Select(item => item.GetInt64()), look.Scales);
            Assert.Equal(sex.Value.GetProperty("colors").EnumerateArray().Select(item => item.GetInt64()), look.Colors);
            Assert.Equal(6, look.Colors.Count);
            Assert.All(look.Colors, color => Assert.InRange(color, 0, 0xFFFFFF));
        }
        Assert.Null(BreedLookCatalog.Get(int.MaxValue, 0));
    }

    [Fact]
    public async Task Concurrent_catalog_readers_get_complete_immutable_defaults()
    {
        var readers = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => BreedLookCatalog.Get(18, 1)!)));
        Assert.All(readers, look =>
        {
            Assert.Same(readers[0], look);
            Assert.Equal(6, look.Colors.Count);
            Assert.NotEmpty(look.Skins);
            Assert.NotEmpty(look.Scales);
        });
        Assert.Throws<NotSupportedException>(() => ((IList<long>)readers[0].Colors)[0] = 0);
        Assert.Throws<NotSupportedException>(() => ((IList<long>)readers[0].Skins).Clear());
    }

    [Fact]
    public void Look_serializer_preserves_recursive_appearance_and_keeps_skins_out_of_look_type()
    {
        var rider = new EntityLook([0x01112233, 0x02000000, 0x06FFFFFF], 3, 2, [55], [110, 2172, 5044], []);
        var root = new EntityLook([0x01445566], 3, 200, [120], [800],
            [new(new([], 3, 77, [80], [], []), 0, SubEntityBindingPointCategory.HookPointCategoryPet),
             new(rider, 0, SubEntityBindingPointCategory.HookPointCategoryMountDriver)]);
        var wire = ProtoMessage.Parse(CharacterLookBuilder.WriteLook(root).Build());
        Assert.Equal(3, Assert.Single(wire.Fields, field => field.FieldNumber == 2).VarIntValue);
        Assert.Equal(new[] { 800 }, Packed(wire, 6));
        Assert.Equal(new[] { 120 }, Packed(wire, 5));
        Assert.Equal(200, Assert.Single(wire.Fields, field => field.FieldNumber == 3).VarIntValue);
        var children = wire.Fields.Where(field => field.FieldNumber == 7).Select(field => ProtoMessage.Parse(field.BytesValue)).ToArray();
        Assert.Equal(new long[] { 1, 2 }, children.Select(child => child.Fields.Single(field => field.FieldNumber == 4).VarIntValue));
        var riderWire = ProtoMessage.Parse(children[1].Fields.Single(field => field.FieldNumber == 1).BytesValue);
        Assert.Equal(new[] { 110, 2172, 5044 }, Packed(riderWire, 6));
        Assert.Equal(new[] { 0x01112233, 0x02000000, 0x06FFFFFF }, Packed(riderWire, 1));
    }

    [Fact]
    public void Composition_attaches_pets_and_aura_to_the_root_and_preserves_the_body()
    {
        var body = new EntityLook([0x01112233], 3, 1, [55], [110, 2172], []);
        var mount = new EntityLook([0x01445566], 3, 200, [120], [800], []);
        SubEntityInformation[] attachments =
        [
            new(new([0x01112233], 3, 77, [80], [], []), 0, SubEntityBindingPointCategory.HookPointCategoryPet),
            new(new([], 3, 170, [], [], []), 0, SubEntityBindingPointCategory.HookPointCategoryBaseForeground),
        ];

        var mounted = CharacterLookBuilder.Compose(body, mount, attachments);
        Assert.Equal(new SubEntityBindingPointCategory?[] { SubEntityBindingPointCategory.HookPointCategoryPet,
            SubEntityBindingPointCategory.HookPointCategoryBaseForeground,
            SubEntityBindingPointCategory.HookPointCategoryMountDriver },
            mounted.SubEntities.Select(child => (SubEntityBindingPointCategory?)child.BindingPointCategory));
        Assert.Equal(body with { Bones = 2 }, mounted.SubEntities[2].SubEntityLook);
        Assert.Empty(body.SubEntities);
        Assert.Empty(mount.SubEntities);

        var onFoot = CharacterLookBuilder.Compose(body, null, attachments);
        Assert.Equal(1, onFoot.Bones);
        Assert.Equal(attachments, onFoot.SubEntities);
        Assert.Same(body, CharacterLookBuilder.Compose(body, null, []));
    }

    [Fact]
    public void Bot_composition_allows_an_appearance_mount_without_a_real_mount()
    {
        var mounted = CharacterLookBuilder.Compose(1, [110, 2172], [0x01112233], [55],
            appearanceMount: new Cosmetics.PieceLook { Bones = 300, Skin = 800, Scale = 90 });
        Assert.Equal(300, mounted.Bones);
        Assert.Equal(new[] { 800 }, mounted.Skins);
        Assert.Equal(new[] { 90 }, mounted.Scales);
        Assert.Empty(mounted.Colors);
        Assert.Equal(2, Assert.Single(mounted.SubEntities).SubEntityLook.Bones);
    }

    [Fact]
    public void Fighter_show_uses_the_requested_fighters_look_without_an_ambient_selected_character()
    {
        using var context = SessionContext.Push(GameSession.SinSocket());
        foreach (int bones in new[] { 200, 300 })
        {
            var look = new EntityLook([0x01112233, 0x06FFFFFF], 3, bones, [120], [800 + bones], []);
            var fighter = new Fighter { Id = 9_000_000_000 + bones, Name = "Owned fighter", IsBot = true,
                Level = 57, MaxAP = 12, MaxMP = 6, BotLook = CharacterLookBuilder.WriteLook(look).Build() };
            var payload = ProtoMessage.Parse(ConnectionProtocol.ReadPayload(FightHandler.BuildFighterShowBytes(fighter), "jxx")!);
            var actor = ProtoMessage.Parse(payload.Fields.Single(field => field.FieldNumber == 2).BytesValue);
            var details = ProtoMessage.Parse(actor.Fields.Single(field => field.FieldNumber == 2).BytesValue);
            var appearance = ProtoMessage.Parse(details.Fields.Single(field => field.FieldNumber == 1).BytesValue);
            Assert.Equal(bones, appearance.Fields.Single(field => field.FieldNumber == 3).VarIntValue);
            Assert.Equal(new[] { 800 + bones }, Packed(appearance, 6));
            Assert.Equal(new[] { 0x01112233, 0x06FFFFFF }, Packed(appearance, 1));
            Assert.Equal(fighter.Id, actor.Fields.Single(field => field.FieldNumber == 3).VarIntValue);
        }
    }

    internal static int[] Packed(ProtoMessage message, int tag)
    {
        using var input = new CodedInputStream(message.Fields.Single(field => field.FieldNumber == tag).BytesValue);
        var values = new List<int>();
        while (!input.IsAtEnd) values.Add(input.ReadInt32());
        return values.ToArray();
    }
}

using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class CharacterSelectionMessagesTests
{
    [Theory]
    [InlineData(0, Gender.Male, "")]
    [InlineData(1, Gender.Female, "2026-11-11T12:34:56Z")]
    public void Selection_uses_the_selected_identity_dates_and_complete_resolved_appearance(int sex, Gender gender, string connected)
    {
        var character = new DatabaseManager.DbCharacter
        {
            Id = 71_001, Name = "Selected", Level = 57, Breed = 18, Sex = sex, LastConnection = connected,
        };
        var pet = new EntityLook([0x01020304], 3, 77, [80], [], []);
        var rider = new EntityLook([0x01112233], 3, 2, [55], [110, 2172, 5044],
            [new(pet, 0, SubEntityBindingPointCategory.HookPointCategoryPet)]);
        var look = new EntityLook([0x01445566], 3, 200, [100], [800],
            [new(new EntityLook([], 3, 900, [], [], []), 0, SubEntityBindingPointCategory.HookPointCategoryBaseForeground),
             new(rider, 0, SubEntityBindingPointCategory.HookPointCategoryMountDriver)]);
        var wrongSession = new GameSession(new GameMessagingTests.CountingCodec(), _ => Task.CompletedTask);
        wrongSession.State.CharacterId = 81_002;
        wrongSession.State.CharacterName = "Other";
        using var scope = SessionContext.Push(wrongSession);

        var selected = CharacterSelectionMessages.Success(character, look);

        Assert.Equal(71_001, selected.Value.Character.Id);
        var basic = Assert.IsType<CharacterComplementCaseCharacterBasicInformation>(selected.Value.Character.ComplementCase).Value;
        Assert.Equal("Selected", basic.Name);
        Assert.Equal(57, basic.Level);
        var appearance = Assert.IsType<CharacterBasicInformationComplementCaseCharacterLook>(basic.ComplementCase).Value;
        Assert.Same(look, appearance.Look);
        Assert.Equal(18, appearance.BreedId);
        Assert.Equal(connected, appearance.LastConnectionDate);
        Assert.Empty(appearance.LastDisconnectionDate);
        var identity = Assert.IsType<CharacterLookComplementCaseBaseInformation>(appearance.ComplementCase).Value;
        Assert.Equal(gender, identity.Gender);
        Assert.IsType<CharacterBaseInformationComplementCaseNone>(identity.ComplementCase);
        Assert.Same(wrongSession, SessionContext.Actual);
    }

    [Fact]
    public void Zero_level_retains_absence_rather_than_a_present_zero()
    {
        var selection = CharacterSelectionMessages.Success(new() { Name = "Selected", Level = 0 },
            new EntityLook([], 3, 1, [], [], []));
        Assert.Null(Assert.IsType<CharacterComplementCaseCharacterBasicInformation>(selection.Value.Character.ComplementCase).Value.Level);
    }

    [Fact]
    public void On_foot_composition_keeps_body_palette_skins_and_size()
    {
        var look = BreedLookTable.Compose(1, [110, 2172, 5044], [0x01112233, 0x02445566], [55]);
        Assert.Equal(3, look.LookType);
        Assert.Equal(1, look.Bones);
        Assert.Equal(new[] { 110, 2172, 5044 }, look.Skins);
        Assert.Equal(new[] { 0x01112233, 0x02445566 }, look.Colors);
        Assert.Equal(new[] { 55 }, look.Scales);
        Assert.Empty(look.SubEntities);
    }

    [Fact]
    public void Mount_composition_uses_mount_palette_and_size_with_the_character_as_rider()
    {
        var look = BreedLookTable.Compose(1, [110, 2172], [0x01112233], [55],
            new Mounts.Look { Bones = 200, Colors = [0x01445566], Scale = 120 });
        Assert.Equal(200, look.Bones);
        Assert.Equal(new[] { 0x01445566 }, look.Colors);
        Assert.Equal(new[] { 120 }, look.Scales);
        Assert.Empty(look.Skins);
        var rider = Assert.Single(look.SubEntities);
        Assert.Equal(SubEntityBindingPointCategory.HookPointCategoryMountDriver, rider.BindingPointCategory);
        Assert.Equal(0, rider.BindingPointIndex);
        Assert.Equal(2, rider.SubEntityLook.Bones);
        Assert.Equal(new[] { 110, 2172 }, rider.SubEntityLook.Skins);
        Assert.Equal(new[] { 0x01112233 }, rider.SubEntityLook.Colors);
        Assert.Equal(new[] { 55 }, rider.SubEntityLook.Scales);
    }

    [Fact]
    public void Appearance_mount_overrides_root_bones_skin_and_size_without_replacing_the_rider()
    {
        var look = BreedLookTable.Compose(1, [110, 2172], [0x01112233], [55],
            new Mounts.Look { Bones = 200, Colors = [0x01445566], Scale = 120 },
            new Cosmetics.PieceLook { Bones = 300, Skin = 800, Scale = 90 });
        Assert.Equal(300, look.Bones);
        Assert.Equal(new[] { 800 }, look.Skins);
        Assert.Equal(new[] { 90 }, look.Scales);
        Assert.Equal(new[] { 0x01445566 }, look.Colors);
        Assert.Equal(new[] { 110, 2172 }, Assert.Single(look.SubEntities).SubEntityLook.Skins);
    }

    [Fact]
    public void Mutation_look_keeps_monster_bones_skins_colors_and_scale()
    {
        var look = ConnectionProtocol.SemanticMonsterLook("{2554|110|1=#112233|100}", false);
        Assert.Equal(2554, look.Bones);
        Assert.Equal(3, look.LookType);
        Assert.Equal(new[] { 110 }, look.Skins);
        Assert.Equal(new[] { 0x01112233 }, look.Colors);
        Assert.Equal(new[] { 100 }, look.Scales);
        Assert.Empty(look.SubEntities);
    }
}

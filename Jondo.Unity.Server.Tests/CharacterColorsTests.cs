using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Database.Migrations;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class CharacterColorsTests
{
    [Fact]
    public void Six_choices_resolve_defaults_independently_and_keep_black_and_the_last_slot()
    {
        var palette = CharacterColors.FromRgb([-1, 0, -1, 0xABCDEF, -1, 0xFFFFFF]);
        long[] defaults = [0x111111, 0x222222, 0x333333, 0x444444, 0x555555, 0x666666];
        Assert.Equal(new CharacterColors(null, 0, null, 0xABCDEF, null, 0xFFFFFF), palette);
        Assert.Equal(new[] { 0x111111, 0, 0x333333, 0xABCDEF, 0x555555, 0xFFFFFF }, palette.Resolve(defaults));
        Assert.Equal(new long[] { 0x01111111, 0x02000000, 0x03333333, 0x04ABCDEF, 0x05555555, 0x06FFFFFF },
            palette.ToIndexed(defaults));
        defaults[0] = 0x777777;
        Assert.Equal(0x777777, palette.Resolve(defaults)[0]);
        Assert.Null(palette.Color1);
        Assert.Equal(new int[6], default(CharacterColors).Resolve(null));
        Assert.Equal(6, CharacterColors.FromRgb([]).ToIndexed(defaults).Count);
    }

    [Fact]
    public void Outfit_palette_uses_fixed_encoded_slots_instead_of_list_position()
    {
        var palette = CharacterColors.FromIndexed([0x06123456, 0x02000000, 0x04ABCDEF]);
        Assert.Equal(new CharacterColors(null, 0, null, 0xABCDEF, null, 0x123456), palette);
        Assert.Equal(new CharacterColors(null, 0x112233, null, null, null, null),
            CharacterColors.FromIndexed([-1, 0x112233]));
        Assert.Throws<ArgumentException>(() => CharacterColors.FromIndexed([0x01112233, 0x01445566]));
        Assert.Throws<ArgumentException>(() => CharacterColors.FromIndexed([0x07112233]));
        Assert.Throws<ArgumentException>(() => CharacterColors.FromIndexed([0x01112233, 0x112233]));
    }

    [Fact]
    public void Invalid_inputs_are_rejected_instead_of_masked_or_truncated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterColors(-1, null, null, null, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterColors(null, null, null, null, null, 0x1000000));
        Assert.Throws<ArgumentOutOfRangeException>(() => CharacterColors.FromRgb([-2]));
        Assert.Throws<ArgumentOutOfRangeException>(() => CharacterColors.FromRgb([0x1000000]));
        Assert.Throws<OverflowException>(() => CharacterColors.FromRgb([long.MaxValue]));
        Assert.Throws<ArgumentException>(() => CharacterColors.FromRgb([1, 2, 3, 4, 5, 6, 7]));
        Assert.Throws<ArgumentException>(() => CharacterColors.FromIndexed([1, 2, 3, 4, 5, 6, 7]));
        Assert.Throws<ArgumentOutOfRangeException>(() => default(CharacterColors)[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => default(CharacterColors)[7]);
    }

    [Fact]
    public void Legacy_decoder_extracts_fixed_slots_without_applying_any_migration()
    {
        string bare = Convert.ToHexString(Pb.New().Packed(1, [0x112233, 0, -1, 0x445566, -1, 0xFFFFFF]).Build());
        string indexed = Convert.ToHexString(Pb.New().Packed(1, [0x06123456, 0x02000000, 0x04ABCDEF]).Build());
        Assert.Equal(new int?[] { 0x112233, 0, null, 0x445566, null, 0xFFFFFF },
            Enumerable.Range(1, 6).Select(slot => CharacterColorsMigration.DecodeColor(bare, slot)).ToArray());
        Assert.Equal(new int?[] { null, 0, null, 0xABCDEF, null, 0x123456 },
            Enumerable.Range(1, 6).Select(slot => CharacterColorsMigration.DecodeColor(indexed, slot)).ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not hex")]
    [InlineData("0A0180")]
    [InlineData("2218A28B9B0FCBE5F615A4E1B91992A6C820888CA028F5B7CB34")]
    [InlineData("080118012218A28B9B0FCBE5F615A4E1B91992A6C820888CA028F5B7CB34")]
    [InlineData("08011803220180")]
    public void Missing_or_unreadable_legacy_palette_leaves_each_slot_inherited(string? look)
        => Assert.All(Enumerable.Range(1, 6), slot => Assert.Null(CharacterColorsMigration.DecodeColor(look, slot)));

    [Fact]
    public void A_current_palette_takes_precedence_over_a_historical_field_four_palette()
    {
        string look = Convert.ToHexString(Pb.New().Var(1, 1).Var(3, 3)
            .Packed(4, [0x01112233]).Packed(1, [0x06000000]).Build());
        Assert.Equal(new int?[] { null, null, null, null, null, 0 },
            Enumerable.Range(1, 6).Select(slot => CharacterColorsMigration.DecodeColor(look, slot)).ToArray());
    }

    [Fact]
    public void List_selection_and_composed_look_use_the_six_fixed_columns()
    {
        var character = new DbCharacter
        {
            Id = 71_001, Breed = 18, Sex = 1, Name = "Palette", Level = 57,
            Colors = new(0x112233, 0, null, 0x445566, null, 0xFFFFFF),
        };
        var defaults = BreedLookCatalog.Get(18, 1)!.Colors;
        var expected = new[] { 0x01112233, 0x02000000, 0x03000000 | (int)defaults[2],
            0x04445566, 0x05000000 | (int)defaults[4], 0x06FFFFFF };
        var indexed = CharacterLookBuilder.SavedColors(character);
        Assert.Equal(expected.Select(value => (long)value), indexed);
        var look = CharacterLookBuilder.Compose(1, [110, 2172], indexed, [55]);
        var list = new CharacterListMessages(_ => look).Snapshot([character]);
        var listedBasic = Assert.IsType<CharacterComplementCaseCharacterBasicInformation>(list.Characters[0].ComplementCase).Value;
        var listed = Assert.IsType<CharacterBasicInformationComplementCaseCharacterLook>(listedBasic.ComplementCase).Value;
        var selected = CharacterSelectionMessages.Success(character, look);
        var selectedBasic = Assert.IsType<CharacterComplementCaseCharacterBasicInformation>(selected.Value.Character.ComplementCase).Value;
        var appearance = Assert.IsType<CharacterBasicInformationComplementCaseCharacterLook>(selectedBasic.ComplementCase).Value;
        Assert.Equal(expected, listed.Look.Colors);
        Assert.Equal(expected, appearance.Look.Colors);
    }
}

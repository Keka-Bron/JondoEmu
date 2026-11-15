using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

/// <summary>The account's selection-screen characters, resolved before serialization.</summary>
internal sealed class CharacterListMessages(Func<DbCharacter, EntityLook> look)
{
    internal static CharacterListMessages Live { get; } = new(character => CharacterLookBuilder.SemanticLook(character));

    internal CharacterListEvent Snapshot(IReadOnlyList<DbCharacter> characters)
        => new(ConnectionProtocol.MostRecentFirst(characters).Select(Character).ToArray());

    private Character Character(DbCharacter character)
    {
        var gender = character.Sex switch
        {
            0 => Gender.Male,
            1 => Gender.Female,
            _ => throw new GameConversionException($"Unsupported character gender {character.Sex}."),
        };
        var identity = new CharacterBaseInformation(gender, new CharacterBaseInformationComplementCaseNone());
        // Dates order the list, but the selection screen's existing payload omits both.
        // Selection success has its own date policy in CharacterSelectionMessages.
        var appearance = new CharacterLook(Snapshot(look(character)), character.Breed, "", "",
            new CharacterLookComplementCaseBaseInformation(identity));
        var basic = new CharacterBasicInformation(character.Name, character.Level == 0 ? null : character.Level,
            new CharacterBasicInformationComplementCaseCharacterLook(appearance));
        return new(character.Id, new CharacterComplementCaseCharacterBasicInformation(basic));
    }

    private static EntityLook Snapshot(EntityLook value) => new(value.Colors.ToArray(), value.LookType,
        value.Bones, value.Scales.ToArray(), value.Skins.ToArray(), value.SubEntities.Select(child =>
            new SubEntityInformation(Snapshot(child.SubEntityLook), child.BindingPointIndex, child.BindingPointCategory)).ToArray());
}

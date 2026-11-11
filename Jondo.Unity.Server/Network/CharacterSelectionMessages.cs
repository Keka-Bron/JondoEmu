using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal static class CharacterSelectionMessages
{
    internal static CharacterSelectionEventCaseSuccess Success(DatabaseManager.DbCharacter character)
        => Success(character, BreedLookTable.SemanticLook(character));

    internal static CharacterSelectionEventCaseSuccess Success(DatabaseManager.DbCharacter character, EntityLook look)
    {
        var identity = new CharacterBaseInformation(character.Sex == 1 ? Gender.Female : Gender.Male,
            new CharacterBaseInformationComplementCaseNone());
        var appearance = new CharacterLook(look, character.Breed, character.LastConnection, "",
            new CharacterLookComplementCaseBaseInformation(identity));
        var basic = new CharacterBasicInformation(character.Name, character.Level == 0 ? null : character.Level,
            new CharacterBasicInformationComplementCaseCharacterLook(appearance));
        return new(new CharacterSelectionSuccess(new Character(character.Id,
            new CharacterComplementCaseCharacterBasicInformation(basic))));
    }
}

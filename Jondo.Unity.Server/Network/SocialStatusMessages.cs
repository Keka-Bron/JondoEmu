using Jondo.Unity.Protocol.Game;

namespace Jondo.Unity.Server.Network;

internal static class SocialStatusMessages
{
    // Session values are the historical wire numbers; stable enum ordinals are independent.
    internal static CharacterStatus Status(SessionState? state, bool online = false)
        => new(state == null ? (online ? CharacterStatusStatus.Available : CharacterStatusStatus.Offline)
            : state.ChatStatus switch
            {
                0 => CharacterStatusStatus.Offline, 1 => CharacterStatusStatus.Available,
                2 => CharacterStatusStatus.Idle, 3 => CharacterStatusStatus.Afk,
                4 => CharacterStatusStatus.Private, 5 => CharacterStatusStatus.Solo,
                _ => throw new GameConversionException($"Unsupported chat status {state.ChatStatus}."),
            }, state?.ChatStatusMessage ?? "");

    internal static Gender Gender(int sex) => sex switch
    {
        0 => Jondo.Unity.Protocol.Game.Gender.Male,
        1 => Jondo.Unity.Protocol.Game.Gender.Female,
        _ => throw new GameConversionException($"Unsupported character sex {sex}."),
    };
}

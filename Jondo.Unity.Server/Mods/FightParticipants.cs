using Jondo.Unity.ServerMods;

namespace Jondo.Unity.Server.Mods;

internal static class FightParticipants
{
    internal static IFightParticipantPolicy? Policy => ServerModHooks.Get<IFightParticipantPolicy>();
}

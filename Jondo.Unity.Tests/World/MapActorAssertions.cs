using Jondo.Unity.Protocol.Game;
using Xunit;

namespace Jondo.Unity.Tests.World;

internal static class MapActorAssertions
{
    internal static HumanoidInformation Human(ActorPositionInformation actor)
    {
        var roleplay = Assert.IsType<ActorInformationCaseRolePlayActor>(actor.ActorInformation!.ActorCase).Value;
        var named = Assert.IsType<RolePlayActorCaseNamedActor>(roleplay.ActorCase).Value;
        return Assert.IsType<NamedActorCaseHumanoidInformation>(named.ActorCase).Value;
    }
}

namespace Jondo.Unity.Server.Network;

internal static class QuestProtocol
{
    // An individual objective completed with more of its step to go (ieb).
    // Unlike validation of a whole step, this has no reviewed semantic binding yet.
    internal static byte[] BuildObjectiveValidated(int questId, int objectiveId)
        => Pb.New().Var(1, questId).Var(2, objectiveId).Build();
}

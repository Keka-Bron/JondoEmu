using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Server.Network;

internal static class RaidEntryMessages
{
    internal static RunningRaidJoinedEvent Joined(GuildRaidBoard.Raid board, RaidInstance raid)
    {
        var participants = new Dictionary<string, RunningRaidParticipant>();
        foreach (var participant in board.Participants)
        {
            long id = participant.CharacterId;
            string key = GuildRaidProtocol.KeyOf(id);
            var character = DatabaseManager.GetCharacterById(id);
            var session = SessionRegistry.FindByCharacter(id);
            var card = new RaidPlayerInformation(character?.Level ?? 0, SocialStatusMessages.Gender(character?.Sex ?? 0),
                id, character?.Breed ?? 0, key, SocialStatusMessages.Status(session?.State), character?.Name ?? "");
            participants.Add(key, new(participant.Group == 0 ? null : participant.Group, null, card, State(raid, id)));
        }
        var goals = new Dictionary<int, RaidGoalState>();
        foreach (var goal in GuildRaidCatalogue.GoalsOf(raid.RaidId))
            if (raid.Goals.TryGetValue(goal.Id, out int value))
                goals.Add(goal.Id, new(value >= Math.Max(1, goal.Value)
                    ? new RaidGoalStateCaseCompleted(new()) : new RaidGoalStateCaseInProgress(new(value))));

        // The client score is signed int32. Never wrap a larger stored score.
        var state = new RunningRaidState(checked((int)raid.Score), goals, raid.Health, GuildRaidManager.VariablesOf(raid));
        return new(new(participants, raid.RaidId, GuildRaidProtocol.IsoDate(raid.EndsUtc),
            GuildRaidProtocol.KeyOf(raid.CaptainId), state), board.Uuid);
    }

    internal static RunningRaidParticipantState State(RaidInstance raid, long id)
        => (RunningRaidParticipantState)GuildRaidProtocol.StateOf(raid, id);
}

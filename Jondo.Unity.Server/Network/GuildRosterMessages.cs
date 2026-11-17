using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface IGuildRosterBackend
{
    DbCharacter? Character(long characterId);
    int Points(long characterId);
    int Contributed(long characterId);
    bool Online(long characterId);
    SessionState? State(long characterId);
    bool SharesHavenBag(long characterId);
}

/// <summary>Detached full-list replacements shared by entry, windows, joining and guild refreshes.</summary>
internal sealed class GuildRosterMessages(IGuildRosterBackend backend)
{
    internal static GuildRosterMessages Live { get; } = new(new LiveBackend());

    internal GuildMembersEvent Snapshot(IEnumerable<GuildStore.Member> members)
    {
        var result = new List<Character>();
        foreach (var member in members)
        {
            var character = backend.Character(member.CharacterId);
            if (character == null) continue;
            bool online = backend.Online(member.CharacterId);
            var state = online ? backend.State(member.CharacterId) : null;
            int contributed = backend.Contributed(member.CharacterId);
            var details = new GuildMember(backend.Points(member.CharacterId),
                new(contributed, contributed), MoodSmileyId: 0, HavenBagShared: backend.SharesHavenBag(member.CharacterId),
                Note: new(member.Note ?? "", member.NoteMs));
            // Reviewed SocialMember fields: account at 4, enrollment at 10, gender at 9.
            // The former writer reversed account/enrollment and used gender as an offline flag.
            var social = new SocialMember(0, member.Rank,
                !online ? CharacterState.NotConnected : state?.IsInFight == true ? CharacterState.InFight : CharacterState.InRolePlay,
                character.AccountId, character.Breed, details, SocialStatusMessages.Status(state, online),
                SocialStatusMessages.Gender(character.Sex), member.JoinedUtcMs);
            result.Add(new(member.CharacterId, new CharacterComplementCaseCharacterBasicInformation(
                new(character.Name, character.Level, new CharacterBasicInformationComplementCaseSocialMember(social)))));
        }
        return new(result.ToArray());
    }

    private sealed class LiveBackend : IGuildRosterBackend
    {
        public DbCharacter? Character(long id) => DatabaseManager.GetCharacterById(id);
        public int Points(long id) => Achievements.PointsOf(id);
        public int Contributed(long id) => GuildStore.ContributedBy(id);
        public bool Online(long id) => SessionRegistry.IsOnline(id);
        public SessionState? State(long id) => SessionRegistry.FindByCharacter(id)?.State;
        public bool SharesHavenBag(long id) => (HavenBagStore.SharedOf(id) & HavenBagStore.SharedWithGuild) != 0;
    }
}

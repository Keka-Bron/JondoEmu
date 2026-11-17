using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class GuildRosterTests
{
    [Fact]
    public void Roster_is_one_detached_replacement_in_member_order_skipping_missing_characters()
    {
        var backend = new Backend();
        var first = new GuildStore.Member { CharacterId = 71_001, Rank = 83_005, JoinedUtcMs = 8_000_000_003, Note = "note", NoteMs = 8_000_000_004 };
        var second = new GuildStore.Member { CharacterId = 71_002, Rank = 4 };
        var rows = new List<GuildStore.Member> { first, new() { CharacterId = 71_099 }, second };
        var messages = new GuildRosterMessages(backend);
        var snapshot = messages.Snapshot(rows);
        Assert.Equal(new long[] { 71_001, 71_002 }, snapshot.Members.Select(member => member.Id));
        var basic = Assert.IsType<CharacterComplementCaseCharacterBasicInformation>(snapshot.Members[0].ComplementCase).Value;
        Assert.Equal("One", basic.Name);
        Assert.Equal(357, basic.Level);
        var social = Social(snapshot.Members[0]);
        Assert.Equal(9_000_000_001, social.AccountId);
        Assert.Equal(8_000_000_003, social.EnrollmentDate);
        Assert.Equal(83_005, social.RankId);
        Assert.Equal(Gender.Female, social.Gender);
        Assert.Equal(CharacterState.InFight, social.State);
        Assert.Equal(new CharacterStatus(CharacterStatusStatus.Private, "busy"), social.Status);
        Assert.Equal(new GuildMember(8094, new(70, 70), 0, true, new("note", 8_000_000_004)), social.GuildMember);
        var offline = Social(snapshot.Members[1]);
        Assert.Equal(CharacterState.NotConnected, offline.State);
        Assert.Equal(Gender.Male, offline.Gender);
        Assert.Equal(new CharacterStatus(CharacterStatusStatus.Offline, ""), offline.Status);
        Assert.Equal(new GuildMember(0, new(0, 0), 0, false, new("", 0)), offline.GuildMember);
        rows.Clear();
        first.Note = "changed";
        first.Rank = 99;
        backend.Character(71_001)!.Name = "changed";
        backend.OnlineState.ChatStatusMessage = "changed";
        Assert.Equal("One", basic.Name);
        Assert.Equal("note", social.GuildMember!.Note!.Content);
        Assert.Equal("busy", social.Status!.Message);
        Assert.Empty(messages.Snapshot(rows).Members);
    }

    [Fact]
    public void Online_hero_without_socket_gets_available_status_and_roleplay_state()
    {
        var backend = new Backend { Hero = true };
        var social = Social(Assert.Single(new GuildRosterMessages(backend).Snapshot([new() { CharacterId = 71_002 }]).Members));
        Assert.Equal(CharacterState.InRolePlay, social.State);
        Assert.Equal(new CharacterStatus(CharacterStatusStatus.Available, ""), social.Status);
    }

    [Fact]
    public void Failed_source_does_not_return_a_partial_replacement()
    {
        var backend = new Backend { Fail = true };
        Assert.Throws<IOException>(() => new GuildRosterMessages(backend).Snapshot([new() { CharacterId = 71_002 }, new() { CharacterId = 71_001 }]));
    }

    [Theory]
    [InlineData(0, CharacterStatusStatus.Offline)]
    [InlineData(1, CharacterStatusStatus.Available)]
    [InlineData(2, CharacterStatusStatus.Idle)]
    [InlineData(3, CharacterStatusStatus.Afk)]
    [InlineData(4, CharacterStatusStatus.Private)]
    [InlineData(5, CharacterStatusStatus.Solo)]
    public void Status_uses_explicit_wire_to_semantic_correspondence(int value, CharacterStatusStatus expected)
        => Assert.Equal(expected, SocialStatusMessages.Status(new() { ChatStatus = value }).Status);

    internal static SocialMember Social(Character member)
        => Assert.IsType<CharacterBasicInformationComplementCaseSocialMember>(
            Assert.IsType<CharacterComplementCaseCharacterBasicInformation>(member.ComplementCase).Value.ComplementCase).Value;

    private sealed class Backend : IGuildRosterBackend
    {
        internal bool Hero, Fail;
        internal readonly SessionState OnlineState = new() { ChatStatus = 4, ChatStatusMessage = "busy", IsInFight = true };
        private readonly Dictionary<long, DbCharacter> _characters = new()
        {
            [71_001] = new() { Id = 71_001, Name = "One", AccountId = 9_000_000_001, Level = 357, Sex = 1, Breed = 12 },
            [71_002] = new() { Id = 71_002, Name = "Two", AccountId = 9_000_000_002, Level = 50, Breed = 8 },
        };
        public DbCharacter? Character(long id) => _characters.GetValueOrDefault(id);
        public int Points(long id) => id == 71_001 ? (Fail ? throw new IOException("points source") : 8094) : 0;
        public int Contributed(long id) => id == 71_001 ? 70 : 0;
        public bool Online(long id) => id == 71_001 || Hero;
        public SessionState? State(long id) => id == 71_001 ? OnlineState : null;
        public bool SharesHavenBag(long id) => id == 71_001;
    }
}

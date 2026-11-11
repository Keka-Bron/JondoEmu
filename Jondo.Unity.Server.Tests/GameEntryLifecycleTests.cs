using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class GameEntryLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly GameCodec Codec = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Identification_waits_for_old_sessions_before_reading_characters_and_selecting_the_welcome(bool resumable)
    {
        var h = new Harness(authenticated: false);
        if (resumable) h.Backend.Resumable.Add(h.Character.Id);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Backend.CloseOthers = closed.Task;
        var ticket = SessionRegistry.Issue(h.Character.AccountId, h.Character.ServerId, "fr");
        var unrelated = new Harness();
        using var context = SessionContext.Push(unrelated.Session);
        Task<GameEntryResult> identifying = h.Send(new IdentificationRequest(ticket.Value, "en"));
        try
        {
            Assert.Equal(new[] { "ticket", "close-others" }, h.Backend.Calls);
            Assert.False(identifying.IsCompleted);
            Assert.Same(unrelated.Session, SessionContext.Actual);
        }
        finally { closed.TrySetResult(); }

        Assert.Equal(GameEntryResult.Handled, await identifying.WaitAsync(Timeout));
        Assert.Equal(new[] { "ticket", "close-others", "characters", "welcome" }, h.Backend.Calls);
        Assert.Equal(!resumable, h.Backend.WelcomeWithList);
        Assert.Equal(h.Character.AccountId, h.Session.AccountId);
        Assert.Equal(h.Character.ServerId, h.Session.ServerId);
        Assert.Equal("fr", h.Session.State.Language);
        Assert.Null(SessionRegistry.Redeem(ticket.Value));
        Assert.Equal(GameEntryResult.Close, await h.Send(new IdentificationRequest(ticket.Value, "en")));
        Assert.Same(unrelated.Session, SessionContext.Actual);
    }

    [Fact]
    public async Task Invalid_ticket_closes_without_welcome_or_character_lookup()
    {
        var h = new Harness(authenticated: false);
        Assert.Equal(GameEntryResult.Close, await h.Send(new IdentificationRequest(Guid.NewGuid().ToString("N"), "en")));
        Assert.Equal(new[] { "ticket" }, h.Backend.Calls);
        Assert.False(h.Session.IsAuthenticated);
        Assert.Empty(h.Packets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Character_list_only_forces_continuation_when_an_owned_character_has_a_resumable_fight(bool resumable)
    {
        var h = new Harness();
        if (resumable) h.Backend.Resumable.Add(h.Character.Id);

        Assert.Equal(GameEntryResult.Handled, await h.Send(new CharacterListRequest(), int.MinValue));

        Assert.Equal(!resumable, h.Backend.ListWithInitialization);
        Assert.Equal(resumable ? new[] { "kvi", "kvd" } : new[] { "kvi" }, h.Aliases());
        Assert.All(h.Packets, packet => Assert.Null(Codec.ReadEnvelope(packet).Uid));
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("first-tutorial")]
    [InlineData("first-no-tutorial")]
    public async Task Selection_uses_the_requested_id_and_abandons_the_old_fight_before_initialization(string kind)
    {
        var h = new Harness();
        h.Backend.Resumable.Add(h.Character.Id);
        GameRequest request = kind == "ordinary" ? new CharacterSelectionRequest(h.Character.Id)
            : new CharacterFirstSelectionRequest(kind == "first-tutorial", h.Character.Id);

        Assert.Equal(GameEntryResult.Handled, await h.Send(request));

        Assert.Equal(new[] { "select", "abandon", "find", "load", "presence", "initial", "raid", "guild", "friends", "remodel" },
            h.Backend.Calls);
        Assert.Equal(h.Character.Id, h.Session.CharacterId);
        Assert.True(h.Session.IsInWorld);
        Assert.False(h.Session.State.IsInFight);
        Assert.DoesNotContain(h.Character.Id, h.Backend.Resumable);
    }

    [Theory]
    [InlineData("foreign-account")]
    [InlineData("foreign-server")]
    [InlineData("missing")]
    public async Task Rejected_selection_never_enters_or_announces_the_character(string scenario)
    {
        var h = new Harness();
        if (scenario == "foreign-account") h.Character.AccountId++;
        if (scenario == "foreign-server") h.Character.ServerId++;
        long requested = scenario == "missing" ? h.Character.Id + 1 : h.Character.Id;

        Assert.Equal(GameEntryResult.Close, await h.Send(new CharacterSelectionRequest(requested)));

        Assert.Equal(new[] { "select" }, h.Backend.Calls);
        Assert.False(h.Session.IsInWorld);
        Assert.False(h.Session.HasCharacter);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Force_selection_only_resumes_a_character_from_the_owning_account_and_server(bool ownFight)
    {
        var h = new Harness();
        var foreign = new DatabaseManager.DbCharacter
        {
            Id = h.Character.Id + 1, AccountId = h.Session.AccountId + 1, ServerId = h.Session.ServerId, Name = "Other",
        };
        h.Backend.Characters.Insert(0, foreign);
        h.Backend.Resumable.Add(foreign.Id);
        if (ownFight) h.Backend.Resumable.Add(h.Character.Id);

        Assert.Equal(ownFight ? GameEntryResult.Handled : GameEntryResult.Close,
            await h.Send(new CharacterForceSelectionReadyRequest()));

        Assert.Equal(ownFight, h.Session.IsInWorld);
        Assert.Equal(ownFight, h.Session.State.IsInFight);
        Assert.Equal(ownFight ? h.Character.Id : 0, h.Session.CharacterId);
        Assert.DoesNotContain("presence", h.Backend.Calls);
        Assert.DoesNotContain("abandon", h.Backend.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sequence_confirmation_and_first_ping_initialize_the_map_only_once(bool pingFirst)
    {
        var h = new Harness();
        await h.Enter();
        Assert.Equal(GameEntryResult.Handled, await h.Send(pingFirst ? new PingRequest(true) : new SequenceNumberRequest(12)));
        Assert.Equal(GameEntryResult.Handled, await h.Send(new SequenceNumberRequest(13)));
        Assert.Equal(GameEntryResult.Handled, await h.Send(new PingRequest(true)));

        Assert.Equal(1, h.Backend.Calls.Count(c => c == "map-initialization"));
        Assert.Equal(1, h.Backend.Calls.Count(c => c == "tick"));
        Assert.Equal(new[] { "kqy", "jru", "kqy" }, h.Aliases());
        Assert.Equal(new MapCurrentEvent(h.Session.MapId), Codec.Decode(h.Packets[1]).Content);
        Assert.Equal(new PongEvent(true), Codec.Decode(h.Packets[2]).Content);
    }

    [Fact]
    public async Task Authenticated_ping_before_selection_is_only_a_heartbeat()
    {
        var h = new Harness();
        Assert.Equal(GameEntryResult.Handled, await h.Send(new PingRequest(false)));
        Assert.Equal(new[] { "tick" }, h.Backend.Calls);
        Assert.Equal(new PongEvent(false), Codec.Decode(Assert.Single(h.Packets)).Content);
    }

    [Fact]
    public async Task Map_travel_suppresses_entry_replay_and_returning_to_selection_resets_the_lifecycle()
    {
        var h = new Harness();
        await h.Enter();
        h.Service.OnMapChange();
        await h.Send(new SequenceNumberRequest(1));
        await h.Send(new PingRequest(false));
        Assert.DoesNotContain("map-initialization", h.Backend.Calls);

        await h.Service.ReturnToSelectionAsync();
        Assert.False(h.Session.IsInWorld);
        Assert.True(h.Session.IsAuthenticated);
        Assert.Equal(new[] { "return-token", "prepare-return", "remove-from-map" }, h.Backend.Calls.TakeLast(3));
        await h.Send(new CharacterSelectionRequest(h.Character.Id));
        await h.Send(new SequenceNumberRequest(2));
        await h.Send(new SequenceNumberRequest(3));
        Assert.Equal(1, h.Backend.Calls.Count(c => c == "map-initialization"));
    }

    [Fact]
    public async Task Returning_after_initialized_entry_allows_one_new_map_initialization()
    {
        var h = new Harness();
        await h.Enter();
        await h.Send(new SequenceNumberRequest(1));
        await h.Service.ReturnToSelectionAsync();
        await h.Send(new CharacterSelectionRequest(h.Character.Id));
        await h.Send(new SequenceNumberRequest(2));
        await h.Send(new PingRequest(false));
        Assert.Equal(2, h.Backend.Calls.Count(c => c == "map-initialization"));
    }

    [Fact]
    public async Task Independent_sessions_do_not_share_map_initialization_or_ambient_context()
    {
        var first = new Harness(accountId: 90_000_201);
        var second = new Harness(accountId: 90_000_202);
        using var context = SessionContext.Push(first.Session);
        await Task.WhenAll(first.Enter(), second.Enter());
        await Task.WhenAll(first.Send(new SequenceNumberRequest(1)), second.Send(new PingRequest(true)));
        await first.Send(new PingRequest(false));

        Assert.Same(first.Session, SessionContext.Actual);
        Assert.Equal(1, first.Backend.Calls.Count(c => c == "map-initialization"));
        Assert.Equal(1, second.Backend.Calls.Count(c => c == "map-initialization"));
        Assert.Equal(new[] { "kqy", "jru" }, second.Aliases());
    }

    [Theory]
    [InlineData("map")]
    [InlineData("context")]
    [InlineData("fight")]
    public async Task Pending_fight_readiness_wins_over_roleplay_map_validation(string kind)
    {
        var h = new Harness();
        await h.Enter();
        h.Session.State.IsInFight = true;
        h.Backend.FightMapPending = true;
        GameRequest request = kind switch
        {
            "map" => new MapInformationRequest(checked((int)h.Session.MapId + 1)),
            "context" => new ContextReadyRequest(h.Session.MapId + 1),
            _ => new FightMapRequest(),
        };

        Assert.Equal(GameEntryResult.Handled, await h.Send(request));
        Assert.Equal(new[] { "fight-map" }, h.Backend.Calls);
    }

    [Fact]
    public async Task Map_information_repeats_for_travel_but_never_sends_roleplay_actors_during_a_fight()
    {
        var h = new Harness();
        await h.Enter();
        await h.Send(new MapInformationRequest(checked((int)h.Session.MapId)));
        await h.Send(new MapInformationRequest(checked((int)h.Session.MapId)));
        Assert.Equal(2, h.Backend.Calls.Count(c => c == "actors"));
        Assert.DoesNotContain("map-initialization", h.Backend.Calls);

        h.Session.State.IsInFight = true;
        Assert.Equal(GameEntryResult.Handled, await h.Send(new MapInformationRequest(checked((int)h.Session.MapId))));
        Assert.Equal(2, h.Backend.Calls.Count(c => c == "actors"));
        Assert.Equal(GameEntryResult.Unhandled, await h.Send(new FightMapRequest()));
        Assert.Equal(GameEntryResult.Close, await h.Send(new MapInformationRequest(checked((int)h.Session.MapId + 1))));
        Assert.Equal(GameEntryResult.Close, await h.Send(new ContextReadyRequest(h.Session.MapId + 1)));
    }

    [Fact]
    public async Task Invalid_phases_and_missing_request_uid_close_before_any_backend_effect()
    {
        var h = new Harness(authenticated: false);
        GameRequest[] requests = [new CharacterListRequest(), new CharacterSelectionRequest(h.Character.Id),
            new CharacterForceSelectionReadyRequest(), new SequenceNumberRequest(1), new MapInformationRequest(42),
            new ContextReadyRequest(42), new PingRequest(false)];
        foreach (var request in requests) Assert.Equal(GameEntryResult.Close, await h.Send(request));
        Assert.Equal(GameEntryResult.Close,
            await h.Service.HandleAsync(new GameFrame(new IdentificationRequest("unused", "en"))));
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public async Task Failed_map_send_escapes_without_a_pong_fallback_or_other_session_side_effects()
    {
        var h = new Harness();
        await h.Enter();
        var failure = new IOException("Map transport failed");
        h.Backend.BeforeMap = Task.FromException(failure);
        var unrelated = new Harness(accountId: 90_000_203);
        using var context = SessionContext.Push(unrelated.Session);

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => h.Send(new PingRequest(false))));
        Assert.Equal(new[] { "find", "map-initialization" }, h.Backend.Calls);
        Assert.Empty(h.Packets);
        Assert.Empty(unrelated.Backend.Calls);
        Assert.Same(unrelated.Session, SessionContext.Actual);
    }

    private sealed class Harness
    {
        public GameSession Session { get; }
        public Backend Backend { get; }
        public GameEntryService Service { get; }
        public DatabaseManager.DbCharacter Character { get; }
        public List<byte[]> Packets { get; } = [];

        public Harness(bool authenticated = true, long accountId = 90_000_200)
        {
            Session = new GameSession(Codec, packet => { Packets.Add(packet); return Task.CompletedTask; });
            if (authenticated) Session.BindAccount(accountId, 290, "fr");
            Character = new() { Id = accountId + 1000, AccountId = accountId, ServerId = 290, Name = "Adventurer" };
            Backend = new Backend(Session, Character);
            Service = new GameEntryService(Session, Backend);
        }

        public Task<GameEntryResult> Send(GameRequest request, int uid = -1) => Service.HandleAsync(new(request, uid));
        public string[] Aliases() => Packets.Select(p => Codec.ReadEnvelope(p).Alias).ToArray();
        public async Task Enter()
        {
            Assert.Equal(GameEntryResult.Handled, await Send(new CharacterSelectionRequest(Character.Id)));
            Backend.Calls.Clear();
            Packets.Clear();
        }
    }

    private sealed class Backend(GameSession owner, DatabaseManager.DbCharacter character) : IGameEntryBackend
    {
        public List<string> Calls { get; } = [];
        public List<DatabaseManager.DbCharacter> Characters { get; } = [character];
        public HashSet<long> Resumable { get; } = [];
        public Task CloseOthers { get; set; } = Task.CompletedTask;
        public Task BeforeMap { get; set; } = Task.CompletedTask;
        public bool? WelcomeWithList { get; private set; }
        public bool? ListWithInitialization { get; private set; }
        public bool FightMapPending { get; set; }
        public string WelcomeMessage => "";

        private void Record(string operation, GameSession? session = null)
        {
            Assert.Same(owner, SessionContext.Actual);
            if (session != null) Assert.Same(owner, session);
            Calls.Add(operation);
        }

        public bool BindTicket(GameSession session, IdentificationRequest request)
        {
            Record("ticket", session);
            return GameEntryDispatch.BindTicket(session, request);
        }

        public async Task CloseOtherSessionsAsync(GameSession session)
        {
            Record("close-others", session);
            await CloseOthers;
        }

        public IReadOnlyList<DatabaseManager.DbCharacter> GetCharacters(GameSession session)
        {
            Record("characters", session);
            return Characters.Where(c => c.AccountId == session.AccountId && c.ServerId == session.ServerId).ToArray();
        }

        public bool CanResumeFight(long characterId) => Resumable.Contains(characterId);
        public bool SelectCharacter(GameSession session, long characterId)
        {
            Record("select", session);
            var selected = Characters.FirstOrDefault(c => c.Id == characterId
                && c.AccountId == session.AccountId && c.ServerId == session.ServerId);
            if (selected == null) return false;
            session.State.CharacterId = selected.Id;
            session.State.MapId = 154010371;
            return true;
        }

        public bool ResumeFight(GameSession session, long characterId)
        {
            Record("resume", session);
            if (!Resumable.Contains(characterId) || !SelectCharacter(session, characterId)) return false;
            session.State.IsInFight = true;
            return true;
        }

        public Task AbandonFightAsync(long characterId)
        {
            Record("abandon");
            Assert.Equal(owner.CharacterId, characterId);
            Resumable.Remove(characterId);
            owner.State.IsInFight = false;
            return Task.CompletedTask;
        }

        public Task SendWelcomeAsync(GameSession session, IReadOnlyList<DatabaseManager.DbCharacter> characters, bool withList)
        {
            Record("welcome", session);
            WelcomeWithList = withList;
            return Task.CompletedTask;
        }

        public Task SendCharacterListAsync(GameSession session, IReadOnlyList<DatabaseManager.DbCharacter> characters, bool withInitialization)
        {
            Record("list", session);
            ListWithInitialization = withInitialization;
            return session.SendAsync(new CharacterListEvent([]));
        }

        public DatabaseManager.DbCharacter? FindCharacter(long characterId)
        {
            Record("find");
            return Characters.FirstOrDefault(c => c.Id == characterId);
        }

        public void LoadCharacterState(GameSession session, DatabaseManager.DbCharacter selected) => Record("load", session);
        public Task AnnounceMapPresenceAsync(GameSession session, DatabaseManager.DbCharacter selected) { Record("presence", session); return Task.CompletedTask; }
        public Task SendInitialStateAsync(GameSession session, DatabaseManager.DbCharacter selected) { Record("initial", session); return Task.CompletedTask; }
        public Task RestoreGuildRaidAsync(long characterId) { Record("raid"); return Task.CompletedTask; }
        public Task NotifyGuildConnectionAsync(long characterId) { Record("guild"); return Task.CompletedTask; }
        public Task NotifyFriendsAsync(long accountId) { Record("friends"); return Task.CompletedTask; }
        public Task RestoreRemodelAsync(GameSession session, DatabaseManager.DbCharacter selected) { Record("remodel", session); return Task.CompletedTask; }

        public async Task SendMapInitializationAsync(GameSession session, DatabaseManager.DbCharacter selected)
        {
            Record("map-initialization", session);
            await BeforeMap;
            await Task.Yield();
            Assert.Same(owner, SessionContext.Actual);
            await session.SendAsync(new PongEvent(false));
            await session.SendAsync(new MapCurrentEvent(session.MapId));
        }

        public Task<bool> TrySendFightMapAsync(GameSession session, GameRequest request)
        {
            Record("fight-map", session);
            return Task.FromResult(FightMapPending);
        }

        public Task SendMapInformationAsync(GameSession session, DatabaseManager.DbCharacter selected) { Record("actors", session); return Task.CompletedTask; }
        public Task TickAlterationsAsync(GameSession session) { Record("tick", session); return Task.CompletedTask; }
        public Task SendReturnToSelectionAsync(GameSession session) { Record("return-token", session); return Task.CompletedTask; }
        public void PrepareReturnToSelection(GameSession session) => Record("prepare-return", session);
        public Task RemoveFromMapAsync(GameSession session) { Record("remove-from-map", session); return Task.CompletedTask; }
    }
}

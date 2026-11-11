using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class GameEntryServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly GameCodec Codec = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Entry_loads_the_owning_session_and_awaits_initial_state_before_notifications(bool inFight)
    {
        var packets = new List<byte[]>();
        var session = SelectedSession(packet => { packets.Add(packet); return Task.CompletedTask; });
        session.State.IsInFight = inFight;
        var releaseInitialState = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend(session)
        {
            InitialState = _ => releaseInitialState.Task,
            WelcomeMessage = "  Welcome, adventurer  ",
        };
        var service = new GameEntryService(session, backend);
        var unrelated = SelectedSession(_ => Task.CompletedTask);
        using var context = SessionContext.Push(unrelated);
        // The real packet loop already holds this turn; the service must not take it again.
        using var turn = await session.EnterTurnAsync();

        Task<bool> entering = service.EnterWorldAsync();
        try
        {
            await backend.InitialStateStarted.Task.WaitAsync(Timeout);
            Assert.Same(unrelated, SessionContext.Actual);
            Assert.True(session.IsInWorld);
            Assert.False(unrelated.IsInWorld);
            Assert.Equal(new[] { 123, 456 }, session.State.ElementsUsed.Order());
            Assert.Empty(unrelated.State.ElementsUsed);
            Assert.Equal(inFight
                ? new[] { "find", "load", "initial" }
                : new[] { "find", "load", "presence", "initial" }, backend.Calls);
            Assert.Empty(packets);
            Assert.False(entering.IsCompleted);
        }
        finally
        {
            releaseInitialState.TrySetResult();
        }

        Assert.True(await entering.WaitAsync(Timeout));
        Assert.Same(unrelated, SessionContext.Actual);
        var expected = new List<string> { "find", "load" };
        if (!inFight) expected.Add("presence");
        expected.AddRange(["initial", "raid", "guild", "friends", "remodel", "welcome"]);
        Assert.Equal(expected, backend.Calls);

        byte[] welcome = Assert.Single(packets);
        var envelope = Codec.ReadEnvelope(welcome);
        Assert.Equal(GameMessageKind.Event, envelope.Kind);
        Assert.Null(envelope.Uid);
        Assert.Equal("lqn", envelope.Alias);
        // Pin compatibility with the original notice independently of stable encode/decode.
        Assert.Equal(ConnectionProtocol.BuildNotice("Welcome, adventurer"), envelope.Content.Value.ToByteArray());
        var message = Assert.IsType<TextInformationEvent>(Codec.Decode(welcome).Content);
        Assert.Equal(TextInformationType.TextInformationMessage, message.MessageType);
        Assert.Equal(new[] { "Welcome, adventurer" }, message.Parameters);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task Entry_rejects_invalid_phases_before_backend_side_effects(bool authenticated, bool selected, bool inWorld)
    {
        var session = new GameSession(Codec, _ => throw new InvalidOperationException("Unexpected send"));
        if (authenticated) session.BindAccount(90_000_100, 290, "fr");
        if (selected) session.State.CharacterId = 987654321;
        if (inWorld) session.EnterWorld();
        var backend = new Backend(session);

        Assert.False(await new GameEntryService(session, backend).EnterWorldAsync());

        Assert.Empty(backend.Calls);
        Assert.Equal(inWorld, session.IsInWorld);
    }

    [Fact]
    public async Task Missing_character_stops_before_loading_or_entering_the_world()
    {
        var session = SelectedSession(_ => throw new InvalidOperationException("Unexpected send"));
        var backend = new Backend(session) { MissingCharacter = true };
        var unrelated = SelectedSession(_ => Task.CompletedTask);
        using var context = SessionContext.Push(unrelated);

        Assert.False(await new GameEntryService(session, backend).EnterWorldAsync());

        Assert.Equal(new[] { "find" }, backend.Calls);
        Assert.False(session.IsInWorld);
        Assert.Empty(session.State.ElementsUsed);
        Assert.Same(unrelated, SessionContext.Actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\n ")]
    public async Task Empty_welcome_setting_sends_no_notice(string welcome)
    {
        var session = SelectedSession(_ => throw new InvalidOperationException("Unexpected send"));
        var backend = new Backend(session) { WelcomeMessage = welcome };

        Assert.True(await new GameEntryService(session, backend).EnterWorldAsync());

        Assert.Contains("remodel", backend.Calls);
        Assert.Equal("welcome", backend.Calls[^1]);
    }

    [Fact]
    public async Task Failed_initial_send_propagates_without_retry_or_later_notifications_and_restores_context()
    {
        int sends = 0;
        var failure = new IOException("Client disconnected");
        var session = SelectedSession(_ => { sends++; return Task.FromException(failure); });
        var backend = new Backend(session)
        {
            InitialState = owner => owner.SendAsync(new QueueStatusEvent(0, 0)),
        };
        var unrelated = SelectedSession(_ => Task.CompletedTask);
        using var context = SessionContext.Push(unrelated);

        var error = await Assert.ThrowsAsync<IOException>(() => new GameEntryService(session, backend).EnterWorldAsync());

        Assert.Same(failure, error);
        Assert.Equal(1, sends);
        Assert.Equal(new[] { "find", "load", "presence", "initial" }, backend.Calls);
        Assert.Same(unrelated, SessionContext.Actual);
    }

    [Theory]
    [InlineData(-17)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public async Task Subscription_dispatch_uses_the_owning_world_session_and_sends_events_without_request_uid(int uid)
    {
        var packets = new List<byte[]>();
        var session = SelectedSession(packet => { packets.Add(packet); return Task.CompletedTask; });
        session.EnterWorld();
        var wrong = SelectedSession(_ => throw new Exception("Wrong recipient"));
        using var context = SessionContext.Push(wrong);
        using var turn = await session.EnterTurnAsync();
        var request = new ChatChannelSubscriptionRequest([ChatChannel.Private], [ChatChannel.Fight]);
        var payload = Codec.Encode(new(request, uid));
        var frame = GameEntryDispatch.Decode(session, payload, ConnectionProtocol.UriPrefix + "ktn");
        Assert.NotNull(frame);
        Assert.Equal(uid, frame.Uid);
        var backend = new Backend(session);
        Assert.Equal(GameEntryResult.Handled, await new GameEntryService(session, backend).HandleAsync(frame).WaitAsync(Timeout));
        Assert.Empty(backend.Calls);
        Assert.Equal(21, packets.Count);
        Assert.Equal(new[] { ChatChannel.Private }, Assert.IsType<ChatChannelsEnabledEvent>(Codec.Decode(packets[0]).Content).ActiveChannels);
        Assert.All(packets, packet => Assert.Null(Codec.ReadEnvelope(packet).Uid));
        Assert.Null(wrong.State.ActiveChatChannels);
        Assert.Same(wrong, SessionContext.Actual);
    }

    [Fact]
    public async Task Subscription_before_world_entry_is_closed_without_changing_state_or_sending()
    {
        var session = SelectedSession(_ => throw new Exception("Unexpected send"));
        Assert.Equal(GameEntryResult.Close, await new GameEntryService(session, new Backend(session))
            .HandleAsync(new(new ChatChannelSubscriptionRequest([ChatChannel.Private], []), 0)));
        Assert.Null(session.State.ActiveChatChannels);
    }

    private static GameSession SelectedSession(Func<byte[], Task> send)
    {
        var session = new GameSession(Codec, send);
        session.BindAccount(90_000_100, 290, "fr");
        session.State.CharacterId = 987654321;
        session.State.MapId = 154010371;
        return session;
    }

    private sealed class Backend(GameSession owner) : IGameEntryBackend
    {
        public bool BindTicket(GameSession session, IdentificationRequest request) => throw new NotSupportedException();
        public Task CloseOtherSessionsAsync(GameSession session) => throw new NotSupportedException();
        public IReadOnlyList<DatabaseManager.DbCharacter> GetCharacters(GameSession session) => throw new NotSupportedException();
        public bool CanResumeFight(long characterId) => throw new NotSupportedException();
        public bool SelectCharacter(GameSession session, long characterId) => throw new NotSupportedException();
        public bool ResumeFight(GameSession session, long characterId) => throw new NotSupportedException();
        public Task AbandonFightAsync(long characterId) => throw new NotSupportedException();
        public Task SendWelcomeAsync(GameSession session, IReadOnlyList<DatabaseManager.DbCharacter> characters, bool withList)
            => throw new NotSupportedException();
        public Task SendCharacterListAsync(GameSession session, IReadOnlyList<DatabaseManager.DbCharacter> characters, bool withInitialization)
            => throw new NotSupportedException();
        public Task SendMapInitializationAsync(GameSession session, DatabaseManager.DbCharacter character) => throw new NotSupportedException();
        public Task<bool> TrySendFightMapAsync(GameSession session, GameRequest request) => throw new NotSupportedException();
        public Task SendMapInformationAsync(GameSession session, DatabaseManager.DbCharacter character) => throw new NotSupportedException();
        public Task TickAlterationsAsync(GameSession session) => throw new NotSupportedException();
        public Task SendReturnToSelectionAsync(GameSession session) => throw new NotSupportedException();
        public void PrepareReturnToSelection(GameSession session) => throw new NotSupportedException();
        public Task RemoveFromMapAsync(GameSession session) => throw new NotSupportedException();

        public List<string> Calls { get; } = [];
        public bool MissingCharacter { get; init; }
        public Func<GameSession, Task> InitialState { get; init; } = _ => Task.CompletedTask;
        public TaskCompletionSource InitialStateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string _welcomeMessage = "";
        public string WelcomeMessage
        {
            get { Record("welcome"); return _welcomeMessage; }
            init => _welcomeMessage = value;
        }

        public DatabaseManager.DbCharacter? FindCharacter(long characterId)
        {
            Record("find");
            Assert.Equal(owner.CharacterId, characterId);
            return MissingCharacter ? null : new DatabaseManager.DbCharacter
            {
                Id = characterId, AccountId = owner.AccountId, ServerId = owner.ServerId, Name = "Adventurer",
            };
        }

        public void LoadCharacterState(GameSession session, DatabaseManager.DbCharacter character)
        {
            Record("load", session, character);
            Assert.False(session.IsInWorld);
            session.State.ElementsUsed = [123, 456];
        }

        public Task AnnounceMapPresenceAsync(GameSession session, DatabaseManager.DbCharacter character)
        {
            Record("presence", session, character);
            Assert.True(session.IsInWorld);
            return Task.CompletedTask;
        }

        public async Task SendInitialStateAsync(GameSession session, DatabaseManager.DbCharacter character)
        {
            Record("initial", session, character);
            Assert.True(session.IsInWorld);
            InitialStateStarted.TrySetResult();
            await InitialState(session);
            Assert.Same(owner, SessionContext.Actual);
        }

        public Task RestoreGuildRaidAsync(long characterId)
        {
            Record("raid");
            Assert.Equal(owner.CharacterId, characterId);
            return Task.CompletedTask;
        }

        public Task NotifyGuildConnectionAsync(long characterId)
        {
            Record("guild");
            Assert.Equal(owner.CharacterId, characterId);
            return Task.CompletedTask;
        }

        public Task NotifyFriendsAsync(long accountId)
        {
            Record("friends");
            Assert.Equal(owner.AccountId, accountId);
            return Task.CompletedTask;
        }

        public Task RestoreRemodelAsync(GameSession session, DatabaseManager.DbCharacter character)
        {
            Record("remodel", session, character);
            return Task.CompletedTask;
        }

        private void Record(string operation, GameSession? session = null, DatabaseManager.DbCharacter? character = null)
        {
            Assert.Same(owner, SessionContext.Actual);
            if (session != null) Assert.Same(owner, session);
            if (character != null)
            {
                Assert.Equal(owner.CharacterId, character.Id);
                Assert.Equal(owner.AccountId, character.AccountId);
                Assert.Equal(owner.ServerId, character.ServerId);
            }
            Calls.Add(operation);
        }
    }
}

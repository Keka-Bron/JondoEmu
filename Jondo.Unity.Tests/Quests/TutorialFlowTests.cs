using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Tests.Quests;

[Collection("MapManager")]
public sealed class TutorialFlowTests
{
    private const int Island = 241437185, Destination = 241443329;

    // Record semantic sends, independently of what the client actually receives.
    private sealed class Client : IGameCodec
    {
        internal readonly GameCodec Profile = new();
        internal readonly List<GameFrame> Sent = [];
        public byte[] Encode(GameFrame frame)
        {
            Sent.Add(frame);
            return Profile.Encode(frame);
        }
        public GameFrame Decode(ReadOnlySpan<byte> packet) => Profile.Decode(packet);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task New_character_can_accept_walk_and_leave_the_guided_tutorial(bool walk)
    {
        long account = walk ? 8_965_720_001 : 8_965_720_002;
        var client = new Client();
        await using var pipe = await ClientPipe.OpenAsync(account, 0, 0, codec: client, enterWorld: false);
        var session = pipe.Session;
        var entry = new GameEntryService(session, new GameEntryBackend());
        using var scope = SessionContext.Push(session);
        async Task<List<GamePayload>> Act(GameRequest request)
        {
            client.Sent.Clear();
            using var turn = await session.EnterTurnAsync();
            Assert.Equal(GameEntryResult.Handled, await GameNodeProxy.DispatchPacketAsync(session,
                client.Profile.Encode(new(request, -1)), entry, () => throw new InvalidOperationException($"Legacy dispatch: {request}")));
            // Fence this request on the loopback socket. A map broadcast may use the
            // owner's codec but exclude the traveler; every delivery to the traveler
            // must still have gone through that codec, in order.
            var sent = new Queue<GameFrame>(client.Sent);
            var fence = new QuestStepValidatedEvent(-1, -1);
            await session.SendAsync(fence);
            var received = new List<GamePayload>();
            while (true)
            {
                var message = client.Profile.Decode(await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer)
                    .WaitAsync(TimeSpan.FromSeconds(5)));
                if (message.Content.Equals(fence)) break;
                while (sent.TryPeek(out var next) && next.Content.GetType() != message.Content.GetType()) sent.Dequeue();
                Assert.True(sent.TryDequeue(out _), $"Unexpected {message.Content.GetType().Name} after {request.GetType().Name}");
                Assert.Equal(message.Content is GameResponse ? -1 : (int?)null, message.Uid);
                received.Add(message.Content);
            }
            return received;
        }

        MapManager.Initialize();
        Npcs.Initialize();
        QuestEngine.Load();
        DatabaseManager.EnsureProgressionTables();
        long character = 0;
        try
        {
            string name = "Guide-" + string.Concat(Guid.NewGuid().ToString("N")[..8].Select(c => (char)('a' + Convert.ToInt32(c.ToString(), 16))));
            var created = await Act(new CharacterCreationRequest(new(name, 0, [-1], null, 0, Gender.Male, 11)));
            character = Assert.Single(DatabaseManager.GetCharactersByAccountId(account, session.ServerId)).Id;
            Assert.Contains(new CharacterCreationResultEvent(NameCompliance.NameOk, CharacterCreationResult.CreateOk), created);
            await Act(new CharacterFirstSelectionRequest(true, character));
            Assert.Equal(Island, session.MapId);
            Assert.Contains(new MapCurrentEvent(Island), await Act(new SequenceNumberRequest(1)));
            Assert.Contains(await Act(new MapInformationRequest(Island)), e => e is MapComplementaryInformationEvent { MapId: Island });
            Assert.IsType<FriendListEvent>(Assert.Single(await Act(new FriendListRequest())));
            Assert.IsType<AcquaintanceListEvent>(Assert.Single(await Act(new AcquaintanceListRequest())));
            Assert.Collection(await Act(new AnomalySubareaInformationRequest()),
                m => Assert.IsType<AlterationsEvent>(m),
                m => Assert.NotEmpty(Assert.IsType<AnomalySubareaInformationEvent>(m).Subareas));
            Assert.IsType<ObjectAveragePricesEvent>(Assert.Single(await Act(new ObjectAveragePricesRequest())));
            Assert.Empty(await Act(new QuestStepInformationRequest(2545)));

            var greeting = await Act(new NpcGenericActionRequest(Npcs.Talk, Island, -20000));
            Assert.Contains(new NpcDialogCreationEvent(Island, -20000), greeting);
            var question = Assert.Single(greeting.OfType<NpcDialogQuestionEvent>());
            Assert.Equal(60352, question.MessageId);
            var reply = Assert.Single(question.VisibleReplies);
            Assert.Equal(83101, reply.ReplyId);
            Assert.Equal(2192, Assert.Single(reply.Actions).Id);
            var acceptance = await Act(new NpcDialogReplyRequest(reply.ReplyId));
            Assert.Collection(acceptance.Where(m => m is StagingEvent or DialogLeaveEvent or QuestStartedEvent or QuestStepInformationEvent),
                m => Assert.Equal(new StagingEvent(0, 100, 902), m),
                m => Assert.Equal(new DialogLeaveEvent(DialogType.DialogDialog), m),
                m => Assert.Equal(new QuestStartedEvent(2545), m),
                m => AssertStep([m], 3226));
            var detail = AssertStep(await Act(new QuestStepInformationRequest(2545)), 3226);
            Assert.True(Assert.Single(detail.Objectives, o => o.ObjectiveId == 19534).ObjectivePending);

            if (walk)
            {
                await Act(new MapMovementRequest(Island, [session.State.CellId, 281], false));
                Assert.Equal(3226, QuestEngine.Log!.Run(2545)!.StepId);
                var arrival = await Act(new MapMovementConfirmRequest());
                Assert.IsType<MapMovementConfirmedResponse>(arrival[0]);
                Assert.Collection(arrival.Where(m => m is QuestStepValidatedEvent or QuestStepInformationEvent or StagingEvent or NpcDialogCreationEvent or NpcDialogQuestionEvent),
                    m => Assert.Equal(new QuestStepValidatedEvent(2545, 3226), m),
                    m => AssertStep([m], 3228),
                    m => Assert.Equal(new StagingEvent(0, 100, 912), m),
                    m => Assert.Equal(new NpcDialogCreationEvent(Island, -20000), m),
                    m => Assert.Equal(60333, Assert.IsType<NpcDialogQuestionEvent>(m).MessageId),
                    m => Assert.Equal(new StagingEvent(0, 100, 302), m));
                Assert.True(NpcHandler.IsDialogueOpen);
                // One piece already owned: the exit only grants the other seven.
                Equipment.Add(123456, 10784, 1, Equipment.Bag, "");
            }

            var exit = await Act(new GuideModQuitRequest());
            int[] quests = [2545, 2502, 2511, 1629, 1630];
            Assert.Equal(quests, exit.OfType<QuestValidatedEvent>().Select(q => q.QuestId));
            Assert.All(quests, q => Assert.True(QuestEngine.Log!.Finished(q)));
            int[] set = [10784, 10785, 10794, 10797, 10798, 10799, 10800, 10801];
            Assert.Equal(set.Skip(walk ? 1 : 0), exit.OfType<ObjectAddedEvent>().Select(e => e.Object.Item.Gid));
            Assert.All(set, gid => Assert.Equal(1, Equipment.HowMany(gid)));
            Assert.False(NpcHandler.IsDialogueOpen);
            Assert.Equal(walk ? 1 : 0, exit.OfType<DialogLeaveEvent>().Count());
            Assert.Contains(new MapCurrentEvent(Destination), exit);
            if (walk) Assert.True(exit.FindIndex(m => m is DialogLeaveEvent) < exit.FindIndex(m => m is MapCurrentEvent));
            Assert.Equal(Destination, session.MapId);
            Assert.Equal(MapManager.GetNearestWalkableCell(Destination, 127), session.State.CellId);
            Assert.Contains(await Act(new MapInformationRequest(Destination)), e => e is MapComplementaryInformationEvent { MapId: Destination });
            Assert.Empty(await Act(new GuideModQuitRequest()));
            Assert.Empty(await Act(new QuestStepInformationRequest(2545)));
            var reconnected = GameSession.SinSocket();
            using (SessionContext.Push(reconnected))
            {
                Assert.True(DatabaseManager.LoadCharacter(character));
                Assert.Equal((session.MapId, session.State.CellId), (reconnected.MapId, reconnected.State.CellId));
            }
        }
        finally { if (character > 0) DatabaseManager.DeleteCharacter(character, account); }
    }

    private static QuestActiveDetails AssertStep(IEnumerable<GamePayload> messages, int stepId)
    {
        var quest = Assert.Single(messages.OfType<QuestStepInformationEvent>()).Information;
        Assert.Equal(2545, quest.QuestId);
        Assert.Equal(stepId, quest.Details!.StepId);
        return quest.Details;
    }
}

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Achievements;
using Jondo.Unity.World.Emotes;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Sessions;

[Collection("MapManager")]
public sealed class EmoteLearningFlowTests
{
    private static readonly GameCodec Codec = new();
    private const long ItemLearner = 8_193_001, RewardLearner = 8_193_002;
    private const int ItemUid = 8_193_101;
    private static bool Available => File.Exists(Paths.WorldDb) && File.Exists(Paths.EmotesJson) && File.Exists(Paths.AchievementsJson);

    [Fact]
    public async Task Attitude_item_announces_learning_before_consumption_and_a_known_attitude_does_not_spend_again()
    {
        if (!Available) return;
        Emotes.Load();
        Clean(ItemLearner);
        try
        {
            await using var pipe = await ClientPipe.OpenAsync(8_193_201, ItemLearner, 8_193_301, "Emote-learner");
            using var context = SessionContext.Push(pipe.Session);
            using var turn = await pipe.Session.EnterTurnAsync();
            Emotes.LoadFrom(ItemLearner);
            var catalogue = new EmoteCatalogue();
            var scroll = DatabaseManager.ItemTemplatesOfType(AttitudeItemHandler.ScrollType)
                .Select(id => (Id: id, Emote: AttitudeItemHandler.AttitudeOf(AttitudeItemHandler.ScrollType,
                    DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(id)))))
                .First(candidate => candidate.Emote > 0 && catalogue.Of(candidate.Emote) != null
                    && !pipe.Session.State.Emotes.Contains(candidate.Emote)
                    && string.IsNullOrWhiteSpace(DatabaseManager.GetItemTemplateCriterion(candidate.Id)));
            Assert.True(DatabaseManager.InsertCharacterItem(ItemUid, ItemLearner, scroll.Id, 2, Equipment.Bag, ""));
            var item = Equipment.Add(ItemUid, scroll.Id, 2, Equipment.Bag, "");

            Assert.True(await AttitudeItemHandler.TryUseAsync(pipe.ToClient, item));
            Assert.Equal(new EmoteAddedEvent(scroll.Emote), (await NextAsync(pipe)).Content);
            var quantity = Assert.IsType<ObjectQuantityEvent>((await NextAsync(pipe)).Content);
            Assert.Equal(new ObjectUidWithQuantity(ItemUid, 1), quantity.Object);
            Assert.IsType<InventoryWeightEvent>((await NextAsync(pipe)).Content);
            Assert.Contains(scroll.Emote, DatabaseManager.LoadEmotes(ItemLearner));
            Assert.Equal(1, item.Quantity);

            Assert.True(await AttitudeItemHandler.TryUseAsync(pipe.ToClient, item));
            var refused = Assert.IsType<TextInformationEvent>((await NextAsync(pipe)).Content);
            Assert.Equal(InfoMessages.AttitudeAlreadyKnown, refused.MessageId);
            Assert.Equal(1, item.Quantity);
            await pipe.Quiet();
        }
        finally { Clean(ItemLearner); }
    }

    [Fact]
    public async Task Achievement_emote_notifications_precede_the_reward_result_and_repeated_claims_do_not_relearn()
    {
        if (!Available) return;
        Achievements.Load();
        Emotes.Load();
        Clean(RewardLearner);
        try
        {
            await using var pipe = await ClientPipe.OpenAsync(8_193_202, RewardLearner, 8_193_302, "Reward-learner");
            using var context = SessionContext.Push(pipe.Session);
            using var turn = await pipe.Session.EnterTurnAsync();
            pipe.Session.State.CharacterLevel = 200;
            pipe.Session.State.Experience = ExperienceTable.LevelFloor(200);
            Achievements.LoadFrom(RewardLearner);
            Emotes.LoadFrom(RewardLearner);
            var log = pipe.Session.State.Achievements!;
            var catalogue = new EmoteCatalogue();
            var reward = new AchievementCatalogue().All()
                .Select(achievement => (achievement.Id, Payout: log.Payout(achievement.Id, 200)))
                .First(candidate => candidate.Payout.Items.Count == 0 && candidate.Payout.Emotes.Count > 0
                    && candidate.Payout.Emotes.All(id => catalogue.Of(id) != null && !pipe.Session.State.Emotes.Contains(id)));
            log.Restore(reward.Id, claimed: false);

            await Achievements.ClaimAsync(pipe.ToClient, reward.Id);
            var learned = new System.Collections.Generic.List<int>();
            bool resultReceived = false;
            for (int i = 0; i < 32 && !resultReceived; i++)
            {
                byte[] packet = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5));
                var envelope = Codec.ReadEnvelope(packet);
                string? name = GameBindings.StableName(envelope.Alias);
                if (name == nameof(EmoteAddedEvent)) learned.Add(Assert.IsType<EmoteAddedEvent>(Codec.Decode(packet).Content).EmoteId);
                if (name == nameof(AchievementRewardResultEvent))
                {
                    Assert.Equal(new AchievementRewardResultEvent(true, reward.Id), Codec.Decode(packet).Content);
                    resultReceived = true;
                }
            }
            Assert.True(resultReceived);
            Assert.Equal(reward.Payout.Emotes.Distinct(), learned);
            Assert.Equal(learned.Order(), DatabaseManager.LoadEmotes(RewardLearner).Order());
            await Achievements.ClaimAsync(pipe.ToClient, reward.Id);
            await pipe.Quiet();
        }
        finally { Clean(RewardLearner); }
    }

    private static async Task<GameFrame> NextAsync(ClientPipe pipe)
    {
        byte[] packet = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5));
        var frame = Codec.Decode(packet);
        Assert.Null(frame.Uid);
        return frame;
    }

    private static void Clean(long characterId)
    {
        DatabaseManager.EnsureProgressionTables();
        using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM CharacterEmotes WHERE CharacterId = $id;
            DELETE FROM CharacterItems WHERE CharacterId = $id;
            DELETE FROM CharacterAchievements WHERE CharacterId = $id;
            DELETE FROM CharacterAchievementCounters WHERE CharacterId = $id;";
        command.Parameters.AddWithValue("$id", characterId);
        command.ExecuteNonQuery();
    }
}

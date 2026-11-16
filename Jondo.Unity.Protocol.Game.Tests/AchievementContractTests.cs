using Google.Protobuf;
using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class AchievementContractTests
{
    private readonly GameCodec _codec = new();

    [Theory]
    [InlineData(200, 870000000004L, 423, "0a0d08c8011084f8a980a91918a703")]
    [InlineData(1, 80000000017L, 8518, "0a0c08011091c0fc82aa0218c642")]
    public void Finished_matches_independent_rat_hunt_and_tutorial_fixtures(int level, long character, int achievement, string hex)
    {
        var entry = new AchievedAchievement(level, character, achievement, 0);
        AssertBody("mfu", hex, new AchievementFinishedEvent(entry));
        Assert.Equal(entry, Assert.IsType<AchievementFinishedEvent>(Decode("mfu", hex)).Achievement);
    }

    [Fact]
    public void List_retains_mixed_account_owners_order_and_absent_claimed_level()
    {
        const string hex = "0a0a1084f8a980a91918a7030a0c08021091c0fc82aa0218c742";
        var entries = new AchievedAchievement[] { new(null, 870000000004, 423, 0), new(2, 80000000017, 8519, 0) };
        AssertBody("mft", hex, new AchievementsEvent(entries));
        Assert.Equal(entries, Assert.IsType<AchievementsEvent>(Decode("mft", hex)).AchievedAchievements);
    }

    [Fact]
    public void Optional_zero_level_is_distinct_from_absence_and_empty_lists_are_replacements()
    {
        AssertBody("mft", "", new AchievementsEvent([]));
        Assert.Empty(Assert.IsType<AchievementsEvent>(Decode("mft", "")).AchievedAchievements);
        AssertBody("mft", "0a0410011802", new AchievementsEvent([new(null, 1, 2, 0)]));
        AssertBody("mft", "0a06080010011802", new AchievementsEvent([new(0, 1, 2, 0)]));
        Assert.Equal(0, Assert.Single(Assert.IsType<AchievementsEvent>(Decode("mft", "0a06080010011802")).AchievedAchievements).FinishedLevel);
    }

    [Fact]
    public void Information_retains_player_identity_name_and_nested_earned_entry()
    {
        const string hex = "080112034164611a0708c80110011802";
        var message = new AchievementFinishedInformationEvent(1, "Ada", new(200, 1, 2, 0));
        AssertBody("mgc", hex, message);
        Assert.Equal(message, Decode("mgc", hex));
    }

    [Theory]
    [InlineData("mfu")]
    [InlineData("mgc")]
    public void Finished_messages_preserve_absent_versus_present_empty_entries(string alias)
    {
        string present = alias == "mfu" ? "0a00" : "1a00";
        GameEvent Message(AchievedAchievement? entry) => alias == "mfu"
            ? new AchievementFinishedEvent(entry) : new AchievementFinishedInformationEvent(0, "", entry);
        AssertBody(alias, "", Message(null));
        AssertBody(alias, present, Message(new(null, 0, 0, 0)));
        Assert.Equal(Message(null), Decode(alias, ""));
        Assert.Equal(Message(new(null, 0, 0, 0)), Decode(alias, present));
    }

    private GamePayload Decode(string alias, string hex)
        => _codec.Decode(CodecTests.Frame(1, alias, Convert.FromHexString(hex))).Content;

    private void AssertBody(string alias, string hex, GameEvent message)
    {
        var envelope = _codec.ReadEnvelope(_codec.Encode(new(message)));
        Assert.Equal(alias, envelope.Alias);
        Assert.Equal(GameMessageKind.Event, envelope.Kind);
        Assert.Null(envelope.Uid);
        Assert.Equal(Convert.FromHexString(hex), envelope.Content.Value.ToByteArray());
    }
}

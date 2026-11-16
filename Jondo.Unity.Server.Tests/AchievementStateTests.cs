using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class AchievementStateTests
{
    [Fact]
    public void Snapshot_detaches_account_owners_and_preserves_claimed_level_absence()
    {
        var rows = new List<(int, long, int)> { (81_001, 8_000_000_003, 0), (81_002, 8_000_000_004, 137) };
        var snapshot = AchievementMessages.Snapshot(rows);
        rows.Clear();
        Assert.Equal(new AchievedAchievement[] { new(null, 8_000_000_003, 81_001, 0), new(137, 8_000_000_004, 81_002, 0) }, snapshot.AchievedAchievements);
    }

    [Fact]
    public async Task Entry_delivers_before_checking_then_saves_and_announces_in_source_order_with_explicit_owner()
    {
        var backend = new Backend();
        var target = new Recipient(71_001);
        var ambient = new Recipient(71_002);
        target.AfterSend = frame =>
        {
            if (frame.Content is AchievementsEvent)
            {
                Assert.Equal(-1, target.Session.State.AchievementLevelChecked);
                backend.Rows.Clear();
                backend.Calls.Add("list");
            }
            else
            {
                var earned = Assert.IsType<AchievementFinishedEvent>(frame.Content).Achievement!;
                Assert.Equal(247, target.Session.State.AchievementLevelChecked);
                Assert.Contains(earned.AchievementId, backend.Saved);
                backend.Calls.Add($"own:{earned.AchievementId}");
            }
        };
        using var context = SessionContext.Push(ambient.Session);
        using var turn = await target.Session.EnterTurnAsync();
        await new AchievementStateService(backend).SendListAsync(target.Session).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "snapshot:71001", "list", "check:71001", "save:71001:81003:200", "own:81003", "map:71001:81003", "save:71001:81004:200", "own:81004", "map:71001:81004" }, backend.Calls);
        Assert.Equal(2, Assert.IsType<AchievementsEvent>(target.Codec.Frames[0].Content).AchievedAchievements.Count);
        Assert.Equal(new[] { 81_003, 81_004 }, target.Codec.Frames.Skip(1).Select(frame => Assert.IsType<AchievementFinishedEvent>(frame.Content).Achievement!.AchievementId));
        Assert.All(target.Codec.Frames.Skip(1), frame =>
            Assert.Equal(new AchievedAchievement(200, 71_001, Assert.IsType<AchievementFinishedEvent>(frame.Content).Achievement!.AchievementId, 0), Assert.IsType<AchievementFinishedEvent>(frame.Content).Achievement));
        Assert.All(target.Codec.Frames, frame => Assert.Null(frame.Uid));
        Assert.Equal(3, target.Delivered.Count);
        Assert.Equal(0, target.Codec.Decodes);
        Assert.Equal(0, target.Session.UnoCadaVez.CurrentCount);
        Assert.Empty(ambient.Codec.Frames);
        Assert.Same(ambient.Session, SessionContext.Actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_loaded_lists_send_but_absent_logs_omit_delivery_and_checking(bool available)
    {
        var backend = new Backend { Available = available };
        backend.Rows.Clear();
        backend.Earned.Clear();
        var target = new Recipient();
        await new AchievementStateService(backend).SendListAsync(target.Session);
        if (available)
        {
            Assert.Empty(Assert.IsType<AchievementsEvent>(Assert.Single(target.Codec.Frames).Content).AchievedAchievements);
            Assert.Equal(247, target.Session.State.AchievementLevelChecked);
            Assert.Equal(new[] { "snapshot:71001", "check:71001" }, backend.Calls);
        }
        else
        {
            Assert.Empty(target.Codec.Frames);
            Assert.Equal(-1, target.Session.State.AchievementLevelChecked);
            Assert.Equal(new[] { "snapshot:71001" }, backend.Calls);
        }
    }

    [Theory]
    [InlineData("snapshot:71001", 0, -1)]
    [InlineData("check:71001", 1, -1)]
    [InlineData("save:71001:81003:200", 1, 247)]
    [InlineData("save:71001:81004:200", 2, 247)]
    public async Task Source_and_persistence_failures_propagate_at_the_original_delivery_boundary(string failAt, int sent, int checkedLevel)
    {
        var backend = new Backend { FailAt = failAt };
        var target = new Recipient();
        Assert.Same(backend.Failure, await Assert.ThrowsAsync<IOException>(() => new AchievementStateService(backend).SendListAsync(target.Session)));
        Assert.Equal(sent, target.Delivered.Count);
        Assert.Equal(sent, target.Codec.Frames.Count);
        Assert.Equal(checkedLevel, target.Session.State.AchievementLevelChecked);
        Assert.Equal(1, backend.Calls.Count(call => call == failAt));
        Assert.Equal(sent > 1 ? new[] { 81_003 } : [], backend.Saved);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task Failed_delivery_stops_checks_or_later_announcements_without_rolling_back_prior_persistence(int failAt, bool codecFailure)
    {
        var backend = new Backend();
        var target = new Recipient { FailAt = failAt, CodecFailure = codecFailure };
        using var turn = await target.Session.EnterTurnAsync();
        Assert.Same(target.Failure, await Assert.ThrowsAsync<IOException>(() => new AchievementStateService(backend).SendListAsync(target.Session)));
        Assert.Equal(failAt + 1, target.Codec.Frames.Count);
        Assert.Equal(failAt, target.Delivered.Count);
        Assert.Equal(codecFailure ? failAt : failAt + 1, target.Sends);
        Assert.Equal(failAt == 0 ? -1 : 247, target.Session.State.AchievementLevelChecked);
        Assert.Equal(failAt, backend.Saved.Count);
        Assert.Equal(Math.Max(0, failAt - 1), backend.Calls.Count(call => call.StartsWith("map:", StringComparison.Ordinal)));
        Assert.Equal(failAt == 0 ? 0 : 1, backend.Calls.Count(call => call.StartsWith("check:", StringComparison.Ordinal)));
        Assert.Equal(0, target.Session.UnoCadaVez.CurrentCount);
        Assert.Equal(0, target.Codec.Decodes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hero_credit_and_payload_use_source_while_only_the_master_codec_delivers_the_own_event(bool fighting)
    {
        var backend = new Backend();
        var source = new Recipient(71_003);
        source.Session.State.CharacterName = "Hero";
        source.Session.State.CharacterLevel = 137;
        source.Session.State.IsInFight = fighting;
        var master = new Recipient(71_004);
        var ambient = new Recipient(71_005);
        using var context = SessionContext.Push(ambient.Session);
        using var turn = await master.Session.EnterTurnAsync();
        await new AchievementStateService(backend).AnnounceAsync(source.Session, master.Session, [81_005]);
        var frame = Assert.Single(master.Codec.Frames);
        var entry = Assert.IsType<AchievementFinishedEvent>(frame.Content).Achievement!;
        Assert.Equal(new AchievedAchievement(137, 71_003, 81_005, 0), entry);
        Assert.Equal(fighting ? new[] { "save:71003:81005:137" } : new[] { "save:71003:81005:137", "map:71003:81005" }, backend.Calls);
        if (!fighting)
        {
            var information = Assert.Single(backend.Information);
            Assert.Same(entry, information.Achievement);
            Assert.Equal(71_003, information.PlayerId);
            Assert.Equal("Hero", information.PlayerName);
        }
        Assert.Null(frame.Uid);
        Assert.Empty(source.Codec.Frames);
        Assert.Empty(ambient.Codec.Frames);
        Assert.Same(ambient.Session, SessionContext.Actual);
        Assert.Equal(0, master.Session.UnoCadaVez.CurrentCount);
    }

    private sealed class Backend : IAchievementStateBackend
    {
        internal readonly List<string> Calls = [];
        internal readonly List<int> Saved = [], Earned = [81_003, 81_004];
        internal readonly List<(int, long, int)> Rows = [(81_001, 8_000_000_003, 0), (81_002, 8_000_000_004, 137)];
        internal readonly List<AchievementFinishedInformationEvent> Information = [];
        internal bool Available = true;
        internal string FailAt = "";
        internal readonly IOException Failure = new("achievement source failed");
        private void Call(GameSession source, string stage)
        {
            Assert.Same(source, SessionContext.Actual);
            Calls.Add(stage);
            if (stage == FailAt) throw Failure;
        }
        public AchievementsEvent? Snapshot(GameSession source)
        {
            Call(source, $"snapshot:{source.CharacterId}");
            return Available ? AchievementMessages.Snapshot(Rows) : null;
        }
        public IReadOnlyList<int> CheckEverything(GameSession source) { Call(source, $"check:{source.CharacterId}"); return Earned.ToArray(); }
        public void CreditAndSave(GameSession source, int achievementId, int level)
        {
            Call(source, $"save:{source.CharacterId}:{achievementId}:{level}");
            Saved.Add(achievementId);
        }
        public Task NotifyMapAsync(GameSession source, AchievementFinishedInformationEvent message)
        {
            Call(source, $"map:{source.CharacterId}:{message.Achievement!.AchievementId}");
            Information.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class Recipient
    {
        internal readonly RecordingCodec Codec;
        internal readonly GameSession Session;
        internal readonly List<byte[]> Delivered = [];
        internal int Sends, FailAt = -1;
        internal bool CodecFailure;
        internal readonly IOException Failure = new("achievement delivery failed");
        internal Action<GameFrame>? AfterSend;
        internal Recipient(long characterId = 71_001)
        {
            Codec = new((_, index) => { if (CodecFailure && index == FailAt) throw Failure; });
            Session = new(Codec, packet =>
            {
                if (Sends++ == FailAt && !CodecFailure) throw Failure;
                Delivered.Add(packet);
                AfterSend?.Invoke(Codec.Frames[^1]);
                return Task.CompletedTask;
            });
            Session.State.CharacterId = characterId;
            Session.State.CharacterLevel = 247;
            Session.State.AchievementLevelChecked = -1;
        }
    }

    private sealed class RecordingCodec(Action<GameFrame, int> onEncode) : IGameCodec
    {
        internal readonly List<GameFrame> Frames = [];
        internal int Decodes;
        public byte[] Encode(GameFrame frame) { Frames.Add(frame); onEncode(frame, Frames.Count - 1); return [0xA7, checked((byte)Frames.Count)]; }
        public GameFrame Decode(ReadOnlySpan<byte> packet) { Decodes++; throw new InvalidOperationException("Achievement sends must not decode intermediate packets."); }
    }
}

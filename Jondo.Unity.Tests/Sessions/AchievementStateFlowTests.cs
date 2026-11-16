using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Achievements;
using Jondo.Unity.World.Quests;
using Microsoft.Data.Sqlite;
using Xunit;
using JobExperience = Jondo.Unity.Server.Managers.JobExperience;

namespace Jondo.Unity.Tests.Sessions;

[Collection("MapManager")]
public sealed class AchievementStateFlowTests : IDisposable
{
    private const long Character = 9_969_100_001, Account = 8_969_100_001;
    private readonly AchievementCatalogue _book;

    public AchievementStateFlowTests()
    {
        MapManager.Initialize();
        Merkasako.Initialize();
        Achievements.Load();
        _book = new();
        Assert.True(_book.Ready);
        DatabaseManager.EnsureProgressionTables();
        Clear();
    }

    public void Dispose() => Clear();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Job_experience_owns_the_whole_progress_save_and_notification_burst_despite_another_ambient_session(bool fail)
    {
        const int job = 28;
        long experience = JobExperience.Floor(10);
        var codec = new RecordingCodec { FailAt = fail ? 0 : -1 };
        var sent = new List<byte[]>();
        var source = new GameSession(codec, packet => { sent.Add(packet); return Task.CompletedTask; });
        source.State.CharacterId = Character;
        source.State.CharacterLevel = 137;
        source.State.IsInFight = true;
        source.State.Achievements = new(_book, new Achievements.CharacterFacts());
        var wrong = GameSession.SinSocket();
        wrong.State.CharacterId = Character + 9;
        wrong.State.Jobs[job] = new() { JobId = job, Experience = JobExperience.Floor(200) };
        using var context = SessionContext.Push(wrong);
        using var turn = await source.EnterTurnAsync();

        var work = WorkshopHandler.GiveJobExperienceAsync(source, job, experience, bonus: false);
        if (fail) Assert.Same(codec.Failure, await Assert.ThrowsAsync<IOException>(() => work));
        else await work.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(10, source.State.JobLevel(job));
        Assert.Equal(experience, source.State.Jobs[job].Experience);
        Assert.Equal(experience, DatabaseManager.LoadJobExperience(Character)[job]);
        Assert.Equal(200, wrong.State.JobLevel(job));
        Assert.Empty(DatabaseManager.LoadJobExperience(wrong.CharacterId));
        Assert.Equal(Jondo.Unity.Protocol.Op.Isz, ConnectionProtocol.ReadOpcode(sent[0]));
        Assert.Equal(Jondo.Unity.Protocol.Op.Irq, ConnectionProtocol.ReadOpcode(sent[1]));
        var entry = Assert.IsType<AchievementFinishedEvent>(Assert.Single(codec.Frames).Content).Achievement!;
        Assert.Equal(new AchievedAchievement(137, Character, 14, 0), entry);
        Assert.Equal((14, false), Assert.Single(DatabaseManager.LoadAchievements(Character)));
        Assert.Equal(fail ? 2 : 3, sent.Count);
        Assert.Equal(0, source.UnoCadaVez.CurrentCount);
        Assert.Same(wrong, SessionContext.Actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(247)]
    public async Task Live_list_is_sorted_detached_and_uses_the_explicit_owner_with_claimed_level_absence(int level)
    {
        var codec = new RecordingCodec();
        await using var own = await ClientPipe.OpenAsync(Account, Character, 191105026, codec: codec);
        own.Session.State.CharacterLevel = level;
        var log = Log(own.Session);
        log.Restore(81_005, false, Character + 4);
        log.NoteLevel(81_005, 251);
        log.Restore(81_003, false, Character + 2);
        log.Restore(81_001, true, Character + 1);
        log.Restore(81_004, false); // Missing achiever: use this character.
        log.Restore(81_002, false, Character + 3);
        log.NoteLevel(81_002, 137);
        codec.AfterEncode = _ => { log.MarkClaimed(81_002); log.Restore(81_006, true); };
        var wrong = GameSession.SinSocket();
        wrong.State.CharacterLevel = 42;
        using var context = SessionContext.Push(wrong);
        using var turn = await own.Session.EnterTurnAsync();
        await Achievements.SendListAsync(own.Session).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecordingCodec.Token(1), await Read(own));
        var snapshot = Assert.IsType<AchievementsEvent>(Assert.Single(codec.Frames).Content);
        int fallbackLevel = level == 0 ? 1 : 200;
        Assert.Equal(new AchievedAchievement[]
        {
            new(null, Character + 1, 81_001, 0), new(137, Character + 3, 81_002, 0),
            new(fallbackLevel, Character + 2, 81_003, 0), new(fallbackLevel, Character, 81_004, 0),
            new(200, Character + 4, 81_005, 0),
        }, snapshot.AchievedAchievements);
        Assert.Equal(level, own.Session.State.AchievementLevelChecked);
        Assert.Equal(0, own.Session.UnoCadaVez.CurrentCount);
        Assert.Equal(0, codec.Decodes);
        Assert.Null(codec.Frames[0].Uid);
        Assert.Same(wrong, SessionContext.Actual);
        Assert.Empty(DatabaseManager.LoadAchievements(Character));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_framed_list_sends_empty_loaded_state_or_mixed_account_owners(bool empty)
    {
        await using var pipe = await ClientPipe.OpenAsync(Account, Character, 191105026);
        pipe.Session.State.CharacterLevel = 247;
        var log = Log(pipe.Session);
        if (!empty)
        {
            log.Restore(81_002, false, Character + 1);
            log.NoteLevel(81_002, 137);
            log.Restore(81_001, true, Character);
        }
        await Achievements.SendListAsync(pipe.Session);
        var codec = new GameCodec();
        var packet = await Read(pipe);
        var envelope = codec.ReadEnvelope(packet);
        Assert.Equal("mft", envelope.Alias);
        Assert.Null(envelope.Uid);
        var snapshot = Assert.IsType<AchievementsEvent>(codec.Decode(packet).Content);
        Assert.Equal(empty ? [] : new AchievedAchievement[] { new(null, Character, 81_001, 0), new(137, Character + 1, 81_002, 0) }, snapshot.AchievedAchievements);
        if (empty) Assert.Empty(envelope.Content.Value.ToByteArray());
    }

    [Fact]
    public async Task Live_entry_sends_the_old_snapshot_before_new_unlocks_and_a_repeat_list_contains_the_new_credit()
    {
        await using var pipe = await ClientPipe.OpenAsync(Account, Character, 191105026);
        pipe.Session.State.CharacterLevel = 200;
        pipe.Session.State.Achievements = new(_book, new Achievements.CharacterFacts());
        using var turn = await pipe.Session.EnterTurnAsync();
        await Achievements.SendListAsync(pipe.Session).WaitAsync(TimeSpan.FromSeconds(5));
        var codec = new GameCodec();
        Assert.Empty(Assert.IsType<AchievementsEvent>(codec.Decode(await Read(pipe)).Content).AchievedAchievements);
        var log = pipe.Session.State.Achievements;
        Assert.NotEmpty(log.Earned);
        var announced = new List<int>();
        foreach (int _ in log.Earned)
        {
            var frame = codec.Decode(await Read(pipe));
            var entry = Assert.IsType<AchievementFinishedEvent>(frame.Content).Achievement!;
            Assert.Equal(Character, entry.AchievedBy);
            Assert.Equal(200, entry.FinishedLevel);
            Assert.Null(frame.Uid);
            announced.Add(entry.AchievementId);
        }
        Assert.Equal(log.Earned.OrderBy(id => id), announced.OrderBy(id => id));
        Assert.Equal(announced.OrderBy(id => id), DatabaseManager.LoadAchievements(Character).Select(row => row.Achievement).OrderBy(id => id));
        Assert.Equal(200, pipe.Session.State.AchievementLevelChecked);

        await Achievements.SendListAsync(pipe.Session);
        var updated = Assert.IsType<AchievementsEvent>(codec.Decode(await Read(pipe)).Content);
        Assert.Equal(announced.OrderBy(id => id), updated.AchievedAchievements.Select(entry => entry.AchievementId));
        Assert.All(updated.AchievedAchievements, entry => Assert.Equal(new AchievedAchievement(200, Character, entry.AchievementId, 0), entry));
        Assert.False(pipe.FromServer.DataAvailable);
        Assert.Equal(0, pipe.Session.UnoCadaVez.CurrentCount);
    }

    [Fact]
    public async Task Deferred_count_keeps_explicit_owner_state_and_map_check_clears_pending_before_failed_unlock_delivery()
    {
        var codec = new RecordingCodec { FailAt = 0 };
        await using var pipe = await ClientPipe.OpenAsync(Account, Character, 191105026, codec: codec);
        var source = pipe.Session;
        source.State.CharacterLevel = 1;
        source.State.Achievements = new(_book, new Achievements.CharacterFacts());
        var wrong = GameSession.SinSocket();
        wrong.State.CharacterLevel = 247;
        using var context = SessionContext.Push(wrong);
        await Achievements.CountAsync(source, AchievementCatalogue.CraftKey, 0, defer: true);
        Assert.Empty(codec.Frames);
        Assert.False(source.State.Achievements.Has(120));
        Assert.Contains(120, source.State.AchievementsPending);
        Assert.Equal(1, source.State.AchievementTallies[(AchievementCatalogue.CraftKey, 0)]);
        Assert.Empty(wrong.State.AchievementTallies);
        Assert.Empty(wrong.State.AchievementsPending);

        using var turn = await source.EnterTurnAsync();
        Assert.Same(codec.Failure, await Assert.ThrowsAsync<IOException>(() => Achievements.OnMapEnteredAsync(source, 0, 0)));
        Assert.Empty(source.State.AchievementsPending);
        Assert.Equal(1, source.State.AchievementLevelChecked);
        Assert.True(source.State.Achievements.Has(120));
        Assert.Equal(Character, source.State.Achievements.AchieverOf(120));
        Assert.Equal((120, false), Assert.Single(DatabaseManager.LoadAchievements(Character)));
        Assert.Equal(new AchievedAchievement(1, Character, 120, 0), Assert.IsType<AchievementFinishedEvent>(Assert.Single(codec.Frames).Content).Achievement);
        Assert.Same(wrong, SessionContext.Actual);
        Assert.Equal(0, source.UnoCadaVez.CurrentCount);
    }

    [Fact]
    public async Task Missing_log_omits_the_list_and_failed_list_encoding_does_not_check_or_change_the_level_marker()
    {
        var codec = new RecordingCodec { FailAt = 0 };
        await using var own = await ClientPipe.OpenAsync(Account, Character, 191105026, codec: codec);
        own.Session.State.CharacterLevel = 200;
        own.Session.State.AchievementLevelChecked = -1;
        await Achievements.SendListAsync(own.Session);
        Assert.Empty(codec.Frames);
        // Ambient character facts would earn level achievements if the list failure did not stop checking.
        own.Session.State.Achievements = new(_book, new Achievements.CharacterFacts());
        Assert.Same(codec.Failure, await Assert.ThrowsAsync<IOException>(() => Achievements.SendListAsync(own.Session)));
        Assert.Empty(own.Session.State.Achievements.Earned);
        Assert.Equal(-1, own.Session.State.AchievementLevelChecked);
        Assert.False(own.FromServer.DataAvailable);
        Assert.Empty(DatabaseManager.LoadAchievements(Character));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_live_earned_delivery_keeps_credit_level_and_persistence_before_the_failure(bool codecFailure)
    {
        var codec = new RecordingCodec { FailAt = codecFailure ? 0 : -1 };
        var failure = codec.Failure;
        var source = new GameSession(codec, _ => throw failure);
        source.State.CharacterId = Character;
        source.State.CharacterLevel = 247;
        var log = Log(source);
        log.Restore(81_001, false);
        log.Restore(81_002, false);
        using var turn = await source.EnterTurnAsync();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => AchievementStateService.Live.AnnounceAsync(source, source, [81_001, 81_002])));
        Assert.Equal(Character, log.AchieverOf(81_001));
        Assert.Equal(200, log.LevelOf(81_001));
        Assert.Equal(0, log.AchieverOf(81_002));
        Assert.Equal(0, log.LevelOf(81_002));
        Assert.Equal((81_001, false), Assert.Single(DatabaseManager.LoadAchievements(Character)));
        Assert.Equal((200L, 0L), Stored(81_001));
        Assert.Single(codec.Frames);
        Assert.Equal(0, source.UnoCadaVez.CurrentCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Map_notifications_use_recipient_codecs_source_bag_and_existing_fight_policy_with_isolated_failures(bool fighting)
    {
        long map = Merkasako.MapOfTheme(Merkasako.Themes.First());
        var ownCodec = new RecordingCodec(0xA1);
        var peerCodec = new RecordingCodec(0xA2);
        var badCodec = new RecordingCodec(0xA3) { FailAt = 0 };
        var wrongCodec = new RecordingCodec(0xA4);
        await using var own = await ClientPipe.OpenAsync(Account, Character, map, "Achiever", ownCodec);
        await using var peer = await ClientPipe.OpenAsync(Account + 1, Character + 1, map, codec: peerCodec);
        await using var bad = await ClientPipe.OpenAsync(Account + 2, Character + 2, map, codec: badCodec);
        await using var broken = await ClientPipe.OpenAsync(Account + 3, Character + 3, map);
        await using var wrong = await ClientPipe.OpenAsync(Account + 4, Character + 4, map, codec: wrongCodec);
        own.Session.State.CharacterLevel = 137;
        own.Session.State.IsInFight = fighting;
        peer.Session.State.HavenBagOwnerId = bad.Session.State.HavenBagOwnerId = broken.Session.State.HavenBagOwnerId = Character;
        // The legacy notification has no recipient fight filter, even for a fighting peer.
        peer.Session.State.FightId = 77;
        peer.Session.State.IsInFight = true;
        broken.ToClient.Dispose();
        Log(own.Session).Restore(81_001, false);
        using var context = SessionContext.Push(wrong.Session);
        using var sourceTurn = await own.Session.EnterTurnAsync();
        using var peerTurn = await peer.Session.EnterTurnAsync();
        await AchievementStateService.Live.AnnounceAsync(own.Session, own.Session, [81_001]).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecordingCodec.Token(1, 0xA1), await Read(own));
        var finished = Assert.IsType<AchievementFinishedEvent>(Assert.Single(ownCodec.Frames).Content);
        Assert.Equal(new AchievedAchievement(137, Character, 81_001, 0), finished.Achievement);
        if (fighting)
        {
            Assert.Empty(peerCodec.Frames);
            Assert.Empty(badCodec.Frames);
        }
        else
        {
            Assert.Equal(RecordingCodec.Token(1, 0xA2), await Read(peer));
            var information = Assert.IsType<AchievementFinishedInformationEvent>(Assert.Single(peerCodec.Frames).Content);
            Assert.Equal(Character, information.PlayerId);
            Assert.Equal("Achiever", information.PlayerName);
            Assert.Same(finished.Achievement, information.Achievement);
            Assert.Single(badCodec.Frames);
        }
        Assert.Empty(wrongCodec.Frames);
        Assert.True(SessionRegistry.TryGet(bad.Session.Id, out _));
        Assert.True(SessionRegistry.TryGet(broken.Session.Id, out _));
        Assert.Equal(0, own.Session.UnoCadaVez.CurrentCount);
        Assert.Equal(0, peer.Session.UnoCadaVez.CurrentCount);
        Assert.Same(wrong.Session, SessionContext.Actual);
    }

    [Fact]
    public async Task Ordinary_sqlite_save_failure_remains_log_and_continue_after_in_memory_credit()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = $"CREATE TRIGGER FailAchievementTypedSend BEFORE INSERT ON CharacterAchievements WHEN NEW.CharacterId = {Character} BEGIN SELECT RAISE(FAIL, 'achievement fixture write failure'); END;";
        command.ExecuteNonQuery();
        try
        {
            await using var pipe = await ClientPipe.OpenAsync(Account, Character, 191105026);
            pipe.Session.State.CharacterLevel = 137;
            var log = Log(pipe.Session);
            log.Restore(81_001, false);
            await AchievementStateService.Live.AnnounceAsync(pipe.Session, pipe.Session, [81_001]);
            var frame = new GameCodec().Decode(await Read(pipe));
            Assert.Equal(new AchievedAchievement(137, Character, 81_001, 0), Assert.IsType<AchievementFinishedEvent>(frame.Content).Achievement);
            Assert.Equal(Character, log.AchieverOf(81_001));
            Assert.Equal(137, log.LevelOf(81_001));
            Assert.Empty(DatabaseManager.LoadAchievements(Character));
        }
        finally
        {
            command.CommandText = "DROP TRIGGER FailAchievementTypedSend;";
            command.ExecuteNonQuery();
        }
    }

    private AchievementLog Log(GameSession session)
        => session.State.Achievements = new(_book, new NoProgress());

    private static (long Level, long Claimed) Stored(int achievement)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT Level, Claimed FROM CharacterAchievements WHERE CharacterId = {Character} AND AchievementId = $a;";
        command.Parameters.AddWithValue("$a", achievement);
        using var row = command.ExecuteReader();
        Assert.True(row.Read());
        return (row.GetInt64(0), row.GetInt64(1));
    }

    private static void Clear()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = $"DELETE FROM CharacterAchievements WHERE CharacterId = {Character}; DELETE FROM CharacterAchievementCounters WHERE CharacterId = {Character}; DELETE FROM CharacterJobs WHERE CharacterId = {Character};";
        command.ExecuteNonQuery();
    }
    private static SqliteConnection Open() { var db = new SqliteConnection(DatabaseManager.WorldConnectionString); db.Open(); return db; }
    private static Task<byte[]> Read(ClientPipe pipe)
        => Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5));

    private sealed class NoProgress : IQuestFacts
    {
        public int Level => 1;
        public long MapId => 0;
        public bool Finished(int questId) => false;
        public bool Active(int questId) => false;
        public bool ObjectiveDone(int objectiveId) => false;
    }

    private sealed class RecordingCodec(byte marker = 0xA7) : IGameCodec
    {
        internal readonly List<GameFrame> Frames = [];
        internal readonly IOException Failure = new("achievement encoding failed");
        internal int FailAt = -1, Decodes;
        internal Action<GameFrame>? AfterEncode;
        internal static byte[] Token(int index, byte marker = 0xA7) => [marker, checked((byte)index)];
        public byte[] Encode(GameFrame frame)
        {
            Frames.Add(frame);
            if (Frames.Count - 1 == FailAt) throw Failure;
            AfterEncode?.Invoke(frame);
            return Token(Frames.Count, marker);
        }
        public GameFrame Decode(ReadOnlySpan<byte> packet) { Decodes++; throw new InvalidOperationException("Achievement delivery must not decode intermediate packets."); }
    }
}

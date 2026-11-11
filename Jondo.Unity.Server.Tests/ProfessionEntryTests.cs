using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;
using JobExperience = Jondo.Unity.Protocol.Game.JobExperience;
using JobProgress = Jondo.Unity.Server.Managers.JobExperience;

namespace Jondo.Unity.Server.Tests;

public sealed class ProfessionEntryTests
{
    private static readonly GameCodec Wire = new();

    [Fact]
    public void Catalogue_selects_practisable_jobs_from_game_data_and_keeps_base_only_in_directory_settings()
    {
        const int gathering = 81_001, crafting = 81_002, magus = 81_003, unsupported = 81_004;
        var skills = new Dictionary<int, IReadOnlyList<SkillDefinition>>
        {
            [gathering] = [new() { Id = 91_001, GatheredResourceItem = 101_001 }],
            [crafting] = [new() { Id = 91_002 }],
            [magus] = [new() { Id = 91_003, IsForgemagus = true }],
            [unsupported] = [new() { Id = 91_004 }],
        };
        IReadOnlyList<SkillDefinition> SkillsOf(int job) => skills.GetValueOrDefault(job) ?? [];
        var jobs = new[] { unsupported, magus, crafting, WorkshopHandler.BaseJob, gathering, crafting }
            .Select(id => new JobDefinition { Id = id });
        var directory = ProfessionMessages.SupportedDirectoryJobs(jobs, SkillsOf, skill => skill == 91_002 ? 1 : 0);
        Assert.Equal(new[] { WorkshopHandler.BaseJob, gathering, crafting, magus }, directory);
        var messages = new ProfessionMessages(directory, SkillsOf);
        var state = new SessionState();
        Assert.Equal(new[] { gathering, crafting, magus }, messages.Descriptions(state).Jobs.Select(job => job.JobId));
        Assert.Equal(new[] { gathering, crafting, magus }, messages.Experiences(state).Experiences.Select(job => job.JobId));
        Assert.Equal(directory, ArtisanHandler.Settings(state, messages.DirectoryJobs).Settings.Select(job => job.JobId));
        Assert.Equal(0, ArtisanHandler.Settings(state, directory).Settings[0].MinLevel);
    }

    [Theory]
    [InlineData(0L, 1, 0L, 20L)]
    [InlineData(19L, 1, 0L, 20L)]
    [InlineData(20L, 2, 20L, 60L)]
    [InlineData(59L, 2, 20L, 60L)]
    [InlineData(60L, 3, 60L, 120L)]
    [InlineData(398_000L, 200, 398_000L, 0L)]
    [InlineData(4_294_967_313L, 200, 398_000L, 0L)]
    public void Experience_uses_saved_long_totals_and_level_boundaries_including_untouched_jobs(
        long xp, int level, long floor, long next)
    {
        const int progressed = 81_001, untouched = 81_002, stale = 81_003;
        var state = new SessionState();
        state.Jobs[progressed] = new JobProgress.Progress { JobId = progressed, Experience = xp };
        state.Jobs[stale] = new JobProgress.Progress { JobId = stale, Experience = 1320 };
        var messages = new ProfessionMessages([untouched, WorkshopHandler.BaseJob, progressed], _ => []);
        var result = messages.Experiences(state);
        Assert.Equal(new[] { new JobExperience(progressed, next, level, floor, xp),
            new JobExperience(untouched, 20, 1, 0, 0) }, result.Experiences);
        Assert.Equal(xp, state.Jobs[progressed].Experience);
        Assert.False(state.Jobs.ContainsKey(untouched));
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(100, 10)]
    public void Skill_descriptions_share_level_up_policy_for_unlocked_locked_and_crafting_skills(int level, int ceiling)
    {
        const int job = 81_001, gathering = 91_001, locked = 91_002, craft = 91_003;
        SkillDefinition[] skills = [
            new() { Id = craft },
            new() { Id = locked, GatheredResourceItem = 101_002, LevelMin = 120 },
            new() { Id = gathering, GatheredResourceItem = 101_001, LevelMin = 1 }];
        var description = ProfessionMessages.Description(job, level, skills);
        Assert.Equal(job, description.JobId);
        Assert.Equal(new[] {
            new JobSkillAction(gathering, new JobSkillActionCaseTimed(new(new JobSkillActionCollect(ceiling, 1), 30))),
            new JobSkillAction(locked, new JobSkillActionCaseTimed(new(new JobSkillActionCollect(1, 1), 30))),
            new JobSkillAction(craft, new JobSkillActionCaseCraft(new(100))) }, description.Skills);
        Assert.Empty(ProfessionMessages.Description(job, level, []).Skills);
    }

    [Fact]
    public async Task Entry_sends_each_characters_professions_before_settings_and_lists_only_its_subscribed_jobs_at_the_hook()
    {
        const int gathering = 81_001, crafting = 81_002, gatherSkill = 91_001, craftSkill = 91_002;
        var professions = new ProfessionMessages([crafting, WorkshopHandler.BaseJob, gathering], job => job == gathering
            ? [new() { Id = gatherSkill, GatheredResourceItem = 101_001, LevelMin = 1 }]
            : [new() { Id = craftSkill }]);
        var first = Recipient();
        var second = Recipient();
        first.Session.State.Jobs[gathering] = new JobProgress.Progress { JobId = gathering, Experience = 99_000 };
        first.Session.State.CrafterSettings[crafting] = new(83, false, true);
        first.Session.State.CrafterSettings[gathering] = new(1, true, false);
        second.Session.State.Jobs[crafting] = new JobProgress.Progress { JobId = crafting, Experience = 20 };
        second.Session.State.CrafterSettings[gathering] = new(17, true, true);

        using (SessionContext.Push(second.Session))
        {
            await SendInitialAsync(first.Session, professions);
            Assert.DoesNotContain(first.Frames, frame => frame.Content is JobBookSubscriptionEvent);
            await ProfessionMessages.SendSubscriptionsAsync(first.Session);
            Assert.Same(second.Session, SessionContext.Actual);
        }
        using (SessionContext.Push(first.Session))
        {
            await SendInitialAsync(second.Session, professions);
            await ProfessionMessages.SendSubscriptionsAsync(second.Session);
            Assert.Same(first.Session, SessionContext.Actual);
        }

        Assert.Collection(ProfessionFrames(first.Frames),
            frame => AssertDescriptions(frame, gathering, gatherSkill, 10, crafting, craftSkill),
            frame => Assert.Equal(new[] { new JobExperience(gathering, 101_000, 100, 99_000, 99_000),
                new JobExperience(crafting, 20, 1, 0, 0) }, Assert.IsType<JobExperiencesUpdateEvent>(frame.Content).Experiences),
            frame => Assert.Equal(new[] { new JobCrafterDirectorySettings(WorkshopHandler.BaseJob, true, 0),
                new JobCrafterDirectorySettings(gathering, true, 1), new JobCrafterDirectorySettings(crafting, false, 83) },
                Assert.IsType<JobCrafterDirectorySettingsEvent>(frame.Content).Settings),
            frame => Assert.Equal(new[] { new JobBookSubscription(crafting, true) },
                Assert.IsType<JobBookSubscriptionEvent>(frame.Content).Subscriptions));
        Assert.Collection(ProfessionFrames(second.Frames),
            frame => AssertDescriptions(frame, gathering, gatherSkill, 4, crafting, craftSkill),
            frame => Assert.Equal(new[] { new JobExperience(gathering, 20, 1, 0, 0),
                new JobExperience(crafting, 60, 2, 20, 20) }, Assert.IsType<JobExperiencesUpdateEvent>(frame.Content).Experiences),
            frame => Assert.Equal(new[] { new JobCrafterDirectorySettings(WorkshopHandler.BaseJob, true, 0),
                new JobCrafterDirectorySettings(gathering, true, 17), new JobCrafterDirectorySettings(crafting, true, 1) },
                Assert.IsType<JobCrafterDirectorySettingsEvent>(frame.Content).Settings),
            frame => Assert.Equal(new[] { new JobBookSubscription(gathering, true) },
                Assert.IsType<JobBookSubscriptionEvent>(frame.Content).Subscriptions));
        foreach (var target in new[] { first, second })
        {
            Assert.Equal(0, target.Codec.Decodes);
            Assert.All(target.Frames, frame => Assert.Null(frame.Uid));
        }
    }

    [Fact]
    public async Task Unlisted_character_omits_subscription_event_and_empty_catalogue_still_sends_empty_initial_profession_messages()
    {
        var target = Recipient();
        target.Session.State.CrafterSettings[81_001] = new(1, true, false);
        await SendInitialAsync(target.Session, new([], _ => []));
        await ProfessionMessages.SendSubscriptionsAsync(target.Session);
        Assert.Collection(ProfessionFrames(target.Frames),
            frame => Assert.Empty(Assert.IsType<JobDescriptionEvent>(frame.Content).Jobs),
            frame => Assert.Empty(Assert.IsType<JobExperiencesUpdateEvent>(frame.Content).Experiences),
            frame => Assert.Empty(Assert.IsType<JobCrafterDirectorySettingsEvent>(frame.Content).Settings));
        Assert.Equal(0, target.Codec.Decodes);
    }

    [Fact]
    public async Task Profession_send_failure_stops_before_experience_settings_and_subscription_hook()
    {
        var failure = new IOException("test profession transport failure");
        var frames = new List<GameFrame>();
        var codec = new GameMessagingTests.CountingCodec();
        var session = new GameSession(codec, packet =>
        {
            var frame = Wire.Decode(packet);
            frames.Add(frame);
            return frame.Content is JobDescriptionEvent ? Task.FromException(failure) : Task.CompletedTask;
        });
        session.State.CrafterSettings[81_001] = new(1, true, true);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(async () =>
        {
            await SendInitialAsync(session, new([81_001], _ => []));
            await ProfessionMessages.SendSubscriptionsAsync(session);
        }));
        Assert.Equal(0, codec.Decodes);
        Assert.Single(ProfessionFrames(frames));
        Assert.IsType<JobDescriptionEvent>(frames.Last().Content);
    }

    [Fact]
    public async Task Subscription_failure_propagates_without_a_retry()
    {
        var failure = new IOException("test subscription transport failure");
        var codec = new GameMessagingTests.CountingCodec();
        var session = new GameSession(codec, _ => Task.FromException(failure));
        session.State.CrafterSettings[81_001] = new(1, true, true);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => ProfessionMessages.SendSubscriptionsAsync(session)));
        Assert.Equal(1, codec.Encodes);
    }

    private static (GameSession Session, List<GameFrame> Frames, GameMessagingTests.CountingCodec Codec) Recipient()
    {
        var frames = new List<GameFrame>();
        var codec = new GameMessagingTests.CountingCodec();
        var session = new GameSession(codec, packet => { frames.Add(Wire.Decode(packet)); return Task.CompletedTask; });
        return (session, frames, codec);
    }

    private static Task<int> SendInitialAsync(GameSession session, ProfessionMessages professions)
        => new GameEntryInitialMessages([], [], session.State, professions, 0, 0, () => new([]),
            () => new("1970-01-01T00:00:00.000Z", []), () => new([]),
            () => GameEntryInitialMessagesTests.Selection(session.CharacterId),
            () => InventoryMessages.FromCatalogue.Snapshot(session.State), () => new([]), () => new(81_001),
            () => EntryInformationMessages.PreviousConnection(session.State.PreviousVisit), AfterSelectionMessagesTests.Empty,
            InitialPresentationMessagesTests.Empty).SendAsync(session);

    private static IEnumerable<GameFrame> ProfessionFrames(IEnumerable<GameFrame> frames)
        => frames.Where(frame => frame.Content is JobDescriptionEvent or JobExperiencesUpdateEvent
            or JobCrafterDirectorySettingsEvent or JobBookSubscriptionEvent);

    private static void AssertDescriptions(GameFrame frame, int gathering, int gatherSkill, int ceiling, int crafting, int craftSkill)
    {
        Assert.Collection(Assert.IsType<JobDescriptionEvent>(frame.Content).Jobs,
            job =>
            {
                Assert.Equal(gathering, job.JobId);
                Assert.Equal(new JobSkillAction(gatherSkill,
                    new JobSkillActionCaseTimed(new(new JobSkillActionCollect(ceiling, 1), 30))), Assert.Single(job.Skills));
            },
            job =>
            {
                Assert.Equal(crafting, job.JobId);
                Assert.Equal(new JobSkillAction(craftSkill, new JobSkillActionCaseCraft(new(100))), Assert.Single(job.Skills));
            });
    }
}

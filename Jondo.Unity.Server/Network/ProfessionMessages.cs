using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using JobProgress = Jondo.Unity.Server.Managers.JobExperience;

namespace Jondo.Unity.Server.Network;

/// <summary>Profession state built from the supported game catalogue and the owning character.</summary>
internal sealed class ProfessionMessages(IEnumerable<int> directoryJobs,
    Func<int, IReadOnlyList<SkillDefinition>> skillsOf)
{
    /// <summary>The craft-skill probability is 100 in all four historical workshop captures.</summary>
    private const int CraftProbability = 100;

    internal IReadOnlyList<int> DirectoryJobs { get; } = directoryJobs.Distinct().OrderBy(id => id).ToArray();

    /// <summary>
    /// The base job belongs to directory settings; professions with progress must
    /// have a resource, recipe or smithmagic skill. The capture's twenty jobs are
    /// a historical observation, not a second catalogue or a required count.
    /// </summary>
    internal static IReadOnlyList<int> SupportedDirectoryJobs(IEnumerable<JobDefinition> jobs,
        Func<int, IReadOnlyList<SkillDefinition>> skillsOf, Func<int, int> recipesOf)
        => jobs.Where(job => ArtisanHandler.IsPractised(job.Id, skillsOf(job.Id), recipesOf))
            .Select(job => job.Id).Distinct().OrderBy(id => id).ToArray();

    internal JobDescriptionEvent Descriptions(SessionState state)
        => new(DirectoryJobs.Where(id => id != WorkshopHandler.BaseJob)
            .Select(id => Description(id, state.JobLevel(id), skillsOf(id))).ToArray());

    /// <summary>
    /// CharacterJobs progress, including untouched jobs. The old SendJobs read its
    /// profession IDs from the recording: removing that payload once sent no jobs
    /// at all, with saved progress intact and no count logged. Earlier still, every
    /// relog reset the displayed levels to one. Neither the recording's maxed-out
    /// character nor an empty recorded payload supplies this character's progress.
    /// </summary>
    internal JobExperiencesUpdateEvent Experiences(SessionState state)
        => new(DirectoryJobs.Where(id => id != WorkshopHandler.BaseJob).Select(id =>
        {
            long xp = state.Jobs.TryGetValue(id, out var progress) ? progress.Experience : 0;
            int level = JobProgress.LevelOf(xp);
            return new Jondo.Unity.Protocol.Game.JobExperience(id, JobProgress.Next(level), level,
                JobProgress.Floor(level), xp);
        }).ToArray());

    /// <summary>
    /// Shared with level-up descriptions: every skill of the job, gathering minimum
    /// one, maximum one while locked and the server ceiling once unlocked; craft 100%.
    /// The duration is the same GatherTenths used by the actual gathering action.
    /// </summary>
    internal static JobDescription Description(int jobId, int level, IEnumerable<SkillDefinition> skills)
        => new(jobId, skills.OrderBy(skill => skill.Id).Select(skill => Action(skill.Id, skill.IsGathering, 1,
            level >= skill.LevelMin ? GatheringHandler.Ceiling(level, skill.LevelMin) : 1)).ToArray());

    internal static JobSkillAction Action(int skillId, bool gathers, int least, int most)
        => new(skillId, gathers
            ? new JobSkillActionCaseTimed(new(new JobSkillActionCollect(most, least), Resources.GatherTenths))
            : new JobSkillActionCaseCraft(new(CraftProbability)));

    internal static JobBookSubscriptionEvent Subscriptions(SessionState state)
        => new(state.CrafterSettings.Where(setting => setting.Value.Listed).OrderBy(setting => setting.Key)
            .Select(setting => new JobBookSubscription(setting.Key, true)).ToArray());

    internal static Task SendSubscriptionsAsync(GameSession session)
    {
        var subscriptions = Subscriptions(session.State);
        // Preserve the live entry hook's omission for a character with no listings.
        return subscriptions.Subscriptions.Count == 0 ? Task.CompletedTask : session.SendAsync(subscriptions);
    }
}

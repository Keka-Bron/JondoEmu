using Jondo.Unity.Launcher.Managers;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>
    /// Resolves the authoritative 3.6 gathering metadata. Network execution is intentionally kept
    /// out until the map-element/skill link and the 3.6 state packets have been captured.
    /// </summary>
    public static class GatheringHandler
    {
        public static bool TryResolve(int skillId, out SkillDefinition skill,
                                      out JobDefinition job, out string error)
        {
            skill = null!;
            job = null!;
            error = "";
            if (!SkillManager.TryGet(skillId, out skill))
            {
                error = $"Compétence {skillId} inconnue.";
                return false;
            }
            if (!skill.IsGathering)
            {
                error = $"La compétence {skillId} n'est pas une récolte.";
                return false;
            }
            if (!JobManager.TryGet(skill.ParentJobId, out job))
            {
                error = $"Métier {skill.ParentJobId} absent pour la compétence {skillId}.";
                return false;
            }
            return true;
        }
    }
}

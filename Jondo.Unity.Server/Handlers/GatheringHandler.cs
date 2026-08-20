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
                error = $"Habilidad {skillId} desconocida.";
                return false;
            }
            if (!skill.IsGathering)
            {
                error = $"La habilidad {skillId} no es de recoleccion.";
                return false;
            }
            if (!JobManager.TryGet(skill.ParentJobId, out job))
            {
                error = $"Falta el oficio {skill.ParentJobId} de la habilidad {skillId}.";
                return false;
            }
            return true;
        }
    }
}

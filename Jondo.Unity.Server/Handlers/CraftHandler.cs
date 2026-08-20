using System;
using System.Collections.Generic;
using Jondo.Unity.Launcher.Managers;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>Resolves a 3.6 craft skill and its recipes without inventing a 2.68 wire format.</summary>
    public static class CraftHandler
    {
        public static bool TryResolve(int skillId, out SkillDefinition skill,
                                      out JobDefinition job,
                                      out IReadOnlyList<RecipeDefinition> recipes,
                                      out string error)
        {
            skill = null!;
            job = null!;
            recipes = Array.Empty<RecipeDefinition>();
            error = "";
            if (!SkillManager.TryGet(skillId, out skill))
            {
                error = $"Compétence {skillId} inconnue.";
                return false;
            }
            recipes = RecipeManager.ForSkill(skillId);
            if (recipes.Count == 0)
            {
                error = $"Aucune recette 3.6 pour la compétence {skillId}.";
                return false;
            }
            if (!JobManager.TryGet(skill.ParentJobId, out job))
            {
                error = $"Métier {skill.ParentJobId} absent pour la compétence {skillId}.";
                return false;
            }
            return true;
        }

        /// <summary>Validates that a result belongs to the craft skill selected on the element.</summary>
        public static bool TryResolveRecipe(int skillId, int resultId,
                                            out RecipeDefinition recipe, out string error)
        {
            recipe = null!;
            error = "";
            if (!RecipeManager.TryGetByResult(resultId, out recipe))
            {
                error = $"Recette produisant l'objet {resultId} inconnue.";
                return false;
            }
            if (recipe.SkillId != skillId)
            {
                error = $"La recette {resultId} n'appartient pas à la compétence {skillId}.";
                recipe = null!;
                return false;
            }
            return true;
        }
    }
}

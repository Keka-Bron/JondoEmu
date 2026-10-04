using System;
using System.Collections.Generic;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Handlers
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
                error = $"Habilidad {skillId} desconocida.";
                return false;
            }
            recipes = RecipeManager.ForSkill(skillId);
            if (recipes.Count == 0)
            {
                error = $"No hay receta 3.6 para la habilidad {skillId}.";
                return false;
            }
            if (!JobManager.TryGet(skill.ParentJobId, out job))
            {
                error = $"Falta el oficio {skill.ParentJobId} de la habilidad {skillId}.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// The recipe of this skill that the ingredients on the workbench make, if any: the same
        /// items in the same quantities, nothing missing and nothing left over. A signature rune is
        /// not an ingredient and the caller takes it out before asking.
        /// </summary>
        public static RecipeDefinition? Match(int skillId, IReadOnlyDictionary<int, int> ingredients)
        {
            if (ingredients.Count == 0) return null;
            foreach (var recipe in RecipeManager.ForSkill(skillId))
            {
                var wanted = new Dictionary<int, int>();
                foreach (var i in recipe.Ingredients)
                    wanted[i.ItemId] = wanted.TryGetValue(i.ItemId, out int had) ? had + i.Quantity : i.Quantity;
                if (wanted.Count != ingredients.Count) continue;

                bool same = true;
                foreach (var pair in wanted)
                {
                    if (!ingredients.TryGetValue(pair.Key, out int got) || got != pair.Value) { same = false; break; }
                }
                if (same) return recipe;
            }
            return null;
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
                error = $"La receta {resultId} no es de la habilidad {skillId}.";
                recipe = null!;
                return false;
            }
            return true;
        }
    }
}

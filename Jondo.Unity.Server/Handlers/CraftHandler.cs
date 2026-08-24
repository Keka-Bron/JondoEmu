using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Managers;
using Jondo.Unity.Launcher.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>Resolves a 3.6 craft skill and its recipes without inventing a 2.68 wire format.</summary>
    public static class CraftHandler
    {
        /// <summary>
        /// Opens the native 3.6.10.10 craft interface for a verified station binding. This
        /// deliberately stops at opening the exchange: ingredient mutation still requires the
        /// exact current request/result aliases and is not inferred from static recipe data.
        /// </summary>
        public static async Task OpenAsync(NetworkStream stream, Interactives.Element element,
                                           int skillId)
        {
            if (!TryResolve(skillId, out SkillDefinition skill, out JobDefinition job,
                            out IReadOnlyList<RecipeDefinition> recipes, out string error))
            {
                Console.WriteLine($"[Crafting] Station {element.Id} rejected: {error}");
                return;
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.InteractiveUsedMessage,
                    ConnectionProtocol.BuildElementInUse(
                        element.Id, skillId, SessionContext.State.CharacterId)));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.ExchangeCraftStartedEvent,
                    ConnectionProtocol.BuildCraftStarted(skillId)));

            Console.WriteLine($"[Crafting] Opened job {job.Id} station on map " +
                              $"{SessionContext.State.MapId} (element {element.Id}, skill " +
                              $"{skill.Id}, {recipes.Count} recipes).");
        }

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
                error = $"Unknown skill {skillId}.";
                return false;
            }
            recipes = RecipeManager.ForSkill(skillId);
            if (recipes.Count == 0)
            {
                error = $"No 3.6 recipe exists for skill {skillId}.";
                return false;
            }
            if (!JobManager.TryGet(skill.ParentJobId, out job))
            {
                error = $"Skill {skillId} references missing job {skill.ParentJobId}.";
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
                error = $"Unknown recipe result {resultId}.";
                return false;
            }
            if (recipe.SkillId != skillId)
            {
                error = $"Recipe {resultId} does not belong to skill {skillId}.";
                recipe = null!;
                return false;
            }
            return true;
        }
    }
}

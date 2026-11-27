using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.World.Fights;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Quests;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Watches a fight finish and ticks off the quest objectives it earned.
    /// </summary>
    /// <remarks>
    /// The other half of the engine. The client tells the server when a free-text objective is done
    /// — it is the only one that knows — but nobody is going to be trusted about having killed
    /// something, so the three objective types that name a monster are settled here, from what
    /// actually died.
    ///
    /// Modelled on <see cref="ChallengeWatcher"/> and hooked in beside it, because there is exactly
    /// one place a fight really ends and this is a second thing that wants to know.
    /// </remarks>
    public static class QuestWatcher
    {
        /// <summary>
        /// A fight is over. Counts what was beaten and moves the quests that wanted it.
        /// </summary>
        /// <remarks>
        /// Three things worth saying about the counting.
        ///
        /// <b>Summons do not count.</b> They sit in Rojo with <c>IsMonster</c> set and a level of
        /// their own, and this project has already been bitten twice by forgetting it: a monster
        /// that summons was paying out kamas for creatures it made up during the fight. A quest
        /// that wanted three Jalatós beaten would otherwise be finished by one Jalató that summons
        /// two more of itself.
        ///
        /// <b>The monster is <c>MonsterId</c>, never the fighter id.</b> <c>Fighter.Id</c> is a
        /// per-fight sequence, negative, and means nothing outside the fight it belongs to.
        ///
        /// <b>Losing counts for nothing</b>, but for a beat binding marked «lost». Every one of these objective types is worded as
        /// beating something.
        /// </remarks>
        public static async Task FightEndedAsync(GameSession session, FightInstance fight, bool won)
        {
            using var context = SessionContext.Push(session);
            if (!won)
            {
                await CreditDefeatAsync(session, fight);
                return;
            }

            var log = Quests.Log;
            var book = Quests.Book;
            if (log == null || book == null) return;

            // How many of each kind were beaten. Grouped by the template rather than by the
            // fighter, because a quest asks for "three Jalatós" and does not care which three.
            var beaten = new Dictionary<int, int>();
            foreach (var monster in fight.Rojo)
            {
                if (!monster.BeatenAsGroupMember) continue;
                if (monster.MonsterId <= 0) continue;

                beaten.TryGetValue(monster.MonsterId, out int already);
                beaten[monster.MonsterId] = already + 1;
            }

            if (beaten.Count == 0) return;

            // The map the fight was ABOUT, not the arena it was fought in. Type 16 ("on this map")
            // and the Beat bindings name a roleplay map; during the fight State.MapId is the arena
            // (ResolveArenaMapId), so reading that left every dungeon-boss kill unchecked.
            long mapId = fight.RoleplayMapId != 0 ? fight.RoleplayMapId : SessionContext.State.MapId;

            var shared = new HashSet<(int QuestId, int StepId, int ObjectiveId)>();
            await CreditKillsAsync(session, beaten, mapId, shared, isCompanion: false);
            if (shared.Count == 0) return;

            await QuestSharing.ShareQuestActionAsync(
                companion => CreditKillsAsync(companion, beaten, mapId, shared, isCompanion: true),
                companion => companion.Session is GameSession participant
                    && participant.State.FightId == fight.FightId && fight.HaGanado(companion.CharacterId),
                quests: shared.Select(key => key.QuestId).Distinct().ToList());
        }

        /// <summary>
        /// A fight lost: only the beat bindings marked «lost» move, for the monsters that were in it.
        /// The quest that wants the character overpowered -- caught and thrown into a cell -- goes
        /// on from the defeat; every other objective still counts losing as nothing.
        /// </summary>
        private static async Task CreditDefeatAsync(GameSession session, FightInstance fight)
        {
            var log = Quests.Log;
            var book = Quests.Book;
            if (log == null || book == null || Quests.Bindings == null) return;

            // Who was there, summons aside: a defeat is against the group, not what it made up.
            var faced = new HashSet<int>();
            foreach (var monster in fight.Rojo)
            {
                if (monster.MonsterId <= 0 || monster.EsInvocado) continue;
                faced.Add(monster.MonsterId);
            }
            if (faced.Count == 0) return;

            long mapId = fight.RoleplayMapId != 0 ? fight.RoleplayMapId : SessionContext.State.MapId;
            foreach (var run in log.Doing().ToList())
            {
                var step = book.Step(run.StepId);
                if (step == null || run.Finished) continue;

                foreach (var objective in step.Objectives.ToList())
                {
                    var binding = Quests.Bindings.Of(objective.Id);
                    if (binding == null || binding.Kind != QuestBindingKind.Beat || !binding.Lost) continue;
                    if (run.Done.Contains(objective.Id)) continue;
                    if (!binding.NamesOneOf(faced.Contains)) continue;
                    if (binding.MapId != 0 && binding.MapId != mapId && !SameDungeonRoom(binding.MapId, mapId)) continue;
                    if (binding.AfterObjectives.Any(before => !run.Done.Contains(before) && !log.ObjectiveDone(before)))
                        continue;

                    System.Console.WriteLine($"[Quests] Objective {objective.Id}: lost as the quest wants it.");
                    await Quests.TickAsync(session, run.QuestId, objective.Id);
                }
            }
        }

        private static async Task CreditKillsAsync(GameSession session, Dictionary<int, int> beaten, long mapId,
                                                   HashSet<(int QuestId, int StepId, int ObjectiveId)> shared, bool isCompanion)
        {
            var log = Quests.Log;
            var book = Quests.Book;
            if (log == null || book == null) return;

            bool CanCredit(QuestRun run, QuestStep step, QuestObjective objective)
            {
                if (run.Finished || run.StepId != step.Id || run.Done.Contains(objective.Id)) return false;
                var key = (run.QuestId, step.Id, objective.Id);
                if (isCompanion) return shared.Contains(key);
                shared.Add(key);
                return true;
            }

            // The quests in hand are copied out first: ticking an objective can finish a step and
            // a quest, which changes the log, and walking a collection that is being changed is
            // how this would throw in front of a player at the end of a fight.
            foreach (var run in log.Doing().ToList())
            {
                var step = book.Step(run.StepId);
                if (step == null) continue;

                foreach (var objective in step.Objectives.ToList())
                {
                    if (objective.MonsterId == 0) continue;
                    if (!beaten.TryGetValue(objective.MonsterId, out int killed)) continue;
                    if (Quests.Bindings?.Of(objective.Id)?.Lost == true) continue;

                    // Type 16 wants it done on one particular map. Exact match first; if the kill
                    // was on another room of the same dungeon (boss placed on the wrong hall while
                    // the catalogue still names the real one), credit it anyway.
                    if (objective.OnMap != 0 && objective.OnMap != mapId &&
                        !SameDungeonRoom(objective.OnMap, mapId))
                        continue;

                    // "In one fight" means the tally does not carry: this fight either did it or
                    // it did not. Type 14 is the one that adds up across fights.
                    if (objective.InOneFight && killed < objective.Needed) continue;

                    // Under an idol's alteration, when it says: read before the fight's count
                    // brings the alteration down (AlterationHandler.AfterFightAsync comes after).
                    if (!UnderItsAlteration(objective)) continue;

                    // Not while something else of its step comes first -- a holy water to use, an
                    // item to hand over --: fights side by side, and talks, do not hold it back.
                    if (BehindOtherWork(step, objective, run)) continue;

                    if (CanCredit(run, step, objective))
                        await Quests.TickAsync(session, run.QuestId, objective.Id, killed);
                }

                // And the free-text ones that are a fight in all but the catalogue's type.
                // "Vaincre le Milimilou" is objective 9785 of quest 1635, written as prose with no
                // monster in it: the catalogue says nothing, so the binding does.
                // One win closes one link of a chain: three rows on the same monsters, each after
                // the one before, take three fights, not one that ticks them all in a row.
                var closedNow = new HashSet<int>();
                foreach (var objective in step.Objectives.ToList())
                {
                    var binding = Quests.Bindings.Of(objective.Id);
                    if (binding == null || binding.Kind != QuestBindingKind.Beat || binding.Lost) continue;
                    if (!binding.NamesOneOf(beaten.ContainsKey)) continue;
                    if (binding.MapId != 0 && binding.MapId != mapId &&
                        !SameDungeonRoom(binding.MapId, mapId))
                        continue;
                    if (binding.AfterObjectives.Any(before => !run.Done.Contains(before) && !log.ObjectiveDone(before)))
                        continue;
                    if (binding.AfterObjectives.Any(closedNow.Contains)) continue;

                    if (!CanCredit(run, step, objective)) continue;

                    // Several fights for one objective: each one counted, the last one ticks it.
                    if (binding.Times > 1 && !Quests.CountTowards(run, objective.Id, binding.Times))
                        continue;

                    // A win that only earns it: the mark goes down, and pressing the element
                    // again picks it up (Quests, «marks»).
                    if (binding.Marks != 0)
                    {
                        if (SessionContext.State.ElementsUsed.Add(binding.Marks))
                            DatabaseManager.RememberElement(SessionContext.State.CharacterId, binding.Marks);
                        System.Console.WriteLine($"[Quests] Objective {objective.Id}: won, mark {binding.Marks} noted; its element picks it up.");
                        continue;
                    }

                    await Quests.TickAsync(session, run.QuestId, objective.Id);
                    closedNow.Add(objective.Id);
                }
            }
        }

        /// <summary>
        /// Whether the character fought under the alteration the objective asks for, when it asks for
        /// one: an idol of the Turquoise Dofus's blessings used in the boss's room. Its one fight is
        /// still on the list when the quests are told; the count brings it down afterwards.
        /// </summary>
        internal static bool UnderItsAlteration(QuestObjective objective, long? characterId = null)
            => objective.UnderAlteration == 0
               || Alterations.Has(characterId ?? SessionContext.State.CharacterId, objective.UnderAlteration);

        /// <summary>
        /// Whether a fight objective still has, before it in its step, something pending that is
        /// neither a talk nor another fight: Klaus Konbah beaten before his holy water was used.
        /// </summary>
        internal static bool BehindOtherWork(QuestStep step, QuestObjective objective, QuestRun run)
        {
            // Objectives a binding lets go in any order do not wait on one another: the fourth
            // patient of «La inyección es la solución» fought before the second is cured.
            bool anyOrder = Quests.Bindings?.Of(objective.Id)?.AnyOrder == true;
            foreach (var before in step.Objectives)
            {
                if (before.Id == objective.Id) return false;
                if (run.Done.Contains(before.Id)) continue;
                if (before.TypeId == QuestCatalogue.GoToSee || before.MonsterId != 0) continue;
                if (anyOrder && Quests.Bindings?.Of(before.Id)?.AnyOrder == true) continue;
                return true;
            }
            return false;
        }

        /// <summary>Whether both maps are rooms of the same dungeon (or the same map).</summary>
        internal static bool SameDungeonRoom(long a, long b)
        {
            if (a == 0 || b == 0) return false;
            if (a == b) return true;

            var left = DungeonManager.OfRoom(a);
            var right = DungeonManager.OfRoom(b);
            return left != null && right != null && left.Id == right.Id;
        }
    }
}

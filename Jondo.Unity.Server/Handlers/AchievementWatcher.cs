using System.Collections.Generic;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Watches a fight finish and counts what the achievements count: every monster beaten, and
    /// every monster beaten in a fight where a challenge was won.
    /// </summary>
    /// <remarks>
    /// Hooked in beside <see cref="ChallengeWatcher"/> and <see cref="QuestWatcher"/>, after both,
    /// because there is one place a fight really ends and the challenge verdicts have to be in by
    /// then: "Vencer a los siguientes monstruos superando un reto" — 1,887 objectives written as
    /// <c>Ef&gt;monster,0</c> — needs to know whether one was won.
    ///
    /// The same three rules as the quest watcher: summons do not count, the monster is the
    /// template and never the fighter, and losing counts for nothing.
    /// </remarks>
    public static class AchievementWatcher
    {
        public static void FightEnded(FightInstance fight, bool won)
        {
            // Only a real fight against monsters: a duel, the Koliseo and a training session at
            // the kanojedo hand out nothing, and beating a practice puch is not beating a monster.
            if (!won || !fight.Reglas.ReparteBotin) return;

            var beaten = new Dictionary<int, int>();
            foreach (var monster in fight.Rojo)
            {
                if (monster.EsInvocado) continue;
                if (monster.MonsterId <= 0) continue;

                beaten.TryGetValue(monster.MonsterId, out int already);
                beaten[monster.MonsterId] = already + 1;
            }

            if (beaten.Count == 0) return;

            Achievements.AfterFight(beaten, DungeonManager.IsRoom(fight.RoleplayMapId),
                                    ChallengeWatcher.WonIn(fight));
        }
    }
}

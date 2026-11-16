using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The experience scrolls: effect 605, «+#1 a #2 puntos de experiencia» -- the white one's 200,
    /// the ivory's 2,000, the golden's 20,000 --, and the Gatrool industrial, which gives energy too;
    /// and the job scrolls, effect 614, «+#1 punto(s) de experiencia del oficio #2», 34 of them.
    /// </summary>
    /// <remarks>
    /// The experience goes as every reward's does (<see cref="CharacterRewards.GiveExperienceAsync"/>:
    /// the level-up, the kub, the kuf with the gain), at the scroll's own figure: the server's
    /// experience bonus is for what is earned, not for a fixed amount. What a capture would show
    /// around it is not captured.
    /// </remarks>
    public static class ExperienceItemHandler
    {
        public const int ExperienceEffect = 605;

        /// <summary>«+#1 punto(s) de experiencia del oficio #2».</summary>
        public const int JobExperienceEffect = 614;

        /// <summary>Effects that say something of the item and do nothing on use.</summary>
        private static readonly HashSet<int> Markers = new() { 981, 983, 2818 };

        /// <summary>Whether an item gives experience and nothing besides but life or energy.</summary>
        internal static bool GivesExperience(IReadOnlyList<DatabaseManager.ItemEffectData> effects)
            => effects.Any(e => e.EffectId == ExperienceEffect)
               && effects.All(e => e.EffectId is ExperienceEffect or RecoveryItemHandler.LifeEffect
                                   or RecoveryItemHandler.EnergyEffect || Markers.Contains(e.EffectId));

        /// <summary>
        /// What a job scroll trains: the job in its second number and the points in its third. The
        /// baker's scroll of the house chest's capture carries «f6 { f1: 100, f3: 28 }», a hundred points
        /// of the farmer's job -- the baker's is the farmer's since the jobs were merged --, and all 34
        /// carry a hundred. Null when the item does anything else.
        /// </summary>
        internal static IReadOnlyList<(int Job, long Points)>? JobScrollOf(IReadOnlyList<DatabaseManager.ItemEffectData> effects)
        {
            if (!effects.Any(e => e.EffectId == JobExperienceEffect)) return null;
            if (!effects.All(e => e.EffectId == JobExperienceEffect || Markers.Contains(e.EffectId))) return null;
            var trained = effects.Where(e => e.EffectId == JobExperienceEffect)
                                 .Select(e => (Job: e.DiceSide, Points: (long)(e.Value != 0 ? e.Value : e.DiceNum)))
                                 .Where(t => t.Job > 0 && t.Points > 0)
                                 .ToList();
            return trained.Count > 0 ? trained : null;
        }

        /// <summary>An experience scroll used: false when the item is no such thing.</summary>
        public static async Task<bool> TryUseAsync(GameSession session, Equipment.Item item)
        {
            using var context = SessionContext.Push(session);
            var stream = session.Stream;
            var effects = DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(item.Template));
            if (JobScrollOf(effects) is { } jobs) return await TrainAsync(session, item, jobs);
            if (!GivesExperience(effects)) return false;

            var me = SessionContext.State;
            if (me.IsInFight || me.FightId != 0) return true;
            if (!await ItemUseHandler.CriterionHoldsAsync(stream, item)) return true;

            long experience = effects.Where(e => e.EffectId == ExperienceEffect)
                                     .Sum(e => (long)RecoveryItemHandler.Roll(e, Random.Shared));
            var recovery = RecoveryItemHandler.RecoveryOf(effects.Where(e => e.EffectId is RecoveryItemHandler.LifeEffect
                                                                              or RecoveryItemHandler.EnergyEffect).ToList());
            long character = me.CharacterId;
            var now = DateTime.UtcNow;
            int missing = RestingLife.MissingAt(character, now);
            int energy = Energy.Of(character);
            var (life, gained) = recovery == null ? (0, 0) : RecoveryItemHandler.WouldGive(recovery, missing, energy, Random.Shared);

            if (!await ItemUseHandler.SpendOneAsync(session, item.Uid, weigh: false)) return true;

            if (life > 0)
            {
                RestingLife.Set(character, missing - life, now);
                me.RegenerationStartedUtc = now;
            }
            if (gained > 0) Energy.Set(character, energy + gained);

            bool levelled = await CharacterRewards.GiveExperienceAsync(stream, experience, serverBonus: false);
            if (life > 0)
                await WriteAsync(stream, ConnectionProtocol.BuildRegenerationStarted(ConnectionProtocol.RegenerationRateNow()));
            if (gained > 0)
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildSystemMessage(InfoMessages.EnergyRecovered, gained.ToString())));
            await ItemUseHandler.WeighAsync(stream);
            CharacterRewards.Save();
            if (levelled) await Achievements.AfterLevelAsync(session);

            Console.WriteLine($"[Items] {me.CharacterName} uses {item.Template}: +{experience} experience" +
                              (gained > 0 ? $", +{gained} energy" : "") + (levelled ? $", level {me.CharacterLevel}." : "."));
            return true;
        }

        /// <summary>
        /// A job scroll read: its points to its job, at the scroll's own figure -- the Almanax's bonus is
        /// for what is earned working --, with the job's level-up and «Has ganado {0} XP en el oficio de
        /// $job{1}». A job this client does not have keeps the scroll.
        /// </summary>
        private static async Task<bool> TrainAsync(GameSession session, Equipment.Item item,
                                                   IReadOnlyList<(int Job, long Points)> jobs)
        {
            var stream = session.Stream;
            var me = SessionContext.State;
            if (me.IsInFight || me.FightId != 0) return true;
            if (jobs.Any(j => !JobManager.TryGet(j.Job, out _)))
            {
                Console.WriteLine($"[Items] Job scroll {item.Template} trains a job this client does not have.");
                return true;
            }
            if (!await ItemUseHandler.CriterionHoldsAsync(stream, item)) return true;
            if (!await ItemUseHandler.SpendOneAsync(session, item.Uid, weigh: false)) return true;

            foreach (var (job, points) in jobs)
            {
                await WorkshopHandler.GiveJobExperienceAsync(session, job, points, bonus: false);
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildSystemMessage(InfoMessages.JobExperienceGained, points.ToString(), job.ToString())));
            }
            await ItemUseHandler.WeighAsync(stream);

            Console.WriteLine($"[Items] {me.CharacterName} reads job scroll {item.Template}: " +
                              string.Join(", ", jobs.Select(j => $"+{j.Points} in job {j.Job}")) + ".");
            return true;
        }

        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);
    }
}

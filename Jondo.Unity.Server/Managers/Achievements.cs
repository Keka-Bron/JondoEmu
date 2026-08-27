using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Achievements;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The achievements: earning them, and paying for them.
    /// </summary>
    /// <remarks>
    /// Same two halves as the quests. Ankama's catalogue is the same for everybody and is read once
    /// at startup; what one character has earned lives on <see cref="SessionState"/> and is reached
    /// through <c>SessionContext.State</c>. A static dictionary here would hand somebody else's
    /// badge to whoever logged in second.
    ///
    /// <b>Earning and being paid are two different things</b>, and the capture is what says so.
    /// <c>Logros\aceptar recompensas de un logro</c> is a player pressing the claim button: the
    /// client sends <c>mga {1: 8990}</c> and only then does the reward arrive. So the server marks
    /// an achievement earned when it is earned, and hands over the items when it is asked.
    ///
    /// The protocol was measured, not guessed. Across the captures every id carried by
    /// <c>mfs</c> and <c>mfu</c> is a real achievement, and the meanings line up with what the
    /// player was doing: the tutorial capture earns 8518 "Primer tiempo", whose objective reads
    /// <c>(Qf=2511)</c>, right after finishing quest 2511 "Primeras armas". The long-route capture
    /// earns "Landas de Cania" and "Bosque de Litneg" while crossing them, and the guild capture
    /// earns "Recibidor de gremio amakneano" on walking in.
    /// </remarks>
    public static class Achievements
    {
        private static AchievementCatalogue? _book;

        /// <summary>Ankama's catalogue. Null until <see cref="Load"/> has run.</summary>
        public static AchievementCatalogue? Book => _book;

        public static bool Ready => _book != null && _book.Ready;

        /// <summary>This character's badges. Null before entering the world.</summary>
        public static AchievementLog? Log => SessionContext.State.Achievements;

        /// <summary>Reads the catalogue. Once, at startup.</summary>
        public static void Load()
        {
            if (_book != null) return;

            _book = new AchievementCatalogue(null, Console.WriteLine);
            if (!_book.Ready)
            {
                Console.WriteLine("[Logros] No hay catálogo. No se conseguirá ninguno.");
                return;
            }

            Console.WriteLine($"[Logros] {_book.Count:N0} logros, {_book.ObjectiveCount:N0} objetivos, " +
                              $"{_book.RewardCount:N0} recompensas, {_book.FromQuestsCount:N0} " +
                              "que se ganan acabando misiones.");
        }

        /// <summary>
        /// Puts this character's badges on, from the database.
        /// </summary>
        /// <remarks>
        /// Nothing is re-checked on login, deliberately. An achievement earned under an older
        /// version of the rules stays earned: taking somebody's badge away because this emulator
        /// got better at judging is worse than leaving one that should not have been given.
        /// </remarks>
        public static void LoadFrom(long characterId)
        {
            var quests = SessionContext.State.Quests;
            if (_book == null || !_book.Ready || quests == null)
            {
                SessionContext.State.Achievements = null;
                return;
            }

            var log = new AchievementLog(_book, quests);

            int rows = 0;
            foreach (var (achievement, claimed) in DatabaseManager.LoadAchievements(characterId))
            {
                log.Restore(achievement, claimed);
                rows++;
            }

            SessionContext.State.Achievements = log;
            if (rows > 0)
            {
                Console.WriteLine($"[Logros] {rows} en la vitrina del personaje {characterId}, " +
                                  $"{log.Points} puntos.");
            }
        }

        /// <summary>
        /// A quest has just been finished. Grant whatever that earned.
        /// </summary>
        public static async Task AfterQuestAsync(NetworkStream stream, int questId)
        {
            var log = Log;
            if (log == null) return;

            foreach (int achievement in log.AfterQuest(questId))
            {
                await AnnounceAsync(stream, achievement);
            }
        }

        /// <summary>
        /// Tells the client an achievement is earned, and writes it down.
        /// </summary>
        /// <remarks>
        /// Two messages, in the order the captures have them: <c>mfu</c> with the character's level
        /// and id, then <c>mfs</c> naming the achievement. The level in <c>mfu</c> is the
        /// <em>character's</em>, not the achievement's — 1 and 2 in the tutorial where the player
        /// was levelling, 200 in the long-route capture for achievements whose own levels are 30,
        /// 50, 110 and 140.
        /// </remarks>
        private static async Task AnnounceAsync(NetworkStream stream, int achievementId)
        {
            var state = SessionContext.State;
            DatabaseManager.SaveAchievement(state.CharacterId, achievementId, claimed: false);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Mfu, AchievementProtocol.BuildEarned(
                    state.CharacterLevel, state.CharacterId, achievementId)));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Mfs, AchievementProtocol.BuildFinished(achievementId)));

            string name = _book?.Of(achievementId)?.Name ?? "";
            Console.WriteLine($"[Logros] Conseguido el {achievementId}" +
                              (name.Length > 0 ? $" «{name}»" : "") + ".");
        }

        /// <summary>
        /// The client is asking for the reward of an achievement it has earned (mga).
        /// </summary>
        /// <remarks>
        /// <paramref name="achievementId"/> of -1 is the client asking for everything it is owed,
        /// which is what it sends on entering the world in three of the captures.
        ///
        /// A reward can carry its own criterion — one achievement paying different people
        /// differently — and one that this engine cannot judge is <b>not</b> paid. That is the
        /// opposite of what the quest start conditions do, on purpose: letting an unreadable
        /// condition through there costs somebody a quest they get anyway, and letting one through
        /// here hands over an item nobody earned.
        /// </remarks>
        public static async Task ClaimAsync(NetworkStream stream, int achievementId)
        {
            var log = Log;
            if (log == null || _book == null) return;

            var owed = new List<int>();
            if (achievementId > 0)
            {
                if (log.Has(achievementId) && !log.WasClaimed(achievementId)) owed.Add(achievementId);
            }
            else
            {
                owed.AddRange(log.Unclaimed());
            }

            foreach (int id in owed)
            {
                if (!log.MarkClaimed(id)) continue;

                DatabaseManager.SaveAchievement(SessionContext.State.CharacterId, id, claimed: true);
                await PayAsync(stream, id);

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Mfs, AchievementProtocol.BuildFinished(id)));
            }
        }

        /// <summary>
        /// Hands over what an achievement promised.
        /// </summary>
        /// <remarks>
        /// <b>The items only, and it is the same gap the quests have.</b> An achievement's items
        /// come with exact quantities — 2,137 of the 6,394 rewards carry some — but the experience
        /// and the kamas are ratios, multipliers on a base this emulator does not have. Ornaments,
        /// titles, emotes and guild points are real rewards this server has nowhere to put yet, so
        /// they are named in the log rather than silently dropped.
        /// </remarks>
        private static async Task PayAsync(NetworkStream stream, int achievementId)
        {
            var achievement = _book?.Of(achievementId);
            if (achievement == null) return;

            foreach (var reward in achievement.Rewards)
            {
                if (!Owed(reward)) continue;

                foreach (var (item, count) in reward.Items)
                {
                    if (!await Equipment.GiveAsync(stream, item, Math.Max(1, count)))
                    {
                        Console.WriteLine($"[Logros] El objeto {item} del logro {achievementId} no se " +
                                          "ha podido dar.");
                    }
                }

                var pending = new List<string>();
                if (reward.Titles.Count > 0) pending.Add($"{reward.Titles.Count} título(s)");
                if (reward.Ornaments.Count > 0) pending.Add($"{reward.Ornaments.Count} ornamento(s)");
                if (reward.Emotes.Count > 0) pending.Add($"{reward.Emotes.Count} emote(s)");
                if (reward.Spells.Count > 0) pending.Add($"{reward.Spells.Count} hechizo(s)");
                if (reward.ExperienceRatio > 0 || reward.KamasRatio > 0)
                {
                    pending.Add($"experiencia x{reward.ExperienceRatio} y kamas x{reward.KamasRatio}, " +
                                "que son multiplicadores sin base conocida");
                }

                if (pending.Count > 0)
                {
                    Console.WriteLine($"[Logros] El {achievementId} promete además {string.Join(", ", pending)}: " +
                                      "todavía no se entrega.");
                }
            }
        }

        /// <summary>Whether a reward's own condition lets this character have it.</summary>
        private static bool Owed(AchievementReward reward)
        {
            var log = Log;
            var quests = SessionContext.State.Quests;
            if (reward.Criterion.Length == 0) return true;
            if (log == null || quests == null) return false;

            var verdict = World.Quests.QuestCriterion.Judge(reward.Criterion, quests);
            return verdict.Met && verdict.FullyJudged && !verdict.Broke;
        }
    }
}

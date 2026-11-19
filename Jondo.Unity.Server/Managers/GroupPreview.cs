using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Achievements;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What a monster group on the map holds for whoever looks at it: the items it may drop, each
    /// with its chance, and the achievements beating it can still earn. JondoFix shows both under
    /// the client's own group tooltip (MonsterGroupTooltip.cs), asked through the control API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The chances are the fight's own (<see cref="FightHandler"/>'s RollLoot): the monster's row
    /// for its grade times the prospecting multiplier, unless the row disables it, and the server's
    /// drop bonus on top. Not the challenges, not the Almanax, not an anomaly's zone: what the
    /// group pays in a plain fight. An item several monsters drop is shown once, at the chance of
    /// getting at least one: 1 - Π(1 - p).
    /// </para>
    /// <para>
    /// A row with a criterion -- a quest's item -- is shown to whoever meets it now, as the fight
    /// would hand it out; the raid and season rows of the global table, which ask where the fight
    /// is, are left out unless they ask nothing.
    /// </para>
    /// </remarks>
    public static class GroupPreview
    {
        /// <summary>One monster of the group, as the client's tooltip knows it: template and grade (1 to 5).</summary>
        public readonly record struct Member(int Monster, int Grade);

        /// <summary>An item and its chance in percent.</summary>
        public readonly record struct Drop(int Item, double Percent);

        /// <summary>An achievement to earn, its points, and the monster of the group it is about.</summary>
        public readonly record struct Unlock(int Achievement, int Points, int Monster);

        /// <summary>The most achievements listed: the tooltip is not the achievements window.</summary>
        public const int MostUnlocks = 6;

        /// <summary>The chance of getting at least one of an item that several rolls may give.</summary>
        public static double AtLeastOne(IEnumerable<double> percents)
        {
            double none = 1.0;
            foreach (double p in percents) none *= 1.0 - Math.Clamp(p, 0.0, 100.0) / 100.0;
            return (1.0 - none) * 100.0;
        }

        /// <summary>One row's chance in a plain fight, as RollLoot reckons it.</summary>
        public static double ChanceOf(double percent, bool noProspecting, double multiplier, int dropBonus)
        {
            double chance = noProspecting ? percent : Math.Min(100.0, percent * multiplier);
            return ServerSettings.ChanceWithBonus(chance, dropBonus);
        }

        /// <summary>
        /// The group's items for the session in context, most likely first. <paramref name="receives"/>
        /// answers a row's criterion for a monster; null takes the player's own (PlayerCriteria).
        /// </summary>
        public static List<Drop> Drops(IReadOnlyList<Member> group, int prospecting, long mapId,
                                       Func<string, int, bool>? receives = null)
        {
            receives ??= (criterion, monster) => PlayerCriteria.Met(criterion, mapId, null, monster);
            double multiplier = FightHandler.DropMultiplier(prospecting);
            int bonus = ServerSettings.Current.DropBonusPercent;

            var rolls = new Dictionary<int, List<double>>();
            void Add(int item, double percent)
            {
                if (percent <= 0) return;
                if (!rolls.TryGetValue(item, out var list)) rolls[item] = list = new List<double>();
                list.Add(percent);
            }

            foreach (var member in group)
            {
                if (member.Monster <= 0) continue;
                // The server's coin falls off every monster, without a roll (JondoCoin).
                Add(JondoCoin.TemplateId, 100.0);

                foreach (var row in DatabaseManager.GetMonsterDrops(member.Monster, Math.Max(0, member.Grade - 1)))
                {
                    if (row.ReceiverCriterion.Length > 0 && !receives(row.ReceiverCriterion, member.Monster)) continue;
                    Add(row.ObjectId, ChanceOf(row.PercentDrop, row.NoProspecting, multiplier, bonus));
                }

                foreach (var row in DatabaseManager.GetMonsterGlobalDrops(member.Monster))
                {
                    if (row.ObjectId == AnomalyLoot.Fragment || row.ReceiverCriterion.Length > 0) continue;
                    Add(row.ObjectId, ChanceOf(row.PercentDrop, true, multiplier, bonus));
                }
            }

            return rolls.Select(r => new Drop(r.Key, AtLeastOne(r.Value)))
                .OrderByDescending(d => d.Percent).ThenBy(d => d.Item)
                .ToList();
        }

        /// <summary>
        /// The achievements beating this group counts towards and the log does not hold yet: those
        /// that count one of its monsters (EM, Ef) and those of the challenges its monsters impose
        /// (EH: a dungeon boss's «Duo», «Zombie»...). The ones of another game type (SC) are left out.
        /// </summary>
        public static List<Unlock> Unlocks(IReadOnlyList<Member> group, AchievementCatalogue? book, AchievementLog? log)
        {
            var found = new List<Unlock>();
            if (book == null || !book.Ready) return found;

            var seen = new HashSet<int>();
            void Consider(int achievementId, int monster)
            {
                if (!seen.Add(achievementId) || log?.Has(achievementId) == true) return;
                var achievement = book.Of(achievementId);
                if (achievement == null || !OfThisServer(achievement)) return;
                found.Add(new Unlock(achievementId, achievement.Points, monster));
            }

            // The boss first: its achievements are the ones a player looks for on the tooltip.
            foreach (int monster in group.Select(m => m.Monster).Where(m => m > 0).Distinct()
                         .OrderByDescending(m => DungeonManager.IsBoss(m)))
            {
                foreach (var challenge in Challenges.CarriedBy(monster))
                    foreach (int id in book.WaitingOn("EH", challenge.Id)) Consider(id, monster);
                foreach (int id in book.WaitingOn("EM", monster)) Consider(id, monster);
                foreach (int id in book.WaitingOn("Ef", monster)) Consider(id, monster);
            }

            return found.Take(MostUnlocks).ToList();
        }

        /// <summary>Whether nothing in it asks for another game type than this server's (SC).</summary>
        private static bool OfThisServer(Achievement achievement)
            => achievement.Objectives.All(o => o.Terms.All(t => t.Op != "SC" || t.Key == Achievements.ServerGameType));
    }
}

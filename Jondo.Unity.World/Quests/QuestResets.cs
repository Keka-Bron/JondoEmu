using System;

namespace Jondo.Unity.World.Quests
{
    /// <summary>
    /// When the repeatable quests come back: every day at 7:00, every Tuesday at 7:00, every
    /// first of January at 7:00 (Dofus pour les noobs: «Quête répétable tous les jours à partir de
    /// 7h», «Quête répétable (hebdomadaire mardi 7h)»). In the server's own time, as Ankama's.
    /// </summary>
    public static class QuestResets
    {
        public const int Hour = 7;

        /// <summary>The last daily turn at or before <paramref name="now"/>.</summary>
        public static DateTime Daily(DateTime now)
        {
            var today = now.Date.AddHours(Hour);
            return now >= today ? today : today.AddDays(-1);
        }

        /// <summary>The last Tuesday 7:00 at or before <paramref name="now"/>.</summary>
        public static DateTime Weekly(DateTime now)
        {
            var day = Daily(now);
            int back = ((int)day.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7;
            return day.AddDays(-back);
        }

        /// <summary>The last first of January 7:00 at or before <paramref name="now"/>.</summary>
        public static DateTime Yearly(DateTime now)
        {
            var year = new DateTime(now.Year, 1, 1, Hour, 0, 0, now.Kind);
            return now >= year ? year : year.AddYears(-1);
        }
    }
}

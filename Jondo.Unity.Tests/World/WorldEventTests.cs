using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Osavora's weekly quests: the season as a world event («WE»), the days since a marker was
    /// received («DD»), and the date an item carries (805).
    /// </summary>
    public class WorldEventTests
    {
        /// <summary>«Una pequeña comezón» (2529), the Repos' branch and the others.</summary>
        private const string Itch =
            "PL>179&Qf=2515&(((WE=230|WE=255)&(DD>7,31582|PO!31582))|((WE=229|WE=254)&(DD>7,31581|PO!31581))|((WE=228|WE=252)&(DD>7,31580|PO!31580)))";

        private sealed class Player : IQuestFacts
        {
            public int Level { get; init; } = 200;
            public long MapId => 0;
            public bool Finished(int questId) => questId == 2515;
            public bool Active(int questId) => false;
            public bool ObjectiveDone(int objectiveId) => false;
            public int Event { get; init; }
            public Dictionary<int, long> Days { get; } = new();
            public Dictionary<int, long> Hours { get; } = new();

            public long? Count(string op, long key, string flag) => op switch
            {
                "WE" => key == Event ? 1 : 0,
                "PO" => Days.ContainsKey((int)key) || Hours.ContainsKey((int)key) ? 1 : 0,
                "DD" => Days.TryGetValue((int)key, out long days) ? days : 0,
                "DH" => Hours.TryGetValue((int)key, out long hours) ? hours : 0,
                _ => null,
            };
        }

        /// <summary>«PL&gt;99&amp;Qf=636&amp;(DH&gt;12,32105|PO!32105)»: the hours first, the item second, as DD.</summary>
        [Fact]
        public void The_hours_since_an_item_count_like_its_days()
        {
            const string Again = "PL>99&Qf=2515&(DH>12,32105|PO!32105)";
            Assert.True(QuestCriterion.Judge(Again, new Player()).Met);
            var soon = new Player();
            soon.Hours[32105] = 5;
            Assert.False(QuestCriterion.Judge(Again, soon).Met);
            var later = new Player();
            later.Hours[32105] = 13;
            Assert.True(QuestCriterion.Judge(Again, later).Met);
            Assert.True(QuestCriterion.Judge(Again, later).FullyJudged);
        }

        [Fact]
        public void One_season_is_on_each_week()
        {
            // The first Naissance: Monday 21 July 2025, 8:00 in Paris (6:00 UTC).
            var naissance = new DateTimeOffset(2025, 7, 21, 9, 0, 0, TimeSpan.Zero);
            Assert.Equal(new[] { WorldEvents.Naissance }, WorldEvents.Active(naissance));
            Assert.True(WorldEvents.IsActive(WorldEvents.Chasse, naissance.AddDays(7)));
            Assert.True(WorldEvents.IsActive(WorldEvents.Repos, naissance.AddDays(14)));
            Assert.False(WorldEvents.IsActive(255, naissance.AddDays(14)));   // «perturbé»: never here
        }

        [Fact]
        public void A_weekly_quest_waits_a_week_from_its_marker()
        {
            Assert.True(QuestCriterion.Judge(Itch, new Player { Event = WorldEvents.Repos }).Met);   // no marker yet

            var done = new Player { Event = WorldEvents.Repos };
            done.Days[31582] = 3;
            Assert.False(QuestCriterion.Judge(Itch, done).Met);   // done three days ago

            var week = new Player { Event = WorldEvents.Repos };
            week.Days[31582] = 8;
            Assert.True(QuestCriterion.Judge(Itch, week).Met);

            var other = new Player { Event = WorldEvents.Chasse };
            other.Days[31582] = 3;
            Assert.True(QuestCriterion.Judge(Itch, other).Met);   // the Repos' marker says nothing of the Chasse

            Assert.False(QuestCriterion.Judge(Itch, new Player { Event = 0 }).Met);
            Assert.True(QuestCriterion.Judge(Itch, new Player { Event = WorldEvents.Repos }).FullyJudged);
        }

        /// <summary>The capture's item received at 00:15 on 13 August 2026 in Paris: { f1: 13, f2: 656, f3: 8, f4: 15 }.</summary>
        [Fact]
        public void A_received_date_travels_as_the_capture_has_it()
        {
            long seconds = new DateTimeOffset(2026, 8, 12, 22, 15, 10, TimeSpan.Zero).ToUnixTimeSeconds();
            var date = ItemEffectMessages.Date(seconds);
            Assert.Equal(13, date.Day);
            Assert.Equal(656, date.Year);
            Assert.Equal(8, date.Month);
            Assert.Equal(15, date.Minute);
            Assert.Equal(0, date.Hour);
        }

        [Fact]
        public void The_days_since_a_marker_count_from_its_date()
        {
            var session = GameSession.SinSocket();
            var now = DateTimeOffset.UtcNow;
            var marker = new Equipment.Item { Uid = 9_400_400_001, Template = 31582, Position = Equipment.Bag };
            marker.Effects.Add(new Equipment.ItemEffect(Equipment.ReceivedOn, now.AddDays(-9).ToUnixTimeSeconds(), 0, 0));
            session.State.EquipmentItems[marker.Uid] = marker;
            using (SessionContext.Push(session))
            {
                Assert.Equal(9, Equipment.DaysSinceReceived(31582, now));
                Assert.Equal(0, Equipment.DaysSinceReceived(31581, now));
            }
        }
    }
}

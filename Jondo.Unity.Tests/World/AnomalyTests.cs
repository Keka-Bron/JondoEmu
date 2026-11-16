using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The anomalies' loot and missions: the fragments every monster may drop in an anomaly's zone
    /// to whoever carries the minor uchronic elixir's state, and the guild missions they count for.
    /// </summary>
    public class AnomalyTests
    {
        private const string FragmentCriterion = "(HA=50|HS=3383)&Az=1&Pm!28049666";

        [Fact]
        public void A_fragment_falls_in_an_anomaly_to_whoever_carries_the_elixir()
        {
            if (Anomalies.All.Count == 0) return;   // no anomalies data
            var anomaly = Anomalies.All[0];
            // A map of the anomaly's zone: its vestige's may stand in another.
            long zoneMap = DatabaseManager.MapsOfSubArea(anomaly.SubAreaId).DefaultIfEmpty(0).Min();
            if (zoneMap == 0) return;
            var session = GameSession.SinSocket();
            session.State.CharacterId = 9_400_000_001;
            session.State.MapId = zoneMap;
            using (SessionContext.Push(session))
            {
                var fight = new FightInstance(9_400_001, zoneMap);
                var me = new Fighter { Id = session.State.CharacterId, MaxHP = 100, CurrentHP = 100 };
                fight.AddPlayer(me);

                Assert.False(FightHandler.SeLoLleva(fight, FragmentCriterion));   // no elixir
                me.Buffs.PonerEstado(3383);
                Assert.True(FightHandler.SeLoLleva(fight, FragmentCriterion));

                var elsewhere = new FightInstance(9_400_002, 191105026);
                elsewhere.AddPlayer(me);
                Assert.False(FightHandler.SeLoLleva(elsewhere, FragmentCriterion));   // not an anomaly
                Assert.True(FightHandler.SeLoLleva(elsewhere, "Az=0"));
            }
        }

        /// <summary>
        /// An uchronic elixir is the side's, its strongest: a guardian's rows -- the temporal loops
        /// at 3 % minor, 5 % improved or major, 1 % without --, rolled for a character who drank
        /// nothing beside one who drank the major, go by the major alone.
        /// </summary>
        [Fact]
        public void The_strongest_elixir_of_the_side_counts_for_everyone_on_it()
        {
            var session = GameSession.SinSocket();
            session.State.CharacterId = 9_400_000_011;
            using (SessionContext.Push(session))
            {
                var fight = new FightInstance(9_400_011, 191105026);
                var me = new Fighter { Id = session.State.CharacterId, MaxHP = 100, CurrentHP = 100 };
                var ally = new Fighter { Id = 9_400_000_012, MaxHP = 100, CurrentHP = 100 };
                fight.AddPlayer(me);
                fight.AddPlayer(ally);
                ally.Buffs.PonerEstado(3383);
                ally.Buffs.PonerEstado(3385);

                Assert.Equal(3, AnomalyLoot.SideElixir(fight, me.Id));
                Assert.True(FightHandler.SeLoLleva(fight, "HA=51|HS=3384|HA=52|HS=3385"));   // 5 %
                Assert.False(FightHandler.SeLoLleva(fight, "HA=50|HS=3383"));                 // 3 %: not the strongest
                Assert.False(FightHandler.SeLoLleva(fight, "HA!50&HS!3383&HA!51&HS!3384&HA!52&HS!3385"));   // 1 %
                Assert.True(FightHandler.SeLoLleva(fight, "HA=52|HS=3385"));                  // the relic's 0.5 %
            }
        }

        /// <summary>The Almanax's «en todas las zonas influenciadas por una anomalía temporal» (its criterion 13).</summary>
        [Fact]
        public void The_almanax_knows_an_anomaly_s_zones()
        {
            if (Anomalies.All.Count == 0) return;
            var bonus = new Jondo.Unity.World.Almanax.AlmanaxBonus { Type = 1, Amount = 100, Criteria = new[] { (13, 1) } };
            Assert.True(Almanax.Fits(bonus, Almanax.Where.Anywhere with { Subarea = Anomalies.All[0].SubAreaId }));
            Assert.False(Almanax.Fits(bonus, Almanax.Where.Anywhere with { Subarea = -5 }));
        }

        [Fact]
        public void The_anomaly_missions_count_what_their_text_says()
        {
            var monsters = new GuildMissions.Objective(158, 158, 1) { SuperCategoryId = 3, MinLevel = 120, Quantity = 50 };
            var guardian = new GuildMissions.Objective(262, 262, 1) { SuperCategoryId = 3, MinLevel = 130 };
            var fragments = new GuildMissions.Objective(413, 412, 1) { SuperCategoryId = 3, ObjectId = 26870, Quantity = 5 };
            var loot = new Dictionary<int, int> { [26870] = 2 };

            Assert.Equal(4, GuildMissionWatcher.AnomalyWorth(monsters, 130, true, 4, false, loot));
            Assert.Equal(0, GuildMissionWatcher.AnomalyWorth(monsters, 110, true, 4, false, loot));   // too low a zone
            Assert.Equal(0, GuildMissionWatcher.AnomalyWorth(monsters, 130, false, 4, false, loot));  // no elixir
            Assert.Equal(1, GuildMissionWatcher.AnomalyWorth(guardian, 150, true, 1, true, loot));
            Assert.Equal(0, GuildMissionWatcher.AnomalyWorth(guardian, 150, true, 3, false, loot));
            Assert.Equal(2, GuildMissionWatcher.AnomalyWorth(fragments, 15, false, 3, false, loot));
            Assert.Equal(0, GuildMissionWatcher.AnomalyWorth(monsters with { SuperCategoryId = 4 }, 130, true, 4, false, loot));
        }
    }
}

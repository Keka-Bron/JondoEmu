using System;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Maps;
using Xunit;
using Group = Jondo.Unity.Server.Managers.MobSpawnManager.MobGroup;
using Member = Jondo.Unity.Server.Managers.MobSpawnManager.MobMember;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Monsters that jump on whoever comes too close, by the client's rule, as the Gigalodón's
    /// abyss uses it: in the dark its monsters aggress from ten cells, Willorque from fifty always.
    /// </summary>
    public class MonsterAggressionTests
    {
        private const int Madrepeora = 8324, Rokoncha = 8365, Willorque = 8252, Tofu = 494;
        private const int Here = 300;

        private static Group GroupOf(int cell, params (int Template, int Level)[] members) => new()
        {
            MobId = -cell,
            CellId = cell,
            Members = members.Select(m => new Member
            {
                Monster = new MobSpawnManager.MonsterData { Id = m.Template },
                Level = m.Level,
            }).ToList(),
        };

        /// <summary>A cell at exactly this distance from <see cref="Here"/>, by the client's geometry.</summary>
        private static int CellAt(int distance)
            => Enumerable.Range(0, 560).First(c => MapGeometry.Distance(Here, c) == distance);

        /// <summary>The first floor of a Gigalodón raid at this light, answering its criteria.</summary>
        private static Criterion.Resolver FirstFloorAt(int light)
        {
            var raid = new RaidInstance(1, Raids.Gigalodon, 1, 1, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
            raid.SetLight(1, light, DateTimeOffset.UtcNow);
            return raid.ResolverFor(Raids.Of(Raids.Gigalodon).Floors[0]);
        }

        [Fact]
        public void The_profiles_are_the_client_s()
        {
            var common = MonsterAggression.ProfileOf(Madrepeora);
            Assert.Equal((10, 0, 3000), (common.Zone, common.LevelDiff, common.DelayMs));
            Assert.Contains("n1_worldlight", common.Immunity);

            var willorque = MonsterAggression.ProfileOf(Willorque);
            Assert.Equal(50, willorque.Zone);
            Assert.Equal("", willorque.Immunity);

            Assert.False(MonsterAggression.ProfileOf(Tofu).Aggressive);
        }

        /// <summary>37 of the abyss's 73 maps let monsters aggress; the rest are its safe ones.</summary>
        [Fact]
        public void Only_some_of_the_abyss_maps_allow_aggression()
        {
            var maps = Raids.Of(Raids.Gigalodon).Floors.SelectMany(DatabaseManager.MapsOfSubArea).ToList();
            Assert.Equal(73, maps.Count);
            Assert.Equal(37, maps.Count(MonsterAggression.MapAllows));
        }

        /// <summary>A group's zone is its widest aggressor's, its level the highest; a peaceful leader makes no threat.</summary>
        [Fact]
        public void A_group_threatens_as_its_aggressors_do()
        {
            var threat = MonsterAggression.ThreatOf(GroupOf(Here, (Madrepeora, 200), (Rokoncha, 210)));
            Assert.Equal((10, 210), (threat.Zone, threat.Level));
            Assert.Equal(3000, threat.DelayMs);

            Assert.Null(MonsterAggression.ThreatOf(GroupOf(Here, (Tofu, 50), (Madrepeora, 200))));
        }

        /// <summary>In the dark the common monsters jump from ten cells; with light they do not.</summary>
        [Fact]
        public void In_the_dark_the_abyss_s_monsters_jump()
        {
            var near = GroupOf(CellAt(8), (Madrepeora, 200));
            var far = GroupOf(CellAt(12), (Madrepeora, 200));

            Assert.Same(near, MonsterAggression.Nearest(new[] { far, near }, FirstFloorAt(0), 200, Here)?.Group);
            Assert.Null(MonsterAggression.Nearest(new[] { far }, FirstFloorAt(0), 200, Here));
            Assert.Null(MonsterAggression.Nearest(new[] { near }, FirstFloorAt(1), 200, Here));
        }

        /// <summary>Willorque has no immunity and fifty cells: it always jumps.</summary>
        [Fact]
        public void Willorque_always_jumps()
        {
            var willorque = GroupOf(CellAt(15), (Willorque, 2400));
            Assert.NotNull(MonsterAggression.Nearest(new[] { willorque }, FirstFloorAt(4), 200, Here));
            Assert.Null(MonsterAggression.Nearest(new[] { GroupOf(CellAt(15), (Madrepeora, 200)) }, FirstFloorAt(0), 200, Here));
        }

        /// <summary>A player above the group's aggression level is left alone; levels count up to 200.</summary>
        [Fact]
        public void Stronger_players_are_left_alone()
        {
            var weak = GroupOf(CellAt(3), (Madrepeora, 150));
            Assert.Null(MonsterAggression.Nearest(new[] { weak }, FirstFloorAt(0), 151, Here));
            Assert.NotNull(MonsterAggression.Nearest(new[] { weak }, FirstFloorAt(0), 150, Here));

            var full = GroupOf(CellAt(3), (Madrepeora, 200));
            Assert.NotNull(MonsterAggression.Nearest(new[] { full }, FirstFloorAt(0), 250, Here));
        }
    }
}

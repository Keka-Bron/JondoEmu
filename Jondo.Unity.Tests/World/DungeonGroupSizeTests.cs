using System;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// A dungeon room's group, against the jalatós capture: eight in a fixed order, of which a team
    /// fights the first clamp(players, 4, 8), the rooms climbing the grades, the boss leading his.
    /// </summary>
    /// <remarks>
    /// <c>Mazmorras/mazmorra de los jalatós completa</c>: a Sacrier alone through the five rooms of
    /// dungeon 1. Each jss has one group of eight and its alternatives by team size; the Sacrier
    /// fought four in every room.
    /// </remarks>
    [Collection("MapManager")]
    public class DungeonGroupSizeTests
    {
        /// <summary>Dungeon 1's own monsters, from Subareas.Monsters of subarea 82, the Jalató Real (147) out.</summary>
        private static readonly int[] Jalatos = { 101, 134, 148, 149, 4822 };
        private const int JalatoReal = 147;

        /// <summary>Four for one to four players, then one each, eight at most -- and all of a group that is not a room's.</summary>
        [Theory]
        [InlineData(1, 4)]
        [InlineData(2, 4)]
        [InlineData(4, 4)]
        [InlineData(5, 5)]
        [InlineData(7, 7)]
        [InlineData(8, 8)]
        [InlineData(12, 8)]
        public void A_room_fights_the_first_of_its_eight(int players, int monsters)
        {
            MobSpawnManager.EnsureMonsterData();
            var room = new MobSpawnManager.MobGroup
            {
                Modular = true,
                Members = MobSpawnManager.ComposeRoom(Jalatos, Array.Empty<int>(), 0, 5, 121373185),
            };

            var fighting = MobSpawnManager.MembersFor(room, players);

            Assert.Equal(monsters, fighting.Count);
            Assert.Equal(room.Members.Take(monsters), fighting);

            var wild = new MobSpawnManager.MobGroup { Members = room.Members.Take(2).ToList() };
            Assert.Equal(2, MobSpawnManager.MembersFor(wild, players).Count);
        }

        /// <summary>Eight, the first four all different, every one at the room's grade.</summary>
        [Fact]
        public void A_room_is_eight_and_its_first_four_differ()
        {
            MobSpawnManager.EnsureMonsterData();

            for (int index = 0; index < 5; index++)
            {
                var members = MobSpawnManager.ComposeRoom(Jalatos, Array.Empty<int>(), index, 5, 121373185 + index);

                Assert.Equal(MobSpawnManager.DungeonGroupSize, members.Count);
                Assert.Equal(4, members.Take(4).Select(m => m.Monster.Id).Distinct().Count());
                Assert.All(members, m => Assert.Contains(m.Monster.Id, Jalatos));
                Assert.All(members, m => Assert.Equal(index, m.GradeIndex));
            }
        }

        /// <summary>The boss leads his room, once, at his top grade, with seven of the dungeon's own.</summary>
        [Fact]
        public void The_boss_leads_his_room_once()
        {
            MobSpawnManager.EnsureMonsterData();

            var members = MobSpawnManager.ComposeRoom(Jalatos, new[] { JalatoReal }, 4, 5, 121374211);

            Assert.Equal(8, members.Count);
            Assert.Equal(JalatoReal, members[0].Monster.Id);
            Assert.Single(members, m => m.Monster.Id == JalatoReal);
            Assert.Equal(4, members[0].GradeIndex);
            Assert.All(members.Skip(1), m => Assert.Contains(m.Monster.Id, Jalatos));
        }

        /// <summary>The same room composed twice is the same room: the map seeds it.</summary>
        [Fact]
        public void A_room_is_the_same_every_time()
        {
            MobSpawnManager.EnsureMonsterData();

            var once = MobSpawnManager.ComposeRoom(Jalatos, Array.Empty<int>(), 2, 5, 121375233);
            var twice = MobSpawnManager.ComposeRoom(Jalatos, Array.Empty<int>(), 2, 5, 121375233);

            Assert.Equal(once.Select(m => (m.Monster.Id, m.GradeIndex)), twice.Select(m => (m.Monster.Id, m.GradeIndex)));
        }

        /// <summary>Rooms 1 to 5 at grades 1 to 5, as measured; a lone room at the top; longer dungeons spread.</summary>
        [Theory]
        [InlineData(0, 5, 0)]
        [InlineData(2, 5, 2)]
        [InlineData(4, 5, 4)]
        [InlineData(0, 1, 4)]
        [InlineData(0, 10, 0)]
        [InlineData(9, 10, 4)]
        [InlineData(1, 3, 2)]
        public void The_rooms_climb_the_grades(int room, int rooms, int grade)
            => Assert.Equal(grade, MobSpawnManager.RoomGrade(room, rooms));

        /// <summary>
        /// The alternatives of the first room of the capture, byte for byte: the first four for
        /// one player, then five to eight, each member with its id, level and grade and no look.
        /// </summary>
        [Fact]
        public void The_room_shows_its_alternatives_as_the_capture()
        {
            MobSpawnManager.EnsureMonsterData();
            var group = MobSpawnManager.ComposeOffMap(new[] { 149, 134, 101, 148, 148, 101, 134, 149 }.Select(m => (m, 0)))!;

            var creatures = Pb.New();
            ConnectionProtocol.AddAlternatives(creatures, group.Members);

            Assert.Equal(
                "1a250a07089501101620010a07088601101620010a060865101620010a070894011016200110011a2e0a0708950110" +
                "1620010a07088601101620010a060865101620010a07089401101620010a070894011016200110051a360a070895" +
                "01101620010a07088601101620010a060865101620010a07089401101620010a07089401101620010a0608651016" +
                "200110061a3f0a07089501101620010a07088601101620010a060865101620010a07089401101620010a07089401" +
                "101620010a060865101620010a070886011016200110071a480a07089501101620010a07088601101620010a0608" +
                "65101620010a07089401101620010a07089401101620010a060865101620010a07088601101620010a0708950110" +
                "1620011008",
                Convert.ToHexString(creatures.Build()).ToLowerInvariant());
        }
    }
}

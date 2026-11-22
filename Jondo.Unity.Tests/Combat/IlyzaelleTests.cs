using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Ilyzaelle (4967) keeps level 220 on every grade and only her life changes, 42 000 to
    /// 60 000; her guide ties it to the party, «4 personnage ou moins : 42 000 PV» up to
    /// «8 personnages : 60 000 PV». She came at her top grade every time, 60 000 for a
    /// player alone. Now her grade follows the party.
    /// </summary>
    public class IlyzaelleTests
    {
        private const int Ilyzaelle = 4967, Roster = 4968;

        [Theory]
        [InlineData(1, 0)]
        [InlineData(4, 0)]
        [InlineData(5, 1)]
        [InlineData(6, 2)]
        [InlineData(7, 3)]
        [InlineData(8, 4)]
        public void Her_grade_follows_the_party(int fighters, int grade)
        {
            if (MobSpawnManager.GetMonsterData(Ilyzaelle) == null) MobSpawnManager.InitializeAndSpawnAll();
            if (MobSpawnManager.GetMonsterData(Ilyzaelle) == null) return;   // no world data
            MonsterPartyGrades.Reload();

            var members = MobSpawnManager.ComposeMembers(new[] { (Ilyzaelle, 5) }
                .Concat(Enumerable.Repeat((Roster, 2), 7)));
            var group = new MobSpawnManager.MobGroup { Modular = true, Leaders = 1, Members = members };

            var fought = MobSpawnManager.MembersFor(group, fighters);
            Assert.Equal(grade, fought[0].GradeIndex);
            Assert.Equal(5, group.Members[0].GradeIndex);   // the group on the map is left as it is
            Assert.All(fought.Skip(1), m => Assert.Equal(2, m.GradeIndex));
        }

        [Fact]
        public void The_file_lists_her()
        {
            Assert.True(System.IO.File.Exists(Paths.ContentFile(MonsterPartyGrades.File)));
            MonsterPartyGrades.Reload();
            Assert.Equal(0, MonsterPartyGrades.GradeFor(Ilyzaelle, 1, 6));
            Assert.Equal(4, MonsterPartyGrades.GradeFor(Ilyzaelle, 8, 6));
            Assert.Null(MonsterPartyGrades.GradeFor(Roster, 8, 5));
            var rows = MonsterPartyGrades.Parse("{\"grades\":[{\"monster\":4967,\"from\":4},{\"monster\":5,\"from\":0},{\"from\":2}]}");
            Assert.Equal(new[] { (4967, 4) }, rows);
        }
    }
}

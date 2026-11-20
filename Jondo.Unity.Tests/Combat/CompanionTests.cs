using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The companions (Managers.Companions, FightCompanions): their characteristics at their
    /// master's level from the client's ranges, their spells' grades, and the fighter that comes
    /// into his fight -- on his side, his to play, told as a companion.
    /// </summary>
    public class CompanionTests
    {
        /// <summary>Lumino as datos/companions_3.6.10.11.json has it, cut down.</summary>
        private const string Lumino = """
            [{"id":1,"nameId":346959,"look":"{7531|||110}","startingSpellLevelId":0,
              "characteristics":[{"carac":0,"ranges":[[1.0,55.0],[200.0,5.0]]},
                                 {"carac":1,"ranges":[[1.0,6.0],[200.0,0.0302]]},
                                 {"carac":23,"ranges":[[1.0,3.0],[180.0,0.0168]]},
                                 {"carac":11,"ranges":[[200.0,14.825]]},
                                 {"carac":37,"ranges":[[1.0,5.0],[200.0,0.0754]]}],
              "spells":[{"spell":4208,"grades":[[1,30],[2,100],[3,160]]}]}]
            """;

        [Fact]
        public void A_characteristic_is_its_base_and_what_each_level_adds()
        {
            var lumino = Companions.Read(Lumino)[1];
            int At(int carac, int level) => Companions.ValueAt(lumino.Characteristics.First(c => c.Characteristic == carac).Ranges, level);

            Assert.Equal(55, At(0, 1));
            Assert.Equal(55 + 5 * 49, At(0, 50));
            Assert.Equal(55 + 5 * 199, At(0, 200));
            // Past its last range it adds no more.
            Assert.Equal(At(0, 200), At(0, 230));
            Assert.Equal(6, At(1, 1));
            Assert.Equal(12, At(1, 200));
            // The MP stop growing at 180.
            Assert.Equal(6, At(23, 180));
            Assert.Equal(6, At(23, 200));
            // A range alone is what a level adds from level 1.
            Assert.Equal(0, At(11, 1));
            Assert.Equal(726, At(11, 50));
        }

        [Fact]
        public void A_spell_is_at_the_highest_grade_its_level_reaches()
        {
            var grades = Companions.Read(Lumino)[1].Spells[0].Grades;
            Assert.Equal(1, Companions.GradeAt(grades, 1));
            Assert.Equal(1, Companions.GradeAt(grades, 99));
            Assert.Equal(2, Companions.GradeAt(grades, 100));
            Assert.Equal(3, Companions.GradeAt(grades, 200));
        }

        [Fact]
        public void The_companion_fights_on_its_masters_side_and_is_his_to_play()
        {
            var lumino = Companions.Read(Lumino)[1];
            var master = new Fighter { Id = 10, Name = "Master", Level = 50, TeamId = FightInstance.Azules };

            var fighter = FightHandler.BuildCompanionFighter(lumino, master, -1);

            Assert.True(fighter.IsCompanion);
            Assert.Equal(1, fighter.CompanionId);
            Assert.Equal(FightInstance.Azules, fighter.TeamId);
            Assert.Equal(50, fighter.Level);
            Assert.Equal(300 + 726, fighter.MaxHP);
            Assert.Equal(fighter.MaxHP, fighter.CurrentHP);
            Assert.Equal(7, fighter.MaxAP);
            Assert.Equal(3, fighter.MaxMP);
            Assert.Equal(8, fighter.NeutralResPct);
            Assert.True(fighter.IsPlayedAlly);
            Assert.Equal(master.Id, fighter.DrivenBy);
            Assert.True(fighter.ControlledBy(master.Id));
            Assert.Equal(new[] { (4208, 1) }, fighter.HechizosDeInvocado);
            Assert.Equal(7531, fighter.LookBoneId);
        }

        [Fact]
        public void The_item_says_which_companion_it_is()
        {
            var effects = new[] { new Equipment.ItemEffect(1161, 1, 0, 0), new Equipment.ItemEffect(983, 60, 0, 0) };
            Assert.Equal(1, Companions.IdOf(effects));
            Assert.Equal(0, Companions.IdOf(new[] { new Equipment.ItemEffect(983, 60, 0, 0) }));
        }

        [Fact]
        public void A_companions_end_screen_row_is_the_fifth_case_with_its_level()
        {
            var jyg = FightProtocol.BuildFightResults(new[]
            {
                new FightProtocol.FightResult { Fighter = -1, Winner = true, Level = 50, Companion = true },
            }, 1000);
            var row = ProtoMessage.Parse(ProtoMessage.Parse(jyg).Fields.First(f => f.FieldNumber == 2).BytesValue);
            var who = ProtoMessage.Parse(row.Fields.First(f => f.FieldNumber == 3).BytesValue);
            var companion = ProtoMessage.Parse(who.Fields.Single(f => f.FieldNumber == 5).BytesValue);
            Assert.Equal(50, companion.Fields.Single(f => f.FieldNumber == 1).VarIntValue);
            Assert.DoesNotContain(who.Fields, f => f.FieldNumber == 2);
        }

        [Fact]
        public void A_companion_is_told_in_the_fighters_own_case()
        {
            var identity = FightProtocol.CompanionIdentity(10, "Master", 1, 50);
            var block = FightProtocol.FighterBlock(245, 1, -1,
                System.Array.Empty<(int, long, long)>(), System.Array.Empty<byte>(), identity,
                isMonster: true, team: FightInstance.Azules, companion: true).Build();
            // { f2 { f2 the fighter, f3 the look } }: the fighter's own fields.
            var body = ProtoMessage.Parse(ProtoMessage.Parse(block).Fields.First(f => f.FieldNumber == 2).BytesValue);
            var fighter = ProtoMessage.Parse(body.Fields.First(f => f.FieldNumber == 2).BytesValue);
            Assert.Contains(fighter.Fields, f => f.FieldNumber == 8);
            Assert.DoesNotContain(fighter.Fields, f => f.FieldNumber == 3);
        }
    }
}

using System.Linq;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Rows armed off two grades of one spell are two hooks, each read off its own grade.
    /// Dazahk's behaviour 10346 arms its grade-1 turn-end row and, at his turn start, casts
    /// grade 3, which arms rows of its own: folded into one hook turned to grade 3, the
    /// turn-end row that counts his Nimpatience was lost.
    /// </summary>
    public class ArmedHookGradeTests
    {
        [Fact]
        public void Rows_of_another_grade_keep_the_first_grade_hook()
        {
            var boss = new Fighter { Id = -1, TeamId = 1, IsMonster = true, MonsterId = 5319 };
            boss.Buffs.ArmarFilas(10346, 1, -1, boss.Id, 1, new[] { 193618, 193628 });
            boss.Buffs.ArmarFilas(10346, 3, 3, boss.Id, 1, new[] { 193632, 193633 });

            var first = Assert.Single(boss.Buffs.ActiveSpells, e => e.Hechizo == 10346 && e.Grado == 1);
            var third = Assert.Single(boss.Buffs.ActiveSpells, e => e.Hechizo == 10346 && e.Grado == 3);
            Assert.Equal(new[] { 193618, 193628 }, first.Filas.OrderBy(f => f));
            Assert.Equal(-1, first.CaducaEnRonda);
            Assert.Equal(new[] { 193632, 193633 }, third.Filas.OrderBy(f => f));
        }

        [Fact]
        public void Rows_of_the_same_grade_join_its_hook()
        {
            var boss = new Fighter { Id = -1, TeamId = 1, IsMonster = true, MonsterId = 5319 };
            boss.Buffs.ArmarFilas(10346, 1, 2, boss.Id, 1, new[] { 193618 });
            boss.Buffs.ArmarFilas(10346, 1, -1, boss.Id, 1, new[] { 193628 });

            var hook = Assert.Single(boss.Buffs.ActiveSpells, e => e.Hechizo == 10346);
            Assert.Equal(new[] { 193618, 193628 }, hook.Filas.OrderBy(f => f));
            Assert.Equal(-1, hook.CaducaEnRonda);
        }
    }
}

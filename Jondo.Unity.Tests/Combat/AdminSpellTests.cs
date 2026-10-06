using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Doom de Masas, the administration spell used to skip a fight.
    /// </summary>
    /// <remarks>
    /// It is not invented: it is in the client's own catalogue with the name «Doom de Masas» and the
    /// adminName «Doom de masse». A single grade, 1 AP, range 0, and two effects:
    ///
    ///   141  «Mata al objetivo»   mask «A» —the opponents—  zone 65, which is the whole map
    ///   120  gives back AP        mask «C» —the caster—
    ///
    /// Both masks matter: «A» is what keeps you from killing yourself, and 120 is
    /// what lets you chain it without running out of points.
    /// </remarks>
    public class AdminSpellTests
    {
        [Fact]
        public void El_hechizo_existe_en_los_datos_del_cliente()
        {
            // One AP and a single grade, just as it is in SpellLevels.
            var (grado, nivelId, coste) = SpellEffects.GradoDe(AdminSpells.DoomDeMasas, 200);

            Assert.Equal(AdminSpells.GradoDeDoom, grado);
            Assert.Equal(20557, nivelId);
            Assert.Equal(1, coste);
        }

        [Fact]
        public void Mata_a_los_de_enfrente_y_a_nadie_mas()
        {
            var efectos = SpellEffects.De(AdminSpells.DoomDeMasas, AdminSpells.GradoDeDoom);
            Assert.NotEmpty(efectos);

            var mata = efectos.FirstOrDefault(e => e.EffectId == 141);
            Assert.NotNull(mata);

            // «A» are the opponents and «a» one's own side. If this turned into «a»,
            // or into «a,A», the administrator would strike himself down on pressing.
            Assert.Equal("A", mata!.TargetMask);

            // And the zone is the whole map, which is what makes the zero range not matter.
            Assert.Equal(Jondo.Unity.World.Maps.Zone.WholeMap, mata.Forma);
        }

        [Fact]
        public void Solo_se_declara_a_partir_de_administrador()
        {
            Assert.Equal(Roles.Administrador, AdminSpells.HaceFalta);

            // And an account that does not exist does not have it: Para() cannot default to true.
            Assert.False(AdminSpells.Para(0));
            Assert.False(AdminSpells.Para(-1));
        }
    }
}

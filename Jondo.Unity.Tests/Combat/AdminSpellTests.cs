using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Doom de Masas, el hechizo de administración con el que se salta una pelea.
    /// </summary>
    /// <remarks>
    /// No es inventado: está en el catálogo del propio cliente con el nombre «Doom de Masas» y el
    /// adminName «Doom de masse». Un solo grado, 1 PA, alcance 0, y dos efectos:
    ///
    ///   141  «Mata al objetivo»   máscara «A» —los de enfrente—  zona 65, que es todo el mapa
    ///   120  devuelve PA          máscara «C» —quien lanza—
    ///
    /// Las dos máscaras importan: la «A» es la que hace que no te mates a ti mismo, y el 120 es
    /// lo que deja encadenarlo sin quedarse sin puntos.
    /// </remarks>
    public class AdminSpellTests
    {
        [Fact]
        public void El_hechizo_existe_en_los_datos_del_cliente()
        {
            // Un PA y un solo grado, tal cual está en SpellLevels.
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

            // «A» son los de enfrente y «a» los del propio bando. Si esto se convirtiera en «a»,
            // o en «a,A», el administrador se fulminaría a sí mismo al pulsar.
            Assert.Equal("A", mata!.TargetMask);

            // Y la zona es todo el mapa, que es lo que hace que el alcance cero no importe.
            Assert.Equal(Jondo.Unity.World.Maps.Zone.WholeMap, mata.Forma);
        }

        [Fact]
        public void Solo_se_declara_a_partir_de_administrador()
        {
            Assert.Equal(Roles.Administrador, AdminSpells.HaceFalta);

            // Y una cuenta que no existe no lo tiene: Para() no puede dar verdadero por defecto.
            Assert.False(AdminSpells.Para(0));
            Assert.False(AdminSpells.Para(-1));
        }
    }
}

using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The shield: effects 1020 and 1039, 401 spells between the two.
    /// </summary>
    /// <remarks>
    /// 1020 gives a percentage of the caster's LEVEL and 1039 one of his LIFE. The
    /// percentage goes in the die, not in the value: Caparazón carries diceNum 150 and Soldagüino 200;
    /// Bendición Maravillosa 10 and Coraza de Dopeul 20. The value is zero in the six read.
    ///
    /// What these tests pin is that the shield is NOT life: it is not healed, it does not count for
    /// death and it drops on its own. Putting it in CurrentHP would have been shorter and would have left a
    /// shielded character healing up to the shield's cap.
    /// </remarks>
    public class ShieldTests
    {
        private static Fighter Uno(int vida = 1000) => new Fighter
        {
            Id = 1, Level = 200, MaxHP = vida, CurrentHP = vida,
        };

        [Fact]
        public void El_escudo_se_come_el_golpe_antes_que_la_vida()
        {
            var quien = Uno();
            quien.Escudar(300, caducaEnRonda: 3);

            // A hit smaller than the shield does not touch life.
            Assert.Equal(0, quien.PasarPorElEscudo(200));
            Assert.Equal(100, quien.PuntosDeEscudo);

            // And a bigger one lets through only what is left over.
            Assert.Equal(150, quien.PasarPorElEscudo(250));
            Assert.Equal(0, quien.PuntosDeEscudo);
        }

        [Fact]
        public void Sin_escudo_el_golpe_pasa_entero()
        {
            var quien = Uno();
            Assert.Equal(500, quien.PasarPorElEscudo(500));
        }

        [Fact]
        public void Se_suma_y_se_queda_la_caducidad_mas_lejana()
        {
            var quien = Uno();
            quien.Escudar(100, caducaEnRonda: 3);
            quien.Escudar(200, caducaEnRonda: 6);

            Assert.Equal(300, quien.PuntosDeEscudo);

            // In round 3 it still holds, because the second lasts until round 6.
            quien.CaducarElEscudo(3);
            Assert.Equal(300, quien.PuntosDeEscudo);

            quien.CaducarElEscudo(6);
            Assert.Equal(0, quien.PuntosDeEscudo);
        }

        [Fact]
        public void El_escudo_no_es_vida()
        {
            // It neither counts for death nor is healed: they are two different sacks.
            var quien = Uno(vida: 100);
            quien.Escudar(500, caducaEnRonda: 9);

            Assert.Equal(100, quien.CurrentHP);
            Assert.Equal(100, quien.MaxHP);
            Assert.True(quien.IsAlive);

            quien.TakeDamage(quien.PasarPorElEscudo(600));
            Assert.False(quien.IsAlive);
        }
    }
}

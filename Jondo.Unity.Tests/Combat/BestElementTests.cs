using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The «best element»: 2822 and the catalogue's element 5.
    /// </summary>
    /// <remarks>
    /// Twenty class spells hit «in the caster's best element» — Llamilla, Bilbipo,
    /// Apetito de Cocobur —. It is not an element: it is a question to the caster, and it is answered with
    /// the buffs on, because a spell that raises your agility mid-fight can
    /// change the answer, and that is precisely what it is cast for.
    /// </remarks>
    public class BestElementTests
    {
        private static Fighter Con(int fuerza, int inteligencia, int suerte, int agilidad)
            => new Fighter
            {
                Id = 1, MaxHP = 100, CurrentHP = 100,
                Strength = fuerza, Intelligence = inteligencia,
                Chance = suerte, Agility = agilidad,
            };

        [Theory]
        [InlineData(500, 100, 100, 100, 1)]   // tierra
        [InlineData(100, 500, 100, 100, 2)]   // fuego
        [InlineData(100, 100, 500, 100, 3)]   // agua
        [InlineData(100, 100, 100, 500, 4)]   // aire
        public void Gana_la_caracteristica_mas_alta(int fu, int inte, int su, int ag, int esperado)
        {
            Assert.Equal(esperado, EffectEngine.MejorElementoDe(Con(fu, inte, su, ag), ronda: 1));
        }

        [Fact]
        public void El_empate_se_rompe_siempre_igual()
        {
            // Which one the real game picks with two equal characteristics is not measured, but ONE
            // stable criterion is needed: two identical casts have to give the same.
            var quien = Con(300, 300, 300, 300);

            int primero = EffectEngine.MejorElementoDe(quien, ronda: 1);
            Assert.Equal(primero, EffectEngine.MejorElementoDe(quien, ronda: 1));
            Assert.Equal(1, primero);
        }

        [Fact]
        public void El_2822_cuenta_como_dano()
        {
            // If it did not count, its twenty spells would not hit and would give no error.
            Assert.True(EffectEngine.EsDeDano(2822));
            Assert.True(EffectEngine.EsDeDano(99));
            Assert.False(EffectEngine.EsDeDano(141));
        }
    }
}

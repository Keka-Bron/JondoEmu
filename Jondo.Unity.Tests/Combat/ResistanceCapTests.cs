using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A character takes blows with 50 % resistance at most, the client's own
    /// DofusConstants.MaxResistCharacter; whatever else fights keeps all of its own.
    /// </summary>
    public class ResistanceCapTests
    {
        private static Fighter Character(int resistance) => new()
        {
            Id = 1, Breed = 8, Level = 200, MaxHP = 4000, CurrentHP = 4000,
            EarthResPct = resistance, FireResPct = resistance, WaterResPct = resistance,
            AirResPct = resistance, NeutralResPct = resistance,
        };

        [Theory]
        [InlineData(ElementType.Earth)]
        [InlineData(ElementType.Fire)]
        [InlineData(ElementType.Water)]
        [InlineData(ElementType.Air)]
        [InlineData(ElementType.Neutral)]
        public void A_character_reads_fifty_at_most(ElementType element)
        {
            Assert.Equal(Fighter.MaxResistCharacter, FightHandler.ResistenciaDeLaVictima(Character(64), element, 1));
            Assert.Equal(37, FightHandler.ResistenciaDeLaVictima(Character(37), element, 1));
            Assert.Equal(-40, FightHandler.ResistenciaDeLaVictima(Character(-40), element, 1));
        }

        [Fact]
        public void A_blow_on_a_character_with_more_takes_what_fifty_lets_through()
        {
            int resistance = FightHandler.ResistenciaDeLaVictima(Character(80), ElementType.Fire, 1);
            Assert.Equal(50, DamageCalculator.CalculateDamage(100, ElementType.Fire, 0, 0, 0, 0, resistance, 0));
        }

        [Fact]
        public void A_monster_a_summon_and_an_incarnation_keep_theirs()
        {
            var monster = Character(80);
            monster.Breed = 0;
            monster.IsMonster = true;
            Assert.Equal(80, FightHandler.ResistenciaDeLaVictima(monster, ElementType.Earth, 1));

            var summon = Character(80);
            summon.Breed = 0;
            summon.Invocador = 1;
            Assert.Equal(80, FightHandler.ResistenciaDeLaVictima(summon, ElementType.Earth, 1));

            var incarnated = Character(80);
            incarnated.IncarnatedMonster = 3985;
            Assert.Equal(80, FightHandler.ResistenciaDeLaVictima(incarnated, ElementType.Earth, 1));
        }
    }
}

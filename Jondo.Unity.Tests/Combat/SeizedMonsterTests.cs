using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Dramak's Pantins (3483): their own «Manipulado por otro» (30668) lays «Toma el control de la
    /// entidad» on them, and a player of the other side plays them until it ends.
    /// </summary>
    public class SeizedMonsterTests
    {
        private static Fighter Person(long id, int team) => new()
        {
            Id = id, TeamId = team, CellId = 100 + (int)id, MaxHP = 2000, CurrentHP = 2000, Level = 100,
            MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4,
        };

        private static (FightInstance, Fighter, Fighter, Fighter) Fight()
        {
            var fight = new FightInstance(1, 1);
            var first = Person(10, 0);
            var second = Person(11, 0);
            var pantin = new Fighter
            {
                Id = -2, TeamId = 1, CellId = 300, MaxHP = 500, CurrentHP = 40, Level = 100,
                IsMonster = true, MonsterId = 3483,
            };
            fight.AddPlayer(first); fight.AddPlayer(second); fight.AddMonster(pantin);
            return (fight, first, second, pantin);
        }

        [Fact]
        public void The_nearest_person_of_the_other_side_plays_it()
        {
            var (fight, first, second, pantin) = Fight();
            first.CellId = 100; second.CellId = 299; pantin.CellId = 300;
            Assert.Equal(second.Id, FightHandler.SeizerOf(fight, pantin));

            second.CurrentHP = 0;
            Assert.Equal(first.Id, FightHandler.SeizerOf(fight, pantin));
            first.CurrentHP = 0;
            Assert.Equal(0, FightHandler.SeizerOf(fight, pantin));
        }
    }
}

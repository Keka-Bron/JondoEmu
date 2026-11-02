using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>What an aggression between the cities is worth in honour and dishonour (Honour).</summary>
    public class HonourTests
    {
        private static Honour.Fighter F(long id, int level, int honour = 0, int dishonour = 0, int side = Alignments.Bonta)
            => new(id, level, side, honour, dishonour);

        private static List<Honour.Change> Settle(IReadOnlyList<Honour.Fighter> winners, IReadOnlyList<Honour.Fighter> losers,
                                                  bool attackersWon = true, bool lately = false)
            => Honour.Settle(winners, losers, attackersWon, (_, _) => lately);

        [Fact]
        public void Ten_grades_from_the_honour()
        {
            Assert.Equal(1, Honour.GradeOf(0));
            Assert.Equal(1, Honour.GradeOf(499));
            Assert.Equal(2, Honour.GradeOf(500));
            Assert.Equal(10, Honour.GradeOf(17500));
            Assert.Equal(10, Honour.GradeOf(Honour.Most));
            Assert.Equal((0, 500), (Honour.FloorOf(1), Honour.CeilingOf(1)));
            Assert.Equal((17500, Honour.Most), (Honour.FloorOf(10), Honour.CeilingOf(10)));
        }

        [Fact]
        public void A_balanced_win_is_a_hundred_and_the_loser_pays_six_tenths()
        {
            var changes = Settle(new[] { F(1, 100) }, new[] { F(2, 100, honour: 400, side: Alignments.Brakmar) });
            Assert.Contains(changes, c => c.Id == 1 && c.Honour == 100 && c.Message == Honour.Gained);
            Assert.Contains(changes, c => c.Id == 2 && c.Honour == -60 && c.Message == Honour.DefeatLost);
        }

        [Fact]
        public void Beating_someone_much_stronger_is_worth_more()
        {
            var changes = Settle(new[] { F(1, 100) }, new[] { F(2, 200, side: Alignments.Brakmar) });
            Assert.Equal(200, changes.Single(c => c.Id == 1).Honour);
        }

        [Fact]
        public void A_humiliation_gives_the_attackers_dishonour_and_nothing_else()
        {
            var changes = Settle(new[] { F(1, 200) }, new[] { F(2, 50, honour: 800, side: Alignments.Brakmar) });
            var shame = changes.Single();
            Assert.Equal((1L, 0, 2, Honour.Humiliation), (shame.Id, shame.Honour, shame.Dishonour, shame.Message));

            // The same humiliating fight won by those attacked shames nobody.
            Assert.Empty(Settle(new[] { F(1, 200) }, new[] { F(2, 50, side: Alignments.Brakmar) }, attackersWon: false));
        }

        [Fact]
        public void The_same_loser_again_within_the_hour_gives_nothing()
        {
            var changes = Settle(new[] { F(1, 100) }, new[] { F(2, 100, honour: 1000, side: Alignments.Brakmar) }, lately: true);
            Assert.Equal((0, Honour.NotThisTime), (changes.Single().Honour, changes.Single().Message));
        }

        [Fact]
        public void Dishonour_is_paid_off_before_honour_is_earned()
        {
            var changes = Settle(new[] { F(1, 100, dishonour: 3) }, new[] { F(2, 100, honour: 1000, side: Alignments.Brakmar) });
            var mine = changes.Single(c => c.Id == 1);
            Assert.Equal((0, -1, Honour.Cleaned), (mine.Honour, mine.Dishonour, mine.Message));
        }
    }
}

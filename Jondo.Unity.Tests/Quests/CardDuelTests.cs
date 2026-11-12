using System;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>The zolla's card game of «Le jeu de Lenald» (CardDuels).</summary>
    public class CardDuelTests
    {
        [Fact]
        public void Stone_beats_scissors_scissors_beat_paper_and_paper_beats_stone()
        {
            Assert.Equal(1, CardDuels.Beats(new[] { 1, 0, 0 }, new[] { 0, 0, 1 }));
            Assert.Equal(1, CardDuels.Beats(new[] { 0, 0, 1 }, new[] { 0, 1, 0 }));
            Assert.Equal(1, CardDuels.Beats(new[] { 0, 1, 0 }, new[] { 1, 0, 0 }));
            Assert.Equal(0, CardDuels.Beats(new[] { 1, 0, 0 }, new[] { 0, 1, 0 }));
            Assert.Equal(0, CardDuels.Beats(new[] { 5, 0, 0 }, new[] { 5, 0, 0 }));

            // Every card against every card: three stones and two papers against five scissors.
            Assert.Equal(15, CardDuels.Beats(new[] { 3, 2, 0 }, new[] { 0, 0, 5 }));
        }

        [Fact]
        public void A_drawn_hand_has_five_cards()
        {
            var random = new Random(7);
            for (int i = 0; i < 100; i++)
            {
                var hand = CardDuels.Draw(random);
                Assert.Equal(CardDuels.HandSize, hand[0] + hand[1] + hand[2]);
            }
        }

        [Fact]
        public void The_game_goes_to_whoever_wins_more_rounds_and_then_starts_again()
        {
            const long character = -424242;
            int won = 0;
            var random = new Random(11);
            for (int round = 0; round < 3; round++)
            {
                CardDuels.Choose(character, new[] { 2, 2, 1 });
                if (CardDuels.Round(character, random)) won++;
            }
            Assert.Equal(won >= 2, CardDuels.Won(character));

            // Over: asked again, nothing has been won.
            Assert.False(CardDuels.Won(character));
        }
    }
}

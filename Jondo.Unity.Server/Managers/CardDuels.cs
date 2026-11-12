using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The zolla's card game, «Le jeu de Lenald»: five wooden cards of stone, paper and scissors
    /// chosen in the conversation and laid face down, turned over against the zolla's own five;
    /// three rounds, and whoever wins more of them wins the game.
    /// </summary>
    /// <remarks>
    /// The rules are the zolla's own words (15910): «La piedla aplasta a las tijelas que, a su vez,
    /// coltan el papel, que a su vez, cuble la piedla. El que obtenga más victolias, gana la
    /// paltida. Se juega en tres tulnos.» The replies only say how many cards of each kind are
    /// played, never in which order, so INFERRED: every card of one hand meets every card of the
    /// other, and the round goes to whoever beats more of the other's; a tie is the zolla's, who
    /// has a line for a round won (15939) and one for a round lost (15940) and none for a draw.
    /// His hand: INFERRED, each of his five cards drawn at random. Kept in memory only: a game
    /// left half way through a restart is lost.
    /// </remarks>
    public static class CardDuels
    {
        /// <summary>The kinds of card, in the order a hand counts them.</summary>
        public const int Stone = 0, Paper = 1, Scissors = 2;

        /// <summary>The cards in a hand.</summary>
        public const int HandSize = 5;

        private sealed class Game
        {
            public int[] Hand = Array.Empty<int>();
            public int Won;
            public int Played;
        }

        private static readonly ConcurrentDictionary<long, Game> _games = new();

        /// <summary>The hand the character has chosen, as stone, paper and scissors cards.</summary>
        public static void Choose(long characterId, IReadOnlyList<int> hand)
        {
            var game = _games.GetOrAdd(characterId, _ => new Game());
            game.Hand = hand.Take(3).ToArray();
        }

        /// <summary>The cards turned over: whether the round is the character's.</summary>
        public static bool Round(long characterId, Random? random = null)
        {
            var game = _games.GetOrAdd(characterId, _ => new Game());
            int[] mine = game.Hand.Length == 3 ? game.Hand : new int[3];
            int[] his = Draw(random ?? Random.Shared);
            int wins = Beats(mine, his), losses = Beats(his, mine);
            bool won = wins > losses;
            game.Played++;
            if (won) game.Won++;
            game.Hand = Array.Empty<int>();
            Console.WriteLine($"[Cards] {Show(mine)} against {Show(his)}: {wins} to {losses}, round " +
                              $"{game.Played} {(won ? "won" : "lost")} ({game.Won} won so far).");
            return won;
        }

        /// <summary>Who has won: the character, with more rounds won than lost. The game is over.</summary>
        public static bool Won(long characterId)
        {
            _games.TryRemove(characterId, out var game);
            bool won = game != null && game.Won * 2 > game.Played;
            Console.WriteLine($"[Cards] The game is {(won ? "won" : "lost")}: {game?.Won ?? 0} of {game?.Played ?? 0} rounds.");
            return won;
        }

        /// <summary>
        /// How many of the second hand's cards the first one's beat, each card against each: stone
        /// over scissors, scissors over paper, paper over stone.
        /// </summary>
        public static int Beats(IReadOnlyList<int> a, IReadOnlyList<int> b)
            => a[Stone] * b[Scissors] + a[Scissors] * b[Paper] + a[Paper] * b[Stone];

        /// <summary>A hand of five cards, each of a kind drawn at random.</summary>
        public static int[] Draw(Random random)
        {
            var hand = new int[3];
            for (int i = 0; i < HandSize; i++) hand[random.Next(3)]++;
            return hand;
        }

        private static string Show(IReadOnlyList<int> hand)
            => $"{hand[Stone]} stone, {hand[Paper]} paper, {hand[Scissors]} scissors";
    }
}

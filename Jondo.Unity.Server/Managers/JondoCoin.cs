using System;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The Jondo Coin: the server's own currency, the one every monster drops and the one
    /// paid with in the shops that do not charge in kamas.
    ///
    /// It is not a new item. The client is Ankama's and only knows how to draw and name what
    /// comes in its own data, so an invented id would not exist for it: it would have no
    /// icon, no name, no place in the inventory. What is done is TAKE ONE OF ITS OWN and change
    /// its name from JondoFix, which is the client mod (see the ItemData.get_name patch).
    ///
    /// The chosen one is the «Moneda onírica minúscula», and not by chance:
    ///
    ///   - its icon, 148013, is a turquoise coin with sparkles: it is told apart at a glance
    ///     from the kamas, which are yellow;
    ///   - IT WEIGHS ZERO. It is what makes this work: someone can gather fifty thousand without
    ///     the pods saying anything. With almost any other resource the player would have been
    ///     stuck at two hundred coins;
    ///   - it is of type 131, that of the resources monsters drop, so it stacks and
    ///     behaves as what it is;
    ///   - no recipe and no profession uses it, so taking it away from the game breaks nothing.
    ///
    /// And it has three sisters with the SAME icon and also weightless —20441 small, 20442
    /// large and 20443 huge—, in case a coin of another rank is ever needed.
    /// </summary>
    public static class JondoCoin
    {
        /// <summary>
        /// The template that acts as the coin. «Moneda onírica minúscula» in Ankama's data,
        /// «Jondo Coin» on the player's screen.
        /// </summary>
        public const int TemplateId = 20440;

        /// <summary>How wide each bracket is: 25 levels at a time.</summary>
        public const int LevelsPerBand = 25;

        /// <summary>
        /// The level from which one stops moving up brackets, that is the ceiling: 9 coins.
        ///
        /// The rule is «one more coin for every 25 levels», but taken literally a
        /// level 2,400 monster would pay 96 coins in one go. Measured over the 26,969
        /// monster-and-grade combinations of the game: the median is 140, the 99th percentile is 220
        /// and only 188 —of 51 templates out of 5,134— go over 225. And the highest ones are bosses or
        /// test entries: «[!] Willorque» with 2,400, «[!] Mureine» with 1,800.
        ///
        /// So bracket 9 is the last. It covers 99 % of the game just as the rule asked, and
        /// puts a door on the 1 % that would break it. If it is ever wanted with no ceiling, this
        /// number is raised and that is it.
        /// </summary>
        public const int HighestBandedLevel = 225;

        /// <summary>How many coins a monster of that level pays.</summary>
        ///
        /// <remarks>
        /// Level 1 to 25 one, 26 to 50 two, 51 to 75 three, and so on. A zero or negative level —which
        /// should not exist, but the client's data is theirs and not ours— pays one, not
        /// zero nor some odd number.
        /// </remarks>
        public static int RewardFor(int monsterLevel)
        {
            int nivel = Math.Clamp(monsterLevel, 1, HighestBandedLevel);
            return 1 + (nivel - 1) / LevelsPerBand;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Which of a weapon's damage lines a hit deals.
    /// </summary>
    /// <remarks>
    /// Lines with the same dice are alternatives, and a hit deals one of them: a damage line
    /// before a steal, and among the damage lines the wielder's best element. Measured over the
    /// 13 weapon hits of the captures whose weapon has such lines, with no exception:
    ///
    ///   Arco de vueloceronte  11-15 in all four elements     earth, twice (1,382 strength)
    ///   Lavacha               12-16 earth steal and damage   the damage, seven times
    ///   Garras de la Despedazadora  11-13 water damage and fire steal   the water damage,
    ///                         twice, though its wielder has more intelligence than chance
    ///   Palarpón              21-25 fire, twice              one fire blow
    ///   Cocobur               6-9 water and air steal        the water one (257 chance, 100 agility)
    ///
    /// Lines with dice of their own all hit: Espada diablina and Palote Timológico deal their
    /// fire damage and their smaller fire steal every time. The client's data carries nothing
    /// that tells the lines apart -- same fields, no trigger, no draw --, so the rule is the
    /// server's. The emulator dealt every line, and the bow hit four times as hard as it should.
    /// </remarks>
    internal static class WeaponHit
    {
        /// <summary>A weapon damage line: the effect, its element and its dice.</summary>
        internal readonly record struct Line(int Effect, int Element, int Min, int Max);

        /// <summary>The steal effects, 91 to 95, which give way to a damage line with the same dice.</summary>
        internal static bool IsSteal(int effect) => (effect >= 91 && effect <= 95) || effect == 2828;

        /// <summary>
        /// The lines a hit deals, in the order the weapon carries them.
        /// </summary>
        /// <param name="pointsOfElement">
        /// The wielder's points in an element's characteristic, buffs included: what picks the
        /// best element among alternatives.
        /// </param>
        /// <remarks>
        /// A tie goes to the later element in the order neutral, earth, fire, water, air. The
        /// client's own tooltip says so: with 1,415 in each of the four characteristics, it put
        /// the bow's damage on air and drew the other three lines as 0.
        /// </remarks>
        internal static List<Line> LinesThatHit(IReadOnlyList<Line> lines, Func<int, int> pointsOfElement)
        {
            var chosen = new HashSet<int>();
            foreach (var group in Enumerable.Range(0, lines.Count).GroupBy(i => (lines[i].Min, lines[i].Max)))
            {
                var options = group.ToList();
                var damage = options.Where(i => !IsSteal(lines[i].Effect)).ToList();
                if (damage.Count > 0) options = damage;

                int best = options[0];
                foreach (int i in options.Skip(1))
                {
                    int points = pointsOfElement(lines[i].Element), bestPoints = pointsOfElement(lines[best].Element);
                    if (points > bestPoints || (points == bestPoints && lines[i].Element >= lines[best].Element))
                        best = i;
                }
                chosen.Add(best);
            }
            return Enumerable.Range(0, lines.Count).Where(chosen.Contains).Select(i => lines[i]).ToList();
        }
    }
}

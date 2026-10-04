using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.World.Fights
{
    /// <summary>
    /// Tackle and escape: what leaving a cell next to enemies costs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client explains it and names the two characteristics, but gives no figure. Its own
    /// catalogue (<c>CharacteristicData</c>) has 78 <c>tackleEvade</c> -- "huida" -- and 79
    /// <c>tackleBlock</c> -- "placaje" --, and Translations 1119639 says the rest: "the more escape
    /// you have, the fewer AP and MP you lose trying to leave your enemies' tackle zone". The zone
    /// is the four cells around a fighter, the only ones one step away.
    /// </para>
    /// <para>
    /// The figure is measured on the seven tackles the captures hold (a <c>jwe 104</c> each), with
    /// the escape of whoever walks and the tackle of whoever holds him read off their own sheets:
    /// </para>
    /// <code>
    ///   capture, frame                                   escape tackle  AP -> lost   MP -> lost
    ///   eleccion personaje-carga world-tutorial   #478      0      0     6 -> 3       3 -> 2
    ///   eleccion personaje-carga world-tutorial   #2439     0      0     2 -> 1       4 -> 2
    ///   aceptar desafio-combate completo          #1600    75     89     2 -> 1       2 -> 1
    ///   entrar a combate-desconectarse-reconectar #2007    10      5     7 -> 1       3 -> 0
    ///   atacar a recaudador y perder              #367     96     60    10 -> 2       2 -> 0
    ///   ruta muy larga zonas pandala-sidimote     #2355    66     98     1 -> 1       6 -> 4
    ///   submarino steamer-...-dopeul-perder       #1241    80     60     8 -> 3       5 -> 2
    /// </code>
    /// <para>
    /// All seven are one rule: the mover keeps <c>(escape + 2) / (2 × (tackle + 2))</c> of his
    /// points, and what he loses is the rest rounded half up. The tutorial's three MP at a half
    /// settle the rounding: 1.5 is lost as 2 -- rounding what he KEEPS would have left him 2.
    /// Walks that start in contact and cost nothing agree too: an escape of 69 against 0 ("aceptar
    /// desafio", frame 2542) and one of 80 against 10 (the Dopeul's, frame 1774) keep more than
    /// everything, and nothing is sent.
    /// </para>
    /// <para>
    /// Every one of the seven has a single tackler. The frame names them in a list
    /// (<c>f11 { f1: packed }</c>), so several are a real shape; that each of them keeps his own
    /// share and the shares multiply is INFERRED -- no capture has two. So is reading a negative
    /// escape or tackle as zero: the share turns negative below -2, and the one negative escape
    /// of the captures (a Xelor at -32) only ever walked away from training dummies that do not
    /// tackle.
    /// </para>
    /// </remarks>
    public static class Tackle
    {
        /// <summary>"tackleEvade", escape, in the client's characteristic catalogue.</summary>
        public const int EscapeCharacteristic = 78;

        /// <summary>"tackleBlock", tackle, in the client's characteristic catalogue.</summary>
        public const int TackleCharacteristic = 79;

        /// <summary>What a tackle took: action points and movement points.</summary>
        public readonly record struct Loss(int ActionPoints, int MovementPoints)
        {
            public static readonly Loss None = new Loss(0, 0);

            /// <summary>Whether it took anything at all -- and so whether anything is announced.</summary>
            public bool Any => ActionPoints > 0 || MovementPoints > 0;
        }

        /// <summary>
        /// The share of his points a fighter with <paramref name="escape"/> keeps when he leaves
        /// the zone of tacklers with these <paramref name="tackles"/>: between 0 and 1, and 1 with
        /// nobody holding him.
        /// </summary>
        public static double KeptShare(int escape, IEnumerable<int> tackles)
        {
            var (kept, of) = Fraction(escape, tackles);
            return kept >= of ? 1.0 : (double)kept / of;
        }

        /// <summary>
        /// The points lost out of <paramref name="points"/> leaving that zone: the share he does
        /// not keep, rounded half up. Worked in integers so that a half is a half and not
        /// 0.49999.
        /// </summary>
        public static int PointsLost(int points, int escape, IEnumerable<int> tackles)
        {
            if (points <= 0) return 0;
            var (kept, of) = Fraction(escape, tackles);
            if (kept >= of) return 0;

            // points × (of − kept) / of, plus a half, floored: (2·points·(of − kept) + of) / (2·of).
            long lost = (2L * points * (of - kept) + of) / (2L * of);
            return (int)Math.Clamp(lost, 0, points);
        }

        /// <summary>Both losses at once, for the points the fighter holds when he leaves.</summary>
        public static Loss Resolve(int escape, IReadOnlyCollection<int> tackles, int actionPoints, int movementPoints)
        {
            if (tackles == null || tackles.Count == 0) return Loss.None;
            return new Loss(PointsLost(actionPoints, escape, tackles),
                            PointsLost(movementPoints, escape, tackles));
        }

        /// <summary>
        /// Whoever of <paramref name="enemies"/> stands on one of the four cells around
        /// <paramref name="cell"/> and is able to tackle, in the order given.
        /// </summary>
        public static List<Fighter> TacklersAround(int cell, IEnumerable<Fighter> enemies, Func<Fighter, bool> canTackle)
        {
            var around = new HashSet<int>(MapGeometry.GetNeighbors(cell));
            return enemies.Where(e => e != null && around.Contains(e.CellId) && canTackle(e)).ToList();
        }

        /// <summary>
        /// The share kept as a fraction, kept / of: the product of every tackler's
        /// (escape + 2) / (2 × (tackle + 2)). Four tacklers at most -- there are four cells around
        /// -- so the product fits in a long with room to spare.
        /// </summary>
        private static (long Kept, long Of) Fraction(int escape, IEnumerable<int> tackles)
        {
            long kept = 1, of = 1;
            int evade = Math.Max(0, escape);
            foreach (int tackle in tackles ?? Array.Empty<int>())
            {
                kept *= evade + 2;
                of *= 2L * (Math.Max(0, tackle) + 2);
            }
            return (kept, of);
        }
    }
}

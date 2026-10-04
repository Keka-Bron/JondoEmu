using System;

namespace Jondo.Unity.World.Fights
{
    /// <summary>
    /// What losing a fight against monsters costs: energy, half the life, and the way home.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on the four defeats of a real character against monsters, all of the same level
    /// 354 -- a 200 with Omega 154 -- and all telling the same story in the kub the real server
    /// sends right behind the jyg:
    /// </para>
    /// <code>
    ///   capture, jyg frame                                  energy (29)     life missing (97)
    ///   entrar a combate-cerrar juego...reanudar     #278    5000 -> 3000    1153 of 1153 -> 576
    ///   submarino steamer-...-dopeul-perder          #2492   3000 -> 1000                 -> 576
    ///   vestigio de zaap-...-lanzar combate-abandonar #763   1000 -> 1       1153 of 1153 -> 576
    ///   ruta muy larga zonas pandala-sidimote        #8825  10000 -> 8000    6192 of 6192 -> 3096
    /// </code>
    /// <para>
    /// The energy: the client's own help, Translations 1156704, says a lost fight "except in the
    /// Koliseo or a challenge" costs "10 times the level", out of a gauge of 10,000. The level
    /// 354 lost 2,000 each time -- 10 × 200 -- so the Omega levels do not count. And the gauge
    /// does not empty: 1,000 lost 999, "Has perdido 999 puntos de energía", and stayed at one.
    /// Nobody turns into a ghost; the 3.6.10 client still has the texts for tombs, ghosts and
    /// phoenixes, but no capture reaches them.
    /// </para>
    /// <para>
    /// The life: whatever it was, the loser comes back with half of his maximum missing -- 576 of
    /// 1,153 and 3,096 of 6,192 -- the rest of it his.
    /// </para>
    /// <para>
    /// The collector's defeat ("atacar a recaudador y perder", jyg #6333) cost 10,000 -> 5,000 with
    /// the same half life: a fight of its own kind, and not this rule's. The doubling in a "divine
    /// dimension" the same help text mentions is not measured.
    /// </para>
    /// </remarks>
    public static class DefeatPenalty
    {
        /// <summary>The energy gauge: "un máximo de 10 000", and 47 in every captured kub.</summary>
        public const int MaxEnergy = 10000;

        /// <summary>"10 veces su nivel".</summary>
        public const int EnergyPerLevel = 10;

        /// <summary>
        /// The last level the energy counts: 2,000 lost at 354, so the 154 Omega levels above 200
        /// cost nothing.
        /// </summary>
        public const int LastLevelCounted = 200;

        /// <summary>The gauge stops here: 1,000 energy lost 999.</summary>
        public const int LeastEnergy = 1;

        /// <summary>What a defeat takes from <paramref name="energy"/> at <paramref name="level"/>.</summary>
        public static int EnergyLost(int level, int energy)
        {
            int due = EnergyPerLevel * Math.Clamp(level, 1, LastLevelCounted);
            return Math.Clamp(Math.Min(due, energy - LeastEnergy), 0, due);
        }

        /// <summary>The life a loser has missing when he comes back: half his maximum, rounded down.</summary>
        public static int MissingLifeAfter(int maxLife) => Math.Max(0, maxLife) / 2;
    }
}

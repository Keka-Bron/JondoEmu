using System;
using System.Collections.Concurrent;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The life a character is missing outside a fight, and how it comes back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until now every fight ended with the life full, and every fight began with it full. A
    /// defeat is where that stops being true: the loser comes back with half his maximum
    /// missing (<see cref="Jondo.Unity.World.Fights.DefeatPenalty"/>), and it travels in the kub
    /// as characteristic 97, <c>hitPointLoss</c>, "f4 { f2: -576 }".
    /// </para>
    /// <para>
    /// It comes back at the pace of the regeneration the fight's end starts (ktz, one point per
    /// <see cref="ConnectionProtocol.RegenerationTickMs"/>), and the next fight takes what is
    /// left: "entrar a combate-cerrar juego" enters its fight nine and a half minutes after the
    /// collector's defeat left 576 missing, and its kuq reads 1,153 of 1,153 after 1,138 ticks.
    /// </para>
    /// <para>
    /// Kept in memory, per character, for as long as the server runs: a restart gives the life
    /// back. What a won fight leaves is not kept either -- the fight end gives the life back
    /// full as it always did; the captures do show a winner keeping his wounds, and that is left
    /// for another change.
    /// </para>
    /// </remarks>
    public static class RestingLife
    {
        private readonly record struct Wound(int Missing, DateTime SinceUtc);

        private static readonly ConcurrentDictionary<long, Wound> _wounds = new();

        /// <summary>Leaves the character with <paramref name="missing"/> life short, counted from now.</summary>
        public static void Set(long characterId, int missing, DateTime nowUtc)
        {
            if (characterId == 0) return;
            if (missing <= 0) { _wounds.TryRemove(characterId, out _); return; }
            _wounds[characterId] = new Wound(missing, nowUtc);
        }

        /// <summary>Gives the character his whole life back.</summary>
        public static void Clear(long characterId) => _wounds.TryRemove(characterId, out _);

        /// <summary>
        /// The life still missing at <paramref name="atUtc"/>, once the regeneration since the
        /// wound has done its part.
        /// </summary>
        public static int MissingAt(long characterId, DateTime atUtc)
        {
            if (!_wounds.TryGetValue(characterId, out var wound)) return 0;
            int healed = ConnectionProtocol.RegenerationTicksSince(wound.SinceUtc, atUtc);
            return Math.Max(0, wound.Missing - healed);
        }

        /// <summary>
        /// The life he enters a fight with: his maximum less what is still missing, and never
        /// less than one.
        /// </summary>
        public static int LifeAt(long characterId, int maxLife, DateTime atUtc)
            => Math.Max(1, maxLife - Math.Min(MissingAt(characterId, atUtc), Math.Max(0, maxLife - 1)));
    }
}

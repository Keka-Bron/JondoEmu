using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The matches found and not yet answered, and who has been banned from signing up.
    /// </summary>
    /// <remarks>
    /// Between finding the match and starting it there is a pop-up: «se ha detectado un combate», with
    /// accept, decline and a deadline. Everything here comes from two captures of the real server, one of
    /// 2 versus 2 and another of 3 versus 3:
    ///
    /// <code>
    ///   S-&gt;C  lsh «103b»          the notice. f2 = 59, and they are SECONDS
    ///   C-&gt;S  luy «1001»          accept. f2 is a bool, not an index
    ///   S-&gt;C  lth «1001»          the acknowledgement, through root 3 with the request's id
    /// </code>
    ///
    /// That 59 is seconds is not a hunch: in the 3 versus 3 capture the player let the
    /// deadline run out, and between the lsh and the first frame of the expiry <b>60,014 ms</b> pass.
    /// Both captures bring the same 59, in different modes and on different servers, so
    /// it is the whole deadline and not what was left of it.
    ///
    /// On expiring, the real server sends four things and then bans signing up for a while:
    ///
    /// <code>
    ///   lty  the ranking, which is not sent here: see KoliseoHandler
    ///   lqn { 1, 503, ["1788216996"] }   the notice, with the timestamp at which it is lifted
    ///   ltk  empty
    ///   lsx { f3: 3, f4: mode }          out of the queue
    /// </code>
    ///
    /// And on trying to sign up again too early, <c>lqn { 1, 642, ["4"] }</c> with the minutes
    /// left. The client's text says five minutes and the capture shows a «4» one minute
    /// later, so <see cref="Castigo"/> is five.
    ///
    /// WHAT IS NOT MEASURED: what the client sends on pressing DECLINE. In the capture the deadline
    /// was left to run out. From the shape of the luy —a proto3 bool, which does not travel when false— a decline would
    /// have to arrive as a luy with an empty payload, and it is treated that way; the precedent of the pvp challenge does
    /// the same (accept «08ec031001», decline «08e903»).
    /// </remarks>
    public static class KoliseoOffers
    {
        /// <summary>How long the pop-up lasts. The lsh's f2, and the 60 s measured waiting for it.</summary>
        public const int Segundos = 59;

        /// <summary>How long it takes to be able to sign up again after letting it expire.</summary>
        public const int Castigo = 5;

        public sealed class Offer
        {
            public long Id { get; init; }
            public int Mode { get; init; }
            public int TeamSize { get; init; }
            public IReadOnlyList<long> Blue { get; init; } = Array.Empty<long>();
            public IReadOnlyList<long> Red { get; init; } = Array.Empty<long>();

            /// <summary>Who has said yes. The rest are still pending.</summary>
            public HashSet<long> Accepted { get; } = new HashSet<long>();

            /// <summary>True as soon as someone resolves it, so it is not resolved twice.</summary>
            public bool Closed { get; set; }

            public object Gate { get; } = new object();

            public IEnumerable<long> Everybody
            {
                get
                {
                    foreach (long id in Blue) yield return id;
                    foreach (long id in Red) yield return id;
                }
            }
        }

        private static long _next = 1;
        private static readonly ConcurrentDictionary<long, Offer> _offers = new();

        /// <summary>Which offer each character is in. One can only be in one.</summary>
        private static readonly ConcurrentDictionary<long, long> _of = new();

        /// <summary>Until when each one is banned from signing up.</summary>
        private static readonly ConcurrentDictionary<long, DateTime> _banned = new();

        /// <summary>The last mode in which each one was found a match.</summary>
        /// <remarks>
        /// The lsx of coming back from the koliseo carries the mode in the same f4 as the one of being
        /// searching, and on coming back there is neither queue nor offer to take it from. It is recorded on opening
        /// the offer, which is the last moment it is known.
        /// </remarks>
        private static readonly ConcurrentDictionary<long, int> _lastMode = new();

        public static int Pending => _offers.Count;

        /// <summary>Opens an offer for the two teams already matched.</summary>
        public static Offer Open(int mode, int teamSize, IReadOnlyList<long> blue,
                                 IReadOnlyList<long> red)
        {
            var offer = new Offer
            {
                Id = System.Threading.Interlocked.Increment(ref _next),
                Mode = mode,
                TeamSize = teamSize,
                Blue = new List<long>(blue),
                Red = new List<long>(red),
            };

            _offers[offer.Id] = offer;
            foreach (long id in offer.Everybody)
            {
                _of[id] = offer.Id;
                _lastMode[id] = mode;
            }
            return offer;
        }

        public static Offer? Of(long characterId)
            => _of.TryGetValue(characterId, out long id) && _offers.TryGetValue(id, out var offer)
                ? offer
                : null;

        public static Offer? ById(long id) => _offers.TryGetValue(id, out var offer) ? offer : null;

        /// <summary>
        /// Records a yes. Returns true when EVERYONE has already said yes, which is when the
        /// fight can start.
        /// </summary>
        public static bool Accept(Offer offer, long characterId)
        {
            lock (offer.Gate)
            {
                if (offer.Closed) return false;
                offer.Accepted.Add(characterId);

                foreach (long id in offer.Everybody)
                {
                    if (!offer.Accepted.Contains(id)) return false;
                }

                offer.Closed = true;
                return true;
            }
        }

        /// <summary>
        /// Closes the offer and deletes it. Returns false if someone had got there first, so that
        /// the expiry does not trample an acceptance that arrived by a whisker.
        /// </summary>
        public static bool Close(Offer offer)
        {
            lock (offer.Gate)
            {
                if (offer.Closed) return false;
                offer.Closed = true;
            }
            Forget(offer);
            return true;
        }

        /// <summary>Removes the offer from the index. <see cref="Close"/> or the acceptance closes it.</summary>
        public static void Forget(Offer offer)
        {
            _offers.TryRemove(offer.Id, out _);
            foreach (long id in offer.Everybody) _of.TryRemove(id, out _);
        }

        /// <summary>The ones who did not say yes. They are the ones who get the penalty.</summary>
        public static List<long> WhoDidNotAnswer(Offer offer)
        {
            var quienes = new List<long>();
            lock (offer.Gate)
            {
                foreach (long id in offer.Everybody)
                {
                    if (!offer.Accepted.Contains(id)) quienes.Add(id);
                }
            }
            return quienes;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  The penalty
        // ═══════════════════════════════════════════════════════════════════

        public static void Ban(long characterId, DateTime cuando)
            => _banned[characterId] = cuando;

        /// <summary>When the penalty is lifted, or null if there is none.</summary>
        public static DateTime? BannedUntil(long characterId)
        {
            if (!_banned.TryGetValue(characterId, out var hasta)) return null;
            if (hasta <= DateTime.UtcNow)
            {
                _banned.TryRemove(characterId, out _);
                return null;
            }
            return hasta;
        }

        /// <summary>
        /// The minutes left, rounded UP.
        /// </summary>
        /// <remarks>
        /// The capture shows a «4» on retrying shortly after a five-minute penalty, and
        /// rounding down that would have come out «4» only during the fifth minute. Rounding
        /// up it comes out «4» during the whole fourth, which is what is seen.
        /// </remarks>
        public static int MinutesLeft(long characterId)
        {
            var hasta = BannedUntil(characterId);
            if (hasta == null) return 0;

            double minutos = (hasta.Value - DateTime.UtcNow).TotalMinutes;
            return Math.Max(1, (int)Math.Ceiling(minutos));
        }

        /// <summary>Which mode the last koliseo was played in, or zero if it is not on record.</summary>
        public static int LastMode(long characterId)
            => _lastMode.TryGetValue(characterId, out int mode) ? mode : 0;

        /// <summary>Only for the tests: no offers, no penalties and no memory.</summary>
        internal static void ForgetEverything()
        {
            _offers.Clear();
            _of.Clear();
            _banned.Clear();
            _lastMode.Clear();
        }
    }
}

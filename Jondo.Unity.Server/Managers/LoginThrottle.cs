using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// How many times one address, or one account name, may be guessed at before it has to wait.
    /// </summary>
    /// <remarks>
    /// This used to be a dictionary and two helpers inside <see cref="DatabaseManager"/>, and it had
    /// four holes worth naming because each one has a different shape:
    ///
    /// <list type="number">
    /// <item>Read, then write. The check looked at the count and the increment happened at the far
    /// end of the method, with a password hash in between. Twenty connections firing at once all
    /// read "0 attempts" and all proceeded, so the limit of five bought nothing against the thing it
    /// exists for: PBKDF2 is deliberately expensive, and the cheapest way to spend somebody else's
    /// CPU was to ask for it in parallel. The attempt is now counted <b>before</b> the hash, under a
    /// lock, and the count that comes back is the one the decision is made on.</item>
    ///
    /// <item>A success wiped the counter. Anyone holding one working account -- a free one on a
    /// public test server -- could log into it to reset the limit and go back to guessing. A success
    /// now takes back only its own attempt, so honest logins cost nothing and failures still add up.</item>
    ///
    /// <item>Per address only. Guessing one password from a thousand addresses never tripped
    /// anything. The account name is now counted too, and either side can block.</item>
    ///
    /// <item>The dictionary only ever grew. Every address that ever failed stayed in memory for the
    /// life of the process; on a public server that is an attacker-chosen leak. Stale rows are swept
    /// once there are enough of them to be worth sweeping.</item>
    /// </list>
    ///
    /// A blocked attempt does <b>not</b> push the deadline back. Sliding it would sound stricter and
    /// is worse: it lets whoever is hammering hold everybody behind their NAT out for as long as they
    /// keep going.
    /// </remarks>
    public static class LoginThrottle
    {
        /// <summary>Failures allowed inside the window before the wait starts.</summary>
        public const int MaxAttempts = 5;

        /// <summary>How long the count is remembered, and how long the wait lasts.</summary>
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

        /// <summary>Rows to reach before stale ones are swept. Nothing sacred, just "enough to bother".</summary>
        private const int SweepAt = 4096;

        private sealed class Slot
        {
            public int Attempts;
            public DateTime Last;
        }

        private static readonly ConcurrentDictionary<string, Slot> _slots
            = new ConcurrentDictionary<string, Slot>(StringComparer.Ordinal);

        /// <summary>How many addresses and names are being counted. For the tests and for eyeballing.</summary>
        public static int Watched => _slots.Count;

        /// <summary>Forgets everybody. Tests only -- calling this in the server hands out free retries.</summary>
        public static void Clear() => _slots.Clear();

        /// <summary>
        /// Books one attempt against the address and the account name. False means do not go on.
        /// </summary>
        /// <remarks>
        /// Call this <b>before</b> touching the database or hashing anything: the whole point is that
        /// the expensive part never runs for an attempt that is over the limit.
        /// </remarks>
        public static bool TryBegin(string? clientIp, string? login, DateTime now, out string error)
        {
            error = "";
            string address = "ip:" + (clientIp ?? "");
            string name = string.IsNullOrEmpty(login) ? "" : "user:" + login;

            if (!Bump(address, now, out double waitAddress))
            {
                error = Wait(waitAddress);
                return false;
            }

            if (name.Length > 0 && !Bump(name, now, out double waitName))
            {
                // The address was already charged for this attempt and it is not going to happen,
                // so give it back. Otherwise a locked-out account name would drag its address down
                // with it, which is how one person guessing at "admin" locks out their whole office.
                Give(address);
                error = Wait(waitName);
                return false;
            }

            Sweep(now);
            return true;
        }

        /// <summary>
        /// The password was right. Takes back the attempt this login booked, and clears the name.
        /// </summary>
        /// <remarks>
        /// The name is cleared outright because whoever just typed its password is its owner, and
        /// the count against it existed to protect them. The address is only given its one attempt
        /// back: the failures before this one were still failures, and were quite possibly somebody
        /// else's.
        /// </remarks>
        public static void Succeeded(string? clientIp, string? login)
        {
            Give("ip:" + (clientIp ?? ""));
            if (!string.IsNullOrEmpty(login)) _slots.TryRemove("user:" + login, out _);
        }

        private static bool Bump(string key, DateTime now, out double remainingSeconds)
        {
            remainingSeconds = 0;
            var slot = _slots.GetOrAdd(key, _ => new Slot { Last = now });

            lock (slot)
            {
                if (now - slot.Last >= Window) slot.Attempts = 0;

                if (slot.Attempts >= MaxAttempts)
                {
                    // Measured from the last attempt that counted, not from this one: see the
                    // remark above about not sliding the deadline.
                    remainingSeconds = (Window - (now - slot.Last)).TotalSeconds;
                    if (remainingSeconds < 0) remainingSeconds = 0;
                    return false;
                }

                slot.Attempts++;
                slot.Last = now;
                return true;
            }
        }

        private static void Give(string key)
        {
            if (!_slots.TryGetValue(key, out var slot)) return;
            lock (slot)
            {
                if (slot.Attempts > 0) slot.Attempts--;
            }
        }

        private static string Wait(double seconds)
            => $"[Anti-DDoS] Too many failed attempts. Temporarily locked out for {Math.Ceiling(seconds)} s.";

        private static void Sweep(DateTime now)
        {
            if (_slots.Count < SweepAt) return;

            var gone = new List<string>();
            foreach (var pair in _slots)
            {
                lock (pair.Value)
                {
                    if (now - pair.Value.Last >= Window) gone.Add(pair.Key);
                }
            }

            foreach (string key in gone) _slots.TryRemove(key, out _);
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The parties: who goes with whom and who leads.
    ///
    /// ─── The identifier ─────────────────────────────────────────────────────────────────────
    ///
    /// A party has its own number, and it is nobody's: in the four captures 69145,
    /// 69158, 69186 and 71272 come out, consecutive and low, that is a server counter. Here it is done the same
    /// and it starts where they do, so that the numbers look the way the client expects.
    ///
    /// ─── It is not stored in the database ───────────────────────────────────────────────────
    ///
    /// A party lasts as long as the session: if the server goes down, there is no party to recover,
    /// just like in the real game. That is why it lives in memory and not in SQLite.
    ///
    /// ─── The rules that come from the captures ──────────────────────────────────────────────
    ///
    /// A party with ONE person breaks up: on declining the invitation, the real server sends the
    /// <c>iko</c> and the <c>imy</c> stuck together, in the same TCP segment. That is, inviting creates the
    /// party before the other answers, and if he says no, it breaks up on its own.
    ///
    /// And the places are eight, which is what the ing's f10 and the ijz's f3 carry.
    /// </summary>
    public static class Parties
    {
        /// <summary>How many people fit. From the ing's f10 and the ijz's f3.</summary>
        public const int MaxMembers = 8;

        /// <summary>Where the numbers start, so that they look like the real ones.</summary>
        private const int FirstId = 69000;

        public sealed class Party
        {
            public int Id { get; init; }

            /// <summary>Who leads. It is the ing's f4.</summary>
            public long LeaderId { get; set; }

            /// <summary>The ones already inside, in the order they joined.</summary>
            public List<long> Members { get; } = new();

            /// <summary>The ones with the invitation open: invitee → who invited him.</summary>
            public Dictionary<long, long> Pending { get; } = new();

            /// <summary>
            /// The members following the leader's movement: each asked with an imh and has not
            /// said imo since. Right after a change of leader the new one can still be in here,
            /// until the handler cuts them all; <see cref="FollowersOf"/> never returns him.
            /// </summary>
            public HashSet<long> Followers { get; } = new();

            public object Gate { get; } = new();
        }

        private static int _next = FirstId;
        private static readonly ConcurrentDictionary<int, Party> _parties = new();

        /// <summary>Which party each character is in, whether inside or invited.</summary>
        private static readonly ConcurrentDictionary<long, int> _of = new();

        public static int Count => _parties.Count;

        public static Party? Get(int id) => _parties.TryGetValue(id, out var p) ? p : null;

        /// <summary>A character's party, whether he is inside or has an open invitation.</summary>
        public static Party? Of(long characterId)
            => _of.TryGetValue(characterId, out int id) ? Get(id) : null;

        /// <summary>Is he INSIDE a party? Having an open invitation does not count.</summary>
        public static bool IsInParty(long characterId)
        {
            var party = Of(characterId);
            if (party == null) return false;
            lock (party.Gate) return party.Members.Contains(characterId);
        }

        /// <summary>
        /// Creates the party around whoever invites. The real server creates it on sending the ime,
        /// before the other answers: that is why the ing with a single member arrives at once.
        /// </summary>
        public static Party Create(long leaderId)
        {
            var party = new Party { Id = System.Threading.Interlocked.Increment(ref _next), LeaderId = leaderId };
            party.Members.Add(leaderId);
            _parties[party.Id] = party;
            _of[leaderId] = party.Id;
            return party;
        }

        /// <summary>
        /// Leaves the invitation open. False if the invitee is already in some party.
        ///
        /// If he already had it open and the same one invites him again, it is fine all the same and it is sent to him
        /// again: closing the little window with the cross does not tell the server, and if this were not allowed, the
        /// invitee would be stuck forever, unable to be invited again.
        /// </summary>
        public static bool Invite(Party party, long guestId, long hostId)
        {
            if (IsInParty(guestId)) return false;

            lock (party.Gate)
            {
                if (party.Pending.TryGetValue(guestId, out long antes)) return antes == hostId;
                if (party.Members.Count + party.Pending.Count >= MaxMembers) return false;
                party.Pending[guestId] = hostId;
            }
            _of[guestId] = party.Id;
            return true;
        }

        /// <summary>Acepta: pasa de invitado a miembro.</summary>
        public static bool Accept(Party party, long guestId)
        {
            lock (party.Gate)
            {
                if (!party.Pending.Remove(guestId)) return false;
                if (party.Members.Contains(guestId)) return true;
                party.Members.Add(guestId);
            }
            _of[guestId] = party.Id;
            return true;
        }

        /// <summary>Declines. Returns who had invited, or zero if there was no such invitation.</summary>
        public static long Refuse(Party party, long guestId)
        {
            long host;
            lock (party.Gate)
            {
                if (!party.Pending.TryGetValue(guestId, out host)) return 0;
                party.Pending.Remove(guestId);
            }
            _of.TryRemove(guestId, out _);
            return host;
        }

        /// <summary>
        /// Leaves the party. Returns who has to be told and whether the party has broken up.
        ///
        /// If the one leaving was the leader, the next one who joined leads: a party without a leader the
        /// client does not understand, and leaving the lead with someone who is no longer there is worse.
        /// </summary>
        public static (IReadOnlyList<long> Remaining, bool Dissolved, long NewLeader) Leave(
            Party party, long characterId)
        {
            bool dissolved;
            long newLeader = 0;
            List<long> remaining;

            lock (party.Gate)
            {
                party.Members.Remove(characterId);
                party.Pending.Remove(characterId);
                party.Followers.Remove(characterId);

                if (party.LeaderId == characterId && party.Members.Count > 0)
                {
                    party.LeaderId = party.Members[0];
                    newLeader = party.LeaderId;
                }

                dissolved = party.Members.Count <= 1;
                remaining = new List<long>(party.Members);
            }

            _of.TryRemove(characterId, out _);
            if (dissolved) Dissolve(party);
            return (remaining, dissolved, newLeader);
        }

        /// <summary>Hands over the lead. False if the recipient is not inside.</summary>
        public static bool Promote(Party party, long newLeaderId)
        {
            lock (party.Gate)
            {
                if (!party.Members.Contains(newLeaderId)) return false;
                party.LeaderId = newLeaderId;
            }
            return true;
        }

        /// <summary>Breaks up the party and lets everyone go.</summary>
        public static void Dissolve(Party party)
        {
            long[] todos;
            lock (party.Gate)
            {
                todos = party.Members.Concat(party.Pending.Keys).ToArray();
                party.Members.Clear();
                party.Pending.Clear();
                party.Followers.Clear();
            }
            foreach (long quien in todos) _of.TryRemove(quien, out _);
            _parties.TryRemove(party.Id, out _);
        }

        /// <summary>The members, to send something to all of them.</summary>
        public static IReadOnlyList<long> MembersOf(Party party)
        {
            lock (party.Gate) return new List<long>(party.Members);
        }

        // ─── Following the leader ───────────────────────────────────────────────

        /// <summary>
        /// A member starts following the leader's movement (imh). Returns his party, or null
        /// when he cannot follow anybody.
        /// </summary>
        /// <remarks>
        /// Only somebody already inside and not leading: an invitee has not joined yet, and the
        /// leader following himself would be sent his own position after every step. The imh
        /// carries nothing, not even whom to follow, so it always means the leader.
        /// <paramref name="isNew"/> is false when he was following already: the member's client
        /// asks again every time the leader switches following on, without an imo in between
        /// (frames 0 and 10 of "Grupos/con grupo seguir desplazamiento del lider...").
        /// </remarks>
        public static Party? Follow(long characterId, out bool isNew)
        {
            isNew = false;
            var party = Of(characterId);
            if (party == null) return null;

            lock (party.Gate)
            {
                if (!party.Members.Contains(characterId) || party.LeaderId == characterId) return null;
                isNew = party.Followers.Add(characterId);
            }
            return party;
        }

        /// <summary>
        /// A member stops following (imo). Returns his party, or null when he is in none.
        /// <paramref name="wasFollowing"/> says whether the server still had him as following.
        /// </summary>
        public static Party? Unfollow(long characterId, out bool wasFollowing)
        {
            wasFollowing = false;
            var party = Of(characterId);
            if (party == null) return null;

            lock (party.Gate)
            {
                if (!party.Members.Contains(characterId)) return null;
                wasFollowing = party.Followers.Remove(characterId);
            }
            return party;
        }

        /// <summary>
        /// Who is following this character: his followers if he leads a party, nobody otherwise.
        /// It is asked on every step anybody takes, so a character in no party costs one lookup.
        /// </summary>
        public static IReadOnlyList<long> FollowersOf(long leaderId)
        {
            var party = Of(leaderId);
            if (party == null) return Array.Empty<long>();

            lock (party.Gate)
            {
                if (party.LeaderId != leaderId || party.Followers.Count == 0) return Array.Empty<long>();
                return party.Followers.Where(f => f != leaderId && party.Members.Contains(f)).ToList();
            }
        }

        /// <summary>Everybody stops following at once. Returns who was.</summary>
        public static IReadOnlyList<long> CutFollowers(Party party)
        {
            lock (party.Gate)
            {
                var were = party.Followers.ToList();
                party.Followers.Clear();
                return were;
            }
        }
    }
}

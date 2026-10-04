using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Following the party leader: a member asks to (imh) or stops (imo), and every move of the
    /// leader reaches whoever follows him. The messages, and the frames they were measured in, are
    /// in <see cref="PartyFollowProtocol"/>.
    /// </summary>
    /// <remarks>
    /// ─── The member's client does the walking ─────────────────────────────────────────────
    ///
    /// The server never moves the member. It tells his client where the leader is (ikv) and the
    /// client walks there, sending the ordinary jrw, jqi and jqk -- the jqk with f3 = 1, the
    /// autopilot's mark -- and getting the ordinary answers. So there is nothing to send beyond
    /// the ikv, and it has to go at two moments, both measured in "Grupos/con grupo seguir
    /// desplazamiento del lider...":
    ///
    /// <code>
    ///   the leader walks                 jsj to the map, then ikv    frames 32-33, 52-54, 608-609
    ///   the leader walks off an edge     jsd + kmu to the old map,   frames 101-103, 122-124,
    ///                                    then ikv with the new map   156-158, 180-182, 211-213
    ///                                    and the cell he landed on
    /// </code>
    ///
    /// Those are <see cref="LeaderMovedAsync"/>, called from <see cref="WorldMoveHandler"/> after
    /// the jsj and from <see cref="SessionRegistry.AnunciarMudanzaAsync"/> after the kmu.
    ///
    /// ─── A zaap ends the following ────────────────────────────────────────────────────────
    ///
    /// The real server does not take the member along through a zaap. In the same segment as the
    /// kmu that takes the leader off the old map, 3.4 s after his iwn on the zaap, the member gets
    /// an EMPTY imk -- following off -- and no jsd: there is no way out to walk. His client answers
    /// imo, and the server says 1661, ika and inb (frames 244-250). Following comes back when it
    /// is switched on again (imk 1 at frame 268, 14.7 s later): the client asks imh, the ikv of the
    /// answer puts the leader at [5,-18], far from [1,-32], and the client walks the eighteen maps
    /// on its own. That is <see cref="LeaderTravelledAsync"/>, called by the zaap window for all
    /// it offers: zaaps, zaapis and anomalies.
    ///
    /// Every other map change that is not a walk -- .teleport, a map element, a house, a dungeon
    /// door -- sends the ikv like an edge does, with the map and cell he landed on. INFERRED: none
    /// is in a capture. It is the plain meaning of the ikv, "the leader is here now", and the
    /// capture shows the client walking to a leader however far away the ikv puts him; cutting
    /// the following instead would drop it on every staircase.
    ///
    /// ─── What is not measured ─────────────────────────────────────────────────────────────
    ///
    /// Everything here is seen from the member's side. What turns following ON on the real server
    /// -- the imk 1 of frames 9 and 268 -- is not in any capture: it happens on the leader's
    /// client, and before it, at frames 4-5, the leader walks and leaves with no ikv to a member
    /// who had asked to follow. So the real server seems to keep a switch on the leader's side,
    /// and the message that flips it has never been recorded. Here there is no such switch: a
    /// member who asks to follow is followed. That departs from frames 4-5 on purpose, and it is
    /// the reason nothing here ever sends imk 1.
    ///
    /// Also inferred: the 1664 to the leader on imo, and cutting the followers when the leader
    /// changes (promotion, or the leader leaving). The ima of "nombrar a otro jugador jefe" gets
    /// the same empty imk the zaap sends, which fits following being switched off with the
    /// leader; the followers are sent it too, so their clients stop instead of walking after
    /// somebody who no longer leads.
    /// </remarks>
    public static class PartyFollowHandler
    {
        /// <summary>One frame for one character.</summary>
        public readonly record struct Delivery(long To, byte[] Frame);

        /// <summary>
        /// What the handler needs of the leader: who he is and where. A <see cref="MapId"/> of 0
        /// means his position is not known -- he is not in the world -- and then no ikv goes.
        /// </summary>
        public sealed record LeaderView(long Id, string Name, long MapId, int CellId, MapInfo? Map)
        {
            public static LeaderView Of(GameSession session)
                => new LeaderView(session.CharacterId, session.State.CharacterName,
                                  session.State.MapId, session.State.CellId,
                                  MapManager.GetMapInfo(session.State.MapId));
        }

        // ─── imh: follow the leader ─────────────────────────────────────────────

        public static async Task FollowAsync(NetworkStream stream, byte[] payload)
        {
            var me = SessionContext.Current;
            var plan = PlanFollow(me.CharacterId, me.State.CharacterName,
                                  ConnectionProtocol.RequestId(payload), Live);
            await DeliverAsync(stream, me.CharacterId, plan);

            Console.WriteLine($"[Party] {me.State.CharacterName} follows the party leader.");
        }

        /// <summary>
        /// The answer to an imh, and the notice to the leader. In the capture's order: 1662, ikv,
        /// iln -- all three in one instant -- and then the 1663 to the leader.
        /// </summary>
        /// <remarks>
        /// The iln goes whatever happens: it is the answer to a request, and a request left
        /// without one leaves the client waiting. The capture only has imh that succeed, so what
        /// the real server answers to somebody with nobody to follow is not known; here it is the
        /// bare iln.
        ///
        /// The 1663 only goes the first time: asked again while already following -- which the
        /// client does each time following is switched on, frames 0 and 10 -- the leader has
        /// already been told.
        /// </remarks>
        internal static List<Delivery> PlanFollow(long memberId, string memberName, long requestId,
                                                  Func<long, LeaderView?> leaderOf)
        {
            var plan = new List<Delivery>();
            var party = Parties.Follow(memberId, out bool isNew);
            var leader = party == null ? null : leaderOf(party.LeaderId);

            if (leader != null)
            {
                plan.Add(new Delivery(memberId, PartyFollowProtocol.BuildFollowing(leader.Name)));
                if (leader.MapId > 0)
                {
                    plan.Add(new Delivery(memberId, PartyFollowProtocol.BuildLeaderPosition(
                        leader.Id, leader.MapId, leader.Map, leader.CellId)));
                }
            }

            plan.Add(new Delivery(memberId, PartyFollowProtocol.BuildFollowAnswer(requestId)));

            if (leader != null && isNew)
                plan.Add(new Delivery(leader.Id, PartyFollowProtocol.BuildFollowsYou(memberName)));

            return plan;
        }

        // ─── imo: stop following ────────────────────────────────────────────────

        public static async Task UnfollowAsync(NetworkStream stream, byte[] payload)
        {
            var me = SessionContext.Current;
            var plan = PlanUnfollow(me.CharacterId, me.State.CharacterName,
                                    ConnectionProtocol.RequestId(payload), Live);
            await DeliverAsync(stream, me.CharacterId, plan);

            Console.WriteLine($"[Party] {me.State.CharacterName} stops following the party leader.");
        }

        /// <summary>
        /// The answer to an imo: 1661, ika with the member's own id, inb -- frames 248 to 250 --
        /// and the 1664 to the leader, which is inferred.
        /// </summary>
        /// <remarks>
        /// It answers even when the server had already stopped counting him as a follower,
        /// because that is exactly the zaap: the server cuts the following, sends the empty imk,
        /// and the imo that comes back still gets the 1661 and the ika in the capture.
        /// </remarks>
        internal static List<Delivery> PlanUnfollow(long memberId, string memberName, long requestId,
                                                    Func<long, LeaderView?> leaderOf)
        {
            var plan = new List<Delivery>();
            var party = Parties.Unfollow(memberId, out _);
            var leader = party == null || party.LeaderId == memberId ? null : leaderOf(party.LeaderId);

            if (leader != null)
            {
                plan.Add(new Delivery(memberId, PartyFollowProtocol.BuildStoppedFollowing(leader.Name)));
                plan.Add(new Delivery(memberId, PartyFollowProtocol.BuildFollowEnded(memberId)));
            }

            plan.Add(new Delivery(memberId, PartyFollowProtocol.BuildUnfollowAnswer(requestId)));

            if (leader != null)
                plan.Add(new Delivery(leader.Id, PartyFollowProtocol.BuildStoppedFollowingYou(memberName)));

            return plan;
        }

        // ─── The leader moves ───────────────────────────────────────────────────

        /// <summary>
        /// The leader has walked, or landed on another map: his followers are told where he is
        /// now. Anybody who leads nobody costs one dictionary lookup.
        /// </summary>
        public static Task LeaderMovedAsync(GameSession leader)
        {
            if (leader == null || leader.CharacterId <= 0) return Task.CompletedTask;
            if (Parties.FollowersOf(leader.CharacterId).Count == 0) return Task.CompletedTask;
            return DeliverAsync(null, 0, PlanLeaderMoved(LeaderView.Of(leader)));
        }

        /// <summary>The same ikv to each follower, wherever each one is.</summary>
        internal static List<Delivery> PlanLeaderMoved(LeaderView leader)
        {
            var plan = new List<Delivery>();
            if (leader.MapId <= 0) return plan;

            var followers = Parties.FollowersOf(leader.Id);
            if (followers.Count == 0) return plan;

            byte[] where = PartyFollowProtocol.BuildLeaderPosition(
                leader.Id, leader.MapId, leader.Map, leader.CellId);
            foreach (long follower in followers) plan.Add(new Delivery(follower, where));
            return plan;
        }

        /// <summary>
        /// The leader is travelling by zaap: following ends, and each follower gets the empty imk.
        /// Has to go BEFORE the kmu the old map gets, which is the order of frames 245-246; with
        /// nobody following any more, the ikv of the move announcement then goes to nobody.
        /// </summary>
        public static Task LeaderTravelledAsync(GameSession leader)
        {
            if (leader == null || leader.CharacterId <= 0) return Task.CompletedTask;
            return DeliverAsync(null, 0, PlanLeaderTravelled(leader.CharacterId));
        }

        /// <summary>
        /// Cuts the leader's followers and says so to each. Nothing when he leads nobody: a member
        /// who takes a zaap on his own is still following, and the next ikv sends him back.
        /// </summary>
        internal static List<Delivery> PlanLeaderTravelled(long leaderId)
        {
            var party = Parties.Of(leaderId);
            if (party == null || party.LeaderId != leaderId) return new List<Delivery>();
            return PlanCut(party);
        }

        /// <summary>
        /// The party changes leader: whoever followed the old one stops. INFERRED, see the remarks
        /// of the class; called after the ilx that announces the new leader.
        /// </summary>
        public static Task LeaderChangedAsync(Parties.Party party)
            => party == null ? Task.CompletedTask : DeliverAsync(null, 0, PlanCut(party));

        private static List<Delivery> PlanCut(Parties.Party party)
        {
            var plan = new List<Delivery>();
            foreach (long follower in Parties.CutFollowers(party))
                plan.Add(new Delivery(follower, PartyFollowProtocol.BuildFollowSwitch(false)));
            return plan;
        }

        // ─── Delivery ───────────────────────────────────────────────────────────

        /// <summary>
        /// The leader as he is now: from his session when he is in the world, with only his name
        /// from the database when he is not.
        /// </summary>
        private static LeaderView? Live(long characterId)
        {
            var session = SessionRegistry.FindByCharacter(characterId);
            if (session != null && session.IsInWorld) return LeaderView.Of(session);

            var character = DatabaseManager.GetCharacterById(characterId);
            return character == null ? null : new LeaderView(characterId, character.Name, 0, 0, null);
        }

        /// <summary>
        /// Sends a plan in its order: what is for the one who asked goes down his own socket, the
        /// rest to each character's session. Somebody who is gone is skipped, not an error.
        /// </summary>
        private static async Task DeliverAsync(NetworkStream? stream, long requester, List<Delivery> plan)
        {
            foreach (var delivery in plan)
            {
                try
                {
                    if (stream != null && delivery.To == requester)
                    {
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, delivery.Frame);
                        continue;
                    }

                    var session = SessionRegistry.FindByCharacter(delivery.To);
                    if (session != null) await session.SendAsync(delivery.Frame);
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[Party] Could not send to {delivery.To}: {ex.Message}");
                }
            }
        }
    }
}

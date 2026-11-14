using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Network
{
    /// <summary>
    /// Following the party leader across maps, against the frames of the real server.
    /// </summary>
    /// <remarks>
    /// Every hex string here is a whole frame copied out of "Grupos/con grupo seguir
    /// desplazamiento del lider en mapas cercanos y a traves de un zaap", recorded from the
    /// member's client: Leader (290000000003) leads, the member is 300000000001. The frame numbers
    /// are those of <c>hilo.tramas</c> on that file. The one 1663 comes from the leader's side, in
    /// "Grupos/invitar otro jugador a mi grupo y que acepte invitacion".
    ///
    /// The decision tests use ids of their own, far from any real character, because the parties
    /// are static state.
    /// </remarks>
    public class PartyFollowTests
    {
        private const long Leader = 290000000003;
        private const long Member = 300000000001;

        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        private static MapInfo Map(long id, int x, int y, int subArea)
            => new MapInfo { MapId = id, PosX = x, PosY = y, SubAreaId = subArea };

        // ─── The frames ─────────────────────────────────────────────────────────

        /// <summary>The leader's position after a walk and after each kind of map change.</summary>
        [Theory]
        // frame 2: the answer to the first imh, the leader standing on 415 of [1,-32]
        [InlineData(120062979L, 1, -32, 30, 415,
            "0a390a370a13747970652e616e6b616d612e636f6d2f696b7612200883a8e3aab8081a14088388a0391001201e28e0ffffffffffffffff01209f03")]
        // frame 33: behind his jsj 406 -> 46, same map as the member
        [InlineData(120063491L, 2, -32, 30, 46,
            "0a380a360a13747970652e616e6b616d612e636f6d2f696b76121f0883a8e3aab8081a1408838ca0391002201e28e0ffffffffffffffff01202e")]
        // frame 103: behind the jsd (up) and kmu, landed on 538 of [2,-33]
        [InlineData(120063490L, 2, -33, 30, 538,
            "0a390a370a13747970652e616e6b616d612e636f6d2f696b7612200883a8e3aab8081a1408828ca0391002201e28dfffffffffffffffff01209a04")]
        // frame 182: behind the jsd (left) and kmu, landed on 265 of [1,-33]
        [InlineData(120062978L, 1, -33, 30, 265,
            "0a390a370a13747970652e616e6b616d612e636f6d2f696b7612200883a8e3aab8081a14088288a0391001201e28dfffffffffffffffff01208902")]
        // frame 271: after the zaap, the leader at [5,-18] in another subarea
        [InlineData(191105026L, 5, -18, 95, 260,
            "0a390a370a13747970652e616e6b616d612e636f6d2f696b7612200883a8e3aab8081a14088290905b1005205f28eeffffffffffffffff01208402")]
        public void The_leader_position_is_the_captured_ikv(long mapId, int x, int y, int subArea,
                                                              int cell, string captured)
        {
            Assert.Equal(captured, Hex(PartyFollowProtocol.BuildLeaderPosition(
                Leader, mapId, Map(mapId, x, y, subArea), cell)));
        }

        [Fact]
        public void Following_switched_on_and_off_are_the_two_captured_imk()
        {
            // frame 9 (on, 0801) and frame 245 (off, no payload at all, next to the zaap's kmu)
            Assert.Equal("0a1b0a190a13747970652e616e6b616d612e636f6d2f696d6b12020801",
                         Hex(PartyFollowProtocol.BuildFollowSwitch(true)));
            Assert.Equal("0a170a150a13747970652e616e6b616d612e636f6d2f696d6b",
                         Hex(PartyFollowProtocol.BuildFollowSwitch(false)));
        }

        [Fact]
        public void The_answers_to_imh_and_imo_carry_the_request_id_on_root_field_three()
        {
            // The member's imh of frame 0, and the iln and inb of frames 3 and 250.
            byte[] imh = Convert.FromHexString(
                "12220a150a13747970652e616e6b616d612e636f6d2f696d6810ffffffffffffffffff01");
            long request = ConnectionProtocol.RequestId(imh);
            Assert.Equal(-1, request);
            Assert.NotNull(ConnectionProtocol.ReadPayload(imh, Op.Imh));

            Assert.Equal("1a220a150a13747970652e616e6b616d612e636f6d2f696c6e10ffffffffffffffffff01",
                         Hex(PartyFollowProtocol.BuildFollowAnswer(request)));
            Assert.Equal("1a220a150a13747970652e616e6b616d612e636f6d2f696e6210ffffffffffffffffff01",
                         Hex(PartyFollowProtocol.BuildUnfollowAnswer(request)));
        }

        [Fact]
        public void The_notices_are_the_captured_lqn_and_ika()
        {
            // frame 1 (1662), frame 248 (1661), frame 249 (ika with the member's own id)
            Assert.Equal("0a240a220a13747970652e616e6b616d612e636f6d2f6c716e120b10fe0c22064c6561646572",
                         Hex(PartyFollowProtocol.BuildFollowing("Leader")));
            Assert.Equal("0a240a220a13747970652e616e6b616d612e636f6d2f6c716e120b10fd0c22064c6561646572",
                         Hex(PartyFollowProtocol.BuildStoppedFollowing("Leader")));
            Assert.Equal("0a200a1e0a13747970652e616e6b616d612e636f6d2f696b6112070881f092cbdd08",
                         Hex(PartyFollowProtocol.BuildFollowEnded(Member)));

            // frame 10 of "invitar otro jugador a mi grupo y que acepte invitacion", leader's side
            Assert.Equal("0a280a260a13747970652e616e6b616d612e636f6d2f6c716e120f10ff0c220a466f6c6c6f7765722d31",
                         Hex(PartyFollowProtocol.BuildFollowsYou("Follower-1")));
        }

        [Fact]
        public void The_leader_leaving_by_an_edge_is_the_captured_jsd_and_kmu()
        {
            // frames 101-102: up (6); frame 156: down (2); frames 5-6: east, which is 0 and so no
            // f3 at all -- the leader walking from [1,-32] to [2,-32].
            Assert.Equal("0a220a200a13747970652e616e6b616d612e636f6d2f6a736412091083a8e3aab8081806",
                         Hex(ConnectionProtocol.BuildActorLeft(Leader, 6)));
            Assert.Equal("0a220a200a13747970652e616e6b616d612e636f6d2f6a736412091083a8e3aab8081802",
                         Hex(ConnectionProtocol.BuildActorLeft(Leader, 2)));
            Assert.Equal("0a200a1e0a13747970652e616e6b616d612e636f6d2f6a736412071083a8e3aab808",
                         Hex(ConnectionProtocol.BuildActorLeft(Leader, 0)));
            Assert.Equal("0a200a1e0a13747970652e616e6b616d612e636f6d2f6b6d7512071083a8e3aab808",
                         Hex(ConnectionProtocol.BuildActorRemoved(Leader)));

            // And the member's own, before his jru: frame 110, the same way out.
            Assert.Equal("0a220a200a13747970652e616e6b616d612e636f6d2f6a736412091081f092cbdd081806",
                         Hex(ConnectionProtocol.BuildActorLeft(Member, 6)));
        }

        // ─── Who hears what when somebody leaves a map ──────────────────────────

        [Fact]
        public async Task A_party_mate_on_the_old_map_gets_the_way_out_and_then_the_kmu()
        {
            var notices = await DeparturePackets(partyMate: true, direction: 6);
            Assert.Equal(new[]
            {
                "0a220a200a13747970652e616e6b616d612e636f6d2f6a736412091083a8e3aab8081806",
                "0a200a1e0a13747970652e616e6b616d612e636f6d2f6b6d7512071083a8e3aab808",
            }, notices.Select(Hex).ToArray());
        }

        [Fact]
        public async Task A_jump_sends_no_way_out_even_to_the_party()
        {
            // frames 245-246: the leader takes the zaap and the member gets the empty imk and the
            // kmu; the imk is the follow handler's, and from the map there is only the kmu.
            var notices = await DeparturePackets(partyMate: true, direction: null);
            Assert.Equal(new[] { "0a200a1e0a13747970652e616e6b616d612e636f6d2f6b6d7512071083a8e3aab808" },
                         notices.Select(Hex).ToArray());
        }

        [Fact]
        public async Task Somebody_outside_the_party_only_sees_him_go()
        {
            var notices = await DeparturePackets(partyMate: false, direction: 4);
            Assert.Single(notices);
            Assert.Equal("0a200a1e0a13747970652e616e6b616d612e636f6d2f6b6d7512071083a8e3aab808", Hex(notices[0]));
        }

        private static async Task<IReadOnlyList<byte[]>> DeparturePackets(bool partyMate, int? direction)
        {
            // Give the real presence service a real party and the production codec;
            // expected captured frames above stay independent of the implementation.
            using var party = new Party(Leader, Member);
            var packets = new List<byte[]>();
            var target = new GameSession(new GameCodec(), packet => { packets.Add(packet); return Task.CompletedTask; });
            target.State.CharacterId = partyMate ? Member : Member + 1;
            Assert.Equal(1, await MapPresenceService.Live.RemoveAsync(new[] { target }, Leader, direction));
            return packets;
        }

        // ─── Who follows whom ───────────────────────────────────────────────────

        /// <summary>A party of a leader and members, all inside, and its undoing.</summary>
        private sealed class Party : IDisposable
        {
            public Parties.Party Value { get; }

            public Party(long leader, params long[] members)
            {
                Value = Parties.Create(leader);
                foreach (long member in members)
                {
                    Assert.True(Parties.Invite(Value, member, leader));
                    Assert.True(Parties.Accept(Value, member));
                }
            }

            public void Dispose() => Parties.Dissolve(Value);
        }

        private static PartyFollowHandler.LeaderView View(long id, string name, long mapId, int cell,
                                                          int x, int y, int subArea)
            => new PartyFollowHandler.LeaderView(id, name, mapId, cell, Map(mapId, x, y, subArea));

        [Fact]
        public void Asking_to_follow_is_answered_as_the_capture_and_the_leader_is_told()
        {
            const long leader = 8_100_000_000_001, member = 8_100_000_000_002;
            using var party = new Party(leader, member);
            var where = View(leader, "Leader", 120063491, 406, 2, -32, 30);

            var plan = PartyFollowHandler.PlanFollow(member, "Follower-1", -1,
                id => id == leader ? where : null);

            // frames 11, 12, 13 -- 1662, ikv, iln, in that order -- and then the 1663.
            Assert.Equal(new[] { member, member, member, leader }, plan.Select(d => d.To).ToArray());
            Assert.Equal("0a240a220a13747970652e616e6b616d612e636f6d2f6c716e120b10fe0c22064c6561646572",
                         Hex(plan[0].Frame));
            Assert.Equal("0a390a370a13747970652e616e6b616d612e636f6d2f696b7612200883a8e3aab8081a1408838ca0391002201e28e0ffffffffffffffff01209603",
                         Hex(PartyFollowProtocol.BuildLeaderPosition(Leader, 120063491, where.Map, 406)));
            Assert.Equal(Hex(PartyFollowProtocol.BuildLeaderPosition(leader, 120063491, where.Map, 406)),
                         Hex(plan[1].Frame));
            Assert.Equal("1a220a150a13747970652e616e6b616d612e636f6d2f696c6e10ffffffffffffffffff01",
                         Hex(plan[2].Frame));
            Assert.Equal(Hex(PartyFollowProtocol.BuildFollowsYou("Follower-1")), Hex(plan[3].Frame));

            Assert.Equal(new[] { member }, Parties.FollowersOf(leader));
        }

        /// <summary>The leader's «que me sigan» (ikb): on, the members not following get imk 1; off, the followers are cut.</summary>
        [Fact]
        public void The_leader_s_switch_turns_following_on_and_off()
        {
            const long leader = 8_100_000_000_061, follows = 8_100_000_000_062, idle = 8_100_000_000_063;
            using var party = new Party(leader, follows, idle);
            Assert.NotNull(Parties.Follow(follows, out _));

            var on = PartyFollowHandler.PlanLeaderSwitch(leader, true);
            Assert.Equal(new[] { idle }, on.Select(d => d.To).ToArray());
            Assert.Equal(Hex(PartyFollowProtocol.BuildFollowSwitch(true)), Hex(on[0].Frame));

            var off = PartyFollowHandler.PlanLeaderSwitch(leader, false);
            Assert.Equal(new[] { follows }, off.Select(d => d.To).ToArray());
            Assert.Equal(Hex(PartyFollowProtocol.BuildFollowSwitch(false)), Hex(off[0].Frame));
            Assert.Empty(Parties.FollowersOf(leader));

            // A member's switch is not heard.
            Assert.Empty(PartyFollowHandler.PlanLeaderSwitch(idle, true));
        }

        [Fact]
        public void Asking_again_gets_the_same_answer_and_the_leader_is_not_told_twice()
        {
            const long leader = 8_100_000_000_011, member = 8_100_000_000_012;
            using var party = new Party(leader, member);
            var where = View(leader, "Leader", 120063491, 406, 2, -32, 30);

            PartyFollowHandler.PlanFollow(member, "m", -1, _ => where);
            var again = PartyFollowHandler.PlanFollow(member, "m", -1, _ => where);

            // frames 0 and 10: the second imh comes with no imo in between and gets all three.
            Assert.Equal(new[] { member, member, member }, again.Select(d => d.To).ToArray());
        }

        [Fact]
        public void Nobody_but_a_member_can_follow_and_the_request_is_still_answered()
        {
            const long leader = 8_100_000_000_021, member = 8_100_000_000_022;
            const long invitee = 8_100_000_000_023, stranger = 8_100_000_000_024;
            using var party = new Party(leader, member);
            Assert.True(Parties.Invite(party.Value, invitee, leader));
            var where = View(leader, "L", 120063491, 406, 2, -32, 30);

            foreach (long who in new[] { leader, invitee, stranger })
            {
                var plan = PartyFollowHandler.PlanFollow(who, "x", -1, _ => where);
                Assert.Single(plan);
                Assert.Equal(who, plan[0].To);
                Assert.Equal(Hex(PartyFollowProtocol.BuildFollowAnswer(-1)), Hex(plan[0].Frame));
            }

            Assert.Empty(Parties.FollowersOf(leader));
        }

        [Fact]
        public void Only_the_followers_hear_where_the_leader_went()
        {
            const long leader = 8_100_000_000_031, follower = 8_100_000_000_032, idle = 8_100_000_000_033;
            using var party = new Party(leader, follower, idle);
            Assert.NotNull(Parties.Follow(follower, out _));

            // The walk off the top of [2,-32], landing on 538 of [2,-33]: frame 103.
            var plan = PartyFollowHandler.PlanLeaderMoved(View(leader, "L", 120063490, 538, 2, -33, 30));

            Assert.Single(plan);
            Assert.Equal(follower, plan[0].To);
            Assert.Equal(Hex(PartyFollowProtocol.BuildLeaderPosition(
                             leader, 120063490, Map(120063490, 2, -33, 30), 538)),
                         Hex(plan[0].Frame));
        }

        [Fact]
        public void A_follower_walking_tells_nobody()
        {
            const long leader = 8_100_000_000_041, follower = 8_100_000_000_042;
            using var party = new Party(leader, follower);
            Assert.NotNull(Parties.Follow(follower, out _));

            Assert.Empty(PartyFollowHandler.PlanLeaderMoved(View(follower, "F", 120063491, 18, 2, -32, 30)));
            Assert.Empty(Parties.FollowersOf(follower));
            Assert.Empty(Parties.FollowersOf(8_100_000_000_049));   // in no party at all
        }

        [Fact]
        public void A_zaap_cuts_the_following_and_the_imo_that_comes_back_is_answered_in_full()
        {
            const long leader = 8_100_000_000_051, follower = 8_100_000_000_052;
            using var party = new Party(leader, follower);
            Assert.NotNull(Parties.Follow(follower, out _));

            // frame 245: the empty imk, to the follower only.
            var cut = PartyFollowHandler.PlanLeaderTravelled(leader);
            Assert.Single(cut);
            Assert.Equal(follower, cut[0].To);
            Assert.Equal("0a170a150a13747970652e616e6b616d612e636f6d2f696d6b", Hex(cut[0].Frame));

            // From then on his walks reach nobody...
            Assert.Empty(Parties.FollowersOf(leader));
            Assert.Empty(PartyFollowHandler.PlanLeaderMoved(View(leader, "L", 191105026, 260, 5, -18, 95)));

            // ...and the imo the member's client answers with still gets 1661, ika and inb
            // (frames 248-250), then the inferred 1664 to the leader.
            var plan = PartyFollowHandler.PlanUnfollow(follower, "Follower-1", -1,
                id => id == leader ? View(leader, "Leader", 191105026, 260, 5, -18, 95) : null);
            Assert.Equal(new[] { follower, follower, follower, leader }, plan.Select(d => d.To).ToArray());
            Assert.Equal(Hex(PartyFollowProtocol.BuildStoppedFollowing("Leader")), Hex(plan[0].Frame));
            Assert.Equal(Hex(PartyFollowProtocol.BuildFollowEnded(follower)), Hex(plan[1].Frame));
            Assert.Equal(Hex(PartyFollowProtocol.BuildUnfollowAnswer(-1)), Hex(plan[2].Frame));
            Assert.Equal(Hex(PartyFollowProtocol.BuildStoppedFollowingYou("Follower-1")), Hex(plan[3].Frame));
        }

        [Fact]
        public void A_member_taking_a_zaap_on_his_own_keeps_following()
        {
            const long leader = 8_100_000_000_061, follower = 8_100_000_000_062;
            using var party = new Party(leader, follower);
            Assert.NotNull(Parties.Follow(follower, out _));

            Assert.Empty(PartyFollowHandler.PlanLeaderTravelled(follower));
            Assert.Equal(new[] { follower }, Parties.FollowersOf(leader));
        }

        // ─── Following another member (imr / ikx) ───────────────────────────────

        [Fact]
        public void Following_a_member_is_the_leader_s_following_with_him_in_front()
        {
            const long leader = 8_100_000_000_071, member = 8_100_000_000_072, follower = 8_100_000_000_073;
            using var party = new Party(leader, member, follower);
            var where = View(member, "Member-Name", 120063491, 406, 2, -32, 30);

            var plan = PartyFollowHandler.PlanFollow(follower, "Follower-1", -1,
                id => id == member ? where : null, member);

            Assert.Equal(new[] { follower, follower, follower, member }, plan.Select(d => d.To).ToArray());
            Assert.Equal(Hex(PartyFollowProtocol.BuildFollowing("Member-Name")), Hex(plan[0].Frame));
            Assert.Equal(Hex(PartyFollowProtocol.BuildLeaderPosition(member, 120063491, where.Map, 406)),
                         Hex(plan[1].Frame));
            Assert.Equal(Hex(PartyFollowProtocol.BuildFollowsYou("Follower-1")), Hex(plan[3].Frame));

            // He is that member's follower, not the leader's: the member's steps reach him.
            Assert.Equal(new[] { follower }, Parties.FollowersOf(member));
            Assert.Empty(Parties.FollowersOf(leader));
            var moved = PartyFollowHandler.PlanLeaderMoved(View(member, "M", 120063490, 538, 2, -33, 30));
            Assert.Equal(new[] { follower }, moved.Select(d => d.To).ToArray());
        }

        [Fact]
        public void Nobody_follows_himself_or_somebody_outside_the_party()
        {
            const long leader = 8_100_000_000_081, member = 8_100_000_000_082, stranger = 8_100_000_000_083;
            using var party = new Party(leader, member);

            Assert.Null(Parties.Follow(member, out _, member));
            Assert.Null(Parties.Follow(member, out _, stranger));
            // The leader may follow a member: he only cannot follow whoever leads.
            Assert.NotNull(Parties.Follow(leader, out _, member));
            Assert.Equal(new[] { leader }, Parties.FollowersOf(member));
        }

        [Fact]
        public void Stopping_tells_the_member_he_followed_and_a_new_leader_does_not_cut_him()
        {
            const long leader = 8_100_000_000_091, member = 8_100_000_000_092;
            const long byName = 8_100_000_000_093, byLead = 8_100_000_000_094;
            using var party = new Party(leader, member, byName, byLead);
            Assert.NotNull(Parties.Follow(byName, out _, member));
            Assert.NotNull(Parties.Follow(byLead, out _));

            // The party changes leader: only those following whoever leads are cut.
            Assert.True(Parties.Promote(party.Value, member));
            Assert.Equal(new[] { byLead }, Parties.CutFollowers(party.Value));
            Assert.Equal(new[] { byName }, Parties.FollowersOf(member));

            // ikx: the 1664 goes to the member he followed, not to the leader.
            var plan = PartyFollowHandler.PlanUnfollow(byName, "Follower-1", -1,
                id => View(id, id == member ? "Member-Name" : "Other", 120063491, 406, 2, -32, 30));
            Assert.Equal(new[] { byName, byName, byName, member }, plan.Select(d => d.To).ToArray());
            Assert.Equal(Hex(PartyFollowProtocol.BuildStoppedFollowing("Member-Name")), Hex(plan[0].Frame));
            Assert.Empty(Parties.FollowersOf(member));
        }

        [Fact]
        public void A_member_leaving_frees_whoever_followed_him_and_a_zaap_cuts_his_followers()
        {
            const long leader = 8_100_000_000_101, member = 8_100_000_000_102, follower = 8_100_000_000_103;
            using var party = new Party(leader, member, follower);
            Assert.NotNull(Parties.Follow(follower, out _, member));

            var cut = PartyFollowHandler.PlanLeaderTravelled(member);
            Assert.Equal(new[] { follower }, cut.Select(d => d.To).ToArray());
            Assert.Empty(Parties.FollowersOf(member));

            Assert.NotNull(Parties.Follow(follower, out _, member));
            Parties.Leave(party.Value, member);
            Assert.Empty(party.Value.Followers);
        }

        [Fact]
        public void An_imo_from_outside_any_party_gets_only_its_answer()
        {
            var plan = PartyFollowHandler.PlanUnfollow(8_100_000_000_071, "x", -1, _ => null);
            Assert.Single(plan);
            Assert.Equal(Hex(PartyFollowProtocol.BuildUnfollowAnswer(-1)), Hex(plan[0].Frame));
        }

        [Fact]
        public async System.Threading.Tasks.Task A_new_leader_ends_the_following()
        {
            const long leader = 8_100_000_000_081, a = 8_100_000_000_082, b = 8_100_000_000_083;
            using var party = new Party(leader, a, b);
            Assert.NotNull(Parties.Follow(a, out _));
            Assert.NotNull(Parties.Follow(b, out _));

            // b takes over: he is never his own follower, and a stops once the change is told.
            Assert.True(Parties.Promote(party.Value, b));
            Assert.Equal(new[] { a }, Parties.FollowersOf(b));

            await PartyFollowHandler.LeaderChangedAsync(party.Value);
            Assert.Empty(Parties.FollowersOf(b));
        }

        [Fact]
        public void A_follower_who_leaves_the_party_is_no_longer_followed_by_the_server()
        {
            const long leader = 8_100_000_000_091, a = 8_100_000_000_092, b = 8_100_000_000_093;
            using var party = new Party(leader, a, b);
            Assert.NotNull(Parties.Follow(a, out _));
            Assert.NotNull(Parties.Follow(b, out _));

            Parties.Leave(party.Value, a);
            Assert.Equal(new[] { b }, Parties.FollowersOf(leader));
        }

        // ─── Against the world data ─────────────────────────────────────────────

        /// <summary>
        /// The position in the ikv is the map's own row: the six maps the capture carries, read
        /// from MapPositions, give back the captured bytes. Skips without world.db, which is never
        /// in git; any world.db will do, the one of datos/world.zip included, since positions
        /// are not something a migration touches.
        /// </summary>
        [Fact]
        public void The_captured_positions_are_the_maps_of_the_world_database()
        {
            string path = Paths.WorldDb;
            if (!File.Exists(path)) return;

            var captured = new Dictionary<long, (int Cell, string Frame)>
            {
                [120062979] = (415, "0a390a370a13747970652e616e6b616d612e636f6d2f696b7612200883a8e3aab8081a14088388a0391001201e28e0ffffffffffffffff01209f03"),
                [120063491] = (46, "0a380a360a13747970652e616e6b616d612e636f6d2f696b76121f0883a8e3aab8081a1408838ca0391002201e28e0ffffffffffffffff01202e"),
                [120063490] = (538, "0a390a370a13747970652e616e6b616d612e636f6d2f696b7612200883a8e3aab8081a1408828ca0391002201e28dfffffffffffffffff01209a04"),
                [120063489] = (538, "0a390a370a13747970652e616e6b616d612e636f6d2f696b7612200883a8e3aab8081a1408818ca0391002201e28deffffffffffffffff01209a04"),
                [120062978] = (265, "0a390a370a13747970652e616e6b616d612e636f6d2f696b7612200883a8e3aab8081a14088288a0391001201e28dfffffffffffffffff01208902"),
                [191105026] = (260, "0a390a370a13747970652e616e6b616d612e636f6d2f696b7612200883a8e3aab8081a14088290905b1005205f28eeffffffffffffffff01208402"),
            };

            var rows = new Dictionary<long, MapInfo>();
            try
            {
                using var connection = new SqliteConnection(
                    "Data Source=" + path.Replace('\\', '/') + ";Mode=ReadOnly;Pooling=False");
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT MapId, PosX, PosY, SubAreaId FROM MapPositions WHERE MapId IN (" +
                                      string.Join(",", captured.Keys) + ");";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    long id = reader.GetInt64(0);
                    rows[id] = Map(id, reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
                }
            }
            catch (SqliteException)
            {
                // Locked by a running server or a publish: a skip, not a failure.
                return;
            }

            foreach (var (mapId, (cell, frame)) in captured)
            {
                Assert.True(rows.ContainsKey(mapId), $"map {mapId} is not in MapPositions");
                Assert.Equal(frame, Hex(PartyFollowProtocol.BuildLeaderPosition(Leader, mapId, rows[mapId], cell)));
            }
        }
    }
}

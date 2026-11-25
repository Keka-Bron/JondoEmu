using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol.Game.Wire;
using Game = Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Coming into somebody else's fight during its placement, and the fight as the map shows it.
    /// </summary>
    [Collection("MapManager")]   // shares map 188744196 with FightStartOnceTests and the placement tests
    public class FightJoinTests
    {
        private const long PartyLeader = 290000000003;
        private const long Recorder = 300000000001;
        private const long Group = -20002;
        private const long Fight487 = 487;

        private static Fighter Person(long id, string name, int level)
            => new Fighter { Id = id, Name = name, Level = level, MaxHP = 100, CurrentHP = 100 };

        private static Fighter Monster(long id, int monster, int gradeIndex)
            => new Fighter { Id = id, IsMonster = true, MonsterId = monster, GradeIndex = gradeIndex,
                             MaxHP = 10, CurrentHP = 10 };

        /// <summary>Two sides of 12 cells, the follow capture's kba of frame 167.</summary>
        private static FightInstance FollowFight()
        {
            var fight = new FightInstance(Fight487, 188744196, 191105032) { DefenderLeaderId = Group };
            fight.SetPlacementCells(
                new[] { 317, 357, 359, 399, 318, 386, 330, 344, 346, 371, 373, 400 },
                new[] { 187, 203, 228, 270, 231, 271, 188, 202, 216, 242, 256, 257 });
            fight.AddPlayer(Person(PartyLeader, "Leader", 447));
            fight.AddMonster(Monster(-1, 492, 0));
            return fight;
        }

        // ════════════════════════════════════════════════════════════════════
        //  Who fits where
        // ════════════════════════════════════════════════════════════════════

        /// <summary>The first free cell of his side, in the kba's order: the leader keeps his.</summary>
        [Fact]
        public void A_joiner_takes_the_first_free_cell_of_his_side()
        {
            var fight = FollowFight();
            var recorder = Person(Recorder, "Hero-Example", 354);

            Assert.True(fight.JoinTeam(recorder, FightInstance.Azules, FightHandler.MaxPeoplePerTeam));
            Assert.Equal(FightInstance.Azules, recorder.TeamId);
            Assert.Equal(357, recorder.CellId);
            Assert.Equal(2, fight.PeopleIn(FightInstance.Azules));
            Assert.Contains(recorder, fight.TurnOrder);

            // And nobody is moved on top of anybody during the placement.
            fight.ChangePlacementCell(Recorder, 317);
            Assert.Equal(357, recorder.CellId);
        }

        [Fact]
        public void Joining_is_refused_once_the_fight_has_started()
        {
            var fight = FollowFight();
            fight.StartFight();

            Assert.Equal(FightInstance.JoinRefusal.NotInPlacement,
                         fight.CanJoin(FightInstance.Azules, FightHandler.MaxPeoplePerTeam));
            Assert.False(fight.JoinTeam(Person(Recorder, "Hero-Example", 354), FightInstance.Azules,
                                        FightHandler.MaxPeoplePerTeam));
            Assert.Equal(1, fight.PeopleIn(FightInstance.Azules));

            // The same answer through the kay's door, for somebody standing on the map.
            var recorder = new SessionState { CharacterId = Recorder, MapId = 188744196 };
            Assert.Equal("NotInPlacement", FightHandler.WhyNotJoin(fight, PartyLeader, recorder, out _));
        }

        [Fact]
        public void A_full_side_is_refused()
        {
            var fight = FollowFight();
            for (int i = 1; i < FightHandler.MaxPeoplePerTeam; i++)
            {
                Assert.True(fight.JoinTeam(Person(1000 + i, "P" + i, 100), FightInstance.Azules,
                                           FightHandler.MaxPeoplePerTeam));
            }

            Assert.Equal(8, fight.PeopleIn(FightInstance.Azules));
            Assert.Equal(FightInstance.JoinRefusal.TeamFull,
                         fight.CanJoin(FightInstance.Azules, FightHandler.MaxPeoplePerTeam));
            Assert.False(fight.JoinTeam(Person(Recorder, "Hero-Example", 354), FightInstance.Azules,
                                        FightHandler.MaxPeoplePerTeam));

            var recorder = new SessionState { CharacterId = Recorder, MapId = 188744196 };
            Assert.Equal("TeamFull", FightHandler.WhyNotJoin(fight, PartyLeader, recorder, out _));
        }

        [Fact]
        public void A_side_without_a_free_cell_is_full_too()
        {
            var fight = new FightInstance(1, 100, 200) { DefenderLeaderId = Group };
            fight.SetPlacementCells(new[] { 10, 11 }, new[] { 400, 401 });
            fight.AddPlayer(Person(1, "A", 1));
            fight.AddMonster(Monster(-1, 492, 0));
            Assert.True(fight.JoinTeam(Person(2, "B", 1), FightInstance.Azules, 8));

            Assert.Equal(FightInstance.JoinRefusal.TeamFull, fight.CanJoin(FightInstance.Azules, 8));
            Assert.Equal(-1, fight.FreePlacementCell(FightInstance.Azules));
        }

        [Fact]
        public void Nobody_joins_the_monsters()
        {
            var fight = FollowFight();
            var recorder = new SessionState { CharacterId = Recorder, MapId = 188744196 };

            Assert.Equal(FightInstance.JoinRefusal.MonsterTeam,
                         fight.CanJoin(FightInstance.Rojos, FightHandler.MaxPeoplePerTeam));
            Assert.Equal("MonsterTeam", FightHandler.WhyNotJoin(fight, Group, recorder, out int team));
            Assert.Equal(FightInstance.Rojos, team);
        }

        [Fact]
        public void The_kay_is_checked_against_who_asks()
        {
            var fight = FollowFight();

            var onTheMap = new SessionState { CharacterId = Recorder, MapId = 188744196 };
            Assert.Null(FightHandler.WhyNotJoin(fight, PartyLeader, onTheMap, out int team));
            Assert.Equal(FightInstance.Azules, team);

            var elsewhere = new SessionState { CharacterId = Recorder, MapId = 188744197 };
            Assert.NotNull(FightHandler.WhyNotJoin(fight, PartyLeader, elsewhere, out _));

            var fighting = new SessionState { CharacterId = Recorder, MapId = 188744196, IsInFight = true };
            Assert.NotNull(FightHandler.WhyNotJoin(fight, PartyLeader, fighting, out _));

            Assert.NotNull(FightHandler.WhyNotJoin(fight, 42, onTheMap, out _));
            Assert.NotNull(FightHandler.WhyNotJoin(null, PartyLeader, onTheMap, out _));
        }

        /// <summary>What is left of the placement, in tenths, never below zero.</summary>
        [Fact]
        public void The_placement_counts_down_from_when_it_opened()
        {
            var fight = FollowFight();
            var opened = fight.PlacementOpenedUtc;

            Assert.Equal(442, fight.PlacementDecisecondsLeft(450, opened.AddMilliseconds(800)));
            Assert.Equal(403, fight.PlacementDecisecondsLeft(450, opened.AddMilliseconds(4_700)));
            Assert.Equal(0, fight.PlacementDecisecondsLeft(450, opened.AddSeconds(60)));
        }

        /// <summary>Somebody who leaves the placement is out of it, and his cell is free again.</summary>
        [Fact]
        public void Leaving_the_placement_frees_the_place()
        {
            var fight = FollowFight();
            fight.JoinTeam(Person(Recorder, "Hero-Example", 354), FightInstance.Azules, FightHandler.MaxPeoplePerTeam);

            Assert.True(fight.LeavePlacement(Recorder));
            Assert.Equal(-1, fight.EquipoDe(Recorder));
            Assert.Equal(357, fight.FreePlacementCell(FightInstance.Azules));
            Assert.False(fight.LeavePlacement(-1));
        }

        // ════════════════════════════════════════════════════════════════════
        //  The monster side of a dungeon follows the number of people
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// B frames 100-119 and 140-167: at every arrival the monsters go and come back with new
        /// ids below the old ones, on the same red cells in order.
        /// </summary>
        [Fact]
        public void Replacing_the_monsters_draws_new_ids_on_the_same_cells()
        {
            var fight = new FightInstance(471, 225181696, 225181697) { DefenderLeaderId = -20000 };
            fight.SetPlacementCells(new[] { 282, 241, 201, 214 }, new[] { 487, 444, 485, 486 });
            fight.AddPlayer(Person(1, "A", 1));
            for (int i = 0; i < 4; i++) fight.AddMonster(Monster(-1 - i, 7214 + i, 0));

            var (removed, added) = fight.ReplaceMonsters(4, (index, id, cell) => Monster(id, 7214 + index, 0));

            Assert.Equal(new long[] { -1, -2, -3, -4 }, removed.Select(m => m.Id));
            Assert.Equal(new long[] { -5, -6, -7, -8 }, added.Select(m => m.Id));
            Assert.Equal(new[] { 487, 444, 485, 486 }, added.Select(m => m.CellId));
            Assert.Equal(4, fight.Rojo.Count);
            Assert.All(fight.Rojo, m => Assert.Equal(FightInstance.Rojos, m.TeamId));
        }

        private static readonly int[] Jalatos = { 101, 134, 148, 149, 4822 };

        [Fact]
        public void A_dungeon_group_grows_with_the_people()
        {
            MobSpawnManager.EnsureMonsterData();
            var room = new MobSpawnManager.MobGroup
            {
                MobId = -3_500_000,
                Modular = true,
                Members = MobSpawnManager.ComposeRoom(Jalatos, Array.Empty<int>(), 0, 5, 121373185),
            };

            var fight = new FightInstance(9001, 121373185, 121373185) { DefenderLeaderId = room.MobId };
            fight.SetPlacementCells(Enumerable.Range(200, 8), Enumerable.Range(400, 8));
            fight.AddPlayer(Person(1, "A", 50));
            long id = -1;
            foreach (var member in MobSpawnManager.MembersFor(room, 1))
            {
                fight.AddMonster(FightHandler.BuildMonsterFighter(member, id--, 0));
            }
            Assert.Equal(4, fight.Rojo.Count);

            // Up to four people the room fights four -- rebuilt all the same, as the capture does.
            for (int people = 2; people <= 4; people++)
            {
                fight.JoinTeam(Person(people, "P" + people, 50), FightInstance.Azules, FightHandler.MaxPeoplePerTeam);
                var (removed, added) = FightHandler.RegrowMonsters(fight, room);
                Assert.Equal(4, removed.Count);
                Assert.Equal(4, added.Count);
            }

            // The fifth brings the fifth monster of the eight, the eighth the eighth, and no more.
            for (int people = 5; people <= 8; people++)
            {
                fight.JoinTeam(Person(people, "P" + people, 50), FightInstance.Azules, FightHandler.MaxPeoplePerTeam);
                var (_, added) = FightHandler.RegrowMonsters(fight, room);

                Assert.Equal(people, added.Count);
                Assert.Equal(people, fight.Rojo.Count(m => m.IsMonster));
                Assert.Equal(room.Members.Take(people).Select(m => m.Monster.Id), added.Select(m => m.MonsterId));
                Assert.Equal(fight.RedPlacementCells.Take(people), added.Select(m => m.CellId));
                Assert.All(added, m => Assert.True(m.MaxHP > 0));
            }

            // Every id handed out once: the new ones always below the old.
            Assert.Equal(fight.Rojo.Count, fight.Rojo.Select(m => m.Id).Distinct().Count());
            Assert.True(fight.Rojo.Max(m => m.Id) < -4);
        }

        /// <summary>An ordinary group is not touched: the follow capture's poutch is still -1.</summary>
        [Fact]
        public void An_ordinary_group_stays_as_it_is()
        {
            MobSpawnManager.EnsureMonsterData();
            var room = new MobSpawnManager.MobGroup
            {
                Members = MobSpawnManager.ComposeRoom(Jalatos, Array.Empty<int>(), 0, 5, 121373185).Take(1).ToList(),
            };
            var fight = FollowFight();
            fight.JoinTeam(Person(Recorder, "Hero-Example", 354), FightInstance.Azules, FightHandler.MaxPeoplePerTeam);

            var (removed, added) = FightHandler.RegrowMonsters(fight, room);

            Assert.Empty(removed);
            Assert.Empty(added);
            Assert.Equal(-1, fight.Rojo.Single().Id);
        }

        // ════════════════════════════════════════════════════════════════════
        //  The party: who follows in, who is ready with the leader
        // ════════════════════════════════════════════════════════════════════

        private static GameSession InWorld(long characterId, long mapId)
        {
            var session = GameSession.SinSocket();
            session.BindAccount(characterId % 1_000_000 + 1, 1);
            session.State.CharacterId = characterId;
            session.State.CharacterName = "C" + characterId;
            session.State.MapId = mapId;
            session.EnterWorld();
            Assert.True(SessionRegistry.Register(session));
            return session;
        }

        [Fact]
        public async Task Party_notice_is_typed_and_goes_to_waiting_members_but_not_to_the_fighter_or_followers()
        {
            const long leader = 8_101_000_001, follower = 8_101_000_002, stayer = 8_101_000_003, solo = 8_101_000_004;
            const long map = 188744196;
            var party = Parties.Create(leader);
            Parties.Invite(party, follower, leader);
            Parties.Accept(party, follower);
            Parties.Invite(party, stayer, leader);
            Parties.Accept(party, stayer);
            var leadCodec = new PartyCodec();
            var followerCodec = new PartyCodec();
            var stayerCodec = new PartyCodec();
            await using var lead = await Economy.ClientPipe.OpenAsync(leader, leader, map, codec: leadCodec);
            await using var follow = await Economy.ClientPipe.OpenAsync(follower, follower, map, codec: followerCodec);
            await using var stay = await Economy.ClientPipe.OpenAsync(stayer, stayer, map + 1, codec: stayerCodec);
            try
            {
                FightHandler.SetAutoOptions(follower, join: true, ready: false);
                follow.Session.State.PendingMovementMapId = map;
                var fight = new FightInstance(9102, map, 191105032) { Reglas = FightRules.Desafio };
                fight.SetPlacementCells(Enumerable.Range(300, 8), Enumerable.Range(180, 8));
                var boss = Person(leader, "Leader", 200);
                fight.AddPlayer(boss);
                await FightHandler.PartyAfterEntryAsync(fight, Person(solo, "Solo", 200), opening: true);
                Assert.Empty(stayerCodec.Frames);
                await FightHandler.PartyAfterEntryAsync(fight, boss, opening: true);

                var sent = Assert.Single(stayerCodec.Frames);
                Assert.Null(sent.Uid);
                var notice = Assert.IsType<Game.PartyMemberInFightEvent>(sent.Content);
                Assert.Equal(leader, notice.MemberId);
                Assert.Equal(9102, notice.FightId);
                Assert.Equal("Leader", notice.MemberName);
                Assert.Equal(Game.PartyMemberInFightCause.MemberChallenge, notice.Reason);
                Assert.Equal(0, notice.TimeBeforeFightStart);
                Assert.Equal(map, notice.StandardFightMap!.MapId);
                Assert.Equal(notice, new GameCodec().Decode(await Jondo.Protocol.NetworkMessage.ReadFrameAsync(stay.FromServer)
                    .WaitAsync(TimeSpan.FromSeconds(5))).Content);
                Assert.Empty(leadCodec.Frames);
                Assert.Empty(followerCodec.Frames);
            }
            finally
            {
                using (SessionContext.Push(follow.Session)) await FightHandler.AfterWalkAsync(follow.ToClient);
                FightHandler.SetAutoOptions(follower, join: false, ready: false);
                Parties.Dissolve(party);
            }
        }

        private sealed class PartyCodec : Game.IGameCodec
        {
            public List<Game.GameFrame> Frames { get; } = [];
            public byte[] Encode(Game.GameFrame frame) { Frames.Add(frame); return new GameCodec().Encode(frame); }
            public Game.GameFrame Decode(ReadOnlySpan<byte> frame) => throw new InvalidOperationException("A send cannot decode.");
        }

        [Fact]
        public void A_member_with_the_automatic_entry_follows_his_leader_in()
        {
            const long leader = 8_100_000_001, follower = 8_100_000_002, stayer = 8_100_000_003;
            const long map = 188744196;

            var party = Parties.Create(leader);
            Parties.Invite(party, follower, leader);
            Parties.Accept(party, follower);
            Parties.Invite(party, stayer, leader);
            Parties.Accept(party, stayer);
            var sessions = new[] { InWorld(leader, 0), InWorld(follower, map), InWorld(stayer, map) };
            try
            {
                FightHandler.SetAutoOptions(follower, join: true, ready: false);

                var fight = new FightInstance(9101, map, 191105032) { DefenderLeaderId = Group };
                fight.SetPlacementCells(Enumerable.Range(300, 8), Enumerable.Range(180, 8));
                var boss = Person(leader, "Leader", 200);
                fight.AddPlayer(boss);
                fight.AddMonster(Monster(-1, 492, 0));

                Assert.Equal(new[] { follower }, FightHandler.FollowersInto(fight, boss, opening: true));

                // Not from another map, not while fighting, and not behind a non-leader's manual join.
                sessions[1].State.MapId = map + 1;
                Assert.Empty(FightHandler.FollowersInto(fight, boss, opening: true));
                sessions[1].State.MapId = map;
                sessions[1].State.FightId = 55;
                Assert.Empty(FightHandler.FollowersInto(fight, boss, opening: true));
                sessions[1].State.FightId = 0;
                Assert.Empty(FightHandler.FollowersInto(fight, Person(stayer, "Stayer", 1), opening: false));

                // Nor once the placement is over.
                fight.StartFight();
                Assert.Empty(FightHandler.FollowersInto(fight, boss, opening: true));
            }
            finally
            {
                FightHandler.SetAutoOptions(follower, join: false, ready: false);
                foreach (var s in sessions) SessionRegistry.Unregister(s);
                Parties.Dissolve(party);
            }
        }

        [Fact]
        public void Only_the_original_member_opener_can_ready_the_party_leader()
        {
            const long leader = 8_210_000_001, opener = leader + 1, joiner = leader + 2;
            var party = Parties.Create(leader);
            try
            {
                foreach (long member in new[] { opener, joiner })
                {
                    Parties.Invite(party, member, leader);
                    Parties.Accept(party, member);
                }
                FightHandler.SetAutoOptions(leader, join: false, ready: true);
                FightHandler.SetAutoOptions(joiner, join: false, ready: true);
                var fight = new FightInstance(9202, 100, 200) { OpenerId = opener };
                fight.SetPlacementCells(Enumerable.Range(300, 8), Enumerable.Range(180, 8));
                fight.AddPlayer(Person(opener, "Opener", 200));
                fight.AddMonster(Monster(-1, 492, 0));
                Assert.True(fight.JoinTeam(Person(joiner, "Joiner", 200), FightInstance.Azules, 8));
                Assert.True(fight.JoinTeam(Person(leader, "Leader", 200), FightInstance.Azules, 8));

                Assert.Equal(new[] { leader }, FightHandler.ReadyAlongWith(fight, opener));
                Assert.False(fight.Buscar(joiner).IsReady);
                fight.Buscar(leader).IsReady = false;
                Assert.True(fight.LeavePlacement(opener));
                Assert.Equal(joiner, fight.ChallengerLeaderId);
                Assert.Empty(FightHandler.ReadyAlongWith(fight, joiner));
                Assert.False(fight.Buscar(leader).IsReady);
            }
            finally
            {
                FightHandler.SetAutoOptions(leader, false, false);
                FightHandler.SetAutoOptions(joiner, false, false);
                Parties.Dissolve(party);
            }
        }

        [Fact]
        public void A_member_with_the_automatic_ready_is_ready_with_his_leader()
        {
            const long leader = 8_200_000_001, follower = 8_200_000_002, other = 8_200_000_003;

            var party = Parties.Create(leader);
            Parties.Invite(party, follower, leader);
            Parties.Accept(party, follower);
            try
            {
                FightHandler.SetAutoOptions(follower, join: false, ready: true);

                var fight = new FightInstance(9201, 100, 200) { DefenderLeaderId = Group };
                fight.SetPlacementCells(Enumerable.Range(300, 8), Enumerable.Range(180, 8));
                fight.AddPlayer(Person(leader, "Leader", 200));
                fight.AddMonster(Monster(-1, 492, 0));
                fight.JoinTeam(Person(follower, "Follower", 200), FightInstance.Azules, 8);
                fight.JoinTeam(Person(other, "Other", 200), FightInstance.Azules, 8);

                // Only on the leader's ready, and only his party's with the switch on.
                Assert.Empty(FightHandler.ReadyAlongWith(fight, follower));
                Assert.Equal(new[] { follower }, FightHandler.ReadyAlongWith(fight, leader));
                Assert.True(fight.Buscar(follower).IsReady);
                Assert.False(fight.Buscar(other).IsReady);

                // Ready already: not announced twice.
                Assert.Empty(FightHandler.ReadyAlongWith(fight, leader));
            }
            finally
            {
                FightHandler.SetAutoOptions(follower, join: false, ready: false);
                Parties.Dissolve(party);
            }
        }

    }
}

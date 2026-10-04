using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Protocol;
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
    /// <remarks>
    /// The frames are the captures', byte for byte:
    ///
    ///   F  Combate/entrar a combate con listo automatico y entrada automatica siguiendo a lider
    ///      de grupo-combate-victoria (Harmoo 293213045026 leads, Sacri-Master 302677754146
    ///      follows him into fight 487 against the group -20002)
    ///   S  Combate/meterse en combate de otra persona haciendo click en la espadita y luego
    ///      abandonar para salirse (KTAS5625 into Inu-no-Kami's side of challenge 4923)
    ///   B  Busqueda grupo/busqueda automatica de grupo-encuentra para una dung... (fight 471)
    ///   J  Mazmorras/mazmorra de los jalatós completa (a kay turned down, fight 4368)
    ///   Z  Clases/Zobal/zobal-hechizos normales (Venom-Vi's fight 3331 seen from the map)
    /// </remarks>
    public class FightJoinTests
    {
        private const long Harmoo = 293213045026;
        private const long Sacri = 302677754146;
        private const long Group = -20002;
        private const long Fight487 = 487;

        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
        private static byte[] Bytes(string hex) => Convert.FromHexString(hex);

        // ════════════════════════════════════════════════════════════════════
        //  The frames
        // ════════════════════════════════════════════════════════════════════

        /// <summary>S frame 0: the kay of a click on Inu-no-Kami's flag. J frame 855 as well.</summary>
        [Fact]
        public void The_kay_names_a_fighter_of_the_side_and_the_fight()
        {
            var (fighter, fight) = FightJoinProtocol.ReadJoinRequest(
                ConnectionProtocol.Push(Op.Kay, Bytes("08e380ecc4d40110bb26")));
            Assert.Equal(57052692579, fighter);
            Assert.Equal(4923, fight);

            (fighter, fight) = FightJoinProtocol.ReadJoinRequest(
                ConnectionProtocol.Push(Op.Kay, Bytes("08a282bc96e619109022")));
            Assert.Equal(886420996386, fighter);
            Assert.Equal(4368, fight);

            Assert.Equal((0L, 0L), FightJoinProtocol.ReadJoinRequest(ConnectionProtocol.Push(Op.Kmv)));
        }

        /// <summary>J frame 856: the one refusal measured, 16, for a side restricted to its party.</summary>
        [Fact]
        public void A_refused_kay_is_answered_with_the_fighter_and_the_reason()
        {
            Assert.Equal("08a282bc96e6191010",
                         Hex(FightJoinProtocol.BuildJoinRefused(886420996386, FightJoinProtocol.RefusedPartyOnly)));
        }

        /// <summary>Z frame 2632: the swords of a fight nobody locked, the monsters' side empty.</summary>
        [Fact]
        public void The_swords_are_the_fight_with_its_flags_and_its_two_sides()
        {
            const long venom = 52819984483;
            var attackers = FightJoinProtocol.Team(0, venom, false,
                new[] { FightJoinProtocol.PersonOnTeam(venom, 201, "Venom-Vi") });
            var defenders = FightJoinProtocol.Team(1, -21200, true, Array.Empty<Pb>());

            byte[] hpy = FightJoinProtocol.BuildFightShown(
                FightJoinProtocol.FightOnMap(3331, FightRules.ContraMonstruos.TipoDelKam, 180, 182,
                                             attackers, defenders));

            Assert.Equal("0a490a04b401b60110041a2318e380c4e2c40120012a180a160a0d08c901120856656e6f6d2d56"
                         + "6910e380c4e2c4011a13100118b0dafeffffffffffff0120012a0030012a002a0030831a", Hex(hpy));
        }

        /// <summary>F frames 139-141: the map's kae for each side, and the monsters' growing.</summary>
        [Fact]
        public void The_map_gets_each_side_as_a_kae()
        {
            var harmoo = FightJoinProtocol.Team(0, Harmoo, false,
                new[] { FightJoinProtocol.PersonOnTeam(Harmoo, 447, "Harmoo") });
            Assert.Equal("0a2118a282f0a6c40820012a160a140a0b08bf0312064861726d6f6f10a282f0a6c40810e703",
                         Hex(FightJoinProtocol.BuildTeamUpdate(harmoo, Fight487)));

            var empty = FightJoinProtocol.Team(1, Group, true, Array.Empty<Pb>());
            Assert.Equal("0a13100118dee3feffffffffffff0120012a00300110e703",
                         Hex(FightJoinProtocol.BuildTeamUpdate(empty, Fight487)));

            var one = FightJoinProtocol.Team(1, Group, true, new[] { FightJoinProtocol.MonsterOnTeam(-1, 492, 1) });
            Assert.Equal("0a27100118dee3feffffffffffff0120012a140a1210ffffffffffffffffff012a0508ec031001300110e703",
                         Hex(FightJoinProtocol.BuildTeamUpdate(one, Fight487)));
        }

        /// <summary>
        /// The board's kae: the receiver's own side, its leader, no members. F frame 168 (the
        /// attackers) and S frame 23 (the red side of a challenge, f6 = 1).
        /// </summary>
        [Fact]
        public void The_board_carries_one_kae_for_the_receivers_own_side()
        {
            Assert.Equal("0a0b18a282f0a6c40820012a0010e703",
                         Hex(FightJoinProtocol.BuildTeamUpdate(FightJoinProtocol.Team(0, Harmoo, false, null), Fight487)));
            Assert.Equal("0a0d18e380ecc4d40120012a00300110bb26",
                         Hex(FightJoinProtocol.BuildTeamUpdate(
                             FightJoinProtocol.Team(1, 57052692579, false, null), 4923)));
        }

        /// <summary>The count, the swords going, a member off a side, a fighter off the board.</summary>
        [Fact]
        public void The_small_ones_are_the_captures()
        {
            Assert.Equal("1001", Hex(FightJoinProtocol.BuildFightCount(1)));          // F 138
            Assert.Equal("1003", Hex(FightJoinProtocol.BuildFightCount(3)));          // S 54
            Assert.Equal("", Hex(FightJoinProtocol.BuildFightCount(0)));              // Hipermago 4984
            Assert.Equal("089022", Hex(FightJoinProtocol.BuildFightHidden(4368)));    // J 857
            Assert.Equal("08841a", Hex(FightJoinProtocol.BuildFightHidden(3332)));    // Z 2650
            Assert.Equal("08d703100120ffffffffffffffffff01",
                         Hex(FightJoinProtocol.BuildTeamMemberRemoved(471, 1, -1)));  // B 100
            Assert.Equal("08f7ffffffffffffffff01", Hex(FightJoinProtocol.BuildFighterRemoved(-9)));  // B 140
            Assert.Equal("10f7ffffffffffffffff01", Hex(FightJoinProtocol.BuildActorHidden(-9)));     // B 141
            Assert.Equal("10dee3feffffffffffff01", Hex(FightJoinProtocol.BuildActorHidden(Group))); // F 132
            Assert.Empty(FightJoinProtocol.BuildLeftFight());                                        // S 37
        }

        /// <summary>
        /// The ilh: B frame 95 (a fight against monsters, 449 tenths of placement left) and the
        /// challenge's (no countdown, f7 = 4), both with the map's MapPositions row in f8.
        /// </summary>
        [Fact]
        public void The_party_hears_that_a_member_went_into_a_fight()
        {
            byte[] dungeon = FightJoinProtocol.BuildPartyMemberInFight(
                842853908770, 449, 71814809, "Ocrazy", 471, 108346,
                FightJoinProtocol.PartyFightMonsters, 225181696, 32, -64, 1028);
            Assert.Equal("08a28288f0c31810c10318999d9f2222064f6372617a7928d70330bace0638014215088080b06b"
                         + "102020840828c0ffffffffffffffff01", Hex(dungeon));

            byte[] challenge = FightJoinProtocol.BuildPartyMemberInFight(
                Harmoo, 0, 63000188, "Harmoo", 492, 71272,
                FightJoinProtocol.PartyFightChallenge, 188744196, 2, -18, 95);
            Assert.Equal("08a282f0a6c40818fc9c851e22064861726d6f6f28ec0330e8ac0438044214088484805a1002205f"
                         + "28eeffffffffffffffff01", Hex(challenge));
        }

        /// <summary>
        /// What the follower's board says (F 162-163): the kam names who OPENED the fight, the kaa
        /// what is left of the placement; and where he stands with the side he faces (F 152).
        /// </summary>
        [Fact]
        public void The_joiners_board_names_the_opener_and_what_is_left()
        {
            Assert.Equal("100418dee3feffffffffffff012202ec0328e70330a282f0a6c408",
                         Hex(FightProtocol.BuildFightAnnounced(4, Group, new long[] { 492 }, Fight487, Harmoo)));
            Assert.Equal("1801200128ba033004", Hex(FightProtocol.BuildFightSummary(4, 442)));
            Assert.Equal("121008bb01100118ffffffffffffffffff01120c08e502100518a28280c8e708",
                         Hex(FightProtocol.BuildFightersPlaced(new[] { (187, 1, -1L), (357, 5, Sacri) })));
        }

        // ════════════════════════════════════════════════════════════════════
        //  The teams of a real fight, as the map sees them
        // ════════════════════════════════════════════════════════════════════

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
            fight.AddPlayer(Person(Harmoo, "Harmoo", 447));
            fight.AddMonster(Monster(-1, 492, 0));
            return fight;
        }

        /// <summary>F frames 153, 140 and 141, built from the fight itself after Sacri came in.</summary>
        [Fact]
        public void A_fights_sides_come_out_as_the_capture_draws_them()
        {
            var fight = FollowFight();
            Assert.True(fight.JoinTeam(Person(Sacri, "Sacri-Master", 354), FightInstance.Azules,
                                       FightHandler.MaxPeoplePerTeam));

            Assert.Equal("0a3d18a282f0a6c40820012a320a140a0b08bf0312064861726d6f6f10a282f0a6c4080a1a0a1108e2"
                         + "02120c53616372692d4d617374657210a28280c8e70810e703",
                         Hex(FightJoinProtocol.BuildTeamUpdate(
                             FightHandler.TeamOnMap(fight, FightInstance.Azules, withMembers: true), Fight487)));
            Assert.Equal("0a13100118dee3feffffffffffff0120012a00300110e703",
                         Hex(FightJoinProtocol.BuildTeamUpdate(
                             FightHandler.TeamOnMap(fight, FightInstance.Rojos, withMembers: false), Fight487)));
            Assert.Equal("0a27100118dee3feffffffffffff0120012a140a1210ffffffffffffffffff012a0508ec031001300110e703",
                         Hex(FightJoinProtocol.BuildTeamUpdate(
                             FightHandler.TeamOnMap(fight, FightInstance.Rojos, withMembers: true), Fight487)));
        }

        // ════════════════════════════════════════════════════════════════════
        //  Who fits where
        // ════════════════════════════════════════════════════════════════════

        /// <summary>The first free cell of his side, in the kba's order: the leader keeps his.</summary>
        [Fact]
        public void A_joiner_takes_the_first_free_cell_of_his_side()
        {
            var fight = FollowFight();
            var sacri = Person(Sacri, "Sacri-Master", 354);

            Assert.True(fight.JoinTeam(sacri, FightInstance.Azules, FightHandler.MaxPeoplePerTeam));
            Assert.Equal(FightInstance.Azules, sacri.TeamId);
            Assert.Equal(357, sacri.CellId);
            Assert.Equal(2, fight.PeopleIn(FightInstance.Azules));
            Assert.Contains(sacri, fight.TurnOrder);

            // And nobody is moved on top of anybody during the placement.
            fight.ChangePlacementCell(Sacri, 317);
            Assert.Equal(357, sacri.CellId);
        }

        [Fact]
        public void Joining_is_refused_once_the_fight_has_started()
        {
            var fight = FollowFight();
            fight.StartFight();

            Assert.Equal(FightInstance.JoinRefusal.NotInPlacement,
                         fight.CanJoin(FightInstance.Azules, FightHandler.MaxPeoplePerTeam));
            Assert.False(fight.JoinTeam(Person(Sacri, "Sacri-Master", 354), FightInstance.Azules,
                                        FightHandler.MaxPeoplePerTeam));
            Assert.Equal(1, fight.PeopleIn(FightInstance.Azules));

            // The same answer through the kay's door, for somebody standing on the map.
            var sacri = new SessionState { CharacterId = Sacri, MapId = 188744196 };
            Assert.Equal("NotInPlacement", FightHandler.WhyNotJoin(fight, Harmoo, sacri, out _));
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
            Assert.False(fight.JoinTeam(Person(Sacri, "Sacri-Master", 354), FightInstance.Azules,
                                        FightHandler.MaxPeoplePerTeam));

            var sacri = new SessionState { CharacterId = Sacri, MapId = 188744196 };
            Assert.Equal("TeamFull", FightHandler.WhyNotJoin(fight, Harmoo, sacri, out _));
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
            var sacri = new SessionState { CharacterId = Sacri, MapId = 188744196 };

            Assert.Equal(FightInstance.JoinRefusal.MonsterTeam,
                         fight.CanJoin(FightInstance.Rojos, FightHandler.MaxPeoplePerTeam));
            Assert.Equal("MonsterTeam", FightHandler.WhyNotJoin(fight, Group, sacri, out int team));
            Assert.Equal(FightInstance.Rojos, team);
        }

        [Fact]
        public void The_kay_is_checked_against_who_asks()
        {
            var fight = FollowFight();

            var onTheMap = new SessionState { CharacterId = Sacri, MapId = 188744196 };
            Assert.Null(FightHandler.WhyNotJoin(fight, Harmoo, onTheMap, out int team));
            Assert.Equal(FightInstance.Azules, team);

            var elsewhere = new SessionState { CharacterId = Sacri, MapId = 188744197 };
            Assert.NotNull(FightHandler.WhyNotJoin(fight, Harmoo, elsewhere, out _));

            var fighting = new SessionState { CharacterId = Sacri, MapId = 188744196, IsInFight = true };
            Assert.NotNull(FightHandler.WhyNotJoin(fight, Harmoo, fighting, out _));

            Assert.NotNull(FightHandler.WhyNotJoin(fight, 42, onTheMap, out _));
            Assert.NotNull(FightHandler.WhyNotJoin(null, Harmoo, onTheMap, out _));
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
            fight.JoinTeam(Person(Sacri, "Sacri-Master", 354), FightInstance.Azules, FightHandler.MaxPeoplePerTeam);

            Assert.True(fight.LeavePlacement(Sacri));
            Assert.Equal(-1, fight.EquipoDe(Sacri));
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
            fight.JoinTeam(Person(Sacri, "Sacri-Master", 354), FightInstance.Azules, FightHandler.MaxPeoplePerTeam);

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

                Assert.Equal(new[] { follower }, FightHandler.FollowersInto(fight, boss));

                // Not from another map, not while fighting, and not behind somebody who does not lead.
                sessions[1].State.MapId = map + 1;
                Assert.Empty(FightHandler.FollowersInto(fight, boss));
                sessions[1].State.MapId = map;
                sessions[1].State.FightId = 55;
                Assert.Empty(FightHandler.FollowersInto(fight, boss));
                sessions[1].State.FightId = 0;
                Assert.Empty(FightHandler.FollowersInto(fight, Person(stayer, "Stayer", 1)));

                // Nor once the placement is over.
                fight.StartFight();
                Assert.Empty(FightHandler.FollowersInto(fight, boss));
            }
            finally
            {
                FightHandler.SetAutoOptions(follower, join: false, ready: false);
                foreach (var s in sessions) SessionRegistry.Unregister(s);
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

        /// <summary>The four switches of the party window, and nothing else, go to the handler.</summary>
        [Fact]
        public void The_party_switches_are_recognised()
        {
            foreach (string op in new[] { Op.Ilf, Op.Int, Op.Ikr, Op.Inp })
            {
                Assert.True(FightHandler.IsAutoOptionRequest(Op.Uri(op)));
            }
            Assert.False(FightHandler.IsAutoOptionRequest(Op.Uri(Op.Kay)));
            Assert.False(FightHandler.IsAutoOptionRequest(Op.Uri(Op.Ino)));
        }
    }
}

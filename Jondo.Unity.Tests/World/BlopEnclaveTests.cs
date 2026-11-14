using System.Linq;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Blop enclave as the game has it (Dofus pour les noobs, «Clos des Blops»): four fight
    /// rooms, Bibiblop's hall where one Royal Blop is chosen, that Royal Blop's room, and the exit
    /// room where Bibiblop takes whoever holds its key into the Royal Rainbow Blop's lair.
    /// </summary>
    [Collection("MapManager")]
    public sealed class BlopEnclaveTests
    {
        private const long Entrance = 166985728, FourthRoom = 166989824, Hall = 166990848,
                           ExitRoom = 166989826, Lair = 166990850;

        private static readonly (long Room, int Boss)[] Royal =
        {
            (166988802, 1184), (166985730, 1186), (166986754, 1185), (166987778, 1187),
        };

        private static void Load()
        {
            if (MapManager.GetMapInfo(Hall) == null) MapManager.Initialize();
            DungeonManager.Initialize();
        }

        [Fact]
        public void The_fourth_room_leads_to_the_hall_and_each_royal_room_to_the_exit_room()
        {
            Load();
            var enclave = DungeonManager.Get(11)!;
            Assert.Equal(4, enclave.Rooms.Count);
            Assert.Equal(FourthRoom, enclave.Rooms[^1]);
            Assert.Empty(enclave.Bosses);
            Assert.Same(enclave, DungeonManager.AtEntrance(Entrance));

            Assert.Equal(Hall, DungeonHandler.AfterAWinIn(FourthRoom).Map);
            foreach (var (room, boss) in Royal)
            {
                var dungeon = DungeonManager.OfRoom(room)!;
                Assert.Equal(new[] { boss }, dungeon.Bosses);
                Assert.Equal(room, dungeon.LastRoom);
                Assert.Equal(ExitRoom, DungeonHandler.AfterAWinIn(room).Map);
            }
            Assert.False(DungeonManager.IsRoom(Hall));
            Assert.False(DungeonManager.IsRoom(ExitRoom));
        }

        [Fact]
        public void The_lair_is_entered_from_the_exit_room_with_its_own_key()
        {
            Load();
            var lair = DungeonManager.AtEntrance(ExitRoom)!;
            Assert.Equal(49, lair.Id);
            Assert.Equal(Lair, lair.FirstRoom);
            Assert.Equal(new[] { 1188, 1184, 1185, 1186, 1187 }, lair.Bosses);
            Assert.Equal(new[] { (9254, 1) }, lair.Required);
            Assert.Equal(Entrance, DungeonManager.WayOut(lair));
        }

        /// <summary>
        /// «Le Blop Multicolore qui sera accompagné des 4 Blops Royaux» (Dofus pour les noobs): the
        /// five, one of each, and all five to a player who comes alone -- drawn from a roster, it
        /// came with two Indigo and two Coco, and without the Reinette.
        /// </summary>
        [Fact]
        public void The_lair_brings_the_royal_rainbow_blop_and_the_four_royals_to_whoever_comes()
        {
            Load();
            MobSpawnManager.EnsureMonsterData();
            Assert.True(DungeonManager.Get(49)!.BossAlone);

            var group = MobSpawnManager.RecomposeDungeonRoom(Lair)!;
            Assert.Equal(new[] { 1188, 1184, 1185, 1186, 1187 }, group.Members.Select(m => m.Monster.Id));
            Assert.Equal(5, group.Leaders);
            foreach (int players in new[] { 1, 4, 5, 8 })
                Assert.Equal(group.Members, MobSpawnManager.MembersFor(group, players));

            // One alternative, the five, from one player on.
            var actor = MapActorMessages.MonsterGroup(group, false);
            var roleplay = Assert.IsType<ActorInformationCaseRolePlayActor>(actor.ActorInformation!.ActorCase).Value;
            var monsters = Assert.IsType<RolePlayActorCaseMonsterGroupActor>(roleplay.ActorCase).Value;
            var alternative = Assert.Single(monsters.Identification.Alternatives);
            Assert.Equal(1, alternative.PlayerCount);
            Assert.Equal(group.Members.Select(member => member.Monster.Id), alternative.Monsters.Select(member => member.Gid));
        }

        [Fact]
        public void Bibiblop_offers_the_royal_rooms_in_the_hall_and_the_lair_in_the_exit_room()
        {
            var trees = NpcDialogueContent.Load(Paths.ContentFile(NpcDialogueContent.AuthoredFile)).Rows
                .Select(r => r.Value.Value).Where(d => d.NpcId == 1034).ToList();

            var hall = trees.Single(d => d.MapId == Hall);
            Assert.Equal(5403, hall.Opening);
            var goesTo = hall.Lines.Where(l => l.Message != 5403)
                .Select(l => l.Choices.Single(c => c.Reply == 20292).TeleportsTo).OrderBy(m => m);
            Assert.Equal(Royal.Select(r => r.Room).OrderBy(m => m), goesTo);

            var exit = trees.Single(d => d.MapId == ExitRoom);
            Assert.Equal(5410, exit.Opening);
            var doors = exit.Lines.Single().Choices.Where(c => c.Door.Length > 0).Select(c => c.Reply).OrderBy(r => r);
            Assert.Equal(new long[] { 4531, 6623 }, doors);
        }
    }
}

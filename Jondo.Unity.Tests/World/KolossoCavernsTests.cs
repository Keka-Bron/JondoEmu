using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Kolosso's Caverns (Dofus pour les noobs, «Cavernes du Kolosso»): a lobby whose four doors
    /// lead to the element rooms, fought in any order; each win leads back to the lobby, and the
    /// last on to the three empty rooms and the Kolosso. The catalogue walked the empty rooms
    /// with a fight in each and never the element rooms.
    /// </summary>
    [Collection("MapManager")]
    public sealed class KolossoCavernsTests
    {
        private const long Lobby = 61865984, Icy = 61998084, Venomous = 61998082, Bestial = 61998338,
                           Pyrotechnic = 61998340, Second = 61867008, Third = 61868032, Fourth = 61868034,
                           Fifth = 61868036;

        private static DungeonManager.Dungeon Load()
        {
            if (MapManager.GetMapInfo(Lobby) == null) MapManager.Initialize();
            DungeonManager.Initialize();
            MobSpawnManager.EnsureMonsterData();
            return DungeonManager.Get(60)!;
        }

        [Fact]
        public void The_lobby_leads_to_the_element_rooms_and_the_last_win_on()
        {
            var caverns = Load();
            Assert.Equal(Lobby, caverns.FirstRoom);
            Assert.Equal(Fifth, caverns.LastRoom);
            var hub = caverns.Hub!;
            Assert.Equal(Lobby, hub.Room);
            Assert.Equal(new[] { Venomous, Icy, Bestial, Pyrotechnic }, hub.Rooms.Keys.OrderBy(r => r));

            // Any order: the first win, whichever room, goes back to the lobby next to its door.
            Assert.Equal((Lobby, 216), hub.AfterAWin(Bestial, new long[0]));
            Assert.Equal((Lobby, 338), hub.AfterAWin(Icy, new[] { Bestial }));
            // The fourth leads on into the Second Room, counted whether or not it is noted yet.
            Assert.Equal(Second, hub.AfterAWin(Venomous, new[] { Bestial, Icy, Pyrotechnic })!.Value.Map);
            Assert.Equal(Second, hub.AfterAWin(Venomous, new[] { Bestial, Icy, Pyrotechnic, Venomous })!.Value.Map);
            Assert.Null(hub.AfterAWin(Third, new long[0]));
        }

        [Fact]
        public void The_lobby_and_corridors_are_empty_and_each_element_room_has_its_own()
        {
            Load();
            foreach (long empty in new[] { Lobby, Second, Third, Fourth })
                Assert.Null(MobSpawnManager.RecomposeDungeonRoom(empty));

            Assert.All(MobSpawnManager.RecomposeDungeonRoom(Icy)!.Members,
                       m => Assert.Contains(m.Monster.Id, new[] { 2966, 2884, 2885, 2886 }));
            Assert.All(MobSpawnManager.RecomposeDungeonRoom(Venomous)!.Members, m => Assert.Equal(2884, m.Monster.Id));
            Assert.All(MobSpawnManager.RecomposeDungeonRoom(Bestial)!.Members, m => Assert.Equal(2883, m.Monster.Id));
            Assert.All(MobSpawnManager.RecomposeDungeonRoom(Pyrotechnic)!.Members, m => Assert.Equal(2965, m.Monster.Id));
            Assert.Contains(MobSpawnManager.RecomposeDungeonRoom(Fifth)!.Members, m => m.Monster.Id == 2986);
        }

        [Fact]
        public void Each_lobby_door_opens_on_its_room_and_each_room_leads_back()
        {
            Load();
            TeleportManager.Initialize();
            foreach (var (element, room) in new[] { (458477, Icy), (458478, Venomous), (458480, Bestial), (458481, Pyrotechnic) })
            {
                Assert.True(TeleportManager.TryGet(Lobby, element, out var door));
                Assert.Equal(room, door.DestinationMapId);
            }
            foreach (var (room, cell) in new[] { (Icy, 206), (Venomous, 218), (Bestial, 93), (Pyrotechnic, 155) })
            {
                Assert.True(TeleportManager.TryGetCellTrigger(room, cell, out var back));
                Assert.Equal(Lobby, back.DestinationMapId);
            }
            Assert.True(TeleportManager.TryGetCellTrigger(Second, 136, out var on));
            Assert.Equal(Third, on.DestinationMapId);
        }
    }
}

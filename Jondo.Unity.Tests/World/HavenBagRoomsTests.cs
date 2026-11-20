using System;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;
using Jondo.Unity.Protocol.Game;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The haven bag's rooms and themes, and whose bag one stands in: «ir al merkasako y volver»
    /// and «cambiar de salas en merkasako», and the client's HavenbagManagerUI.
    /// </summary>
    [Collection("MapManager")]
    public class HavenBagRoomsTests
    {
        private static byte[] Hex(string hex) => Convert.FromHexString(hex);

        private static bool WorldLoaded()
        {
            if (!MapManager.WalkableCells.ContainsKey(Jail.MapId)) MapManager.Initialize();
            Merkasako.Initialize();
            HavenBagStore.Initialize();
            long bag = Merkasako.MapOfTheme(Merkasako.DefaultTheme);
            return bag != 0 && MapManager.GetMapInfo(bag) != null;
        }

        /// <summary>The rooms (jbf) of the captured account, byte for byte.</summary>
        [Fact]
        public void Rooms_have_the_capture_s_shape()
        {
            Assert.Equal(Hex("0a0208200a04080410010a04080110020a0408011003"),
                         ConnectionProtocol.BuildHavenBagRooms(new[] { (0, 32), (1, 4), (2, 1), (3, 1) }));
        }

        /// <summary>The live typed snapshot keeps the owner's identity and selected room zero.</summary>
        [Fact]
        public void Whose_bag_uses_the_current_owner_theme_and_room()
        {
            if (!WorldLoaded()) return;
            const long who = 9_963_000_711;
            var session = GameSession.SinSocket();
            session.BindAccount(who, 1);
            session.State.CharacterId = who;
            session.State.MapId = Merkasako.MapOfTheme(Merkasako.DefaultTheme);
            HavenBagStore.SetCurrentRoom(who, 0);
            var snapshot = MapInformationMessages.Live.Snapshot(session, new() { Id = who, Name = "Bag-Owner", Level = 354, Breed = 1 });
            var bag = Assert.IsType<MapSpecificComplementaryInformationCaseHavenBagInformation>(snapshot.SpecificCase).Value;
            Assert.Equal(Merkasako.DefaultTheme, bag.Theme);
            Assert.Equal(0, bag.RoomId);
            Assert.Equal(who, bag.OwnerInformation.Id);
            var owner = Assert.IsType<CharacterComplementCaseCharacterBasicInformation>(bag.OwnerInformation.ComplementCase).Value;
            Assert.Equal("Bag-Owner", owner.Name);
            Assert.Equal(354, owner.Level);
            Assert.IsType<CharacterBasicInformationComplementCaseNone>(owner.ComplementCase);
        }

        /// <summary>Every character has every theme and the four rooms; a room keeps the theme it is given.</summary>
        [Fact]
        public void Every_theme_and_four_rooms()
        {
            if (!WorldLoaded()) return;
            const long who = 990_000_711;

            Assert.True(Merkasako.Themes.Count > 1);
            var rooms = HavenBagStore.RoomsOf(who);
            Assert.Equal(Enumerable.Range(0, Merkasako.Rooms), rooms.Select(r => r.Room));

            int other = Merkasako.Themes.First(t => t != Merkasako.DefaultTheme);
            HavenBagStore.SetRoomTheme(who, 2, other);
            Assert.Equal(other, HavenBagStore.ThemeOfRoom(who, 2));
            HavenBagStore.SetCurrentRoom(who, 2);
            Assert.Equal(2, HavenBagStore.CurrentRoom(who));
            HavenBagStore.SetRoomTheme(who, 2, Merkasako.DefaultTheme);
            HavenBagStore.SetCurrentRoom(who, 0);
        }

        /// <summary>
        /// Into the bag, the rooms' list follows the map (jbf); jbl goes to a room in its theme; jbt
        /// gives the room a theme. The map's jss names its owner, so the client shows its menu.
        /// </summary>
        [Fact]
        public async Task Rooms_are_gone_to_and_themed()
        {
            if (!WorldLoaded()) return;
            const long who = 990_000_712;
            int other = Merkasako.Themes.First(t => t != Merkasako.DefaultTheme);
            HavenBagStore.SetRoomTheme(who, 1, other);
            HavenBagStore.SetCurrentRoom(who, 0);

            await using var pipe = await ClientPipe.OpenAsync(who, who, 191105026, "Dueno");
            pipe.Session.State.CharacterLevel = MerkasakoHandler.MinimumLevel;
            using (SessionContext.Push(pipe.Session))
            {
                await MerkasakoHandler.EnterFromOutsideAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Jbn));
                // Behind the map's own frames: jsd, jru, lqu, the discovered map.
                string got;
                do { (got, _) = await pipe.NextAny(); } while (got != Op.Jbf);
                Assert.True(Merkasako.IsHavenBag(pipe.Session.State.MapId));

                await MerkasakoHandler.ChangeRoomAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Jbl, Hex("0801")));
                Assert.Equal(Merkasako.MapOfTheme(other), pipe.Session.State.MapId);
                Assert.Equal(1, HavenBagStore.CurrentRoom(who));

                await MerkasakoHandler.ChangeThemeAsync(pipe.ToClient,
                    ConnectionProtocol.Push(Op.Jbt, Pb.New().Var(1, Merkasako.DefaultTheme).Build()));
                Assert.Equal(Merkasako.MapOfTheme(Merkasako.DefaultTheme), pipe.Session.State.MapId);
                Assert.Equal(Merkasako.DefaultTheme, HavenBagStore.ThemeOfRoom(who, 1));

                var me = new DbCharacter { Id = who, Name = "Dueno", Level = 200, Breed = 1 };
                var snapshot = MapInformationMessages.Live.Snapshot(pipe.Session, me);
                var bag = Assert.IsType<MapSpecificComplementaryInformationCaseHavenBagInformation>(snapshot.SpecificCase).Value;
                Assert.Equal(who, bag.OwnerInformation.Id);
                Assert.Equal(1, bag.RoomId);
                Assert.Equal(Merkasako.DefaultTheme, bag.Theme);
            }
            HavenBagStore.SetCurrentRoom(who, 0);
        }

        /// <summary>
        /// Accepting the room sends jbk, the pieces (jbg), jav and jaw, and the pieces may come
        /// behind the jav (the 16/10 test). The room is what the pieces carry, whatever the order;
        /// the next upload replaces it. It was stored on the jbk, empty.
        /// </summary>
        [Fact]
        public async Task The_room_is_kept_whatever_the_order()
        {
            if (!WorldLoaded()) return;
            const long who = 990_000_715;
            long bag = Merkasako.MapOfTheme(Merkasako.DefaultTheme);
            int theme = Merkasako.ThemeOfMap(bag);

            static byte[] Piece(int cell, long type)
                => ConnectionProtocol.Push(Op.Jbg, Pb.New().Msg(2, Pb.New().Var(1, cell).Var(2, type)).Build());

            await using var pipe = await ClientPipe.OpenAsync(who, who, bag, "Decorador");
            using (SessionContext.Push(pipe.Session))
            {
                pipe.Session.State.MapId = bag;

                MerkasakoHandler.UploadStarts();
                await MerkasakoHandler.CloseEditorAsync(pipe.ToClient);       // jav
                MerkasakoHandler.CollectFurniture(Piece(427, 52098212));
                MerkasakoHandler.CollectFurniture(Piece(357, 5073046));
                await MerkasakoHandler.CloseEditorAsync(pipe.ToClient);       // jaw
                Assert.Equal(new[] { (357, 5073046L), (427, 52098212L) },
                             HavenBagStore.FurnitureOf(who, theme).Select(f => (f.Cell, f.TypeId)));

                // The next accepted room replaces this one.
                MerkasakoHandler.UploadStarts();
                MerkasakoHandler.CollectFurniture(Piece(427, 52098212));
                Assert.Equal(new[] { 427 }, HavenBagStore.FurnitureOf(who, theme).Select(f => f.Cell));

                HavenBagStore.SaveFurniture(who, theme, Array.Empty<HavenBagStore.Furniture>());
            }
        }

        /// <summary>
        /// One who logged in inside his bag leaves it the way he came in, which the database keeps:
        /// he went to the starting point, Incarnam. And leaving resets the roleplay context (kml,
        /// kmp), which is what takes the bag's menu off.
        /// </summary>
        [Fact]
        public async Task Leaving_goes_back_where_one_came_in()
        {
            if (!WorldLoaded()) return;
            const long who = 990_000_716, street = 191105026;
            HavenBagStore.SetCurrentRoom(who, 0);

            await using (var first = await ClientPipe.OpenAsync(who, who, street, "Vuelve"))
            using (SessionContext.Push(first.Session))
            {
                first.Session.State.CharacterLevel = MerkasakoHandler.MinimumLevel;
                await MerkasakoHandler.EnterFromOutsideAsync(first.ToClient, ConnectionProtocol.Push(Op.Jbn));
                Assert.True(Merkasako.IsHavenBag(first.Session.State.MapId));
            }

            long bag = Merkasako.MapOfTheme(HavenBagStore.ThemeOfRoom(who, 0));
            await using var later = await ClientPipe.OpenAsync(who, who, bag, "Vuelve");
            using (SessionContext.Push(later.Session))
            {
                Assert.Equal(0, later.Session.State.HavenBagEntryMapId);
                await MerkasakoHandler.EnterFromOutsideAsync(later.ToClient, ConnectionProtocol.Push(Op.Jbn));
                Assert.Equal(street, later.Session.State.MapId);

                var seen = new System.Collections.Generic.List<string>();
                string got;
                do { (got, _) = await later.NextAny(); seen.Add(got); } while (got != Op.Jru);
                Assert.Contains(Op.Kml, seen);
                Assert.Contains(Op.Kmp, seen);
            }
        }

        /// <summary>
        /// Two bags of one theme are one map and two bags: each sees himself alone in his. The
        /// second to come in saw the first.
        /// </summary>
        [Fact]
        public async Task Two_bags_of_one_theme_do_not_see_each_other()
        {
            if (!WorldLoaded()) return;
            const long first = 990_000_713, second = 990_000_714;
            foreach (long who in new[] { first, second })
            {
                HavenBagStore.SetRoomTheme(who, 0, Merkasako.DefaultTheme);
                HavenBagStore.SetCurrentRoom(who, 0);
            }

            await using var a = await ClientPipe.OpenAsync(first, first, 191105026, "Uno");
            await using var b = await ClientPipe.OpenAsync(second, second, 191105026, "Dos");
            a.Session.State.CharacterLevel = b.Session.State.CharacterLevel = MerkasakoHandler.MinimumLevel;
            using (SessionContext.Push(a.Session))
                await MerkasakoHandler.EnterFromOutsideAsync(a.ToClient, ConnectionProtocol.Push(Op.Jbn));
            using (SessionContext.Push(b.Session))
                await MerkasakoHandler.EnterFromOutsideAsync(b.ToClient, ConnectionProtocol.Push(Op.Jbn));

            long bag = a.Session.State.MapId;
            Assert.Equal(bag, b.Session.State.MapId);
            using (SessionContext.Push(b.Session))
                Assert.Equal(new[] { second }, SessionRegistry.OnMap(bag).Select(s => s.CharacterId));
            using (SessionContext.Push(a.Session))
                Assert.Equal(new[] { first }, SessionRegistry.OnMap(bag).Select(s => s.CharacterId));
        }
    }
}

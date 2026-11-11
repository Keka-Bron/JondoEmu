using System.Globalization;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Network
{
    /// <summary>Entry uses current account, character and connection state, without a recorded manifest.</summary>
    [Collection("forgemagic")]
    public class WorldEntryOwnDataTests
    {
        private const long Who = 8_999_201;
        private static readonly GameCodec Wire = new();

        private static DatabaseManager.DbCharacter Character()
            => new() { Id = Who, AccountId = Who, Name = "Propio", Level = 200, Breed = 8 };

        [Fact]
        public void No_mutual_friend_playing_uses_the_semantic_zero_count_policy()
        {
            var session = GameSession.SinSocket();
            session.BindAccount(Who, 1);
            session.State.CharacterId = Who;
            var opening = new MapEntryOpeningMessages(_ => false, _ => 0,
                _ => [], (_, _) => false, () => []);

            var notice = Assert.Single(opening.Snapshot(session, GameContext.RolePlay).OfType<TextInformationEvent>());

            Assert.Equal(InfoMessages.OnlineMutualFriends, notice.MessageId);
            Assert.Equal(new[] { "0" }, notice.Parameters);
        }

        [Fact]
        public async Task The_gifts_once_the_character_is_chosen_are_the_account_s()
        {
            var packets = new List<byte[]>();
            var session = new GameSession(Wire, packet => { packets.Add(packet); return Task.CompletedTask; });
            session.BindAccount(Who, 1);
            session.State.CharacterId = Who + 1;
            bool granted = false;
            var gifts = new GiftService(account =>
            {
                Assert.Equal(Who, account);
                granted = true;
            }, account =>
            {
                Assert.True(granted);
                Assert.Equal(Who, account);
                return [new() { Id = 91_001, Gid = 81_001, Quantity = 3, Title = "Own gift" }];
            }, () => [], _ => { });

            await new GameEntryContinuationMessages(gifts).SendAsync(session);

            Assert.IsType<HaapiSessionEvent>(Wire.Decode(packets[0]).Content);
            var action = Assert.Single(Assert.IsType<GameActionItemListEvent>(Wire.Decode(packets[1]).Content).Actions);
            Assert.Equal(91_001, Assert.Single(action.ItemUids));
            Assert.Equal(81_001, Assert.Single(action.Content!.Items).ObjectGid);
            Assert.Equal(3, Assert.Single(action.Content.Items).Quantity);
        }

        [Fact]
        public void The_look_on_entering_is_the_character_s()
        {
            var character = Character();
            var actual = InitialPresentationMessages.Live(character).Look();
            var expected = Assert.IsType<OutfitLookUpdatedEvent>(Wire.Decode(ConnectionProtocol.Push(
                Op.Lxc, ConnectionProtocol.BuildLookChanged(character))).Content);
            Assert.Equal(ConnectionProtocol.LookIdOf(character.Id), actual.OutfitId);
            Assert.Equal(expected.OutfitId, actual.OutfitId);
            Assert.NotNull(actual.Look);
            Assert.Equal(expected.Look!.Colors, actual.Look.Colors);
            Assert.Equal(expected.Look.Bones, actual.Look.Bones);
            Assert.Equal(expected.Look.Scales, actual.Look.Scales);
            Assert.Equal(expected.Look.Skins, actual.Look.Skins);
        }

        [Fact]
        public async Task Haapi_ids_belong_to_connections_and_are_stable_within_a_connection()
        {
            var packets = new List<byte[]>();
            GameSession Session() => new(Wire, packet => { packets.Add(packet); return Task.CompletedTask; });
            var first = Session();
            var second = Session();
            var continuation = new GameEntryContinuationMessages(new GiftService(_ => { }, _ => [], () => [], _ => { }));

            await continuation.SendAsync(first);
            await continuation.SendAsync(first);
            await continuation.SendAsync(second);

            var ids = packets.Select(packet => Wire.Decode(packet).Content).OfType<HaapiSessionEvent>()
                .Select(message => message.SessionUid).ToArray();
            Assert.Equal(ids[0], ids[1]);
            Assert.NotEqual(ids[0], ids[2]);
            Assert.All(ids, id => Assert.True(long.Parse(id, CultureInfo.InvariantCulture) > 0));
        }

        [Fact]
        public async Task Map_entry_advertises_no_BAK_key_without_a_local_key_issuer()
        {
            var packets = new List<byte[]>();
            var session = new GameSession(Wire, packet => { packets.Add(packet); return Task.CompletedTask; });
            var opening = new MapEntryOpeningMessages(_ => false, _ => 0, _ => [], (_, _) => false, () => []);
            var map = new GameEntryMapMessages(TimeProvider.System, () => 5, _ => new([], []),
                _ => (new([], ShortcutBar.Spell), new([], ShortcutBar.General)));

            await map.SendAsync(session, opening, GameContext.RolePlay, 191105026);

            Assert.Equal(new BakApiKeyEvent(""), Assert.Single(packets.Select(packet => Wire.Decode(packet).Content)
                .OfType<BakApiKeyEvent>()));
        }
    }
}

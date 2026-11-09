using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Network
{
    /// <summary>
    /// The world entry's messages about the account and the session: the mutual friends playing,
    /// the shop gifts waiting, the look, and the session's ids.
    /// </summary>
    [Collection("world entry content")]
    public class WorldEntryOwnDataTests
    {
        private const long Who = 8_999_201;

        private static void ManifestLoaded() => WorldEntryContent.Load(Paths.ContentFile(WorldEntryContent.AuthoredFile));

        private static DatabaseManager.DbCharacter Character()
            => new DatabaseManager.DbCharacter { Id = Who, AccountId = Who, Name = "Propio", Level = 200, Breed = 8 };

        private static WorldEntryContent.Row RowOf(string block, string opcode)
            => WorldEntryContent.Rows(block).Single(r => r.Opcode == opcode);

        private static int MessageOf(byte[] lqn)
            => (int)(ProtoMessage.Parse(lqn).Fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 0)?.VarIntValue ?? 0);

        [Fact]
        public void No_mutual_friend_playing_tells_no_count_of_them()
        {
            ManifestLoaded();
            var row = WorldEntryContent.Rows(WorldEntry.BlockMap)
                .Single(r => r.Opcode == Op.Lqn && MessageOf(ConnectionProtocol.ReadPayload(r.Frame, Op.Lqn)!) == ConnectionProtocol.MutualFriendsPlayingMessage);

            var rebuilt = WorldEntry.Rebuilt(row.Frame, row.Built, Character());

            Assert.NotNull(rebuilt);
            Assert.Empty(rebuilt!);
            Assert.Equal(0, FriendsHandler.MutualPlaying(Who));
        }

        [Fact]
        public void The_gifts_once_the_character_is_chosen_are_the_account_s()
        {
            ManifestLoaded();
            var row = RowOf(WorldEntry.BlockAfterConfirm, Op.Jtg);

            var rebuilt = WorldEntry.Rebuilt(row.Frame, row.Built, Character());

            Assert.Equal(GiftHandler.BuildList(Who), ConnectionProtocol.ReadPayload(rebuilt!, Op.Jtg));
        }

        [Fact]
        public void The_look_on_entering_is_the_character_s()
        {
            ManifestLoaded();
            var character = Character();
            var row = RowOf(WorldEntry.BlockAfterCharacter, Op.Lxc);

            var rebuilt = WorldEntry.Rebuilt(row.Frame, row.Built, character);

            Assert.Equal(ConnectionProtocol.BuildLookChanged(character), ConnectionProtocol.ReadPayload(rebuilt!, Op.Lxc));
        }

        [Theory]
        [InlineData("afterConfirm", "jby")]
        [InlineData("map", "lru")]
        public void The_session_s_ids_are_new_each_time(string block, string opcode)
        {
            ManifestLoaded();
            var row = RowOf(block, opcode);
            var recorded = ConnectionProtocol.ReadPayload(row.Frame, opcode)!;

            var first = ConnectionProtocol.ReadPayload(WorldEntry.Rebuilt(row.Frame, row.Built, Character())!, opcode)!;
            var second = ConnectionProtocol.ReadPayload(WorldEntry.Rebuilt(row.Frame, row.Built, Character())!, opcode)!;

            Assert.NotEqual(recorded, first);
            Assert.NotEqual(first, second);
            // The same shape: one text in f1, as long as the recorded one.
            string text = System.Text.Encoding.UTF8.GetString(ProtoMessage.Parse(first).Fields.Single(f => f.FieldNumber == 1).BytesValue);
            string was = System.Text.Encoding.UTF8.GetString(ProtoMessage.Parse(recorded).Fields.Single(f => f.FieldNumber == 1).BytesValue);
            Assert.Equal(was.Length, text.Length);
        }
    }
}

using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Sessions;

public sealed class CharacterListRefreshTests
{
    [Fact]
    public async Task Creation_and_deletion_refresh_the_owning_sessions_list_including_the_last_character()
    {
        const long account = 8_919_099_001;
        var codec = new RecordingCodec();
        await using var pipe = await ClientPipe.OpenAsync(account, account + 100_000,
            CharacterCreationHandler.StartingMap, codec: codec, enterWorld: false);
        pipe.Session.BindAccount(account, 290);
        var unrelated = GameSession.SinSocket();
        unrelated.BindAccount(account + 1, 291);
        using var context = SessionContext.Push(unrelated);
        long old = 0, fresh = 0;
        int delivered = 0;
        try
        {
            old = DatabaseManager.CreateCharacter(account, 290, Name("Old"),
                1, 0, 0, [], CharacterCreationHandler.StartingMap, 1, 0, 0, []);
            Assert.True(old > 0);
            using (var connection = new SqliteConnection(DatabaseManager.WorldConnectionString))
            {
                connection.Open();
                using var played = connection.CreateCommand();
                played.CommandText = "UPDATE Characters SET LastConnection = '2026-01-01T00:00:00Z' WHERE Id = $id";
                played.Parameters.AddWithValue("$id", old);
                played.ExecuteNonQuery();
            }

            string name = Name("New");
            await CharacterCreationHandler.CreateAsync(pipe.Session, CharacterManagementRequestFixtures.Create(name));
            Assert.Equal(CharacterManagementRequestFixtures.CreationAccepted, await ReadPacket());
            fresh = DatabaseManager.GetCharactersByAccountId(account, 290).Single(character => character.Name == name).Id;
            await ReadRefresh(fresh, old);
            Assert.Same(unrelated, SessionContext.Actual);

            await Delete(fresh, old);
            await Delete(old);
            Assert.Equal(15, codec.Frames.Count);
            Assert.Equal(15, delivered);
            Assert.All(codec.Frames, frame => Assert.Null(frame.Uid));
            Assert.Same(unrelated, SessionContext.Actual);
        }
        finally
        {
            if (fresh > 0) DatabaseManager.DeleteCharacter(fresh, account);
            if (old > 0) DatabaseManager.DeleteCharacter(old, account);
        }

        async Task Delete(long id, params long[] remaining)
        {
            Assert.True(await CharacterDeletionHandler.DeleteAsync(pipe.Session,
                CharacterManagementRequestFixtures.Delete(id)));
            await ReadRefresh(remaining);
        }

        async Task ReadRefresh(params long[] characterIds)
        {
            Assert.Equal(delivered + 5, codec.Frames.Count);
            var refresh = codec.Frames.Skip(delivered).ToArray();
            Assert.Equal(new GamePayload[] { new QueueStatusEvent(1, 1), new QueueStatusEvent(1, 0),
                new QueueStatusEvent(0, 0) }, refresh.Take(3).Select(frame => frame.Content));
            var list = Assert.IsType<CharacterListEvent>(refresh[3].Content);
            Assert.Equal(characterIds, list.Characters.Select(character => character.Id));
            Assert.IsType<GameActionItemListEvent>(refresh[4].Content);
            for (int i = 0; i < refresh.Length; i++)
                Assert.Equal(codec.Packets[delivered + i], await ReadPacket());
            delivered += refresh.Length;
        }

        async Task<byte[]> ReadPacket()
        {
            var packet = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(packet);
            return packet;
        }
    }

    // Keep unique fixture names within the client's letter-only, 20-character rule.
    private static string Name(string prefix)
        => prefix + "-" + string.Concat(Guid.NewGuid().ToString("N")[..11]
            .Select(digit => (char)('a' + Convert.ToInt32(digit.ToString(), 16))));

    private sealed class RecordingCodec : IGameCodec
    {
        internal List<GameFrame> Frames { get; } = [];
        internal List<byte[]> Packets { get; } = [];
        public byte[] Encode(GameFrame frame)
        {
            Frames.Add(frame);
            // Deliberately opaque, unique tokens: socket delivery must not require protobuf.
            byte[] packet = [0xfe, checked((byte)Frames.Count), 0xff];
            Packets.Add(packet);
            return packet;
        }
        public GameFrame Decode(ReadOnlySpan<byte> payload) => throw new Exception("Unexpected refresh decode");
    }
}

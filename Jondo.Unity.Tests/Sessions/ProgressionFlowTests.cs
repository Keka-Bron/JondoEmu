using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;
using Xunit;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Tests.Sessions
{
    /// <summary>
    /// The achievement claim, the window and an emote, end to end through a real socket, against
    /// the frames the captures have for them.
    /// </summary>
    /// <remarks>
    /// The character is the one of the captures — 302677754146, account 65924386 — so that what
    /// comes out can be compared byte for byte with what the real server sent to it.
    /// </remarks>
    [Collection(Jondo.Unity.Tests.Quests.AlmanaxTests.AlmanaxClock)]
    public class ProgressionFlowTests
    {
        private const long Character = 302677754146;
        private const long Account = 65924386;

        private static bool Available
            => File.Exists(Paths.WorldDb) && File.Exists(Paths.AchievementsJson) && File.Exists(Paths.EmotesJson);

        private static byte[] Hex(string hex) => Convert.FromHexString(hex);

        private static void Forget()
        {
            DatabaseManager.EnsureProgressionTables();
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                DELETE FROM CharacterAchievements WHERE CharacterId = $c;
                DELETE FROM CharacterAchievementCounters WHERE CharacterId = $c;
                DELETE FROM CharacterEmotes WHERE CharacterId = $c;";
            command.Parameters.AddWithValue("$c", Character);
            command.ExecuteNonQuery();
        }

        /// <summary>A player connected through a loopback socket, in the world on a map of the Pandala route.</summary>
        private sealed class Connected : IAsyncDisposable
        {
            private readonly TcpListener _listener;
            private readonly TcpClient _client;
            private readonly TcpClient _server;

            public GameSession Player { get; }
            public NetworkStream ToClient { get; }
            public NetworkStream FromServer { get; }

            private Connected(TcpListener listener, TcpClient client, TcpClient server)
            {
                _listener = listener;
                _client = client;
                _server = server;
                ToClient = server.GetStream();
                FromServer = client.GetStream();

                Player = new GameSession(ToClient);
                Player.BindAccount(Account, 1);
                Player.State.CharacterId = Character;
                Player.State.CharacterName = "Test";
                Player.State.CharacterLevel = 200;
                Player.State.MapId = 207627266;
                Player.EnterWorld();
                SessionRegistry.Register(Player);
            }

            public static async Task<Connected> OpenAsync()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                var server = await listener.AcceptTcpClientAsync();
                return new Connected(listener, client, server);
            }

            /// <summary>
            /// The read in flight. Kept, because a read that is waited for and abandoned — which
            /// is what checking for silence does — still takes the next frame when it comes.
            /// </summary>
            private Task<byte[]>? _pending;

            private Task<byte[]> Read() => _pending ??= Jondo.Protocol.NetworkMessage.ReadFrameAsync(FromServer);

            public async Task<byte[]> NextAsync(string opcode)
            {
                var read = Read();
                var first = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10)));
                Assert.True(first == read, $"nothing arrived where {opcode} was expected");
                _pending = null;

                byte[] frame = await read;
                byte[]? payload = ConnectionProtocol.ReadPayload(frame, opcode);
                Assert.True(payload != null, $"{opcode} was expected and {NetworkEnvelope.GetMessageTypeUrl(frame)} came");
                return payload!;
            }

            /// <summary>Nothing more arrives for a moment: a refusal is silence.</summary>
            public async Task NothingAsync()
            {
                var read = Read();
                var first = await Task.WhenAny(read, Task.Delay(TimeSpan.FromMilliseconds(400)));
                Assert.True(first != read, "something arrived where the capture has silence");
            }

            public ValueTask DisposeAsync()
            {
                SessionRegistry.Unregister(Player);
                _client.Dispose();
                _server.Dispose();
                _listener.Stop();
                return ValueTask.CompletedTask;
            }
        }

        [Fact]
        public async Task A_claim_pays_the_experience_and_closes_with_mfs_once()
        {
            if (!Available) return;
            Achievements.Load();
            Emotes.Load();
            Forget();

            try
            {
                await using var at = await Connected.OpenAsync();
                using (SessionContext.Push(at.Player))
                {
                    Achievements.LoadFrom(Character);
                    at.Player.State.Experience = ExperienceTable.LevelFloor(200);
                    at.Player.State.Achievements!.Restore(8992, claimed: false);

                    // Pandala route capture, frames 2643-2646: mga -1 then kub, kuf {545}, mfs.
                    // Without that character's 5 % bonus the experience is the base, 520.
                    await AchievementHandler.ClaimAsync(at.ToClient,
                        ConnectionProtocol.Push(Op.Mga, Hex("08ffffffffffffffffff01")));

                    await at.NextAsync(Op.Kub);
                    Assert.Equal(Hex("088804"), await at.NextAsync(Op.Kuf));
                    Assert.Equal(Hex("100120a046"), await at.NextAsync(Op.Mfs));
                    Assert.Equal(ExperienceTable.LevelFloor(200) + 520, at.Player.State.Experience);

                    // Paid once: a second claim finds nothing owed and says nothing.
                    await AchievementHandler.ClaimAsync(at.ToClient,
                        ConnectionProtocol.Push(Op.Mga, Hex("08a046")));
                    await at.NothingAsync();

                    Assert.Contains((8992, true), DatabaseManager.LoadAchievements(Character));
                }
            }
            finally
            {
                Forget();
            }
        }

        [Fact]
        public async Task The_window_answers_its_three_requests_in_the_capture_s_order()
        {
            if (!Available) return;
            Achievements.Load();
            Forget();

            try
            {
                await using var at = await Connected.OpenAsync();
                using (SessionContext.Push(at.Player))
                {
                    Achievements.LoadFrom(Character);

                    // Chats\usando todos los chats, frames 103-108.
                    await AchievementHandler.OpenedAsync(at.ToClient);
                    await at.NextAsync(Op.Mgb);

                    await AchievementHandler.SecondRequestAsync(at.ToClient,
                        Hex("12220a150a13747970652e616e6b616d612e636f6d2f6d667010ffffffffffffffffff01"));
                    Assert.Empty(await at.NextAsync(Op.Mfx));

                    await AchievementHandler.CategoryAsync(at.ToClient,
                        Hex("12260a190a13747970652e616e6b616d612e636f6d2f6d66661202082810ffffffffffffffffff01"));
                    var mfo = ProtoMessage.Parse(await at.NextAsync(Op.Mfo)).Fields;
                    Assert.Equal(23, mfo.Count(f => f.FieldNumber == 2));
                }
            }
            finally
            {
                Forget();
            }
        }

        [Fact]
        public async Task Ontoral_hands_over_today_s_offering_and_only_once_a_day()
        {
            if (!Available || !File.Exists(Paths.AlmanaxJson) || !File.Exists(Paths.QuestsJson)) return;
            QuestEngine.Load();
            Achievements.Load();
            Almanax.Load();
            Forget();

            var clock = Almanax.Clock;
            Almanax.Clock = () => new DateTime(2026, 9, 26, 12, 0, 0);
            try
            {
                await using var at = await Connected.OpenAsync();
                using (SessionContext.Push(at.Player))
                {
                    at.Player.State.CharacterLevel = 50;
                    at.Player.State.MapId = 101450251;
                    QuestEngine.LoadFrom(Character);
                    Achievements.LoadFrom(Character);

                    // INFERRED: no capture visits the sanctuary. Talking to Ontoral Zo (NPC 1625)
                    // and answering his opening line hands over the day's offering: quest 965,
                    // "PL>19&Ad=11", on 26 September.
                    Assert.Equal(1625, Almanax.Giver);
                    at.Player.State.OpenDialogueNpcId = 1625;
                    await QuestEngine.OnReplyAsync(at.ToClient, 12047);
                    Assert.Equal(QuestProtocol.BuildQuestStarted(965), await at.NextAsync(Op.Ief));
                    Assert.True(QuestEngine.Log!.Active(965));

                    // Once handed in, not again the same day.
                    Achievements.SetTally(Almanax.OfferingKind, 965, Almanax.DayKey(Almanax.Clock()));
                    Assert.Empty(QuestEngine.OfferedRightNowBy(1625, 101450251));
                    Assert.False(QuestEngine.CanBeTakenFrom(1625, 101450251, 966));
                }
            }
            finally
            {
                Almanax.Clock = clock;
                Almanax.Reset();
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM CharacterQuests WHERE CharacterId = $c;";
                command.Parameters.AddWithValue("$c", Character);
                try { command.ExecuteNonQuery(); } catch (SqliteException) { }
                Forget();
            }
        }

        [Fact]
        public async Task The_juggler_plays_for_the_map_and_a_second_one_too_soon_does_not()
        {
            if (!Available) return;
            Emotes.Load();
            Forget();

            try
            {
                await using var at = await Connected.OpenAsync();
                using (SessionContext.Push(at.Player))
                {
                    Emotes.LoadFrom(Character);

                    // Not learned yet: silence, whatever the client asks.
                    await EmoteHandler.PlayAsync(at.ToClient, ConnectionProtocol.Push(Op.Khl, Hex("081d")));
                    await at.NothingAsync();

                    // Learned — as an achievement reward would teach it — and announced with khi.
                    Assert.True(await Emotes.LearnAsync(at.ToClient, 29));
                    Assert.Equal(Hex("081d"), await at.NextAsync(Op.Khi));

                    // Emotes\usar emote malabares, frames 4-5, byte for byte.
                    await EmoteHandler.PlayAsync(at.ToClient, ConnectionProtocol.Push(Op.Khl, Hex("081d")));
                    Assert.Equal(Hex("08a28280c8e708181d28a2dab71f320f416e696d456d6f74654a7567676c65"),
                        await at.NextAsync(Op.Khh));

                    // Sitting straight after is refused as the capture refuses it: no answer.
                    await EmoteHandler.PlayAsync(at.ToClient, ConnectionProtocol.Push(Op.Khl, Hex("0801")));
                    await at.NothingAsync();

                    Assert.Contains(29, DatabaseManager.LoadEmotes(Character));
                }
            }
            finally
            {
                Forget();
            }
        }
    }
}

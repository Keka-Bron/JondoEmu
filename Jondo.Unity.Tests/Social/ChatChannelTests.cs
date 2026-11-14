using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Social
{
    /// <summary>
    /// Alone: a server-wide line reaches every session registered, those of the tests running
    /// beside -- ChatCommunityTests read it in place of its own answer.
    /// </summary>
    [CollectionDefinition("server-wide chat", DisableParallelization = true)]
    public sealed class ServerWideChatCollection { }

    /// <summary>
    /// A chat line reaches whoever hears its channel: trade, recruitment and community the whole
    /// server, the party its members. They went back to whoever wrote them alone.
    /// </summary>
    [Collection("server-wide chat")]
    public class ChatChannelTests
    {
        private static readonly GameCodec Codec = new();
        private static long _nextId = 93_000_000_000;

        private sealed class Peer : System.IDisposable
        {
            internal ConcurrentQueue<byte[]> Packets { get; } = new();
            internal GameSession Session { get; }
            internal Peer(long mapId)
            {
                Session = new GameSession(Codec, bytes => { Packets.Enqueue(bytes); return Task.CompletedTask; });
                long id = Interlocked.Increment(ref _nextId);
                Session.BindAccount(id, 1);
                Session.State.CharacterId = id;
                Session.State.MapId = mapId;
                Session.EnterWorld();
                Assert.True(SessionRegistry.Register(Session));
            }
            public void Dispose() => SessionRegistry.Unregister(Session);
        }

        private static readonly byte[] Line = { 1, 2, 3 };

        [Theory]
        [InlineData(ChatChannels.Trade)]
        [InlineData(ChatChannels.Recruitment)]
        [InlineData(ChatChannels.Community)]
        public async Task The_server_wide_channels_reach_another_map(int channel)
        {
            using var speaker = new Peer(1001);
            using var elsewhere = new Peer(2002);

            Assert.True(await ChatChannels.SayAsync(speaker.Session, channel, Line) >= 1);
            Assert.Contains(Line, elsewhere.Packets);
            Assert.Empty(speaker.Packets);   // his own copy is sent apart, before
        }

        [Fact]
        public async Task The_general_channel_stays_on_the_map()
        {
            using var speaker = new Peer(1003);
            using var beside = new Peer(1003);
            using var elsewhere = new Peer(2004);

            await ChatChannels.SayAsync(speaker.Session, ChatChannels.General, Line);
            Assert.Contains(Line, beside.Packets);
            Assert.DoesNotContain(Line, elsewhere.Packets);
        }

        /// <summary>Trade and recruitment take a line every two minutes, each its own; the others none.</summary>
        [Fact]
        public void Trade_and_recruitment_wait_two_minutes_between_lines()
        {
            long who = Interlocked.Increment(ref _nextId);
            var t0 = new System.DateTime(2026, 11, 14, 2, 0, 0, System.DateTimeKind.Utc);
            const int player = Jondo.Unity.Launcher.Roles.Jugador;

            Assert.Equal(0, ChatChannels.WaitLeft(who, ChatChannels.Trade, player, t0));
            Assert.Equal(120, ChatChannels.WaitLeft(who, ChatChannels.Trade, player, t0));
            Assert.Equal(90, ChatChannels.WaitLeft(who, ChatChannels.Trade, player, t0.AddSeconds(30)));
            Assert.Equal(1, ChatChannels.WaitLeft(who, ChatChannels.Trade, player, t0.AddSeconds(119.5)));
            // A refused line does not start the wait again.
            Assert.Equal(0, ChatChannels.WaitLeft(who, ChatChannels.Trade, player, t0.AddMinutes(2)));

            // Recruitment counts on its own; the general channel and the community wait for nothing.
            Assert.Equal(0, ChatChannels.WaitLeft(who, ChatChannels.Recruitment, player, t0.AddMinutes(2)));
            Assert.Equal(0, ChatChannels.WaitLeft(who, ChatChannels.General, player, t0));
            Assert.Equal(0, ChatChannels.WaitLeft(who, ChatChannels.General, player, t0));
            Assert.Equal(0, ChatChannels.WaitLeft(who, ChatChannels.Community, player, t0));
            Assert.Equal(0, ChatChannels.WaitLeft(who, ChatChannels.Community, player, t0));

            // A moderator, not at all.
            long staff = Interlocked.Increment(ref _nextId);
            Assert.Equal(0, ChatChannels.WaitLeft(staff, ChatChannels.Trade, Jondo.Unity.Launcher.Roles.Moderador, t0));
            Assert.Equal(0, ChatChannels.WaitLeft(staff, ChatChannels.Trade, Jondo.Unity.Launcher.Roles.Moderador, t0));
        }

        [Fact]
        public async Task The_party_channel_reaches_the_members_alone()
        {
            using var leader = new Peer(1005);
            using var member = new Peer(2006);
            using var stranger = new Peer(1005);
            var party = Parties.Create(leader.Session.CharacterId);
            try
            {
                Assert.True(Parties.Invite(party, member.Session.CharacterId, leader.Session.CharacterId));
                Assert.True(Parties.Accept(party, member.Session.CharacterId));

                Assert.Equal(1, await ChatChannels.SayAsync(leader.Session, ChatChannels.Party, Line));
                Assert.Contains(Line, member.Packets);
                Assert.DoesNotContain(Line, stranger.Packets);

                // Nobody in a party: nobody hears.
                Assert.Equal(0, await ChatChannels.SayAsync(stranger.Session, ChatChannels.Party, Line));
            }
            finally { Parties.Dissolve(party); }
        }
    }
}

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Managers;
using Jondo.Unity.Launcher.Network;
using Xunit;

namespace Jondo.Unity.Tests.Sessions
{
    /// <summary>
    /// One player's state, and one player's locks, stay inside that player's session.
    /// </summary>
    /// <remarks>
    /// Up to eight clients run at once and they used to share statics. Everything here is a bug
    /// that only appears with a second player connected, which is exactly the kind nobody meets
    /// while developing alone.
    /// </remarks>
    public class SessionIsolationTests
    {
        /// <summary>
        /// The fight lock belongs to each session, not to all of them.
        ///
        /// It was a static SemaphoreSlim in FightHandler, one for all eight connections, and it is
        /// held across the socket write: one slow client left everybody else unable to move a piece
        /// in their own fight. What is checked is that two sessions can hold it at once, which is
        /// precisely what used to be impossible.
        /// </summary>
        [Fact]
        public void Two_sessions_do_not_share_the_fight_lock()
        {
            var one = GameSession.SinSocket();
            var other = GameSession.SinSocket();

            Assert.NotSame(one.UnoCadaVez, other.UnoCadaVez);

            one.UnoCadaVez.Wait();
            try
            {
                Assert.True(other.UnoCadaVez.Wait(0),
                    "a session holding the lock blocked another one: one slow client freezes everybody's fight");
                other.UnoCadaVez.Release();
            }
            finally
            {
                one.UnoCadaVez.Release();
            }
        }

        [Fact]
        public void Player_caches_do_not_leak_across_sessions()
        {
            var first = GameSession.SinSocket();
            var second = GameSession.SinSocket();

            first.State.EquipmentItems[101] = new Equipment.Item { Uid = 101 };
            first.State.ChosenSpells[1] = 1001;
            first.State.SpellBar[0] = 1001;
            first.State.OpenNpcShopId = 11;

            second.State.EquipmentItems[202] = new Equipment.Item { Uid = 202 };
            second.State.ChosenSpells[1] = 2002;
            second.State.SpellBar[0] = 2002;
            second.State.OpenNpcShopId = 22;

            using (SessionContext.Push(first))
            {
                Assert.NotNull(Equipment.ByUid(101));
                Assert.Null(Equipment.ByUid(202));
                Assert.Equal(1001, SpellChoices.Chosen[1]);
                Assert.Equal(1001, SpellChoices.Bar[0]);
                Assert.Equal(11, first.State.OpenNpcShopId);
            }

            using (SessionContext.Push(second))
            {
                Assert.NotNull(Equipment.ByUid(202));
                Assert.Null(Equipment.ByUid(101));
                Assert.Equal(2002, SpellChoices.Chosen[1]);
                Assert.Equal(2002, SpellChoices.Bar[0]);
                Assert.Equal(22, second.State.OpenNpcShopId);
            }
        }

        /// <summary>
        /// Two writes never interleave on one socket.
        /// </summary>
        /// <remarks>
        /// A frame cut in half by another frame is not a recoverable error: the client loses the
        /// stream and there is no resynchronisation. It happened whenever a timer's burst met the
        /// burst of whatever was serving the client.
        /// </remarks>
        [Fact]
        public async Task Writes_on_one_socket_are_serialised()
        {
            var stream = new OverlapDetectingStream();

            await Task.WhenAll(
                Jondo.Protocol.NetworkMessage.WriteRawFrameAsync(stream, new byte[] { 1, 2, 3 }),
                Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, new byte[] { 4, 5, 6 }),
                Jondo.Protocol.NetworkMessage.WriteRawFrameAsync(stream, new byte[] { 7, 8, 9 }));

            Assert.False(stream.OverlapDetected, "packet writes overlapped on one socket");
        }

        /// <summary>A stream that notices if a second write starts before the first has finished.</summary>
        private sealed class OverlapDetectingStream : Stream
        {
            private int _writing;

            public bool OverlapDetected { get; private set; }

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                if (Interlocked.Exchange(ref _writing, 1) == 1) OverlapDetected = true;

                // Long enough that a second writer would get in if nothing were holding it back.
                await Task.Delay(5, token).ConfigureAwait(false);

                Interlocked.Exchange(ref _writing, 0);
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
            {
                if (Interlocked.Exchange(ref _writing, 1) == 1) OverlapDetected = true;
                await Task.Delay(5, token).ConfigureAwait(false);
                Interlocked.Exchange(ref _writing, 0);
            }

            public override void Write(byte[] buffer, int offset, int count)
                => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => 0;
            public override long Position { get => 0; set { } }
            public override void Flush() { }
            public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
            public override int Read(byte[] buffer, int offset, int count) => 0;
            public override long Seek(long offset, SeekOrigin origin) => 0;
            public override void SetLength(long value) { }
        }
    }
}

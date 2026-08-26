using System.IO;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// A frame with an invented length does not get to allocate memory.
    /// </summary>
    /// <remarks>
    /// Five bytes — FF FF FF FF 07 — used to ask for a 2 GB array before a single byte of content
    /// was read, and eight connections were enough to bring the server down without authenticating.
    /// Everything here arrives from a socket, so it can be wrong on purpose.
    /// </remarks>
    public class FrameLimitTests
    {
        private static byte[]? Read(params byte[] bytes)
            => Jondo.Protocol.NetworkMessage.ReadFrameAsync(new MemoryStream(bytes))
                                            .GetAwaiter().GetResult();

        [Fact]
        public void A_two_gigabyte_length_is_refused()
        {
            // 0xFFFFFFFF07 = 2147483647. This used to be new byte[2147483647].
            Assert.Null(Read(0xFF, 0xFF, 0xFF, 0xFF, 0x07));
        }

        [Fact]
        public void A_length_varint_that_never_ends_is_refused()
        {
            // Without a cap on the number of bytes, the loop reads forever.
            Assert.Null(Read(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF));
        }

        [Fact]
        public void An_ordinary_frame_still_gets_through()
        {
            // The other half of the check: a cap set too low would break every connection instead.
            byte[]? frame = Read(0x03, 0x0A, 0x01, 0x41);

            Assert.NotNull(frame);
            Assert.Equal(3, frame!.Length);
        }

        [Fact]
        public void An_empty_stream_gives_nothing_rather_than_throwing()
        {
            // A socket that closes mid-handshake looks exactly like this, and it happens whenever
            // somebody alt-F4s the client.
            Assert.Null(Read());
        }

        [Fact]
        public void A_frame_that_ends_early_gives_nothing()
        {
            // Says four bytes, carries two. A short read must not be mistaken for a whole frame.
            Assert.Null(Read(0x04, 0x0A, 0x01));
        }
    }
}

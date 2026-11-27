using System.IO;
using System.Threading.Tasks;
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
        private static async Task<byte[]?> ReadAsync(params byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            return await Jondo.Protocol.NetworkMessage.ReadFrameAsync(stream);
        }

        [Fact]
        public async Task A_two_gigabyte_length_is_refused()
        {
            // 0xFFFFFFFF07 = 2147483647. This used to be new byte[2147483647].
            Assert.Null(await ReadAsync(0xFF, 0xFF, 0xFF, 0xFF, 0x07));
        }

        [Fact]
        public async Task A_length_varint_that_never_ends_is_refused()
        {
            // Without a cap on the number of bytes, the loop reads forever.
            Assert.Null(await ReadAsync(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF));
        }

        [Fact]
        public async Task An_ordinary_frame_still_gets_through()
        {
            // The other half of the check: a cap set too low would break every connection instead.
            byte[]? frame = await ReadAsync(0x03, 0x0A, 0x01, 0x41);

            Assert.NotNull(frame);
            Assert.Equal(3, frame!.Length);
        }

        [Fact]
        public async Task A_socket_with_no_account_is_held_to_a_lower_ceiling()
        {
            // 0x8180 0x04 = 65537, one byte over what a socket that has not said who it is may
            // announce: the array is made before the frame arrives, so nothing is read of it.
            int ceiling = Jondo.Unity.Server.Network.ConnectionGuard.UnauthenticatedFrame;
            using var over = new MemoryStream(new byte[] { 0x81, 0x80, 0x04 });
            Assert.Null(await Jondo.Protocol.NetworkMessage.ReadFrameAsync(over, ceiling));

            // And an ordinary frame passes under it.
            using var under = new MemoryStream(new byte[] { 0x03, 0x0A, 0x01, 0x41 });
            byte[]? frame = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(under, ceiling);
            Assert.NotNull(frame);
        }

        [Fact]
        public async Task An_empty_stream_gives_nothing_rather_than_throwing()
        {
            // A socket that closes mid-handshake looks exactly like this, and it happens whenever
            // somebody alt-F4s the client.
            Assert.Null(await ReadAsync());
        }

        [Fact]
        public async Task A_frame_that_ends_early_gives_nothing()
        {
            // Says four bytes, carries two. A short read must not be mistaken for a whole frame.
            Assert.Null(await ReadAsync(0x04, 0x0A, 0x01));
        }
    }
}

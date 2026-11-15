using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// What a socket that has not said who it is may take from the server: how long, how many
    /// from one address, and how large a frame.
    /// </summary>
    /// <remarks>
    /// Nothing bounded it. A socket that connected and sent nothing waited in ReadFrameAsync for
    /// ever, outside every count; one that sent a first game frame took a place in
    /// <see cref="SessionRegistry"/> -- the server's whole capacity -- before presenting a ticket,
    /// and kept it for as long as it liked; and each could announce an 8 MB frame, whose array is
    /// made before a byte of it arrives. A few hundred idle sockets from one machine were the
    /// server full, or its memory.
    ///
    /// The loopback is left alone: the simulated players and a launcher on the server's own
    /// machine come from there, by the dozen.
    /// </remarks>
    internal static class ConnectionGuard
    {
        /// <summary>How long a new socket has to send its first frame.</summary>
        public static readonly TimeSpan FirstFrame = TimeSpan.FromSeconds(15);

        /// <summary>How long a game socket has to present its ticket once its first frame is in.</summary>
        public static readonly TimeSpan Authentication = TimeSpan.FromSeconds(30);

        /// <summary>The sockets one address may have that have not presented a ticket.</summary>
        public const int PendingPerAddress = 16;

        /// <summary>
        /// The largest frame read from a socket with no account behind it. The ticket and the
        /// connection server's messages are a few hundred bytes.
        /// </summary>
        public const int UnauthenticatedFrame = 64 * 1024;

        private static readonly ConcurrentDictionary<string, int> _waiting = new();

        /// <summary>The address at the socket's other end, or empty when it cannot be read.</summary>
        public static string AddressOf(Socket? socket)
        {
            try
            {
                return (socket?.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static bool Exempt(string address)
            => address.Length == 0 || (IPAddress.TryParse(address, out var ip) && IPAddress.IsLoopback(ip));

        /// <summary>
        /// Counts a socket that has not presented a ticket against its address. Null when the
        /// address already has as many as it may; otherwise what gives the place back, once.
        /// </summary>
        public static IDisposable? Enter(Socket? socket)
        {
            string address = AddressOf(socket);
            if (Exempt(address)) return new Place(null);

            int waiting = _waiting.AddOrUpdate(address, 1, (_, n) => n + 1);
            if (waiting + UnauthenticatedSessionsOf(address) > PendingPerAddress)
            {
                Leave(address);
                return null;
            }
            return new Place(address);
        }

        /// <summary>Whether an address may have one more game session with no ticket yet.</summary>
        public static bool MayOpenSession(string address)
            => Exempt(address)
               || UnauthenticatedSessionsOf(address) + (_waiting.TryGetValue(address, out int n) ? n : 0) < PendingPerAddress;

        private static int UnauthenticatedSessionsOf(string address)
            => SessionRegistry.All().Count(s => !s.IsAuthenticated && s.State.ClientIp == address);

        private static void Leave(string address)
        {
            int left = _waiting.AddOrUpdate(address, 0, (_, n) => Math.Max(0, n - 1));
            if (left == 0) _waiting.TryRemove(new System.Collections.Generic.KeyValuePair<string, int>(address, 0));
        }

        private sealed class Place : IDisposable
        {
            private string? _address;
            public Place(string? address) => _address = address;

            public void Dispose()
            {
                string? address = System.Threading.Interlocked.Exchange(ref _address, null);
                if (address != null) Leave(address);
            }
        }

        /// <summary>
        /// Closes the socket once the time is up unless <paramref name="settled"/> says it did what
        /// was waited for. The read it was blocked in ends, and its connection goes out its own way.
        /// </summary>
        public static void CloseUnless(Socket? socket, TimeSpan after, Func<bool> settled, string what)
        {
            if (socket == null || Exempt(AddressOf(socket))) return;
            _ = Task.Delay(after).ContinueWith(_ =>
            {
                try
                {
                    if (settled() || !socket.Connected) return;
                    Console.WriteLine($"[Guard] {AddressOf(socket)} closed: {what} within {after.TotalSeconds:0} s.");
                    socket.Close();
                }
                catch (Exception)
                {
                    // Already gone.
                }
            }, TaskScheduler.Default);
        }
    }
}

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Jondo.Unity.Launcher.Network
{
    /// <summary>One connected game client and all state that belongs exclusively to it.</summary>
    public sealed class GameSession
    {
        public GameSession(NetworkStream stream) => Stream = stream ?? throw new ArgumentNullException(nameof(stream));

        public Guid Id { get; } = Guid.NewGuid();
        public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;
        public NetworkStream Stream { get; }
        public SessionState State { get; } = new SessionState();
        public long AccountId { get; private set; }
        public int ServerId { get; private set; }
        public long CharacterId => State.CharacterId;
        public long MapId => State.MapId;
        public bool IsAuthenticated => AccountId > 0;
        public bool HasCharacter => CharacterId > 0;
        public bool IsInWorld { get; private set; }

        /// <summary>Sends one length-prefixed game packet to this client's socket.</summary>
        public Task SendAsync(byte[] packet) => Jondo.Protocol.NetworkMessage.WriteFrameAsync(Stream, packet);

        public void BindAccount(long accountId, int serverId)
        {
            if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId));
            AccountId = accountId;
            ServerId = serverId;
        }

        public void EnterWorld()
        {
            if (!IsAuthenticated || !HasCharacter)
                throw new InvalidOperationException("A session needs an account and character before entering the world.");
            IsInWorld = true;
        }

        public void LeaveWorld() => IsInWorld = false;
    }

    /// <summary>
    /// Carries a session through the asynchronous handler pipeline. It contains no shared player
    /// state: AsyncLocal gives each connection flow its own GameSession.
    /// </summary>
    public static class SessionContext
    {
        private static readonly AsyncLocal<GameSession?> CurrentSlot = new AsyncLocal<GameSession?>();

        public static GameSession Current => CurrentSlot.Value
            ?? throw new InvalidOperationException("No game session is bound to the current async flow.");
        public static SessionState State => Current.State;

        public static IDisposable Push(GameSession session)
        {
            var previous = CurrentSlot.Value;
            CurrentSlot.Value = session;
            return new Scope(previous);
        }

        private sealed class Scope : IDisposable
        {
            private readonly GameSession? _previous;
            private bool _disposed;
            public Scope(GameSession? previous) => _previous = previous;
            public void Dispose()
            {
                if (_disposed) return;
                CurrentSlot.Value = _previous;
                _disposed = true;
            }
        }
    }
}

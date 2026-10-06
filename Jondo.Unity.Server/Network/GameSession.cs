using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Jondo.Unity.Server.Network
{
    /// <summary>One connected game client and all state that belongs exclusively to it.</summary>
    public sealed class GameSession
    {
        public GameSession(NetworkStream stream) => Stream = stream ?? throw new ArgumentNullException(nameof(stream));

        private GameSession() { }

        /// <summary>
        /// A session with no client behind it, for what runs outside a connection: startup,
        /// background threads and tests. It keeps state and sends nothing.
        /// </summary>
        public static GameSession SinSocket() => new GameSession();

        /// <summary>
        /// This session's turn: one at a time.
        ///
        /// What arrives from the client and the fight timers ask for it, so that they do not
        /// trample the fight state mid-burst. It belongs TO THE SESSION on purpose.
        ///
        /// It was in FightHandler as a static SemaphoreSlim, one for the eight connections, and
        /// it is held during the write to the socket: a slow client —or one that decides
        /// not to read— left all the others unable to make a move in their own fight until
        /// that one finished. The reason that justified it was that NetworkMessage split each
        /// frame into two writes, the length and the body; today WriteSerializedAsync builds the
        /// whole frame in an array and writes it in one go, behind a lock PER SOCKET, so
        /// that reason no longer exists.
        ///
        /// When there are group fights this will fall short —two sessions touching the same
        /// FightInstance— and a lock per fight will be needed. Today each fight belongs to a single
        /// player (LeaveFight looks for the fights HE is in), so work that cannot yet be tested
        /// is not done in advance.
        /// </summary>
        public SemaphoreSlim UnoCadaVez { get; } = new SemaphoreSlim(1, 1);

        public Guid Id { get; } = Guid.NewGuid();
        public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;
        public NetworkStream? Stream { get; }

        /// <summary>Whether it has a client on the other side that something can be sent to.</summary>
        public bool TieneCliente => Stream != null;
        public SessionState State { get; } = new SessionState();
        public long AccountId { get; private set; }
        public int ServerId { get; private set; }
        public long CharacterId => State.CharacterId;
        public long MapId => State.MapId;
        public bool IsAuthenticated => AccountId > 0;
        public bool HasCharacter => CharacterId > 0;
        public bool IsInWorld { get; private set; }

        /// <summary>
        /// Sends a packet to this client. If the session has no socket —the fallback one— it does
        /// nothing, instead of blowing up.
        /// </summary>
        public Task SendAsync(byte[] packet)
            => Stream == null
                ? Task.CompletedTask
                : Jondo.Protocol.NetworkMessage.WriteFrameAsync(Stream, packet);

        public void BindAccount(long accountId, int serverId, string language = "es")
        {
            if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId));
            AccountId = accountId;
            ServerId = serverId;
            State.Language = string.IsNullOrWhiteSpace(language) ? "es" : language;
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

        /// <summary>
        /// The session bound to this thread, or <c>null</c> if there is none.
        ///
        /// What is outside a connection exists and is normal: the server's startup, the turn
        /// clock thread, the one that repopulates the monsters, the test tools. All
        /// those build packets without having a client behind them.
        /// </summary>
        public static GameSession? Actual => CurrentSlot.Value;

        /// <summary>
        /// This thread's session. If none is bound it returns a LOOSE one, without a socket, instead
        /// of blowing up.
        ///
        /// Here there was a <c>throw</c>, and with 295 places asking for the state it was enough for ONE to
        /// be called outside a connection's thread to bring down whatever it touched. The earlier global
        /// could not fail like that because there was always something to read, so the change turned
        /// routes that worked into fatal ones.
        ///
        /// Whoever really needs a client behind must look at <see cref="Actual"/> and decide;
        /// whoever only wants to read or write state gets an empty drawer and carries on.
        /// </summary>
        public static GameSession Current => CurrentSlot.Value ?? Suelta;
        public static SessionState State => Current.State;

        /// <summary>
        /// The fallback session, without a socket. Nothing can be sent to it —<see cref="GameSession.SendAsync"/>
        /// rejects it— but its state is read and written without getting in the way.
        /// </summary>
        private static readonly GameSession Suelta = GameSession.SinSocket();

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

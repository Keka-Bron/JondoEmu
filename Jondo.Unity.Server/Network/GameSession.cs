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
        /// Una sesión sin cliente detrás, para lo que corre fuera de una conexión: el arranque,
        /// los hilos de fondo y las pruebas. Guarda estado y no manda nada.
        /// </summary>
        public static GameSession SinSocket() => new GameSession();

        /// <summary>
        /// El turno de esta sesión: uno cada vez.
        ///
        /// Lo piden lo que llega del cliente y los temporizadores de combate, para que no se
        /// pisen el estado del combate a media ráfaga. Es DE LA SESIÓN a propósito.
        ///
        /// Estaba en FightHandler como un SemaphoreSlim estático, uno para las ocho conexiones, y
        /// se sostiene durante la escritura en el socket: un cliente lento —o uno al que le da
        /// por no leer— dejaba a todos los demás sin poder mover ficha en su propio combate hasta
        /// que aquél terminara. El motivo que lo justificaba era que NetworkMessage partía cada
        /// trama en dos escrituras, la longitud y el cuerpo; hoy WriteSerializedAsync monta la
        /// trama entera en un array y la escribe de una vez, detrás de un candado POR SOCKET, así
        /// que ese motivo ya no existe.
        ///
        /// Cuando haya combates de grupo esto se quedará corto —dos sesiones tocando un mismo
        /// FightInstance— y hará falta un candado por combate. Hoy cada combate es de un jugador
        /// solo (LeaveFight busca los combates donde está ÉL), así que no se adelanta trabajo que
        /// todavía no se puede probar.
        /// </summary>
        public SemaphoreSlim UnoCadaVez { get; } = new SemaphoreSlim(1, 1);

        public Guid Id { get; } = Guid.NewGuid();
        public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;
        public NetworkStream? Stream { get; }

        /// <summary>Si tiene un cliente al otro lado al que se le pueda mandar algo.</summary>
        public bool TieneCliente => Stream != null;
        public SessionState State { get; } = new SessionState();
        public long AccountId { get; private set; }
        public int ServerId { get; private set; }
        public int LaunchInstanceId { get; private set; }
        public long CharacterId => State.CharacterId;
        public long MapId => State.MapId;
        public bool IsAuthenticated => AccountId > 0;
        public bool HasCharacter => CharacterId > 0;
        public bool IsInWorld { get; private set; }

        /// <summary>
        /// Manda un paquete a este cliente. Si la sesión no tiene socket —la de reserva— no hace
        /// nada, en vez de reventar.
        /// </summary>
        public Task SendAsync(byte[] packet)
            => Stream == null
                ? Task.CompletedTask
                : Jondo.Protocol.NetworkMessage.WriteFrameAsync(Stream, packet);

        public void BindAccount(long accountId, int serverId, string language = "es",
                                int launchInstanceId = 0)
        {
            if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId));
            AccountId = accountId;
            ServerId = serverId;
            LaunchInstanceId = launchInstanceId;
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
        /// La sesión atada a este hilo, o <c>null</c> si no hay ninguna.
        ///
        /// Lo de fuera de una conexión existe y es normal: el arranque del servidor, el hilo del
        /// reloj de los turnos, el que repuebla los monstruos, las herramientas de prueba. Todos
        /// ésos arman paquetes sin tener un cliente detrás.
        /// </summary>
        public static GameSession? Actual => CurrentSlot.Value;

        /// <summary>
        /// La sesión de este hilo. Si no hay ninguna atada devuelve una SUELTA, sin socket, en vez
        /// de reventar.
        ///
        /// Aquí había un <c>throw</c>, y con 295 sitios pidiendo el estado bastaba con que UNO se
        /// llamara fuera del hilo de una conexión para tumbar lo que tocara. El global de antes no
        /// podía fallar así porque siempre había algo que leer, de modo que el cambio convertía en
        /// mortales rutas que funcionaban.
        ///
        /// Quien de verdad necesite un cliente detrás debe mirar <see cref="Actual"/> y decidir;
        /// quien sólo quiera leer o escribir estado se lleva un cajón vacío y sigue.
        /// </summary>
        public static GameSession Current => CurrentSlot.Value ?? Suelta;
        public static SessionState State => Current.State;

        /// <summary>
        /// La sesión de reserva, sin socket. No se le puede mandar nada —<see cref="GameSession.SendAsync"/>
        /// lo rechaza— pero su estado se lee y se escribe sin estorbar.
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

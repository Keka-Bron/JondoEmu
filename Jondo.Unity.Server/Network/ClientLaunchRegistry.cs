using Jondo.Unity.Launcher;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// Associates one launcher invocation with one account through Zaap and the connection server.
    /// Every lookup uses a client-owned value, so concurrent clients cannot overwrite one another.
    /// </summary>
    public static class ClientLaunchRegistry
    {
        /// <summary>
        /// Clientes que puede tener abiertos a la vez UNA misma dirección. Ocho, que es lo que cabe
        /// en un grupo de Dofus. NO es la capacidad del servidor: esa es Contract.ClientesEnTotal.
        /// </summary>
        public const int MaximumClients = Jondo.Unity.Launcher.Contract.ClientesPorIp;
        public sealed class Launch
        {
            public int InstanceId { get; init; }
            public long AccountId { get; init; }
            public string Hash { get; init; } = "";
            public string LauncherToken { get; init; } = "";

            /// <summary>
            /// El idioma con el que arranca este cliente. Por defecto el del lanzador, que es
            /// español salvo que se cambie: aquí ponía "fr" a pelo.
            /// </summary>
            public string Language { get; init; } = "es";

            /// <summary>Desde dónde se lanzó. Agrupa los clientes de una misma persona.</summary>
            public string Ip { get; init; } = "";
            public DateTime CreatedAtUtc { get; init; }

            /// <summary>
            /// La ultima vez que este lanzamiento dio senales de vida.
            /// </summary>
            /// <remarks>
            /// No es lo mismo que CreatedAtUtc y esa diferencia es todo el arreglo. Antes el
            /// barrido se saltaba cualquier lanzamiento que tuviera entrada en ByGameSession, y
            /// esa entrada la pone el handshake de Thrift y no la quita nadie: un cliente que se
            /// moria DESPUES del handshake -y con el lanzador cerrado tambien- dejaba la cuenta
            /// marcada como ocupada hasta reiniciar el servidor, y Register rechazaba todos los
            /// intentos siguientes con "cuenta-ya-abierta".
            ///
            /// Tener entrada ahi prueba que ALGUNA VEZ conecto, no que siga estando. Esto prueba
            /// lo segundo: se toca cada vez que alguien resuelve su sesion de juego, que es lo que
            /// hace un cliente vivo una y otra vez.
            /// </remarks>
            public DateTime LastSeenUtc { get; set; }
        }

        private static readonly ConcurrentDictionary<string, Launch> ByHash =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Launch> ByGameSession =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, long> Tokens =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<long, Launch> ByAccount = new();
        private static readonly object RegistrationGate = new();
        private static int _nextInstanceId;

        public static Launch Register(long accountId, string launcherToken, string hash, string language,
                                      string ip = "")
        {
            if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId));
            if (string.IsNullOrWhiteSpace(hash)) throw new ArgumentException("A launch hash is required.", nameof(hash));

            string deDonde = string.IsNullOrWhiteSpace(ip) ? Contract.LocalIp : ip.Trim();

            lock (RegistrationGate)
            {
                // Los dos rechazos viajan como CÓDIGO, no como frase.
                //
                // Estaban en francés escrito a pelo; luego pasaron por el catálogo de textos del
                // lanzador, y eso dejaba a un trozo de servidor leyendo las preferencias de idioma
                // del usuario en %APPDATA%. Un servidor no traduce: dice qué ha pasado y quien
                // tenga una ventana delante decide en qué idioma se lo cuenta a la persona.
                if (ByAccount.ContainsKey(accountId))
                    throw new InvalidOperationException(Contract.MotivoCuentaYaAbierta);

                // El tope de ocho es POR DIRECCIÓN, no del servidor entero.
                //
                // Contaba ByAccount.Count, o sea todos los clientes de todo el mundo: con el
                // servidor en una máquina y los jugadores en otras, el noveno cliente del servidor
                // se rechazaba aunque fuera el primero de esa persona. El ocho viene del grupo de
                // Dofus y es de una persona, no del servidor.
                int suyos = 0;
                foreach (var otro in ByAccount.Values)
                {
                    if (string.Equals(otro.Ip, deDonde, StringComparison.OrdinalIgnoreCase)) suyos++;
                }
                if (suyos >= Contract.ClientesPorIp)
                    throw new InvalidOperationException(Contract.MotivoTopeDeClientes);

                var launch = new Launch
                {
                    InstanceId = Interlocked.Increment(ref _nextInstanceId),
                    AccountId = accountId,
                    Hash = hash,
                    LauncherToken = launcherToken ?? "",
                    Language = string.IsNullOrWhiteSpace(language) ? "es" : language,
                    Ip = deDonde,
                    CreatedAtUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow,
                };
                ByHash[hash] = launch;
                ByAccount[accountId] = launch;
                RegisterToken(accountId, launcherToken);
                return launch;
            }
        }

        public static bool TryConnect(int instanceId, string hash, out string gameSession)
        {
            gameSession = "";
            if (string.IsNullOrWhiteSpace(hash) || !ByHash.TryGetValue(hash, out var launch)) return false;
            if (launch.InstanceId != instanceId) return false;

            gameSession = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            ByGameSession[gameSession] = launch;
            launch.LastSeenUtc = DateTime.UtcNow;
            return true;
        }

        public static bool TryGetByGameSession(string gameSession, out Launch? launch)
        {
            if (string.IsNullOrWhiteSpace(gameSession))
            {
                launch = null;
                return false;
            }
            if (!ByGameSession.TryGetValue(gameSession, out launch)) return false;

            // Senal de vida. Un cliente vivo pasa por aqui una y otra vez -cada vez que hay que
            // resolver de quien es esta sesion-, y uno muerto no vuelve nunca.
            if (launch != null) launch.LastSeenUtc = DateTime.UtcNow;
            return true;
        }

        public static void RegisterToken(long accountId, string? token)
        {
            if (accountId > 0 && !string.IsNullOrWhiteSpace(token)) Tokens[token] = accountId;
        }

        public static long ResolveToken(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return 0;
            if (Tokens.TryGetValue(token, out long accountId)) return accountId;

            // La sesión del lanzador primero: el token de juego se lo rota el cliente cada vez que
            // arranca, así que el que el lanzador guardó de la vez anterior sólo sigue estando en
            // su columna.
            long suya = DatabaseManager.GetAccountIdByLauncherToken(token);
            return suya != 0 ? suya : DatabaseManager.GetAccountIdByToken(token);
        }

        /// <summary>
        /// The launch this account is running, if it still has one.
        /// </summary>
        /// <remarks>
        /// Added for the language: the launcher is the only party that knows which --langCode the
        /// client was started with, and it puts it here. Everything downstream that wants to answer
        /// a player in their own language has to come through this, because the authentication
        /// request does not carry it.
        /// </remarks>
        public static bool TryGetByAccount(long accountId, out Launch? launch)
            => ByAccount.TryGetValue(accountId, out launch);

        public static bool IsActive(long accountId) => ByAccount.ContainsKey(accountId);
        public static int ActiveCount => ByAccount.Count;

        public static void Remove(Launch launch)
        {
            ByHash.TryRemove(launch.Hash, out _);
            ByAccount.TryRemove(launch.AccountId, out _);
            foreach (var pair in ByGameSession)
            {
                if (ReferenceEquals(pair.Value, launch)) ByGameSession.TryRemove(pair.Key, out _);
            }
        }

        /// <summary>
        /// Quita el lanzamiento de una cuenta sin tener el objeto delante.
        ///
        /// Hace falta desde que el lanzador es otro proceso: el que ve morir el proceso del cliente
        /// es él, y por el cable sólo puede mandar el número de la cuenta.
        /// </summary>
        /// <summary>
        /// Olvida todos los lanzamientos. Del banco de pruebas: en el servidor nadie debe llamarla,
        /// porque le soltaria la cuenta a todo el que este jugando.
        /// </summary>
        internal static void ForgetEverything()
        {
            ByHash.Clear();
            ByAccount.Clear();
            ByGameSession.Clear();
            Tokens.Clear();
        }

        public static void RemoveByAccount(long accountId)
        {
            if (ByAccount.TryGetValue(accountId, out var launch)) Remove(launch);
        }

        /// <summary>Las cuentas que tienen un cliente abierto ahora mismo.</summary>
        public static IReadOnlyCollection<long> ActiveAccounts => ByAccount.Keys.ToArray();

        /// <summary>
        /// Suelta los lanzamientos que se quedaron colgados: los que se registraron hace rato y
        /// nunca llegaron a conectar al servidor de juego.
        ///
        /// Sin esto, un cliente que arranca y muere antes de llegar al 5555 —o un lanzador que se
        /// cierra en mal momento— deja la cuenta marcada como ocupada para siempre, y Register la
        /// rechaza cada vez. El CreatedAtUtc llevaba puesto desde el principio y no lo leía nadie.
        /// </summary>
        public static int SoltarLosCaducados(TimeSpan cuanto)
        {
            int soltados = 0;
            var ahora = DateTime.UtcNow;
            foreach (var pair in ByAccount)
            {
                var launch = pair.Value;

                // Desde la ultima senal, no desde que se anoto. El de antes era "si tiene entrada
                // en ByGameSession no se toca", y esa entrada la pone el handshake y no la quita
                // nadie: un cliente que se moria despues del handshake dejaba la cuenta ocupada
                // hasta reiniciar el servidor.
                //
                // Y NO se suelta al cerrarse un socket, que es la otra forma de arreglar esto y
                // abre dos agujeros: volver a la pantalla de personajes cierra el socket de juego
                // -es la flecha de atras, no salir- asi que soltar ahi permite relanzar la misma
                // cuenta con el cliente anterior todavia vivo, tantas veces como se quiera, y de
                // paso el recuento por IP nunca pasa de uno y el tope de ocho deja de existir.
                var visto = launch.LastSeenUtc == default ? launch.CreatedAtUtc : launch.LastSeenUtc;
                if (ahora - visto < cuanto) continue;

                // Y la senal que de verdad zanja: hay un socket de esa cuenta conectado ahora
                // mismo. Hace falta ADEMAS de la marca de tiempo porque un jugador quieto puede
                // pasarse los cinco minutos sin que nadie resuelva su sesion, y soltarle el
                // lanzamiento le dejaria la cuenta libre para que la abriera otro cliente con el
                // suyo todavia jugando.
                if (SessionRegistry.HasConnected(launch.AccountId)) continue;

                Remove(launch);
                soltados++;
                Console.WriteLine($"[Lanzamientos] La cuenta {launch.AccountId} lleva " +
                                  $"{(ahora - visto).TotalMinutes:0} min sin dar senales. Se suelta.");
            }
            return soltados;
        }

        /// <summary>Regression guard for the exact failure mode of the old active-account field.</summary>
        internal static void AssertTwoClientsAreIsolated()
        {
            string hashA = Guid.NewGuid().ToString("N");
            string hashB = Guid.NewGuid().ToString("N");
            var launchA = Register(101, "", hashA, "fr");
            var launchB = Register(202, "", hashB, "en");
            try
            {
                if (!TryConnect(launchA.InstanceId, hashA, out string sessionA) ||
                    !TryConnect(launchB.InstanceId, hashB, out string sessionB) ||
                    sessionA == sessionB ||
                    !TryGetByGameSession(sessionA, out var resolvedA) || resolvedA?.AccountId != 101 ||
                    !TryGetByGameSession(sessionB, out var resolvedB) || resolvedB?.AccountId != 202 ||
                    TryConnect(launchA.InstanceId, hashB, out _))
                {
                    throw new InvalidOperationException("Multi-account launch sessions are not isolated.");
                }
            }
            finally
            {
                Remove(launchA);
                Remove(launchB);
            }
        }

        internal static void AssertEightClientLimit()
        {
            var launches = new List<Launch>();
            try
            {
                // Los ocho desde la MISMA direccion, que es lo que agrupa a una persona.
                const string mismaCasa = "10.0.0.7";
                for (int i = 0; i < Contract.ClientesPorIp; i++)
                    launches.Add(Register(1000 + i, "", Guid.NewGuid().ToString("N"), "fr", mismaCasa));

                bool rejected = false;
                try { Register(9999, "", Guid.NewGuid().ToString("N"), "fr", mismaCasa); }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("The ninth game client was not rejected.");

                // Pero desde OTRA direccion si entra: el tope es de una persona, no del servidor.
                // Cuando eran la misma constante, este noveno cliente se rechazaba tambien, y con
                // el servidor en otra maquina eso dejaba el mundo en ocho jugadores como mucho.
                var deFuera = Register(8888, "", Guid.NewGuid().ToString("N"), "fr", "10.0.0.99");
                launches.Add(deFuera);
            }
            finally
            {
                foreach (var launch in launches) Remove(launch);
            }
        }
    }
}

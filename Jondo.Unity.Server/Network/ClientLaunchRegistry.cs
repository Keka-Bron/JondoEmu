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
        /// Clients ONE same address can have open at once. Eight, which is what fits
        /// in a Dofus party. It is NOT the server's capacity: that is Contract.ClientesEnTotal.
        /// </summary>
        public const int MaximumClients = Jondo.Unity.Launcher.Contract.ClientesPorIp;
        public sealed class Launch
        {
            public int InstanceId { get; init; }
            public long AccountId { get; init; }
            public string Hash { get; init; } = "";
            public string LauncherToken { get; init; } = "";

            /// <summary>
            /// The language this client starts with. By default the launcher's, which is
            /// Spanish unless changed: here it used to say "fr" hard-coded.
            /// </summary>
            public string Language { get; init; } = "es";

            /// <summary>Where it was launched from. It groups the clients of one same person.</summary>
            public string Ip { get; init; } = "";
            public DateTime CreatedAtUtc { get; init; }

            /// <summary>
            /// The last time this launch gave signs of life.
            /// </summary>
            /// <remarks>
            /// It is not the same as CreatedAtUtc and that difference is the whole fix. Before, the
            /// sweep skipped any launch that had an entry in ByGameSession, and
            /// that entry is put there by the Thrift handshake and nobody removes it: a client that
            /// died AFTER the handshake -and with the launcher closed too- left the account
            /// marked as busy until the server restarted, and Register rejected all the
            /// following attempts with "cuenta-ya-abierta".
            ///
            /// Having an entry there proves it connected AT SOME POINT, not that it is still there. This proves
            /// the second: it is touched every time someone resolves his game session, which is what
            /// a live client does again and again.
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
                // Both rejections travel as a CODE, not as a sentence.
                //
                // They were in hard-coded French; then they went through the launcher's text
                // catalogue, and that left a piece of the server reading the user's language
                // preferences in %APPDATA%. A server does not translate: it says what happened and whoever
                // has a window in front decides in which language to tell the person.
                if (ByAccount.ContainsKey(accountId))
                    throw new InvalidOperationException(Contract.MotivoCuentaYaAbierta);

                // The cap of eight is PER ADDRESS, not for the whole server.
                //
                // It counted ByAccount.Count, that is all the clients of everybody: with the
                // server on one machine and the players on others, the server's ninth client
                // was rejected even if it was that person's first. The eight comes from the Dofus
                // party and belongs to a person, not to the server.
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

            // Sign of life. A live client goes through here again and again -every time it has to
            // be resolved whose session this is-, and a dead one never comes back.
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

            // The launcher's session first: the client rotates its game token every time it
            // starts, so the one the launcher stored the previous time only remains in
            // its column.
            long suya = DatabaseManager.GetAccountIdByLauncherToken(token);
            if (suya != 0) return suya;

            long juego = DatabaseManager.GetAccountIdByToken(token);
            if (juego != 0) return juego;

            // It says WHICH one was presented and where it was looked for, because a plain «matches no
            // account» does not distinguish the three reasons it can happen for —the token
            // being from another start, the column having been overwritten, or the client
            // sending something else— and without distinguishing them there is nowhere to go. Masked: it is a
            // live credential and the log stays on disk.
            Console.WriteLine($"[Lanzamientos] Token {Enmascarar(token)} desconocido: no está entre " +
                              $"los {Tokens.Count} de este arranque, ni en LauncherToken, ni en GameToken.");
            return 0;
        }

        /// <summary>The first four and the last four, to be able to follow its trail without writing it.</summary>
        private static string Enmascarar(string token)
            => token.Length <= 12
                ? new string('x', token.Length)
                : token.Substring(0, 4) + new string('x', token.Length - 8) + token.Substring(token.Length - 4);

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
        /// Forgets all the launches. For the test bench: on the server nobody must call it,
        /// because it would release the account of everyone playing.
        /// </summary>
        internal static void ForgetEverything()
        {
            ByHash.Clear();
            ByAccount.Clear();
            ByGameSession.Clear();
            Tokens.Clear();
        }

        /// <summary>
        /// Removes an account's launch without having the object at hand.
        ///
        /// It is needed since the launcher is another process: the one that sees the client process die
        /// is the launcher, and over the wire it can only send the account number.
        /// </summary>
        public static void RemoveByAccount(long accountId)
        {
            if (ByAccount.TryGetValue(accountId, out var launch)) Remove(launch);
        }

        /// <summary>The accounts that have a client open right now.</summary>
        public static IReadOnlyCollection<long> ActiveAccounts => ByAccount.Keys.ToArray();

        /// <summary>
        /// Releases the launches that were left hanging: the ones registered a while ago that
        /// never got to connect to the game server.
        ///
        /// Without this, a client that starts and dies before reaching 5555 —or a launcher that
        /// closes at a bad moment— leaves the account marked as busy forever, and Register
        /// rejects it every time. CreatedAtUtc had been set from the start and nobody read it.
        /// </summary>
        public static int SoltarLosCaducados(TimeSpan cuanto)
        {
            int soltados = 0;
            var ahora = DateTime.UtcNow;
            foreach (var pair in ByAccount)
            {
                var launch = pair.Value;

                // Since the last sign, not since it was recorded. The earlier one was "if it has an entry
                // in ByGameSession it is not touched", and that entry is put by the handshake and nobody
                // removes it: a client that died after the handshake left the account busy
                // until the server restarted.
                //
                // And it is NOT released when a socket closes, which is the other way of fixing this and
                // opens two holes: going back to the character screen closes the game socket
                // -it is the back arrow, not quitting- so releasing there allows relaunching the same
                // account with the previous client still alive, as many times as one likes, and on top of that
                // the per-IP count never goes above one and the cap of eight stops existing.
                var visto = launch.LastSeenUtc == default ? launch.CreatedAtUtc : launch.LastSeenUtc;
                if (ahora - visto < cuanto) continue;

                // And the sign that really settles it: there is a socket of that account connected right
                // now. It is needed ON TOP of the timestamp because an idle player can
                // spend the five minutes without anyone resolving his session, and releasing his
                // launch would leave the account free for another client to open it with his
                // still playing.
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
                // The eight from the SAME address, which is what groups one person.
                const string mismaCasa = "10.0.0.7";
                for (int i = 0; i < Contract.ClientesPorIp; i++)
                    launches.Add(Register(1000 + i, "", Guid.NewGuid().ToString("N"), "fr", mismaCasa));

                bool rejected = false;
                try { Register(9999, "", Guid.NewGuid().ToString("N"), "fr", mismaCasa); }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("The ninth game client was not rejected.");

                // But from ANOTHER address it does get in: the cap belongs to a person, not to the server.
                // When they were the same constant, this ninth client was rejected too, and with
                // the server on another machine that left the world at eight players at most.
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

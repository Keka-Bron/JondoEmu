using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;

namespace Jondo.Unity.Launcher.Network
{
    /// <summary>
    /// Associates one launcher invocation with one account through Zaap and the connection server.
    /// Every lookup uses a client-owned value, so concurrent clients cannot overwrite one another.
    /// </summary>
    public static class ClientLaunchRegistry
    {
        public const int MaximumClients = 8;
        public sealed class Launch
        {
            public int InstanceId { get; init; }
            public long AccountId { get; init; }
            public string Hash { get; init; } = "";
            public string LauncherToken { get; init; } = "";
            public string Language { get; init; } = "fr";
            public DateTime CreatedAtUtc { get; init; }
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

        public static Launch Register(long accountId, string launcherToken, string hash, string language)
        {
            if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId));
            if (string.IsNullOrWhiteSpace(hash)) throw new ArgumentException("A launch hash is required.", nameof(hash));

            lock (RegistrationGate)
            {
                if (ByAccount.ContainsKey(accountId))
                    throw new InvalidOperationException("Ce compte possède déjà un client actif.");
                if (ByAccount.Count >= MaximumClients)
                    throw new InvalidOperationException("La limite de 8 clients actifs est atteinte.");

                var launch = new Launch
                {
                    InstanceId = Interlocked.Increment(ref _nextInstanceId),
                    AccountId = accountId,
                    Hash = hash,
                    LauncherToken = launcherToken ?? "",
                    Language = string.IsNullOrWhiteSpace(language) ? "fr" : language,
                    CreatedAtUtc = DateTime.UtcNow
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
            return true;
        }

        public static bool TryGetByGameSession(string gameSession, out Launch? launch)
        {
            if (string.IsNullOrWhiteSpace(gameSession))
            {
                launch = null;
                return false;
            }
            return ByGameSession.TryGetValue(gameSession, out launch);
        }

        public static void RegisterToken(long accountId, string? token)
        {
            if (accountId > 0 && !string.IsNullOrWhiteSpace(token)) Tokens[token] = accountId;
        }

        public static long ResolveToken(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return 0;
            if (Tokens.TryGetValue(token, out long accountId)) return accountId;
            return DatabaseManager.GetAccountIdByToken(token);
        }

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
                for (int i = 0; i < MaximumClients; i++)
                    launches.Add(Register(1000 + i, "", Guid.NewGuid().ToString("N"), "fr"));

                bool rejected = false;
                try { Register(9999, "", Guid.NewGuid().ToString("N"), "fr"); }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("The ninth game client was not rejected.");
            }
            finally
            {
                foreach (var launch in launches) Remove(launch);
            }
        }
    }
}

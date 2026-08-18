using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Jondo.Unity.Launcher.Network
{
    /// <summary>
    /// Session tickets shared between the connection server and the game server.
    ///
    /// The client makes two separate connections: on the first one it authenticates and picks a
    /// server, and gets a ticket back; on the second one it presents that ticket to say who it
    /// is. Without this registry there is no way to know which account the second connection
    /// belongs to, and the character list would end up being the same one for everybody.
    ///
    /// The ticket is single-use and expires, so that it cannot work as a permanent key.
    /// </summary>
    public static class SessionRegistry
    {
        /// <summary>Grace period for the client to close one connection and open the next one.</summary>
        private static readonly TimeSpan Expiration = TimeSpan.FromMinutes(5);

        public sealed class Ticket
        {
            public string Value { get; init; } = "";
            public long AccountId { get; init; }
            public int ServerId { get; init; }
            public DateTime Created { get; init; }
        }

        private static readonly ConcurrentDictionary<string, Ticket> _tickets
            = new ConcurrentDictionary<string, Ticket>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<Guid, GameSession> _sessions
            = new ConcurrentDictionary<Guid, GameSession>();

        public static int ConnectedCount => _sessions.Count;

        private static readonly object SessionGate = new object();

        public static bool Register(GameSession session)
        {
            lock (SessionGate)
            {
                if (_sessions.Count >= ClientLaunchRegistry.MaximumClients) return false;
                return _sessions.TryAdd(session.Id, session);
            }
        }

        public static bool Unregister(GameSession session) => _sessions.TryRemove(session.Id, out _);

        public static bool TryGet(Guid sessionId, out GameSession? session)
            => _sessions.TryGetValue(sessionId, out session);

        public static GameSession? FindByCharacter(long characterId)
            => _sessions.Values.FirstOrDefault(s => s.CharacterId == characterId);

        /// <summary>Returns a stable snapshot; callers never enumerate a mutable registry.</summary>
        public static IReadOnlyList<GameSession> OnMap(long mapId)
            => _sessions.Values
                .Where(s => s.IsInWorld && s.MapId == mapId)
                .ToArray();

        /// <summary>Sends one packet to every connected character on a map.</summary>
        public static async Task<int> BroadcastToMapAsync(long mapId, byte[] packet,
                                                           Guid? exceptSessionId = null)
        {
            var targets = OnMap(mapId)
                .Where(s => !exceptSessionId.HasValue || s.Id != exceptSessionId.Value)
                .ToArray();

            var results = await Task.WhenAll(targets.Select(async target =>
            {
                try
                {
                    await target.SendAsync(packet);
                    return true;
                }
                catch (Exception ex)
                {
                    Unregister(target);
                    Program.LogDebug($"[Sessions] Send to {target.Id} failed: {ex.Message}");
                    return false;
                }
            }));
            return results.Count(delivered => delivered);
        }

        /// <summary>Creates a new ticket for a specific account and server.</summary>
        public static Ticket Issue(long accountId, int serverId)
        {
            Purge();

            var ticket = new Ticket
            {
                Value = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                AccountId = accountId,
                ServerId = serverId,
                Created = DateTime.UtcNow
            };
            _tickets[ticket.Value] = ticket;
            return ticket;
        }

        /// <summary>
        /// Redeems a ticket. Returns null if it does not exist or if it has expired.
        /// It is consumed on use: a ticket is not good for two connections.
        /// </summary>
        public static Ticket? Redeem(string value)
        {
            Purge();
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!_tickets.TryRemove(value.Trim(), out var ticket)) return null;
            if (DateTime.UtcNow - ticket.Created > Expiration) return null;
            return ticket;
        }

        private static void Purge()
        {
            DateTime now = DateTime.UtcNow;
            foreach (var pair in _tickets)
            {
                if (now - pair.Value.Created > Expiration)
                {
                    _tickets.TryRemove(pair.Key, out _);
                }
            }
        }
    }
}

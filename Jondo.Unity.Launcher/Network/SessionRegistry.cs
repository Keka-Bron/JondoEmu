using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;

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
                // La capacidad del SERVIDOR, que no tiene nada que ver con cuántos clientes abre una
                // persona. Aquí ponía el tope del lanzador multicuenta -ocho- y por eso el servidor
                // entero rechazaba la novena conexión, viniera del ordenador que viniera.
                if (_sessions.Count >= Contract.ClientesEnTotal) return false;
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

        /// <summary>
        /// La mudanza de un personaje, contada a los dos mapas: al que deja, que se ha ido (jsd);
        /// al que llega, que ha llegado (jsn).
        ///
        /// Hacía falta UNA sola pieza porque los caminos por los que un personaje cambia de mapa
        /// son cuatro —el borde, el zaap, el .teleport y el mando de mapa— y cada uno avisaba a su
        /// manera: el del borde mandaba el jsd al mapa viejo y nada al nuevo; el del zaap no
        /// mandaba ninguno de los dos, sólo sacaba al personaje de su propia pantalla. De ahí lo
        /// que se veía jugando: quien llegaba por el zaap veía a los que ya estaban —su lista de
        /// actores se la trae entera— pero ellos a él no lo veían hasta recargar el mapa. Y al
        /// revés igual: quien se iba por el zaap se quedaba de fantasma en la pantalla del otro.
        ///
        /// El aviso de llegada es el mismo jsn que ya se manda al entrar al mundo, así que el
        /// cliente no distingue entre «acaba de conectarse» y «acaba de llegar»: dibuja al actor.
        ///
        /// Se llama DESPUÉS de mover el estado de la sesión al mapa nuevo, que es lo que decide a
        /// quién le llega cada cosa: el que se muda ya no está en el mapa viejo y sí en el nuevo,
        /// y en los dos casos se le excluye a él mismo, que de sus propios movimientos ya se
        /// entera por otro lado.
        /// </summary>
        public static async Task<(int seVa, int llega)> AnunciarMudanzaAsync(GameSession quien,
                                                                            long mapaQueDeja,
                                                                            int? porDonde = null)
        {
            if (quien == null || !quien.IsInWorld || quien.CharacterId <= 0) return (0, 0);

            int seVa = 0;
            if (mapaQueDeja > 0 && mapaQueDeja != quien.MapId)
            {
                seVa = await BroadcastToMapAsync(mapaQueDeja,
                    ConnectionProtocol.BuildActorLeft(quien.CharacterId, porDonde), quien.Id);
            }

            var ficha = DatabaseManager.GetCharacterById(quien.CharacterId);
            if (ficha == null) return (seVa, 0);

            int llega = await BroadcastToMapAsync(quien.MapId,
                ConnectionProtocol.Push(Op.GameContextRefreshEntityLookMessage, ConnectionProtocol.BuildActorRefreshed(
                    ficha, quien.State.CellId, quien.State.Orientation, quien.AccountId)),
                quien.Id);

            if (seVa > 0 || llega > 0)
            {
                Program.LogDebug($"[Mudanza] {ficha.Name}: {mapaQueDeja} -> {quien.MapId}. " +
                                 $"Avisados {seVa} que deja y {llega} que se encuentra.");
            }
            return (seVa, llega);
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

using System;
using System.Collections.Concurrent;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The challenges between players awaiting an answer.
    ///
    /// It is called Duels and not Challenges because in this code «reto» (challenge) is already something
    /// else: the fight challenges with an achievement behind them, which Managers.Challenges handles. Two
    /// different things with the same name is exactly how the bugs nobody finds slip in.
    /// </summary>
    /// <remarks>
    /// A challenge is an id and two characters, and it lives from when somebody challenges until the other
    /// answers. Measured in the four challenge captures: the server hands out increasing ids (489, 490, 492,
    /// 494 in the same session) and uses them as the only reference in the three following frames, so the
    /// state has to be here and not in either of the two sessions.
    ///
    /// It is static on purpose, like the launch registry: a challenge crosses TWO connections -- whoever
    /// challenges and whoever answers are different sockets -- and storing it in one's session would leave
    /// it invisible to the other.
    /// </remarks>
    public static class Duels
    {
        /// <summary>A pending challenge.</summary>
        public sealed class Duel
        {
            public int Id { get; init; }
            public long ChallengerId { get; init; }
            public long TargetId { get; init; }
            public long MapId { get; init; }
        }

        private static readonly ConcurrentDictionary<int, Duel> _pendientes
            = new ConcurrentDictionary<int, Duel>();

        private static int _siguiente;

        /// <summary>
        /// Where they are numbered from. The captures start at 489, which comes from a long session of the
        /// real server: the number itself means nothing, it only has to be unique and increasing.
        /// </summary>
        private const int PrimerId = 1;

        public static int Pending => _pendientes.Count;

        /// <summary>Opens a challenge and returns its id.</summary>
        /// <remarks>
        /// The map is stored with it because a challenge is between two who are in the same place: if one
        /// leaves before answering, accepting it would set up a fight on a map where he no longer is. That is
        /// checked on accepting, not here.
        /// </remarks>
        public static Duel Open(long challengerId, long targetId, long mapId)
        {
            int id = System.Threading.Interlocked.Increment(ref _siguiente) + PrimerId - 1;
            var desafio = new Duel
            {
                Id = id,
                ChallengerId = challengerId,
                TargetId = targetId,
                MapId = mapId,
            };

            _pendientes[id] = desafio;
            return desafio;
        }

        /// <summary>The challenge with that id, or null if there is none or it has been answered.</summary>
        public static Duel? Get(int id)
            => _pendientes.TryGetValue(id, out var desafio) ? desafio : null;

        /// <summary>Takes it off the list. Returns null if somebody else got there first.</summary>
        /// <remarks>
        /// Returning the challenge on removing it, and not a bool, is what keeps two simultaneous answers from
        /// setting up two fights: only one of the two takes the object.
        /// </remarks>
        public static Duel? Take(int id)
            => _pendientes.TryRemove(id, out var desafio) ? desafio : null;

        /// <summary>
        /// Whether either of the two is already in a pending challenge.
        /// </summary>
        /// <remarks>
        /// Without this the same player can be challenged a hundred times and have his screen filled with
        /// windows, or ten can be challenged at once and all of them accepted. One challenge per person at a
        /// time, in either role.
        /// </remarks>
        public static bool Busy(long characterId)
        {
            foreach (var desafio in _pendientes.Values)
            {
                if (desafio.ChallengerId == characterId || desafio.TargetId == characterId)
                    return true;
            }
            return false;
        }

        /// <summary>Closes the challenges this character is in. Returns how many.</summary>
        /// <remarks>
        /// On disconnecting, or on changing map. A challenge whose challenger is gone is a window that cannot be
        /// answered: accepting it would find nobody.
        /// </remarks>
        public static int ForgetThoseOf(long characterId)
        {
            int cerrados = 0;
            foreach (var desafio in _pendientes.Values)
            {
                if (desafio.ChallengerId != characterId && desafio.TargetId != characterId) continue;
                if (_pendientes.TryRemove(desafio.Id, out _)) cerrados++;
            }
            return cerrados;
        }

        /// <summary>Only for tests: leaves the list empty.</summary>
        internal static void ForgetEverything()
        {
            _pendientes.Clear();
            _siguiente = 0;
        }
    }
}

using System;
using System.Collections.Concurrent;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Los desafíos entre jugadores que están pendientes de respuesta.
    ///
    /// Se llama Duels y no Challenges porque en este código «reto» ya es otra cosa: los retos de
    /// combate con logro detrás, que lleva Managers.Challenges. Dos cosas distintas con el mismo
    /// nombre en español es justo como se cuelan los errores que nadie encuentra.
    /// </summary>
    /// <remarks>
    /// Un desafío es un id y dos personajes, y vive desde que alguien reta hasta que el otro
    /// contesta. Medido en las cuatro capturas de desafío: el servidor reparte ids crecientes
    /// (489, 490, 492, 494 en la misma sesión) y los usa como única referencia en las tres tramas
    /// siguientes, así que el estado tiene que estar aquí y no en ninguna de las dos sesiones.
    ///
    /// Es estático a propósito, como el registro de lanzamientos: un desafío cruza DOS conexiones
    /// —quien reta y quien contesta son sockets distintos— y guardarlo en la sesión de uno lo
    /// dejaría invisible para el otro.
    /// </remarks>
    public static class Duels
    {
        /// <summary>Un desafío pendiente.</summary>
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
        /// Desde dónde se numeran. Las capturas empiezan en el 489, que es de una sesión larga del
        /// servidor real: el número en sí no significa nada, sólo tiene que ser único y creciente.
        /// </summary>
        private const int PrimerId = 1;

        public static int Pending => _pendientes.Count;

        /// <summary>Abre un desafío y devuelve su id.</summary>
        /// <remarks>
        /// El mapa se guarda con él porque un desafío es entre dos que están en el mismo sitio: si
        /// uno se marcha antes de contestar, aceptarlo montaría un combate en un mapa donde ya no
        /// está. Eso se comprueba al aceptar, no aquí.
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

        /// <summary>El desafío con ese id, o null si no lo hay o ya se contestó.</summary>
        public static Duel? Get(int id)
            => _pendientes.TryGetValue(id, out var desafio) ? desafio : null;

        /// <summary>Lo saca de la lista. Devuelve null si otro llegó antes.</summary>
        /// <remarks>
        /// Devolver el desafío al quitarlo, y no un bool, es lo que hace que dos respuestas a la
        /// vez no monten dos combates: sólo una de las dos se lleva el objeto.
        /// </remarks>
        public static Duel? Take(int id)
            => _pendientes.TryRemove(id, out var desafio) ? desafio : null;

        /// <summary>
        /// Si alguno de los dos ya está metido en un desafío pendiente.
        /// </summary>
        /// <remarks>
        /// Sin esto se puede retar cien veces al mismo y llenarle la pantalla de ventanas, o retar
        /// a diez a la vez y aceptar todos. Un desafío por persona a la vez, en cualquiera de los
        /// dos papeles.
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

        /// <summary>Cierra los desafíos en los que ande este personaje. Devuelve cuántos.</summary>
        /// <remarks>
        /// Al desconectarse, o al cambiar de mapa. Un desafío cuyo retador ya no está es una
        /// ventana que no se puede contestar: aceptarla no encontraría a nadie.
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

        /// <summary>Sólo para los tests: deja la lista vacía.</summary>
        internal static void ForgetEverything()
        {
            _pendientes.Clear();
            _siguiente = 0;
        }
    }
}

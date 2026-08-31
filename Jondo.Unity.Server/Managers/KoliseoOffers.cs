using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Las partidas encontradas y todavía sin contestar, y a quién se le ha prohibido apuntarse.
    /// </summary>
    /// <remarks>
    /// Entre encontrar la partida y empezarla hay un cartel: «se ha detectado un combate», con
    /// aceptar, rechazar y un plazo. Todo lo de aquí sale de dos capturas del servidor real, una de
    /// 2 contra 2 y otra de 3 contra 3:
    ///
    /// <code>
    ///   S-&gt;C  lsh «103b»          el aviso. f2 = 59, y son SEGUNDOS
    ///   C-&gt;S  luy «1001»          aceptar. f2 es un bool, no un índice
    ///   S-&gt;C  lth «1001»          el acuse, por la raíz 3 con el id de la petición
    /// </code>
    ///
    /// Que el 59 son segundos no es una corazonada: en la captura del 3 contra 3 el jugador dejó
    /// vencer el plazo, y entre el lsh y la primera trama del vencimiento pasan <b>60.014 ms</b>.
    /// Las dos capturas traen el mismo 59, en modalidades distintas y en servidores distintos, así
    /// que es el plazo entero y no lo que quedaba.
    ///
    /// Al vencer, el servidor real manda cuatro cosas y luego prohíbe apuntarse un rato:
    ///
    /// <code>
    ///   lty  la clasificación, que aquí no se manda: ver KoliseoHandler
    ///   lqn { 1, 503, ["1788216996"] }   el aviso, con la marca de tiempo en que se levanta
    ///   ltk  vacío
    ///   lsx { f3: 3, f4: modalidad }     fuera de la cola
    /// </code>
    ///
    /// Y al intentar reapuntarse antes de tiempo, <c>lqn { 1, 642, ["4"] }</c> con los minutos que
    /// faltan. El texto del cliente dice cinco minutos y la captura enseña un «4» un minuto
    /// después, así que <see cref="Castigo"/> son cinco.
    ///
    /// LO QUE NO ESTÁ MEDIDO: qué manda el cliente al pulsar RECHAZAR. En la captura se dejó vencer
    /// el plazo. Por la forma del luy —un bool de proto3, que en falso no viaja— un rechazo tendría
    /// que llegar como un luy de carga vacía, y así se trata; el precedente del desafío pvp hace
    /// lo mismo (aceptar «08ec031001», rechazar «08e903»).
    /// </remarks>
    public static class KoliseoOffers
    {
        /// <summary>Lo que dura el cartel. El f2 del lsh, y los 60 s que se midieron esperándolo.</summary>
        public const int Segundos = 59;

        /// <summary>Lo que se tarda en poder volver a apuntarse tras dejarlo vencer.</summary>
        public const int Castigo = 5;

        public sealed class Offer
        {
            public long Id { get; init; }
            public int Mode { get; init; }
            public int TeamSize { get; init; }
            public IReadOnlyList<long> Blue { get; init; } = Array.Empty<long>();
            public IReadOnlyList<long> Red { get; init; } = Array.Empty<long>();

            /// <summary>Quiénes han dicho que sí. Los demás siguen pendientes.</summary>
            public HashSet<long> Accepted { get; } = new HashSet<long>();

            /// <summary>Cierto en cuanto alguien la resuelve, para que no se resuelva dos veces.</summary>
            public bool Closed { get; set; }

            public object Gate { get; } = new object();

            public IEnumerable<long> Everybody
            {
                get
                {
                    foreach (long id in Blue) yield return id;
                    foreach (long id in Red) yield return id;
                }
            }
        }

        private static long _next = 1;
        private static readonly ConcurrentDictionary<long, Offer> _offers = new();

        /// <summary>En qué oferta está metido cada personaje. Uno sólo puede estar en una.</summary>
        private static readonly ConcurrentDictionary<long, long> _of = new();

        /// <summary>Hasta cuándo tiene prohibido apuntarse cada uno.</summary>
        private static readonly ConcurrentDictionary<long, DateTime> _banned = new();

        /// <summary>La última modalidad en que a cada uno se le encontró partida.</summary>
        /// <remarks>
        /// El lsx de volver del koliseo lleva la modalidad en el mismo f4 que el de estar
        /// buscando, y al volver ya no hay ni cola ni oferta de la que sacarla. Se apunta al abrir
        /// la oferta, que es el último momento en que se sabe.
        /// </remarks>
        private static readonly ConcurrentDictionary<long, int> _lastMode = new();

        public static int Pending => _offers.Count;

        /// <summary>Abre una oferta para los dos equipos ya emparejados.</summary>
        public static Offer Open(int mode, int teamSize, IReadOnlyList<long> blue,
                                 IReadOnlyList<long> red)
        {
            var offer = new Offer
            {
                Id = System.Threading.Interlocked.Increment(ref _next),
                Mode = mode,
                TeamSize = teamSize,
                Blue = new List<long>(blue),
                Red = new List<long>(red),
            };

            _offers[offer.Id] = offer;
            foreach (long id in offer.Everybody)
            {
                _of[id] = offer.Id;
                _lastMode[id] = mode;
            }
            return offer;
        }

        public static Offer? Of(long characterId)
            => _of.TryGetValue(characterId, out long id) && _offers.TryGetValue(id, out var offer)
                ? offer
                : null;

        public static Offer? ById(long id) => _offers.TryGetValue(id, out var offer) ? offer : null;

        /// <summary>
        /// Apunta un sí. Devuelve cierto cuando ya han dicho que sí TODOS, que es cuando el
        /// combate puede empezar.
        /// </summary>
        public static bool Accept(Offer offer, long characterId)
        {
            lock (offer.Gate)
            {
                if (offer.Closed) return false;
                offer.Accepted.Add(characterId);

                foreach (long id in offer.Everybody)
                {
                    if (!offer.Accepted.Contains(id)) return false;
                }

                offer.Closed = true;
                return true;
            }
        }

        /// <summary>
        /// Cierra la oferta y la borra. Devuelve falso si alguien se le había adelantado, para que
        /// el vencimiento no pise a una aceptación que llegó por los pelos.
        /// </summary>
        public static bool Close(Offer offer)
        {
            lock (offer.Gate)
            {
                if (offer.Closed) return false;
                offer.Closed = true;
            }
            Forget(offer);
            return true;
        }

        /// <summary>Quita la oferta del índice. La cierra <see cref="Close"/> o la aceptación.</summary>
        public static void Forget(Offer offer)
        {
            _offers.TryRemove(offer.Id, out _);
            foreach (long id in offer.Everybody) _of.TryRemove(id, out _);
        }

        /// <summary>Los que no dijeron que sí. Son los que se llevan el castigo.</summary>
        public static List<long> WhoDidNotAnswer(Offer offer)
        {
            var quienes = new List<long>();
            lock (offer.Gate)
            {
                foreach (long id in offer.Everybody)
                {
                    if (!offer.Accepted.Contains(id)) quienes.Add(id);
                }
            }
            return quienes;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  El castigo
        // ═══════════════════════════════════════════════════════════════════

        public static void Ban(long characterId, DateTime cuando)
            => _banned[characterId] = cuando;

        /// <summary>Cuándo se le levanta el castigo, o null si no lo tiene.</summary>
        public static DateTime? BannedUntil(long characterId)
        {
            if (!_banned.TryGetValue(characterId, out var hasta)) return null;
            if (hasta <= DateTime.UtcNow)
            {
                _banned.TryRemove(characterId, out _);
                return null;
            }
            return hasta;
        }

        /// <summary>
        /// Los minutos que le quedan, redondeados HACIA ARRIBA.
        /// </summary>
        /// <remarks>
        /// La captura enseña un «4» al reintentar poco después de un castigo de cinco minutos, y
        /// redondeando hacia abajo eso habría salido «4» sólo durante el quinto minuto. Hacia
        /// arriba sale «4» durante todo el cuarto, que es lo que se ve.
        /// </remarks>
        public static int MinutesLeft(long characterId)
        {
            var hasta = BannedUntil(characterId);
            if (hasta == null) return 0;

            double minutos = (hasta.Value - DateTime.UtcNow).TotalMinutes;
            return Math.Max(1, (int)Math.Ceiling(minutos));
        }

        /// <summary>En qué modalidad jugó el último koliseo, o cero si no consta.</summary>
        public static int LastMode(long characterId)
            => _lastMode.TryGetValue(characterId, out int mode) ? mode : 0;

        /// <summary>Sólo para las pruebas: sin ofertas, sin castigos y sin memoria.</summary>
        internal static void ForgetEverything()
        {
            _offers.Clear();
            _of.Clear();
            _banned.Clear();
            _lastMode.Clear();
        }
    }
}

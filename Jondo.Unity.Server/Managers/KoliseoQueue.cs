using System;
using System.Collections.Generic;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// La cola del koliseo: quién está esperando y a qué modalidad.
    /// </summary>
    /// <remarks>
    /// Una cola por modalidad, porque quien se apunta a 3 contra 3 no vale para llenar un 1 contra
    /// 1. El emparejamiento es el más simple que funciona: en cuanto hay gente para los dos
    /// equipos, se cogen los primeros por orden de llegada y se monta el combate.
    ///
    /// Es estático por lo mismo que <see cref="Duels"/>: la cola cruza tantas conexiones como
    /// jugadores tenga, y guardarla en una sesión la dejaría invisible para las demás.
    ///
    /// <b>Lo que aquí NO se hace, y conviene saberlo.</b> No se mira el nivel de nadie, ni su
    /// puntuación, ni se equilibran los equipos: el koliseo de verdad empareja por clasificación,
    /// y esa clasificación son las dos listas de más de tres mil bytes que la captura trae en el
    /// <c>iqt</c> y el <c>irc</c>, que no están implementadas. Emparejar por orden de llegada es
    /// una decisión provisional y está dicha, no disimulada.
    /// </remarks>
    public static class KoliseoQueue
    {
        /// <summary>Quién espera, en qué modalidad y desde cuándo.</summary>
        public sealed class Waiting
        {
            public long CharacterId { get; init; }
            public int Mode { get; init; }
        }

        private static readonly object _candado = new object();
        private static readonly Dictionary<int, List<Waiting>> _porModalidad = new();

        public static int Count
        {
            get
            {
                lock (_candado)
                {
                    int total = 0;
                    foreach (var cola in _porModalidad.Values) total += cola.Count;
                    return total;
                }
            }
        }

        public static int CountIn(int mode)
        {
            lock (_candado)
                return _porModalidad.TryGetValue(mode, out var cola) ? cola.Count : 0;
        }

        /// <summary>Si este personaje ya está esperando en alguna.</summary>
        public static bool Waits(long characterId)
        {
            lock (_candado)
            {
                foreach (var cola in _porModalidad.Values)
                    foreach (var uno in cola)
                        if (uno.CharacterId == characterId) return true;
                return false;
            }
        }

        /// <summary>Apunta a alguien. Devuelve false si ya estaba o la modalidad no existe.</summary>
        public static bool Enrol(long characterId, int mode)
        {
            if (characterId == 0) return false;

            lock (_candado)
            {
                foreach (var cola in _porModalidad.Values)
                    foreach (var uno in cola)
                        if (uno.CharacterId == characterId) return false;

                if (!_porModalidad.TryGetValue(mode, out var mia))
                    _porModalidad[mode] = mia = new List<Waiting>();

                mia.Add(new Waiting { CharacterId = characterId, Mode = mode });
                return true;
            }
        }

        /// <summary>Lo saca de donde esté. Devuelve la modalidad de la que salió, o -1.</summary>
        public static int Leave(long characterId)
        {
            lock (_candado)
            {
                foreach (var (modalidad, cola) in _porModalidad)
                {
                    int at = cola.FindIndex(uno => uno.CharacterId == characterId);
                    if (at < 0) continue;

                    cola.RemoveAt(at);
                    return modalidad;
                }
                return -1;
            }
        }

        /// <summary>
        /// Si ya hay gente para los dos equipos, los saca de la cola y los devuelve.
        /// </summary>
        /// <remarks>
        /// Saca a los <c>2 × teamSize</c> primeros y los reparte por orden: los primeros al azul y
        /// los siguientes al rojo. Sacarlos DENTRO del candado es lo que impide que dos llamadas a
        /// la vez se lleven a la misma persona a dos combates.
        /// </remarks>
        public static (List<long> Blue, List<long> Red)? TryMatch(int mode, int teamSize)
        {
            if (teamSize <= 0) return null;

            lock (_candado)
            {
                if (!_porModalidad.TryGetValue(mode, out var cola)) return null;
                if (cola.Count < teamSize * 2) return null;

                var azul = new List<long>();
                var rojo = new List<long>();

                for (int i = 0; i < teamSize; i++) azul.Add(cola[i].CharacterId);
                for (int i = teamSize; i < teamSize * 2; i++) rojo.Add(cola[i].CharacterId);

                cola.RemoveRange(0, teamSize * 2);
                return (azul, rojo);
            }
        }

        /// <summary>Sólo para los tests: deja las colas vacías.</summary>
        internal static void ForgetEverything()
        {
            lock (_candado) _porModalidad.Clear();
        }
    }
}

using System;
using System.Collections.Generic;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Las papeleras: el almacén público donde va lo que la gente tira.
    ///
    /// En el juego real guardan lo que otros han soltado, y se vacían solas al cabo de un rato. Aquí
    /// abren VACÍAS y a propósito: no hay nada que restaurar —nadie ha tirado nada en este servidor—
    /// y llenarlas de objetos inventados sería poner en el mundo cosas que no vienen de ninguna
    /// parte. Lo que se implementa es el mecanismo: se clica, se abre, se puede meter y sacar.
    ///
    /// ─── De dónde sale cada número ──────────────────────────────────────────────────────────
    ///
    /// El TIPO (105) y la HABILIDAD (153) salen de la captura de la papelera de delante del banco de
    /// Bonta: el servidor real los manda en el jss y en el iwn.
    ///
    /// Los GRÁFICOS salen de cruzar las 304 capturas con el volcado del cliente
    /// —tools/tipos_interactivos.py—, y ahí está el motivo de no fiarse de una sola captura: la de
    /// Bonta enseñaba el gráfico 260022 y con él salían 31 papeleras. Hay cuatro gráficos
    /// distintos, y en total son <b>67</b> repartidas por 63 mapas.
    /// </summary>
    public static class Bins
    {
        /// <summary>El tipo con el que el cliente dibuja una papelera. Medido del jss real.</summary>
        public const int Type = 105;

        /// <summary>La habilidad de «usar», que el servidor devuelve en el iwn.</summary>
        public const int UseSkill = 153;

        /// <summary>
        /// Los cuatro aspectos que tiene una papelera.
        ///
        /// No son variantes de adorno: cada ciudad usa el suyo, y con uno solo se quedaban fuera 36
        /// de las 67.
        /// </summary>
        private static readonly HashSet<int> Graphics = new() { 8438, 46529, 63081, 260022 };

        private static readonly Dictionary<long, List<Interactives.Element>> _byMap = new();

        public static int Count { get; private set; }
        public static int MapCount => _byMap.Count;

        public static void Initialize()
        {
            _byMap.Clear();
            Count = 0;

            foreach (long mapId in Interactives.MapIds)
            {
                List<Interactives.Element>? here = null;
                foreach (var element in Interactives.ElementsOf(mapId))
                {
                    if (!Graphics.Contains(element.Gfx)) continue;
                    (here ??= new List<Interactives.Element>()).Add(element);
                    Count++;
                }
                if (here != null) _byMap[mapId] = here;
            }

            Console.WriteLine($"[Papeleras] {Count} en {_byMap.Count} mapas.");
        }

        /// <summary>Las papeleras que hay en este mapa.</summary>
        public static IReadOnlyList<Interactives.Element> On(long mapId)
            => _byMap.TryGetValue(mapId, out var found)
                ? found
                : (IReadOnlyList<Interactives.Element>)Array.Empty<Interactives.Element>();
    }
}

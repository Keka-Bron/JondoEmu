using System;
using System.Collections.Generic;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// La experiencia de oficio: cuánta lleva cada personaje y en qué nivel va.
    ///
    /// ─── La curva, que sale de tres puntos medidos ──────────────────────────────────────────
    ///
    /// El cliente NO trae tabla de experiencia de oficio. La suya, CharacterXpMappings, sólo tiene
    /// una columna y es la del personaje: 110 para el nivel 2, 650 para el 3. Los oficios van por
    /// otro lado y ese lado no viaja en los datos.
    ///
    /// Pero el <c>irq</c> de las capturas lo enseña sin querer, porque manda TOTALES y no
    /// incrementos: f2 es lo que hace falta para el nivel siguiente, f3 el nivel, f4 el suelo del
    /// nivel actual y f5 la experiencia acumulada. De ahí salen tres puntos:
    ///
    ///     nivel   2  ->      20     (subida de campesino, captura del trigo)
    ///     nivel   3  ->      60     (el f2 de esa misma subida)
    ///     nivel 200  -> 398.000     (el f4 del leñador, que está al tope)
    ///
    /// Y los tres los cuadra la misma fórmula:
    ///
    ///     experiencia(nivel) = 10 · nivel · (nivel − 1)
    ///
    ///     10·2·1 = 20 ✔      10·3·2 = 60 ✔      10·200·199 = 398.000 ✔
    ///
    /// Tres de tres, incluido el extremo. No es una curva inventada que pase por dos puntos: es
    /// una fórmula sencilla que acierta en todos los que hay, y el del nivel 200 es el que
    /// difícilmente saldría por casualidad.
    ///
    /// ─── Cuánta se gana ─────────────────────────────────────────────────────────────────────
    ///
    /// Diez por recogida, y es FIJO: se midió con 20, 14 y 17 unidades de madera y las tres veces
    /// fueron +10. No va por unidades.
    /// </summary>
    public static class JobExperience
    {
        /// <summary>El nivel al que llega un oficio.</summary>
        public const int MaxLevel = 200;

        /// <summary>Lo que da una recogida. Medido tres veces con cantidades distintas.</summary>
        public const int PerGather = 10;

        /// <summary>La experiencia acumulada con la que empieza un nivel.</summary>
        public static long Floor(int level)
        {
            if (level <= 1) return 0;
            if (level > MaxLevel) level = MaxLevel;
            return 10L * level * (level - 1);
        }

        /// <summary>Lo que hace falta para el nivel siguiente, o cero si ya está al tope.</summary>
        public static long Next(int level) => level >= MaxLevel ? 0 : Floor(level + 1);

        /// <summary>En qué nivel va alguien con esta experiencia.</summary>
        public static int LevelOf(long experience)
        {
            if (experience < 20) return 1;
            // Se despeja de 10·n·(n−1) y luego se ajusta a mano, que es más corto que iterar
            // doscientas veces y no se fía de la aritmética en coma flotante para el borde.
            int level = (int)Math.Floor((1 + Math.Sqrt(1 + 0.4 * experience)) / 2);
            if (level < 1) level = 1;
            if (level > MaxLevel) level = MaxLevel;
            while (level < MaxLevel && Floor(level + 1) <= experience) level++;
            while (level > 1 && Floor(level) > experience) level--;
            return level;
        }

        /// <summary>Lo que lleva un personaje en un oficio.</summary>
        public sealed class Progress
        {
            public int JobId { get; init; }
            public long Experience { get; set; }
            public int Level => LevelOf(Experience);
        }

        /// <summary>
        /// Suma experiencia y dice si ha subido de nivel.
        ///
        /// El estado vive en la sesión del personaje, que es quien lo guarda en la base.
        /// </summary>
        public static bool Add(IDictionary<int, Progress> jobs, int jobId, long amount,
                               out Progress progress)
        {
            if (!jobs.TryGetValue(jobId, out progress!))
            {
                progress = new Progress { JobId = jobId, Experience = 0 };
                jobs[jobId] = progress;
            }

            int before = progress.Level;
            progress.Experience += amount;
            if (progress.Experience > Floor(MaxLevel)) progress.Experience = Floor(MaxLevel);
            return progress.Level > before;
        }
    }
}

using System.Collections.Generic;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Lo que cobra el que gana un koliseo.
    /// </summary>
    /// <remarks>
    /// Cuatro cosas, y las cuatro salen del jyg de fin de combate de la captura del koliseo
    /// completo —dos contra dos, con reparto de kolichas—. Las cuatro entradas del jyg:
    ///
    /// <code>
    ///   GANAN (llevan el f4 = 2)
    ///     nivel 227   3.400 kamas   260 × 12736   2 × 34478   4.722.600 de experiencia
    ///     nivel 290   2.800 kamas   230 × 12736   2 × 34478   7.496.344 de experiencia
    ///   PIERDEN
    ///     nivel 354   botín de cero bytes, y el bloque de experiencia SIN el f1
    ///     nivel 447   igual
    /// </code>
    ///
    /// De ahí sale, MEDIDO: que se paga, qué se paga, y que el que pierde no cobra nada —ni
    /// experiencia; su bloque va sin el campo de lo ganado, no con un cero—.
    ///
    /// Lo que NO sale de ahí es la FÓRMULA, y conviene decirlo claro: son dos ganadores, o sea dos
    /// puntos. Y los dos puntos ni siquiera van en el sentido que uno esperaría —el de nivel 290
    /// cobra MENOS kamas y MENOS kolichas que el de 227—, así que ni con la mejor voluntad se
    /// puede sacar de aquí una función del nivel. Los dos ganaron a los mismos dos rivales, de
    /// nivel 354 y 447, así que tampoco es el nivel del rival lo que los separa. Se parece a una
    /// prima por ser el que menos nivel tiene, pero con dos números eso es una corazonada, no una
    /// medida.
    ///
    /// Así que los kamas, las kolichas y las vitorichas son CONSTANTES, y la constante es la media
    /// de lo medido. Es una decisión, no un hallazgo, y está aquí en tres números para que cambiarla
    /// el día que haya más capturas sea cambiar tres números.
    ///
    /// La experiencia sí admite algo mejor que una constante. Puesta sobre la banda del nivel
    /// —lo que va del suelo del nivel al del siguiente— los dos ganadores caen casi en el mismo
    /// sitio:
    ///
    /// <code>
    ///   227   4.722.600 de 65.410.444    7,22 %
    ///   290   7.496.344 de 122.431.633   6,12 %
    /// </code>
    ///
    /// Dos puntos a poco más de un punto porcentual uno de otro. Se usa el 6,67 %, que es la media,
    /// y con eso la cifra sale razonable en cualquier nivel en vez de ser ridícula abajo y ridícula
    /// arriba, que es lo que pasaría con una constante.
    /// </remarks>
    public static class KoliseoRewards
    {
        /// <summary>La Kolicha. El f4 del botín en las dos entradas que ganan.</summary>
        public const int Kolicha = 12736;

        /// <summary>La Vitoricha, la otra moneda del koliseo.</summary>
        public const int Vitoricha = 34478;

        /// <summary>Media de los dos ganadores medidos, 260 y 230.</summary>
        public const int KolichasPorVictoria = 245;

        /// <summary>Los dos ganadores medidos se llevan dos. Aquí no hay media que hacer.</summary>
        public const int VitorichasPorVictoria = 2;

        /// <summary>Media de los dos ganadores medidos, 3.400 y 2.800.</summary>
        public const int KamasPorVictoria = 3100;

        /// <summary>Qué parte de la banda del nivel se lleva el que gana, en diezmilésimas.</summary>
        /// <remarks>
        /// 667 de 10.000 es el 6,67 %: la media del 7,22 % y el 6,12 % medidos. En diezmilésimas y
        /// no en coma flotante para que la cuenta sea entera de principio a fin y dos servidores
        /// con la misma versión paguen exactamente lo mismo.
        /// </remarks>
        public const long ParteDeLaBanda = 667;

        /// <summary>Lo que cobra en objetos el que gana.</summary>
        public static Dictionary<int, int> Botin() => new Dictionary<int, int>
        {
            [Kolicha] = KolichasPorVictoria,
            [Vitoricha] = VitorichasPorVictoria,
        };

        /// <summary>
        /// La experiencia por ganar, para un personaje de ese nivel.
        /// </summary>
        /// <remarks>
        /// De la banda de SU nivel, no de la del rival: en la captura los dos ganadores cobran
        /// cada uno sobre la suya, y son niveles muy distintos —227 y 290— contra los mismos dos
        /// rivales.
        /// </remarks>
        public static long Experiencia(int nivel)
        {
            long suelo = ExperienceTable.LevelFloor(nivel);
            long siguiente = ExperienceTable.NextLevelFloor(nivel);

            long banda = siguiente - suelo;
            if (banda <= 0) return 0;

            return banda * ParteDeLaBanda / 10000;
        }
    }
}

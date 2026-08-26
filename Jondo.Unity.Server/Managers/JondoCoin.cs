using System;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// La Jondo Coin: la moneda propia del servidor, la que sueltan todos los monstruos y con la
    /// que se paga en las tiendas que no cobran en kamas.
    ///
    /// No es un objeto nuevo. El cliente es el de Ankama y sólo sabe dibujar y nombrar lo que
    /// viene en sus propios datos, así que un id inventado no existiría para él: no tendría
    /// icono, ni nombre, ni sitio en el inventario. Lo que se hace es COGER UNO SUYO y cambiarle
    /// el nombre desde JondoFix, que es el mod del cliente (ver el parche de ItemData.get_name).
    ///
    /// El elegido es la «Moneda onírica minúscula», y no por casualidad:
    ///
    ///   - su icono, el 148013, es una moneda turquesa con destellos: se distingue de un vistazo
    ///     de las kamas, que son amarillas;
    ///   - PESA CERO. Es lo que hace que esto funcione: alguien puede juntar cincuenta mil sin
    ///     que los pods le digan nada. Con casi cualquier otro recurso el jugador se habría
    ///     quedado clavado a las doscientas monedas;
    ///   - es del tipo 131, el de los recursos que sueltan los monstruos, así que se apila y se
    ///     comporta como lo que es;
    ///   - no la usa ninguna receta ni ningún oficio, así que quitársela al juego no rompe nada.
    ///
    /// Y tiene tres hermanas con el MISMO icono y también sin peso —la 20441 pequeña, la 20442
    /// grande y la 20443 enorme—, por si algún día hace falta una moneda de otro rango.
    /// </summary>
    public static class JondoCoin
    {
        /// <summary>
        /// La plantilla que hace de moneda. «Moneda onírica minúscula» en los datos de Ankama,
        /// «Jondo Coin» en la pantalla del jugador.
        /// </summary>
        public const int TemplateId = 20440;

        /// <summary>Lo ancho que es cada tramo: de 25 en 25 niveles.</summary>
        public const int LevelsPerBand = 25;

        /// <summary>
        /// El nivel a partir del cual se deja de subir de tramo, o sea el techo: 9 monedas.
        ///
        /// La regla es «una moneda más por cada 25 niveles», pero llevada al pie de la letra un
        /// monstruo de nivel 2.400 pagaría 96 monedas de una sentada. Medido sobre las 26.969
        /// combinaciones de monstruo y grado del juego: la mediana es 140, el percentil 99 es 220
        /// y sólo 188 —de 51 plantillas de 5.134— pasan del 225. Y las de más arriba son jefes o
        /// entradas de prueba: «[!] Willorque» con 2.400, «[!] Mureine» con 1.800.
        ///
        /// Así que el tramo 9 es el último. Cubre el 99 % del juego tal cual lo pidió la regla, y
        /// le pone puerta al 1 % que la rompería. Si algún día se quiere sin techo, se sube este
        /// número y ya está.
        /// </summary>
        public const int HighestBandedLevel = 225;

        /// <summary>Cuántas monedas paga un monstruo de ese nivel.</summary>
        ///
        /// <remarks>
        /// Nivel 1 a 25 una, 26 a 50 dos, 51 a 75 tres, y así. Un nivel cero o negativo —que no
        /// debería existir, pero los datos del cliente son suyos y no nuestros— paga una, no
        /// cero ni un número raro.
        /// </remarks>
        public static int RewardFor(int monsterLevel)
        {
            int nivel = Math.Clamp(monsterLevel, 1, HighestBandedLevel);
            return 1 + (nivel - 1) / LevelsPerBand;
        }
    }
}

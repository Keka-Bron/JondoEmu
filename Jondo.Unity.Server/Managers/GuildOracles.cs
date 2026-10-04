using System.Collections.Generic;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Los cinco oráculos de la tienda del gremio: qué cuestan y qué alteración ponen.
    ///
    /// La tienda (jkh) los manda por su número de 1 a 5 con el precio YA MULTIPLICADO por las
    /// cuentas activas del gremio: en el gremio de cuatro cuentas de la captura salen 80, 80,
    /// 800, 200 y 80, y en el de una, 20, 20, 200, 50 y 20. De ahí los precios de aquí.
    ///
    /// Qué alteración pone cada uno está medido en UNO: se compró el 1, se activó, y lo que
    /// llegó fue la alteración 859, «Oráculo de saber». Los otros cuatro se colocan por su
    /// precio, que sólo deja libre el orden de los tres que valen 20:
    ///
    ///   859 saber      20    ←  medido
    ///   860 fortuna    20    ←  inferencia: es uno de los tres de 20
    ///   862 divino    200    ←  el único de 200
    ///   861 recolector 50    ←  el único de 50
    ///   863 gladiador  20    ←  inferencia, como el de fortuna
    ///
    /// Los precios de la ficha del juego -saber, fortuna y gladiador a 20, recolector a 50,
    /// divino a 200- cuadran con los cinco de la captura, así que lo único sin demostrar es si
    /// fortuna va en el 2 y gladiador en el 5 o al revés.
    /// </summary>
    public static class GuildOracles
    {
        public sealed class Oracle
        {
            public int Id { get; init; }

            /// <summary>Lo que cuesta POR CUENTA activa del gremio, en kamas de gremio.</summary>
            public int PricePerAccount { get; init; }

            /// <summary>La alteración que pone al activarlo.</summary>
            public int Alteration { get; init; }
        }

        private static readonly Dictionary<int, Oracle> Catalogo = new()
        {
            [1] = new Oracle { Id = 1, PricePerAccount = 20,  Alteration = 859 },
            [2] = new Oracle { Id = 2, PricePerAccount = 20,  Alteration = 860 },
            [3] = new Oracle { Id = 3, PricePerAccount = 200, Alteration = 862 },
            [4] = new Oracle { Id = 4, PricePerAccount = 50,  Alteration = 861 },
            [5] = new Oracle { Id = 5, PricePerAccount = 20,  Alteration = 863 },
        };

        /// <summary>Los cinco, en el orden en que viajan en la tienda.</summary>
        public static IReadOnlyList<Oracle> All => new List<Oracle>
        {
            Catalogo[1], Catalogo[2], Catalogo[3], Catalogo[4], Catalogo[5],
        };

        public static Oracle Of(int id) => Catalogo.TryGetValue(id, out var oracle) ? oracle : null;

        /// <summary>Lo que cuesta de verdad: el precio por cuenta por las cuentas que hay.</summary>
        public static int PriceFor(int id, int accounts)
        {
            var oracle = Of(id);
            return oracle == null ? 0 : oracle.PricePerAccount * (accounts < 1 ? 1 : accounts);
        }

        /// <summary>
        /// Lo que dura puesto un oráculo: dos horas. Lo dice su propia descripción -«durante dos
        /// horas»- y lo confirma la captura, donde el lzs va de 1788304392333 a 1788311580000.
        /// </summary>
        public const int HoursActive = 2;

        /// <summary>
        /// Lo que se tiene para activarlo desde que se compra: un día. En la captura el plazo
        /// que viaja en el jkv cae 24 horas menos unos segundos después de la compra.
        /// </summary>
        public const int HoursToActivate = 24;
    }
}

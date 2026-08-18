using System;
using System.Collections.Generic;

namespace Jondo.Unity.World.Maps
{
    /// <summary>
    /// Las casillas que coge un efecto de hechizo alrededor de la que se apunta.
    ///
    /// La forma viene en el <c>zoneDescr</c> del EffectsJson y es una LETRA guardada como su
    /// código: 'P' un punto, 'C' un círculo, 'X' una cruz, 'L' una línea… El tamaño es el
    /// <c>param1</c> y significa una cosa u otra según la forma —radio en el círculo, largo en la
    /// línea—.
    ///
    /// Las que usa el Ocra, contadas sobre sus 44 hechizos:
    ///
    ///   'P' x534   un punto: sólo la casilla apuntada. Es la de casi todo.
    ///   'C' x49    círculo de radio param1. El Ojo de Topo es 'C' de 2.
    ///   'X' x18    cruz: los cuatro rayos rectos, medido contra las capturas.
    ///   'T' x9     cruz recta de radio param1 sin las diagonales.
    ///   'a' x8     TODO el mapa.
    ///   'L' x9     línea recta desde el lanzador.
    ///   'V' x6     media línea.
    ///   'F' x6     la casilla y sus vecinas en la dirección.
    ///   'Q' x10    rombo hueco.
    ///   'U' x3     'G' x3   '+' x2   '#' x2
    ///
    /// Lo que no está medido NO se inventa: una forma desconocida devuelve la casilla apuntada y
    /// se anota, que es lo que hacía el emulador con todas.
    /// </summary>
    public static class Zone
    {
        public const int Punto = 'P';
        public const int Circulo = 'C';
        public const int Aspa = 'X';
        public const int Cruz = 'T';
        public const int TodoElMapa = 'a';
        public const int Linea = 'L';
        public const int MediaLinea = 'V';
        public const int Rombo = 'Q';
        public const int CruzCompleta = '+';
        public const int Cuadrado = '#';

        /// <summary>
        /// Las casillas que toca el efecto.
        ///
        /// <paramref name="desde"/> es la casilla del que lanza, que hace falta para las formas
        /// que tienen dirección (las líneas); <paramref name="centro"/> es a la que se apunta.
        /// </summary>
        public static List<int> Casillas(int forma, int tamano, int desde, int centro)
        {
            var fuera = new List<int>();
            if (!MapGeometry.IsValid(centro)) return fuera;
            if (tamano < 0) tamano = 0;

            switch (forma)
            {
                case Punto:
                    fuera.Add(centro);
                    return fuera;

                case TodoElMapa:
                    for (int c = 0; c < MapGeometry.MaxCells; c++) fuera.Add(c);
                    return fuera;

                case Circulo:
                    // Todo lo que esté a `tamano` pasos o menos, con la distancia del combate.
                    for (int c = 0; c < MapGeometry.MaxCells; c++)
                        if (MapGeometry.Distance(centro, c) <= tamano) fuera.Add(c);
                    return fuera;

                case Cruz:
                    // Las cuatro direcciones rectas, hasta `tamano`, más el centro.
                    fuera.Add(centro);
                    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                        Estirar(fuera, centro, dx, dy, tamano);
                    return fuera;

                case CruzCompleta:
                    fuera.Add(centro);
                    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1),
                                                     (1, 1), (-1, -1), (1, -1), (-1, 1) })
                        Estirar(fuera, centro, dx, dy, tamano);
                    return fuera;

                case Aspa:
                    // La 'X' son los cuatro rayos RECTOS, los mismos por los que se anda, y no las
                    // diagonales.
                    //
                    // Estaba al revés, y era lo que dejaba a Flecha de Dispersión pegando a uno
                    // solo. Con las diagonales, una 'X' de radio dos sólo genera casillas a
                    // distancia PAR —el centro, cuatro a distancia dos y cuatro a distancia
                    // cuatro— y ninguna a distancia uno, que es justo donde se ponen los bichos.
                    //
                    // Medido: doce impactos de zona 'X' en las capturas —cinco en Flecha de
                    // Dispersión, cinco en Vendetta y dos lanzamientos de Ojo por Ojo—, todos en
                    // línea recta y ninguno en diagonal. Cinco de ellos caen a distancia IMPAR,
                    // que con el aspa es geométricamente imposible.
                    fuera.Add(centro);
                    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                        Estirar(fuera, centro, dx, dy, tamano);
                    return fuera;

                case Cuadrado:
                    var (cx, cy) = MapGeometry.CellToPoint(centro);
                    for (int x = cx - tamano; x <= cx + tamano; x++)
                        for (int y = cy - tamano; y <= cy + tamano; y++)
                        {
                            int c = MapGeometry.PointToCell(x, y);
                            if (c >= 0) fuera.Add(c);
                        }
                    return fuera;

                case Rombo:
                    // Sólo el borde del círculo, que es lo que lo distingue de la 'C'.
                    for (int c = 0; c < MapGeometry.MaxCells; c++)
                        if (MapGeometry.Distance(centro, c) == tamano) fuera.Add(c);
                    return fuera;

                case Linea:
                case MediaLinea:
                {
                    // Siguen recto en la dirección en la que se lanzó.
                    fuera.Add(centro);
                    var d = DireccionEntre(desde, centro);
                    if (d.HasValue) Estirar(fuera, centro, d.Value.Dx, d.Value.Dy, tamano);
                    return fuera;
                }

                default:
                    // Sin medir: la casilla apuntada y nada más.
                    fuera.Add(centro);
                    return fuera;
            }
        }

        private static void Estirar(List<int> donde, int centro, int dx, int dy, int cuantas)
        {
            var (x, y) = MapGeometry.CellToPoint(centro);
            for (int i = 1; i <= cuantas; i++)
            {
                int c = MapGeometry.PointToCell(x + dx * i, y + dy * i);
                if (c < 0) break;
                donde.Add(c);
            }
        }

        /// <summary>
        /// Las OCHO direcciones de Dofus en coordenadas de mapa, numeradas como las numera el
        /// cliente. Las impares son las cuatro por las que se anda; las pares, las diagonales.
        /// </summary>
        public static readonly (int Dx, int Dy)[] Direcciones =
        {
            (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0), (-1, 1), (0, 1),
        };

        /// <summary>
        /// Cuál de las ocho se parece más al camino de una casilla a otra. Es lo que decide hacia
        /// dónde sale volando el que recibe un empujón.
        /// </summary>
        public static (int Dx, int Dy)? DireccionEntre(int desde, int hasta)
        {
            if (!MapGeometry.IsValid(desde) || !MapGeometry.IsValid(hasta) || desde == hasta) return null;
            var (ax, ay) = MapGeometry.CellToPoint(desde);
            var (bx, by) = MapGeometry.CellToPoint(hasta);
            double dx = bx - ax, dy = by - ay;
            double largo = Math.Sqrt(dx * dx + dy * dy);
            if (largo == 0) return null;

            (int Dx, int Dy)? mejor = null;
            double mejorParecido = double.NegativeInfinity;
            foreach (var d in Direcciones)
            {
                double suLargo = Math.Sqrt(d.Dx * d.Dx + d.Dy * d.Dy);
                double parecido = (dx * d.Dx + dy * d.Dy) / (largo * suLargo);
                if (parecido > mejorParecido + 1e-9) { mejorParecido = parecido; mejor = d; }
            }
            return mejor;
        }

        /// <summary>
        /// Si un desplazamiento ALEJA del sitio del que sale o acerca a él. Es lo que decide con
        /// qué número viaja por el cable: el 5 alejarse, el 6 acercarse.
        /// </summary>
        public static bool SeAleja(int desde, int hasta, int centro, int deQuienLanza)
        {
            int origen = (MapGeometry.IsValid(centro) && centro != desde) ? centro : deQuienLanza;
            if (!MapGeometry.IsValid(origen)) return true;
            return MapGeometry.Distance(origen, hasta) >= MapGeometry.Distance(origen, desde);
        }

        /// <summary>
        /// Adónde va a parar el que recibe un empujón (o un tirón, con las casillas en negativo).
        ///
        /// La dirección sale de la casilla a la que se lanzó el hechizo —el centro de su zona—
        /// hacia el que sale volando; si es el que está justo en esa casilla, no hay vector y
        /// entonces manda la casilla del que lanza. Medido sobre los 76 desplazamientos de las
        /// capturas del Ocra.
        ///
        /// Se para en lo primero que encuentre: borde, obstáculo u otro combatiente.
        /// </summary>
        public static int Empujar(int centro, int deQuienLanza, int aQuien, int casillas,
                                  HashSet<int> pisables, HashSet<int> ocupadas)
        {
            if (casillas == 0 || !MapGeometry.IsValid(aQuien)) return aQuien;

            int origen = (centro != aQuien && MapGeometry.IsValid(centro)) ? centro : deQuienLanza;
            var d = DireccionEntre(origen, aQuien);
            if (d == null) return aQuien;

            int dx = d.Value.Dx, dy = d.Value.Dy;
            if (casillas < 0) { dx = -dx; dy = -dy; }   // atraer es lo mismo del revés

            var (x, y) = MapGeometry.CellToPoint(aQuien);
            int donde = aQuien;
            for (int i = 0; i < Math.Abs(casillas); i++)
            {
                x += dx; y += dy;
                int siguiente = MapGeometry.PointToCell(x, y);
                if (siguiente < 0) break;
                if (pisables != null && !pisables.Contains(siguiente)) break;
                if (ocupadas != null && ocupadas.Contains(siguiente)) break;
                donde = siguiente;
            }
            return donde;
        }
    }
}

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
    ///   'T' x9     la barra: el centro y param1 casillas a cada lado, ATRAVESADA al
    ///              lanzamiento. Medida en siete impactos con posiciones: Cencerro (T2),
    ///              Magmacha Calcinada (T1), Flecha de Pelea (T2, dos veces), Impacto
    ///              Aplastante (T1), Espora Dyka (T5, dos veces): todas las víctimas en la
    ///              perpendicular del eje del lanzamiento o en el centro, ninguna detrás
    ///              ni delante. Era una cruz, y Espada Destructora le pegaba al Yopuka que
    ///              la lanzaba desde al lado.
    ///   'a' x8     TODO el mapa.
    ///   'L' x9     línea recta desde el lanzador.
    ///   'V' x6     media línea.
    ///   'F' x6     la casilla y sus vecinas en la dirección.
    ///   'Q' x10    cruz recta, como la 'X', con param2 de radio interior. Las fichas la
    ///              llaman cruz -- "en una cruz de 2 casillas" Transposición Amenazadora,
    ///              "en una cruz de 1 casilla" Llave de Contacto -- y en las capturas Palabra
    ///              Turbulenta (Q1) empuja a (0,-1), (1,0) y (-1,0) del centro, Palabra
    ///              Entretenida (Q3) atrae desde (1,0), (2,0) y (3,0), y Flecha Asaltante y
    ///              Flecha Evasiva ponen su "950 mask c" (Q1) en el Ocra a una casilla recta
    ///              del centro. Era un anillo, que es la 'O'.
    ///   'U' x3     'G' x3   '+' x2   '#' x2
    ///
    /// Lo que no está medido NO se inventa: una forma desconocida devuelve la casilla apuntada y
    /// se anota, que es lo que hacía el emulador con todas.
    ///
    /// The letters are the client's own: its zone factory (gru::blgy in the 3.6.10 GameAssembly)
    /// turns each one into a shape class, and that is what the constants below follow. P X Q + # *
    /// are one class, a cross whose rays go along the walking axes (X, Q), the diagonals (+, #) or
    /// both (*), with the centre (X + *) or without it (Q #). L and '/' are one line class, T and
    /// '-' one perpendicular line class; O is the circle between param1 and param1, I the circle
    /// from param1 out to the edge.
    /// </summary>
    public static class Zone
    {
        public const int Punto = 'P';
        public const int Circulo = 'C';
        public const int Aspa = 'X';
        public const int Barra = 'T';
        public const int TodoElMapa = 'a';
        public const int WholeMap = 'A';
        public const int Linea = 'L';
        public const int CruzRecta = 'Q';
        public const int MedioCirculo = 'U';
        public const int Segmento = 'l';

        /// <summary>The cross along the four diagonals, its centre included.</summary>
        public const int DiagonalCross = '+';

        /// <summary>The cross along the four diagonals, without its centre.</summary>
        public const int DiagonalCrossWithoutCentre = '#';

        /// <summary>The eight rays, the centre included.</summary>
        public const int Star = '*';

        /// <summary>The line class under another letter: the client builds L and '/' alike.</summary>
        public const int DiagonalLine = '/';

        /// <summary>Every cell at param1 steps or more from the centre: I0 is the whole map.</summary>
        public const int OutsideCircle = 'I';

        /// <summary>The filled square, param1 cells each way along the map's two axes.</summary>
        public const int Square = 'G';

        /// <summary>The square without its two diagonals, and so without its centre.</summary>
        public const int SquareWithoutDiagonals = 'W';

        /// <summary>The cone opening away from the caster.</summary>
        public const int Cone = 'V';

        /// <summary>The centre and three rays ahead: the cast's direction and its two neighbours.</summary>
        public const int Fork = 'F';

        /// <summary>A bar across the cast with its two ends bent back.</summary>
        public const int Boomerang = 'B';

        /// <summary>Every other cell of a circle, the outer ring's colour kept.</summary>
        public const int Checkerboard = 'D';

        /// <summary>param1 cells to each side of the cast, param2 cells ahead.</summary>
        public const int Rectangle = 'R';

        /// <summary>Every cell outside a circle measured straight, not in steps.</summary>
        public const int OutsideComplexCircle = 'Z';

        /// <summary>
        /// The ring: the cells at EXACTLY param1 steps from the centre, the centre left out. The
        /// client's own text for the shape (1119924) says "las casillas situadas exactamente a
        /// una cierta distancia de la casilla objetivo, pero no a esta última", and the spells
        /// that carry it call it "un anillo de 2 casillas" (Kabombz, Ovobz). Colado is O2, and in
        /// its capture the bomb it mirrors stands two cells from the centre in all three casts.
        /// </summary>
        public const int Anillo = 'O';

        /// <summary>
        /// The perpendicular line: the centre and param1 cells to each side of it ACROSS the
        /// direction of the cast. "Arcabuz de Dopeul" describes it in so many words -- "una zona
        /// de efecto en línea perpendicular" -- and the client's text (1119963) is "las casillas
        /// alineadas perpendicularmente con la zona de lanzamiento". Fusil is a '-' of 2 and
        /// pushes "hacia los extremos", which is what a push away from the centre does along
        /// this bar. The perpendicular of a diagonal cast is the other diagonal: the client's
        /// preview draws it so, and Fusil has no capture to say otherwise.
        /// </summary>
        public const int LineaPerpendicular = '-';

        /// <summary>
        /// Las casillas que toca el efecto.
        ///
        /// <paramref name="desde"/> es la casilla del que lanza, que hace falta para las formas
        /// que tienen dirección (las líneas); <paramref name="centro"/> es a la que se apunta.
        /// </summary>
        /// <param name="minimo">
        /// The <c>param2</c> of the zone. For the circle, the inner edge in distance: "Venganza
        /// Nocturna" hits C2/1, "alrededor de Sombra" and not Sombra herself. For the crosses
        /// (X Q + # *), the first step kept along each ray: Patada's X3/3, X2/2 and X1/1 are the
        /// four cells that far, Imantación's X6/1 a cross with a hole where its centre is. For the
        /// line from the caster ('l'), its length.
        /// </param>
        /// <param name="stopAtTarget">
        /// The zone's <c>isStopAtTarget</c>: the line from the caster ('l') ends at the aimed cell
        /// instead of running on to its length.
        /// </param>
        public static List<int> Casillas(int forma, int tamano, int desde, int centro, int minimo = 0,
                                         bool stopAtTarget = true)
        {
            var fuera = Formas(forma, tamano, desde, centro, minimo, stopAtTarget);
            // The circle's inner edge is a distance. The crosses count theirs in steps along each
            // ray, which is not the same on a diagonal, and Rays does it itself.
            if (minimo > 0 && forma == Circulo) fuera.RemoveAll(c => MapGeometry.Distance(centro, c) < minimo);
            return fuera;
        }

        /// <summary>Whether this build knows the shape, rather than falling back to the aimed cell.</summary>
        public static bool IsKnown(int forma)
            => forma is Punto or Circulo or Aspa or Barra or TodoElMapa or WholeMap or Linea or CruzRecta
                     or MedioCirculo or Segmento or Anillo or LineaPerpendicular or DiagonalCross
                     or DiagonalCrossWithoutCentre or Star or DiagonalLine or OutsideCircle or Square
                     or SquareWithoutDiagonals or Cone or Fork or Boomerang or Checkerboard or Rectangle
                     or OutsideComplexCircle or FormaDeCeldasFijas;

        /// <summary>The zone whose cells the effect names outright (<c>cellIds</c>).</summary>
        public const int FormaDeCeldasFijas = ';';

        private static readonly (int Dx, int Dy)[] Walking = { (1, 0), (-1, 0), (0, 1), (0, -1) };
        private static readonly (int Dx, int Dy)[] Diagonals = { (1, 1), (-1, -1), (1, -1), (-1, 1) };
        private static readonly (int Dx, int Dy)[] EightWays =
            { (1, 1), (1, -1), (-1, -1), (-1, 1), (1, 0), (0, -1), (-1, 0), (0, 1) };

        /// <summary>
        /// The client's cross (SpellZoneShapeCrossBehavior, its cell routine read at 0x181EB5190):
        /// along each direction, steps 1 to <paramref name="radius"/>, a cell kept from step
        /// <c>min</c> on, where min is param2 and at least one for the shapes without centre; the
        /// centre only when the shape has it and min is zero. The inner edge counts STEPS along
        /// the ray: one diagonal step is two cells away.
        /// </summary>
        private static void Rays(List<int> cells, int centre, int radius, int min, bool withCentre,
                                 (int Dx, int Dy)[] directions)
        {
            if (!withCentre) min = Math.Max(min, 1);
            if (min <= 0) cells.Add(centre);
            var (x, y) = MapGeometry.CellToPoint(centre);
            foreach (var (dx, dy) in directions)
            {
                for (int step = 1; step <= radius; step++)
                {
                    int c = MapGeometry.PointToCell(x + dx * step, y + dy * step);
                    if (c < 0) break;
                    if (step >= min) cells.Add(c);
                }
            }
        }

        /// <summary>
        /// The direction the shapes that turn with the cast are drawn along, numbered as the
        /// client numbers them: the nearest of the eight from the caster to the centre, and 1 when
        /// the caster aims at his own cell -- what the client's own orientation helper (og::ovk)
        /// answers for two equal cells.
        /// </summary>
        private static int Heading(int desde, int centro)
        {
            var d = DireccionEntre(desde, centro);
            return d.HasValue ? Array.IndexOf(Direcciones, d.Value) : 1;
        }

        private static (int Dx, int Dy) Turn(int heading, int by) => Direcciones[((heading + by) % 8 + 8) % 8];

        /// <summary>A point of the map as a cell, off the map as -1.</summary>
        private static int At(int x, int y) => MapGeometry.PointToCell(x, y);

        private static void AddIfOn(List<int> cells, int x, int y)
        {
            int c = At(x, y);
            if (c >= 0) cells.Add(c);
        }

        private static List<int> Formas(int forma, int tamano, int desde, int centro, int minimo, bool stopAtTarget)
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
                case WholeMap:
                    for (int c = 0; c < MapGeometry.MaxCells; c++) fuera.Add(c);
                    return fuera;

                case Circulo:
                    // Todo lo que esté a `tamano` pasos o menos, con la distancia del combate.
                    for (int c = 0; c < MapGeometry.MaxCells; c++)
                        if (MapGeometry.Distance(centro, c) <= tamano) fuera.Add(c);
                    return fuera;

                case Barra:
                {
                    // The bar across the cast: the centre and `tamano` cells to each side of
                    // it, along the axis perpendicular to the nearest of the eight directions
                    // from the caster to the centre. The same bar as '-'; where the two differ
                    // is not measured (the client's text keeps '-' for line casts only).
                    fuera.Add(centro);
                    var d = DireccionEntre(desde, centro);
                    if (!d.HasValue) return fuera;
                    Estirar(fuera, centro, -d.Value.Dy, d.Value.Dx, tamano);
                    Estirar(fuera, centro, d.Value.Dy, -d.Value.Dx, tamano);
                    return fuera;
                }

                case CruzRecta:
                    // The straight cross WITHOUT its centre: the client builds Q as the X with
                    // the centre left out, so a Q1 is the four cells around the aimed one.
                    Rays(fuera, centro, tamano, minimo, withCentre: false, Walking);
                    return fuera;

                case DiagonalCross:
                    Rays(fuera, centro, tamano, minimo, withCentre: true, Diagonals);
                    return fuera;

                case DiagonalCrossWithoutCentre:
                    Rays(fuera, centro, tamano, minimo, withCentre: false, Diagonals);
                    return fuera;

                case Star:
                    Rays(fuera, centro, tamano, minimo, withCentre: true, EightWays);
                    return fuera;

                case OutsideCircle:
                    for (int c = 0; c < MapGeometry.MaxCells; c++)
                        if (MapGeometry.Distance(centro, c) >= tamano) fuera.Add(c);
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
                    Rays(fuera, centro, tamano, minimo, withCentre: true, Walking);
                    return fuera;

                case Square:
                case SquareWithoutDiagonals:
                {
                    // SpellZoneShapeSquareBehavior: every (x + i, y + j) with |i| and |j| at most
                    // param1 -- one at least, so a G0 is a G1 -- whatever the direction. The W
                    // leaves out its two diagonals, |i| == |j|, and the centre with them.
                    int r = Math.Max(1, tamano);
                    var (cx, cy) = MapGeometry.CellToPoint(centro);
                    for (int i = -r; i <= r; i++)
                        for (int j = -r; j <= r; j++)
                        {
                            if (forma == SquareWithoutDiagonals && Math.Abs(i) == Math.Abs(j)) continue;
                            AddIfOn(fuera, cx + i, cy + j);
                        }
                    return fuera;
                }

                case MedioCirculo:
                {
                    // SpellZoneShapeHalfCircleBehavior: the centre and two rays of param1 (one at
                    // least) along the direction turned three eighths each way -- back towards
                    // the caster, a V around the aimed cell. The client's text: "la casilla
                    // objetivo y las casillas de dos de sus diagonales, formando un semicírculo".
                    int r = Math.Max(1, tamano), heading = Heading(desde, centro);
                    var a = Turn(heading, 3);
                    var b = Turn(heading, -3);
                    var (cx, cy) = MapGeometry.CellToPoint(centro);
                    fuera.Add(centro);
                    bool aOn = true, bOn = true;
                    for (int i = 1; i <= r; i++)
                    {
                        if (aOn) { int c = At(cx + a.Dx * i, cy + a.Dy * i); if (c >= 0) fuera.Add(c); else aOn = false; }
                        if (bOn) { int c = At(cx + b.Dx * i, cy + b.Dy * i); if (c >= 0) fuera.Add(c); else bOn = false; }
                    }
                    return fuera;
                }

                case Cone:
                {
                    // SpellZoneShapeConeBehavior: k = 0 to param1 steps ahead of the centre along
                    // the cast, and at each the cell there with k cells to each side of it, across.
                    // A triangle opening away from the caster, its point on the aimed cell; on a
                    // diagonal cast the sides run along the other diagonal, so it has holes.
                    int r = Math.Max(0, tamano), heading = Heading(desde, centro);
                    var ahead = Direcciones[heading];
                    var left = Turn(heading, 2);
                    var right = Turn(heading, -2);
                    var (cx, cy) = MapGeometry.CellToPoint(centro);
                    for (int k = 0; k <= r; k++)
                    {
                        int x = cx + ahead.Dx * k, y = cy + ahead.Dy * k;
                        if (At(x, y) < 0) break;
                        fuera.Add(At(x, y));
                        for (int s = 1; s <= k; s++)
                        {
                            int c = At(x + left.Dx * s, y + left.Dy * s);
                            if (c < 0) break;
                            fuera.Add(c);
                        }
                        for (int s = 1; s <= k; s++)
                        {
                            int c = At(x + right.Dx * s, y + right.Dy * s);
                            if (c < 0) break;
                            fuera.Add(c);
                        }
                    }
                    return fuera;
                }

                case Fork:
                {
                    // SpellZoneShapeForkBehavior: the centre and three rays of param1 + 1 cells
                    // (the constructor adds the one), straight ahead and the two diagonals beside
                    // it. The client handles the four walking directions; every diagonal one
                    // draws the fork of direction 7.
                    int length = Math.Max(0, tamano + 1), heading = Heading(desde, centro);
                    bool across = heading is 1 or 5;
                    int sign = heading is 3 or 5 ? -1 : 1;
                    var (cx, cy) = MapGeometry.CellToPoint(centro);
                    fuera.Add(centro);
                    for (int i = 1; i <= length; i++)
                        foreach (int j in new[] { -1, 0, 1 })
                        {
                            if (across) AddIfOn(fuera, cx + sign * i, cy + j * i);
                            else AddIfOn(fuera, cx + j * i, cy + sign * i);
                        }
                    return fuera;
                }

                case Boomerang:
                {
                    // SpellZoneShapeBoomerangBehavior: a bar across the cast, param1 - 1 cells to
                    // each side of the centre, and at each end one more cell bent back three
                    // eighths against the cast. The centre only when param2 is zero; a param2
                    // shortens the bar, literally as the client does (the data never has one).
                    int length = Math.Max(1, tamano), min = Math.Max(0, minimo), heading = Heading(desde, centro);
                    var a = Turn(heading, 2);
                    var b = Turn(heading, -2);
                    var (cx, cy) = MapGeometry.CellToPoint(centro);
                    int i = min;
                    if (min == 0) { fuera.Add(centro); i = 1; }
                    int ax = cx, ay = cy, bx = cx, by = cy;
                    for (; i < length; i++)
                    {
                        ax += a.Dx; ay += a.Dy; bx += b.Dx; by += b.Dy;
                        AddIfOn(fuera, ax, ay);
                        AddIfOn(fuera, bx, by);
                    }
                    var ta = Turn(heading, 3);
                    var tb = Turn(heading, -3);
                    if (At(ax, ay) >= 0) AddIfOn(fuera, ax + ta.Dx, ay + ta.Dy);
                    if (At(bx, by) >= 0) AddIfOn(fuera, bx + tb.Dx, by + tb.Dy);
                    return fuera;
                }

                case Checkerboard:
                {
                    // SpellZoneShapeCheckerboardBehavior: the circle of param1 from param2 out,
                    // every other cell -- those whose distance has the parity of param1, so the
                    // outer ring is always kept. D60 and D61 are the two colours of a board over
                    // the whole map: the centre's and the other.
                    int r = Math.Max(0, tamano), min = Math.Max(0, minimo);
                    bool even = (r & 1) == 0;
                    var (cx, cy) = MapGeometry.CellToPoint(centro);
                    for (int i = -r; i <= r; i++)
                        for (int j = -r; j <= r; j++)
                        {
                            int far = Math.Abs(i) + Math.Abs(j);
                            if (far > r || far < min) continue;
                            if (((i + j) & 1) == 0 != even) continue;
                            AddIfOn(fuera, cx + i, cy + j);
                        }
                    return fuera;
                }

                case Rectangle:
                {
                    // SpellZoneShapeRectangleBehavior: 2 * param1 + 1 cells wide across the cast
                    // and param2 + 1 deep from the centre forward. The client knows the four
                    // walking directions; a diagonal one is drawn as direction 1.
                    int width = Math.Max(1, 2 * tamano + 1), depth = Math.Max(1, minimo + 1);
                    int heading = Heading(desde, centro);
                    int sign = heading is 3 or 5 ? -1 : 1;
                    bool alongY = heading is 3 or 7;
                    int half = width / 2;
                    var (cx, cy) = MapGeometry.CellToPoint(centro);
                    for (int k = 0; k < depth; k++)
                        for (int w = 0; w < width; w++)
                        {
                            if (alongY) AddIfOn(fuera, cx + w - half, cy + sign * k);
                            else AddIfOn(fuera, cx + sign * k, cy + w - half);
                        }
                    return fuera;
                }

                case OutsideComplexCircle:
                {
                    // SpellZoneShapeOutsideComplexCircleBehavior: every cell whose straight-line
                    // distance from the centre is param1 (one at least) or more, squared to stay
                    // in whole numbers. Never the centre.
                    int r = Math.Max(1, tamano);
                    var (cx, cy) = MapGeometry.CellToPoint(centro);
                    for (int c = 0; c < MapGeometry.MaxCells; c++)
                    {
                        var (x, y) = MapGeometry.CellToPoint(c);
                        if ((x - cx) * (x - cx) + (y - cy) * (y - cy) >= r * r) fuera.Add(c);
                    }
                    return fuera;
                }

                case Segmento:
                {
                    // The segment from the caster towards the aimed cell, as the client's
                    // SpellZoneShapeLineFromCasterBehavior walks it: from param1 cells off the
                    // caster (zero takes the caster's own cell), param2 cells long, and no further
                    // than the aimed cell when the zone stops at the target. "l1/63", the zone of
                    // what Empujoncito and Aspirador do to the bombs on the way, is the line from
                    // the cell after the Tymobot's to where it aims.
                    var d = DireccionEntre(desde, centro);
                    if (d == null) { fuera.Add(centro); return fuera; }
                    var (x, y) = MapGeometry.CellToPoint(desde);
                    int ultimo = Math.Max(0, tamano) + minimo - 1;
                    int hasta = stopAtTarget ? Math.Min(ultimo, MapGeometry.Distance(desde, centro)) : ultimo;
                    for (int paso = Math.Max(0, tamano); paso <= hasta; paso++)
                    {
                        int c = MapGeometry.PointToCell(x + d.Value.Dx * paso, y + d.Value.Dy * paso);
                        if (c < 0) break;
                        fuera.Add(c);
                    }
                    return fuera;
                }

                case Linea:
                case DiagonalLine:
                {
                    // Siguen recto en la dirección en la que se lanzó.
                    fuera.Add(centro);
                    var d = DireccionEntre(desde, centro);
                    if (d.HasValue) Estirar(fuera, centro, d.Value.Dx, d.Value.Dy, tamano);
                    return fuera;
                }

                case LineaPerpendicular:
                {
                    // The centre and a ray to each side of it, across the cast: the direction
                    // turned a quarter both ways. Without a direction -- the caster standing on
                    // the centre -- it is the centre alone.
                    fuera.Add(centro);
                    var d = DireccionEntre(desde, centro);
                    if (!d.HasValue) return fuera;
                    Estirar(fuera, centro, -d.Value.Dy, d.Value.Dx, tamano);
                    Estirar(fuera, centro, d.Value.Dy, -d.Value.Dx, tamano);
                    return fuera;
                }

                case Anillo:
                {
                    // Exactly param1 away, and not the centre.
                    if (tamano <= 0) return fuera;
                    for (int c = 0; c < MapGeometry.MaxCells; c++)
                        if (MapGeometry.Distance(centro, c) == tamano) fuera.Add(c);
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
        /// The first fighter on the straight line from <paramref name="desde"/> towards
        /// <paramref name="hasta"/>: before the aimed cell (<paramref name="beyond"/> false,
        /// strictly between the two) or past it (true, from the cell after the aimed one to the
        /// edge). Null when there is nobody, or when the two cells are not on one of the eight
        /// lines. What "hasta la casilla objetivo" pushes and pulls.
        /// </summary>
        public static int FirstCellOnTheLine(int desde, int hasta, bool beyond, Func<int, bool> ocupada)
        {
            var d = DireccionEntre(desde, hasta);
            if (d == null || ocupada == null) return -1;
            var (x, y) = MapGeometry.CellToPoint(desde);
            int largo = MapGeometry.Distance(desde, hasta);
            int primero = beyond ? largo + 1 : 1;
            int ultimo = beyond ? MapGeometry.MaxCells : largo - 1;
            for (int paso = primero; paso <= ultimo; paso++)
            {
                int c = MapGeometry.PointToCell(x + d.Value.Dx * paso, y + d.Value.Dy * paso);
                if (c < 0) return -1;
                if (ocupada(c)) return c;
            }
            return -1;
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
            => Push(centro, deQuienLanza, aQuien, casillas, pisables, ocupadas).ToCell;

        /// <summary>Contra qué se paró un empujón.</summary>
        /// <remarks>
        /// La distinción NO es cosmética: chocar contra otro combatiente hace daño A LOS DOS —el
        /// empujado entero y la pared la mitad—, y chocar contra el borde o contra un muro se lo
        /// come sólo el empujado. Medido en las 401 capturas: 9 parejas de dos mensajes de daño de
        /// empuje seguidos, y las 9 con el segundo valiendo exactamente la mitad del primero.
        /// </remarks>
        public enum PushStop
        {
            /// <summary>Recorrió las casillas que le tocaban. No hay daño.</summary>
            None = 0,

            /// <summary>El borde de la retícula.</summary>
            Edge,

            /// <summary>Casilla que no se pisa: muro, agujero o fuera del suelo del mapa.</summary>
            Obstacle,

            /// <summary>Otro combatiente. El único caso en el que el daño va a dos.</summary>
            Fighter,

            /// <summary>
            /// A bomb wall. It ENTERS the cell and stops there, and there is no collision damage:
            /// the wall has its own.
            /// </summary>
            /// <remarks>
            /// From the class sheet: "Desplazar una entidad a un muro detendra su desplazamiento y
            /// le infligira danos." The entering-and-stopping part is measured: in
            /// "explobomba-tornabomba-...-explotandolas" frame 8282 pulls -3 from 274 to 260, a
            /// wall cell, and frame 8283 is the wall going off on it AT 260.
            /// </remarks>
            Wall,
        }

        /// <summary>Cómo acabó un empujón.</summary>
        /// <remarks>
        /// Lo que faltaba es <see cref="BlockedCells"/>. El daño de colisión sale de LAS CASILLAS
        /// QUE NO SE RECORRIERON, no de las recorridas ni de las que declara el hechizo, y la
        /// versión de antes devolvía sólo la casilla final: tiraba ese número a la basura.
        /// </remarks>
        public readonly struct PushResult
        {
            /// <summary>Dónde acabó.</summary>
            public int ToCell { get; init; }

            /// <summary>Cuántas casillas se quedaron sin recorrer. Cero si llegó entero.</summary>
            public int BlockedCells { get; init; }

            /// <summary>Contra qué se paró.</summary>
            public PushStop Stop { get; init; }

            /// <summary>La casilla del que hizo de pared, si fue un combatiente. Menos uno si no.</summary>
            public int BlockerCell { get; init; }
        }

        /// <summary>
        /// Adónde va a parar el que recibe un empujón (o un tirón, con las casillas en negativo), y
        /// contra qué se para.
        ///
        /// La dirección sale de la casilla a la que se lanzó el hechizo —el centro de su zona—
        /// hacia el que sale volando; si es el que está justo en esa casilla, no hay vector y
        /// entonces manda la casilla del que lanza. Medido sobre los 76 desplazamientos de las
        /// capturas del Ocra.
        /// </summary>
        public static PushResult Push(int centro, int deQuienLanza, int aQuien, int casillas,
                                      HashSet<int> pisables, HashSet<int> ocupadas,
                                      HashSet<int> paran = null)
        {
            var quieto = new PushResult { ToCell = aQuien, BlockedCells = 0,
                                          Stop = PushStop.None, BlockerCell = -1 };
            if (casillas == 0 || !MapGeometry.IsValid(aQuien)) return quieto;

            int origen = (centro != aQuien && MapGeometry.IsValid(centro)) ? centro : deQuienLanza;
            var d = DireccionEntre(origen, aQuien);
            if (d == null) return quieto;

            int dx = d.Value.Dx, dy = d.Value.Dy;
            if (casillas < 0) { dx = -dx; dy = -dy; }   // atraer es lo mismo del revés

            int pedidas = Math.Abs(casillas);
            var (x, y) = MapGeometry.CellToPoint(aQuien);
            int donde = aQuien, dadas = 0;
            var freno = PushStop.None;
            int paredEn = -1;

            for (int i = 0; i < pedidas; i++)
            {
                x += dx; y += dy;
                int siguiente = MapGeometry.PointToCell(x, y);

                if (siguiente < 0) { freno = PushStop.Edge; break; }
                if (pisables != null && !pisables.Contains(siguiente))
                {
                    freno = PushStop.Obstacle; break;
                }
                if (ocupadas != null && ocupadas.Contains(siguiente))
                {
                    freno = PushStop.Fighter; paredEn = siguiente; break;
                }

                // UN TIRON NO SE PASA DE LARGO. Atraer camina hacia el centro, y sin esto lo
                // cruzaba y salia por el otro lado: la Imantacion del tymador tira de sus bombas
                // seis casillas, asi que una bomba a dos del punto acababa cuatro casillas mas
                // alla, en la direccion contraria. Y como el hechizo tira DOS veces -- una en su
                // propio efecto 6 y otra en el 18652 que encadena --, la segunda la traia de
                // vuelta: en el registro se ve el baile, la bomba -5 de la 272 a la 185 y de la
                // 185 otra vez a la 272.
                //
                // Lo que se para es en cuanto pisaria el centro, que es donde para un tiron en
                // el juego: pegado a quien tira.
                if (casillas < 0 && siguiente == centro)
                {
                    freno = PushStop.Fighter; paredEn = siguiente; break;
                }

                donde = siguiente;
                dadas++;

                // AND A BOMB WALL STOPS IT DEAD, but only after stepping onto it. Unlike every
                // other stop above, this one happens AFTER the cell is taken: the sheet says
                // "desplazar una entidad a un muro detendra su desplazamiento", into it, not
                // short of it, and the capture shows exactly that -- pulled from 274 to 260 and
                // caught at 260.
                if (paran != null && paran.Contains(siguiente))
                {
                    freno = PushStop.Wall;
                    break;
                }
            }

            return new PushResult
            {
                ToCell = donde,
                BlockedCells = pedidas - dadas,
                Stop = freno,
                BlockerCell = paredEn,
            };
        }
    }
}

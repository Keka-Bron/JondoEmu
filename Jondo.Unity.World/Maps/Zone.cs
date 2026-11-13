using System;
using System.Collections.Generic;

namespace Jondo.Unity.World.Maps
{
    /// <summary>
    /// The cells a spell effect takes around the one targeted.
    ///
    /// The shape comes in the EffectsJson's <c>zoneDescr</c> and it is a LETTER stored as its
    /// code: 'P' a point, 'C' a circle, 'X' a cross, 'L' a line… The size is the
    /// <c>param1</c> and means one thing or another depending on the shape —radius in the circle, length in the
    /// line—.
    ///
    /// The ones the Cra uses, counted over its 44 spells:
    ///
    ///   'P' x534   a point: only the targeted cell. It is the one for almost everything.
    ///   'C' x49    circle of radius param1. Ojo de Topo is 'C' of 2.
    ///   'X' x18    cross: the four straight rays, measured against the captures.
    ///   'T' x9     the bar: the centre and param1 cells to each side, ACROSS the
    ///              cast. Measured in seven impacts with positions: Cencerro (T2),
    ///              Magmacha Calcinada (T1), Flecha de Pelea (T2, twice), Impacto
    ///              Aplastante (T1), Espora Dyka (T5, twice): all the victims on the
    ///              perpendicular of the cast's axis or at the centre, none behind
    ///              nor in front. It was a cross, and Espada Destructora hit the Iop who
    ///              cast it from right beside.
    ///   'a' x8     the WHOLE map.
    ///   'L' x9     straight line from the caster.
    ///   'V' x6     half line.
    ///   'F' x6     the cell and its neighbours in the direction.
    ///   'Q' x10    straight cross, like 'X', with param2 as inner radius. The sheets
    ///              call it a cross -- "en una cruz de 2 casillas" Transposición Amenazadora,
    ///              "en una cruz de 1 casilla" Llave de Contacto -- and in the captures Palabra
    ///              Turbulenta (Q1) pushes (0,-1), (1,0) and (-1,0) from the centre, Palabra
    ///              Entretenida (Q3) pulls from (1,0), (2,0) and (3,0), and Flecha Asaltante and
    ///              Flecha Evasiva put their "950 mask c" (Q1) on the Cra one straight cell
    ///              from the centre. It was a ring, which is 'O'.
    ///   'U' x3     'G' x3   '+' x2   '#' x2
    ///
    /// What is not measured is NOT invented: an unknown shape returns the targeted cell and
    /// is noted, which is what the emulator did with all of them.
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
        /// The cells the effect touches.
        ///
        /// <paramref name="desde"/> is the caster's cell, which is needed for the shapes
        /// that have a direction (the lines); <paramref name="centro"/> is the one targeted.
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

        /// <summary>
        /// How many cells of falloff a zone's blow counts against whoever stands on
        /// <paramref name="target"/>: the steps its <c>damageDecreaseStepPercent</c> is taken
        /// off for, before the cap. Zero when the shape does not fall off at all.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The client's rule, 3.6.10.11: <c>gru::blgz</c> picks the measure by shape and
        /// <c>grz::blil</c> applies it. The measure starts from the aimed cell, never from the
        /// caster, and an offset is taken off it before it counts:
        /// </para>
        /// <list type="bullet">
        /// <item>The whole map ('A', 'a') and the empty zone lose nothing, and neither does any
        /// zone bigger than 50 (a C63 is the whole map too).</item>
        /// <item>The ring ('O') counts from its own radius. All of its cells are that far, so
        /// it hits them in full. The Tigredón's Salto del Grillete (O2) was losing 20% on the
        /// only cells it has.</item>
        /// <item>The shapes with a minimum size ('C', 'Q', 'X', '+') and the line from the caster
        /// ('l') count from that minimum: a C3 with a hole of 1 hits its inner edge in full.</item>
        /// <item>The squares ('G', 'R', 'W') measure the larger of the two coordinate gaps.</item>
        /// <item>The half circle and the diagonal shapes ('U', '-', '/', '+', '#') take half the
        /// distance.</item>
        /// <item>The cone and the fork ('V', 'F') measure along the way they were cast
        /// (<see cref="Heading"/>, the client's og::ovk). On the two walked axes, it is the gap on
        /// that axis. Straight down or up, it is the gap between rows. Straight across, the
        /// client adds the two rows instead of taking one from the other. That looks like a slip,
        /// and it is kept as it is: the client shows its preview with it.</item>
        /// <item>Every other shape measures the plain distance.</item>
        /// </list>
        /// </remarks>
        public static int FalloffCells(int shape, int size, int minSize, int caster, int centre, int target)
        {
            if (!MapGeometry.IsValid(centre) || !MapGeometry.IsValid(target)) return 0;
            if (shape is 0 or ' ' or TodoElMapa or WholeMap || size > 50) return 0;

            int measure;
            switch (shape)
            {
                case Square: case Rectangle: case SquareWithoutDiagonals:
                {
                    var (cx, cy) = MapGeometry.CellToPoint(centre);
                    var (tx, ty) = MapGeometry.CellToPoint(target);
                    measure = Math.Max(Math.Abs(cx - tx), Math.Abs(cy - ty));
                    break;
                }
                case MedioCirculo: case DiagonalLine: case LineaPerpendicular:
                case DiagonalCross: case DiagonalCrossWithoutCentre:
                    measure = MapGeometry.Distance(centre, target) >> 1;
                    break;
                case Cone: case Fork:
                {
                    var (cx, cy) = MapGeometry.CellToPoint(centre);
                    var (tx, ty) = MapGeometry.CellToPoint(target);
                    measure = (Heading(caster, centre) % 4) switch
                    {
                        0 => Math.Abs(Math.Abs(cx - cy) + Math.Abs(tx - ty)),
                        1 => Math.Abs(cx - tx),
                        2 => Math.Abs(Math.Abs(cx - cy) - Math.Abs(tx - ty)),
                        _ => Math.Abs(cy - ty),
                    };
                    break;
                }
                default:
                    measure = MapGeometry.Distance(centre, target);
                    break;
            }

            int offset = shape == Anillo ? size
                       : shape is Circulo or CruzRecta or Aspa or DiagonalCross or Segmento ? minSize
                       : 0;
            return Math.Max(0, measure - offset);
        }

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
                    // Everything `tamano` steps away or less, with the fight's distance.
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
                    // 'X' is the four STRAIGHT rays, the same ones walked along, and not the
                    // diagonals.
                    //
                    // It was the other way round, and it was what left Flecha de Dispersión hitting only
                    // one. With the diagonals, an 'X' of radius two only generates cells at an
                    // EVEN distance —the centre, four at distance two and four at distance
                    // four— and none at distance one, which is exactly where the creatures stand.
                    //
                    // Measured: twelve 'X' zone impacts in the captures —five in Flecha de
                    // Dispersión, five in Vendetta and two casts of Ojo por Ojo—, all in a
                    // straight line and none diagonal. Five of them fall at an ODD distance,
                    // which with the saltire is geometrically impossible.
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
                    // They carry on straight in the direction it was cast in.
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
                    // Unmeasured: the targeted cell and nothing else.
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
        /// Dofus's EIGHT directions in map coordinates, numbered as the client numbers
        /// them. The odd ones are the four walked along; the even ones, the diagonals.
        /// </summary>
        public static readonly (int Dx, int Dy)[] Direcciones =
        {
            (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0), (-1, 1), (0, 1),
        };

        /// <summary>
        /// Which of the eight best resembles the path from one cell to another. It is what decides which way
        /// whoever receives a push goes flying.
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
        /// Where whoever receives a push ends up (or a pull, with the cells as a negative).
        ///
        /// The direction comes from the cell the spell was cast at —its zone's centre—
        /// towards whoever goes flying; if it is the one right on that cell, there is no vector and
        /// then the caster's cell rules. Measured over the 76 displacements of the
        /// Cra captures.
        ///
        /// It stops at the first thing it meets: edge, obstacle or another fighter.
        /// </summary>
        public static int Empujar(int centro, int deQuienLanza, int aQuien, int casillas,
                                  HashSet<int> pisables, HashSet<int> ocupadas)
            => Push(centro, deQuienLanza, aQuien, casillas, pisables, ocupadas).ToCell;

        /// <summary>What a push stopped against.</summary>
        /// <remarks>
        /// The distinction is NOT cosmetic: colliding with another fighter hurts BOTH —the
        /// pushed one fully and the wall half—, and colliding with the edge or a wall only
        /// the pushed one takes it. Measured in the 401 captures: 9 pairs of two push damage
        /// messages in a row, and all 9 with the second worth exactly half the first.
        /// </remarks>
        public enum PushStop
        {
            /// <summary>It covered the cells it had to. There is no damage.</summary>
            None = 0,

            /// <summary>The grid's edge.</summary>
            Edge,

            /// <summary>A cell not stepped on: wall, hole or off the map's floor.</summary>
            Obstacle,

            /// <summary>Another fighter. The only case in which the damage goes to two.</summary>
            Fighter,

            /// <summary>
            /// A bomb wall, or a trap. It ENTERS the cell and stops there, and there is no collision
            /// damage: the wall and the trap have their own.
            /// </summary>
            /// <remarks>
            /// From the class sheet: "Desplazar una entidad a un muro detendra su desplazamiento y
            /// le infligira danos." The entering-and-stopping part is measured: in
            /// "explobomba-tornabomba-...-explotandolas" frame 8282 pulls -3 from 274 to 260, a
            /// wall cell, and frame 8283 is the wall going off on it AT 260.
            /// </remarks>
            Wall,

            /// <summary>
            /// A portal that is on. It ENTERS the cell and stops there; the fight sends it through
            /// the network and the cells left go on from the way out, the same way
            /// (<see cref="PushResult.BlockedCells"/> are those cells, and do not hurt).
            /// </summary>
            Portal,
        }

        /// <summary>How a push ended.</summary>
        /// <remarks>
        /// What was missing is <see cref="BlockedCells"/>. The collision damage comes from THE CELLS
        /// NOT COVERED, not from the ones covered nor from the ones the spell declares, and the
        /// earlier version returned only the final cell: it threw that number away.
        /// </remarks>
        public readonly struct PushResult
        {
            /// <summary>Where it ended.</summary>
            public int ToCell { get; init; }

            /// <summary>How many cells were left uncovered. Zero if it arrived whole.</summary>
            public int BlockedCells { get; init; }

            /// <summary>What it stopped against.</summary>
            public PushStop Stop { get; init; }

            /// <summary>The cell of whoever acted as the wall, if it was a fighter. Minus one if not.</summary>
            public int BlockerCell { get; init; }

            /// <summary>The way it went, one cell's step: what the cells left after a portal go on along.</summary>
            public int Dx { get; init; }

            /// <summary>See <see cref="Dx"/>.</summary>
            public int Dy { get; init; }
        }

        /// <summary>
        /// Which way a push sends whoever stands at <paramref name="hasta"/>, seen from
        /// <paramref name="desde"/>: along the line when the two share one of the eight, and
        /// otherwise along the walking axis that separates them the most.
        /// </summary>
        /// <remarks>
        /// It is not the nearest of the eight. A target two cells along one axis and one along
        /// the other is closer to the diagonal than to the axis, and the push went diagonally:
        /// Amenaza pulled a Puch two steps that way and left it on the far side of the Yopuka,
        /// while the client's preview drew it in front of him. Over the 469 displacements of the
        /// captures with a known origin, the axis gets 435 right and the nearest of the eight 408;
        /// of the 31 where they part, the axis takes 29 (Feca, Hipermago, Osamodas, Tymador,
        /// Zobal, Zurcarák...).
        /// </remarks>
        public static (int Dx, int Dy)? DisplacementDirection(int desde, int hasta)
        {
            if (!MapGeometry.IsValid(desde) || !MapGeometry.IsValid(hasta) || desde == hasta) return null;
            var (ax, ay) = MapGeometry.CellToPoint(desde);
            var (bx, by) = MapGeometry.CellToPoint(hasta);
            int dx = bx - ax, dy = by - ay;
            if (dx == 0 || dy == 0 || Math.Abs(dx) == Math.Abs(dy)) return (Math.Sign(dx), Math.Sign(dy));
            return Math.Abs(dx) > Math.Abs(dy) ? (Math.Sign(dx), 0) : (0, Math.Sign(dy));
        }

        /// <summary>
        /// Where whoever receives a push ends up (or a pull, with the cells as a negative), and
        /// what it stops against.
        ///
        /// The direction comes from the cell the spell was cast at —its zone's centre—
        /// towards whoever goes flying; if it is the one right on that cell, there is no vector and
        /// then the caster's cell rules. Measured over the 76 displacements of the
        /// Cra captures. Which of the eight ways it takes is <see cref="DisplacementDirection"/>.
        /// </summary>
        /// <param name="judgedFrom">
        /// The cell the way is judged from, when the one moved is no longer where the spell
        /// found him; minus one for where he stands. See FightInstance.CastLanded.
        /// </param>
        /// <param name="portal">Whether a cell holds a portal that takes him: he stops on it (<see cref="PushStop.Portal"/>).</param>
        public static PushResult Push(int centro, int deQuienLanza, int aQuien, int casillas,
                                      HashSet<int> pisables, HashSet<int> ocupadas,
                                      HashSet<int> paran = null, int judgedFrom = -1,
                                      Func<int, bool> portal = null)
        {
            var quieto = new PushResult { ToCell = aQuien, BlockedCells = 0,
                                          Stop = PushStop.None, BlockerCell = -1 };
            if (casillas == 0 || !MapGeometry.IsValid(aQuien)) return quieto;

            int seenAt = MapGeometry.IsValid(judgedFrom) ? judgedFrom : aQuien;
            int origen = (centro != seenAt && MapGeometry.IsValid(centro)) ? centro : deQuienLanza;
            var d = DisplacementDirection(origen, seenAt);
            if (d == null) return quieto;

            int dx = d.Value.Dx, dy = d.Value.Dy;
            if (casillas < 0) { dx = -dx; dy = -dy; }   // pulling is the same the other way round

            return PushAlong(aQuien, dx, dy, Math.Abs(casillas), pisables, ocupadas, paran, portal,
                             pullCentre: casillas < 0 ? centro : -1);
        }

        /// <summary>
        /// A displacement of <paramref name="cells"/> cells from <paramref name="from"/>, one
        /// step of (<paramref name="dx"/>, <paramref name="dy"/>) at a time, and what it stops
        /// against: <see cref="Push"/> once it has its way, and the cells a portal leaves to go on
        /// from its way out.
        /// </summary>
        /// <param name="pullCentre">A pull's centre, which it stops on; minus one for none.</param>
        public static PushResult PushAlong(int from, int dx, int dy, int cells,
                                           HashSet<int> walkable, HashSet<int> occupied,
                                           HashSet<int> walls = null, Func<int, bool> portal = null,
                                           int pullCentre = -1)
        {
            if (cells <= 0 || !MapGeometry.IsValid(from) || (dx == 0 && dy == 0))
                return new PushResult { ToCell = from, Stop = PushStop.None, BlockerCell = -1, Dx = dx, Dy = dy };

            var (x, y) = MapGeometry.CellToPoint(from);
            int donde = from, dadas = 0;
            var freno = PushStop.None;
            int paredEn = -1;

            for (int i = 0; i < cells; i++)
            {
                // A DIAGONAL STEP SQUEEZES BETWEEN TWO CORNERS, and one wall on either side is
                // enough to cut it: the step from (x, y) to (x+dx, y+dy) brushes the cells
                // (x+dx, y) and (x, y+dy), and a block on one of them stops the push short, as an
                // obstacle would. Violencia pushed a Puch straight down the screen between two
                // stone blocks, where the real game stops it against them.
                if (dx != 0 && dy != 0 && walkable != null
                    && (!walkable.Contains(MapGeometry.PointToCell(x + dx, y))
                        || !walkable.Contains(MapGeometry.PointToCell(x, y + dy))))
                {
                    freno = PushStop.Obstacle; break;
                }

                x += dx; y += dy;
                int siguiente = MapGeometry.PointToCell(x, y);

                if (siguiente < 0) { freno = PushStop.Edge; break; }
                if (walkable != null && !walkable.Contains(siguiente))
                {
                    freno = PushStop.Obstacle; break;
                }
                if (occupied != null && occupied.Contains(siguiente))
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
                if (walls != null && walls.Contains(siguiente))
                {
                    freno = PushStop.Wall;
                    break;
                }

                // A PORTAL THAT IS ON takes him, and the push goes on from its way out with what
                // is left. Odisea's step back onto the portal on 215 goes through it right behind
                // the slide (frames 74-79); that the cells beyond the portal are walked from the way
                // out is the class's rule, with no capture of a longer push into one.
                if (portal != null && portal(siguiente))
                {
                    freno = PushStop.Portal;
                    break;
                }

                // A PULL DOES NOT OVERSHOOT. Pulling walks towards the centre, and without this it
                // crossed it and came out the other side: the Rogue's Imantación pulls his bombs
                // six cells, so a bomb two from the point ended up four cells
                // beyond, in the opposite direction. And since the spell pulls TWICE -- once in its
                // own effect 6 and again in the 18652 it chains --, the second brought it
                // back: in the log the dance is seen, bomb -5 from 272 to 185 and from
                // 185 back to 272.
                //
                // It stops ON the centre, not short of it. Stopping a cell before left
                // Congregación's Puch beside the cell it was pulled to -- diagonal to the
                // Yopuka, where the client's preview stood it on that cell. The captures land
                // on it whenever it is free: Cruce (Q1 to Q3, three cells), Colapso, Cencerro,
                // Timón, Cabestrante, Shock, Ovillo, Parafuso. When somebody stands there --
                // the caster, a first one pulled -- the occupied cell above has already
                // stopped it beside them, which is Imantación's 303 to 274 with a bomb on 260.
                if (siguiente == pullCentre)
                {
                    dadas = cells;
                    break;
                }
            }

            return new PushResult
            {
                ToCell = donde,
                BlockedCells = cells - dadas,
                Stop = freno,
                BlockerCell = paredEn,
                Dx = dx,
                Dy = dy,
            };
        }
    }
}

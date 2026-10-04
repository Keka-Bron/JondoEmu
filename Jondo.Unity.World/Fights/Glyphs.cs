using System;
using System.Collections.Generic;

namespace Jondo.Unity.World.Fights
{
    /// <summary>Cuándo se dispara lo que hay puesto en el suelo.</summary>
    public enum Disparo
    {
        /// <summary>Al pisarlo. La trampa, que además se gasta.</summary>
        AlPisar,

        /// <summary>Al empezar el turno encima.</summary>
        AlEmpezarElTurno,

        /// <summary>Las dos cosas: el glifo de aura y la runa.</summary>
        AlPisarYAlEmpezar,

        /// <summary>At the end of the turn of whoever stands on it: the turn-end glyph (402).</summary>
        AlAcabarElTurno,
    }

    /// <summary>
    /// Algo puesto en el suelo que lanza un hechizo cuando alguien lo toca.
    /// </summary>
    /// <remarks>
    /// Un solo tipo para las cuatro familias del catálogo —el glifo de aura (1091, 316 hechizos),
    /// el de inicio de turno (401, 142), la trampa (400, 100) y la runa (2022, 65)— porque
    /// medidas las cuatro tienen EXACTAMENTE la misma forma:
    ///
    ///   diceNum   el hechizo que lanza al dispararse
    ///   diceSide  el grado de ese hechizo
    ///   value     el color, en RGB. El Avispero lleva 16777215, que es blanco puro
    ///   duration  las rondas que dura. El -1 quiere decir que no se cae sola
    ///   zoneDescr la huella: la forma y el radio alrededor de la casilla apuntada
    ///   targetMask a quién le hace efecto
    ///
    /// Lo único que las distingue es CUÁNDO se disparan, y eso cabe en un enum. Hacer cuatro
    /// clases con el mismo cuerpo habría sido copiar tres veces la parte difícil —la huella, la
    /// caducidad, la máscara— para variar la fácil.
    /// </remarks>
    public sealed class Glifo
    {
        public Glifo(long dueno, IReadOnlyCollection<int> casillas, int hechizo, int grado,
                     int color, int caducaEnRonda, string mascara, Disparo cuando)
        {
            Dueno = dueno;
            Casillas = new HashSet<int>(casillas);
            Hechizo = hechizo;
            Grado = grado;
            Color = color;
            CaducaEnRonda = caducaEnRonda;
            Mascara = mascara ?? "";
            Cuando = cuando;
        }

        /// <summary>The cell it was aimed at: the f10 of its jwe 401. Minus one when unknown.</summary>
        public int Centro { get; set; } = -1;

        /// <summary>Quién lo puso. El daño que haga es suyo.</summary>
        public long Dueno { get; }

        /// <summary>El identificador que ve el cliente. Lo reparte el combate.</summary>
        public int Id { get; set; }

        public HashSet<int> Casillas { get; }
        public int Hechizo { get; }
        public int Grado { get; }
        public int Color { get; }

        /// <summary>La ronda en la que se cae. Cero: no se cae sola.</summary>
        public int CaducaEnRonda { get; }

        public string Mascara { get; }
        public Disparo Cuando { get; }

        /// <summary>Si ya se gastó. Las trampas se gastan al primer pisotón.</summary>
        public bool Gastado { get; set; }

        /// <summary>¿Se dispara con esto?</summary>
        public bool SeDisparaAlPisar
            => !Gastado && (Cuando == Disparo.AlPisar || Cuando == Disparo.AlPisarYAlEmpezar);

        public bool SeDisparaAlEmpezarElTurno
            => !Gastado && (Cuando == Disparo.AlEmpezarElTurno || Cuando == Disparo.AlPisarYAlEmpezar);

        public bool SeDisparaAlAcabarElTurno => !Gastado && Cuando == Disparo.AlAcabarElTurno;

        /// <summary>The effect that laid it -- 400 trap, 401/402 glyph, 1091 aura, 1165 glyph, 2022 rune -- for 1026 and 2023.</summary>
        public int Tipo { get; set; }

        /// <summary>The fighters inside a monster's aura glyph: what it gave them goes when they leave.</summary>
        public HashSet<long> Dentro { get; } = new HashSet<long>();

        /// <summary>The spell that laid it, which a 2018 "Disipa los glifos" names in its die.</summary>
        public int HechizoQueLoPuso { get; set; }

        /// <summary>La trampa se gasta; el glifo se queda hasta que caduque.</summary>
        public bool SeGastaAlDispararse => Cuando == Disparo.AlPisar;

        public bool Cubre(int casilla) => Casillas.Contains(casilla);

        public override string ToString()
            => $"glifo {Id} de {Dueno}: hechizo {Hechizo} grado {Grado} en {Casillas.Count} casilla(s)";
    }
}

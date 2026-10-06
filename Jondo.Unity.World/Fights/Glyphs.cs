using System;
using System.Collections.Generic;

namespace Jondo.Unity.World.Fights
{
    /// <summary>When what is laid on the ground fires.</summary>
    public enum Disparo
    {
        /// <summary>On stepping on it. The trap, which is also spent.</summary>
        AlPisar,

        /// <summary>On starting the turn on top of it.</summary>
        AlEmpezarElTurno,

        /// <summary>Both things: the aura glyph and the rune.</summary>
        AlPisarYAlEmpezar,

        /// <summary>At the end of the turn of whoever stands on it: the turn-end glyph (402).</summary>
        AlAcabarElTurno,
    }

    /// <summary>
    /// Something laid on the ground that casts a spell when someone touches it.
    /// </summary>
    /// <remarks>
    /// A single type for the catalogue's four families —the aura glyph (1091, 316 spells),
    /// the turn-start one (401, 142), the trap (400, 100) and the rune (2022, 65)— because
    /// measured, all four have EXACTLY the same shape:
    ///
    ///   diceNum   the spell it casts on firing
    ///   diceSide  that spell's grade
    ///   value     the colour, in RGB. The Avispero carries 16777215, which is pure white
    ///   duration  the rounds it lasts. -1 means it does not drop on its own
    ///   zoneDescr the footprint: the shape and the radius around the targeted cell
    ///   targetMask whom it affects
    ///
    /// The only thing telling them apart is WHEN they fire, and that fits in an enum. Making four
    /// classes with the same body would have been copying the hard part three times —the footprint, the
    /// expiry, the mask— to vary the easy one.
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

        /// <summary>Who placed it. The damage it does is his.</summary>
        public long Dueno { get; }

        /// <summary>The identifier the client sees. The fight hands it out.</summary>
        public int Id { get; set; }

        public HashSet<int> Casillas { get; }
        public int Hechizo { get; }
        public int Grado { get; }
        public int Color { get; }

        /// <summary>The round in which it drops. Zero: it does not drop on its own.</summary>
        public int CaducaEnRonda { get; }

        public string Mascara { get; }
        public Disparo Cuando { get; }

        /// <summary>Whether it has already been spent. Traps are spent on the first step.</summary>
        public bool Gastado { get; set; }

        /// <summary>Does it fire with this?</summary>
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

        /// <summary>The trap is spent; the glyph stays until it expires.</summary>
        public bool SeGastaAlDispararse => Cuando == Disparo.AlPisar;

        public bool Cubre(int casilla) => Casillas.Contains(casilla);

        public override string ToString()
            => $"glifo {Id} de {Dueno}: hechizo {Hechizo} grado {Grado} en {Casillas.Count} casilla(s)";
    }
}

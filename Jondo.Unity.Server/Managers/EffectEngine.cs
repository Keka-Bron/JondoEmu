using Jondo.Unity.World.Combat;
using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Lo que hay que hacer con un efecto ya resuelto: aplicarlo y contárselo al cliente.
    /// El motor decide QUÉ pasa; quien lo llama decide cómo se manda por el cable.
    /// </summary>
    public sealed class Outcome
    {
        public Fighter Sobre { get; init; } = null!;
        /// <summary>The concrete caster after following a hidden spell chain.</summary>
        public Fighter Caster { get; set; }

        /// <summary>
        /// A sub-cast's child that waits on a trigger, and on whom: nothing to apply or announce,
        /// only a hook to put -- see FightHandler.EngancharLoPendiente.
        /// </summary>
        public bool EnganchePendiente { get; init; }

        /// <summary>One row of a monster spell armed on <see cref="Sobre"/>, the row in <see cref="Efecto"/>.</summary>
        public bool FilaArmada { get; init; }
        /// <summary>
        /// The fighter from whose cell a hidden chained spell is visually launched. Damage still
        /// belongs to <see cref="Caster"/>; a nearest-target rebound merely travels from its
        /// previous victim to the next one on the client.
        /// </summary>
        public Fighter AnimationCaster { get; init; }
        public SpellEffect Efecto { get; init; } = null!;
        public Buff Buff { get; init; }
        public int HechizoOrigen { get; init; }
        public int NivelOrigen { get; init; }

        /// <summary>The caster's turn ends once this cast has gone out (effect 1031).</summary>
        public bool AcabaElTurno { get; init; }

        /// <summary>
        /// Whether this row comes out of a spell's CRITICAL list: the jxm carries it as its f9.
        /// A critical cast runs the whole chain on the critical lists, and a spell with none
        /// runs its ordinary one unflagged.
        /// </summary>
        public bool Critico { get; set; }

        /// <summary>The caster picked <see cref="Sobre"/> up (effect 50): it rides on him from now on.</summary>
        public bool Carga { get; init; }

        /// <summary>The caster threw <see cref="Sobre"/> (effect 51) to <see cref="CasillaHasta"/>.</summary>
        public bool Lanza { get; init; }

        /// <summary>
        /// The copies Tymadura left (effect 1097), already on the board. The caster himself went
        /// from <see cref="CasillaDesde"/> to <see cref="CasillaHasta"/>.
        /// </summary>
        public List<Fighter> Ilusiones { get; init; }

        /// <summary>Si el efecto cambia una característica en el acto, cuál y en cuánto.</summary>
        public int Caracteristica { get; init; }
        public int Cuanto { get; init; }

        /// <summary>Si el efecto encadena otro hechizo, cuál y en qué grado.</summary>
        public int HechizoEncadenado { get; init; }
        public int GradoEncadenado { get; init; }

        /// <summary>
        /// A concrete damage row selected inside a chained spell. Root-cast damage still follows
        /// the ordinary damage pipeline; this marker preserves the target and spell-bonus
        /// snapshot of a hidden 792/1160/2160 execution.
        /// </summary>
        public bool NestedDamage { get; init; }
        public int DamageElement { get; init; }
        public int DamageDistance { get; init; }
        public int DamageSpellBonus { get; init; }
        public bool CriticalDamage { get; init; }

        /// <summary>
        /// Si el efecto mueve a alguien, de dónde a dónde. Menos uno cuando no mueve a nadie.
        /// </summary>
        public int CasillaDesde { get; init; } = -1;
        public int CasillaHasta { get; init; } = -1;
        public bool Mueve => CasillaHasta >= 0 && CasillaHasta != CasillaDesde;

        /// <summary>
        /// Verdadero cuando el motor no sabe todavía qué hace este efecto pero SÍ sabe que dura y
        /// que el cliente lo pinta. Se manda igual al panel y se anota en el registro, en vez de
        /// tirarlo en silencio, que es lo que se hacía antes con catorce familias enteras.
        /// </summary>
        public bool SoloParaElPanel { get; init; }

        /// <summary>La plantilla de bicho que hay que sacar al tablero, si el efecto invoca.</summary>
        public int Invoca { get; init; }

        /// <summary>
        /// Where the summon goes: the cell of the resolution that summoned it -- a fixed cell of a
        /// ';' zone, a chained spell's own cell -- and not always the cast's. Minus one: the cast's.
        /// </summary>
        public int CasillaDeLaInvocacion { get; init; } = -1;

        /// <summary>A spell's cooldown on the bearer changed (1036, 1045): his client is told.</summary>
        public bool CambiaLaRecarga { get; init; }

        /// <summary>2023 or 1026: the caster's runes or glyphs go off now.</summary>
        public int ActivaSuelo { get; init; }

        /// <summary>The life, in percent, the last ally to fall comes back with (780, 1034); zero for any other outcome.</summary>
        public int Revive { get; init; }

        /// <summary>The glyphs a 2018 took off the board, for the client to be told.</summary>
        public List<Jondo.Unity.World.Fights.Glifo> GlifosQuitados { get; init; }

        /// <summary>El efecto 141: mata al objetivo, sin cálculo de por medio.</summary>
        public bool Fulmina { get; init; }

        /// <summary>The double a 180 has just put on the board, for the fight to announce.</summary>
        public Fighter Doble { get; init; }

        /// <summary>
        /// The cell the spell that produced this outcome was aimed at, when it was resolved: what a
        /// chained cast's jwe 300 names, though a row of the same cast has moved its target since
        /// -- Estela's sub-cast goes out on 371, where the Selatrop stood, not on the 427 he jumped
        /// to (frame 10). Minus one when not known.
        /// </summary>
        public int CeldaDelLanzamiento { get; set; } = -1;

        /// <summary>
        /// A portal to lay on this cell (1181), with the placing row in <see cref="Efecto"/>: the
        /// fight lays it, since what it does to the network -- which portal goes, which turns on
        /// -- is told frame by frame. Minus one for any other outcome.
        /// </summary>
        public int PortalAt { get; init; } = -1;

        /// <summary>
        /// Who stood on <see cref="PortalAt"/> when the portal was laid: a row of the same cast
        /// may have moved him before the fight tells it (Estela's caster jumps away), and the
        /// portal goes down off while he is there.
        /// </summary>
        public long PortalOccupant { get; init; }

        /// <summary>The cells whose portals a 1183 switches off, until the caster's next turn.</summary>
        public List<int> PortalsOffAt { get; init; }

        /// <summary><see cref="Sobre"/> goes through the portal he stands on (1182, "Teleportal").</summary>
        public bool Teleportal { get; init; }

        /// <summary>
        /// <see cref="Sobre"/> walks after <see cref="Caster"/> (2184), up to this many cells;
        /// zero for any other outcome.
        /// </summary>
        public int Follows { get; init; }

        /// <summary>
        /// The 3793 script marker going off now: it travels as an action (jwe 3793) with the
        /// spell, its grade, its value and the cell of <see cref="Sobre"/>, and leaves no row.
        /// </summary>
        public bool Marcador { get; init; }

        /// <summary>
        /// Points of a removal the target dodged: they go out as a jwe 308 (AP) or 309 (MP)
        /// before anything else of the effect. Zero when nothing was dodged.
        /// </summary>
        public int PuntosEsquivados { get; init; }

        /// <summary>
        /// The effect the row is announced as when it is not the catalogue's own: a landed
        /// "retira PA" travels as a "-N PA" (168) with N the points really lost, in the 74
        /// removal rows of the captures. Zero to announce the effect as it is.
        /// </summary>
        public int EfectoEnElCable { get; init; }

        /// <summary>
        /// A row registered for later -- a "-N de daños recibidos" that waits for a blow of its
        /// kind. It travels with its own trigger, hidden, and with the value in the dice slot
        /// when the effect rolls none: "jxm 265 f1=23 f10=23 'DR' f15=7" on the bomb of the
        /// Remisión capture.
        /// </summary>
        public bool FilaEnganchada { get; init; }

        /// <summary>Los puntos de escudo que este efecto ha puesto. Cero cuando no pone ninguno.</summary>
        public int Escudo { get; init; }

        /// <summary>Vida que se va sin ser un golpe: el «-N% PdV».</summary>
        public int VidaQueSeVa { get; init; }

        /// <summary>Vida que el lanzador le pasa al objetivo.</summary>
        public int VidaTransferida { get; init; }

        /// <summary>Cuántos embrujos se ha llevado por delante un efecto que acorta duraciones.</summary>
        public int EmbrujosCaidos { get; init; }

        /// <summary>Que la invocación salga donde estaba el que acaba de morir, y no al lado.</summary>
        public bool EnLaCasillaDelMuerto { get; init; }

        /// <summary>Lo que este efecto ha dejado puesto en el suelo, si ha dejado algo.</summary>
        public Jondo.Unity.World.Fights.Glifo Glifo { get; init; }

        /// <summary>
        /// El SEGUNDO desplazamiento, cuando el efecto mueve a dos. Menos uno cuando no.
        /// </summary>
        /// <remarks>
        /// Lo pide el intercambio de posiciones, que es el único que mueve al lanzador y al
        /// objetivo a la vez. Anunciar sólo uno de los dos deja al cliente con alguien pintado
        /// donde ya no está.
        /// </remarks>
        public Fighter Tambien { get; init; }
        public int CasillaDesdeDelOtro { get; init; } = -1;
        public int CasillaHastaDelOtro { get; init; } = -1;
        public bool MueveTambien => Tambien != null && CasillaHastaDelOtro >= 0
                                    && CasillaHastaDelOtro != CasillaDesdeDelOtro;

        /// <summary>Los puntos de vida que se han devuelto, si el efecto cura.</summary>
        public int Cura { get; init; }

        /// <summary>
        /// El daño de haberse chocado al empujar, YA CALCULADO PERO SIN APLICAR.
        ///
        /// Quitar vida es del que lleva el combate: es quien recorta por la vida que queda,
        /// erosiona, anuncia la muerte y juzga los retos. Aquí sólo se dice cuánto.
        /// </summary>
        public int CollisionDamage { get; init; }

        /// <summary>El que hizo de pared, si lo que frenó el empujón fue otro combatiente.</summary>
        public Fighter Blocker { get; init; }

        /// <summary>
        /// Lo que cobra la pared: la MITAD del daño del empujado, redondeando hacia abajo.
        ///
        /// Y es la mitad de ese daño, no una cuenta nueva con las características de la pared:
        /// medido en el koliseo, la pareja 497/248 sale con la resistencia al empuje de la VÍCTIMA
        /// metida en el 497. Recalculándolo con lo del bloqueador los números no cuadran.
        /// </summary>
        public int CollisionDamageToBlocker { get; init; }

        /// <summary>Embrujos retirados par un effet 406 ou par le retrait d'un état.</summary>
        public IReadOnlyList<Buff> BuffsQuitados { get; init; } = Array.Empty<Buff>();

        /// <summary>
        /// The rows the new row REPLACED on the bearer -- the old copy of a spell that does not
        /// stack, the oldest of one that stacks to a cap. Announced gone, jya and jwe 514, before
        /// the new row, as the real server does when Espada del Juicio is cast again.
        /// </summary>
        public IReadOnlyList<Buff> Relevados { get; set; } = Array.Empty<Buff>();

        /// <summary>Apariencia temporal solicitada por el efecto 335, o cero.</summary>
        public int Apariencia { get; init; }
    }

    /// <summary>A delayed one-shot heal that has just reached its activation round.</summary>
    internal sealed class DelayedHealOutcome
    {
        public Fighter Target { get; init; } = null!;
        public long CasterId { get; init; }
        public Buff Buff { get; init; } = null!;
        public int Healed { get; init; }
    }

    /// <summary>
    /// A waiting row whose round has come: what it was, on whom, and the live row it turned
    /// into when it is one that lasts. A kill and a marker turn into nothing: the death and the
    /// jwe 3793 are the whole of them.
    /// </summary>
    internal sealed class PendingActivation
    {
        public Fighter Target { get; init; } = null!;
        public long CasterId { get; init; }
        public Buff Waiting { get; init; } = null!;
        public Buff Live { get; init; }
        public bool Kills => Waiting.EffectId == EffectEngine.MataAlObjetivo;
        public bool Marks => Waiting.EffectId == EffectEngine.MarcadorDeGuion;

        /// <summary>The rows a waiting "remove state" took off when its round came.</summary>
        public List<Buff> Quitados { get; init; }

        /// <summary>A waiting sub-cast: its round has come and the child is cast now.</summary>
        public bool Casts => EffectEngine.EsDeLaFamiliaDeSublanzar(Waiting.EffectId);
    }

    /// <summary>
    /// El motor de efectos: coge las entradas del EffectsJson de un hechizo y las convierte en
    /// cosas que le pasan a alguien.
    ///
    /// No hay ni un hechizo escrito a mano aquí dentro. Todo sale de dos sitios de la base:
    ///
    ///   - <c>SpellLevels.EffectsJson</c>, que dice qué efectos tiene el hechizo, con cuánto, a
    ///     quién (<c>targetMask</c>), cuándo (<c>triggers</c>) y por cuántos turnos.
    ///   - La tabla <c>Effects</c> del cliente, que dice qué característica toca cada número de
    ///     efecto y con qué signo (<c>BonusType</c>).
    ///
    /// Con eso salen solas cosas como éstas, que antes había que escribir una por una:
    ///
    ///   Flecha Helada  = 1079 (quita 2 PA) + 96 (21-24 de agua) + 293 (+8 de daños básicos de
    ///                    Flecha Helada, tres turnos, sobre uno mismo)
    ///   Disparos Lejanos = 280 y 281 repetidos (+3 de alcance mínimo y +6 de máximo) sobre una
    ///                    lista larga de hechizos, un turno
    ///   Dofus Ocre     = el objeto regala el "hechizo" 8394 por su efecto 1175; ese hechizo, en su
    ///                    grado 1, dice "cuando me peguen lanza mi grado 2" y "al empezar el turno
    ///                    lanza mi grado 3"; el grado 2 pone el estado 519 y el 3 da +1 PA si NO se
    ///                    tiene ese estado, +20 de huida si sí, y lo quita al acabar el turno.
    /// </summary>
    public static class EffectEngine
    {
        // Los números de efecto que el motor entiende de forma especial. El resto se resuelve por
        // su característica en el catálogo.
        //
        // Los que pegan son DIEZ, no cinco: del 91 al 95 son los de ROBO DE VIDA y del 96 al 100
        // los de daño a secas, uno por elemento cada tanda. El emulador sólo miraba del 96 al 100,
        // así que hechizos como Flecha Voraz —que pega con el 94, robo de fuego— o el Ojo de Topo
        // —el 91, robo de agua— no encajaban en ningún sitio y el daño salía de donde no debía.
        //
        // Y el elemento no hay que deducirlo del número: lo dice el catálogo en su columna
        // ElementId, con 0 neutral, 1 tierra, 2 fuego, 3 agua y 4 aire.
        private const int DanoPrimero = EffectSupport.FirstDamage;
        private const int DanoUltimo = EffectSupport.LastDamage;

        /// <summary>
        /// Los que pegan en función de lo que el objetivo lleve EROSIONADO. Son cinco, uno por
        /// elemento, en dos tandas: la del 1092 al 1096 y la del 1118 al 1122. En su descripción
        /// el dado no es el daño sino el tanto por ciento.
        /// </summary>
        private static readonly HashSet<int> PorLoErosionado
            = new HashSet<int> { 1092, 1093, 1094, 1095, 1096, 1118, 1119, 1120, 1121, 1122 };

        public static bool PegaSegunLoErosionado(int efecto) => PorLoErosionado.Contains(efecto);

        /// <summary>
        /// The ones that hit out of the CASTER's life: 275 to 279 a percentage of the life he is
        /// missing -- "PdV faltantes del lanzador", Conde Kontatrás's Contratiempo among many
        /// monsters -- and 85 to 89 of the life he has. The die is the percentage, and the
        /// caster's characteristics do not grow it: only the target's resistances bring it down.
        /// </summary>
        private static readonly HashSet<int> PorLaVidaQueLeFalta = new HashSet<int> { 275, 276, 277, 278, 279 };
        private static readonly HashSet<int> PorSuVida = new HashSet<int> { 85, 86, 87, 88, 89 };

        public static bool PegaSegunLaVidaDelLanzador(int efecto)
            => PorLaVidaQueLeFalta.Contains(efecto) || PorSuVida.Contains(efecto);

        /// <summary>
        /// How a blow gets its number when it is not the ordinary die grown by the caster's
        /// characteristics. None of these is grown by them: only the target's resistances apply.
        /// </summary>
        public enum ModoDeDano { Normal, VidaDelLanzador, Fijo, VidaDelObjetivo, DanoSufrido, PorPuntos }

        private static readonly HashSet<int> Fijos = new HashSet<int> { 1063, 1064, 1065, 1066 };
        private static readonly HashSet<int> PorLaVidaDelObjetivo = new HashSet<int> { 1067, 1068, 1069, 1070, 1071 };
        private static readonly HashSet<int> PorElDanoSufrido = new HashSet<int> { 1123, 1124, 1125, 1126, 1127, 1128 };

        /// <summary>
        /// "Daños: #1% de los daños FINALES sufridos" (1223) and its five elements (1224 to 1228):
        /// the blow the bearer took, after everything, returned in part. Masacre, Dispersión,
        /// Corona de Espinas, Soberbia. Measured: the Xelor's 92 of air on his cómplice comes
        /// back 75% as "jwe 1225 69" on the enemy next to it; the Yopuka's 64 of water on the
        /// Masacre target 30% as "jwe 1227 19".
        /// </summary>
        private static readonly HashSet<int> PorElDanoFinalSufrido = new HashSet<int> { 1223, 1224, 1225, 1226, 1227, 1228 };

        /// <summary>
        /// The one of the family in an element, for the first of it that names none (1123, 1223):
        /// neutral, air, fire, water, earth follow it in that order -- 1225 is air, 1227 water.
        /// </summary>
        internal static int DelElemento(int efecto, int elemento)
        {
            if (efecto != 1123 && efecto != 1223) return efecto;
            int paso = elemento switch { 4 => 2, 2 => 3, 3 => 4, 1 => 5, _ => 1 };
            return efecto + paso;
        }

        /// <summary>Whether a blow is a share of the blow that set it off.</summary>
        internal static bool DevuelveElGolpe(int efecto)
            => PorElDanoSufrido.Contains(efecto) || PorElDanoFinalSufrido.Contains(efecto);
        private static readonly HashSet<int> PorPAUsado = new HashSet<int> { 1131, 1132, 1133, 1134, 1135 };
        private static readonly HashSet<int> PorPMUsado = new HashSet<int> { 1136, 1137, 1138, 1139, 1140 };

        public static ModoDeDano ModoDe(int efecto)
            => PegaSegunLaVidaDelLanzador(efecto) ? ModoDeDano.VidaDelLanzador
             : Fijos.Contains(efecto) ? ModoDeDano.Fijo
             : PorLaVidaDelObjetivo.Contains(efecto) ? ModoDeDano.VidaDelObjetivo
             : DevuelveElGolpe(efecto) ? ModoDeDano.DanoSufrido
             : PorPAUsado.Contains(efecto) || PorPMUsado.Contains(efecto) ? ModoDeDano.PorPuntos
             : ModoDeDano.Normal;

        /// <summary>
        /// The number such a blow starts from: "1063 a 1066 (fijo)" the die itself; "1067 a 1071
        /// % PdV del objetivo" of the target's life; "1123 a 1128 % de los daños iniciales
        /// sufridos" of the blow that set it off; "1131 a 1140, #2 por #1 PA/PM utilizado" of the
        /// points the target has spent this turn; the caster's life for 275-279 and 85-89.
        /// </summary>
        public static int BaseDelModo(SpellEffect efecto, int dado, Fighter lanzador, Fighter objetivo, FightInstance combate)
        {
            switch (ModoDe(efecto.EffectId))
            {
                case ModoDeDano.VidaDelLanzador: return VidaDeLaQueSale(efecto.EffectId, lanzador) * dado / 100;
                case ModoDeDano.Fijo: return dado;
                case ModoDeDano.VidaDelObjetivo: return Math.Max(0, objetivo.CurrentHP) * dado / 100;
                case ModoDeDano.DanoSufrido: return Math.Max(0, combate.DanoDelDisparo) * dado / 100;
                case ModoDeDano.PorPuntos:
                    int usados = PorPAUsado.Contains(efecto.EffectId)
                        ? Math.Max(0, objetivo.MaxAP - objetivo.CurrentAP)
                        : Math.Max(0, objetivo.MaxMP - objetivo.CurrentMP);
                    return Math.Max(0, efecto.DiceSide) * (usados / Math.Max(1, efecto.DiceNum));
                default: return dado;
            }
        }

        /// <summary>The life the percentage of a 275-279 or 85-89 is taken from.</summary>
        public static int VidaDeLaQueSale(int efecto, Fighter lanzador)
            => PorLaVidaQueLeFalta.Contains(efecto)
                ? Math.Max(0, lanzador.MaxHP - lanzador.CurrentHP)
                : Math.Max(0, lanzador.CurrentHP);

        /// <summary>¿Este efecto pega?</summary>
        public static bool EsDeDano(int efecto)
            => (efecto >= DanoPrimero && efecto <= DanoUltimo)
               || efecto == DanoDelMejorElemento
               || efecto == DanoDelPeorElemento
               || PegaSegunLoErosionado(efecto)
               || PorLosPMRestantes.Contains(efecto)
               || ModoDe(efecto) != ModoDeDano.Normal;

        /// <summary>
        /// "#1 a #2 de daños de aire (% PM restantes)" and its four brothers, 1012 to 1016: an
        /// ordinary blow, grown by the caster's characteristics, whose die is scaled by the share
        /// of his turn's movement points he still holds. Cénit's capture holds both ends: cast with
        /// the Yopuka's three MP untouched, its 1013 (52-58) hits 108 next to the 56 of its
        /// 27-29; cast with none left, "jwe 1013" goes out with no amount. The share in between
        /// is the reading of the words "% PM restantes", not a measure.
        /// </summary>
        private static readonly HashSet<int> PorLosPMRestantes = new HashSet<int> { 1012, 1013, 1014, 1015, 1016 };

        /// <summary>The die of a "% PM restantes" blow, scaled by the MP the caster has left of his turn's.</summary>
        public static int ConLosPMRestantes(SpellEffect efecto, int dado, Fighter lanzador, int ronda)
        {
            if (efecto == null || lanzador == null || !PorLosPMRestantes.Contains(efecto.EffectId)) return dado;
            int deSuTurno = Math.Max(1, lanzador.MaxMP + lanzador.Buffs.De(PuntosDeMovimiento, ronda));
            int quedan = Math.Clamp(lanzador.CurrentMP, 0, deSuTurno);
            return dado * quedan / deSuTurno;
        }

        /// <summary>
        /// 782, "Maximiza los efectos aleatorios en el objetivo", and 781, "Minimiza los efectos
        /// aleatorios del objetivo": rows on a fighter that turn a die rolled for a blow or a heal
        /// on him to its top (782), or a die he rolls himself to its bottom (781). The Zurcarák's
        /// El Diablo "maximiza los efectos aleatorios en todo el mundo" on "a,A"; Mala Sombra
        /// "minimiza los efectos aleatorios del enemigo objetivo". Where both meet on one die the
        /// caster's minimisation is read first -- no capture has the two together.
        /// </summary>
        public const int MaximizaLosAzares = 782;
        public const int MinimizaLosAzares = 781;

        /// <summary>A die with the 781 of its roller and the 782 of its target applied.</summary>
        public static int ConLosAzares(SpellEffect efecto, Fighter lanzador, Fighter objetivo, int ronda, int sacado)
        {
            if (efecto == null) return sacado;
            int minimo = efecto.DiceNum, maximo = Math.Max(efecto.DiceNum, efecto.DiceSide);
            if (maximo <= minimo) return sacado;
            if (lanzador != null && lanzador.Buffs.Puestos.Any(b => b.EffectId == MinimizaLosAzares && b.Vivo(ronda))) return minimo;
            if (objetivo != null && objetivo.Buffs.Puestos.Any(b => b.EffectId == MaximizaLosAzares && b.Vivo(ronda))) return maximo;
            return sacado;
        }

        /// <summary>
        /// Los golpes que da un hechizo: uno por cada efecto de daño que le toque al objetivo.
        ///
        /// Se resuelve con las mismas máscaras que el resto —de ahí que Flecha Voraz pegue 11-13 o
        /// 34-38 según el estado que lleve el objetivo— y se devuelve el elemento ya resuelto.
        /// Si el hechizo no tiene ni un efecto de daño, no devuelve nada: Tiro de Repliegue sólo
        /// aparta al que lanza y no debe quitarle un solo punto de vida a nadie.
        /// </summary>
        /// <param name="efectos">
        /// The cast's own draw of the rows (<see cref="EfectosSorteados"/>), so that the blows
        /// and the rows of one cast agree on what the dice said. Without it, drawn here.
        /// </param>
        public static List<(SpellEffect Efecto, int Elemento, Fighter Sobre, int Lejos)> Golpes(
            FightInstance combate, Fighter quienLanza, int hechizo, int grado, Fighter objetivo,
            int celdaApuntada = -1, bool critico = false, IReadOnlyList<SpellEffect> efectos = null,
            string disparador = AlLanzar, bool soloAlObjetivo = false)
        {
            var fuera = new List<(SpellEffect, int, Fighter, int)>();
            bool delMonstruo = !PlayerSpells.Contains(hechizo);
            foreach (var efecto in efectos ?? EfectosSorteados(hechizo, grado, critico))
            {
                if (!EsDeDano(efecto.EffectId)) continue;

                // A poison hits when its trigger comes, not at the cast: 285 monster attack spells
                // carry damage under TB or TE, and so do 37 rows of class spells -- Arsénico's
                // "98 under TB": in sram-arsenico.pcapng the cast only hooks the row, and the 27
                // air damage go out at the start of each target's turn (frames of -2, -3, -4).
                if (!efecto.Disparadores().Any(d => string.Equals(d, disparador, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // El elemento lo dice el propio hechizo en su effectElement; si no lo trae, el
                // catálogo por el número de efecto.
                int elemento = efecto.Element >= 0 ? efecto.Element
                                                   : DatabaseManager.EffectElement(efecto.EffectId);

                // El «mejor elemento» no es un elemento: es una pregunta al lanzador. Se resuelve
                // aquí, con los embrujos puestos, porque un hechizo que te suba la agilidad a
                // mitad de combate puede cambiar cuál es tu mejor elemento, y eso es justamente
                // para lo que se lanza.
                if (efecto.EffectId == DanoDelMejorElemento || elemento == ElementoMejor)
                {
                    elemento = MejorElementoDe(quienLanza, combate.RoundNumber);
                }
                else if (efecto.EffectId == DanoDelPeorElemento)
                {
                    elemento = PeorElementoDe(quienLanza, combate.RoundNumber);
                }

                // A share of the blow that set it off, in that blow's element (see ResolveEffects).
                var fila = efecto;
                if (DevuelveElGolpe(efecto.EffectId))
                {
                    if (elemento < 0) elemento = combate.ElementoDelDisparo;
                    int suyo = DelElemento(efecto.EffectId, elemento);
                    if (suyo != efecto.EffectId) fila = efecto.ComoEfecto(suyo);
                }
                foreach (var sobre in AQuien(combate, quienLanza, objetivo, efecto, celdaApuntada,
                                             soloAlObjetivo: soloAlObjetivo))
                {
                    if (sobre == null || !sobre.IsAlive) continue;

                    // A cuántas casillas del centro de la zona está. El daño baja según se aleja,
                    // y cuánto lo dice el propio hechizo.
                    int lejos = celdaApuntada >= 0
                        ? Jondo.Unity.World.Maps.MapGeometry.Distance(celdaApuntada, sobre.CellId)
                        : 0;
                    fuera.Add((fila, elemento, sobre, lejos));
                }
            }
            return fuera;
        }

        /// <summary>
        /// El daño que le queda a uno que está a <paramref name="lejos"/> casillas del centro.
        ///
        /// Se pierde un tanto por ciento por casilla, con un tope de pasos, y los dos números los
        /// trae el hechizo en su <c>zoneDescr</c>: los del Ocra que caen lo hacen al diez por
        /// ciento con tope de cuatro, o sea que del quinto anillo en adelante ya no baja más.
        ///
        /// La tirada del dado es UNA para todo el lanzamiento: si de "25 a 30" sale 26, en el
        /// centro entran 26 y a una casilla, el 90% de eso.
        /// </summary>
        public static int ConLaCaidaDeLaZona(int dano, SpellEffect efecto, int lejos)
        {
            if (dano <= 0 || lejos <= 0 || efecto.PasoDeCaida <= 0) return dano;

            int pasos = efecto.TopeDeCaida > 0 ? Math.Min(lejos, efecto.TopeDeCaida) : lejos;
            double queda = Math.Pow(1.0 - efecto.PasoDeCaida / 100.0, pasos);
            return Math.Max(0, (int)Math.Round(dano * queda));
        }
        private const int Empujar = EffectSupport.Push;

        /// <summary>La 84, «Empuje»: la suma PLANA del que empuja. El porcentaje es la 158.</summary>
        private const int DanoDeEmpuje = 84;

        /// <summary>La 85, «Empuje (fijo)»: la resta PLANA del que lo recibe.</summary>
        private const int ResistenciaAlEmpuje = 85;

        /// <summary>El estado que clava a uno en el sitio. No confundir con la característica 97.</summary>
        private const int Indesplazable = 97;

        /// <summary>
        /// El término fijo de la fórmula del daño de colisión.
        ///
        /// No sale de ningún dato del cliente —ni el bundle de constantes ni las 38 fórmulas lua
        /// tienen nada de combate—: sale de medir. Con un lanzador de nivel 200 sin bonos, el
        /// paréntesis vale 132 y el daño por casilla 33.
        /// </summary>
        private const int BaseDelEmpuje = 32;

        /// <summary>
        /// El daño de estamparse al recibir un empujón.
        ///
        ///   daño = casillasSinRecorrer × (nivel/2 + la 84 del que empuja
        ///                                 − la 85 del que lo recibe + 32) / 4
        ///
        /// Está en un método aparte para que la guardia de regresión pueda comprobarla contra las
        /// muestras que la midieron, que están en AssertPushDamageMatchesTheCapture.
        ///
        /// La división por cuatro va AL FINAL, sobre el producto: con la resistencia dentro del
        /// paréntesis y una sola división, el koliseo da 331 por dos casillas, que es lo medido.
        /// Restando fuera saldrían 316.
        /// </summary>
        public static int DanoDeColision(int nivelDelQueEmpuja, int suEmpuje, int laResistencia,
                                         int casillasSinRecorrer)
        {
            if (casillasSinRecorrer <= 0) return 0;

            int porCasilla = nivelDelQueEmpuja / 2 + suEmpuje - laResistencia + BaseDelEmpuje;
            return Math.Max(0, casillasSinRecorrer * porCasilla / 4);
        }
        /// <summary>«Teletransporta a la casilla objetivo». 425 hechizos lo llevan.</summary>
        /// <remarks>
        /// No es un empujón de muchas casillas: no recorre el camino, así que ni choca ni hace
        /// daño de colisión, y no le importa que haya algo en medio. Sólo le importa la casilla
        /// de destino, que tiene que estar libre y pisable.
        /// </remarks>
        /// <summary>«N% del nivel en escudo» (234 hechizos) y «N% de PdV en el escudo» (167).</summary>
        /// <remarks>
        /// El tanto por ciento va en el DADO, no en el valor: Caparazón lleva diceNum 150 y
        /// Soldagüino 200 sobre el nivel; Bendición Maravillosa lleva 10 y Coraza de Dopeul 20
        /// sobre la vida. El value va a cero en los seis que se han leído.
        ///
        /// El escudo no es vida y no se cura: por eso vive en su propio saco del luchador y no
        /// en CurrentHP.
        /// </remarks>
        /// <summary>«Devuelve de N PA» (163 hechizos). El número va en el dado, la máscara es «C».</summary>
        /// <remarks>
        /// Lo llevan Doom y Matanza, que cuestan 1 PA y lo devuelven, así que se pueden encadenar.
        /// No es un boost de PA con duración: es una devolución inmediata, y por eso no pasa por
        /// el embrujo.
        /// </remarks>
        /// <summary>
        /// Lo que se pone en el suelo: glifo de aura, glifo de inicio de turno, trampa y runa.
        /// </summary>
        /// <remarks>
        /// 623 hechizos entre las cuatro, y las cuatro con la MISMA forma medida:
        ///
        ///   diceNum   el hechizo que lanza al dispararse
        ///   diceSide  su grado
        ///   value     el color en RGB —el Avispero lleva 16777215, blanco puro—
        ///   duration  las rondas; el -1 quiere decir que no se cae sola
        ///   zoneDescr la huella alrededor de la casilla apuntada
        ///
        /// Lo único que cambia es cuándo se disparan, así que van por un solo camino con cuatro
        /// disparadores en vez de por cuatro caminos con el mismo cuerpo.
        /// </remarks>
        /// <summary>A qué apunta el hechizo hijo de un sublanzamiento.</summary>
        private enum Apunta
        {
            /// <summary>Al candidato que la máscara acaba de elegir.</summary>
            AlCandidato,

            /// <summary>De vuelta al que lanzó el hechizo padre.</summary>
            AlLanzadorPadre,

            /// <summary>A la casilla que apuntó el padre, resuelta OTRA VEZ en ese momento.</summary>
            ALaCasillaDelPadre,

            /// <summary>Al más cercano de la zona.</summary>
            AlMasCercano,

            /// <summary>
            /// At the source of what set the parent off -- the one whose blow fired the trigger,
            /// or the parent's caster at a cast. 1019 casts the child there.
            /// </summary>
            AlOrigen,
        }

        /// <summary>Una fila de la familia «haz que se lance otro hechizo».</summary>
        private readonly struct Sublanzamiento
        {
            public Sublanzamiento(bool lanzaElCandidato, Apunta apunta, bool topePorValor,
                                  bool yaLoHaceElCaminoViejo = false, bool lanzaElOrigen = false)
            {
                LanzaElCandidato = lanzaElCandidato;
                Apunta = apunta;
                TopePorValor = topePorValor;
                YaLoHaceElCaminoViejo = yaLoHaceElCaminoViejo;
                LanzaElOrigen = lanzaElOrigen;
            }

            /// <summary>
            /// Whether the child is cast by the SOURCE of what set the parent off: the attacker
            /// whose blow fired a damage trigger, or the parent's caster when nothing did.
            /// </summary>
            public bool LanzaElOrigen { get; }

            /// <summary>Si el que lanza el hijo es el candidato en vez del lanzador del padre.</summary>
            public bool LanzaElCandidato { get; }

            public Apunta Apunta { get; }

            /// <summary>Si el campo value limita cuántos candidatos se cogen.</summary>
            public bool TopePorValor { get; }

            /// <summary>Los tres que ya resuelve el código de antes y que no se tocan hoy.</summary>
            public bool YaLoHaceElCaminoViejo { get; }
        }

        /// <summary>
        /// La familia entera de «haz que se lance otro hechizo», en una sola tabla.
        /// </summary>
        /// <remarks>
        /// Son NUEVE efectos y no nueve mecánicas: la misma resolución con tres parámetros —quién
        /// lanza el hijo, a qué apunta, y cuántos candidatos coge—. En todos, diceNum es el
        /// hechizo hijo y diceSide su grado, y en todos el hijo es GRATIS: no cuesta PA.
        ///
        /// La tabla sale del censo de las 431 capturas del juego real, emparejando cada anuncio de
        /// lanzamiento con el padre que lo produjo. Dos agentes independientes rehicieron el
        /// corpus y sacaron los mismos totales —37.947 tramas jwe, 21.307 lanzamientos—, y estas
        /// son las cuentas:
        ///
        ///   efecto   n      mismo lanzador   misma casilla   objetivo==lanzador
        ///   792      6332   4447             3560            6269
        ///   1160     3826   3783             1772             918
        ///   2160      332    332               54              18
        ///   2792       10      0                2              10
        ///   2793      323     68               74             320
        ///   2794      235    182              228              79
        ///   2795        6      0                6               0
        ///
        /// Y el 1017 aparte, con 97 encadenamientos y CERO contraejemplos: el objetivo del hijo es
        /// el lanzador del padre en 97 de 97, y el lanzador del hijo no lo es en 97 de 97.
        ///
        /// El control que lo cierra: con la MISMA máscara «h,P», el 792 no invierte ni una vez en
        /// 106 y el 1017 invierte las 77. O sea que lo que decide no es la máscara, es el efecto.
        /// Y en Jormun conviven un 1160 y un 1017 con la misma máscara en el mismo lanzamiento,
        /// en tramas contiguas, con resultado opuesto.
        ///
        /// Lo que NO se ha medido y va dicho: que el value sea el tope de candidatos es la mejor
        /// explicación de por qué 792 y 2792 conviven con la misma máscara y el mismo hechizo
        /// hijo cambiando sólo ese campo —niveles 80667 y 35952—, pero las diez ejecuciones de
        /// 2792 del corpus tuvieron siempre un solo candidato, así que no está demostrado.
        /// </remarks>
        private static readonly Dictionary<int, Sublanzamiento> Familia = new()
        {
            // Los tres que ya resolvía el motor. Están aquí para que la tabla sea completa y para
            // poder compararlos, pero su código sigue siendo el de antes: reescribirlos esta
            // noche, con el servidor en uso, sería cambiar lo que funciona por lo que aún no.
            [EffectSupport.CastSpell]     = new(true,  Apunta.AlCandidato,        true,  true),
            [EffectSupport.TriggerSpell]  = new(false, Apunta.AlCandidato,        false, true),
            [NearestTargetExecuteSpell]  = new(false, Apunta.AlMasCercano,       false, true),

            // Los cuatro que faltaban, y sus dos primos.
            [1017] = new(true,  Apunta.AlLanzadorPadre,    false),
            [2792] = new(true,  Apunta.AlCandidato,        true),
            [2793] = new(true,  Apunta.AlCandidato,        true),
            [2794] = new(true,  Apunta.ALaCasillaDelPadre, false),
            [2795] = new(true,  Apunta.AlCandidato,        true),
            [2960] = new(false, Apunta.ALaCasillaDelPadre, false),

            // The three the monsters use and no class capture shows, read off the only place
            // that spells them out -- Conde Kontatrás's clock, where each one is the only reading
            // that makes the dofuspourlesnoobs guide come true:
            //
            //   793   like the 792, the candidate casts: Alternancia's "793 under D, mask C" has
            //         the Count cast Huso, whose "1105 A,O" throws the attacker to the other
            //         side of him -- "every hit on him teleports the ATTACKER" on odd turns.
            //   1018  the SOURCE casts at the candidate: Medio Tiempo's "1018 under D, mask C"
            //         has the attacker cast Reloj de Bolsillo at the Count, whose "1105 A,F3416"
            //         -- the attacker's enemy that is template 3416 -- throws the Count to the
            //         other side of the attacker, as the guide has it on even turns.
            //   1019  the source casts at itself.
            //
            // With no blow behind them -- at a cast -- the source is the parent's caster.
            [793]  = new(true,  Apunta.AlCandidato,        false),
            [1018] = new(false, Apunta.AlCandidato,        false, lanzaElOrigen: true),
            [1019] = new(false, Apunta.AlOrigen,           false, lanzaElOrigen: true),

            // 2017 is to 1017 what 2792 is to 792: the candidate casts the child back at the
            // parent's caster, and the value caps how many candidates do. Recursividad is the
            // one class spell that writes it: the enemy it pushed casts 13881's grade 1, whose
            // "2017 13881-3 value 1" on the turrets in contact (Q1) has ONE turret cast grade 3
            // back at him -- its 1105 throws him to the other side of that turret, its 1160
            // pushes him on, and its 792 has him look for the next turret: "si el objetivo
            // termina su desplazamiento en contacto con una torreta, esta lo teletransporta
            // simétricamente y le vuelve a aplicar los efectos del hechizo". INFERRED from that
            // sheet and those rows: the Steamer capture only casts it with no turret in contact
            // (frames 3782-3788: grades 2 and 6, nothing of 2017).
            [2017] = new(true,  Apunta.AlLanzadorPadre,    true),
        };

        /// <summary>
        /// The mark that hooks a sub-cast's child on its target when the child has rows waiting on
        /// a trigger: Morsagüino's Refresgüino, cast on each enemy, holds only an "H" row -- when
        /// healed -- and left nothing on anybody, so it was never hooked and the heal that makes
        /// him vulnerable did nothing. Null when the child waits on nothing.
        /// </summary>
        /// <summary>Whether a row waits on anything but the cast.</summary>
        private static bool EsperaUnDisparador(SpellEffect efecto)
            => efecto.Disparadores().Any(d => !string.Equals(d, AlLanzar, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// A mask without the conditions read when a row goes off -- states, life, the attacker,
        /// the telefragged, the summoned, the caster's -- for arming it by side, kind and zone.
        /// </summary>
        private static string SinCondiciones(string mascara)
        {
            var quedan = new List<string>();
            foreach (var trozo in (mascara ?? "").Split(','))
            {
                string t = trozo.Trim();
                if (t.Length == 0 || t[0] == '*' || t == "O" || t == "T" || t == "W" || t == "U") continue;
                if (t == "o" || t == "u" || t == "PB" || t == "pb" || t == "R" || t == "r") continue;
                if (t.Length > 1 && "EeVv".IndexOf(t[0]) >= 0 && char.IsDigit(t[1])) continue;
                quedan.Add(t);
            }
            return string.Join(",", quedan);
        }

        /// <summary>
        /// How long an armed row lives on its bearer: its own duration, for good at -1, and at 0
        /// to the next round -- or for good when it comes from a behaviour spell, which is the
        /// monster's for the whole fight.
        /// </summary>
        public static int CaducidadDeLaFila(SpellEffect fila, int ronda, bool deConducta)
            => fila.Duration < 0 ? -1
             : fila.Duration > 0 ? ronda + fila.Duration
             : deConducta ? -1 : ronda + 1;

        private static Outcome MarcaDeEnganche(Fighter lanzaElHijo, Fighter sobre, SpellEffect subcast)
        {
            if (sobre == null || subcast.DiceNum <= 0) return null;
            if (!PlayerSpells.Contains(subcast.DiceNum)) return null;
            int grado = Math.Max(1, subcast.DiceSide);
            bool espera = SpellEffects.De(subcast.DiceNum, grado).Any(e =>
                e.Disparadores().Any(d => !string.Equals(d, AlLanzar, StringComparison.OrdinalIgnoreCase))
                && !EsFilaQueLeeElGolpe(e.EffectId));
            if (!espera) return null;
            return new Outcome
            {
                Sobre = sobre, Caster = lanzaElHijo, Efecto = subcast,
                HechizoOrigen = subcast.DiceNum, NivelOrigen = grado,
                EnganchePendiente = true,
            };
        }

        /// <summary>
        /// How long a hooked child lives on its bearer when it left no row to measure it by: its
        /// waiting rows' longest duration, for good if one of them is, and to the next round if
        /// they carry none.
        /// </summary>
        public static int CaducidadDelEnganche(int hechizo, int grado, int ronda)
        {
            int hasta = ronda + 1;
            foreach (var e in SpellEffects.De(hechizo, grado))
            {
                if (!e.Disparadores().Any(d => !string.Equals(d, AlLanzar, StringComparison.OrdinalIgnoreCase))) continue;
                if (e.Duration < 0) return -1;
                hasta = Math.Max(hasta, ronda + e.Duration);
            }
            return hasta;
        }

        /// <summary>
        /// Whether a 406 row takes off a hook that an EARLIER row of the same spell laid: the
        /// hook then lives for the cast alone. Pampandulo hooks 25182 on its caster, takes AP
        /// off, and only then "406 25182"; Zarzas Agresivas hooks its own CMPAS row, hits, and
        /// ends on "406 13527"; Mala Sombra hooks 28716 on the enemy, drains him, "406 28716".
        /// The real server fires a trigger as the row that sets it off lands, so the 406 finds
        /// the hook already spent; here the triggers go off once the cast has been told, after
        /// the engine ran the 406 -- which found nothing yet -- so the fight takes the hook off
        /// again once they have. A 406 written BEFORE the row that hooks, a refresh, does not
        /// count, nor one with a delay or under a trigger of its own.
        /// </summary>
        public static bool CierraUnEngancheDelLanzamiento(int hechizo, int grado, SpellEffect fila, bool critico = false)
        {
            if (fila == null || fila.EffectId != QuitarEfectosDeHechizo || fila.Delay > 0) return false;
            if (EsperaUnDisparador(fila)) return false;
            int cual = fila.Value != 0 ? fila.Value : fila.DiceNum;
            if (cual <= 0) return false;

            var filas = critico && SpellEffects.Criticos(hechizo, grado).Count > 0
                ? SpellEffects.Criticos(hechizo, grado)
                : SpellEffects.De(hechizo, grado);
            int donde = -1;
            for (int i = 0; i < filas.Count; i++)
            {
                if (ReferenceEquals(filas[i], fila) || (fila.EffectUid != 0 && filas[i].EffectUid == fila.EffectUid)) { donde = i; break; }
            }
            for (int i = 0; i < donde; i++)
            {
                var antes = filas[i];
                bool suFilaEspera = cual == hechizo && EsperaUnDisparador(antes) && !EsFilaQueLeeElGolpe(antes.EffectId);
                bool lanzaUnoQueEspera = EsDeLaFamiliaDeSublanzar(antes.EffectId) && antes.DiceNum == cual
                    && SpellEffects.De(cual, Math.Max(1, antes.DiceSide)).Any(e => EsperaUnDisparador(e) && !EsFilaQueLeeElGolpe(e.EffectId));
                if (suFilaEspera || lanzaUnoQueEspera) return true;
            }
            return false;
        }

        /// <summary>¿Este efecto es de los que hacen lanzar otro hechizo?</summary>
        public static bool EsDeLaFamiliaDeSublanzar(int efecto) => Familia.ContainsKey(efecto);

        /// <summary>
        /// "Umbral: #1 PdV": the bearer's life stops at #1 percent of its maximum, once; the blow
        /// that reaches it goes no further and sets off "TR" followed by the spell that put it --
        /// Guerra's Arsenal changes weapon at 80, 60, 40 and 20 %, Miseria turns at 50 %.
        /// </summary>
        public const int Umbral = 2872;

        /// <summary>The trigger a threshold sets off: TR and the spell that put it.</summary>
        public static string AlCruzarElUmbral(int hechizo) => "TR" + hechizo;

        /// <summary>"Invoca al último aliado muerto con entre el #1 y el #2% de sus PdV", and 147 "con el #3%".</summary>
        private static readonly HashSet<int> Resucitar = new HashSet<int> { 780, 1034, 147 };

        private const int TeletransportaOIntercambia = 1101;
        private const int RecargaMenos = 1036;
        private const int RecargaFijada = 1045;
        public const int ActivaLasRunas = 2023;
        public const int ActivaLosGlifos = 1026;

        /// <summary>Damage dealt by a summon: DI.</summary>
        public const string DanoDeInvocacion = "DI";

        /// <summary>
        /// Whether <paramref name="quien"/> is what a mask names, seen from <paramref name="desde"/>:
        /// its sides and kinds -- any of them -- and its conditions -- all of them. For the
        /// triggers that carry a mask, "EK:a,F2992" (an ally of template 2992 died) and
        /// "EC:=0:g" (no allies left). No zone: the whole board.
        /// </summary>
        public static bool CumpleLaMascara(Fighter desde, Fighter quien, string mascara)
        {
            if (desde == null || quien == null) return false;
            bool suyo = quien.TeamId == desde.TeamId;
            bool esInvocado = quien.EsInvocado, esMonstruo = quien.IsMonster && !esInvocado, esJugador = !quien.IsMonster && !esInvocado;
            bool hayLado = false, loNombra = false;
            foreach (var trozo in (mascara ?? "").Split(','))
            {
                string t = trozo.Trim();
                if (t.Length == 0) continue;
                bool? lado = t switch
                {
                    "a" => suyo,
                    "A" => !suyo,
                    "g" => suyo && quien != desde,
                    "c" or "C" => quien == desde,
                    "i" or "j" or "d" => esInvocado && suyo,
                    "I" or "J" or "D" => esInvocado && !suyo,
                    "l" or "h" => esJugador && suyo,
                    "L" or "H" => esJugador && !suyo,
                    "m" => esMonstruo && suyo && quien != desde,
                    "M" => esMonstruo && !suyo,
                    _ => null,
                };
                if (lado.HasValue) { hayLado = true; loNombra |= lado.Value; continue; }
                if (t.Length > 1 && int.TryParse(t.Substring(1), out int n))
                {
                    switch (t[0])
                    {
                        case 'F': if (!quien.IsMonster || quien.MonsterId != n) return false; break;
                        case 'f': if (quien.IsMonster && quien.MonsterId == n) return false; break;
                        case 'E': if (!quien.Buffs.TieneEstado(n)) return false; break;
                        case 'e': if (quien.Buffs.TieneEstado(n)) return false; break;
                    }
                }
            }
            return !hayLado || loNombra;
        }

        /// <summary>Quién lanza el hijo y a qué apunta, para poder comprobarlo desde fuera.</summary>
        public static (bool LanzaElCandidato, string Apunta) ComoSublanza(int efecto)
            => Familia.TryGetValue(efecto, out var fila)
                ? (fila.LanzaElCandidato, fila.Apunta.ToString())
                : (false, "");

        /// <summary>
        /// El 3793: un marcador, no un efecto. No hay nada que aplicar.
        /// </summary>
        /// <remarks>
        /// 430 filas en 163 hechizos, sin texto, sin característica, sin dados y sin duración. El
        /// servidor real lo registra como un embrujo más y lo anuncia cuando salta su disparador,
        /// pero no arrastra a nadie: los efectos que van con él —el veneno, la cura, el PA
        /// diferido— se registran por su cuenta, con su propio disparador y su propia máscara, y
        /// se disparan solos. Coinciden en el tiempo porque comparten el disparador, no porque
        /// éste los llame.
        ///
        /// O sea que tratarlo como una puerta condicional sería inventarse una mecánica. Va al
        /// panel como cualquier otro efecto que no se sabe aplicar, que es lo que ya hace el
        /// camino genérico, y aquí sólo queda dicho por qué está bien que se quede así.
        /// </remarks>
        internal const int MarcadorDeGuion = 3793;

        /// <summary>El 3792: el hermano inmediato del 3793. Tampoco hay nada que aplicar.</summary>
        /// <remarks>
        /// 165 filas en 48 hechizos, y son el mismo animal. Medido sobre las 164 filas que tienen
        /// plantilla de hechizo, sin una sola excepción: el <c>value</c> es un identificador de
        /// una entrada del <c>boundScriptUsageData</c> DEL PROPIO HECHIZO. Y el resto de la fila
        /// está vacío en las 165: dado 0, lado 0, duración 0, retardo 0, y el disparador siempre
        /// inmediato.
        ///
        /// O sea que no lleva ningún número que aplicar a nadie. Es el marcador de «aquí corre un
        /// guion del hechizo», igual que el 3793, y la diferencia entre los dos es sólo que el
        /// 3793 puede ir con disparador y éste no.
        ///
        /// Se declara aquí, y no se deja simplemente que caiga en el camino genérico, porque
        /// entre los dos tocan 39 hechizos de clase: sin decirlo, esos 39 se cuentan para siempre
        /// como «sin implementar» y alguien vuelve a mirarlos cada vez.
        /// </remarks>
        private const int MarcadorDeGuionInmediato = 3792;

        /// <summary>¿Es uno de los dos marcadores de guion, que no hacen nada?</summary>
        public static bool EsMarcadorDeGuion(int efecto)
            => efecto == MarcadorDeGuion || efecto == MarcadorDeGuionInmediato;

        private const int GlifoDeAura = 1091;
        private const int GlifoDeInicioDeTurno = 401;

        /// <summary>«Coloca un glifo de fin de turno».</summary>
        private const int GlifoDeFinDeTurno = 402;

        /// <summary>«Coloca un glifo»: goes off on the turn start of whoever is on it, and on entering.</summary>
        private const int GlifoCorriente = 1165;

        /// <summary>«Disipa los glifos»: the target's glyphs go, the ones of the spell in the die when it names one.</summary>
        private const int DisipaLosGlifos = 2018;

        /// <summary>«Retira los embrujos»: every row that can be dispelled comes off the target.</summary>
        private const int Desembrujar = 132;

        /// <summary>
        /// «Desvela las entidades invisibles»: the target's invisibility rows (150) come off,
        /// whoever put them and whether they can be dispelled or not. Percepción, Predación,
        /// Ojo de Topo, Temblor, Sónar, Centinela.
        /// </summary>
        public const int DesvelaLosInvisibles = 202;

        /// <summary>«Turno cancelado»: a live row of it and the bearer's next turn is lost.</summary>
        public const int TurnoCancelado = 140;

        /// <summary>The forced push and swap: the same as 5 and 8, and nobody pinned escapes them.</summary>
        private const int EmpujeForzado = 1021;
        private const int TironForzado = 1022;
        private const int IntercambioForzado = 1023;
        private const int Trampa = 400;
        private const int Runa = 2022;

        /// <summary>
        /// «Devuelve #1 PA» (120): the points in the bearer's hand, told as the real server tells
        /// them -- the AP sheet, then "jwe 120 f20{f1=N f2=who}" in the caster's name: 117 of them
        /// in 35 captures, Neutral's at frame 11 of "usar neutral en portales".
        /// </summary>
        internal const int DevuelvePA = 120;

        /// <summary>«Duración de los efectos: -N» (167 hechizos). Le recorta rondas a los embrujos.</summary>
        /// <remarks>
        /// Grito Terrorífico lleva dado 4 y máscara «A»: le quita cuatro rondas a lo que el
        /// enemigo tenga encima. Un embrujo al que no le quedan rondas se cae.
        /// </remarks>
        private const int AcortaLosEfectos = 1075;

        /// <summary>«Mata al objetivo y reemplaza por la invocación» (59 hechizos).</summary>
        /// <remarks>
        /// La Siega del Sacrogrito. El dado lleva la plantilla del bicho que sale en su sitio y el
        /// lado del dado su grado. Son las dos cosas a la vez y en ese orden: matar y luego sacar.
        /// </remarks>
        private const int MataYReemplaza = 405;
        private const int MataYReemplazaPorInvocacion = 2796;

        /// <summary>
        /// 1406, "Retira los efectos del rango #1 del hechizo #2": the rows of one GRADE of a spell,
        /// the spell in the value and the grade on the side of the dice. Aguja takes its own
        /// poison's grade 6 off before putting it again -- in its capture five jya and then
        /// "jwe 1406 f33{f2=30842 f3=6 f4=-3}": the spell, the grade, off whom.
        /// </summary>
        public const int QuitaUnGradoDeUnHechizo = 1406;

        /// <summary>
        /// 786, "Cura en el atacante: #1% de los daños": a row under "D" on the bearer -- the
        /// Uginak's prey, the Sram's Zalagarda -- that heals whoever just hit him by that share of
        /// the blow. The Uginak's class sheet: "cuando el uginak y sus aliados infligen daños a
        /// una presa, se curan una parte de los daños que han ocasionado".
        /// </summary>
        private const int CuraAlAtacante = 786;

        /// <summary>
        /// 2973, "Cura #1% de los daños ocasionados", and 2020, "Cura #1% de los daños sufridos":
        /// a heal that is a share of the blow that set its spell off. Mala Sombra's hook on the
        /// enemy casts 28716 when he is hit, and its 2973 heals the Sram's allies around him by
        /// half of it; Perfusión's 2020 heals the Sacrógrito's marked allies by a quarter of what
        /// he takes. Not scaled by the healer's characteristics: the die is the share.
        /// </summary>
        private const int CuraPorElDanoOcasionado = 2973;
        private const int CuraPorElDanoSufrido = 2020;

        private const int EscudoPorNivel = 1020;
        private const int EscudoPorVida = 1039;

        /// <summary>
        /// 1040, the row the client draws for a shield: "#1 de escudo". Both 1020 and 1039 go
        /// out as it, with the points computed. Measured on Patada (1020) in the Tymador captures.
        /// </summary>
        public const int ShieldPanelEffect = 1040;

        /// <summary>The sheet's characteristic for the shield points: 96 in the Patada capture.</summary>
        public const int ShieldCharacteristic = 96;

        private const int VitalityCharacteristic = 11;

        /// <summary>
        /// The same effect, but announced as another one with a flat number: what the panel
        /// shows for a shield (1040 with the points) or for a vitality percentage (153 with the
        /// points). The zone, the mask and the triggers are kept; the dice become the number.
        /// </summary>
        private static SpellEffect ComoSePinta(SpellEffect efecto, int effectId, int puntos) => new()
        {
            EffectId = effectId,
            EffectUid = efecto.EffectUid,
            Value = 0,
            DiceNum = puntos,
            DiceSide = 0,
            Duration = efecto.Duration,
            Delay = efecto.Delay,
            Element = efecto.Element,
            Dispellable = efecto.Dispellable,
            Triggers = efecto.Triggers,
            TargetMask = efecto.TargetMask,
            Forma = efecto.Forma,
            Tamano = efecto.Tamano,
            TamanoMinimo = efecto.TamanoMinimo,
            ParaEnElObjetivo = efecto.ParaEnElObjetivo,
            PasoDeCaida = efecto.PasoDeCaida,
            TopeDeCaida = efecto.TopeDeCaida,
            MaxStack = efecto.MaxStack,
            Probabilidad = efecto.Probabilidad,
            Sorteo = efecto.Sorteo,
        };

        /// <summary>«N de daños del mejor elemento» (20 hechizos de clase).</summary>
        /// <remarks>
        /// Llamilla, Bilbipo, Apetito de Cocobur. El dado es el daño y el elemento lo pone el
        /// lanzador: el suyo más alto de los cuatro. El catálogo del cliente ya numera ese caso
        /// —el 5 de Effects.ElementId es «mejor»—, así que aquí sólo hay que resolverlo mirando
        /// las cuatro características y quedarse con la mayor.
        /// </remarks>
        private const int DanoDelMejorElemento = 2822;

        /// <summary>
        /// «N de daños del peor elemento» (2832): Llamita, "ocasiona daños en el peor elemento
        /// del lanzador". The same question the other way round: of the four characteristics,
        /// the lowest names the element. It went to the panel as a row that nobody knew how to
        /// apply, and the spell hit for nothing.
        /// </summary>
        private const int DanoDelPeorElemento = 2832;

        /// <summary>El 5 de Effects.ElementId: «el mejor», que no es un elemento sino una pregunta.</summary>
        private const int ElementoMejor = 5;

        /// <summary>The two "-N de daños recibidos": a flat cut on the blows the row names.</summary>
        internal static bool EsReduccionDeDanoRecibido(int efecto)
            => efecto == Buffs.DanoRecibidoMenos || efecto == Buffs.DanoRecibidoMenosFijo;

        /// <summary>
        /// The rows a BLOW reads rather than a trigger fires: the flat cuts above and the
        /// "daños sufridos x#1%" (1163). Under a damage kind they are registered at the cast
        /// with the kind as their condition. Salto's 1163 is "trig D": in its capture it goes
        /// out at the cast on the enemy next to the arrival, hidden, until the next round,
        /// and the D says which blows read it -- any. Left as a trigger, nothing fired it and
        /// the enemy took his 100%.
        /// </summary>
        internal static bool EsFilaQueLeeElGolpe(int efecto)
            => EsReduccionDeDanoRecibido(efecto) || efecto == Buffs.DanoSufridoPorCiento
               || efecto == InterceptaLosDanos || efecto == ComparteLosDanos;

        /// <summary>
        /// «Intercepta los daños» (765): a row on the bearer, under the blows it names -- "D"
        /// Sacrificio's and Égida's, "DM" Albarrama's -- whose caster takes those blows in his
        /// place. Measured: the row itself, "jxm 765 'D' f15=7" with no dice, on each ally the zone
        /// names ("Sacrifice", frame 62; the Feca's Égida, frame 1500, laid by the shield summon on
        /// its master). The blow's way to the one who intercepts it is the sheets' words --
        /// "intercepta los daños que reciben los aliados" -- and INFERRED: no capture holds an
        /// intercepted blow, so how it is computed (here: against the interceptor, his resistances
        /// and his shield) and sent (an ordinary blow on him) is not measured.
        /// </summary>
        public const int InterceptaLosDanos = 765;

        /// <summary>
        /// «Comparte los daños» (1061): the same kind of row, on every fighter one cast links --
        /// Don Natural's allies around the tree, Armonía's trees and the Sadida, Comunión Animal's
        /// summon and its Osamodas, Flechas Amorosas' pair. Measured: the rows, "jxm 1061 'D'
        /// f15=7", one per bearer (DonNaturel frame 334, Harmonie 182-185, the Osamodas variants
        /// 1896-1897). The split itself is INFERRED from the words: no capture has a linked fighter
        /// hit. A blow on one of them is cut in as many equal shares as there are linked fighters
        /// alive, each takes his, and what does not divide stays on the one hit.
        /// </summary>
        public const int ComparteLosDanos = 1061;

        /// <summary>«-N% PdV» (9 hechizos). El dado es el TANTO POR CIENTO de la vida máxima.</summary>
        private const int QuitaPorcentajeDeVida = 1048;

        /// <summary>«Transfiere N% de su vida» (7 hechizos).</summary>
        /// <remarks>
        /// El lanzador da y el objetivo recibe. El dado es el tanto por ciento de la vida ACTUAL
        /// del que la da, que es lo que hace que no puedas transferir lo que ya no tienes.
        /// </remarks>
        private const int TransfiereVida = 90;

        private const int Teletransportar = 4;

        /// <summary>
        /// Los cuatro teletransportes simétricos: al otro lado de un pivote, a la misma distancia.
        /// </summary>
        /// <remarks>
        /// 54 hechizos de clase entre los cuatro. Lo que cambia es el pivote, y nada más:
        ///
        ///   1104  «simétrica con respecto al objetivo»   pivote: el objetivo del efecto
        ///   1105  «simétrica con respecto al lanzador»   pivote: quien lanza
        ///   1106  «teletransportación simétrica»         pivote: la casilla apuntada
        ///   1100  «teletransporta a la posición anterior»  no es simétrica: deshace el movimiento
        ///
        /// El reflejo se calcula en coordenadas del mapa, no sobre el número de casilla: la
        /// retícula de Dofus va en diagonal y sumar al índice da un sitio sin relación con el
        /// reflejo.
        /// </remarks>
        private const int SimetricoRespectoAlObjetivo = 1104;
        private const int SimetricoRespectoAlLanzador = 1105;
        private const int Simetrico = 1106;
        private const int ALaPosicionAnterior = 1100;

        /// <summary>«Teletransporta a la posición de inicio de turno».</summary>
        private const int AlInicioDelTurno = 1099;

        /// <summary>«Teletransporta a la posición de inicio de combate».</summary>
        private const int AlInicioDelCombate = 784;

        /// <summary>
        /// A teleport to a cell worked out by the effect -- the mirror of a pivot, a cell the
        /// fighter stood on before. A free cell he lands on; a taken one is a TELEFRAG, in the
        /// client's own words on the Xelor: "se generan cuando dos entidades intercambian
        /// posiciones debido a los efectos de teletransportación de un hechizo". The two swap
        /// and are noted in the spell's telefrags, which the T of the masks reads -- Kontatrás
        /// loses his invulnerability exactly so. One telefrag per spell, as the sheet says.
        /// </summary>
        /// <remarks>
        /// Nobody pinned to the floor goes anywhere, and nobody pinned is swapped out of his cell
        /// either: the Indesplazable (97) and the states the catalogue flags cantBeMoved or
        /// cantSwitchPosition. That is the "Stabilisation" the guide advises against the Count.
        /// </remarks>
        private static Outcome TeletransportarConTelefrag(FightInstance combate, Fighter quienLanza, Fighter sobre,
                                                          int destino, int hechizo, int grado, SpellEffect efecto)
        {
            // Undoing the last move does not count as a move of its own: a second 1100 does not
            // bounce back to where the first one started.
            bool deshace = efecto.EffectId == ALaPosicionAnterior;
            void Llevar(Fighter quien, int casilla)
            {
                if (deshace && quien == sobre) quien.CellId = casilla; else quien.MoverA(casilla);
            }

            if (!sobre.IsAlive || destino == sobre.CellId) return null;
            if (NoSeDejaMover(sobre)) return null;

            // El suelo manda, igual que en el teletransporte normal. A cell that is not there, or
            // not ground, is a teleport that could not land: the W of the masks.
            var suelo = MapManager.GetFightWalkable(combate.ArenaMapId);
            if (destino < 0 || (suelo != null && !suelo.Contains(destino)))
            {
                combate.TeleportsFallidos.Add(sobre.Id);
                return null;
            }

            Fighter ocupante = null;
            foreach (var otro in Todos(combate))
            {
                if (otro != null && otro.IsAlive && otro != sobre && otro.CellId == destino) { ocupante = otro; break; }
            }

            int veniaDe = sobre.CellId;
            if (ocupante == null)
            {
                Llevar(sobre, destino);
                return new Outcome
                {
                    Sobre = sobre, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    CasillaDesde = veniaDe, CasillaHasta = destino,
                };
            }

            if (combate.Telefrags.Count > 0 || NoSeDejaMover(ocupante)) return null;

            Llevar(sobre, destino);
            ocupante.MoverA(veniaDe);
            combate.Telefrags[sobre.Id] = ocupante.Id;
            combate.Telefrags[ocupante.Id] = sobre.Id;
            return new Outcome
            {
                Sobre = sobre, Caster = quienLanza, Efecto = efecto,
                HechizoOrigen = hechizo, NivelOrigen = grado,
                CasillaDesde = veniaDe, CasillaHasta = destino,
                Tambien = ocupante,
                CasillaDesdeDelOtro = destino, CasillaHastaDelOtro = veniaDe,
            };
        }

        /// <summary>Whether a fighter stays where he is whatever moves him: Indesplazable, or a flagged state.</summary>
        private static bool NoSeDejaMover(Fighter quien)
        {
            if (quien.Buffs.TieneEstado(IndesplazableEstado)) return true;
            foreach (int estado in quien.Buffs.Estados)
            {
                var flags = SpellStates.Of(estado);
                if (flags != null && (flags.CantBeMoved || flags.CantSwitchPosition)) return true;
            }
            return false;
        }

        /// <summary>«Indesplazable», which the catalogue does not flag and every push already honours.</summary>
        private const int IndesplazableEstado = 97;

        /// <summary>«Intercambia las posiciones». 253 hechizos.</summary>
        /// <remarks>
        /// Dos que se cambian el sitio. Se mueven LOS DOS, así que hay que anunciar los dos
        /// desplazamientos: con uno solo, el cliente deja a uno de ellos pintado donde estaba y
        /// a partir de ahí ya no coincide con el servidor en nada.
        /// </remarks>
        private const int IntercambiarPosiciones = 8;

        private const int Tirar = EffectSupport.Pull;

        /// <summary>"Retrocede #1 casillas" y "Avanza #1 casillas": mueven al QUE LANZA.</summary>
        private const int Retroceder = EffectSupport.StepBack;
        private const int Avanzar = EffectSupport.StepForward;
        private const int PonerEstado = EffectSupport.AddState;
        private const int QuitarEstado = EffectSupport.RemoveState;

        /// <summary>
        /// "Lanza el hechizo del dado en el grado de la cara". Es el enganche con el que las
        /// actitudes de los objetos encadenan lo que de verdad hacen.
        /// </summary>
        public const int EfectoQueLanzaHechizo = EffectSupport.CastSpell;
        private const int LanzarHechizo = EfectoQueLanzaHechizo;

        /// <summary>
        /// Variante utilisée par les sorts de classe. Chez l'Ouginak, elle relie notamment
        /// Molosse/Apaisement aux sous-sorts qui ajoutent ou retirent la Rage.
        /// </summary>
        private const int DispararHechizo = EffectSupport.TriggerSpell;
        private const int NearestTargetExecuteSpell = EffectSupport.NearestTargetExecuteSpell;

        private const int QuitarEfectosDeHechizo = EffectSupport.RemoveSpellEffects;
        private const int CambiarApariencia = EffectSupport.ChangeLook;

        /// <summary>"Invoca: #1". La plantilla del bicho viaja en el dado.</summary>
        public const int Invocar = EffectSupport.Summon;

        /// <summary>
        /// Los efectos que colocan algo EN UNA CASILLA en vez de sobre alguien: una invocación,
        /// una trampa, un glifo. Su objetivo es el suelo, así que no se les busca dueño.
        ///
        ///   181, 1008, 1011  "Invoca: #1"     400  "Coloca una trampa"
        ///   401  "Coloca un glifo de inicio de turno"
        ///   1091 "Coloca un glifo aura"        2022 "Coloca una runa"
        ///
        /// El 1008 y el 1011 son invocaciones igual que el 181 y con la misma forma —el dado
        /// lleva la plantilla del bicho y el lado su grado, comprobado: el 3987 es «Gladiador
        /// aprendiz ocra» y el 3112 «Explobomba»—. Estaban fuera del conjunto y por eso los 22
        /// hechizos que los llevan no invocaban nada, en silencio.
        /// </summary>
        private static readonly HashSet<int> AlSuelo =
            new HashSet<int> { 181, 1008, 1011, 400, 401, 402, 1091, 1165, 2022, EffectSupport.Illusions, 780, 1034, 147, 1101, InvocaUnDoble,
                               ColocaUnPortal, DesactivaUnPortal };

        /// <summary>«Invoca un doble del lanzador»: the Sram's Doble and Conspirador.</summary>
        public const int InvocaUnDoble = 180;

        /// <summary>«Coloca un portal»: see Jondo.Unity.World.Fights.PortalNetwork.</summary>
        public const int ColocaUnPortal = 1181;

        /// <summary>«Teleportal»: whoever stands on a portal goes through the network.</summary>
        public const int AtraviesaLosPortales = 1182;

        /// <summary>«Desactiva un portal»: off until the caster's next turn (Neutral, Interrupción).</summary>
        public const int DesactivaUnPortal = 1183;

        /// <summary>«Sigue al lanzador»: Lazo Espiritual's bond.</summary>
        public const int SigueAlLanzador = 2184;

        /// <summary>The portal effects: a spell that carries one is not projected through a portal.</summary>
        public static bool EsDePortales(int efecto)
            => efecto == ColocaUnPortal || efecto == AtraviesaLosPortales || efecto == DesactivaUnPortal;

        /// <summary>
        /// The double of a fighter: his characteristics -- life, points, stats, resistances,
        /// the rest of his sheet -- his name, level and look, as a summon of his that plays no
        /// spell ("no ataca") and walks with his MP, so it is his to play. Read off the
        /// "sram-doble" capture, frame 16: the block carries the Sram's look and name, level
        /// 200, and his sheet's numbers, 12 AP and 6 MP among them. Its life at birth, the whole
        /// of his maximum, is an inference: the capture says the maximum, not what it starts
        /// with.
        /// </summary>
        public static Fighter DobleDe(FightInstance combate, Fighter original, int celda)
        {
            var doble = new Fighter
            {
                Id = combate.SiguienteIdDeInvocado(),
                Name = original.Name,
                CellId = celda,
                IsMonster = true,
                DoubleOf = original.Id,
                Level = original.Level,
                Look = original.Look,
                LookBoneId = original.LookBoneId,
                MaxHP = original.MaxHP,
                CurrentHP = original.MaxHP,
                MaxAP = original.MaxAP, CurrentAP = original.MaxAP,
                MaxMP = original.MaxMP, CurrentMP = original.MaxMP,
                Initiative = original.Initiative,
                Strength = original.Strength, Intelligence = original.Intelligence,
                Chance = original.Chance, Agility = original.Agility,
                Power = original.Power, Vitality = original.Vitality,
                CriticalBonus = original.CriticalBonus, CriticalDamage = original.CriticalDamage,
                DamageDealtPercent = original.DamageDealtPercent, FlatDamage = original.FlatDamage,
                EarthDamage = original.EarthDamage, FireDamage = original.FireDamage,
                WaterDamage = original.WaterDamage, AirDamage = original.AirDamage,
                NeutralDamage = original.NeutralDamage, PushDamage = original.PushDamage,
                Range = original.Range,
                NeutralResPct = original.NeutralResPct, EarthResPct = original.EarthResPct,
                FireResPct = original.FireResPct, WaterResPct = original.WaterResPct,
                AirResPct = original.AirResPct,
                JuegaTurno = true,
            };
            foreach (var (caracteristica, valor) in original.Otras) doble.Otras[caracteristica] = valor;
            return doble;
        }

        /// <summary>The zone shape that names its cells outright.</summary>
        private const int FormaDeCeldasFijas = ';';

        /// <summary>
        /// The cells an effect covers: the ones a ';' zone names, or its shape around the cell.
        /// </summary>
        internal static List<int> CasillasDelEfecto(SpellEffect efecto, int desde, int centro)
            => efecto.Forma == FormaDeCeldasFijas && efecto.CeldasFijas.Count > 0
                ? new List<int>(efecto.CeldasFijas)
                : Jondo.Unity.World.Maps.Zone.Casillas(efecto.Forma, efecto.Tamano, desde, centro, efecto.TamanoMinimo,
                                                       efecto.ParaEnElObjetivo);

        /// <summary>Los tres efectos que sacan un bicho al tablero.</summary>
        private static readonly HashSet<int> Invocaciones = new HashSet<int> { 181, 1008, 1011 };

        /// <summary>"Activa una bomba". 36 spells carry it, and none of them worked before.</summary>
        /// <remarks>
        /// It is what the Rogue's Detonador is made of, and Estopín, Detonación, the three
        /// Explosión Tymadora, Tornado Tymador, Tormenta Tymadora, Polvo and Bombinmóvil. Without
        /// it a bomb sat on the board until something killed it, and dying is not exploding: per
        /// the client's own class sheet, "si se mata una bomba que no sea mediante un hechizo que
        /// active la explosión, normalmente morirá sin ocasionar daños ni retiradas".
        /// </remarks>
        public const int ActivarBomba = 1009;

        /// <summary>
        /// Como de lejos llega una explosion, leido de su propio hechizo.
        /// </summary>
        /// <remarks>
        /// No es un dos escrito a mano: el efecto 99 de la Explosion Tymadora lleva
        /// <c>zoneDescr{shape: 67, param1: 2}</c>, que es un circulo de radio dos, y las cuatro
        /// explosiones traen el suyo. Si algun dia Ankama lo cambia, cambia solo.
        /// </remarks>
        internal static int RadioDeLaExplosion(int hechizo, int grado)
        {
            int radio = 0;
            foreach (var efecto in SpellEffects.De(hechizo, grado))
            {
                if (efecto.Forma != Jondo.Unity.World.Maps.Zone.Circulo) continue;
                if (efecto.Tamano > radio) radio = efecto.Tamano;
            }
            return radio;
        }


        public static bool VaAlSuelo(int efecto) => AlSuelo.Contains(efecto);

        /// <summary>Whether this effect puts a summoned creature on the board.</summary>
        /// <remarks>
        /// The three ids read the same in the client -- "Invoca: #1", with the template in the
        /// die -- and the caller almost always wants all three. Checking only 181, which is what
        /// the cast preflight did, misses the Rogue's bombs and the Sadida's trees: those come
        /// through 1008.
        /// </remarks>
        public static bool EsInvocacion(int efecto) => Invocaciones.Contains(efecto);

        /// <summary>Fixed healing. The element's characteristic scales the roll; 49 is flat.</summary>
        private const int FixedHeal = EffectSupport.FireHeal;

        /// <summary>
        /// Las cinco curas fijas, una por elemento.
        /// </summary>
        /// <remarks>
        /// 108 fuego, 2998 agua, 2999 aire, 3000 tierra, 3001 neutral. Las cuatro que faltaban
        /// tocan 31 hechizos de clase.
        ///
        /// No ha hecho falta escribir nada nuevo para ellas: el cálculo de la cura ya estaba
        /// escrito sin atarse al elemento —lee el effectElement del propio efecto y busca con él
        /// la característica que la escala—, y quien lo escribió dejó dicho por qué en un
        /// comentario: «clavar el 15 aquí es lo que hace que las otras cinco salgan mal el día
        /// que se implementen». Lo único que ataba al fuego era que la constante era un número
        /// suelto en vez de un conjunto.
        /// </remarks>
        private static readonly HashSet<int> CurasFijas = new()
        {
            FixedHeal, 2998, 2999, 3000, 3001, CuraDelMejorElemento,
        };

        /// <summary>
        /// 3002, "#1 a #2 de curas del mejor elemento": the sixth fixed heal, whose element is
        /// the caster's best -- Escalpelo, Zarza Tranquilizadora, Socorrismo, Don Natural. The
        /// Aniripsa's class sheet says it in so many words: "puede curar en todos los elementos
        /// ... o incluso en su mejor elemento".
        /// </summary>
        private const int CuraDelMejorElemento = 3002;

        /// <summary>¿Es una de las cinco curas fijas?</summary>
        private static bool EsCuraFija(int efecto) => CurasFijas.Contains(efecto);

        private const int HealsCharacteristic = 49;

        /// <summary>
        /// Which characteristic scales an elemental effect, by the element the effect declares.
        /// </summary>
        /// <remarks>
        /// The same numbers <c>FightHandler.CaracteristicaDelElemento</c> uses for damage, keyed on
        /// the element id rather than on the enum, because that is what <c>SpellEffect.Element</c>
        /// carries -- straight out of the spell's own <c>effectElement</c>, in the numbering of
        /// <c>Effects.ElementId</c>: 0 neutral, 1 earth, 2 fire, 3 water, 4 air, 5 best.
        ///
        /// Written as a lookup rather than a 15 on the grounds that the 15 is only correct by
        /// accident: it is right for the one heal that is implemented, fire, and wrong for the
        /// five that are not. Best-element (5) is never asked here: the heal branch turns it into
        /// the caster's best element first (3002).
        /// </remarks>
        /// <summary>The fighter's own points for one of the four elemental characteristics.</summary>
        private static int StatOf(Fighter quien, int caracteristica) => caracteristica switch
        {
            10 => quien.Strength,
            13 => quien.Chance,
            14 => quien.Agility,
            15 => quien.Intelligence,
            _ => quien.Otra(caracteristica),
        };

        internal static int CharacteristicOfElement(int element) => element switch
        {
            1 => 10,   // tierra, fuerza
            2 => 15,   // fuego, inteligencia
            3 => 13,   // agua, suerte
            4 => 14,   // aire, agilidad
            _ => 10,   // neutral
        };

        /// <summary>
        /// El MEJOR elemento de un combatiente: aquel cuya característica lleva más alta.
        /// </summary>
        /// <remarks>
        /// El catálogo del cliente numera este caso —el 5 de Effects.ElementId es «mejor»— y hay
        /// además un efecto entero para él, el 2822 «N de daños del mejor elemento», que llevan
        /// veinte hechizos de clase: Llamilla, Bilbipo, Apetito de Cocobur.
        ///
        /// Se mira la característica CON LOS EMBRUJOS PUESTOS, no la de la ficha: un hechizo que
        /// te sube la agilidad puede cambiar cuál es tu mejor elemento a mitad de combate, y eso
        /// es justamente para lo que se lanza.
        ///
        /// El empate se rompe por el orden tierra, fuego, agua, aire. No está medido cuál usa el
        /// juego real; hace falta UN criterio estable para que dos lanzamientos iguales den lo
        /// mismo, y éste es el orden en que el propio catálogo numera los elementos.
        /// </remarks>
        internal static int MejorElementoDe(Fighter quien, int ronda)
        {
            int mejor = 1, cuanto = int.MinValue;

            foreach (int elemento in new[] { 1, 2, 3, 4 })
            {
                int car = CharacteristicOfElement(elemento);
                int tiene = StatOf(quien, car) + quien.Buffs.De(car, ronda);
                if (tiene > cuanto) { cuanto = tiene; mejor = elemento; }
            }

            return mejor;
        }

        /// <summary>The element of the caster's lowest characteristic; the first of a tie.</summary>
        internal static int PeorElementoDe(Fighter quien, int ronda)
        {
            int peor = 1, cuanto = int.MaxValue;

            foreach (int elemento in new[] { 1, 2, 3, 4 })
            {
                int car = CharacteristicOfElement(elemento);
                int tiene = StatOf(quien, car) + quien.Buffs.De(car, ronda);
                if (tiene < cuanto) { cuanto = tiene; peor = elemento; }
            }

            return peor;
        }

        /// <summary>Effect 1159, received healing as a percentage multiplier.</summary>
        private const int ReceivedHealingPercent = 1159;

        /// <summary>
        /// Applies the fixed-heal formula after the shared effect roll and zone falloff.
        /// </summary>
        /// <remarks>
        /// <b>The shape of this formula is an inference and is written down as one.</b> Power and
        /// damage bonuses not participating, the flat heals landing after the multiply rather than
        /// before it, and the received-healing multiplier going last: none of the three is measured.
        /// There is no capture of a fixed heal anywhere in this repository -- the only healing
        /// capture is the Ocra's percentage beacon, which takes a different path entirely.
        ///
        /// It follows the damage formula next door, which IS measured, and that is the whole of the
        /// argument for it. What would settle it: a capture of a character with known Intelligence
        /// and known "Curas" casting a 108 spell, twice, with the flat bonus changed in between.
        /// </remarks>
        internal static int CalculateFixedHeal(int baseHeal, int intelligence, int flatHeals,
                                               int receivedMultiplier = 100)
        {
            if (baseHeal <= 0 || receivedMultiplier <= 0) return 0;

            long scaled = (long)baseHeal * Math.Max(0L, 100L + intelligence) / 100L + flatHeals;
            if (scaled <= 0) return 0;

            double received = scaled * (receivedMultiplier / 100.0);
            if (received >= int.MaxValue) return int.MaxValue;
            return Math.Max(0, (int)Math.Round(received));
        }

        /// <summary>"Cura: #1% de los PdV máximos". El dado es el porcentaje.</summary>
        private const int CuraPorcentual = EffectSupport.HealPercent;

        /// <summary>Los dos números de característica de los puntos.</summary>
        /// <summary>
        /// Los efectos que ROBAN vida: pegan y curan al lanzador por la mitad.
        ///
        /// Salen del catálogo del cliente, tal cual los describe: el 91 es «robo de agua», el 92
        /// de tierra, el 93 de aire, el 94 de fuego, el 95 neutral y el 82 el neutral fijo. Los
        /// 2828 y 2890 son «robo del mejor elemento» y «del peor», que eligen el elemento al
        /// vuelo pero roban igual.
        ///
        /// No confundirlos con los 96 a 100, que son los daños del mismo elemento y no curan
        /// nada. Un solo número de diferencia y el comportamiento es otro.
        /// </summary>
        private static readonly HashSet<int> RobosDeVida = new HashSet<int>
        {
            82, 91, 92, 93, 94, 95, 2828, 2890
        };

        public static bool EsRoboDeVida(int efecto) => RobosDeVida.Contains(efecto);

        /// <summary>«Mata al objetivo», el 141 del catálogo del cliente.</summary>
        /// <remarks>
        /// Lo trae Doom de Masas (3450), que es de administración: un PA, alcance cero, zona, y un
        /// efecto 120 detrás que devuelve el PA gastado. Hasta ahora el 141 caía en la rama de
        /// «no sé aplicarlo pero lo anuncio», que lo pintaba en el panel de embrujos y no mataba
        /// a nadie.
        /// </remarks>
        internal const int MataAlObjetivo = 141;

        /// <summary>"Sin efecto adicional": the sheet's marker for a grade that does nothing more.</summary>
        internal const int SinEfectoAdicional = 666;

        private const int PuntosDeAccion = 1;
        private const int PuntosDeMovimiento = 23;

        /// <summary>
        /// "Mata al objetivo". Es la cuenta atrás de un invocado: al nacer le cuelgan uno de
        /// éstos con su ronda, y cuando llega, se deshace.
        /// </summary>
        public const int MatarAlObjetivo = EffectSupport.Kill;

        /// <summary>Cuántos hechizos encadenados se admiten antes de sospechar de un bucle.</summary>
        private const int HondoMaximo = 6;

        /// <summary>
        /// El grado en el que vive el enganche de una actitud. Es siempre el primero: los otros los
        /// nombra él mismo por su número, así que no hay que preguntarle a nadie cuál toca.
        /// </summary>
        public const int GradoDelEnganche = 1;

        /// <summary>El disparador de "ahora mismo".</summary>
        public const string AlLanzar = "I";
        public const string AlEmpezarElTurno = "TB";
        public const string AlAcabarElTurno = "TE";
        public const string CuandoMePegan = "DBE";

        /// <summary>
        /// When the bearer is hurt by somebody standing next to him (DM, "cuerpo a cuerpo")
        /// or by somebody further away (DR), whichever side he is on. Remisión is written on
        /// the pair: its push and its state go under DM, its "-N% de daños recibidos" on a
        /// bomb under DR. The blow's dealer is at hand as the fight's TriggeringAttacker.
        /// </summary>
        public const string CuandoMePeganDeCerca = "DM";
        public const string CuandoMePeganDeLejos = "DR";

        /// <summary>
        /// When the bearer dies. Read off Polvo: "haciendo que explote si es destruida" is its
        /// 1009 "Activa una bomba" and its two 792 under trigger X, and nothing else in the spell
        /// speaks of the bomb's death. The Tymobot's own passive casts 20683 under X too.
        /// </summary>
        public const string AlMorir = "X";

        /// <summary>
        /// Cuando uno ANDA, por cada casilla. Es el disparador del Centinela del Ocra, que da
        /// alcance y daños a distancia a cambio de quedarse quieto: cada paso se lleva uno de
        /// alcance y un dos por ciento de daños.
        ///
        /// Medido en su captura: once movimientos, once bajadas, ninguna excepción.
        /// </summary>
        public const string AlAndar = "CCMPARR";

        // The triggers the monsters' spells wait on, named by the census of their 12,546 spells
        // (scratch/census/monster_triggers.tsv). The damage ones are read off the rows that use
        // them: Moon's four totems wait on DE, DF, DW and DA, one element each; Hell Mina's
        // "Cólera" spells on CDE, CDF, CDW and CDA, one per element her players deal. What the
        // others mean is read off their names and their spells, and said so where it is a guess.

        /// <summary>The bearer takes damage, from anybody, of any element.</summary>
        public const string AlRecibirDano = "D";

        /// <summary>
        /// The bearer takes the damage of a SPELL -- a blow that is not a weapon's. Read off
        /// Dispersión, the Xelor's cómplice, which "devuelve una parte de los daños de hechizo
        /// que sufre" through a 792 under "DS|XDS"; in its capture the chain goes off after the
        /// Xelor's 13254 lands on it. XDS is not read.
        /// </summary>
        public const string AlRecibirDanoDeHechizo = "DS";

        /// <summary>The bearer takes damage from somebody of his own side.</summary>
        public const string DanoDeAliado = "DBA";

        /// <summary>The bearer takes damage in melee: the same melee the reduction rows read it as.</summary>
        public const string DanoCuerpoACuerpo = "DCAC";

        /// <summary>The bearer takes damage of one element: DN, DE, DF, DW, DA.</summary>
        public static string DanoDeElemento(int elemento) => elemento switch
        {
            1 => "DE",
            2 => "DF",
            3 => "DW",
            4 => "DA",
            _ => "DN",
        };

        /// <summary>The bearer DEALS damage of one element: CDN, CDE, CDF, CDW, CDA.</summary>
        public static string CausaDanoDeElemento(int elemento) => "C" + DanoDeElemento(elemento);

        /// <summary>The bearer deals damage in melee.</summary>
        public const string CausaDanoDeCerca = "CDM";

        /// <summary>The bearer is healed (H); the bearer heals somebody (CH).</summary>
        public const string AlSerCurado = "H";
        public const string AlCurar = "CH";

        /// <summary>A state goes on the bearer (EON&lt;n&gt;) or comes off him (EOFF&lt;n&gt;).</summary>
        public static string AlPonerseElEstado(int estado) => "EON" + estado;
        public static string AlQuitarseElEstado(int estado) => "EOFF" + estado;

        /// <summary>The bearer is pushed or pulled (P), teleported (TP), or moved at all by an effect (M).</summary>
        public const string AlSerEmpujado = "P";
        public const string AlSerTeletransportado = "TP";
        public const string AlSerMovido = "M";

        /// <summary>
        /// The bearer takes the damage of a collision: his own push into something (PD), or
        /// somebody pushed into him (PPD). Klim's Galuchat waits on PPD, and the guide lifts his
        /// invulnerability by pushing an entity into him; Obsidiantre's Obligación waits on PMD,
        /// which the guide lifts the same way -- so PMD is taken for the same collision. Inferred.
        /// </summary>
        public const string AlChocarEmpujado = "PD";
        public const string AlChocarleUnEmpujado = "PPD";
        public const string AlChocarleUnEmpujadoM = "PMD";

        /// <summary>The bearer loses AP (APA) or MP (MPA) to an enemy.</summary>
        public const string AlPerderPA = "APA";
        public const string AlPerderPM = "MPA";

        // The triggers the class spells wait on and the fight did not fire, read off the class
        // sheets that describe them. Each is the reading of those words; none is in a capture.

        /// <summary>
        /// Per MP the bearer uses (CMPARR), like CCMPARR: Palabra Maliciosa "retira potencia al
        /// objetivo por cada PM que utiliza"; El Sol maximises "en una entidad si esta utiliza
        /// sus PM".
        /// </summary>
        public const string AlUsarUnPM = "CMPARR";

        /// <summary>The bearer is dispelled (DIS): Barricada "aumenta sus PM si es ... desembrujado".</summary>
        public const string AlSerDesembrujado = "DIS";

        /// <summary>
        /// The bearer summons (CI), with the new summon as the one who set it off -- the "u" of the
        /// masks. Pacto Bestial sacrifices "todas sus invocaciones osamodas presentes y futuras";
        /// Caja de Herramientas makes "todas las invocaciones del aliado objetivo" pacifist.
        /// </summary>
        public const string AlInvocar = "CI";

        /// <summary>The bearer takes a trap's blow (DT): Concentración de Chakra, Toxinas.</summary>
        public const string AlSufrirDanoDeTrampa = "DT";

        /// <summary>The bearer lands a critical hit (CC): Buena Estrella, Destino de Zurcarák.</summary>
        public const string AlGolpeCritico = "CC";

        /// <summary>The bearer is moved by a pull (MA): Imantación, Cruce -- "si las bombas son desplazadas".</summary>
        public const string AlSerAtraido = "MA";

        /// <summary>The bearer is moved by a swap (MS): Impostura, and the Xelor's "MS|TP".</summary>
        public const string AlSerIntercambiado = "MS";

        /// <summary>
        /// The bearer moves somebody else (PO) or deals push damage (CPD): Osadía goes off "si el
        /// objetivo atrae, repele, intercambia posición o inflige daños de empuje".
        /// </summary>
        public const string AlDesplazarAOtro = "PO";
        public const string CausaDanoDeEmpuje = "CPD";

        /// <summary>The bearer loses range to somebody (R): Desprendimiento.</summary>
        public const string AlPerderAlcance = "R";

        /// <summary>
        /// The bearer makes somebody lose AP (CAPAS) or MP (CMPAS): the C of the one who causes
        /// it, as in CH and CDM, on the AP/MP loss of APA/MPA. Pampandulo "retira PA ... los PA
        /// que se retiren se distribuyen a ... los aliados" hooks 25182 on its caster, whose one
        /// row gives AP under CAPAS, before its 1079; Zarzas Agresivas hooks "+1 PM on C" under
        /// CMPAS before its 1080, "da 1 PM al lanzador por enemigo alcanzado". So it goes off
        /// for a removal that lands, a steal's included, once per fighter who lost points. That
        /// the S asks for the points to land (CAPA and CMPA, without it, are only on monster
        /// spells) is an inference.
        /// </summary>
        public const string AlQuitarPA = "CAPAS";
        public const string AlQuitarPM = "CMPAS";

        /// <summary>
        /// The bearer kills another entity (K): the Zurcarák's Emperatriz and Emperador "aumenta
        /// los PM / los PA de una entidad si esta acaba con otra entidad", Pesadilla de Rakooper
        /// "cada vez que un atacante mata a una entidad". On the one whose blow took the last
        /// life, once per death. The reading of those sheets; no capture fires it.
        /// </summary>
        public const string AlMatar = "K";

        /// <summary>
        /// The bearer puts a shield on somebody (CS): El Crupier "aumenta los PM de una entidad si
        /// esta aplica un escudo", Sueño de Rakooper "cuando un defensor aplica un escudo", Amor de
        /// Helsefina "cuando el portador aplica escudo". On the caster of the shield row, once per
        /// cast. The reading of those sheets; no capture fires it.
        /// </summary>
        public const string AlPonerEscudo = "CS";

        /// <summary>
        /// The bearer goes through a portal (PT): Coalición "cura al objetivo cuando atraviesa un
        /// portal", and the Osamodas' Lazo Espiritual goes when he does (M|TP|...|PT). Fired on
        /// whoever crossed, walking in or by a Teleportal.
        /// </summary>
        public const string AlCruzarUnPortal = "PT";

        /// <summary>
        /// An entity goes through a portal of the bearer's (CPT): Ayuda Mutua "aplica efectos
        /// cuando una entidad atraviesa un portal", on its Selatrop. The C of the one it comes from,
        /// as in CH and CAPAS: fired on the owner of the portal crossed. INFERRED that it is the
        /// owner's portals and not anybody's: the sheet does not say whose.
        /// </summary>
        public const string AlCruzarseSuPortal = "CPT";

        /// <summary>
        /// The bearer casts a spell through a portal (PST): the Selatrop's passive hooks its grade 2
        /// on him so -- "+2% daños y curas finales", ten times -- and in the captures it goes off
        /// after every projected cast: "pegar a traves de diferentes portales", frames 21, 74, 96,
        /// 130, 212 and 233; after the spell's own effects in Extinción's, frame 13.
        /// </summary>
        public const string AlProyectarPorUnPortal = "PST";

        /// <summary>The trap of the glyph family, for the fight to tell a trap's blow.</summary>
        public const int ColocaUnaTrampa = Trampa;

        /// <summary>«Retira los embrujos», for the fight to tell a dispel.</summary>
        public const int Desembrujo = Desembrujar;

        /// <summary>
        /// Resuelve un hechizo entero y devuelve lo que hay que hacer, en orden.
        ///
        /// <paramref name="disparador"/> filtra: al lanzar se piden los "I", al empezar el turno los
        /// "TB", y así. Los efectos con otro disparador se quedan quietos hasta que les toque.
        /// </summary>
        /// <param name="efectosSorteados">
        /// The cast's own draw of the random rows, when the blows of the same cast were dealt
        /// from it: without one, the rows are drawn here.
        /// </param>
        /// <param name="rondaDelEnganche">
        /// For a trigger fired off a hooked spell, the round the hook was put in: a hooked row
        /// with a delay does not go off before that round plus its delay. Negative at a cast.
        /// </param>
        public static List<Outcome> Resolver(FightInstance combate, Fighter quienLanza,
                                                  int hechizo, int grado, Fighter objetivo,
                                                  string disparador, int ronda, int hondo = 0,
                                                  int celdaApuntada = -1, bool critico = false,
                                                  int nearestChainBudget = -1,
                                                  Fighter animationCaster = null,
                                                  HashSet<long> bombasYaEstalladas = null,
                                                  IReadOnlyList<SpellEffect> efectosSorteados = null,
                                                  int rondaDelEnganche = -1,
                                                  bool armar = true, bool soloAlObjetivo = false)
        {
            if (hondo > HondoMaximo) return new List<Outcome>();
            if (!armar) combate.SinArmar++;
            var raizDeFuera = combate.RootCaster;
            if (hondo == 0) combate.RootCaster = quienLanza;
            try
            {
            if (nearestChainBudget < 0)
                nearestChainBudget = Todos(combate).Count(fighter => fighter != null && fighter.IsAlive);
            return ResolveEffects(combate, quienLanza, hechizo, grado, objetivo, disparador, ronda,
                                  efectosSorteados ?? EfectosDeLaTirada(hechizo, grado, critico), hondo,
                                  celdaApuntada, critical: critico,
                                  nearestChainBudget: nearestChainBudget,
                                  animationCaster: animationCaster,
                                  bombasYaEstalladas: bombasYaEstalladas,
                                  yaSorteado: efectosSorteados != null,
                                  rondaDelEnganche: rondaDelEnganche,
                                  soloAlObjetivo: soloAlObjetivo);
            }
            finally
            {
                if (!armar) combate.SinArmar--;
                combate.RootCaster = raizDeFuera;
            }
        }

        /// <summary>
        /// Runs the real effect pipeline over an explicit list. Tests inject catalogue-shaped
        /// effects here so they exercise targeting, shared rolls, delays and HP mutation without
        /// replacing the production database.
        /// </summary>
        /// <remarks>
        /// The signature is English and the body is Spanish, and that is the house rule rather than
        /// an oversight: this is <c>Resolver</c>, which has been here a long time, given an English
        /// name and one new parameter so a test can inject the roll. The rule for a legacy file is
        /// to write what you ADD in English and leave the surrounding prose alone -- a wholesale
        /// translation makes a large diff that hides the small change inside it. So the new
        /// parameter, the new comment about the shared heal roll, and nothing else.
        /// </remarks>
        internal static List<Outcome> ResolveEffects(
            FightInstance combat, Fighter caster, int spell, int grade, Fighter target,
            string trigger, int round, IReadOnlyList<SpellEffect> effects, int depth = 0,
            int aimedCell = -1, Func<SpellEffect, int> rollEffect = null,
            bool critical = false, int nearestChainBudget = -1,
            Fighter animationCaster = null,
            HashSet<long> bombasYaEstalladas = null,
            bool yaSorteado = false, int rondaDelEnganche = -1, bool soloAlObjetivo = false)
        {
            var fuera = new List<Outcome>();
            bombasYaEstalladas ??= new HashSet<long>();
            if (depth > HondoMaximo) return fuera;

            // The telefrags this spell makes are its own: "un hechizo solo puede generar y
            // activar un único telefrag por lanzamiento", and a child spell's T reads the child's.
            var telefragsDeFuera = combat.Telefrags;
            combat.Telefrags = new Dictionary<long, long>();
            var fallidosDeFuera = combat.TeleportsFallidos;
            combat.TeleportsFallidos = new HashSet<long>();

            // Whom the caster carries as the spell begins, for the K of its rows after a throw.
            var cargadoDeFuera = combat.CarriedAtCast;
            combat.CarriedAtCast = (caster.Id, caster.Carrying);

            // A monster spell's triggered rows are armed at its cast; a player's keep their hooks.
            bool armaSusFilas = combat.SinArmar == 0 && !PlayerSpells.Contains(spell)
                                && string.Equals(trigger, AlLanzar, StringComparison.OrdinalIgnoreCase);
            if (nearestChainBudget < 0)
                nearestChainBudget = Todos(combat).Count(fighter => fighter != null && fighter.IsAlive);

            // WHERE EVERYBODY STANDS AS THE SPELL LANDS, judged once for all its rows. The
            // real server picks the targets of every row before it moves anybody: in the
            // Fricción capture the pull carries the enemy from 202 to 216 and the state that
            // follows it still lands on him, cast at 202. Read live, the row after a pull
            // found the cell empty and the state went nowhere, and with it the hook that
            // makes the spell keep pulling.
            var celdasAlEmpezar = new Dictionary<Fighter, int>();
            foreach (var luchador in Todos(combat))
            {
                if (luchador != null) celdasAlEmpezar[luchador] = luchador.CellId;
            }

            // Every state condition of one spell is judged against the SAME snapshot, and Rage is
            // why that matters: grade 1 of spell 13745 carries all three branches -- 0->I, I->II
            // and II->beast. Without a snapshot the state the first branch sets makes the second
            // one true inside the same resolution, then the third, and a single point of Rage
            // walks the character straight through all three tiers.
            var estadosAlEmpezar = new Dictionary<Fighter, HashSet<int>>();
            foreach (var luchador in Todos(combat))
            {
                if (luchador != null)
                    estadosAlEmpezar[luchador] = new HashSet<int>(luchador.Buffs.Estados);
            }

            // Los efectos que van a suertes: se sortean ANTES de recorrer nada, y los que no salen
            // se quedan fuera de esta resolución. A cast draws once for its blows and its rows.
            var descartados = yaSorteado ? new HashSet<SpellEffect>() : Sortear(effects);

            // Whether these rows are the critical list's: only when the cast was critical AND
            // this spell has one at this grade, which is what the f9 of the jxm says.
            bool deLaListaCritica = critical && SpellEffects.Criticos(spell, grade).Count > 0;

            // Whether this cast has already hooked the spell on its caster for a row of his.
            bool enganchadoAlLanzador = false;

            foreach (var filaLeida in effects)
            {
                if (descartados.Contains(filaLeida)) continue;
                var efecto = filaLeida;

                bool leToca = false;
                foreach (var d in efecto.Disparadores())
                {
                    if (string.Equals(d, trigger, StringComparison.OrdinalIgnoreCase)) { leToca = true; break; }
                }

                // ARMED, a monster spell's row that waits on a trigger: at the cast, on every
                // fighter its mask and zone name -- its conditions are read when it goes off -- to
                // go off on him alone. Klim's Carcassetagne lands on him and every monster, and
                // goes off at the start of each of THEIR turns; Kontatrás's confusion lands on
                // every player and goes off at each player's turn start.
                if (armaSusFilas && EsperaUnDisparador(efecto) && !EsFilaQueLeeElGolpe(efecto.EffectId))
                {
                    var armada = efecto.Copia();
                    armada.TargetMask = SinCondiciones(efecto.TargetMask);
                    foreach (var portador in AQuien(combat, caster, target, armada, aimedCell, estadosAlEmpezar, celdasAlEmpezar))
                    {
                        if (portador == null || !portador.IsAlive) continue;
                        fuera.Add(new Outcome
                        {
                            Sobre = portador, Caster = caster, Efecto = efecto,
                            HechizoOrigen = spell, NivelOrigen = grade,
                            EnganchePendiente = true, FilaArmada = true,
                        });
                    }
                    if (!leToca) continue;
                }
                // "-N de daños recibidos" under a damage kind is not something to fire: it is a
                // row that waits on the target for a blow of that kind, put at the cast and
                // read when the blow lands (Buffs.ReduccionDeDanoRecibido). Registered here,
                // on whoever the mask and the zone name, with its trigger kept as it is.
                if (!leToca && string.Equals(trigger, AlLanzar, StringComparison.OrdinalIgnoreCase)
                    && EsFilaQueLeeElGolpe(efecto.EffectId))
                {
                    foreach (var recipient in AQuien(combat, caster, target, efecto, aimedCell, estadosAlEmpezar, celdasAlEmpezar, soloAlObjetivo: soloAlObjetivo))
                    {
                        if (recipient == null || !recipient.IsAlive) continue;
                        var fila = recipient.Buffs.Poner(new Buff
                        {
                            EffectId = efecto.EffectId,
                            EffectUid = efecto.EffectUid,
                            MaxStacks = efecto.MaxStack,
                            Cuanto = DelDado(efecto.DiceNum, efecto.DiceSide, efecto.Value),
                            HechizoOrigen = spell,
                            NivelOrigen = grade,
                            Quien = caster.Id,
                            Disparador = efecto.Triggers,
                            CaducaEnRonda = Caduca(efecto, round),
                            EmpiezaEnRonda = Empieza(efecto, round),
                        }, combat.SiguienteEmbrujo);
                        fuera.Add(new Outcome
                        {
                            Sobre = recipient, Efecto = efecto, Buff = fila,
                            HechizoOrigen = spell, NivelOrigen = grade, FilaEnganchada = true,
                            Relevados = new List<Buff>(recipient.Buffs.Relevados),
                        });
                        recipient.Buffs.Relevados.Clear();
                    }
                    continue;
                }

                // A player spell's own row that waits on a trigger OF ITS CASTER -- mask C or c --
                // hooks the spell on him at the cast, although nothing it did stayed on him to
                // hook it by. Zarzas Agresivas' "1160 13554-8 under CMPAS on C" ("da 1 PM al
                // lanzador por enemigo alcanzado"), Golpe por Golpe's "1160 13152 under D on C",
                // Flecha Búmeran's "792 32434 under TE on C": each waits on the caster, and until
                // now was hooked on nobody, so it never went off.
                if (!leToca && depth == 0 && !enganchadoAlLanzador
                    && string.Equals(trigger, AlLanzar, StringComparison.OrdinalIgnoreCase)
                    && PlayerSpells.Contains(spell) && EsperaUnDisparador(efecto)
                    && !EsFilaQueLeeElGolpe(efecto.EffectId)
                    && efecto.TargetMask is "C" or "c")
                {
                    enganchadoAlLanzador = true;
                    fuera.Add(new Outcome
                    {
                        Sobre = caster, Caster = caster, Efecto = efecto,
                        HechizoOrigen = spell, NivelOrigen = grade,
                        EnganchePendiente = true,
                    });
                }

                if (!leToca) continue;

                // A hooked row with a delay waits: Furor's 28604 carries its "1160 under TE" with
                // a delay of one, and in the capture that row goes out with the round after the
                // cast as its activation (f12) -- put in round 19, it fires at the end of a turn
                // of round 20, when the spell was not cast again. Fired at the end of the very
                // turn of the cast, it took the +20 away the moment it was given.
                if (rondaDelEnganche >= 0 && efecto.Delay > 0
                    && !string.Equals(trigger, AlLanzar, StringComparison.OrdinalIgnoreCase)
                    && round < rondaDelEnganche + efecto.Delay)
                {
                    continue;
                }

                // Gone off, a triggered row does what it does NOW: its delay held the trigger
                // back, and is not a second wait. Klim's "952 under PPD, delay 2" is "not before
                // turn 3", not "two turns after the push".
                if (efecto.Delay > 0 && !string.Equals(trigger, AlLanzar, StringComparison.OrdinalIgnoreCase)
                    && !PlayerSpells.Contains(spell))
                {
                    efecto = efecto.Copia();
                    efecto.Delay = 0;
                }

                // And it never FIRES: the row registered at the cast is read by the blow. Fired
                // on the blow's own trigger it would put a second, unconditional row.
                if (EsFilaQueLeeElGolpe(efecto.EffectId)
                    && !string.Equals(trigger, AlLanzar, StringComparison.OrdinalIgnoreCase)) continue;

                // A push or a pull under a trigger is the SHEET's copy of a displacement the
                // spell really does through a sub-cast, and does not run. Remisión carries
                // "5 dice 6 trig DM mask a,A" next to its "1160 13430 trig DM", and 13430
                // holds the push that goes out -- to the attacker, through O; Fricción's "6
                // dice 2 trig DBE", Ojo por Ojo's and Palabra Turbulenta's sit next to their
                // 1160 the same way, and the two left (Maldición Movediza, Puño Meteoro) are
                // under D, which nothing fires yet. In the Remisión capture the real server
                // registers the 950 and the 1160 as hooked rows and the push as nothing, and
                // the bearer does not move when he is hit.
                if ((efecto.EffectId == EffectSupport.Push || efecto.EffectId == EffectSupport.Pull)
                    && !string.Equals(trigger, AlLanzar, StringComparison.OrdinalIgnoreCase)
                    && PlayerSpells.Contains(spell))
                {
                    continue;
                }

                // Root damage is sent by HurtAsync. Damage inside a hidden chained spell must be
                // materialized here or Aplicar will deliberately discard it as an ordinary row.
                if (depth > 0 && EsDeDano(efecto.EffectId))
                {
                    int element = efecto.Element >= 0
                        ? efecto.Element
                        : DatabaseManager.EffectElement(efecto.EffectId);
                    int spellBonus = caster.Buffs.DelHechizo(
                        spell, SpellAspect.DanoBase, round);

                    // A share of the blow that set it off goes out in that blow's element, as the
                    // family's elemental one, and in the name of whoever began the resolution --
                    // "jwe 1227" by the Yopuka in the Masacre capture, though the enemy carrying
                    // Masacre casts the spell that returns it; "jwe 1225" by the cómplice in the
                    // Xelor's, whose own hook it is.
                    var golpeador = caster;
                    var fila = efecto;
                    if (DevuelveElGolpe(efecto.EffectId))
                    {
                        if (element < 0) element = combat.ElementoDelDisparo;
                        int suyo = DelElemento(efecto.EffectId, element);
                        if (suyo != efecto.EffectId) fila = efecto.ComoEfecto(suyo);
                        golpeador = combat.RootCaster ?? caster;
                    }
                    foreach (var recipient in AQuien(
                                 combat, caster, target, efecto, aimedCell, estadosAlEmpezar, celdasAlEmpezar, soloAlObjetivo: soloAlObjetivo))
                    {
                        if (recipient == null || !recipient.IsAlive) continue;
                        fuera.Add(new Outcome
                        {
                            Sobre = recipient,
                            Caster = golpeador,
                            AnimationCaster = animationCaster ?? caster,
                            Efecto = fila,
                            HechizoOrigen = spell,
                            NivelOrigen = grade,
                            NestedDamage = true,
                            DamageElement = element,
                            DamageDistance = aimedCell >= 0
                                ? Jondo.Unity.World.Maps.MapGeometry.Distance(
                                    aimedCell, recipient.CellId)
                                : 0,
                            DamageSpellBonus = spellBonus,
                            CriticalDamage = critical,
                        });
                    }
                    continue;
                }

                // Fixed healing follows damage's roll semantics: one effect roll is shared by all
                // recipients in the zone, then each recipient gets its own distance falloff.
                int sharedHealRoll = EsCuraFija(efecto.EffectId)
                    ? (rollEffect != null ? rollEffect(efecto)
                                          : DelDado(efecto.DiceNum, efecto.DiceSide, efecto.Value))
                    : int.MinValue;

                // Lo que se pone EN EL SUELO no busca a nadie: va a la casilla, esté quien esté.
                //
                // Aquí se caían las balizas. El efecto 181 lleva máscara "a,A" y zona de punto, y
                // el motor buscaba un combatiente encima de la casilla apuntada para aplicárselo.
                // Pero una baliza se invoca justamente donde NO hay nadie: no había candidato, la
                // consecuencia no se creaba y no se pedía invocar nada. El paquete y el reenvío de
                // la lista estaban bien; lo que no llegaba era la orden.
                // Una bomba lanzada sobre casilla OCUPADA no se planta: estalla ahí mismo, y con
                // su otro hechizo. Aquí y no en quien invoca porque la decisión es del efecto:
                // según a dónde apunte, el mismo 1008 saca una bomba o no saca ninguna.
                int alObjetivo = Bombs.OnTarget(efecto.DiceNum);
                if (Invocaciones.Contains(efecto.EffectId) && alObjetivo != 0 && aimedCell >= 0)
                {
                    var quienEstaAhi = EnLaCasilla(combat, aimedCell);
                    if (quienEstaAhi != null)
                    {
                        if (depth >= HondoMaximo) continue;
                        fuera.AddRange(Resolver(combat, caster, alObjetivo,
                                                Math.Max(1, efecto.DiceSide),
                                                quienEstaAhi, AlLanzar, round, depth + 1,
                                                aimedCell, critico: false,
                                                nearestChainBudget: nearestChainBudget,
                                                bombasYaEstalladas: bombasYaEstalladas));
                        continue;
                    }
                }

                if (VaAlSuelo(efecto.EffectId))
                {
                    // Its starred conditions are the caster's, as for any row: an Éclat is only
                    // summoned by an escort that does not already have one (*e968).
                    if (!CasterQualifies(caster, efecto.TargetMask, estadosAlEmpezar)) continue;

                    // A summon on fixed cells, one on each, up to its value when it names one; a
                    // rune on its own. Elsewhere, the aimed cell.
                    bool fijas = efecto.Forma == FormaDeCeldasFijas && efecto.CeldasFijas.Count > 0;
                    var dondeVan = !fijas ? new List<int> { aimedCell }
                                 : EsInvocacion(efecto.EffectId)
                                     ? efecto.CeldasFijas.Take(efecto.Value > 0 && efecto.Value < 999 ? efecto.Value : int.MaxValue).ToList()
                                     : new List<int> { efecto.CeldasFijas[0] };
                    foreach (int alSuelo in dondeVan)
                    {
                        var puesta = Aplicar(combat, caster, caster, spell, grade, efecto,
                                             round, alSuelo, sharedHealRoll);
                        if (puesta != null) fuera.Add(puesta);
                    }
                    continue;
                }

                // "Teletransporta a la casilla objetivo" (4) moves THE CASTER, and is aimed at
                // a cell that has to be empty: nobody in the zone is ever the one who moves.
                // Its "a,A" -- 330 of its 470 rows, Paso de Cacería among them -- went looking
                // for somebody on the empty cell and found no one, so the Ocra kept his cell
                // and only the +1 MP of the spell went out. Measured in his capture: the jwe 4
                // carries the Ocra to the aimed cell three casts out of three. The starred
                // conditions on him are still honoured.
                if (efecto.EffectId == Teletransportar)
                {
                    // A ';' zone names where it goes: Belladona to her island (165), Nagate to hers.
                    int aDonde = efecto.Forma == FormaDeCeldasFijas && efecto.CeldasFijas.Count > 0
                        ? efecto.CeldasFijas[0] : aimedCell;
                    if (aDonde < 0) continue;
                    if (!CasterQualifies(caster, efecto.TargetMask, estadosAlEmpezar)) continue;

                    var saltado = Aplicar(combat, caster, caster, spell, grade, efecto, round,
                                          aDonde, sharedHealRoll);
                    if (saltado != null) fuera.Add(saltado);
                    continue;
                }

                // "Hasta la casilla objetivo" (783, 1043) is aimed at a cell that is usually
                // empty: the one moved is the first fighter on the caster's line, before the
                // cell for the push and past it for the pull. The mask is still honoured.
                if (efecto.EffectId == EffectSupport.PushToTargetCell
                    || efecto.EffectId == EffectSupport.PullToTargetCell)
                {
                    if (aimedCell < 0) continue;
                    int celda = Jondo.Unity.World.Maps.Zone.FirstCellOnTheLine(
                        caster.CellId, aimedCell, beyond: efecto.EffectId == EffectSupport.PullToTargetCell,
                        c => EnLaCasilla(combat, c) != null);
                    var enLaLinea = EnLaCasilla(combat, celda);
                    if (enLaLinea == null) continue;
                    if (!AQuien(combat, caster, enLaLinea, efecto, -1, estadosAlEmpezar).Contains(enLaLinea)) continue;

                    var movido = Aplicar(combat, caster, enLaLinea, spell, grade, efecto, round,
                                         aimedCell, sharedHealRoll);
                    if (movido != null) fuera.Add(movido);
                    continue;
                }

                // A throw (51) lands what the caster carries on the aimed cell: the carried one
                // is the target, whatever the cell holds.
                if (efecto.EffectId == EffectSupport.Throw)
                {
                    if (aimedCell < 0 || caster.Carrying == 0) continue;
                    var llevado = combat.Buscar(caster.Carrying);
                    if (llevado == null) continue;
                    if (!AQuien(combat, caster, llevado, efecto, -1, estadosAlEmpezar).Contains(llevado)) continue;

                    var lanzado = Aplicar(combat, caster, llevado, spell, grade, efecto, round,
                                          aimedCell, sharedHealRoll);
                    if (lanzado != null) fuera.Add(lanzado);
                    continue;
                }

                // Effect 2160 executes one spell on the nearest eligible target. This selection
                // intentionally uses live states: the same hidden spell has just marked its
                // current victim, and that mark is the loop guard for the next rebound.
                if (efecto.EffectId == NearestTargetExecuteSpell)
                {
                    if (nearestChainBudget <= 0) continue;
                    Fighter nearest = AQuien(combat, caster, target, efecto, aimedCell, soloAlObjetivo: soloAlObjetivo)
                        .Where(candidate => candidate != null && candidate.IsAlive)
                        .OrderBy(candidate => aimedCell >= 0
                            ? Jondo.Unity.World.Maps.MapGeometry.Distance(
                                aimedCell, candidate.CellId)
                            : 0)
                        .ThenBy(candidate => candidate.CellId)
                        .ThenBy(candidate => candidate.Id)
                        .FirstOrDefault();
                    if (nearest == null) continue;

                    var chained = Aplicar(combat, caster, nearest, spell, grade, efecto, round,
                                          nearest.CellId, sharedHealRoll);
                    if (chained == null) continue;
                    fuera.Add(chained);
                    if (chained.HechizoEncadenado != 0)
                    {
                        fuera.AddRange(Resolver(
                            combat, caster, chained.HechizoEncadenado,
                            chained.GradoEncadenado, nearest, AlLanzar, round,
                            depth, nearest.CellId, critical, nearestChainBudget - 1,
                            // Keep damage attribution on the Cra while drawing the next spell
                            // from the previous victim's cell.
                            target));
                    }
                    continue;
                }

                // «Activa una bomba»: la bomba apuntada lanza SU explosión, y la explosión trae
                // dentro todo lo demás —el daño de su elemento en círculo de radio dos, el 141
                // que la mata y otro 1009 que enciende a las bombas que pille dentro—.
                if (efecto.EffectId == ActivarBomba)
                {
                    if (depth >= HondoMaximo) continue;

                    // EL MURO PROPAGA. Encender una bomba enciende a las que estan unidas a ella
                    // por un muro, y a las de aquellas, y asi hasta donde llegue la cadena: «Si
                    // una bomba esta unida a otras por un muro y explota, hara explotar tambien a
                    // las otras bombas del muro», dice la ficha de clase. Por eso es una cola y no
                    // un bucle: cada bomba que estalla mete dentro a sus companeras de muro.
                    var cola = new Queue<Fighter>();
                    foreach (var apuntada in AQuien(combat, caster, target, efecto, aimedCell,
                                                    estadosAlEmpezar, celdasAlEmpezar, soloAlObjetivo: soloAlObjetivo))
                    {
                        if (apuntada != null && apuntada.IsAlive) cola.Enqueue(apuntada);
                    }

                    var enElCombate = Todos(combat).ToList();
                    while (cola.Count > 0)
                    {
                        var bomba = cola.Dequeue();
                        if (bomba == null || !bomba.IsAlive) continue;
                        int explosion = Bombs.Explosion(bomba.MonsterId);
                        if (explosion == 0) continue;

                        // UNA VEZ POR CADENA. Dos bombas dentro del radio de la otra se encienden
                        // mutuamente, y sin esto se cobrarían el daño una vez por rebote hasta
                        // agotar la profundidad.
                        if (!bombasYaEstalladas.Add(bomba.Id)) continue;

                        // La lanza ella, desde su casilla y en su propio grado. Medido en
                        // «tymador-detonador»: la bomba -5, invocada en grado 3, lanza el 13455
                        // en su nivel 41955, que es el grado 3.
                        fuera.AddRange(Resolver(combat, bomba, explosion,
                                                Math.Max(1, bomba.GradeIndex),
                                                bomba, AlLanzar, round, depth + 1,
                                                bomba.CellId, critico: false,
                                                nearestChainBudget: nearestChainBudget,
                                                bombasYaEstalladas: bombasYaEstalladas));

                        foreach (var companera in BombWalls.LasDelMismoMuro(enElCombate, bomba))
                        {
                            cola.Enqueue(companera);
                        }

                        // Y LAS QUE PILLE LA EXPLOSION, muro o no muro: «Cuando explota una
                        // bomba, si hay otras bombas del lanzador en la zona de explosion, estas
                        // explotaran tambien». Dos bombas pegadas NO hacen muro -- hace falta
                        // dejar dos casillas -- pero un circulo de radio dos se lleva por delante
                        // a la de al lado igualmente.
                        int radio = RadioDeLaExplosion(explosion, Math.Max(1, bomba.GradeIndex));
                        if (radio > 0)
                        {
                            foreach (var cerca in enElCombate)
                            {
                                if (cerca == null || !cerca.IsAlive || cerca == bomba) continue;
                                if (cerca.Invocador != bomba.Invocador) continue;
                                if (!Bombs.Is(cerca.MonsterId)) continue;
                                if (Jondo.Unity.World.Maps.MapGeometry.Distance(
                                        bomba.CellId, cerca.CellId) > radio) continue;
                                cola.Enqueue(cerca);
                            }
                        }
                    }
                    continue;
                }

                // La familia de «haz que se lance otro hechizo», los que no resolvía el camino
                // viejo. Un solo bloque para los seis, porque son la misma resolución con tres
                // parámetros: quién lanza, a qué apunta y cuántos candidatos coge.
                if (Familia.TryGetValue(efecto.EffectId, out var comoVa)
                    && !comoVa.YaLoHaceElCaminoViejo)
                {
                    if (efecto.DiceNum <= 0) continue;
                    if (depth >= HondoMaximo) continue;

                    var candidatos = new List<Fighter>();
                    foreach (var quien in AQuien(combat, caster, target, efecto, aimedCell,
                                                 estadosAlEmpezar, celdasAlEmpezar, soloAlObjetivo: soloAlObjetivo))
                    {
                        if (quien != null && quien.IsAlive) candidatos.Add(quien);
                    }

                    // El tope. Con value 0 o 999 no hay tope —así van el 792, el 1160 y el 2794,
                    // que llegan hasta once hijos en las capturas—; con un número pequeño sí, y
                    // se respeta: el 2160 saca uno en 251 de 251 y el 2793 con value 6 nunca pasa
                    // de seis. Es la mejor explicación de por qué el 792 y el 2792 conviven con
                    // la misma máscara cambiando sólo este campo, pero NO está demostrado: las
                    // diez ejecuciones de 2792 del corpus tuvieron un solo candidato.
                    if (comoVa.TopePorValor && efecto.Value > 0 && efecto.Value < 999
                        && candidatos.Count > efecto.Value)
                    {
                        candidatos.RemoveRange(efecto.Value, candidatos.Count - efecto.Value);
                    }

                    if (comoVa.Apunta == Apunta.AlMasCercano && candidatos.Count > 1)
                    {
                        candidatos.Sort((a, b) =>
                        {
                            int da = aimedCell >= 0
                                ? Jondo.Unity.World.Maps.MapGeometry.Distance(aimedCell, a.CellId) : 0;
                            int db = aimedCell >= 0
                                ? Jondo.Unity.World.Maps.MapGeometry.Distance(aimedCell, b.CellId) : 0;
                            return da != db ? da.CompareTo(db) : a.Id.CompareTo(b.Id);
                        });
                        candidatos.RemoveRange(1, candidatos.Count - 1);
                    }

                    foreach (var candidato in candidatos)
                    {
                        // Quién lanza el hijo. En el 1017, el 2792 y sus primos es el CANDIDATO,
                        // y eso no es cosmético: las máscaras del hijo se resuelven contra él.
                        // Medido con la lanza del Forjalanza, cuyo hijo lleva un «mata al
                        // objetivo» con máscara C y acaba matando a la propia lanza.
                        var origen = combat.TriggeringAttacker != null && combat.TriggeringAttacker.IsAlive
                            ? combat.TriggeringAttacker
                            : caster;
                        var lanzaElHijo = comoVa.LanzaElOrigen ? origen
                                        : comoVa.LanzaElCandidato ? candidato
                                        : caster;

                        // Y a qué apunta.
                        Fighter aQuien;
                        int aQueCasilla;
                        switch (comoVa.Apunta)
                        {
                            case Apunta.AlOrigen:
                                aQuien = origen;
                                aQueCasilla = origen.CellId;
                                break;

                            case Apunta.AlLanzadorPadre:
                                aQuien = caster;
                                aQueCasilla = caster.CellId;
                                break;

                            case Apunta.ALaCasillaDelPadre:
                                // La casilla del padre, y el objetivo se vuelve a resolver AHORA:
                                // hay hechizos que apuntan a casilla vacía y plantan ahí la
                                // invocación que el hijo tiene que alcanzar. Congelar el objetivo
                                // los dejaría sin hacer nada, en silencio.
                                aQueCasilla = aimedCell >= 0 ? aimedCell : candidato.CellId;
                                aQuien = EnLaCasilla(combat, aQueCasilla);
                                break;

                            default:
                                aQuien = candidato;
                                aQueCasilla = candidato.CellId;
                                break;
                        }

                        // A child with a delay waits, like any row with one: Conde Kontatrás's
                        // Alternancia casts Medio Tiempo with a delay of one and Medio Tiempo
                        // casts Alternancia back the same way, which is how his odd and even
                        // turns take turns. Cast at once, the two called each other in the same
                        // instant until the chain ran out of depth.
                        if (efecto.Delay > 0 && string.Equals(trigger, AlLanzar, StringComparison.OrdinalIgnoreCase))
                        {
                            fuera.Add(Pendiente(combat, lanzaElHijo, aQuien ?? candidato, spell, grade, efecto, round));
                            continue;
                        }

                        // A critical cast runs its chain on the critical lists: Virtud's
                        // critical shield is 29723's own critical row, 550% for 1100.
                        fuera.AddRange(Resolver(combat, lanzaElHijo, efecto.DiceNum,
                                                Math.Max(1, efecto.DiceSide),
                                                aQuien, AlLanzar, round, depth + 1,
                                                aQueCasilla, critico: critical,
                                                nearestChainBudget: nearestChainBudget,
                                                bombasYaEstalladas: bombasYaEstalladas));
                        var enganche = MarcaDeEnganche(lanzaElHijo, aQuien ?? candidato, efecto);
                        if (enganche != null) fuera.Add(enganche);
                    }
                    continue;
                }

                // A steal is two rows per target: the characteristic's malus on him and its bonus
                // on the caster, both with the steal's uid (see Steals).
                if (Steals.Of(efecto.EffectId) is { } robo)
                {
                    foreach (var sobre in AQuien(combat, caster, target, efecto, aimedCell,
                                                 estadosAlEmpezar, celdasAlEmpezar, soloAlObjetivo: soloAlObjetivo))
                    {
                        fuera.AddRange(Robar(combat, caster, sobre, spell, grade, efecto, round, robo));
                    }
                    continue;
                }

                // "Retrocede" y "Avanza" mueven al QUE LANZA, y la máscara es una condición sobre
                // el objetivo, no una lista de destinatarios: se cumple o no se cumple, y el
                // desplazamiento ocurre UNA VEZ. Si se aplicara por candidato, un hechizo que
                // alcanzara a tres bichos movería al lanzador tres veces.
                bool unaSolaVez = efecto.EffectId == Retroceder || efecto.EffectId == Avanzar;

                foreach (var sobre in AQuien(combat, caster, target, efecto, aimedCell,
                                             estadosAlEmpezar, celdasAlEmpezar, soloAlObjetivo: soloAlObjetivo))
                {
                    var hecho = Aplicar(combat, caster, sobre, spell, grade, efecto, round,
                                        aimedCell, sharedHealRoll);
                    if (unaSolaVez)
                    {
                        if (hecho != null) fuera.Add(hecho);
                        break;
                    }
                    if (hecho == null) continue;
                    fuera.Add(hecho);

                    // Un efecto puede encadenar otro hechizo: es como se enganchan las actitudes.
                    //
                    // WHO casts the child and WHERE it is aimed come from the table above, the
                    // same way the new path reads it: a 792 is cast BY THE TARGET at itself, a
                    // 1160 by the parent caster AT the target. This used to hand every child to
                    // the parent caster at the parent's aimed cell, and that is how Mosquete's
                    // combo never reached the bomb: 20643 chained 20497 with the Tymador as its
                    // caster and "C" landed on him, not on the bomb; and 20643 itself was judged
                    // on the cell aimed at instead of the bomb's. Measured on the Mosquete
                    // capture: "jwe 300 f3=-16" -- the bomb -- casts 20497 on cell 274, its own.
                    if (hecho.HechizoEncadenado != 0)
                    {
                        // A monster's 792, 1160, 2160 with a delay wait like the rest of the family:
                        // Conflicto Eterno's imp comes back two turns later, not at once.
                        if (efecto.Delay > 0 && string.Equals(trigger, AlLanzar, StringComparison.OrdinalIgnoreCase)
                            && !PlayerSpells.Contains(spell))
                        {
                            var quienLoLanzaraLuego = Familia.TryGetValue(efecto.EffectId, out var comoEsperara)
                                                      && comoEsperara.LanzaElCandidato ? sobre : caster;
                            fuera.Add(Pendiente(combat, quienLoLanzaraLuego, sobre, spell, grade, efecto, round));
                            continue;
                        }

                        var lanzaElHijo = caster;
                        int casillaDelHijo = sobre.CellId;
                        if (Familia.TryGetValue(efecto.EffectId, out var comoEncadena))
                        {
                            if (comoEncadena.LanzaElCandidato) lanzaElHijo = sobre;
                            casillaDelHijo = comoEncadena.Apunta switch
                            {
                                Apunta.AlLanzadorPadre => caster.CellId,
                                Apunta.ALaCasillaDelPadre => aimedCell >= 0 ? aimedCell : sobre.CellId,
                                _ => sobre.CellId,
                            };
                        }
                        fuera.AddRange(Resolver(combat, lanzaElHijo, hecho.HechizoEncadenado,
                                                hecho.GradoEncadenado, sobre, AlLanzar, round,
                                                depth + 1, casillaDelHijo, critical,
                                                nearestChainBudget,
                                                bombasYaEstalladas: bombasYaEstalladas));
                        var enganche = MarcaDeEnganche(lanzaElHijo, sobre,
                            new SpellEffect { EffectId = efecto.EffectId, DiceNum = hecho.HechizoEncadenado,
                                              DiceSide = hecho.GradoEncadenado, Triggers = AlLanzar });
                        if (enganche != null) fuera.Add(enganche);
                    }
                }
            }
            // The rows of THIS spell and grade carry the flag; a chained spell's rows were
            // flagged by its own resolution, on its own list.
            if (deLaListaCritica)
            {
                foreach (var o in fuera)
                {
                    if (o.HechizoOrigen == spell && o.NivelOrigen == grade) o.Critico = true;
                }
            }
            // Every outcome of this spell was cast by this caster, whether the branch that made it
            // said so or not: a child cast by its candidate -- Hell Mina's players, through a 792 --
            // was hooked in the name of the root caster, and its triggers fired as hers.
            foreach (var o in fuera)
            {
                if (o.Caster == null && o.HechizoOrigen == spell && o.NivelOrigen == grade) o.Caster = caster;
                // And aimed at this cell, the one its announcement names.
                if (o.CeldaDelLanzamiento < 0 && o.HechizoOrigen == spell && o.NivelOrigen == grade) o.CeldaDelLanzamiento = aimedCell;
            }
            combat.Telefrags = telefragsDeFuera;
            combat.TeleportsFallidos = fallidosDeFuera;
            combat.CarriedAtCast = cargadoDeFuera;
            return fuera;
        }

        /// <summary>
        /// The starred tokens of a mask, which are conditions on the CASTER and not on the
        /// target. Read off Pinzas: the pick-up carries "*e3" and the throw "*E3", and 3 is the
        /// state of the one carrying -- the bomb being picked up is not, the bot throwing it
        /// is. The catalogue stars states (10,121 tokens), monster templates (1,163) and life
        /// thresholds (197); the handful of other letters starred (h, m, i, d, j, B, l, s, Q)
        /// are not read and let the effect through.
        /// </summary>
        /// <remarks>
        /// Against the same snapshot as the targets' states when one is given: judged live, the
        /// throw of Pinzas would fire in the very cast that picked the bomb up.
        /// </remarks>
        internal static bool CasterQualifies(Fighter quienLanza, string mascara,
                                             IReadOnlyDictionary<Fighter, HashSet<int>> estados = null)
        {
            if (string.IsNullOrEmpty(mascara) || mascara.IndexOf('*') < 0) return true;
            IReadOnlySet<int> delLanzador = estados != null && estados.TryGetValue(quienLanza, out var alEmpezar)
                ? alEmpezar
                : quienLanza.Buffs.Estados as IReadOnlySet<int> ?? new HashSet<int>(quienLanza.Buffs.Estados);

            foreach (var trozo in mascara.Split(','))
            {
                string t = trozo.Trim();
                if (t.Length < 3 || t[0] != '*') continue;
                char letra = t[1];
                if (!int.TryParse(t.Substring(2), out int numero)) continue;
                switch (letra)
                {
                    case 'E': if (!delLanzador.Contains(numero)) return false; break;
                    case 'e': if (delLanzador.Contains(numero)) return false; break;
                    case 'F': if (!quienLanza.IsMonster || quienLanza.MonsterId != numero) return false; break;
                    case 'f': if (quienLanza.IsMonster && quienLanza.MonsterId == numero) return false; break;
                    case 'V': if (!LifeUnder(quienLanza, numero)) return false; break;
                    case 'v': if (LifeUnder(quienLanza, numero)) return false; break;
                }
            }
            return true;
        }

        /// <summary>
        /// A quién le toca un efecto, según su máscara.
        ///
        ///   C          a quien lo lanza
        ///   c          a quien lo lanza, si está en la zona
        ///   a, A       los del propio bando -- el lanzador incluido -- y los de enfrente
        ///   g          los del propio bando sin el lanzador
        ///   i, I  j, J las invocaciones, del propio bando y de enfrente
        ///   l, L       los jugadores, del propio bando y de enfrente
        ///   m, M       los monstruos que no son invocación, ídem
        ///   P, p       (sobre una invocación) del lanzador, de otro
        ///   h          el invocador del lanzador
        ///   O          the one whose blow set the spell off, wherever he stands
        ///   e&lt;N&gt;      sólo si NO lleva el estado N
        ///   E&lt;N&gt;      sólo si SÍ lo lleva
        ///   F&lt;N&gt; f&lt;N&gt;  sólo si es, o no es, el monstruo N
        ///   V&lt;N&gt; v&lt;N&gt;  sólo con menos, o no menos, del N % de vida
        ///   H          the enemy characters, as L
        ///   T, W, U    the telefragged, the teleport that found no cell, the one just summoned
        ///   B&lt;N&gt; b&lt;N&gt;  a character of class N, or anybody who is not one
        ///   PB, pb     with shield points, or without
        ///   R, r       the cast went through a portal, or did not
        ///   K          the one the caster carried as the spell began
        ///   o, u       the one who set the spell off -- u the summon whose coming did -- in the zone
        ///   D, d       the companions, which this build does not field; x an empty cell
        ///
        /// A letter this engine does not read is let through, never fallen back on the target.
        /// </summary>
        private static IEnumerable<Fighter> AQuien(FightInstance combate, Fighter quienLanza,
                                                   Fighter objetivo, SpellEffect efecto,
                                                   int celdaApuntada = -1,
                                                   IReadOnlyDictionary<Fighter, HashSet<int>> estados = null,
                                                   IReadOnlyDictionary<Fighter, int> celdas = null,
                                                   bool soloAlObjetivo = false)
        {
            var mascara = efecto.TargetMask ?? "";
            bool alLanzador = false, aLosMios = false, aLosDeEnfrente = false, aLosOtrosAliados = false;
            bool ownSummonsOnly = false, notOwnSummons = false, alInvocador = false;
            bool alAtacante = false;
            bool soloTelefragueados = false, soloTeleportFallido = false, soloLoInvocado = false;

            // The kinds of fighter a letter names, by side: the lower case is the caster's side
            // and the upper case the other one. c is the caster himself, when he stands in the
            // zone.
            bool alLanzadorEnLaZona = false;
            bool aInvocacionesAliadas = false, aInvocacionesEnemigas = false;
            bool aJugadoresAliados = false, aJugadoresEnemigos = false;
            bool aMonstruosAliados = false, aMonstruosEnemigos = false;
            var pideEstado = new List<int>();
            var pideNoEstado = new List<int>();
            var requiredMonsterTemplates = new List<int>();
            var excludedMonsterTemplates = new List<int>();
            var lifeBelow = new List<int>();       // V<n>: life strictly under n% of the maximum
            var lifeNotBelow = new List<int>();    // v<n>: life at n% or above
            var requiredBreeds = new List<int>();  // B<n>: a character of that class
            var excludedBreeds = new List<int>();  // b<n>: anybody who is not one
            bool withShield = false, withoutShield = false;
            bool throughPortal = false, notThroughPortal = false;
            bool theCarried = false, theAttackerInTheZone = false;

            foreach (var trozo in mascara.Split(','))
            {
                string t = trozo.Trim();
                if (t.Length == 0) continue;

                // A star puts the condition on the CASTER instead of on the target; those are
                // judged apart, in CasterQualifies.
                if (t[0] == '*') continue;
                if (t == "C") { alLanzador = true; continue; }

                // PB / pb: the target holds shield points, or holds none. Flecha Percutiente
                // writes its two dice so -- 31-34 on "A,pb", 35-39 on "A,PB" -- and its sheet
                // says why: "los daños ... son mayores en los objetivos que tienen escudo".
                if (t == "PB") { withShield = true; continue; }
                if (t == "pb") { withoutShield = true; continue; }

                // R / r: the cast went through a portal, or did not. The Selatrop's sheets say it
                // row by row -- Afrenta "aumenta también el alcance del lanzador si el hechizo se
                // proyecta por un portal" on "C,R", Audacia's step forward "no se aplica si el
                // hechizo se proyecta a través de un portal" on "a,A,r". What projects a cast is
                // the portal network; the fight says whether this one went through it.
                if (t == "R") { throughPortal = true; continue; }
                if (t == "r") { notThroughPortal = true; continue; }

                // K: the one the caster carries -- carried when the spell began, wherever he is
                // when the row lands. Every Pandawa throw names him so: Aguardiente "lanza al
                // objetivo, cura a los aliados y ocasiona daños ... en zona" heals "a,K" and hits
                // "A,K" next to its zone rows, and Vértigo "reduce los daños finales ... de la
                // entidad portada" on "l,m,K".
                if (t == "K") { theCarried = true; continue; }

                // o: whoever set the spell off, like O, but only when the zone covers him.
                // Escudo Elemental "aplica un estado elemental sobre el atacante" through a 1160 on
                // "A,o" in a circle of 63. Inferred from that sheet: no capture shows the two
                // letters apart.
                if (t == "o") { theAttackerInTheZone = true; continue; }

                // u: the summon whose coming set the spell off -- the fight's CI hands it over as
                // the one who set it off. Pacto Bestial's and Caja de Herramientas' rows under CI
                // name it next to "j,P" and "i,P" on the whole map: the new one of the caster's
                // summons, which their sheets say they reach ("presentes y futuras"). Inferred.
                if (t == "u") { theAttackerInTheZone = true; continue; }

                // x: an empty cell. It rides on effects that go to the ground -- Palabra
                // Alquímica's "181 on x" puts its flask there -- and those never ask this method
                // for anybody. Inferred: the two class rows that write it are a summon and a
                // teleport, both aimed at a free cell.
                if (t == "x") continue;

                // D / d: the companions, the controlled fighters that are neither characters nor
                // monsters nor summons. Terraplenado splits its blows between "h,m,d,H,M,D" and
                // "j,J" -- "los daños son mayores sobre las invocaciones" -- so d is no summon;
                // "canEndFight" and the challenge validations cast on "h,d"; Pekegüino's Consuelo
                // counts "cada luchador aliado" on "h,m,d". Inferred. This build fields no
                // companions, so the letters name nobody.
                if (t == "D" || t == "d") continue;

                // B<n> / b<n>: a character of class n, or anybody who is not one -- the case rule
                // of F/f and E/e. Disparos Lejanos gives range on "g,b9" and its sheet says to
                // whom: "a los aliados (excepto ocras)"; Traición casts one spell per enemy class,
                // on "A,B16", "A,B6", "A,B7"...
                if (t.Length > 1 && (t[0] == 'B' || t[0] == 'b') && int.TryParse(t.Substring(1), out int breed))
                {
                    if (t[0] == 'B') requiredBreeds.Add(breed); else excludedBreeds.Add(breed);
                    continue;
                }

                // LA MINÚSCULA Y LA MAYÚSCULA NO SON LO MISMO: la "a" son los del propio bando y
                // la "A" los de enfrente. Estaban las dos en el mismo cubo, y eso hacía cosas
                // absurdas. Tiro de Repliegue, por ejemplo, lleva un 1041 "Retrocede" con máscara
                // "A" y un 1042 "Avanza" con "a": al no distinguirlas se cumplían las dos, el
                // lanzador se movía dos casillas atrás y otras dos adelante, y el desplazamiento
                // neto era cero. En el registro se veía tal cual, ida y vuelta a la misma casilla.
                //
                // Se sostiene en toda la base: los efectos de daño 96-100 llevan sólo "A" o "a,A"
                // —más de seis mil— y la curación 108 lleva "a", "C" o "g".
                if (t == "a") { aLosMios = true; continue; }
                if (t == "A") { aLosDeEnfrente = true; continue; }

                // g: THE OTHER ALLIES -- the caster's side without the caster. It was read as
                // "the allied summons", and the Baliza de Supervivencia's "cura a todos los
                // aliados" healed no Ocra. The catalogue says allies: of the 74 class spells
                // whose sheet describes a bare "g", 62 say "aliados" and one "invocación" --
                // "da 1 PA a los aliados en contacto", "cura a sus aliados alineados con él" --
                // and the spells that "no afectan al lanzador" write "g,A" where the ones that
                // do write "a,A".
                if (t == "g") { aLosOtrosAliados = true; continue; }

                // Multiple uppercase F entries are alternatives. Fulminating Arrow uses this to
                // mark either Cra beacon template while excluding ordinary allies.
                if (t.Length > 1 && t[0] == 'F' &&
                    int.TryParse(t.Substring(1), out int monsterTemplate))
                {
                    requiredMonsterTemplates.Add(monsterTemplate);
                    continue;
                }

                // f<n> is the exclusion, the same case rule as E/e and V/v. Patada's second
                // push -- "A,f3112,f3113,f3114,f5161,f5163,f5162,g", the enemies and the allied
                // summons that are NOT bombs -- was reaching the bombs too, so a bomb got the
                // one-cell push on top of its own ring push.
                if (t.Length > 1 && t[0] == 'f' &&
                    int.TryParse(t.Substring(1), out int excludedTemplate))
                {
                    excludedMonsterTemplates.Add(excludedTemplate);
                    continue;
                }

                // P: a summon has to be THE CASTER'S; p: it has to be somebody else's. Neither
                // letter says anything about a fighter who is not a summon. The whole Tymador
                // kit is written on it -- "a,P,F3112,..." is "las bombas del lanzador" in Patada,
                // Kabúm, Mosquete and Polvo, and "a,A,p,F3112,..." is everybody else's bombs,
                // pushed one cell -- and three cases pin the reading down:
                //
                //   Patada       the caster's bomb takes the ring push of three and NOT the
                //                one-cell push of "a,A,p,F3112": p keeps his own out
                //   Chute        "una invocación del lanzador" is "i,P": a summon, and his
                //   Encendimiento "h,P" lands the +1 AP cost on the bomb's OWNER, who is no
                //                summon at all: P lets him through
                //
                // So it is a condition on summons, not a category of its own.
                if (t == "P") { ownSummonsOnly = true; continue; }
                if (t == "p") { notOwnSummons = true; continue; }

                // h: the caster's summoner, which is how a bomb's Encendimiento reaches the
                // Tymador -- the capture's jxm of effect 296 is cast by bomb -9 on the player.
                if (t == "h") { alInvocador = true; continue; }

                // THE KINDS, IN TWO CASES. The catalogue pairs a lower-case letter with an
                // upper-case one and the sheets say which side each is:
                //
                //   i / I   the summons. "i,P" is "una invocación del lanzador" (Chute), and
                //           Látigo's "aumenta los PM del objetivo si es una invocación aliada"
                //           carries a bare "i": the lower case is the caster's side. It was
                //           read as both sides, which P happened to narrow in every measured
                //           case.
                //   j / J   also summons: Concentración pays its "daños mayores sobre las
                //           invocaciones" on "J,j", Coraza halves its shield "en las
                //           invocaciones" on "j", Desinvocación "dobla los daños con las
                //           invocaciones" on "J,...,j". What tells j from i is not written
                //           anywhere in the sheets, so they are read alike.
                //   l / L   the players. Caja de Herramientas casts on "l" what it does "al
                //           aliado objetivo" and his summons, Ghulificación puts its state on
                //           "L", and the damage rows that are NOT for summons -- Concentración's
                //           "L,M,l,m,c", Obsolescencia's "l,m,L,M" -- name them next to the
                //           monsters.
                //   m / M   the monsters that are nobody's summon, the other half of those rows.
                //   c       the caster, only when he stands in the zone -- Acumulación's "en el
                //           lanzador" and Vitalidad's "la vitalidad es mayor en el lanzador"
                //           are rows with "c" that the caster gets by casting on himself, and
                //           Flecha Asaltante's "950 mask c" lands on the Ocra in its capture
                //           when he is one cell from the aimed cell, inside its Q1. C reaches
                //           him wherever he is.
                //
                // H, D and their lower cases go with these in the monster spells ("H,M,D",
                // "h,m,d") and are not read: what tells H from L is not written down.
                if (t == "i" || t == "j") { aInvocacionesAliadas = true; continue; }
                if (t == "I" || t == "J") { aInvocacionesEnemigas = true; continue; }
                if (t == "l") { aJugadoresAliados = true; continue; }
                if (t == "L") { aJugadoresEnemigos = true; continue; }

                // H: the enemy CHARACTERS, the monster spells' name for what the class spells
                // write L -- "H,M,D" is characters, monsters and summons, the three kinds of the
                // other side. Hell Mina hands her elements out on a bare "H". The lower case is not
                // read so: h is the caster's summoner, on which the Rogue's bombs stand.
                if (t == "H") { aJugadoresEnemigos = true; continue; }
                if (t == "m") { aMonstruosAliados = true; continue; }
                if (t == "M") { aMonstruosEnemigos = true; continue; }
                if (t == "c") { alLanzadorEnLaZona = true; continue; }

                // O: whoever dealt the blow that set the spell off, wherever he stands, and
                // NOBODY ELSE -- the other letters then say which sides and states of his the
                // effect takes. Read off Remisión's capture: its push is "a,A,O,e3795" on a
                // spell cast at the bearer's own cell, and the one pushed is the Tymador who
                // had just hit him in melee from a cell the zone never touches, the bearer
                // himself untouched. And off Cutícula Repulsiva, whose own pushes carry
                // "a,A,O" under the instant trigger: with nobody attacking at the cast, they
                // push nobody.
                if (t == "O") { alAtacante = true; continue; }

                // T: the ones this spell has just telefragged -- swapped by one of its teleports.
                // Krosmoglob's Finta says it in words: "aumenta los PA de Krosmoglob y de un aliado
                // si han intercambiado su posición mediante una teletransportación", on "a,T".
                // W: the ones a teleport of this spell could not land -- the mirror cell off the
                // board. It is what makes Kontatrás's clock do what the guide says, "if a required
                // symmetric cell does not exist, the fight ends with all characters dead": Época's
                // kill on "A,F3416,W" and Área Temporal's on "A,W". No sheet spells it out.
                if (t == "T") { soloTelefragueados = true; continue; }
                if (t == "W") { soloTeleportFallido = true; continue; }

                // U: the fighter this very cast summoned or brought back -- 192 of the 201 levels
                // that write it also summon or revive. It is applied by the fight to the one that
                // came out (FightHandler), never here: read here, "a,U" became "a" and Tal Kasha's
                // "-1 MP on a,U" drained every fighter on the board each turn.
                if (t == "U") { soloLoInvocado = true; continue; }

                if (t.Length > 1 && (t[0] == 'e' || t[0] == 'E') && int.TryParse(t.Substring(1), out int estado))
                {
                    if (t[0] == 'E') pideEstado.Add(estado); else pideNoEstado.Add(estado);
                    continue;
                }

                // LIFE THRESHOLDS. 1,100 uses across the catalogue and, until now, silently
                // ignored -- and an ignored condition is a condition met. That is how the Silver
                // Dofus healed the Ocra to full at the start of EVERY turn: its trigger is
                // "C,V20", the sheet says "cuando el portador tiene menos de un 20% de vida",
                // and the engine never looked.
                //
                // The letter follows the same case rule as F/f and E/e, measured on Ataque Mortal
                // ("danos mayores en objetivos con menos del 50%"): the big die, 54-60, carries
                // V50 and the small one, 43-48, carries v50. So V<n> is life UNDER n percent and
                // v<n> is the complement.
                if (t.Length > 1 && (t[0] == 'V' || t[0] == 'v') && int.TryParse(t.Substring(1), out int pct))
                {
                    if (t[0] == 'V') lifeBelow.Add(pct); else lifeNotBelow.Add(pct);
                }
            }

            // Against the same snapshot as the targets' states: Pinzas carries the pick-up and
            // the throw in one grade, "*e3" and "*E3", and judged live the throw would fire in
            // the very cast that picked the bomb up.
            if (!CasterQualifies(quienLanza, mascara, estados)) yield break;
            if (soloLoInvocado && !soloAlObjetivo) yield break;
            if (throughPortal && !combate.CastThroughPortal) yield break;
            if (notThroughPortal && combate.CastThroughPortal) yield break;

            var candidatos = new List<Fighter>();

            // K: the one the caster carried as the spell began. Like O, a named fighter: the
            // sides of the mask say whether the row takes him, and the zone is not consulted.
            if (theCarried)
            {
                long carried = combate.CarriedAtCast.Caster == quienLanza.Id && combate.CarriedAtCast.Carried != 0
                    ? combate.CarriedAtCast.Carried
                    : quienLanza.Carrying;
                var llevado = carried != 0 ? combate.Buscar(carried) : null;
                if (llevado == null || !llevado.IsAlive) yield break;
                bool deLosSuyos = llevado.TeamId == quienLanza.TeamId;
                bool algunLado = aLosMios || aLosDeEnfrente || aLosOtrosAliados;
                bool leVale = !algunLado
                           || (aLosMios && deLosSuyos) || (aLosDeEnfrente && !deLosSuyos)
                           || (aLosOtrosAliados && deLosSuyos && llevado != quienLanza);
                if (!leVale) yield break;
                candidatos.Add(llevado);
                aLosMios = aLosDeEnfrente = aLosOtrosAliados = alLanzador = alInvocador = false;
                alLanzadorEnLaZona = aInvocacionesAliadas = aInvocacionesEnemigas = false;
                aJugadoresAliados = aJugadoresEnemigos = aMonstruosAliados = aMonstruosEnemigos = false;
            }

            // An ARMED row going off: it is the bearer's -- or, with an O, the one who set it
            // off's -- and nobody else's. Its sides and its zone were read when it was armed.
            if (soloAlObjetivo)
            {
                var unico = alAtacante || theAttackerInTheZone ? combate.TriggeringAttacker : objetivo;
                if (unico != null && unico.IsAlive) candidatos.Add(unico);
                aLosMios = aLosDeEnfrente = aLosOtrosAliados = alLanzador = alInvocador = alAtacante = false;
                alLanzadorEnLaZona = aInvocacionesAliadas = aInvocacionesEnemigas = false;
                aJugadoresAliados = aJugadoresEnemigos = aMonstruosAliados = aMonstruosEnemigos = false;
            }

            // With an O the candidate is the attacker and nobody else; without an attacker at
            // hand -- the cast itself, a turn trigger -- the effect touches no one.
            if (alAtacante)
            {
                var atacante = combate.TriggeringAttacker;
                if (atacante == null || !atacante.IsAlive) yield break;
                bool deLosSuyos = atacante.TeamId == quienLanza.TeamId;
                // "h,O": the attacker, when he is the caster's summoner. Dispersión, the Xelor's
                // cómplice, returns a blow only "si su atacante es su invocador".
                bool leVale = (aLosMios && deLosSuyos) || (aLosDeEnfrente && !deLosSuyos)
                           || (alLanzador && atacante == quienLanza)
                           || (aLosOtrosAliados && deLosSuyos && atacante != quienLanza)
                           || (alInvocador && quienLanza.EsInvocado && atacante.Id == quienLanza.Invocador)
                           || EsDeLaClase(atacante, deLosSuyos);
                if (!leVale) yield break;
                candidatos.Add(atacante);
                aLosMios = aLosDeEnfrente = aLosOtrosAliados = alLanzador = alInvocador = false;
                alLanzadorEnLaZona = aInvocacionesAliadas = aInvocacionesEnemigas = false;
                aJugadoresAliados = aJugadoresEnemigos = aMonstruosAliados = aMonstruosEnemigos = false;
            }

            // Whether one of the kind letters names this fighter, on his side.
            bool EsDeLaClase(Fighter quien, bool suyo)
            {
                bool esInvocado = quien.EsInvocado;
                bool esMonstruo = quien.IsMonster && !esInvocado;
                bool esJugador = !quien.IsMonster && !esInvocado;
                return (alLanzadorEnLaZona && quien == quienLanza)
                    || (aInvocacionesAliadas && esInvocado && suyo)
                    || (aInvocacionesEnemigas && esInvocado && !suyo)
                    || (aJugadoresAliados && esJugador && suyo)
                    || (aJugadoresEnemigos && esJugador && !suyo)
                    || (aMonstruosAliados && esMonstruo && suyo && quien != quienLanza)
                    || (aMonstruosEnemigos && esMonstruo && !suyo);
            }

            bool algunaClase = alLanzadorEnLaZona || aInvocacionesAliadas || aInvocacionesEnemigas
                            || aJugadoresAliados || aJugadoresEnemigos
                            || aMonstruosAliados || aMonstruosEnemigos;

            if (alLanzador) candidatos.Add(quienLanza);
            if (alInvocador && quienLanza.EsInvocado)
            {
                var invocador = combate.Buscar(quienLanza.Invocador);
                if (invocador != null && invocador.IsAlive && !candidatos.Contains(invocador)) candidatos.Add(invocador);
            }

            if (!soloAlObjetivo && (aLosMios || aLosDeEnfrente || aLosOtrosAliados || algunaClase))
            {
                // La zona: el efecto dice de qué FORMA coge el terreno alrededor de la casilla
                // apuntada —un punto, un círculo de radio dos, una cruz— y le toca a todo el que
                // esté encima Y cumpla la máscara.
                // The telefragged are named by the telefrag, not by the ground: they are the ones
                // who swapped, wherever the swap left them. Read off Reloj de Bolsillo, whose
                // "+100 damage" on "a,T" goes, in the guide, to the character the Count swapped
                // with -- standing by then on the cell the Count left, not where the zone looked.
                var enJuego = soloTelefragueados
                    ? combate.Telefrags.Keys.Select(combate.Buscar).Where(f => f != null && f.IsAlive).ToList()
                    : soloTeleportFallido
                        ? combate.TeleportsFallidos.Select(combate.Buscar).Where(f => f != null && f.IsAlive).ToList()
                        : EnLaZona(combate, quienLanza, objetivo, efecto, celdaApuntada, celdas);
                foreach (var quien in enJuego)
                {
                    bool suyo = quien.TeamId == quienLanza.TeamId;

                    bool leToca = (aLosMios && suyo)
                               || (aLosDeEnfrente && !suyo)
                               || (aLosOtrosAliados && suyo && quien != quienLanza)
                               || EsDeLaClase(quien, suyo);
                    if (!leToca) continue;

                    if (!candidatos.Contains(quien)) candidatos.Add(quien);
                }

                // The caster IS one of his own allies: "a" reaches him when he stands in the
                // zone. It used to take him out -- "para eso está la C" -- and that is what
                // left Kabúm without its state: cast at his own cell, its "a" of a cross of two
                // is meant for him first. Measured in the Kabúm capture: the 950 with state 92
                // lands on the caster, cast at 301 with him on 301. The catalogue says the
                // same from the other side: the spells that must not touch their caster --
                // Karadura, Aversión, Silbo, "No afecta al lanzador" -- do not write "a" at
                // all, they write "g,A", the allied summons and the enemies.
            }

            // o: of everybody the zone and the sides named, the one who set the spell off.
            if (theAttackerInTheZone)
            {
                var atacante = combate.TriggeringAttacker;
                candidatos.RemoveAll(c => atacante == null || c != atacante);
            }

            // Sin máscara, al objetivo del lanzamiento. Pero si la máscara dice algo que este motor
            // todavía no sabe leer —"P" los jugadores, "F434" una familia de bichos— NO se cae al
            // objetivo: se deja pasar. Cayendo al objetivo, Flecha Voraz pegaba DOS veces.
            if (candidatos.Count == 0 && mascara.Trim().Length == 0)
            {
                candidatos.Add(objetivo ?? quienLanza);
            }

            foreach (var quien in candidatos)
            {
                if (quien == null) continue;
                IReadOnlySet<int> susEstados = estados != null && estados.TryGetValue(quien, out var alEntrar)
                    ? alEntrar
                    : quien.Buffs.Estados as IReadOnlySet<int> ?? new HashSet<int>(quien.Buffs.Estados);
                bool vale = true;
                if (requiredMonsterTemplates.Count > 0 &&
                    (!quien.IsMonster || !requiredMonsterTemplates.Contains(quien.MonsterId)))
                {
                    vale = false;
                }
                if (quien.IsMonster && excludedMonsterTemplates.Contains(quien.MonsterId)) vale = false;
                if (quien.EsInvocado)
                {
                    // "Del lanzador" seen from a summon means its master's: the Tymobot's Pinzas
                    // carry "a,P,F3112" and pick up the Tymador's bombs, which the bot never
                    // summoned. So a summon casting P reaches what its summoner summoned.
                    long amo = quienLanza.EsInvocado ? quienLanza.Invocador : quienLanza.Id;
                    bool delLanzador = quien.Invocador == amo;
                    if (ownSummonsOnly && !delLanzador) vale = false;
                    if (notOwnSummons && delLanzador) vale = false;
                }
                foreach (int estado in pideEstado) if (!susEstados.Contains(estado)) vale = false;
                foreach (int estado in pideNoEstado) if (susEstados.Contains(estado)) vale = false;
                foreach (int pct in lifeBelow) if (!LifeUnder(quien, pct)) vale = false;
                foreach (int pct in lifeNotBelow) if (LifeUnder(quien, pct)) vale = false;

                // A class is a character's: a monster or a summon is of none.
                int clase = quien.IsMonster || quien.EsInvocado ? 0 : quien.Breed;
                if (requiredBreeds.Count > 0 && !requiredBreeds.Contains(clase)) vale = false;
                if (clase != 0 && excludedBreeds.Contains(clase)) vale = false;
                if (withShield && quien.PuntosDeEscudo <= 0) vale = false;
                if (withoutShield && quien.PuntosDeEscudo > 0) vale = false;
                if (soloTelefragueados && !combate.Telefrags.ContainsKey(quien.Id)) vale = false;
                if (soloTeleportFallido && !combate.TeleportsFallidos.Contains(quien.Id)) vale = false;
                if (vale) yield return quien;
            }
        }

        /// <summary>
        /// Whom a row would reach if <paramref name="caster"/> stood on <paramref name="from"/> and
        /// aimed at <paramref name="aim"/>: this engine's own reading of its mask and its zone
        /// (<see cref="AQuien"/>), for the tactics to weigh a cast before making it. Nothing is
        /// moved or changed; everybody else is where he stands.
        /// </summary>
        /// <summary>Whether the caster meets the conditions a row puts on him (its starred letters).</summary>
        internal static bool CasterMeets(Fighter caster, SpellEffect row) => CasterQualifies(caster, row.TargetMask, null);

        internal static List<Fighter> ReachOf(FightInstance fight, Fighter caster, SpellEffect row, int from, int aim)
        {
            var cells = new Dictionary<Fighter, int>();
            Fighter onAim = null;
            foreach (var fighter in Todos(fight))
            {
                if (fighter == null) continue;
                int cell = fighter == caster ? from : fighter.CellId;
                cells[fighter] = cell;
                if (onAim == null && cell == aim && fighter.IsAlive && !fighter.EstaCargado) onAim = fighter;
            }
            try
            {
                return AQuien(fight, caster, onAim, row, aim, null, cells).Where(f => f != null && f.IsAlive).Distinct().ToList();
            }
            catch (Exception)
            {
                // A letter that reads the state of a resolution under way -- the telefragged, the
                // carried -- has none to read outside one: it reaches nobody.
                return new List<Fighter>();
            }
        }

        /// <summary>Whether a fighter is under a percentage of his maximum life.</summary>
        /// <remarks>
        /// Strictly under, in integers, without dividing: a bomb of 945 at 189 is exactly 20% and
        /// is NOT under 20. The maximum is the one of right now, erosion already taken off.
        /// </remarks>
        public static bool LifeUnder(Fighter who, int percent)
        {
            if (who == null || who.MaxHP <= 0) return false;
            return (long)who.CurrentHP * 100 < (long)who.MaxHP * percent;
        }

        /// <summary>Quién está pisando una casilla, o nadie.</summary>
        private static Fighter EnLaCasilla(FightInstance combate, int casilla)
        {
            if (casilla < 0) return null;
            foreach (var quien in Todos(combate))
            {
                if (quien != null && quien.IsAlive && !quien.EstaCargado && quien.CellId == casilla) return quien;
            }
            return null;
        }

        /// <summary>
        /// Los combatientes que pisa la zona del efecto.
        ///
        /// Si no se sabe a qué casilla se apuntó —las actitudes y los encadenados no apuntan a
        /// ninguna— se cae al objetivo de siempre, que es lo que se hacía antes de haber zonas.
        /// </summary>
        /// <param name="celdas">
        /// Where everybody stood as the spell landed, when the caller took note: a row after a
        /// push or a pull still reaches whoever was in the zone at the cast.
        /// </param>
        private static IEnumerable<Fighter> EnLaZona(FightInstance combate, Fighter quienLanza,
                                                     Fighter objetivo, SpellEffect efecto,
                                                     int celdaApuntada,
                                                     IReadOnlyDictionary<Fighter, int> celdas = null)
        {
            bool fijas = efecto.Forma == FormaDeCeldasFijas && efecto.CeldasFijas.Count > 0;
            if (celdaApuntada < 0 && !fijas)
            {
                if (objetivo != null) yield return objetivo;
                yield break;
            }

            int CeldaDe(Fighter quien)
                => celdas != null && celdas.TryGetValue(quien, out int celda) ? celda : quien.CellId;

            var casillas = CasillasDelEfecto(efecto, CeldaDe(quienLanza), celdaApuntada);
            if (casillas.Count == 0)
            {
                if (objetivo != null) yield return objetivo;
                yield break;
            }

            var dentro = new HashSet<int>(casillas);
            foreach (var quien in Todos(combate))
            {
                if (quien == null || !quien.IsAlive || quien.EstaCargado) continue;
                if (dentro.Contains(CeldaDe(quien))) yield return quien;
            }
        }

        /// <summary>Si uno pisa la zona de un efecto.</summary>
        private static bool EstaEnLaZona(Fighter quien, FightInstance combate, Fighter objetivo,
                                         SpellEffect efecto, int celdaApuntada)
        {
            foreach (var otro in EnLaZona(combate, quien, objetivo, efecto, celdaApuntada))
            {
                if (otro == quien) return true;
            }
            return false;
        }

        private static IEnumerable<Fighter> Todos(FightInstance combate)
        {
            foreach (var f in combate.Azul) yield return f;
            foreach (var f in combate.Rojo) yield return f;
        }

        /// <summary>
        /// Registers an effect that has to WAIT: a row on the target with trigger "Y" that goes
        /// off at the round the delay points at, and does nothing until then. What it will do
        /// travels in the row -- the effect, the characteristic and the amount, the duration
        /// from then on -- and the turn start applies it and drops the row.
        /// </summary>
        /// <remarks>
        /// Measured on Paso de Cacería (three casts) and the Baliza de Supervivencia (two):
        /// the waiting row carries the activation round both as its expiry and in its f12, the
        /// hidden family, and the dice and value of the effect; at the first turn of that
        /// round the marker goes out as a jwe 3793, the kill as a bare death, and a +1 MP as a
        /// new, visible row that names the waiting one as its parent, and then the waiting
        /// rows fall with their jya.
        /// </remarks>
        private static Outcome Pendiente(FightInstance combate, Fighter quienLanza, Fighter sobre,
                                         int hechizo, int grado, SpellEffect efecto, int ronda,
                                         int caracteristica = 0, int cuanto = 0)
        {
            int cuando = Empieza(efecto, ronda);
            var espera = sobre.Buffs.Poner(new Buff
            {
                EffectId = efecto.EffectId,
                EffectUid = efecto.EffectUid,
                MaxStacks = efecto.MaxStack,
                Pendiente = true,
                Caracteristica = caracteristica,
                Cuanto = cuanto,
                Dado = efecto.DiceNum,
                Cara = efecto.DiceSide,
                Valor = efecto.Value,
                Dispellable = efecto.Dispellable,
                Duracion = efecto.Duration,
                HechizoOrigen = hechizo,
                NivelOrigen = grado,
                Quien = quienLanza.Id,
                Disparador = Esperando,
                EmpiezaEnRonda = cuando,
                CaducaEnRonda = cuando,
                Apila = true,
            }, combate.SiguienteEmbrujo);

            return new Outcome
            {
                Sobre = sobre, Caster = quienLanza, Efecto = efecto, Buff = espera,
                HechizoOrigen = hechizo, NivelOrigen = grado,
            };
        }

        /// <summary>The trigger a waiting row is announced with.</summary>
        public const string Esperando = "Y";

        /// <summary>
        /// Applies one row to one fighter and picks up the rows the put replaced, so that the
        /// caller announces them gone -- jya and jwe 514 -- before the new row, the way the
        /// real server does when Espada del Juicio is cast again.
        /// </summary>
        private static Outcome Aplicar(FightInstance combate, Fighter quienLanza, Fighter sobre,
                                       int hechizo, int grado, SpellEffect efecto, int ronda,
                                       int celdaApuntada = -1,
                                       int sharedHealRoll = int.MinValue)
        {
            var hecho = AplicarSinRelevo(combate, quienLanza, sobre, hechizo, grado, efecto, ronda,
                                         celdaApuntada, sharedHealRoll);
            if (hecho?.Buff != null && hecho.Sobre != null && hecho.Sobre.Buffs.Relevados.Count > 0)
            {
                hecho.Relevados = new List<Buff>(hecho.Sobre.Buffs.Relevados);
                hecho.Sobre.Buffs.Relevados.Clear();
            }
            return hecho;
        }

        private static Outcome AplicarSinRelevo(FightInstance combate, Fighter quienLanza, Fighter sobre,
                                                int hechizo, int grado, SpellEffect efecto, int ronda,
                                                int celdaApuntada = -1,
                                                int sharedHealRoll = int.MinValue)
        {
            // El daño lo lleva quien ya lo llevaba; aquí no se toca.
            if (efecto.EffectId >= DanoPrimero && efecto.EffectId <= DanoUltimo) return null;

            if (efecto.EffectId == MataAlObjetivo)
            {
                if (!sobre.IsAlive) return null;

                // "La baliza se destruye 2 turnos después de invocarla": the 141 of her own
                // spell carries a delay of two, and the real server registers it as a waiting
                // row -- jxm 141 with trigger "Y" and the round it goes off in -- and kills her
                // at the first turn of that round, with a bare jwe 103. Applied at once, the
                // beacon died the moment she was born.
                if (efecto.Delay > 0) return Pendiente(combate, quienLanza, sobre, hechizo, grado, efecto, ronda);

                return new Outcome
                {
                    Sobre = sobre,
                    Caster = quienLanza,
                    Efecto = efecto,
                    HechizoOrigen = hechizo,
                    NivelOrigen = grado,
                    Fulmina = true,
                };
            }

            // "Sin efecto adicional" (666) is a marker on the sheet and nothing on the wire: no
            // jxm 666 in any capture, while the panel path sent one that the client had to
            // draw as a row of nothing.
            if (efecto.EffectId == SinEfectoAdicional) return null;

            // The portals: laid on the cell (1181), switched off in the zone (1183), gone through
            // by the one standing on one (1182). What each does to the network is the fight's to
            // work out and tell (FightHandler, FightPortals.cs).
            if (efecto.EffectId == ColocaUnPortal)
            {
                if (celdaApuntada < 0) return null;
                return new Outcome
                {
                    Sobre = quienLanza, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    PortalAt = celdaApuntada,
                    PortalOccupant = EnLaCasilla(combate, celdaApuntada)?.Id ?? 0,
                };
            }
            if (efecto.EffectId == DesactivaUnPortal)
            {
                if (celdaApuntada < 0) return null;
                var celdas = CasillasDelEfecto(efecto, quienLanza.CellId, celdaApuntada);
                if (celdas.Count == 0) celdas = new List<int> { celdaApuntada };
                return new Outcome
                {
                    Sobre = quienLanza, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    PortalsOffAt = celdas,
                };
            }
            if (efecto.EffectId == AtraviesaLosPortales)
            {
                if (sobre == null || !sobre.IsAlive) return null;
                return new Outcome
                {
                    Sobre = sobre, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    Teleportal = true,
                };
            }

            // "Sigue al lanzador" (2184): the bearer walks up to the caster, as far as the die
            // says. Lazo Espiritual's grade 2 is the only class row that goes out ("2184 dn=2 on
            // g,A,E6290", cast by the Osamodas at his own cell at the cast and at every MP he
            // uses), and its capture holds both ends: the summon two cells away steps once into
            // contact (frame 2125), and again when the Osamodas walks one cell away (2140).
            if (efecto.EffectId == SigueAlLanzador)
            {
                if (sobre == null || !sobre.IsAlive || sobre == quienLanza) return null;
                int pasos = efecto.DiceNum > 0 ? efecto.DiceNum : efecto.Value;
                if (pasos <= 0) return null;
                return new Outcome
                {
                    Sobre = sobre, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    Follows = pasos,
                };
            }

            if (efecto.EffectId == MarcadorDeGuion)
            {
                // Right away it is an action, not a row: 202 jwe 3793 in the class captures and
                // not one jxm 3793 with trigger "I". With a delay it waits like the rest.
                if (efecto.Delay > 0) return Pendiente(combate, quienLanza, sobre, hechizo, grado, efecto, ronda);
                return new Outcome
                {
                    Sobre = sobre, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado, Marcador = true,
                };
            }

            // "Teletransporta o intercambia posiciones": the caster to the aimed cell, swapping
            // with whoever stands there.
            if (efecto.EffectId == TeletransportaOIntercambia)
                return TeletransportarConTelefrag(combate, quienLanza, quienLanza, celdaApuntada, hechizo, grado, efecto);

            // A spell's cooldown: cut by #3 (1036), or set to #3 (1045).
            if (efecto.EffectId == RecargaMenos || efecto.EffectId == RecargaFijada)
            {
                if (sobre == null || efecto.DiceNum <= 0) return null;
                sobre.Recarga.TryGetValue(efecto.DiceNum, out int queda);
                sobre.Recarga[efecto.DiceNum] = efecto.EffectId == RecargaFijada
                    ? Math.Max(0, efecto.Value)
                    : Math.Max(0, queda - Math.Max(0, efecto.Value));
                return new Outcome
                {
                    Sobre = sobre, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado, CambiaLaRecarga = true,
                };
            }

            // "Activa las runas" (2023) and "Activa los glifos" (1026): the caster's own go off on
            // whoever stands on them, now.
            if (efecto.EffectId == ActivaLasRunas || efecto.EffectId == ActivaLosGlifos)
            {
                return new Outcome
                {
                    Sobre = quienLanza, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado, ActivaSuelo = efecto.EffectId,
                };
            }

            // A threshold on the bearer's life, at a percentage of its maximum.
            if (efecto.EffectId == Umbral)
            {
                if (sobre == null || !sobre.IsAlive) return null;
                int porcentaje = efecto.DiceNum != 0 ? efecto.DiceNum : efecto.Value;
                return new Outcome
                {
                    Sobre = sobre, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    Buff = sobre.Buffs.Poner(new Buff
                    {
                        EffectId = Umbral, EffectUid = efecto.EffectUid, MaxStacks = efecto.MaxStack,
                        Cuanto = Math.Max(0, porcentaje), HechizoOrigen = hechizo, NivelOrigen = grado,
                        Quien = quienLanza.Id, Disparador = AlLanzar,
                        CaducaEnRonda = Caduca(efecto, ronda), EmpiezaEnRonda = Empieza(efecto, ronda),
                    }, combate.SiguienteEmbrujo),
                };
            }

            // The last ally to fall comes back, as the caster's: the fight brings him.
            if (Resucitar.Contains(efecto.EffectId))
            {
                int vida = DelDado(efecto.DiceNum, efecto.DiceSide, efecto.Value);
                return new Outcome
                {
                    Sobre = quienLanza, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    Revive = Math.Clamp(vida, 1, 100), CasillaDeLaInvocacion = celdaApuntada,
                };
            }

            // "Disipa los glifos": the glyphs of whoever the mask names -- the caster, with a
            // "C", in 72 of the 98 rows -- and only the ones his spell in the die laid, when the
            // die names one: Anutrof's 29575, 6537, 8139...
            if (efecto.EffectId == DisipaLosGlifos)
            {
                if (sobre == null) return null;
                var suyos = combate.Glifos.Where(g => g.Dueno == sobre.Id
                                                   && (efecto.DiceNum <= 0 || g.HechizoQueLoPuso == efecto.DiceNum))
                                          .ToList();
                if (suyos.Count == 0) return null;
                foreach (var g in suyos) combate.Glifos.Remove(g);
                return new Outcome
                {
                    Sobre = sobre, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    GlifosQuitados = suyos,
                };
            }

            // "Retira los embrujos": every row that can be dispelled -- a dispellable of one --
            // comes off, and with it the states those rows held.
            if (efecto.EffectId == Desembrujar)
            {
                if (sobre == null || !sobre.IsAlive) return null;
                var quitados = sobre.Buffs.QuitarLosDesembrujables();
                if (quitados.Count == 0) return null;
                return new Outcome
                {
                    Sobre = sobre, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    BuffsQuitados = quitados,
                };
            }

            if (efecto.EffectId == DesvelaLosInvisibles)
            {
                if (sobre == null || !sobre.IsAlive) return null;
                var invisibles = sobre.Buffs.Puestos
                    .Where(b => b.EffectId == EffectSupport.Visibility && !b.Pendiente).ToList();
                if (invisibles.Count == 0) return null;
                foreach (var fila in invisibles) sobre.Buffs.QuitarFila(fila);
                return new Outcome
                {
                    Sobre = sobre, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    BuffsQuitados = invisibles,
                };
            }

            if (efecto.EffectId == GlifoDeAura || efecto.EffectId == GlifoDeInicioDeTurno
                || efecto.EffectId == Trampa || efecto.EffectId == Runa
                || efecto.EffectId == GlifoDeFinDeTurno || efecto.EffectId == GlifoCorriente)
            {
                // Se pone UNA vez por lanzamiento, no una por cada uno al que pille la zona: la
                // zona del efecto es la HUELLA del glifo, no su lista de víctimas.
                if (sobre != quienLanza && celdaApuntada >= 0) return null;
                if (celdaApuntada < 0) return null;
                if (efecto.DiceNum <= 0) return null;

                var huella = CasillasDelEfecto(efecto, quienLanza.CellId, celdaApuntada);
                if (huella.Count == 0) huella = new List<int> { celdaApuntada };

                var cuando = efecto.EffectId switch
                {
                    Trampa => Jondo.Unity.World.Fights.Disparo.AlPisar,
                    GlifoDeInicioDeTurno => Jondo.Unity.World.Fights.Disparo.AlEmpezarElTurno,
                    GlifoDeFinDeTurno => Jondo.Unity.World.Fights.Disparo.AlAcabarElTurno,
                    _ => Jondo.Unity.World.Fights.Disparo.AlPisarYAlEmpezar,
                };

                var puesto = combate.Poner(new Jondo.Unity.World.Fights.Glifo(
                    quienLanza.Id, huella, efecto.DiceNum, Math.Max(1, efecto.DiceSide),
                    efecto.Value, Caduca(efecto, ronda), efecto.TargetMask, cuando));
                puesto.Centro = celdaApuntada;
                puesto.HechizoQueLoPuso = hechizo;
                puesto.Tipo = efecto.EffectId;

                return new Outcome
                {
                    Sobre = quienLanza, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    Glifo = puesto,
                };
            }

            if (efecto.EffectId == QuitaPorcentajeDeVida)
            {
                if (sobre == null || !sobre.IsAlive) return null;

                int porciento = efecto.DiceNum != 0 ? efecto.DiceNum : efecto.Value;
                if (porciento <= 0) return null;

                // Sobre el TOPE, no sobre lo que le queda: si fuera sobre lo que le queda, un
                // noventa por ciento nunca mataría a nadie por muchas veces que se lanzara.
                int quita = Math.Max(1, sobre.MaxHP * porciento / 100);

                return new Outcome
                {
                    Sobre = sobre, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    Fulmina = false,
                    VidaQueSeVa = quita,
                };
            }

            if (efecto.EffectId == TransfiereVida)
            {
                if (sobre == null || !sobre.IsAlive || !quienLanza.IsAlive) return null;
                if (sobre == quienLanza) return null;

                int porciento = efecto.DiceNum != 0 ? efecto.DiceNum : efecto.Value;
                if (porciento <= 0) return null;

                // De la vida QUE LE QUEDA al que la da: no se puede regalar lo que ya no se
                // tiene. Y nunca hasta matarse: se queda con uno.
                int cuanto = Math.Min(quienLanza.CurrentHP - 1, quienLanza.CurrentHP * porciento / 100);
                if (cuanto <= 0) return null;

                return new Outcome
                {
                    Sobre = sobre, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    VidaTransferida = cuanto,
                };
            }

            if (efecto.EffectId == DevuelvePA)
            {
                int cuantos = efecto.DiceNum != 0 ? efecto.DiceNum : efecto.Value;
                if (cuantos <= 0) return null;

                return new Outcome
                {
                    Sobre = sobre, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    Caracteristica = PuntosDeAccion, Cuanto = cuantos,
                };
            }

            if (efecto.EffectId == AcortaLosEfectos)
            {
                int rondas = efecto.DiceNum != 0 ? efecto.DiceNum : efecto.Value;
                if (rondas <= 0) return null;

                int caidos = sobre.Buffs.Acortar(rondas, ronda);
                if (caidos == 0) return null;

                return new Outcome
                {
                    Sobre = sobre, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    EmbrujosCaidos = caidos,
                };
            }

            // 2796 is the same "mata al objetivo y reemplaza por la invocación: #1" under another
            // number: the Sadida's Potencia Silvestre and Influencia Vegetal turn an allied tree
            // into a doll with it, the template in the dice and its grade on the side.
            if (efecto.EffectId == MataYReemplaza || efecto.EffectId == MataYReemplazaPorInvocacion)
            {
                if (!sobre.IsAlive) return null;
                if (efecto.DiceNum <= 0) return null;

                return new Outcome
                {
                    Sobre = sobre, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    Fulmina = true,
                    Invoca = efecto.DiceNum,
                    EnLaCasillaDelMuerto = true,
                };
            }

            if (efecto.EffectId == InvocaUnDoble)
            {
                // A double of the caster on the aimed cell, free and walkable: the Sram's Doble
                // and Conspirador, "un doble controlable que posee las mismas características
                // que el invocador". Put on the board here, as the illusions are; the fight
                // announces it and does what the spell writes for it ("a,U").
                if (celdaApuntada < 0 || sobre != quienLanza) return null;
                var suelo = MapManager.GetFightWalkable(combate.ArenaMapId);
                if (suelo != null && !suelo.Contains(celdaApuntada)) return null;
                if (EnLaCasilla(combate, celdaApuntada) != null) return null;

                var doble = DobleDe(combate, quienLanza, celdaApuntada);
                combate.Invocar(doble, quienLanza);
                return new Outcome
                {
                    Sobre = quienLanza, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    Doble = doble, CasillaDeLaInvocacion = celdaApuntada,
                };
            }

            if (efecto.EffectId == EffectSupport.Illusions)
            {
                // The caster goes to the aimed cell -- a free, walkable one -- and copies of him
                // appear on the cells symmetric to it around the cell he LEFT: the vector from
                // there to the aimed cell, turned a quarter, a half and three quarters. The
                // capture is one sample and fits both readings -- aimed two cells up his own
                // axis, the copies landed two cells down the other three -- and the fixed
                // "two steps down each axis" that was written first put the copies two cells
                // away from a cast aimed one cell away, out of any cross. Turning the aimed
                // vector keeps them at the distance he jumped, in the shape the sheet
                // describes, and the copies go only on cells that are free and walkable, as
                // many as the effect says.
                if (celdaApuntada < 0 || sobre != quienLanza) return null;
                var suelo = MapManager.GetFightWalkable(combate.ArenaMapId);
                if (suelo != null && !suelo.Contains(celdaApuntada)) return null;
                if (EnLaCasilla(combate, celdaApuntada) != null) return null;

                int origen = quienLanza.CellId;
                quienLanza.MoverA(celdaApuntada);

                var copias = new List<Fighter>();
                int cuantas = Math.Max(1, efecto.DiceNum);
                var (ox, oy) = Jondo.Unity.World.Maps.MapGeometry.CellToPoint(origen);
                var (ax, ay) = Jondo.Unity.World.Maps.MapGeometry.CellToPoint(celdaApuntada);
                int vx = ax - ox, vy = ay - oy;
                // A quarter turn, three quarters, a half: the order the three copies of the
                // capture came out in -- 259, 201, 203 for a jump from 230 to 257.
                foreach (var (dx, dy) in new[] { (-vy, vx), (vy, -vx), (-vx, -vy) })
                {
                    if (copias.Count >= cuantas) break;
                    int celda = Jondo.Unity.World.Maps.MapGeometry.PointToCell(ox + dx, oy + dy);
                    if (celda < 0) continue;
                    if (suelo != null && !suelo.Contains(celda)) continue;
                    if (EnLaCasilla(combate, celda) != null) continue;

                    var copia = new Fighter
                    {
                        Id = combate.SiguienteIdDeInvocado(),
                        Name = "ilusión",
                        CellId = celda,
                        Level = quienLanza.Level,
                        MaxHP = 50 + 5 * Math.Max(1, quienLanza.Level),
                        SummonCost = 0,
                        JuegaTurno = false,
                        EsIlusion = true,
                        Look = quienLanza.Look,
                        LookBoneId = quienLanza.LookBoneId,
                    };
                    copia.CurrentHP = copia.MaxHP;
                    combate.Invocar(copia, quienLanza);
                    quienLanza.Ilusiones.Add(copia.Id);
                    copias.Add(copia);
                }

                return new Outcome
                {
                    Sobre = quienLanza, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    CasillaDesde = origen, CasillaHasta = celdaApuntada,
                    Ilusiones = copias,
                };
            }

            if (efecto.EffectId == EffectSupport.Carry)
            {
                // One at a time, nobody who is already carried or carrying, never oneself.
                if (sobre == quienLanza || quienLanza.Carrying != 0 || sobre.EstaCargado
                    || sobre.Carrying != 0) return null;

                int deDonde = sobre.CellId;
                sobre.CarriedBy = quienLanza.Id;
                quienLanza.Carrying = sobre.Id;
                sobre.CellId = quienLanza.CellId;
                quienLanza.Buffs.PonerEstado(EffectSupport.CarryingState);
                sobre.Buffs.PonerEstado(EffectSupport.CarriedState);

                return new Outcome
                {
                    Sobre = sobre, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    Carga = true, CasillaDesde = deDonde,
                };
            }

            if (efecto.EffectId == EffectSupport.Throw)
            {
                if (celdaApuntada < 0 || sobre.CarriedBy != quienLanza.Id) return null;
                var suelo = MapManager.GetFightWalkable(combate.ArenaMapId);
                if (suelo != null && !suelo.Contains(celdaApuntada)) return null;
                if (EnLaCasilla(combate, celdaApuntada) != null) return null;

                int deDonde = quienLanza.CellId;
                sobre.CarriedBy = 0;
                quienLanza.Carrying = 0;
                quienLanza.Buffs.QuitarEstado(EffectSupport.CarryingState);
                sobre.Buffs.QuitarEstado(EffectSupport.CarriedState);
                sobre.MoverA(celdaApuntada);

                return new Outcome
                {
                    Sobre = sobre, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    Lanza = true, CasillaDesde = deDonde, CasillaHasta = celdaApuntada,
                };
            }

            if (efecto.EffectId == EffectSupport.EndsTheTurn)
            {
                // Nothing to apply on the fighter: the handler ends the turn once the cast has
                // gone out whole. In the Tymadura capture the jyt follows the last jwe of the
                // cast, with the illusions already placed.
                return new Outcome
                {
                    Sobre = sobre, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    AcabaElTurno = true,
                };
            }

            if (efecto.EffectId == EffectSupport.VitalityPercentMalus
                || efecto.EffectId == EffectSupport.VitalityPercentBonus)
            {
                int porciento = efecto.DiceNum != 0 ? efecto.DiceNum : efecto.Value;
                if (porciento <= 0) return null;

                // Of the MAXIMUM LIFE, base and gear included: the test characters of the
                // captures are naked level-200s at 1,150 life (1,050 of level and the 100 of
                // scrolls), and Vitalidad's 20% goes out as +230 and Último Aliento's -50% as
                // -575. Of the vitality characteristic alone -- 100 -- neither number comes
                // out, and a Yopuka at 1,650 got 120 for his 600 of vitality instead of 330.
                // The panel gets the flat effect, 125 or 153, with the points on it; the sheet
                // gets the vitality hole; the buff carries the points on characteristic 11 so
                // that the sheet refresh finds them, and the handler moves the maximum with it
                // and moves it back when the buff falls.
                int puntos = sobre.MaxHP * porciento / 100;
                if (puntos <= 0) return null;
                if (efecto.EffectId == EffectSupport.VitalityPercentMalus) puntos = -puntos;

                sobre.MaxHP = Math.Max(1, sobre.MaxHP + puntos);
                if (sobre.CurrentHP > sobre.MaxHP) sobre.CurrentHP = sobre.MaxHP;

                int comoSePinta = puntos < 0 ? EffectSupport.VitalityFlatMalus : EffectSupport.VitalityFlatBonus;
                var embrujoDeVida = sobre.Buffs.Poner(new Buff
                {
                    EffectId = comoSePinta,
                    EffectUid = efecto.EffectUid,
                    MaxStacks = efecto.MaxStack,
                    Caracteristica = VitalityCharacteristic,
                    Cuanto = puntos,
                    HechizoOrigen = hechizo,
                    NivelOrigen = grado,
                    Quien = quienLanza.Id,
                    Disparador = AlLanzar,
                    CaducaEnRonda = Caduca(efecto, ronda),
                    EmpiezaEnRonda = Empieza(efecto, ronda),
                }, combate.SiguienteEmbrujo);

                return new Outcome
                {
                    Sobre = sobre, Caster = quienLanza,
                    Efecto = ComoSePinta(efecto, comoSePinta, Math.Abs(puntos)),
                    Buff = embrujoDeVida,
                    Caracteristica = VitalityCharacteristic, Cuanto = puntos,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                };
            }

            if (efecto.EffectId == EscudoPorNivel || efecto.EffectId == EscudoPorVida)
            {
                int porciento = efecto.DiceNum != 0 ? efecto.DiceNum : efecto.Value;
                if (porciento <= 0) return null;

                // El 1020 va sobre el nivel DEL QUE LANZA y el 1039 sobre la vida DEL QUE LO
                // RECIBE. Son dos bases distintas y confundirlas da escudos de otro orden: un
                // 150% de nivel son 300 puntos a nivel 200, y un 150% de vida serían miles.
                int base_ = efecto.EffectId == EscudoPorNivel ? quienLanza.Level : sobre.MaxHP;
                int cuanto = base_ * porciento / 100;
                if (cuanto <= 0) return null;

                int caduca = Caduca(efecto, ronda);
                sobre.Escudar(cuanto, caduca);

                // And the panel row, which nothing was sending: the shield held on the server
                // and the client never drew it. Measured on Patada: the bomb gets a jxm of
                // effect 1040 worth the points -- 350, 175% of level 200 -- with the grade and
                // the round it falls, and its sheet gets characteristic 96 at the total. The
                // buff carries no characteristic: the sheet reads the points off the fighter.
                //
                // How many rows live together is the level's maxStack, like any row: Patada is
                // 2 and its second cast adds row 64 next to row 55, both worth 350, sheet at
                // 700; Espada del Juicio is 1 and its second cast drops row 9 for row 13, and
                // the points of the row that went go with it.
                var embrujoDeEscudo = sobre.Buffs.Poner(new Buff
                {
                    EffectId = ShieldPanelEffect,
                    EffectUid = efecto.EffectUid,
                    MaxStacks = efecto.MaxStack,
                    Cuanto = cuanto,
                    HechizoOrigen = hechizo,
                    NivelOrigen = grado,
                    Quien = quienLanza.Id,
                    Disparador = AlLanzar,
                    CaducaEnRonda = caduca,
                    EmpiezaEnRonda = Empieza(efecto, ronda),
                }, combate.SiguienteEmbrujo);
                foreach (var relevado in sobre.Buffs.Relevados) sobre.Desescudar(relevado.Cuanto);

                return new Outcome
                {
                    Sobre = sobre, Caster = quienLanza,
                    Efecto = ComoSePinta(efecto, ShieldPanelEffect, cuanto),
                    Buff = embrujoDeEscudo,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    Escudo = cuanto,
                };
            }

            if (efecto.EffectId == SimetricoRespectoAlObjetivo
                || efecto.EffectId == SimetricoRespectoAlLanzador
                || efecto.EffectId == Simetrico)
            {
                int pivote = efecto.EffectId switch
                {
                    SimetricoRespectoAlLanzador => quienLanza.CellId,
                    SimetricoRespectoAlObjetivo => celdaApuntada >= 0 ? celdaApuntada : sobre.CellId,
                    _ => celdaApuntada >= 0 ? celdaApuntada : quienLanza.CellId,
                };

                int alOtroLado = Jondo.Unity.World.Maps.MapGeometry.Reflejar(sobre.CellId, pivote);
                return TeletransportarConTelefrag(combate, quienLanza, sobre, alOtroLado, hechizo, grado, efecto);
            }

            // Back to a cell the fighter stood on: before his last move (1100), when his turn
            // began (1099), when the fight began (784). Kontatrás's Contratiempo sends its target
            // back to its turn-start cell once he is vulnerable ("A,*e56").
            if (efecto.EffectId == ALaPosicionAnterior || efecto.EffectId == AlInicioDelTurno
                || efecto.EffectId == AlInicioDelCombate)
            {
                // Sin memoria de dónde estaba no hay nada que deshacer, y devolver a cualquier
                // sitio sería peor que no hacer nada.
                int antes = efecto.EffectId switch
                {
                    AlInicioDelTurno => sobre.CasillaAlEmpezarTurno,
                    AlInicioDelCombate => sobre.CasillaAlEmpezarCombate,
                    _ => sobre.CasillaAnterior,
                };
                return TeletransportarConTelefrag(combate, quienLanza, sobre, antes, hechizo, grado, efecto);
            }

            if (efecto.EffectId == Teletransportar)
            {
                if (celdaApuntada < 0) return null;
                if (sobre.CellId == celdaApuntada) return null;

                // El suelo manda. Una casilla que no se puede pisar, o que ya tiene a alguien
                // encima, deja el teletransporte sin hacer: es preferible a mandar a nadie a un
                // agujero, que es lo que pasaba con los empujones antes de mirar el suelo.
                var pisables = MapManager.GetFightWalkable(combate.ArenaMapId);
                if (pisables != null && !pisables.Contains(celdaApuntada)) return null;

                foreach (var otro in Todos(combate))
                {
                    if (otro != null && otro.IsAlive && otro.CellId == celdaApuntada) return null;
                }

                int deDonde = sobre.CellId;
                sobre.MoverA(celdaApuntada);

                return new Outcome
                {
                    Sobre = sobre, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    CasillaDesde = deDonde, CasillaHasta = celdaApuntada,
                };
            }

            if (efecto.EffectId == IntercambiarPosiciones || efecto.EffectId == IntercambioForzado)
            {
                if (sobre == quienLanza) return null;
                if (sobre.CellId == quienLanza.CellId) return null;

                // Aquí no se mira el suelo: las dos casillas ya las está pisando alguien, así que
                // por definición se pueden pisar. Y tampoco se mira si están ocupadas, porque lo
                // están las dos y justamente por eso el cambio es posible.
                int delObjetivo = sobre.CellId;
                int delLanzador = quienLanza.CellId;

                sobre.MoverA(delLanzador);
                quienLanza.MoverA(delObjetivo);

                return new Outcome
                {
                    Sobre = sobre, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    CasillaDesde = delObjetivo, CasillaHasta = delLanzador,
                    Tambien = quienLanza,
                    CasillaDesdeDelOtro = delLanzador, CasillaHastaDelOtro = delObjetivo,
                };
            }

            bool hastaLaCasilla = efecto.EffectId == EffectSupport.PushToTargetCell
                                  || efecto.EffectId == EffectSupport.PullToTargetCell;
            if (efecto.EffectId == Empujar || efecto.EffectId == EffectSupport.PushWithoutDamage ||
                efecto.EffectId == EmpujeForzado || efecto.EffectId == TironForzado ||
                efecto.EffectId == Tirar || efecto.EffectId == Retroceder || efecto.EffectId == Avanzar ||
                hastaLaCasilla)
            {
                // Cuántas casillas: el dado, y si no, el valor. "Hasta la casilla objetivo" is
                // as many as separate the one moved from the aimed cell, and the direction is
                // the caster's line, not the aimed cell's: the aimed cell is where it ENDS.
                int cuantas = hastaLaCasilla
                    ? Jondo.Unity.World.Maps.MapGeometry.Distance(sobre.CellId, celdaApuntada)
                    : efecto.DiceNum != 0 ? efecto.DiceNum : efecto.Value;
                if (cuantas <= 0) return null;
                if (efecto.EffectId == Tirar || efecto.EffectId == EffectSupport.PullToTargetCell
                    || efecto.EffectId == TironForzado) cuantas = -cuantas;
                int centroDelEmpuje = hastaLaCasilla ? quienLanza.CellId : celdaApuntada;

                // "Retrocede" y "Avanza" mueven AL QUE LANZA, no al objetivo. Es lo que hace Tiro
                // de Repliegue, que da alcance y da un paso atrás; el objetivo sólo sirve para
                // saber de dónde se aleja. Medido en su captura: el Ocra estaba en la 411, lanzó
                // a la 410 y acabó en la 412, alejándose de la casilla apuntada.
                bool alLanzador = efecto.EffectId == Retroceder || efecto.EffectId == Avanzar;
                if (alLanzador)
                {
                    sobre = quienLanza;
                    if (efecto.EffectId == Avanzar) cuantas = -cuantas;
                }

                // INDESPLAZABLE: no se mueve, y por tanto TAMPOCO recibe daño de colisión.
                //
                // La diferencia importa y es fácil de equivocar: no es que el daño se reduzca a
                // cero, es que sin desplazamiento no hay choque, aunque tenga el muro pegado a la
                // espalda. Un empujado al que le falta sitio SÍ cobra; éste no.
                //
                // El número del estado está medido: de los 25 hechizos cuya descripción en español
                // nombra el estado Indesplazable, 21 aplican el 97 y el siguiente candidato sale en
                // 1. Y los 155 hechizos que lo ponen se leen solos: Remache, Atracción
                // Estabilizadora, Bombinmóvil, Patinaje.
                //
                // Ojo: el ESTADO 97 no tiene nada que ver con la CARACTERÍSTICA 97, que es la vida
                // que le falta al jugador. Mismo número, dos espacios distintos.
                // And the catalogue names the 97 among 22 states with cantBeMoved and 25 with
                // cantBePushed -- Arraigado, Pesadilla, Cénit... -- which the client's own
                // SpellStateData flags; those are read from datos/spell_states.json.
                if (efecto.EffectId != EmpujeForzado && efecto.EffectId != TironForzado &&
                    (sobre.Buffs.TieneEstado(Indesplazable) || SpellStates.PinsInPlace(sobre))) return null;

                var ocupadas = new HashSet<int>();
                foreach (var otro in Todos(combate))
                    if (otro != null && otro.IsAlive && !otro.EstaCargado && otro != sobre) ocupadas.Add(otro.CellId);

                int desde = sobre.CellId;
                // Las casillas que se pueden pisar en la arena. Iban a null, que quiere decir "no
                // mires el suelo", y por eso los pious acababan en un agujero o fuera del mapa:
                // la única frontera que quedaba era el borde de la retícula de 560 celdas, que es
                // mucho mayor que el suelo de un mapa.
                var pisables = MapManager.GetFightWalkable(combate.ArenaMapId);
                // AND A BOMB WALL STOPS IT. "Desplazar una entidad a un muro detendra su
                // desplazamiento y le infligira danos", says the class sheet; it steps onto the
                // wall cell and goes no further. Which walls count for THIS fighter is the whole
                // rule -- Kabum, its own bombs, once a turn -- and that lives in BombWalls.
                var empujon = Jondo.Unity.World.Maps.Zone.Push(
                    centroDelEmpuje, quienLanza.CellId, desde, cuantas,
                    pisables: pisables, ocupadas: ocupadas,
                    paran: BombWalls.StoppingCells(combate, sobre));

                sobre.MoverA(empujon.ToCell);

                // EL DAÑO DE COLISIÓN, que no se hacía en absoluto.
                //
                // Sale de las casillas que NO se recorrieron, y la fórmula está medida sobre los
                // 127 mensajes de daño de empuje de las 401 capturas:
                //
                //   daño = casillasSinRecorrer × (nivel/2 + la 84 del que empuja
                //                                 − la 85 del que la recibe + 32) / 4
                //
                // Las tres anclas: un lanzador de nivel 200 sin bonos pega 33 por casilla —132/4—
                // y sólo salen 33, 66, 99 y 132, ni un valor intermedio; el Zurkarak «Daddy», que
                // es de NIVEL 165, pega 57 por dos casillas, que es floor(2 × 114,5 / 4) y que
                // ninguna constante fija puede dar; y un Zobal con 100 de empuje de equipo y
                // máscaras de 0, 40, 80 y 120 pega 58, 68, 78 y 88 por casilla.
                //
                // La resistencia va DENTRO del cuarto: en el koliseo, 561 de empuje contra 30 de
                // resistencia dan 331 por dos casillas. Restándola fuera saldría 316.
                //
                // Y sólo lo hace el empujón: el catálogo tiene un efecto aparte, «Empuja (sin
                // daños)», que 54 hechizos usan justamente para no hacerlo, lo que es la prueba de
                // que el 5 normal sí. Del TIRÓN no hay ni un caso bloqueado en las 401 capturas,
                // así que se queda a cero hasta que se mida.
                int colision = 0, aLaPared = 0;
                Fighter pared = null;

                // And a wall is NOT a crash. It stops the displacement, but the damage it deals
                // is its own -- applied by the glyph, over in the handler -- not the damage of
                // slamming into something: the class sheet mentions no collision damage at all
                // for pushing somebody into a wall.
                bool empujaConDano = efecto.EffectId == Empujar && cuantas > 0
                                     && empujon.Stop != Jondo.Unity.World.Maps.Zone.PushStop.Wall;
                // The 1103 is the 5 that never collides; it took this same branch and paid.
                if (empujaConDano && empujon.BlockedCells > 0)
                {
                    int deEmpuje = quienLanza.PushDamage + quienLanza.Buffs.De(DanoDeEmpuje, ronda);
                    int resiste = sobre.Otra(ResistenciaAlEmpuje) +
                                  sobre.Buffs.De(ResistenciaAlEmpuje, ronda);

                    colision = DanoDeColision(quienLanza.Level, deEmpuje, resiste,
                                              empujon.BlockedCells);

                    // Y si lo que lo frenó fue otro combatiente, ése cobra la mitad. Los muros no
                    // cobran.
                    if (colision > 0 && empujon.Stop == Jondo.Unity.World.Maps.Zone.PushStop.Fighter)
                    {
                        foreach (var otro in Todos(combate))
                        {
                            if (otro == null || !otro.IsAlive) continue;
                            if (otro.CellId != empujon.BlockerCell) continue;
                            pared = otro;
                            aLaPared = colision / 2;
                            break;
                        }
                    }
                }

                // Se sale sin nada SÓLO si además no hay daño: cuando al empujado no le queda ni
                // una casilla libre no se mueve, pero se lleva el golpe entero. Medido: en ese
                // caso el servidor real no manda el desplazamiento, sólo el daño.
                if (empujon.ToCell == desde && colision <= 0) return null;

                return new Outcome
                {
                    Sobre = sobre, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    CasillaDesde = desde, CasillaHasta = empujon.ToCell,
                    CollisionDamage = colision,
                    Blocker = pared,
                    CollisionDamageToBlocker = aLaPared,
                };
            }

            if (Invocaciones.Contains(efecto.EffectId))
            {
                // "Invoca: #1", con la plantilla del bicho en el dado. No lo saca al tablero el
                // motor: hace falta repartir identificador, rehacer el orden de turnos y avisar
                // al cliente, y eso es del que lleva el combate.
                if (efecto.DiceNum <= 0) return null;
                return new Outcome
                {
                    Sobre = sobre, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    Invoca = efecto.DiceNum,
                    CasillaDeLaInvocacion = celdaApuntada,
                };
            }

            if (efecto.EffectId == LanzarHechizo || efecto.EffectId == DispararHechizo ||
                efecto.EffectId == NearestTargetExecuteSpell)
            {
                // "Lanza el hechizo del dado en el grado de la cara". Es el enganche de las
                // actitudes: el grado 1 del Amarillo Ocre no hace nada por sí mismo, sólo dice
                // cuándo lanzar sus grados 2 y 3.
                if (efecto.DiceNum <= 0) return null;
                return new Outcome
                {
                    Sobre = sobre,
                    Efecto = efecto,
                    HechizoOrigen = hechizo,
                    NivelOrigen = grado,
                    HechizoEncadenado = efecto.DiceNum,
                    GradoEncadenado = efecto.DiceSide > 0 ? efecto.DiceSide : 1,
                };
            }

            // "Desactiva el estado #3": a row that holds the state switched off while it lives.
            // The state is not taken away -- nothing puts it back when the row falls, and in
            // Kontatrás's fight it is back the turn after -- it just stops counting. 952 used to
            // fall into the characteristic path by its catalogue row (characteristic 71), which
            // left a "+56" nobody read and the Count invulnerable for good.
            if (efecto.EffectId == EffectSupport.DisableState)
            {
                int apagado = efecto.Value != 0 ? efecto.Value : efecto.DiceNum;
                if (apagado == 0 || !sobre.IsAlive) return null;
                return new Outcome
                {
                    Sobre = sobre,
                    Efecto = efecto,
                    HechizoOrigen = hechizo,
                    NivelOrigen = grado,
                    Buff = sobre.Buffs.Poner(new Buff
                    {
                        EffectId = efecto.EffectId,
                        EffectUid = efecto.EffectUid,
                        MaxStacks = efecto.MaxStack,
                        Estado = apagado,
                        HechizoOrigen = hechizo,
                        NivelOrigen = grado,
                        Quien = quienLanza.Id,
                        Disparador = AlLanzar,
                        CaducaEnRonda = Caduca(efecto, ronda),
                        EmpiezaEnRonda = Empieza(efecto, ronda),
                    }, combate.SiguienteEmbrujo),
                };
            }

            if (efecto.EffectId == PonerEstado || efecto.EffectId == QuitarEstado)
            {
                int estado = efecto.Value != 0 ? efecto.Value : efecto.DiceNum;
                if (estado == 0) return null;

                // A monster's state with a delay goes on -- or comes off -- when its round comes:
                // Miauvizor's Inquebrantable (157) at turn 21, not from turn 1.
                if (efecto.Delay > 0 && !PlayerSpells.Contains(hechizo))
                    return Pendiente(combate, quienLanza, sobre, hechizo, grado, efecto, ronda);
                var barridos = new List<Buff>();

                // EL COMBO NO ES UN ESTADO CUALQUIERA, y tratarlo como tal rompia tres cosas a la
                // vez. Es una escalera de peldanos excluyentes que solo puede llevar una bomba,
                // asi que aqui se le imponen sus tres reglas antes de tocar nada:
                //
                //   1. SOLO A UNA BOMBA. Polvora y Mosquete encadenan el hechizo del combo con
                //      mascaras que este motor no sabe estrechar -- "P", "h" --, y sin saber a
                //      quien apuntar caia en el lanzador: el tymador acababa con Combo IV en su
                //      propio panel y la bomba sin subir.
                //   2. UN PELDANO Y NO DOS. La escalera del hechizo vuelve a poner el primero en
                //      cada vuelta -- su mascara excluye del 2485 en adelante pero no el 2484 --
                //      y se lo anunciabamos al cliente antes de quitarlo, asi que la bomba se
                //      veia siempre en Combo I por mucho que el servidor la subiera.
                //   3. NADA POR ENCIMA DEL QUINCE. "El combo aumenta de 1 a 15 maximo", dice la
                //      ficha de clase. La escalera del hechizo tiene dieciocho peldanos y las
                //      bombas llegaban al 18; los tres de arriba pagan lo mismo que el quince,
                //      asi que subir mas no daba nada y el cliente no sabe pintarlos.
                if (Combo.EsPeldano(estado))
                {
                    if (efecto.EffectId == PonerEstado)
                    {
                        if (!Bombs.Is(sobre.MonsterId)) return null;

                        int ahora = Combo.LevelOf(sobre);
                        int sube = Combo.NivelDelPeldano(estado);

                        // BAJAR NO, REPETIR SI. El servidor real vuelve a poner el peldano en el
                        // que ya esta antes de subirlo -- medido en «tymador-explobomba
                        // resiliente», donde los frames 248 y 249 mandan 2484 y 2485 seguidos, sin
                        // nada en medio -- y al refusarselo nuestro flujo dejaba de parecerse al
                        // suyo justo en el sitio que el cliente usa para pintar el numero romano.
                        // Refusar de verdad hace falta en dos casos y solo en dos: bajar de
                        // peldano, y pasar del quince.
                        if (sube < ahora) return null;
                        if (sube > ahora && ahora >= Combo.Tope) return null;

                        // Y LOS VIEJOS SE ANUNCIAN. Quitarlos en silencio era lo que dejaba a la
                        // bomba en Combo I para siempre: el servidor la subia -- se ve en el
                        // registro, «-5 esta en el nivel 13, +280%» -- pero el cliente seguia con
                        // el peldano 1 puesto porque nadie le habia dicho que se lo quitara, y es
                        // el que pintaba.
                        foreach (int viejo in Combo.Ladder())
                        {
                            if (viejo == estado) continue;
                            barridos.AddRange(sobre.Buffs.QuitarEstadoConEmbrujos(viejo));
                        }
                    }
                    // SI NO SE HA SUBIDO, TAMPOCO SE BARRE. Cada peldano de la escalera lleva
                    // detras un 951 que quita el anterior, y con el tope puesto pasaba esto: al
                    // llegar al quince se rechazaba el 950 del dieciseis pero su 951 seguia
                    // quitando el quince, la bomba se quedaba sin combo y volvia a empezar por
                    // abajo. Se veia clavado en la prueba: veinticinco lanzamientos y la bomba
                    // en el nivel 9, que es quince arriba, cero, y nueve otra vez.
                    else if (Combo.LevelOf(sobre) == Combo.NivelDelPeldano(estado))
                    {
                        return null;
                    }
                }

                if (efecto.EffectId == PonerEstado) sobre.Buffs.PonerEstado(estado);
                if (efecto.EffectId == QuitarEstado)
                {
                    barridos.AddRange(sobre.Buffs.QuitarEstadoConEmbrujos(estado));
                }
                IReadOnlyList<Buff> quitados = barridos;

                return new Outcome
                {
                    Sobre = sobre,
                    Efecto = efecto,
                    HechizoOrigen = hechizo,
                    NivelOrigen = grado,
                    Buff = efecto.EffectId == PonerEstado
                        ? sobre.Buffs.Poner(new Buff
                        {
                            EffectId = efecto.EffectId,
                            EffectUid = efecto.EffectUid,
                            MaxStacks = efecto.MaxStack,
                            Estado = estado,
                            HechizoOrigen = hechizo,
                            NivelOrigen = grado,
                            Quien = quienLanza.Id,
                            Disparador = AlLanzar,
                            CaducaEnRonda = Caduca(efecto, ronda),
                            EmpiezaEnRonda = Empieza(efecto, ronda),

                            // THE RUNG STACKS, and not on a whim: it is what stops a BUFF
                            // NUMBER FROM BEING REUSED. Without it, setting again the rung the
                            // bomb already holds fell into the "this one was already here" branch
                            // of Buffs.Poner, which hands back the same object with the same
                            // number, so the client got buff 1 with state 2484 twice and its
                            // removal once. The real server never reuses a number, not once: in
                            // "tymador-explobomba resiliente" the same bomb carries 2484 as buff
                            // 19 and again as buff 23, and takes BOTH off -- frames 260 and 261 --
                            // before stepping it up. Here the opposite happened, and that is why
                            // the bomb sat on Combo I: the client paints the STATE NAME -- text
                            // 1026062 is "Combo I", 1026067 is "Combo II" -- and one of the two
                            // 2484s nobody had withdrawn stayed on it.
                            //
                            // Stacking leaves no two rungs alive: the sweep above calls
                            // QuitarEstadoConEmbrujos, which takes away EVERY copy of the state.
                            Apila = Combo.EsPeldano(estado),
                        }, combate.SiguienteEmbrujo)
                        : null,
                    BuffsQuitados = quitados,
                };
            }

            if (efecto.EffectId == QuitarEfectosDeHechizo)
            {
                int hechizoQuitado = efecto.Value != 0 ? efecto.Value : efecto.DiceNum;
                if (hechizoQuitado <= 0) return null;

                // AND IF THAT SPELL IS ONE OF THE TARGET OWN PASSIVES, IT IS DISARMED. This is how
                // "1 vez por combate" is written in the data: the Silver Dofus (18672) fires its
                // grade 2 under "C,V20", and that grade carries a 406 on 18672 itself -- take my
                // own effects away, so I never fire again. Buffs alone were being removed and the
                // attitude stayed armed, ready to heal 30% at every turn start spent under 20%.
                if (sobre.Buffs.Actitudes.Remove(hechizoQuitado))
                {
                    Program.LogDebug($"[Combate] {sobre.Id} se queda sin la actitud {hechizoQuitado}: " +
                                     $"el efecto 406 del hechizo {hechizo} la desarma.");
                }

                return new Outcome
                {
                    Sobre = sobre,
                    Efecto = efecto,
                    HechizoOrigen = hechizo,
                    NivelOrigen = grado,
                    BuffsQuitados = sobre.Buffs.QuitarDelHechizo(hechizoQuitado),
                };
            }

            if (efecto.EffectId == QuitaUnGradoDeUnHechizo)
            {
                int hechizoQuitado = efecto.Value;
                int gradoQuitado = efecto.DiceSide != 0 ? efecto.DiceSide : efecto.DiceNum;
                if (sobre == null || hechizoQuitado <= 0 || gradoQuitado <= 0) return null;
                return new Outcome
                {
                    Sobre = sobre, Caster = quienLanza, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    BuffsQuitados = sobre.Buffs.QuitarDelHechizo(hechizoQuitado, gradoQuitado),
                };
            }

            if (efecto.EffectId == CuraAlAtacante)
            {
                var atacante = combate.TriggeringAttacker;
                int porciento = efecto.Value != 0 ? efecto.Value : efecto.DiceNum;
                if (atacante == null || !atacante.IsAlive || porciento <= 0 || combate.DanoDelDisparo <= 0) return null;
                return ApplyHealing(combate, quienLanza, atacante, hechizo, grado, efecto, ronda,
                                    combate.DanoDelDisparo * porciento / 100);
            }

            if (efecto.EffectId == CuraPorElDanoOcasionado || efecto.EffectId == CuraPorElDanoSufrido)
            {
                int porciento = DelDado(efecto.DiceNum, efecto.DiceSide, efecto.Value);
                if (sobre == null || !sobre.IsAlive || porciento <= 0 || combate.DanoDelDisparo <= 0) return null;
                return ApplyHealing(combate, quienLanza, sobre, hechizo, grado, efecto, ronda,
                                    combate.DanoDelDisparo * porciento / 100);
            }

            if (efecto.EffectId == CambiarApariencia)
            {
                int apariencia = efecto.Value != 0 ? efecto.Value : efecto.DiceNum;
                if (apariencia <= 0) return null;

                var puesto = sobre.Buffs.Poner(new Buff
                {
                    EffectId = efecto.EffectId,
                    EffectUid = efecto.EffectUid,
                    MaxStacks = efecto.MaxStack,
                    Apariencia = apariencia,
                    HechizoOrigen = hechizo,
                    NivelOrigen = grado,
                    Quien = quienLanza.Id,
                    Disparador = AlLanzar,
                    CaducaEnRonda = Caduca(efecto, ronda),
                    EmpiezaEnRonda = Empieza(efecto, ronda),
                }, combate.SiguienteEmbrujo);

                return new Outcome
                {
                    Sobre = sobre,
                    Efecto = efecto,
                    Buff = puesto,
                    Apariencia = apariencia,
                    HechizoOrigen = hechizo,
                    NivelOrigen = grado,
                };
            }

            // Los que afinan un hechizo concreto: daño básico y alcance, and the rest of the
            // catalogue's category 3 (SpellModifiers). El hechizo va en el dado y lo que se suma,
            // en el valor. A pin is a value even at zero: Bestialidad's "2905 on 13791, value 0"
            // goes out in the molosse capture as a row with no f10, and holds the spell to 0.
            if (Enum.IsDefined(typeof(SpellAspect), efecto.EffectId) && efecto.EffectId != 0)
            {
                var que = (SpellAspect)efecto.EffectId;
                int cuanto = efecto.Value;
                if (efecto.DiceNum <= 0 || (cuanto == 0 && !SpellModifiers.PinsAValue(que))) return null;

                var puesto = sobre.Buffs.Poner(new Buff
                {
                    EffectId = efecto.EffectId,
                    EffectUid = efecto.EffectUid,
                    MaxStacks = efecto.MaxStack,
                    Sobre = que,
                    HechizoAfectado = efecto.DiceNum,
                    Cuanto = cuanto,
                    HechizoOrigen = hechizo,
                    NivelOrigen = grado,
                    Quien = quienLanza.Id,
                    Disparador = AlLanzar,
                    CaducaEnRonda = Caduca(efecto, ronda),
                    EmpiezaEnRonda = Empieza(efecto, ronda),
                }, combate.SiguienteEmbrujo);
                return new Outcome
                {
                    Sobre = sobre, Efecto = efecto, Buff = puesto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                };
            }

            // "-N de daños recibidos" right away (105 with a roll, 265): a row with the cut
            // in it, read when a blow lands. The catalogue hangs it on characteristic 16 with
            // no sign, so the generic path made a row that cut nothing.
            if (EsReduccionDeDanoRecibido(efecto.EffectId))
            {
                int corte = DelDado(efecto.DiceNum, efecto.DiceSide, efecto.Value);
                if (corte <= 0) return null;
                var fila = sobre.Buffs.Poner(new Buff
                {
                    EffectId = efecto.EffectId,
                    EffectUid = efecto.EffectUid,
                    MaxStacks = efecto.MaxStack,
                    Cuanto = corte,
                    HechizoOrigen = hechizo,
                    NivelOrigen = grado,
                    Quien = quienLanza.Id,
                    Disparador = AlLanzar,
                    CaducaEnRonda = Caduca(efecto, ronda),
                    EmpiezaEnRonda = Empieza(efecto, ronda),
                }, combate.SiguienteEmbrujo);
                return new Outcome
                {
                    Sobre = sobre, Efecto = efecto, Buff = fila,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                };
            }

            // The steals -- points, characteristics, range -- are two rows, and are done in
            // ResolveEffects through Robar: nothing of them reaches this far.

            // Fixed healing uses one roll for the whole zone. Distance falloff changes only that
            // shared base; Intelligence, flat heals and the target's received-healing multiplier
            // are then applied independently. Power and damage bonuses never participate.
            if (EsCuraFija(efecto.EffectId))
            {
                if (sobre == null || !sobre.IsAlive) return null;

                int baseHeal = sharedHealRoll != int.MinValue
                    ? sharedHealRoll
                    : DelDado(efecto.DiceNum, efecto.DiceSide, efecto.Value);
                baseHeal = ConLosAzares(efecto, quienLanza, sobre, ronda, baseHeal);
                if (baseHeal <= 0) return null;

                // The spell's own basic healing (2935), added to the roll the way 293 is added
                // to a blow's: Coro Estridente's +5, +10, +15 on 25861, the Zurcarák's cards on
                // 12858.
                baseHeal += SpellModifiers.BaseHeal(quienLanza, hechizo, ronda);

                int distance = celdaApuntada >= 0
                    ? Jondo.Unity.World.Maps.MapGeometry.Distance(celdaApuntada, sobre.CellId)
                    : 0;
                baseHeal = ConLaCaidaDeLaZona(baseHeal, efecto, distance);

                // La característica del ELEMENTO del efecto. Ya no es siempre inteligencia: con
                // las cinco curas activas, el agua escala con suerte, el aire con agilidad y la
                // tierra con fuerza. Por eso esto era una búsqueda y no un 15 clavado.
                // 3002 heals in the caster's best element -- the catalogue's element 5, "the
                // best", which is a question to the caster, the same one 2822 asks for a blow.
                int elementOfHeal = efecto.Element >= 0
                    ? efecto.Element
                    : DatabaseManager.EffectElement(efecto.EffectId);
                if (efecto.EffectId == CuraDelMejorElemento || elementOfHeal == ElementoMejor)
                    elementOfHeal = MejorElementoDe(quienLanza, ronda);
                int healCharacteristic = CharacteristicOfElement(elementOfHeal);

                int intelligence = StatOf(quienLanza, healCharacteristic)
                    + quienLanza.Buffs.De(healCharacteristic, ronda);
                int flatHeals = quienLanza.Otra(HealsCharacteristic)
                    + quienLanza.Buffs.De(HealsCharacteristic, ronda);
                int receivedMultiplier = sobre.Buffs.Multiplicador(ReceivedHealingPercent, ronda);
                int points = CalculateFixedHeal(baseHeal, intelligence, flatHeals,
                                                receivedMultiplier);
                return ApplyHealing(combate, quienLanza, sobre, hechizo, grado, efecto,
                                    ronda, points);
            }

            // Las curaciones por tanto por ciento de la vida máxima. El dado es el PORCENTAJE, no
            // los puntos: la Baliza de Supervivencia cura un siete por ciento del tope de quien
            // recibe, y en el cable eso viaja ya resuelto en puntos.
            if (efecto.EffectId == CuraPorcentual)
            {
                if (sobre == null || !sobre.IsAlive) return null;
                int cuanto = efecto.DiceNum != 0 ? efecto.DiceNum : efecto.Value;
                if (cuanto <= 0) return null;

                int puntos = Math.Max(1, sobre.MaxHP * cuanto / 100);
                return ApplyHealing(combate, quienLanza, sobre, hechizo, grado, efecto,
                                    ronda, puntos);
            }

            // Los que MULTIPLICAN: "daños sufridos x110%", "curas recibidas x50%". No tocan
            // ninguna característica, así que se guardan con su porcentaje en el embrujo y quien
            // calcula el golpe los busca por su número de efecto.
            if (DatabaseManager.EsMultiplicador(efecto.EffectId))
            {
                int cuanto = efecto.DiceNum != 0 ? efecto.DiceNum : efecto.Value;
                if (cuanto <= 0) return null;

                var puestoMult = sobre.Buffs.Poner(new Buff
                {
                    EffectId = efecto.EffectId,
                    EffectUid = efecto.EffectUid,
                    MaxStacks = efecto.MaxStack,
                    Cuanto = cuanto,
                    HechizoOrigen = hechizo,
                    NivelOrigen = grado,
                    Quien = quienLanza.Id,
                    Disparador = AlLanzar,
                    CaducaEnRonda = Caduca(efecto, ronda),
                    EmpiezaEnRonda = Empieza(efecto, ronda),
                    Apila = SeApila(efecto),
                }, combate.SiguienteEmbrujo);

                return new Outcome
                {
                    Sobre = sobre, Efecto = efecto, Buff = puestoMult,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                };
            }

            // Invisibility (150) is a flag of one: "jxm 150 f1=1" on a row whose catalogue dice
            // are empty, in the Sram's Invisibilidad capture (frame 10) and in Bruma's (59, 64),
            // with the sheet's 24 at 1 just before it. Rolled as written it came out as nothing,
            // a bare panel row.
            if (efecto.EffectId == EffectSupport.Visibility && efecto.DiceNum == 0 && efecto.Value == 0)
                efecto = ComoSePinta(efecto, efecto.EffectId, 1);

            // Y todo lo demás: lo que toque una característica, con el signo que diga el catálogo.
            //
            // El dado se TIRA. En el catálogo, diceNum es el mínimo y diceSide el máximo —hay
            // efectos de «uno o dos PA» (1 y 2) y de «dos o tres» (2 y 3)—, y aquí se cogía
            // siempre el mínimo, así que un hechizo que puede quitar hasta tres quitaba dos
            // siempre. Cuando diceSide vale cero, la cantidad es fija y no hay nada que tirar.
            var (caracteristica, signo) = DatabaseManager.EffectMeta(efecto.EffectId);
            int cantidad = caracteristica != 0 && signo != 0
                ? DelDado(efecto.DiceNum, efecto.DiceSide, efecto.Value) * signo
                : 0;

            // Y nunca más de los que le quedan. Es lo mismo que ya hacía la rama de ROBAR puntos
            // unas líneas más arriba, y aquí faltaba: por eso un hechizo podía dejar a un bicho
            // sin sus seis puntos de movimiento de una vez. Además, lo que se anuncia tiene que
            // ser lo que de verdad se ha quitado, no lo que se pretendía quitar.
            //
            // And a "retira" is ROLLED, point by point, against the target's dodge: what he
            // dodges goes out as a 308/309 and what lands as a "-N PA/PM" row with the N that
            // landed. Nothing rolled, nothing dodged, every removal took every point: that is
            // how it was, and how it is not in the game.
            int esquivados = 0, efectoEnElCable = 0;
            if (cantidad < 0 && (caracteristica == PuntosDeAccion || caracteristica == PuntosDeMovimiento)
                && EsRetiradaEsquivable(efecto.EffectId))
            {
                int cuentan = PuntosQueCuentan(combate, sobre, caracteristica, ronda);
                int pedidos = Math.Min(-cantidad, cuentan);
                int maximo = caracteristica == PuntosDeAccion ? sobre.MaxAP : sobre.MaxMP;
                int perdidos = PuntosQuePierde(quienLanza, sobre, caracteristica, pedidos, cuentan, maximo, ronda);
                esquivados = pedidos - perdidos;
                pedidos = perdidos;
                efectoEnElCable = PerdidaEnElCable(caracteristica);
                cantidad = -pedidos;
                if (pedidos == 0)
                {
                    if (esquivados == 0) return null;
                    return new Outcome
                    {
                        Sobre = sobre, Efecto = efecto,
                        HechizoOrigen = hechizo, NivelOrigen = grado, PuntosEsquivados = esquivados,
                    };
                }
            }
            // A flat "-N PA/PM" (168, 169) is a row with the catalogue's own N, not a removal
            // rolled against dodge nor cut to what the target has: Influencia's "-100 PM" goes
            // out as a row of 100 on a monster with three in its capture, and the points in
            // hand simply stop at zero. Cut to what he had, a puch with no MP got no row at all.

            // Y si NO toca ninguna característica, tampoco se tira.
            //
            // Aquí estaba el panel vacío. Este método acababa en un "si no hay característica,
            // nada", y con eso desaparecían CATORCE familias enteras de las que el servidor real
            // sí anuncia: el 1160 y el 1163 de las balizas, el 792 que encadena, el 141 que mata,
            // el 406 que quita los efectos de un hechizo, el 3793, el 1159 de las curas, el 289 de
            // la línea de visión… todas las que en el catálogo tienen Characteristic 0, que son
            // justamente las de clase. Contado sobre las capturas del Ocra: de los treinta y dos
            // efectos que el servidor manda al panel, catorce se perdían por esta línea.
            //
            // Ahora el que no se sepa aplicar se anota y SE MANDA igual, con lo que trae puesto.
            bool soloPanel = caracteristica == 0 || signo == 0 || cantidad == 0;

            // Points that come LATER wait: "aumenta los PM del lanzador en el siguiente turno"
            // is a 128 with a delay of one, and applied now it was one more step in the turn
            // of the cast and none in the next.
            if (!soloPanel && efecto.Delay > 0
                && (caracteristica == PuntosDeAccion || caracteristica == PuntosDeMovimiento))
            {
                return Pendiente(combate, quienLanza, sobre, hechizo, grado, efecto, ronda,
                                 caracteristica, cantidad);
            }

            var embrujo = sobre.Buffs.Poner(new Buff
            {
                EffectId = efecto.EffectId,
                EffectUid = efecto.EffectUid,
                MaxStacks = efecto.MaxStack,
                Caracteristica = soloPanel ? 0 : caracteristica,
                Cuanto = soloPanel ? 0 : cantidad,
                HechizoOrigen = hechizo,
                NivelOrigen = grado,
                Quien = quienLanza.Id,
                Disparador = AlLanzar,
                CaducaEnRonda = Caduca(efecto, ronda),
                EmpiezaEnRonda = Empieza(efecto, ronda),
                Apila = SeApila(efecto),
            }, combate.SiguienteEmbrujo);

            return new Outcome
            {
                Sobre = sobre,
                Efecto = efecto,
                Buff = embrujo,
                Caracteristica = soloPanel ? 0 : caracteristica,
                Cuanto = soloPanel ? 0 : cantidad,
                HechizoOrigen = hechizo,
                NivelOrigen = grado,
                SoloParaElPanel = soloPanel,
                PuntosEsquivados = esquivados,
                EfectoEnElCable = efectoEnElCable,
            };
        }

        /// <summary>
        /// One steal on one target: the points leave him as the characteristic's malus row and
        /// reach the caster as its bonus row, the two with the steal's uid, dice and duration.
        /// Points of action and movement are dodged like a removal -- what is dodged goes out as
        /// a jwe 308/309 -- and never more than he counts with; a characteristic is taken whole.
        /// </summary>
        private static List<Outcome> Robar(FightInstance combate, Fighter quienLanza, Fighter sobre,
                                           int hechizo, int grado, SpellEffect efecto, int ronda,
                                           Steals.Steal robo)
        {
            var fuera = new List<Outcome>();
            if (sobre == null || !sobre.IsAlive || quienLanza == null || sobre == quienLanza) return fuera;

            int cuantos = DelDado(efecto.DiceNum, efecto.DiceSide, efecto.Value);
            if (cuantos <= 0) return fuera;

            int esquivados = 0;
            if (robo.Characteristic == PuntosDeAccion || robo.Characteristic == PuntosDeMovimiento)
            {
                int hay = PuntosQueCuentan(combate, sobre, robo.Characteristic, ronda);
                int pedidos = Math.Min(cuantos, hay);
                if (pedidos <= 0) return fuera;
                int maximo = robo.Characteristic == PuntosDeAccion ? sobre.MaxAP : sobre.MaxMP;
                int perdidos = EsRetiradaEsquivable(efecto.EffectId)
                    ? PuntosQuePierde(quienLanza, sobre, robo.Characteristic, pedidos, hay, maximo, ronda)
                    : pedidos;
                esquivados = pedidos - perdidos;
                cuantos = perdidos;
                if (cuantos == 0)
                {
                    fuera.Add(new Outcome
                    {
                        Sobre = sobre, Caster = quienLanza, Efecto = efecto,
                        HechizoOrigen = hechizo, NivelOrigen = grado, PuntosEsquivados = esquivados,
                    });
                    return fuera;
                }
            }

            Outcome Fila(Fighter portador, int efectoDelCable, int cuanto, int esquivadosAntes)
            {
                var fila = portador.Buffs.Poner(new Buff
                {
                    EffectId = efectoDelCable,
                    EffectUid = efecto.EffectUid,
                    MaxStacks = efecto.MaxStack,
                    Caracteristica = robo.Characteristic,
                    Cuanto = cuanto,
                    Dado = Math.Abs(cuanto),
                    Dispellable = efecto.Dispellable,
                    HechizoOrigen = hechizo,
                    NivelOrigen = grado,
                    Quien = quienLanza.Id,
                    Disparador = AlLanzar,
                    CaducaEnRonda = Caduca(efecto, ronda),
                    EmpiezaEnRonda = Empieza(efecto, ronda),
                }, combate.SiguienteEmbrujo);
                var hecho = new Outcome
                {
                    Sobre = portador, Caster = quienLanza,
                    Efecto = ComoSePinta(efecto, efectoDelCable, Math.Abs(cuanto)),
                    Buff = fila,
                    Caracteristica = robo.Characteristic, Cuanto = cuanto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    PuntosEsquivados = esquivadosAntes,
                    Relevados = new List<Buff>(portador.Buffs.Relevados),
                };
                portador.Buffs.Relevados.Clear();
                return hecho;
            }

            fuera.Add(Fila(sobre, robo.MalusEffect, -cuantos, esquivados));
            fuera.Add(Fila(quienLanza, robo.BonusEffect, cuantos, 0));
            return fuera;
        }

        /// <summary>
        /// Applies an immediate heal or records it as a one-shot buff when the catalogue carries a
        /// delay. A delayed heal is scheduled even while the target is full because it may be hurt
        /// before the activation round; missing-life capping therefore happens only on activation.
        /// </summary>
        private static Outcome ApplyHealing(FightInstance combat, Fighter caster, Fighter target,
                                            int spell, int grade, SpellEffect effect, int round,
                                            int points)
        {
            if (target == null || !target.IsAlive || points <= 0) return null;

            // Through portals a heal grows as a blow does: "los daños y las curas de los
            // hechizos proyectados por portales aumentan" (Portal's own text). INFERRED as the
            // same percent, PortalNetwork.BonusPercent.
            if (combat != null && combat.PortalBonusPercent != 0)
                points = Math.Max(0, (int)Math.Round(points * (100 + combat.PortalBonusPercent) / 100.0));

            if (effect.Delay > 0)
            {
                var pending = target.Buffs.Poner(new Buff
                {
                    EffectId = effect.EffectId,
                    EffectUid = effect.EffectUid,
                    MaxStacks = effect.MaxStack,
                    Cuanto = points,
                    PendingHealPoints = points,
                    HechizoOrigen = spell,
                    NivelOrigen = grade,
                    Quien = caster.Id,
                    Disparador = AlLanzar,
                    CaducaEnRonda = Caduca(effect, round),
                    EmpiezaEnRonda = Empieza(effect, round),
                    Apila = SeApila(effect),
                }, combat.SiguienteEmbrujo);

                return new Outcome
                {
                    Sobre = target,
                    Efecto = effect,
                    Buff = pending,
                    HechizoOrigen = spell,
                    NivelOrigen = grade,
                };
            }

            int applied = Math.Min(points, Math.Max(0, target.MaxHP - target.CurrentHP));
            if (applied <= 0) return null;

            target.CurrentHP += applied;
            return new Outcome
            {
                Sobre = target,
                Efecto = effect,
                HechizoOrigen = spell,
                NivelOrigen = grade,
                Cura = applied,
            };
        }

        /// <summary>
        /// Activates every delayed one-shot heal due in this combat. Dead targets are deliberately
        /// skipped: ordinary healing is not a resurrection path. The pending buff is consumed in
        /// either case so it cannot fire again on the next fighter's turn in the same round.
        /// </summary>
        internal static List<DelayedHealOutcome> ActivateDelayedHealing(FightInstance combat, int round)
        {
            var results = new List<DelayedHealOutcome>();
            foreach (var target in Todos(combat))
            {
                foreach (var pending in target.Buffs.TakeDueHealing(round))
                {
                    int healed = 0;
                    if (target.IsAlive)
                    {
                        healed = Math.Min(pending.PendingHealPoints,
                                          Math.Max(0, target.MaxHP - target.CurrentHP));
                        target.CurrentHP += healed;
                    }

                    results.Add(new DelayedHealOutcome
                    {
                        Target = target,
                        CasterId = pending.Quien,
                        Buff = pending,
                        Healed = healed,
                    });
                }
            }
            return results;
        }

        /// <summary>
        /// Takes every waiting row whose round has come and turns the lasting ones into live
        /// rows: a +1 MP becomes a visible row of the same effect, from this round for its
        /// duration, naming the waiting row as its parent. A kill or a marker leaves nothing
        /// behind for the fight to keep; the caller deals the death and sends the jwe.
        /// </summary>
        internal static List<PendingActivation> ActivateDuePending(FightInstance combat, int round)
        {
            var results = new List<PendingActivation>();
            foreach (var target in Todos(combat))
            {
                if (target == null) continue;
                foreach (var waiting in target.Buffs.TakeDuePending(round))
                {
                    Buff live = null;
                    if (waiting.EffectId == QuitarEstado)
                    {
                        results.Add(new PendingActivation
                        {
                            Target = target, CasterId = waiting.Quien, Waiting = waiting,
                            Quitados = target.Buffs.QuitarEstadoConEmbrujos(waiting.Valor != 0 ? waiting.Valor : waiting.Dado),
                        });
                        continue;
                    }
                    if (waiting.EffectId == PonerEstado && target.IsAlive)
                        target.Buffs.PonerEstado(waiting.Valor != 0 ? waiting.Valor : waiting.Dado);

                    if (waiting.EffectId != MataAlObjetivo && waiting.EffectId != MarcadorDeGuion
                        && !EsDeLaFamiliaDeSublanzar(waiting.EffectId) && target.IsAlive)
                    {
                        live = target.Buffs.Poner(new Buff
                        {
                            EffectId = waiting.EffectId,
                            EffectUid = waiting.EffectUid,
                            MaxStacks = waiting.MaxStacks,
                            Caracteristica = waiting.Caracteristica,
                            Cuanto = waiting.Cuanto,
                            Dado = waiting.Dado,
                            Cara = waiting.Cara,
                            Valor = waiting.Valor,
                            Dispellable = waiting.Dispellable,
                            HechizoOrigen = waiting.HechizoOrigen,
                            NivelOrigen = waiting.NivelOrigen,
                            Quien = waiting.Quien,
                            Disparador = Esperando,
                            EmpiezaEnRonda = round,
                            CaducaEnRonda = waiting.Duracion < 0 ? -1 : round + Math.Max(1, waiting.Duracion),
                            Padre = waiting.Numero,
                            Apila = true,
                            Estado = waiting.EffectId == PonerEstado ? (waiting.Valor != 0 ? waiting.Valor : waiting.Dado) : 0,
                        }, combat.SiguienteEmbrujo);
                    }
                    results.Add(new PendingActivation
                    {
                        Target = target, CasterId = waiting.Quien, Waiting = waiting, Live = live,
                    });
                }
            }
            return results;
        }

        /// <summary>
        /// La lista de efectos que toca, según haya salido crítico o no.
        ///
        /// Un hechizo trae DOS listas en la base y la crítica no es "la normal multiplicada": es
        /// otra tanda entera con sus propios números. Flecha Helada pega de 21 a 24 y en crítico
        /// de 25 a 29; Tiros Potentes da 250 de potencia y en crítico 300. Si la lista crítica
        /// viene vacía —hay hechizos que no la tienen— se usa la de siempre.
        /// </summary>
        public static IReadOnlyList<SpellEffect> EfectosDeLaTirada(int hechizo, int grado, bool critico)
        {
            if (!critico) return SpellEffects.De(hechizo, grado);
            var criticos = SpellEffects.Criticos(hechizo, grado);
            return criticos.Count > 0 ? criticos : SpellEffects.De(hechizo, grado);
        }

        /// <summary>
        /// El sorteo de los efectos que van a suertes, y devuelve LOS QUE NO SALEN.
        ///
        /// Un efecto puede traer una probabilidad en su campo <c>random</c>, y los que la traen se
        /// agrupan por su campo <c>group</c>. Hay dos maneras, y se distinguen por lo que suman:
        ///
        ///   suman 100  -> es un sorteo: sale UNO, con el peso de cada uno. Es lo que hace
        ///                 Invocación de Arakna, que trae dos efectos 181 —la plantilla 246 al
        ///                 ochenta por ciento y la 2630, la Arakna mayor, al veinte—. Sin esto
        ///                 salían las dos a la vez, que es lo que estaba pasando.
        ///   no suman 100 -> cada uno va por su cuenta y ocurre con su propia probabilidad.
        ///
        /// Contado sobre la base entera: hay 1.335 niveles de hechizo con efectos de este tipo, y
        /// en 1.129 de sus grupos la suma es exactamente cien.
        /// </summary>
        /// <summary>
        /// The effects of a cast that really run: the rows of the grade with the random ones
        /// drawn ONCE. The blows and the rows of one cast come out of the same draw, so that
        /// Bumerán Pérfido steals in one element and boosts the characteristic of that same
        /// element, which is what its sheet says.
        /// </summary>
        public static IReadOnlyList<SpellEffect> EfectosSorteados(int hechizo, int grado, bool critico)
        {
            var todos = EfectosDeLaTirada(hechizo, grado, critico);
            var fuera = Sortear(todos);
            if (fuera.Count == 0) return todos;
            var quedan = new List<SpellEffect>(todos.Count);
            foreach (var e in todos) if (!fuera.Contains(e)) quedan.Add(e);
            return quedan;
        }

        /// <summary>
        /// The draw of the random effects of a spell: which ones stay OUT of this cast.
        /// </summary>
        /// <remarks>
        /// The catalogue writes one draw per spell level, not one per group: the <c>random</c>
        /// of every random row of a level adds up to 100 in 1,602 of the 1,603 levels that
        /// carry any (Molestia Búlbuca's two rows of 50, Escarainvoc's four of 25, Cara
        /// Oculta's 96 of 4.17; Guerrillero's grade 2 stops at 55.6 and is drawn over what it
        /// has), and the <c>group</c> says which rows come TOGETHER once one of them is
        /// drawn -- the rows of a group always carry the same share.
        /// Bumerán Pérfido is eight rows of 12.5 in four groups -- a life steal and the matching
        /// characteristic, per element -- so each element has a quarter of the draw and brings
        /// its characteristic with it. Group zero is no group: each of its rows stands alone,
        /// which is what Invocación de Arakna's 80/20 needs. Drawn per group, the eight rows of
        /// 12.5 summed to 25 per group and fell through to eight independent rolls, and the
        /// spell mostly did nothing.
        /// </remarks>
        private static HashSet<SpellEffect> Sortear(IReadOnlyList<SpellEffect> efectos)
        {
            var fuera = new HashSet<SpellEffect>();
            var alAzar = new List<SpellEffect>();
            double suma = 0;
            foreach (var efecto in efectos)
            {
                if (efecto.Probabilidad <= 0) continue;
                alAzar.Add(efecto);
                suma += efecto.Probabilidad;
            }
            if (alAzar.Count == 0) return fuera;

            if (Math.Abs(suma - 100.0) < 0.5 || alAzar.Count > 1)
            {
                // One draw over the whole level, weighted by each row's share.
                double tirada = SiguienteAzar() * suma;
                double acumulado = 0;
                SpellEffect elegido = alAzar[alAzar.Count - 1];
                foreach (var e in alAzar)
                {
                    acumulado += e.Probabilidad;
                    if (tirada <= acumulado) { elegido = e; break; }
                }
                foreach (var e in alAzar)
                {
                    bool conElElegido = e == elegido || (elegido.Sorteo != 0 && e.Sorteo == elegido.Sorteo);
                    if (!conElElegido) fuera.Add(e);
                }
                return fuera;
            }

            // A single random row that is not the whole draw: its own roll.
            if (SiguienteAzar() * 100.0 > alAzar[0].Probabilidad) fuera.Add(alAzar[0]);
            return fuera;
        }

        private static readonly Random _azar = new Random();

        /// <summary>
        /// Lo que sale del dado de un efecto.
        ///
        /// En el catálogo del cliente, <c>diceNum</c> es el mínimo y <c>diceSide</c> el máximo:
        /// hay efectos de «uno o dos puntos de acción» (1 y 2) y de «dos o tres» (2 y 3). Con
        /// diceSide a cero la cantidad es fija, y con los dos a cero se usa el valor suelto.
        /// </summary>
        private static int DelDado(int minimo, int maximo, int valor)
        {
            if (minimo == 0 && maximo == 0) return valor;
            if (maximo > minimo)
            {
                lock (_azar) return _azar.Next(minimo, maximo + 1);
            }
            return minimo != 0 ? minimo : valor;
        }

        private static double SiguienteAzar()
        {
            lock (_azar) return _azar.NextDouble();
        }

        /// <summary>
        /// The dice behind AP and MP removal, one draw in [0, 1) per point. Tests set it to
        /// decide the outcome; the fight uses the engine's own random source.
        /// </summary>
        internal static Func<double> DadoDeRetirada { get; set; } = SiguienteAzar;

        /// <summary>The fight's own dice again, for a test that borrowed them.</summary>
        internal static void DadoDeRetiradaPorDefecto() => DadoDeRetirada = SiguienteAzar;

        /// <summary>
        /// The removal effects that a target can DODGE: "retira PA/PM" (1079, 1080, 101, 127)
        /// and the steals (84, 77). The plain "-N PA/PM" boosts (168, 169) are not rolled: they
        /// are what a spell does to its own caster or what nothing avoids.
        /// </summary>
        private static bool EsRetiradaEsquivable(int efecto)
            => efecto is 1079 or 1080 or 101 or 127 or 84 or 77;

        /// <summary>The removal effect a landed "retira" is announced as: -N PA (168) or -N PM (169).</summary>
        internal static int PerdidaEnElCable(int caracteristica)
            => caracteristica == PuntosDeAccion ? 168 : 169;

        /// <summary>
        /// The points a fighter counts with right now against a removal: the ones left in his
        /// hand while he plays, and the ones his next turn starts with -- his maximum plus the
        /// live rows -- the rest of the time. It used to be his current points always, which
        /// outside his turn are what he had LEFT when his last turn ended: an Ocra who had spent
        /// six of seven could lose one at most, and the real server's sheet after a removal on
        /// a monster that is not playing reads its maximum less the removal.
        /// </summary>
        internal static int PuntosQueCuentan(FightInstance combate, Fighter sobre, int caracteristica, int ronda)
        {
            bool enSuTurno = combate.CurrentFighter == sobre;
            if (caracteristica == PuntosDeAccion)
                return enSuTurno ? sobre.CurrentAP : Math.Max(0, sobre.MaxAP + sobre.Buffs.De(PuntosDeAccion, ronda));
            return enSuTurno ? sobre.CurrentMP : Math.Max(0, sobre.MaxMP + sobre.Buffs.De(PuntosDeMovimiento, ronda));
        }

        /// <summary>
        /// How many of the points a removal asks for are lost, the rest being dodged. One roll
        /// per point, and the target's remaining points go down with each one lost:
        ///
        ///   P(lose the point) = (points left / maximum) × (removal + 2) / (dodge + 2) × 1/2,
        ///                        never under 10 % nor over 90 %
        ///
        /// with the caster's "retira PA/PM" (82/83) against the target's "esquiva PA/PM"
        /// (27/28), each a tenth of wisdom plus what the gear and the live rows say. The
        /// game's own formula, and the reason a "-2 PM" against a well-dodging target is
        /// usually a miss, sometimes a single point, rarely both.
        /// </summary>
        internal static int PuntosQuePierde(Fighter quienLanza, Fighter sobre, int caracteristica,
                                            int pedidos, int cuentan, int maximo, int ronda)
        {
            int retirada = quienLanza.Otra(caracteristica == PuntosDeAccion ? RetiraPA : RetiraPM)
                         + quienLanza.Buffs.De(caracteristica == PuntosDeAccion ? RetiraPA : RetiraPM, ronda);
            int esquiva = sobre.Otra(caracteristica == PuntosDeAccion ? EsquivaPA : EsquivaPM)
                        + sobre.Buffs.De(caracteristica == PuntosDeAccion ? EsquivaPA : EsquivaPM, ronda);
            int perdidos = 0;
            for (int i = 0; i < pedidos && cuentan > 0; i++)
            {
                double p = (double)cuentan / Math.Max(1, maximo)
                         * (Math.Max(0, retirada) + 2.0) / (Math.Max(0, esquiva) + 2.0) * 0.5;
                p = Math.Clamp(p, 0.10, 0.90);
                if (DadoDeRetirada() < p) { perdidos++; cuentan--; }
            }
            return perdidos;
        }

        /// <summary>The catalogue's numbers for the four: retira PA/PM and esquiva PA/PM.</summary>
        public const int RetiraPA = 82;
        public const int RetiraPM = 83;
        public const int EsquivaPA = 27;
        public const int EsquivaPM = 28;

        /// <summary>
        /// Si lo que deja este efecto se acumula en vez de sustituir a lo que ya hubiera.
        ///
        /// Se acumula lo que salta CADA VEZ QUE PASA ALGO, o sea lo que no tiene el disparador de
        /// "al lanzar": el Centinela se come uno de alcance por paso, y tres pasos son tres menos.
        /// Lo que sí es de "al lanzar" se refresca, que es lo que hace Flecha Helada al repetirse.
        /// </summary>
        private static bool SeApila(SpellEffect efecto)
        {
            foreach (var d in efecto.Disparadores())
            {
                if (string.Equals(d, AlLanzar, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        /// <summary>
        /// En qué ronda se cae un efecto. Duración negativa quiere decir "mientras dure el
        /// combate"; cero, que es de un vistazo y no deja embrujo que dure.
        /// </summary>
        private static int Caduca(SpellEffect efecto, int ronda)
            => efecto.Duration < 0 ? -1 : ronda + efecto.Delay + Math.Max(1, efecto.Duration);

        /// <summary>
        /// La ronda en la que un efecto empieza a valer: la del lanzamiento más su retardo.
        ///
        /// Comprobado contra la captura de la Flecha Castigadora, lanzada en la ronda 4: el
        /// embrujo de retardo 1 vive la ronda 5 y se cae al entrar en la 6, y el de retardo 2 vive
        /// la 6.
        /// </summary>
        private static int Empieza(SpellEffect efecto, int ronda) => ronda + efecto.Delay;
    }
}

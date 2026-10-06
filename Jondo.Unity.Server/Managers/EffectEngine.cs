using Jondo.Unity.World.Combat;
using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What has to be done with an already resolved effect: apply it and tell the client.
    /// The engine decides WHAT happens; whoever calls it decides how it is sent on the wire.
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

        /// <summary>If the effect changes a characteristic on the spot, which one and by how much.</summary>
        public int Caracteristica { get; init; }
        public int Cuanto { get; init; }

        /// <summary>If the effect chains another spell, which one and at which grade.</summary>
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
        /// If the effect moves somebody, from where to where. Minus one when it moves nobody.
        /// </summary>
        public int CasillaDesde { get; init; } = -1;
        public int CasillaHasta { get; init; } = -1;
        public bool Mueve => CasillaHasta >= 0 && CasillaHasta != CasillaDesde;

        /// <summary>
        /// True when the engine does not know yet what this effect does but DOES know that it lasts and that the
        /// client draws it. It is sent to the panel all the same and noted in the log, instead of being thrown
        /// away silently, which is what used to be done with fourteen whole families.
        /// </summary>
        public bool SoloParaElPanel { get; init; }

        /// <summary>The creature template that has to be brought onto the board, if the effect summons.</summary>
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

        /// <summary>Effect 141: kills the target, with no calculation in between.</summary>
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

        /// <summary>The shield points this effect has set. Zero when it sets none.</summary>
        public int Escudo { get; init; }

        /// <summary>Life that goes without being a hit: the «-N% HP».</summary>
        public int VidaQueSeVa { get; init; }

        /// <summary>Life the caster passes to the target.</summary>
        public int VidaTransferida { get; init; }

        /// <summary>How many buffs an effect that shortens durations has taken down.</summary>
        public int EmbrujosCaidos { get; init; }

        /// <summary>That the summon comes out where the one who has just died was, and not beside him.</summary>
        public bool EnLaCasillaDelMuerto { get; init; }

        /// <summary>What this effect has left on the ground, if it left anything.</summary>
        public Jondo.Unity.World.Fights.Glifo Glifo { get; init; }

        /// <summary>
        /// The SECOND displacement, when the effect moves two. Minus one when not.
        /// </summary>
        /// <remarks>
        /// The position swap asks for it, which is the only one that moves the caster and the target at once.
        /// Announcing only one of the two leaves the client with somebody drawn where he no longer is.
        /// </remarks>
        public Fighter Tambien { get; init; }
        public int CasillaDesdeDelOtro { get; init; } = -1;
        public int CasillaHastaDelOtro { get; init; } = -1;
        public bool MueveTambien => Tambien != null && CasillaHastaDelOtro >= 0
                                    && CasillaHastaDelOtro != CasillaDesdeDelOtro;

        /// <summary>The life points given back, if the effect heals.</summary>
        public int Cura { get; init; }

        /// <summary>
        /// The damage of having crashed when pushed, ALREADY WORKED OUT BUT NOT APPLIED.
        ///
        /// Taking life is the fight driver's job: it is what clips by the remaining life, erodes, announces the
        /// death and judges the challenges. Here only how much is said.
        /// </summary>
        public int CollisionDamage { get; init; }

        /// <summary>Whoever served as the wall, if what stopped the push was another fighter.</summary>
        public Fighter Blocker { get; init; }

        /// <summary>
        /// What the wall takes: HALF the pushed one's damage, rounding down.
        ///
        /// And it is half of that damage, not a new calculation with the wall's characteristics: measured in the
        /// koliseo, the 497/248 pair comes out with the VICTIM's push resistance included in the 497.
        /// Recalculating it with the blocker's, the numbers do not add up.
        /// </summary>
        public int CollisionDamageToBlocker { get; init; }

        /// <summary>Buffs removed by an effect 406 or by a state being removed.</summary>
        public IReadOnlyList<Buff> BuffsQuitados { get; init; } = Array.Empty<Buff>();

        /// <summary>
        /// The rows the new row REPLACED on the bearer -- the old copy of a spell that does not
        /// stack, the oldest of one that stacks to a cap. Announced gone, jya and jwe 514, before
        /// the new row, as the real server does when Espada del Juicio is cast again.
        /// </summary>
        public IReadOnlyList<Buff> Relevados { get; set; } = Array.Empty<Buff>();

        /// <summary>Temporary appearance requested by effect 335, or zero.</summary>
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
    /// The effect engine: it takes the entries of a spell's EffectsJson and turns them into things that
    /// happen to somebody.
    ///
    /// There is not a single hand-written spell in here. Everything comes from two places in the database:
    ///
    ///   - <c>SpellLevels.EffectsJson</c>, which says which effects the spell has, how much, to whom
    ///     (<c>targetMask</c>), when (<c>triggers</c>) and for how many turns.
    ///   - The client's <c>Effects</c> table, which says which characteristic each effect number touches
    ///     and with what sign (<c>BonusType</c>).
    ///
    /// With that, things like these come out by themselves, which before had to be written one by one:
    ///
    ///   Flecha Helada  = 1079 (removes 2 AP) + 96 (21-24 water) + 293 (+8 basic damage of
    ///                    Flecha Helada, three turns, on oneself)
    ///   Disparos Lejanos = 280 and 281 repeated (+3 minimum range and +6 maximum) on a long
    ///                    list of spells, one turn
    ///   Ochre Dofus    = the item gives the "spell" 8394 through its effect 1175; that spell, at its
    ///                    grade 1, says "when I am hit cast my grade 2" and "at the start of the turn
    ///                    cast my grade 3"; grade 2 sets state 519 and grade 3 gives +1 AP if that state
    ///                    is NOT present, +20 dodge if it is, and removes it at the end of the turn.
    /// </summary>
    public static class EffectEngine
    {
        // The effect numbers the engine understands in a special way. The rest are resolved by
        // their characteristic in the catalogue.
        //
        // The ones that hit are TEN, not five: 91 to 95 are the LIFE STEAL ones and 96 to 100
        // the plain damage ones, one per element in each run. The emulator only looked at 96 to
        // 100, so spells like Flecha Voraz -- which hits with 94, fire steal -- or Ojo de Topo --
        // 91, water steal -- fitted nowhere and the damage came from where it should not.
        //
        // And the element does not have to be deduced from the number: the catalogue says it in
        // its ElementId column, with 0 neutral, 1 earth, 2 fire, 3 water and 4 air.
        private const int DanoPrimero = EffectSupport.FirstDamage;
        private const int DanoUltimo = EffectSupport.LastDamage;

        /// <summary>
        /// The ones that hit according to how much the target has ERODED. There are five, one per element, in
        /// two runs: 1092 to 1096 and 1118 to 1122. In their description the die is not the damage but the
        /// percentage.
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

        /// <summary>Does this effect hit?</summary>
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
        /// The blows a spell deals: one for each damage effect that reaches the target.
        ///
        /// It is resolved with the same masks as the rest -- hence Flecha Voraz hits 11-13 or 34-38 depending on
        /// the state the target carries -- and the element is returned already resolved. If the spell has not a
        /// single damage effect, it returns nothing: Tiro de Repliegue only moves the caster away and must not
        /// take a single life point off anybody.
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

                // The element is said by the spell itself in its effectElement; if it does not carry
                // one, by the catalogue through the effect number.
                int elemento = efecto.Element >= 0 ? efecto.Element
                                                   : DatabaseManager.EffectElement(efecto.EffectId);

                // The «best element» is not an element: it is a question to the caster. It is resolved
                // here, with the buffs on, because a spell that raises your agility halfway through the
                // fight can change which is your best element, and that is exactly what it is cast for.
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

                    // How many cells from the centre of the area he is. Damage drops as he gets further
                    // away, and the spell itself says by how much.
                    int lejos = celdaApuntada >= 0
                        ? Jondo.Unity.World.Maps.MapGeometry.Distance(celdaApuntada, sobre.CellId)
                        : 0;
                    fuera.Add((fila, elemento, sobre, lejos));
                }
            }
            return fuera;
        }

        /// <summary>
        /// The damage left for somebody <paramref name="lejos"/> cells from the centre.
        ///
        /// A percentage is lost per cell, with a cap on steps, and both numbers come with the spell in its
        /// <c>zoneDescr</c>: the Cra's that fall off do so at ten per cent with a cap of four, so from the
        /// fifth ring on it no longer drops.
        ///
        /// The die roll is ONE for the whole cast: if "25 to 30" rolls 26, 26 goes in at the centre and at one
        /// cell, 90% of that.
        /// </summary>
        public static int ConLaCaidaDeLaZona(int dano, SpellEffect efecto, int lejos)
        {
            if (dano <= 0 || lejos <= 0 || efecto.PasoDeCaida <= 0) return dano;

            int pasos = efecto.TopeDeCaida > 0 ? Math.Min(lejos, efecto.TopeDeCaida) : lejos;
            double queda = Math.Pow(1.0 - efecto.PasoDeCaida / 100.0, pasos);
            return Math.Max(0, (int)Math.Round(dano * queda));
        }
        private const int Empujar = EffectSupport.Push;

        /// <summary>84, «Empuje» (push): the pusher's FLAT addition. The percentage is 158.</summary>
        private const int DanoDeEmpuje = 84;

        /// <summary>85, «Empuje (fijo)» (fixed push): the receiver's FLAT subtraction.</summary>
        private const int ResistenciaAlEmpuje = 85;

        /// <summary>The state that pins one to the spot. Not to be confused with characteristic 97.</summary>
        private const int Indesplazable = 97;

        /// <summary>
        /// The fixed term of the collision damage formula.
        ///
        /// It comes from no client data -- neither the constants bundle nor the 38 lua formulas have anything
        /// about fights --: it comes from measuring. With a level 200 caster with no bonuses, the parenthesis
        /// is 132 and the damage per cell 33.
        /// </summary>
        private const int BaseDelEmpuje = 32;

        /// <summary>
        /// The damage of crashing when pushed.
        ///
        ///   damage = cellsNotCovered × (level/2 + the pusher's 84
        ///                               − the receiver's 85 + 32) / 4
        ///
        /// It is in a separate method so that the regression guard can check it against the samples that
        /// measured it, which are in AssertPushDamageMatchesTheCapture.
        ///
        /// The division by four goes AT THE END, on the product: with the resistance inside the parenthesis and
        /// a single division, the koliseo gives 331 for two cells, which is what was measured. Subtracting
        /// outside it would give 316.
        /// </summary>
        public static int DanoDeColision(int nivelDelQueEmpuja, int suEmpuje, int laResistencia,
                                         int casillasSinRecorrer)
        {
            if (casillasSinRecorrer <= 0) return 0;

            int porCasilla = nivelDelQueEmpuja / 2 + suEmpuje - laResistencia + BaseDelEmpuje;
            return Math.Max(0, casillasSinRecorrer * porCasilla / 4);
        }
        /// <summary>«Teleports to the target cell». 425 spells carry it.</summary>
        /// <remarks>
        /// It is not a push of many cells: it does not travel the path, so it neither crashes nor does
        /// collision damage, and it does not care whether something is in the way. It only cares about the
        /// destination cell, which has to be free and walkable.
        /// </remarks>
        /// <summary>«N% of the level as a shield» (234 spells) and «N% of HP as a shield» (167).</summary>
        /// <remarks>
        /// The percentage goes in the DIE, not in the value: Caparazón carries diceNum 150 and Soldagüino 200 on
        /// the level; Bendición Maravillosa carries 10 and Coraza de Dopeul 20 on the life. The value is zero in
        /// the six that were read.
        ///
        /// The shield is not life and is not healed: that is why it lives in the fighter's own bag and not in
        /// CurrentHP.
        /// </remarks>
        /// <summary>«Gives back N AP» (163 spells). The number goes in the die, the mask is «C».</summary>
        /// <remarks>
        /// Doom and Matanza carry it, which cost 1 AP and give it back, so they can be chained. It is not an AP
        /// boost with a duration: it is an immediate refund, and that is why it does not go through the buff.
        /// </remarks>
        /// <summary>
        /// What is put on the ground: aura glyph, turn-start glyph, trap and rune.
        /// </summary>
        /// <remarks>
        /// 623 spells among the four, and all four with THE SAME measured shape:
        ///
        ///   diceNum   the spell it casts when it fires
        ///   diceSide  its grade
        ///   value     the colour in RGB -- the Avispero carries 16777215, pure white --
        ///   duration  the rounds; -1 means it does not drop by itself
        ///   zoneDescr the footprint around the aimed cell
        ///
        /// The only thing that changes is when they fire, so they go by a single road with four triggers
        /// instead of by four roads with the same body.
        /// </remarks>
        /// <summary>What a sub-cast's child spell aims at.</summary>
        private enum Apunta
        {
            /// <summary>At the candidate the mask has just picked.</summary>
            AlCandidato,

            /// <summary>Back at whoever cast the parent spell.</summary>
            AlLanzadorPadre,

            /// <summary>At the cell the parent aimed at, resolved AGAIN at that moment.</summary>
            ALaCasillaDelPadre,

            /// <summary>At the nearest one in the area.</summary>
            AlMasCercano,

            /// <summary>
            /// At the source of what set the parent off -- the one whose blow fired the trigger,
            /// or the parent's caster at a cast. 1019 casts the child there.
            /// </summary>
            AlOrigen,
        }

        /// <summary>A row of the «make another spell be cast» family.</summary>
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

            /// <summary>Whether whoever casts the child is the candidate instead of the parent's caster.</summary>
            public bool LanzaElCandidato { get; }

            public Apunta Apunta { get; }

            /// <summary>Whether the value field limits how many candidates are taken.</summary>
            public bool TopePorValor { get; }

            /// <summary>The three the old code already resolves, which are not touched today.</summary>
            public bool YaLoHaceElCaminoViejo { get; }
        }

        /// <summary>
        /// The whole «make another spell be cast» family, in a single table.
        /// </summary>
        /// <remarks>
        /// They are NINE effects and not nine mechanics: the same resolution with three parameters -- who casts
        /// the child, what it aims at, and how many candidates it takes --. In all of them, diceNum is the child
        /// spell and diceSide its grade, and in all of them the child is FREE: it costs no AP.
        ///
        /// The table comes from the census of the real game's 431 captures, pairing each cast announcement with
        /// the parent that produced it. Two independent agents redid the corpus and got the same totals --
        /// 37,947 jwe frames, 21,307 casts --, and these are the counts:
        ///
        ///   effect   n      same caster      same cell       target==caster
        ///   792      6332   4447             3560            6269
        ///   1160     3826   3783             1772             918
        ///   2160      332    332               54              18
        ///   2792       10      0                2              10
        ///   2793      323     68               74             320
        ///   2794      235    182              228              79
        ///   2795        6      0                6               0
        ///
        /// And 1017 apart, with 97 chainings and ZERO counterexamples: the child's target is the parent's
        /// caster in 97 of 97, and the child's caster is not in 97 of 97.
        ///
        /// The control that settles it: with THE SAME mask «h,P», 792 does not invert once in 106 and 1017
        /// inverts all 77. So what decides is not the mask, it is the effect. And in Jormun a 1160 and a 1017
        /// live together with the same mask in the same cast, in adjacent frames, with opposite results.
        ///
        /// What has NOT been measured and is stated: that the value is the cap on candidates is the best
        /// explanation of why 792 and 2792 live together with the same mask and the same child spell changing
        /// only that field -- levels 80667 and 35952 --, but the ten executions of 2792 in the corpus always had
        /// a single candidate, so it is not proven.
        /// </remarks>
        private static readonly Dictionary<int, Sublanzamiento> Familia = new()
        {
            // The three the engine already resolved. They are here so that the table is complete
            // and to be able to compare them, but their code is still the old one: rewriting them
            // tonight, with the server in use, would be swapping what works for what does not yet.
            [EffectSupport.CastSpell]     = new(true,  Apunta.AlCandidato,        true,  true),
            [EffectSupport.TriggerSpell]  = new(false, Apunta.AlCandidato,        false, true),
            [NearestTargetExecuteSpell]  = new(false, Apunta.AlMasCercano,       false, true),

            // The four that were missing, and their two cousins.
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

        /// <summary>Is this effect one of those that make another spell be cast?</summary>
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

        /// <summary>Who casts the child and what it aims at, to be able to check it from outside.</summary>
        public static (bool LanzaElCandidato, string Apunta) ComoSublanza(int efecto)
            => Familia.TryGetValue(efecto, out var fila)
                ? (fila.LanzaElCandidato, fila.Apunta.ToString())
                : (false, "");

        /// <summary>
        /// 3793: a marker, not an effect. There is nothing to apply.
        /// </summary>
        /// <remarks>
        /// 430 rows in 163 spells, with no text, no characteristic, no dice and no duration. The real server
        /// records it as one more buff and announces it when its trigger fires, but it drags nobody along: the
        /// effects that go with it -- the poison, the heal, the delayed AP -- are recorded on their own, with
        /// their own trigger and their own mask, and fire by themselves. They coincide in time because they
        /// share the trigger, not because this one calls them.
        ///
        /// So treating it as a conditional gate would be making up a mechanic. It goes to the panel like any
        /// other effect that cannot be applied, which is what the generic road already does, and here it is
        /// only stated why it is right for it to stay that way.
        /// </remarks>
        internal const int MarcadorDeGuion = 3793;

        /// <summary>3792: 3793's immediate sibling. Nothing to apply either.</summary>
        /// <remarks>
        /// 165 rows in 48 spells, and they are the same animal. Measured over the 164 rows that have a spell
        /// template, without a single exception: the <c>value</c> is the identifier of an entry of THE SPELL'S
        /// OWN <c>boundScriptUsageData</c>. And the rest of the row is empty in all 165: die 0, side 0, duration
        /// 0, delay 0, and the trigger always immediate.
        ///
        /// So it carries no number to apply to anybody. It is the «a script of the spell runs here» marker, the
        /// same as 3793, and the only difference between the two is that 3793 can go with a trigger and this
        /// one cannot.
        ///
        /// It is declared here, and not simply left to fall into the generic road, because between the two
        /// they touch 39 class spells: without saying so, those 39 are counted forever as «not implemented» and
        /// somebody looks at them again every time.
        /// </remarks>
        private const int MarcadorDeGuionInmediato = 3792;

        /// <summary>Is it one of the two script markers, which do nothing?</summary>
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

        /// <summary>«Effect duration: -N» (167 spells). It cuts rounds off the buffs.</summary>
        /// <remarks>
        /// Grito Terrorífico carries die 4 and mask «A»: it takes four rounds off whatever the enemy has on him.
        /// A buff with no rounds left drops.
        /// </remarks>
        private const int AcortaLosEfectos = 1075;

        /// <summary>«Kills the target and replaces it with the summon» (59 spells).</summary>
        /// <remarks>
        /// The Sacrier's Reaping. The die carries the template of the creature that comes out in its place and
        /// the die's side its grade. It is both things at once and in that order: killing and then bringing out.
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

        /// <summary>«N damage of the best element» (20 class spells).</summary>
        /// <remarks>
        /// Llamilla, Bilbipo, Apetito de Cocobur. The die is the damage and the element is set by the caster:
        /// his highest of the four. The client's catalogue already numbers that case -- Effects.ElementId's 5
        /// is «best» --, so here it only has to be resolved by looking at the four characteristics and keeping
        /// the largest.
        /// </remarks>
        private const int DanoDelMejorElemento = 2822;

        /// <summary>
        /// «N de daños del peor elemento» (2832): Llamita, "ocasiona daños en el peor elemento
        /// del lanzador". The same question the other way round: of the four characteristics,
        /// the lowest names the element. It went to the panel as a row that nobody knew how to
        /// apply, and the spell hit for nothing.
        /// </summary>
        private const int DanoDelPeorElemento = 2832;

        /// <summary>Effects.ElementId's 5: «the best», which is not an element but a question.</summary>
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

        /// <summary>«-N% HP» (9 spells). The die is the PERCENTAGE of the maximum life.</summary>
        private const int QuitaPorcentajeDeVida = 1048;

        /// <summary>«Transfers N% of his life» (7 spells).</summary>
        /// <remarks>
        /// The caster gives and the target receives. The die is the percentage of the giver's CURRENT life,
        /// which is what keeps you from transferring what you no longer have.
        /// </remarks>
        private const int TransfiereVida = 90;

        private const int Teletransportar = 4;

        /// <summary>
        /// The four symmetric teleports: to the other side of a pivot, at the same distance.
        /// </summary>
        /// <remarks>
        /// 54 class spells among the four. What changes is the pivot, and nothing else:
        ///
        ///   1104  «symmetric with respect to the target»   pivot: the effect's target
        ///   1105  «symmetric with respect to the caster»   pivot: whoever casts
        ///   1106  «symmetric teleport»                     pivot: the aimed cell
        ///   1100  «teleports to the previous position»     not symmetric: it undoes the movement
        ///
        /// The reflection is worked out in map coordinates, not on the cell number: Dofus's grid is diagonal and
        /// adding to the index gives a place unrelated to the reflection.
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

        /// <summary>«Swaps positions». 253 spells.</summary>
        /// <remarks>
        /// Two who swap places. BOTH move, so both displacements have to be announced: with only one, the
        /// client leaves one of them drawn where he was and from then on it no longer agrees with the server
        /// on anything.
        /// </remarks>
        private const int IntercambiarPosiciones = 8;

        private const int Tirar = EffectSupport.Pull;

        /// <summary>"Moves back #1 cells" and "Moves forward #1 cells": they move WHOEVER CASTS.</summary>
        private const int Retroceder = EffectSupport.StepBack;
        private const int Avanzar = EffectSupport.StepForward;
        private const int PonerEstado = EffectSupport.AddState;
        private const int QuitarEstado = EffectSupport.RemoveState;

        /// <summary>
        /// "Casts the die's spell at the side's grade". It is the hook the items' attitudes chain what they
        /// really do with.
        /// </summary>
        public const int EfectoQueLanzaHechizo = EffectSupport.CastSpell;
        private const int LanzarHechizo = EfectoQueLanzaHechizo;

        /// <summary>
        /// Variant used by class spells. For the Ouginak, it links Molosse/Apaisement in particular to the
        /// sub-spells that add or remove Rage.
        /// </summary>
        private const int DispararHechizo = EffectSupport.TriggerSpell;
        private const int NearestTargetExecuteSpell = EffectSupport.NearestTargetExecuteSpell;

        // The Ouginak's Rage is granted by sub-spells, after the parent spell's damage: 24128
        // sets the chain up and 13745 moves the Rage from one step to the next. So the target
        // that was hit must not drop out of these two sub-spells' routing when the blow that
        // sets them off has just killed it.
        private const int OuginakRageManager = 13745;
        private const int OuginakRageRelay = 24128;
        private const int OuginakBestialFormEnd = 13747;

        private static bool IsOuginakRageChain(SpellEffect efecto)
            => (efecto.EffectId == LanzarHechizo
                || efecto.EffectId == DispararHechizo
                || efecto.EffectId == NearestTargetExecuteSpell)
               && (efecto.DiceNum == OuginakRageManager
                   || efecto.DiceNum == OuginakRageRelay);

        private static bool IsDelayedOuginakBestialFormEnd(SpellEffect efecto)
            => efecto.DiceNum == OuginakBestialFormEnd;

        private const int QuitarEfectosDeHechizo = EffectSupport.RemoveSpellEffects;
        private const int CambiarApariencia = EffectSupport.ChangeLook;

        /// <summary>"Summons: #1". The creature's template travels in the die.</summary>
        public const int Invocar = EffectSupport.Summon;

        /// <summary>
        /// The effects that place something ON A CELL instead of on somebody: a summon, a trap, a glyph. Their
        /// target is the ground, so no owner is looked for.
        ///
        ///   181, 1008, 1011  "Summons: #1"     400  "Places a trap"
        ///   401  "Places a turn-start glyph"
        ///   1091 "Places an aura glyph"        2022 "Places a rune"
        ///
        /// 1008 and 1011 are summons just like 181 and with the same shape -- the die carries the creature's
        /// template and the side its grade, checked: 3987 is «Gladiador aprendiz ocra» and 3112 «Explobomba» --.
        /// They were outside the set and that is why the 22 spells carrying them summoned nothing, silently.
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

        /// <summary>The three effects that bring a creature onto the board.</summary>
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
        /// How far an explosion reaches, read from its own spell.
        /// </summary>
        /// <remarks>
        /// It is not a two written by hand: the Explosión Tymadora's effect 99 carries
        /// <c>zoneDescr{shape: 67, param1: 2}</c>, which is a circle of radius two, and the four explosions
        /// carry their own. If Ankama ever changes it, it changes by itself.
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
        /// The five fixed heals, one per element.
        /// </summary>
        /// <remarks>
        /// 108 fire, 2998 water, 2999 air, 3000 earth, 3001 neutral. The four that were missing touch 31 class
        /// spells.
        ///
        /// Nothing new had to be written for them: the heal calculation was already written without being tied
        /// to the element -- it reads the effect's own effectElement and looks up with it the characteristic that
        /// scales it --, and whoever wrote it left the reason in a comment: «nailing 15 here is what makes the
        /// other five come out wrong the day they are implemented». The only thing tying it to fire was that the
        /// constant was a loose number instead of a set.
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

        /// <summary>Is it one of the five fixed heals?</summary>
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
        /// A fighter's BEST element: the one whose characteristic he has highest.
        /// </summary>
        /// <remarks>
        /// The client's catalogue numbers this case -- Effects.ElementId's 5 is «best» -- and there is also a
        /// whole effect for it, 2822 «N damage of the best element», which twenty class spells carry: Llamilla,
        /// Bilbipo, Apetito de Cocobur.
        ///
        /// The characteristic is looked at WITH THE BUFFS ON, not the sheet's: a spell that raises your agility
        /// can change which is your best element halfway through a fight, and that is exactly what it is cast
        /// for.
        ///
        /// A tie is broken by the order earth, fire, water, air. Which one the real game uses is not measured;
        /// ONE stable criterion is needed so that two identical casts give the same, and this is the order the
        /// catalogue itself numbers the elements in.
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

        /// <summary>"Heal: #1% of maximum HP". The die is the percentage.</summary>
        private const int CuraPorcentual = EffectSupport.HealPercent;

        /// <summary>The two characteristic numbers of the points.</summary>
        /// <summary>
        /// The effects that STEAL life: they hit and heal the caster for half.
        ///
        /// They come from the client's catalogue, as it describes them: 91 is «water steal», 92 earth, 93 air,
        /// 94 fire, 95 neutral and 82 the fixed neutral. 2828 and 2890 are «best element steal» and «worst»,
        /// which choose the element on the fly but steal all the same.
        ///
        /// Not to be confused with 96 to 100, which are the same element's damage and heal nothing. A single
        /// number of difference and the behaviour is another.
        /// </summary>
        private static readonly HashSet<int> RobosDeVida = new HashSet<int>
        {
            82, 91, 92, 93, 94, 95, 2828, 2890
        };

        public static bool EsRoboDeVida(int efecto) => RobosDeVida.Contains(efecto);

        /// <summary>«Kills the target», the client catalogue's 141.</summary>
        /// <remarks>
        /// Doom de Masas (3450) carries it, which is an administration spell: one AP, range zero, area, and an
        /// effect 120 behind it that gives back the AP spent. Until now 141 fell into the «I do not know how to
        /// apply it but I announce it» branch, which drew it on the buff panel and killed nobody.
        /// </remarks>
        internal const int MataAlObjetivo = 141;

        /// <summary>"Sin efecto adicional": the sheet's marker for a grade that does nothing more.</summary>
        internal const int SinEfectoAdicional = 666;

        private const int PuntosDeAccion = 1;
        private const int PuntosDeMovimiento = 23;

        /// <summary>
        /// "Kills the target". It is a summon's countdown: on being born one of these is hung on it with its
        /// round, and when it comes, it is undone.
        /// </summary>
        public const int MatarAlObjetivo = EffectSupport.Kill;

        /// <summary>How many chained spells are allowed before suspecting a loop.</summary>
        private const int HondoMaximo = 6;

        /// <summary>
        /// The grade an attitude's hook lives in. It is always the first: the others are named by the hook itself
        /// by their number, so there is no need to ask anybody which one is due.
        /// </summary>
        public const int GradoDelEnganche = 1;

        /// <summary>The "right now" trigger.</summary>
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
        /// When one WALKS, per cell. It is the trigger of the Cra's Centinela, which gives range and ranged damage
        /// in exchange for standing still: each step takes one range and two per cent of damage.
        ///
        /// Measured in its capture: eleven moves, eleven drops, no exception.
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
        /// Resolves a whole spell and returns what has to be done, in order.
        ///
        /// <paramref name="disparador"/> filters: on casting the "I" ones are asked for, at the start of the turn
        /// the "TB" ones, and so on. Effects with another trigger stay still until their time comes.
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

            // The effects that go by chance: they are drawn BEFORE walking anything, and the ones
            // that do not come out are left out of this resolution. A cast draws once for its
            // blows and its rows.
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

                // What is put ON THE GROUND looks for nobody: it goes to the cell, whoever is there.
                //
                // This is where the beacons fell. Effect 181 carries mask "a,A" and a point zone, and
                // the engine looked for a fighter on the aimed cell to apply it to. But a beacon is
                // summoned precisely where there is NOBODY: there was no candidate, the consequence
                // was not created and no summon was asked for. The packet and the resent list were
                // fine; what did not arrive was the order.
                // A bomb cast on a TAKEN cell is not planted: it blows up right there, and with its
                // other spell. Here and not in the summoner because the decision is the effect's:
                // depending on where it aims, the same 1008 brings out a bomb or none.
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

                // «Activates a bomb»: the aimed bomb casts ITS explosion, and the explosion carries
                // everything else inside -- its element's damage in a circle of radius two, the 141
                // that kills it and another 1009 that sets off the bombs it catches inside --.
                if (efecto.EffectId == ActivarBomba)
                {
                    if (depth >= HondoMaximo) continue;

                    // THE WALL SPREADS. Setting off a bomb sets off the ones joined to it by a wall, and
                    // theirs, and so on as far as the chain reaches: «Si una bomba esta unida a otras por
                    // un muro y explota, hara explotar tambien a las otras bombas del muro», says the class
                    // sheet. That is why it is a queue and not a loop: each bomb that blows up puts its
                    // wall companions in.
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

                        // ONCE PER CHAIN. Two bombs within each other's radius set each other off, and without
                        // this they would charge the damage once per bounce until the depth ran out.
                        if (!bombasYaEstalladas.Add(bomba.Id)) continue;

                        // It casts it itself, from its cell and at its own grade. Measured in
                        // «tymador-detonador»: bomb -5, summoned at grade 3, casts 13455 at its level 41955,
                        // which is grade 3.
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

                        // And THE ONES THE EXPLOSION CATCHES, wall or no wall: «Cuando explota una bomba, si
                        // hay otras bombas del lanzador en la zona de explosion, estas explotaran tambien».
                        // Two bombs side by side do NOT make a wall -- two cells have to be left -- but a
                        // circle of radius two takes the one next to it down all the same.
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

                // The «make another spell be cast» family, the ones the old road did not resolve. A
                // single block for the six, because they are the same resolution with three
                // parameters: who casts, what it aims at and how many candidates it takes.
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

                    // The cap. With value 0 or 999 there is no cap -- that is how 792, 1160 and 2794 go,
                    // which reach up to eleven children in the captures --; with a small number there is,
                    // and it is respected: 2160 brings out one in 251 of 251 and 2793 with value 6 never
                    // goes above six. It is the best explanation of why 792 and 2792 live together with
                    // the same mask changing only this field, but it is NOT proven: the ten executions of
                    // 2792 in the corpus had a single candidate.
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
                        // Who casts the child. In 1017, 2792 and their cousins it is the CANDIDATE, and that
                        // is not cosmetic: the child's masks are resolved against him. Measured with the
                        // Forjalanza's spear, whose child carries a «kill the target» with mask C and ends up
                        // killing the spear itself.
                        var origen = combat.TriggeringAttacker != null && combat.TriggeringAttacker.IsAlive
                            ? combat.TriggeringAttacker
                            : caster;
                        var lanzaElHijo = comoVa.LanzaElOrigen ? origen
                                        : comoVa.LanzaElCandidato ? candidato
                                        : caster;

                        // And what it aims at.
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
                                // The parent's cell, and the target is resolved again NOW: there are spells that aim
                                // at an empty cell and plant there the summon the child has to reach. Freezing the
                                // target would leave them doing nothing, silently.
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

                // "Moves back" and "Moves forward" move WHOEVER CASTS, and the mask is a condition on
                // the target, not a list of recipients: it is met or not met, and the displacement
                // happens ONCE. If it were applied per candidate, a spell that reached three
                // creatures would move the caster three times.
                bool unaSolaVez = efecto.EffectId == Retroceder || efecto.EffectId == Avanzar;

                foreach (var sobre in AQuien(combat, caster, target, efecto, aimedCell,
                                             estadosAlEmpezar, celdasAlEmpezar,
                                             soloAlObjetivo: soloAlObjetivo,
                                             includeDeadTarget: IsOuginakRageChain(efecto)))
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

                    // An effect can chain another spell: it is how the attitudes hook on.
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
                        // Delayed sub-casts wait like the rest of the family. Most cases in this
                        // old path are monster spells (Conflicto Eterno's imp), but Ouginak Rage
                        // also schedules 13747 one round later: it arms the turn-end removal only
                        // for the following turn, so beast form survives the turn in which it was
                        // gained and the whole next turn.
                        if (efecto.Delay > 0 && string.Equals(trigger, AlLanzar, StringComparison.OrdinalIgnoreCase)
                            && (!PlayerSpells.Contains(spell)
                                || IsDelayedOuginakBestialFormEnd(efecto)))
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
        /// Who an effect reaches, according to its mask.
        ///
        ///   C          whoever casts it
        ///   c          whoever casts it, if he is in the zone
        ///   a, A       those of one's own side -- the caster included -- and those opposite
        ///   g          those of one's own side without the caster
        ///   i, I  j, J the summons, of one's own side and opposite
        ///   l, L       the players, of one's own side and opposite
        ///   m, M       the monsters that are not summons, likewise
        ///   P, p       (on a summon) the caster's, somebody else's
        ///   h          the caster's summoner
        ///   O          the one whose blow set the spell off, wherever he stands
        ///   e&lt;N&gt;      only if he does NOT carry state N
        ///   E&lt;N&gt;      only if he DOES carry it
        ///   F&lt;N&gt; f&lt;N&gt;  only if he is, or is not, monster N
        ///   V&lt;N&gt; v&lt;N&gt;  only with less, or not less, than N % life
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
                                                   bool soloAlObjetivo = false,
                                                   bool includeDeadTarget = false)
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

                // LOWER CASE AND UPPER CASE ARE NOT THE SAME: "a" is one's own side and "A" the opposite
                // one. Both were in the same bucket, and that did absurd things. Tiro de Repliegue, for
                // instance, carries a 1041 "Moves back" with mask "A" and a 1042 "Moves forward" with
                // "a": not telling them apart both were met, the caster moved two cells back and two
                // forward, and the net displacement was zero. In the log it could be seen as it was,
                // there and back to the same cell.
                //
                // It holds across the whole database: damage effects 96-100 carry only "A" or "a,A" --
                // more than six thousand -- and heal 108 carries "a", "C" or "g".
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
                // The zone: the effect says in what SHAPE it takes the ground around the aimed cell --
                // a point, a circle of radius two, a cross -- and it reaches everybody standing on it
                // WHO meets the mask.
                // The telefragged are named by the telefrag, not by the ground: they are the ones
                // who swapped, wherever the swap left them. Read off Reloj de Bolsillo, whose
                // "+100 damage" on "a,T" goes, in the guide, to the character the Count swapped
                // with -- standing by then on the cell the Count left, not where the zone looked.
                var enJuego = soloTelefragueados
                    ? combate.Telefrags.Keys.Select(combate.Buscar).Where(f => f != null && f.IsAlive).ToList()
                    : soloTeleportFallido
                        ? combate.TeleportsFallidos.Select(combate.Buscar).Where(f => f != null && f.IsAlive).ToList()
                        : EnLaZona(combate, quienLanza, objetivo, efecto, celdaApuntada, celdas,
                                   includeDeadTarget);
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

            // Without a mask, at the cast's target. But if the mask says something this engine
            // cannot read yet -- "P" the players, "F434" a family of creatures -- it does NOT fall back
            // on the target: it is let through. Falling back on the target, Flecha Voraz hit TWICE.
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

        /// <summary>Who is standing on a cell, or nobody.</summary>
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
        /// The fighters the effect's zone covers.
        ///
        /// If it is not known which cell was aimed at -- attitudes and chained spells aim at none -- it falls
        /// back on the usual target, which is what was done before there were zones.
        /// </summary>
        /// <param name="celdas">
        /// Where everybody stood as the spell landed, when the caller took note: a row after a
        /// push or a pull still reaches whoever was in the zone at the cast.
        /// </param>
        private static IEnumerable<Fighter> EnLaZona(FightInstance combate, Fighter quienLanza,
                                                     Fighter objetivo, SpellEffect efecto,
                                                     int celdaApuntada,
                                                     IReadOnlyDictionary<Fighter, int> celdas = null,
                                                     bool includeDeadTarget = false)
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
                if (quien == null || quien.EstaCargado) continue;
                if (!quien.IsAlive && !(includeDeadTarget && quien == objetivo)) continue;
                if (dentro.Contains(CeldaDe(quien))) yield return quien;
            }
        }

        /// <summary>Whether somebody is in an effect's zone.</summary>
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
            // The damage is carried by whoever already carried it; it is not touched here.
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
                // It is put ONCE per cast, not once for each one the zone catches: the effect's zone is
                // the glyph's FOOTPRINT, not its list of victims.
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

                // On the MAXIMUM, not on what he has left: if it were on what he has left, ninety per
                // cent would never kill anybody however many times it was cast.
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

                // From the life the giver HAS LEFT: one cannot give away what one no longer has. And
                // never to the point of killing oneself: he keeps one.
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

                // 1020 goes on the CASTER's level and 1039 on the RECEIVER's life. They are two
                // different bases and confusing them gives shields of another order: 150% of the level
                // is 300 points at level 200, and 150% of life would be thousands.
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
                // Without a memory of where he was there is nothing to undo, and sending him anywhere
                // would be worse than doing nothing.
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

                // The ground rules. A cell that cannot be stepped on, or that already has somebody on
                // it, leaves the teleport undone: that is better than sending anybody into a hole,
                // which is what happened with pushes before the ground was looked at.
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

                // The ground is not looked at here: both cells are already being stood on by somebody,
                // so by definition they can be stood on. And whether they are taken is not looked at
                // either, because both are, and that is precisely why the swap is possible.
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
                // How many cells: the die, and if not, the value. "Hasta la casilla objetivo" is
                // as many as separate the one moved from the aimed cell, and the direction is
                // the caster's line, not the aimed cell's: the aimed cell is where it ENDS.
                int cuantas = hastaLaCasilla
                    ? Jondo.Unity.World.Maps.MapGeometry.Distance(sobre.CellId, celdaApuntada)
                    : efecto.DiceNum != 0 ? efecto.DiceNum : efecto.Value;
                if (cuantas <= 0) return null;
                if (efecto.EffectId == Tirar || efecto.EffectId == EffectSupport.PullToTargetCell
                    || efecto.EffectId == TironForzado) cuantas = -cuantas;
                int centroDelEmpuje = hastaLaCasilla ? quienLanza.CellId : celdaApuntada;

                // "Moves back" and "Moves forward" move THE CASTER, not the target. It is what Tiro de
                // Repliegue does, which gives range and takes a step back; the target only serves to
                // know what he moves away from. Measured in its capture: the Cra was on 411, cast at
                // 410 and ended up on 412, moving away from the aimed cell.
                bool alLanzador = efecto.EffectId == Retroceder || efecto.EffectId == Avanzar;
                if (alLanzador)
                {
                    sobre = quienLanza;
                    if (efecto.EffectId == Avanzar) cuantas = -cuantas;
                }

                // UNMOVABLE: he does not move, and therefore does NOT take collision damage EITHER.
                //
                // The difference matters and is easy to get wrong: it is not that the damage is reduced
                // to zero, it is that without displacement there is no crash, even with the wall right
                // at his back. A pushed one lacking room DOES pay; this one does not.
                //
                // The state's number is measured: of the 25 spells whose Spanish description names the
                // Unmovable state, 21 apply 97 and the next candidate comes up in 1. And the 155 spells
                // that set it read by themselves: Remache, Atracción Estabilizadora, Bombinmóvil,
                // Patinaje.
                //
                // Careful: STATE 97 has nothing to do with CHARACTERISTIC 97, which is the life the
                // player is missing. Same number, two different spaces.
                // And the catalogue names the 97 among 22 states with cantBeMoved and 25 with
                // cantBePushed -- Arraigado, Pesadilla, Cénit... -- which the client's own
                // SpellStateData flags; those are read from datos/spell_states.json.
                if (efecto.EffectId != EmpujeForzado && efecto.EffectId != TironForzado &&
                    (sobre.Buffs.TieneEstado(Indesplazable) || SpellStates.PinsInPlace(sobre))) return null;

                var ocupadas = new HashSet<int>();
                foreach (var otro in Todos(combate))
                    if (otro != null && otro.IsAlive && !otro.EstaCargado && otro != sobre) ocupadas.Add(otro.CellId);

                int desde = sobre.CellId;
                // The cells that can be stepped on in the arena. They went as null, which means "do not
                // look at the ground", and that is why the piwis ended up in a hole or off the map: the
                // only border left was the edge of the 560-cell grid, which is much larger than a map's
                // floor.
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

                // COLLISION DAMAGE, which was not done at all.
                //
                // It comes from the cells NOT covered, and the formula is measured on the 127 push
                // damage messages of the 401 captures:
                //
                //   damage = cellsNotCovered × (level/2 + the pusher's 84
                //                               − the receiver's 85 + 32) / 4
                //
                // The three anchors: a level 200 caster with no bonuses hits 33 per cell -- 132/4 -- and
                // only 33, 66, 99 and 132 come out, not one value in between; the Zurkarak «Daddy», who
                // is LEVEL 165, hits 57 for two cells, which is floor(2 × 114.5 / 4) and which no fixed
                // constant can give; and a Zobal with 100 push from equipment and masks of 0, 40, 80
                // and 120 hits 58, 68, 78 and 88 per cell.
                //
                // The resistance goes INSIDE the quarter: in the koliseo, 561 push against 30
                // resistance give 331 for two cells. Subtracting it outside would give 316.
                //
                // And only the push does it: the catalogue has a separate effect, «Pushes (no damage)»,
                // which 54 spells use precisely so as not to do it, which is the proof that the normal
                // 5 does. Of the PULL there is not a single blocked case in the 401 captures, so it
                // stays at zero until it is measured.
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

                    // And if what stopped him was another fighter, that one pays half. Walls do not pay.
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

                // It leaves with nothing ONLY if there is no damage either: when the pushed one has not
                // a single free cell he does not move, but he takes the whole blow. Measured: in that
                // case the real server does not send the displacement, only the damage.
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
                // "Summons: #1", with the creature's template in the die. The engine does not bring it
                // onto the board: an identifier has to be handed out, the turn order rebuilt and the
                // client told, and that belongs to whoever drives the fight.
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
                // "Casts the die's spell at the side's grade". It is the attitudes' hook: Amarillo
                // Ocre's grade 1 does nothing by itself, it only says when to cast its grades 2 and 3.
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

                // THE COMBO IS NOT JUST ANY STATE, and treating it as one broke three things at once.
                // It is a ladder of mutually exclusive rungs that only a bomb can carry, so here its
                // three rules are imposed before touching anything:
                //
                //   1. ONLY ON A BOMB. Polvora and Mosquete chain the combo spell with masks this
                //      engine cannot narrow -- "P", "h" --, and not knowing whom to aim at it fell
                //      on the caster: the Rogue ended up with Combo IV on his own panel and the bomb
                //      did not go up.
                //   2. ONE RUNG AND NOT TWO. The spell's ladder sets the first one again on every
                //      pass -- its mask excludes 2485 onwards but not 2484 -- and we announced it to
                //      the client before removing it, so the bomb always looked like Combo I however
                //      much the server raised it.
                //   3. NOTHING ABOVE FIFTEEN. "El combo aumenta de 1 a 15 maximo", says the class
                //      sheet. The spell's ladder has eighteen rungs and the bombs reached 18; the
                //      three at the top pay the same as fifteen, so going higher gave nothing and the
                //      client cannot draw them.
                if (Combo.EsPeldano(estado))
                {
                    if (efecto.EffectId == PonerEstado)
                    {
                        if (!Bombs.Is(sobre.MonsterId)) return null;

                        int ahora = Combo.LevelOf(sobre);
                        int sube = Combo.NivelDelPeldano(estado);

                        // NOT DOWN, BUT REPEATING IS FINE. The real server sets the rung it is already on
                        // again before raising it -- measured in «tymador-explobomba resiliente», where frames
                        // 248 and 249 send 2484 and 2485 in a row, with nothing in between -- and refusing it,
                        // our flow stopped looking like theirs exactly where the client draws the Roman
                        // numeral. Really refusing is needed in two cases and only two: going down a rung, and
                        // going past fifteen.
                        if (sube < ahora) return null;
                        if (sube > ahora && ahora >= Combo.Tope) return null;

                        // AND THE OLD ONES ARE ANNOUNCED. Removing them silently is what left the bomb at Combo
                        // I forever: the server raised it -- it can be seen in the log, «-5 is at level 13,
                        // +280%» -- but the client still had rung 1 on because nobody had told it to remove it,
                        // and that is the one it drew.
                        foreach (int viejo in Combo.Ladder())
                        {
                            if (viejo == estado) continue;
                            barridos.AddRange(sobre.Buffs.QuitarEstadoConEmbrujos(viejo));
                        }
                    }
                    // IF IT HAS NOT GONE UP, NOTHING IS SWEPT EITHER. Each rung of the ladder carries a 951
                    // behind it that removes the previous one, and with the cap in place this happened: on
                    // reaching fifteen the 950 of sixteen was refused but its 951 still removed fifteen, the
                    // bomb was left without combo and started again from the bottom. It showed plainly in
                    // the test: twenty-five casts and the bomb at level 9, which is fifteen up, zero, and
                    // nine again.
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

            // The ones that tune a specific spell: basic damage and range, and the rest of the
            // catalogue's category 3 (SpellModifiers). The spell goes in the die and what is added,
            // in the value. A pin is a value even at zero: Bestialidad's "2905 on 13791, value 0"
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

                // The characteristic of the effect's ELEMENT. It is no longer always intelligence: with
                // the five heals active, water scales with chance, air with agility and earth with
                // strength. That is why this was a lookup and not a nailed-in 15.
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

            // Heals as a percentage of maximum life. The die is the PERCENTAGE, not the points: the
            // Survival Beacon heals seven per cent of the receiver's maximum, and on the wire that
            // already travels resolved into points.
            if (efecto.EffectId == CuraPorcentual)
            {
                if (sobre == null || !sobre.IsAlive) return null;
                int cuanto = efecto.DiceNum != 0 ? efecto.DiceNum : efecto.Value;
                if (cuanto <= 0) return null;

                int puntos = Math.Max(1, sobre.MaxHP * cuanto / 100);
                return ApplyHealing(combate, quienLanza, sobre, hechizo, grado, efecto,
                                    ronda, puntos);
            }

            // The ones that MULTIPLY: "damage taken x110%", "heals received x50%". They touch no
            // characteristic, so they are stored with their percentage in the buff and whoever
            // works out the blow looks them up by their effect number.
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

            // And everything else: whatever touches a characteristic, with the sign the catalogue says.
            //
            // The die is ROLLED. In the catalogue, diceNum is the minimum and diceSide the maximum --
            // there are effects of «one or two AP» (1 and 2) and of «two or three» (2 and 3) --, and
            // here the minimum was always taken, so a spell that can remove up to three always
            // removed two. When diceSide is zero, the quantity is fixed and there is nothing to roll.
            var (caracteristica, signo) = DatabaseManager.EffectMeta(efecto.EffectId);
            int cantidad = caracteristica != 0 && signo != 0
                ? DelDado(efecto.DiceNum, efecto.DiceSide, efecto.Value) * signo
                : 0;

            // And never more than he has left. It is the same the STEAL points branch a few lines
            // above already did, and it was missing here: that is why a spell could leave a
            // creature without its six movement points at once. Also, what is announced has to be
            // what was really removed, not what was meant to be removed.
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

            // And if it touches NO characteristic, it is not thrown away either.
            //
            // This is where the empty panel was. This method ended in an "if there is no
            // characteristic, nothing", and with that FOURTEEN whole families disappeared that the
            // real server does announce: the beacons' 1160 and 1163, the chaining 792, the killing
            // 141, 406 that removes a spell's effects, 3793, the heals' 1159, line of sight's 289…
            // all the ones that have Characteristic 0 in the catalogue, which are precisely the class
            // ones. Counted on the Cra captures: of the thirty-two effects the server sends to the
            // panel, fourteen were lost through this line.
            //
            // Now the one that cannot be applied is noted and SENT all the same, with what it carries.
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
        /// The list of effects that applies, depending on whether it was critical or not.
        ///
        /// A spell carries TWO lists in the database and the critical one is not "the normal one multiplied": it
        /// is a whole other set with its own numbers. Flecha Helada hits 21 to 24 and on a critical 25 to 29;
        /// Tiros Potentes gives 250 power and on a critical 300. If the critical list comes empty -- there are
        /// spells that do not have one -- the usual one is used.
        /// </summary>
        public static IReadOnlyList<SpellEffect> EfectosDeLaTirada(int hechizo, int grado, bool critico)
        {
            if (!critico) return SpellEffects.De(hechizo, grado);
            var criticos = SpellEffects.Criticos(hechizo, grado);
            return criticos.Count > 0 ? criticos : SpellEffects.De(hechizo, grado);
        }

        /// <summary>
        /// The draw of the effects that go by chance, and it returns THE ONES THAT DO NOT COME OUT.
        ///
        /// An effect can carry a probability in its <c>random</c> field, and those that carry one are grouped by
        /// their <c>group</c> field. There are two ways, and they are told apart by what they add up to:
        ///
        ///   add up to 100  -> it is a draw: ONE comes out, with each one's weight. It is what
        ///                     Invocación de Arakna does, which carries two 181 effects -- template 246
        ///                     at eighty per cent and 2630, the big Arakna, at twenty --. Without this
        ///                     both came out at once, which is what was happening.
        ///   do not add up to 100 -> each goes on its own and happens with its own probability.
        ///
        /// Counted over the whole database: there are 1,335 spell levels with effects of this kind, and in
        /// 1,129 of their groups the sum is exactly a hundred.
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
        /// What comes out of an effect's die.
        ///
        /// In the client's catalogue, <c>diceNum</c> is the minimum and <c>diceSide</c> the maximum: there are
        /// effects of «one or two action points» (1 and 2) and of «two or three» (2 and 3). With diceSide at
        /// zero the quantity is fixed, and with both at zero the loose value is used.
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
        /// Whether what this effect leaves piles up instead of replacing whatever was there.
        ///
        /// What piles up is what fires EVERY TIME SOMETHING HAPPENS, that is what does not have the "on cast"
        /// trigger: the Sentinel takes one range per step, and three steps are three less. What is "on cast"
        /// is refreshed, which is what Flecha Helada does when repeated.
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
        /// The round in which an effect drops. A negative duration means "for as long as the fight lasts";
        /// zero, that it is instant and leaves no lasting buff.
        /// </summary>
        private static int Caduca(SpellEffect efecto, int ronda)
            => efecto.Duration < 0 ? -1 : ronda + efecto.Delay + Math.Max(1, efecto.Duration);

        /// <summary>
        /// The round in which an effect starts to count: the cast's plus its delay.
        ///
        /// Checked against the Flecha Castigadora capture, cast in round 4: the buff with delay 1 lives round 5
        /// and drops on entering 6, and the one with delay 2 lives 6.
        /// </summary>
        private static int Empieza(SpellEffect efecto, int ronda) => ronda + efecto.Delay;
    }
}

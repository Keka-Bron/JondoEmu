using System;
using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.World.Fights
{
    /// <summary>
    /// What a buff that does not touch a single characteristic affects.
    ///
    /// The three come from the client's effects catalogue and share a shape: the affected spell
    /// travels in the <c>diceNum</c> and what is added to it, in the <c>value</c>.
    ///
    ///   293  "#1: +#3 de daños básicos"     280  "+#3 de alcance mínimo"
    ///   281  "#1: +#3 de alcance máximo"
    /// </summary>
    public enum SpellAspect
    {
        Nada = 0,
        DanoBase = 293,
        AlcanceMinimo = 280,
        AlcanceMaximo = 281,

        // The rest of the catalogue's category 3, "a modifier of one spell", all of the same
        // shape: the spell in the dice, the amount -- or 1, for a switch -- in the value.

        /// <summary>282, "#1: alcance modificable".</summary>
        RangeModifiable = 282,

        /// <summary>285, "#1: -#3 PA": the spell costs that much less.</summary>
        ApCostDown = 285,

        /// <summary>286, "#1: -#3 de reactivación": that much off the spell's cast interval.</summary>
        CastIntervalDown = 286,

        /// <summary>287, "#1: +#3% de crítico".</summary>
        CriticalUp = 287,

        /// <summary>289, "#1: línea de visión desactivada".</summary>
        LineOfSightOff = 289,

        /// <summary>290, "#1: +#3 lanzamiento(s) por turno".</summary>
        CastsPerTurnUp = 290,

        /// <summary>291, "#1: +#3 lanzamiento(s) por objetivo".</summary>
        CastsPerTargetUp = 291,

        /// <summary>294, "#1: -#3 de alcance máximo".</summary>
        MaxRangeDown = 294,

        /// <summary>295, "#1: -#3 de alcance mínimo".</summary>
        MinRangeDown = 295,

        /// <summary>296, "#1: +#3 PA": the spell costs that much more.</summary>
        ApCostUp = 296,

        /// <summary>297, "#1: casilla ocupada necesaria desactivada".</summary>
        OccupiedCellOff = 297,

        /// <summary>299, "#1: casilla libre necesaria activada".</summary>
        FreeCellOn = 299,

        /// <summary>314, "#1: casilla ocupada necesaria activada".</summary>
        OccupiedCellOn = 314,

        /// <summary>798, "#1: objetivo visible necesario activado".</summary>
        VisibleTargetOn = 798,

        /// <summary>2905, "#1: alcance máximo fijado en #3": the maximum IS that, whatever else.</summary>
        MaxRangeSet = 2905,

        /// <summary>2906, "#1: alcance mínimo fijado en #3".</summary>
        MinRangeSet = 2906,

        /// <summary>2935, "#1: +#3 de curas básicas": the heal's twin of 293.</summary>
        BaseHeal = 2935,
    }

    /// <summary>
    /// A buff: what a spell leaves placed on someone, and until when.
    ///
    /// There is no hand-written spell list behind this. Each buff comes from an
    /// entry of the spell's <c>EffectsJson</c>, and what the effect number means is said by
    /// the client's <c>Effects</c> table. Here only what is already resolved is kept.
    /// </summary>
    public sealed class Buff
    {
        /// <summary>The consecutive number it travels to the client with, starting at one.</summary>
        public int Numero { get; set; }

        public int EffectId { get; set; }
        public int EffectUid { get; set; }

        /// <summary>The characteristic it touches, or zero if its thing goes another way.</summary>
        public int Caracteristica { get; set; }

        /// <summary>How much, already with its sign.</summary>
        public int Cuanto { get; set; }

        /// <summary>If it affects a specific spell, which one and in what way.</summary>
        public SpellAspect Sobre { get; set; }
        public int HechizoAfectado { get; set; }

        /// <summary>The state it sets or removes, if it is one of those that do that.</summary>
        public int Estado { get; set; }

        /// <summary>The temporary appearance imposed by effect 335, or zero.</summary>
        public int Apariencia { get; set; }

        public int HechizoOrigen { get; set; }
        public int NivelOrigen { get; set; }
        public long Quien { get; set; }
        public string Disparador { get; set; } = "I";

        /// <summary>The round in which it drops. Minus one is "until the fight ends".</summary>
        public int CaducaEnRonda { get; set; }

        /// <summary>
        /// The round in which it STARTS to count. For almost all it is the cast's; for the
        /// delayed ones, that many rounds later as the effect's delay says.
        ///
        /// Without this there was no way of expressing «this starts in two turns», which is what
        /// the Flecha Castigadora does, and the buff was applied whole on the spot.
        /// </summary>
        public int EmpiezaEnRonda { get; set; }

        /// <summary>
        /// Whether it is added to an identical one already there instead of replacing it. Carried by the ones a spell
        /// keeps putting every time something happens —each step, each hit taken—, which pile up.
        /// </summary>
        public bool Apila { get; set; }

        /// <summary>
        /// How many equivalent rows may coexist on the bearer: the spell level's
        /// <c>maxStack</c>. Minus one is no limit, zero and one are "the new one replaces the
        /// old", and a larger number is the cap, the oldest giving way.
        /// </summary>
        /// <remarks>
        /// Measured on the Yopuka captures: Fervor (-1) cast twice in a turn keeps both
        /// shields, rows 10 and 11; Pugilato (-1) its two "+18 de daños básicos"; Tumulto (-1)
        /// puts one "+20" per enemy hit, rows 103 and 104 in one cast. Espada del Juicio (1)
        /// cast again drops its shield -- jya 9, jwe 514 -- and puts row 13; Sentencia (1) the
        /// same with its state. Presión and Espada Destructora are 2, which is what "erosiona"
        /// twice on one target adds up to.
        /// </remarks>
        public int MaxStacks { get; set; }

        /// <summary>
        /// An instantaneous heal waiting for <see cref="EmpiezaEnRonda"/>. The amount is resolved
        /// when the spell is cast, like every other delayed buff, but missing-life capping is done
        /// only when the heal becomes due.
        /// </summary>
        public int PendingHealPoints { get; set; }

        /// <summary>
        /// A row that is only WAITING: the effect has a delay and nothing of it applies until
        /// <see cref="EmpiezaEnRonda"/>. The real server registers it with trigger "Y", hidden
        /// from the panel, and drops it when the effect goes off: the kill of a beacon two
        /// rounds after its birth, the script marker and the +1 MP of Paso de Cacería on the
        /// next turn. What it will do is in <see cref="EffectId"/>, <see cref="Caracteristica"/>
        /// and <see cref="Cuanto"/>, and how long it will then last in <see cref="Duracion"/>.
        /// </summary>
        public bool Pendiente { get; set; }

        /// <summary>
        /// The catalogue numbers of the row, kept for the frames: the dice, the value, and how
        /// dispellable it is. A pending row announces them twice, once waiting and once live.
        /// </summary>
        public int Dado { get; set; }
        public int Cara { get; set; }
        public int Valor { get; set; }
        public int Dispellable { get; set; }

        /// <summary>How many rounds a pending row lasts once it goes off.</summary>
        public int Duracion { get; set; }

        /// <summary>
        /// Whether the row is a critical list's, for the f9 of its frames: a waiting row keeps
        /// it and hands it to the live row it turns into (Espada del Destino's "+40" waits
        /// flagged and goes off flagged, row 29 off row 28 in its capture).
        /// </summary>
        public bool Critico { get; set; }

        /// <summary>
        /// The pending row this one came out of, for the client to link the two: the activated
        /// +1 MP of Paso de Cacería carries the number of its "Y" row. Zero when it has none.
        /// </summary>
        public int Padre { get; set; }

        /// <summary>
        /// Whether the row counts right now: started, and still on the bearer. Expiry is the
        /// sweep's business, not this one's -- a row whose round has come stays in force until
        /// the sweep takes it at its caster's turn, which is when the real server drops it.
        /// </summary>
        public bool Vivo(int ronda) => ronda >= EmpiezaEnRonda && !Pendiente;
    }

    /// <summary>
    /// The buffs and states one carries, and the attitudes one's items give.
    ///
    /// It goes apart from the <see cref="Fighter"/> so that one can see at a glance what is on and
    /// who put it there, which is exactly what the client draws in the buffs panel.
    /// </summary>
    public sealed class Buffs
    {
        private readonly List<Buff> _puestos = new List<Buff>();
        private readonly HashSet<int> _estados = new HashSet<int>();

        /// <summary>
        /// The spells the items give away: the attitudes of the dofus and the trophies, which
        /// come from each item's effect 1175. They are registered on starting the fight and are the ones
        /// that fire at the start and at the end of each turn.
        /// </summary>
        public List<int> Actitudes { get; } = new List<int>();

        /// <summary>
        /// The grade an attitude is held at, for the few that have one: the initial spells of a
        /// character's own choices are cast at his grade of the choice -- 25200 "Explobomba" at
        /// grade 3 for the Tymador who has Explobomba at 3 -- while an item's attitude and a
        /// class passive are always their grade one.
        /// </summary>
        private readonly Dictionary<int, int> _gradosDeActitud = new Dictionary<int, int>();

        public void PonerActitud(int hechizo, int grado = 1)
        {
            if (!Actitudes.Contains(hechizo)) Actitudes.Add(hechizo);
            if (grado > 1) _gradosDeActitud[hechizo] = grado;
        }

        public int GradoDeActitud(int hechizo)
            => _gradosDeActitud.TryGetValue(hechizo, out int grado) ? grado : 1;

        /// <summary>
        /// The spells one has on that STILL HAVE SOMETHING TO DO later.
        ///
        /// A spell does not end on being cast: its effects with a trigger other than "I" stay
        /// waiting for their thing to happen. The Cra's Centinela is the clear case: on casting it, it gives
        /// ten range and twenty per cent ranged damage, and then, FOR EACH STEP
        /// walked, it eats one range and two per cent. That "for each step" is the
        /// CCMPARR trigger, and to be able to fire it one has to remember the spell is still
        /// on.
        ///
        /// It is the same as the items' attitudes, but with an expiry date.
        /// </summary>
        public List<ActiveSpell> ActiveSpells { get; } = new List<ActiveSpell>();

        /// <summary>A spell still on and at what grade, until it drops.</summary>
        public sealed class ActiveSpell
        {
            public int Hechizo { get; set; }
            public int Grado { get; set; }
            public int CaducaEnRonda { get; set; }

            /// <summary>
            /// Who cast it, and therefore who its later effects come from: Polvo's "explode if
            /// destroyed" is the Tymador's doing on his bomb, not the bomb's on itself. Zero
            /// means the bearer.
            /// </summary>
            public long Lanzador { get; set; }

            /// <summary>
            /// The round the hook was put in. A hooked row with a delay does not go off before
            /// this round plus its delay: Furor's decay is a "1160 under TE" with a delay of
            /// one, and it fires at the end of a turn of the round AFTER the cast.
            /// </summary>
            public int PuestoEnRonda { get; set; }

            /// <summary>
            /// Whether the cast that put it was critical: the hook fires with the critical
            /// lists. Pugilato's turn-end 406 goes out flagged, from the critical list's own
            /// entry (uid 371557), when the cast was.
            /// </summary>
            public bool Critico { get; set; }

            public bool Vivo(int ronda) => CaducaEnRonda < 0 || ronda < CaducaEnRonda;

            /// <summary>
            /// The rows of the spell this hook holds for its bearer, by effect uid -- a monster
            /// spell's triggered rows are armed one by one on the fighters their mask and zone
            /// named, and only those go off, on him. Null: every row of the spell, re-aimed each
            /// time, the class spells' way.
            /// </summary>
            public HashSet<int> Filas { get; set; }
        }

        /// <summary>
        /// Arms rows of a spell on this fighter, in their caster's name: added to the hook that
        /// caster already has here with that spell, or a new one. Lasts the longest of the two.
        /// </summary>
        public void ArmarFilas(int hechizo, int grado, int caducaEnRonda, long lanzador, int puestoEnRonda,
                               IEnumerable<int> filas, bool critico = false)
        {
            var ya = ActiveSpells.FirstOrDefault(e => e.Filas != null && e.Hechizo == hechizo && e.Lanzador == lanzador);
            if (ya != null)
            {
                foreach (int fila in filas) ya.Filas.Add(fila);
                ya.Grado = grado;
                if (ya.CaducaEnRonda >= 0) ya.CaducaEnRonda = caducaEnRonda < 0 ? -1 : Math.Max(ya.CaducaEnRonda, caducaEnRonda);
                ya.PuestoEnRonda = puestoEnRonda;
                return;
            }
            ActiveSpells.Add(new ActiveSpell
            {
                Hechizo = hechizo, Grado = grado, CaducaEnRonda = caducaEnRonda, Lanzador = lanzador,
                PuestoEnRonda = puestoEnRonda, Critico = critico, Filas = new HashSet<int>(filas),
            });
        }

        /// <summary>
        /// Everything a fighter put on this one goes: his rows, the states only they held, the
        /// rows he armed here. For a monster's death -- a Pépite's mark on Crunchidor, an Éclat's
        /// invulnerability on its escort did not outlive them, and here they did.
        /// </summary>
        public List<Buff> QuitarLoDe(long quien)
        {
            var quitados = _puestos.FindAll(e => e.Quien == quien && !e.Pendiente);
            _puestos.RemoveAll(e => e.Quien == quien && !e.Pendiente);
            ActiveSpells.RemoveAll(e => e.Lanzador == quien);
            foreach (var quitado in quitados)
            {
                if (quitado.Estado == 0 || quitado.EffectId == Jondo.Unity.World.Combat.EffectSupport.DisableState) continue;
                if (!SigueHabiendo(quitado.Estado)) _estados.Remove(quitado.Estado);
            }
            return quitados;
        }

        /// <summary>Records that this spell is still on, or extends the one already there.</summary>
        public void Enganchar(int hechizo, int grado, int caducaEnRonda, long lanzador = 0, int puestoEnRonda = 0,
                              bool critico = false)
        {
            var ya = _enganchesPorHechizo(hechizo);
            if (ya != null)
            {
                ya.Grado = grado;
                ya.CaducaEnRonda = caducaEnRonda;
                ya.Lanzador = lanzador;
                ya.PuestoEnRonda = puestoEnRonda;
                ya.Critico = critico;
                return;
            }
            ActiveSpells.Add(new ActiveSpell
            {
                Hechizo = hechizo, Grado = grado, CaducaEnRonda = caducaEnRonda, Lanzador = lanzador,
                PuestoEnRonda = puestoEnRonda, Critico = critico,
            });
        }

        private ActiveSpell _enganchesPorHechizo(int hechizo)
            => ActiveSpells.FirstOrDefault(e => e.Hechizo == hechizo && e.Filas == null);

        /// <summary>Removes the hooks already fulfilled.</summary>
        public void BarrerEnganches(int ronda) => ActiveSpells.RemoveAll(e => !e.Vivo(ronda));

        /// <summary>A spell's hooks go, and nothing else of it: for a hook that lives for one cast.</summary>
        public int Desenganchar(int hechizo) => ActiveSpells.RemoveAll(e => e.Hechizo == hechizo);

        public IReadOnlyList<Buff> Puestos => _puestos;
        /// <summary>
        /// The states that count: the ones put, less the ones a live 952 has switched off. The
        /// set itself when nothing is switched off, which is nearly always.
        /// </summary>
        public IReadOnlyCollection<int> Estados
        {
            get
            {
                if (!_puestos.Exists(e => e.EffectId == Jondo.Unity.World.Combat.EffectSupport.DisableState)) return _estados;
                var activos = new HashSet<int>(_estados);
                activos.RemoveWhere(Desactivado);
                return activos;
            }
        }

        public bool TieneEstado(int estado) => _estados.Contains(estado) && !Desactivado(estado);

        /// <summary>Whether a 952 row holds this state switched off right now.</summary>
        public bool Desactivado(int estado)
            => _puestos.Exists(e => e.EffectId == Jondo.Unity.World.Combat.EffectSupport.DisableState
                                 && e.Estado == estado && !e.Pendiente);

        public void PonerEstado(int estado) { if (estado != 0) _estados.Add(estado); }
        public void QuitarEstado(int estado) => _estados.Remove(estado);

        /// <summary>
        /// Removes a state and the buffs that represented it in the panel. Returning them lets
        /// the network layer send a <c>jya</c> for each one.
        /// </summary>
        /// <summary>
        /// Trims rounds off all the buffs and returns how many have dropped entirely.
        /// </summary>
        /// <remarks>
        /// It is effect 1075, «Duración de los efectos: -N», which 167 spells carry —the Ouginak's
        /// Grito Terrorífico with four rounds, for example—.
        ///
        /// Those that do NOT expire by round stay as they are: a permanent buff is not shortened,
        /// because it has nothing to shorten, and subtracting rounds from a zero would leave it expired in the
        /// past round and it would drop entirely.
        /// </remarks>
        public int Acortar(int rondas, int ahora)
        {
            if (rondas <= 0) return 0;

            var caidos = new List<Buff>();
            foreach (var embrujo in _puestos)
            {
                if (embrujo.CaducaEnRonda <= 0) continue;

                embrujo.CaducaEnRonda -= rondas;
                if (embrujo.CaducaEnRonda <= ahora) caidos.Add(embrujo);
            }

            foreach (var muerto in caidos) _puestos.Remove(muerto);
            return caidos.Count;
        }

        public List<Buff> QuitarEstadoConEmbrujos(int estado)
        {
            _estados.Remove(estado);
            var quitados = _puestos.FindAll(e => e.Estado == estado);
            _puestos.RemoveAll(e => e.Estado == estado);
            return quitados;
        }

        /// <summary>
        /// Takes off every row that can be dispelled -- a dispellable of one, the catalogue's
        /// "dispellable" -- and the states only those rows held. For effect 132.
        /// </summary>
        public List<Buff> QuitarLosDesembrujables()
        {
            var quitados = _puestos.FindAll(e => e.Dispellable == 1 && !e.Pendiente);
            _puestos.RemoveAll(e => e.Dispellable == 1 && !e.Pendiente);
            foreach (var quitado in quitados)
            {
                if (quitado.Estado == 0 || quitado.EffectId == Jondo.Unity.World.Combat.EffectSupport.DisableState) continue;
                if (!SigueHabiendo(quitado.Estado)) _estados.Remove(quitado.Estado);
            }
            return quitados;
        }

        /// <summary>Takes one row off, and the state only it held. False when it was not here.</summary>
        public bool QuitarFila(Buff fila)
        {
            if (!_puestos.Remove(fila)) return false;
            if (fila.Estado != 0 && fila.EffectId != Jondo.Unity.World.Combat.EffectSupport.DisableState
                && !SigueHabiendo(fila.Estado)) _estados.Remove(fila.Estado);
            return true;
        }

        /// <summary>Removes everything a spell left, including its states and its hooks.</summary>
        /// <remarks>
        /// The hooks go with the rows: Furor's 406 on 28604 takes the hooked "1160 under TE"
        /// away with the state and the +N -- jya 36, 37 AND 38 in the capture -- and a hook
        /// left behind would fire the decay on the rows the recast has just put.
        /// </remarks>
        public List<Buff> QuitarDelHechizo(int hechizo)
        {
            var quitados = _puestos.FindAll(e => e.HechizoOrigen == hechizo);
            _puestos.RemoveAll(e => e.HechizoOrigen == hechizo);
            ActiveSpells.RemoveAll(e => e.Hechizo == hechizo);

            foreach (var quitado in quitados)
            {
                if (quitado.Estado == 0 || quitado.EffectId == Jondo.Unity.World.Combat.EffectSupport.DisableState) continue;
                if (!SigueHabiendo(quitado.Estado))
                    _estados.Remove(quitado.Estado);
            }
            return quitados;
        }

        /// <summary>
        /// The rows of one grade of a spell, its states and its hooks at that grade: effect 1406.
        /// Aguja takes its own poison's grade 6 off so, and leaves every other grade alone.
        /// </summary>
        public List<Buff> QuitarDelHechizo(int hechizo, int grado)
        {
            bool delGrado(Buff e) => e.HechizoOrigen == hechizo && e.NivelOrigen == grado;
            var quitados = _puestos.FindAll(delGrado);
            _puestos.RemoveAll(delGrado);
            ActiveSpells.RemoveAll(e => e.Hechizo == hechizo && e.Grado == grado);

            foreach (var quitado in quitados)
            {
                if (quitado.Estado == 0 || quitado.EffectId == Jondo.Unity.World.Combat.EffectSupport.DisableState) continue;
                if (!SigueHabiendo(quitado.Estado)) _estados.Remove(quitado.Estado);
            }
            return quitados;
        }

        /// <summary>
        /// The rows the last <see cref="Poner"/> took off to make room for the new one: the
        /// refreshed row of a spell that does not stack, the oldest of one that stacks up to a
        /// cap. The real server announces each as gone -- jya and jwe 514 -- before the new
        /// row; the caller reads them right after the put.
        /// </summary>
        public List<Buff> Relevados { get; } = new List<Buff>();

        /// <summary>
        /// Adds a buff with the number it gets. The number is NOT each fighter's: it is
        /// consecutive for the whole fight, and the one used later to remove it with the jya. In the
        /// capture the player's go from 19 to 25 and the monster's follow from 26 to 32, same
        /// series.
        /// </summary>
        /// <remarks>
        /// How many equivalent rows may live together is the spell level's <c>maxStack</c>, in
        /// <see cref="Buff.MaxStacks"/>: the new row replaces the old at zero and one, joins it
        /// without limit at minus one, and pushes the oldest out at a cap. Two rows are
        /// equivalent when they are the same catalogue entry of the same spell by the same
        /// caster -- the entry, not just the effect: Flecha Castigadora carries two 293 that
        /// only differ by their effectUid, and both stay. A replaced row is never refreshed in
        /// place: the real server never reuses a number (in "tymador-explobomba resiliente"
        /// the same bomb carries state 2484 as buff 19 and again as buff 23), it takes the old
        /// one off and puts a new one, and the ones taken off are left in
        /// <see cref="Relevados"/> for the caller to announce.
        ///
        /// <see cref="Buff.Apila"/> is the old "stack without limit" of the rows a spell puts
        /// each time something happens -- a combo rung, a hooked "-N de daños recibidos", a
        /// per-step malus -- and stays so unless the level writes a cap above one.
        /// </remarks>
        public Buff Poner(Buff embrujo, Func<int> siguienteNumero)
        {
            Relevados.Clear();

            int tope = embrujo.MaxStacks;
            if (embrujo.Apila && tope <= 1) tope = -1;

            if (tope >= 0)
            {
                int cabe = Math.Max(1, tope);
                var equivalentes = _puestos.FindAll(e => e.EffectId == embrujo.EffectId
                                                      && e.EffectUid == embrujo.EffectUid
                                                      && e.HechizoOrigen == embrujo.HechizoOrigen
                                                      && e.HechizoAfectado == embrujo.HechizoAfectado
                                                      && e.Quien == embrujo.Quien
                                                      && e.Pendiente == embrujo.Pendiente);
                while (equivalentes.Count >= cabe)
                {
                    var relevado = equivalentes[0];
                    equivalentes.RemoveAt(0);
                    _puestos.Remove(relevado);
                    Relevados.Add(relevado);
                }
            }

            embrujo.Numero = siguienteNumero();
            _puestos.Add(embrujo);
            return embrujo;
        }

        /// <summary>What the buffs add to a characteristic.</summary>
        public int De(int caracteristica, int ronda)
        {
            int total = 0;
            foreach (var e in _puestos)
            {
                if (e.Caracteristica == caracteristica && e.Vivo(ronda)) total += e.Cuanto;
            }
            return total;
        }

        /// <summary>
        /// What the buffs of an effect number MULTIPLY, as a percentage.
        ///
        /// There is a family that does not add but multiplies: 1163 is "daños sufridos x#1%" and
        /// 1159 "curas recibidas x#1%". They have no characteristic in the catalogue —the client
        /// resolves them by their number—, so the same paths as the rest do not work.
        ///
        /// Returns a hundred when there are none, that is "times one". Several chain: two of
        /// a hundred and ten give a hundred and twenty-one.
        /// </summary>
        /// <summary>
        /// The flat "-N de daños recibidos" (105, 265) the bearer holds against a blow of the
        /// given kinds: the rows put with no trigger at all, and the rows whose trigger names
        /// one of the kinds. Those letters are not triggers that fire but CONDITIONS on the
        /// blow -- "DR" ranged, "DM"/"DCAC" melee, "D" any, "DTB"/"DTE" the poisons of a turn
        /// -- and Remisión's on a bomb is "DR": a bomb shot from afar takes 20 less at grade 3,
        /// one hit from next door takes it all.
        /// </summary>
        public int ReduccionDeDanoRecibido(int ronda, IReadOnlyCollection<string> clasesDelGolpe)
        {
            int total = 0;
            foreach (var e in _puestos)
            {
                if (e.EffectId != DanoRecibidoMenos && e.EffectId != DanoRecibidoMenosFijo) continue;
                if (!e.Vivo(ronda) || e.Cuanto <= 0) continue;
                bool aplica = string.IsNullOrEmpty(e.Disparador) || e.Disparador == "I";
                if (!aplica)
                {
                    foreach (var d in e.Disparador.Split('|'))
                    {
                        if (clasesDelGolpe.Contains(d.Trim())) { aplica = true; break; }
                    }
                }
                if (aplica) total += e.Cuanto;
            }
            return total;
        }

        /// <summary>The two "-N de daños recibidos" of the catalogue.</summary>
        public const int DanoRecibidoMenos = 265;
        public const int DanoRecibidoMenosFijo = 105;

        /// <summary>"Daños sufridos x#1%", the multiplier of Represalias, Tiro Penetrante and Salto.</summary>
        public const int DanoSufridoPorCiento = 1163;

        /// <param name="clasesDelGolpe">
        /// The kinds of the blow being read, for the rows registered under a damage kind
        /// (Salto's "1163 under D"): a row with a kind counts only for a blow of that kind.
        /// Without the list every live row counts.
        /// </param>
        public int Multiplicador(int efecto, int ronda, IReadOnlyCollection<string> clasesDelGolpe = null)
        {
            double total = 1.0;
            bool alguno = false;
            foreach (var e in _puestos)
            {
                if (e.EffectId != efecto || !e.Vivo(ronda)) continue;
                if (e.Cuanto == 0) continue;
                if (clasesDelGolpe != null && !string.IsNullOrEmpty(e.Disparador) && e.Disparador != "I")
                {
                    bool aplica = false;
                    foreach (var d in e.Disparador.Split('|'))
                    {
                        if (clasesDelGolpe.Contains(d.Trim())) { aplica = true; break; }
                    }
                    if (!aplica) continue;
                }
                total *= e.Cuanto / 100.0;
                alguno = true;
            }
            return alguno ? (int)Math.Round(total * 100) : 100;
        }

        /// <summary>
        /// The live rows of an effect that a blow of these kinds reads: registered under a damage
        /// trigger ("D", "DM"...), one of whose triggers is among the blow's kinds.
        /// </summary>
        public List<Buff> LeidasPorElGolpe(int efecto, int ronda, IReadOnlyCollection<string> clasesDelGolpe)
        {
            var fuera = new List<Buff>();
            foreach (var e in _puestos)
            {
                if (e.EffectId != efecto || e.Pendiente || !e.Vivo(ronda)) continue;
                if (string.IsNullOrEmpty(e.Disparador)) continue;
                foreach (var d in e.Disparador.Split('|'))
                {
                    if (clasesDelGolpe.Contains(d.Trim())) { fuera.Add(e); break; }
                }
            }
            return fuera;
        }

        /// <summary>What the buffs add to a specific spell: base damage or range.</summary>
        public int DelHechizo(int hechizo, SpellAspect que, int ronda)
        {
            int total = 0;
            foreach (var e in _puestos)
            {
                if (e.Sobre == que && e.HechizoAfectado == hechizo && e.Vivo(ronda)) total += e.Cuanto;
            }
            return total;
        }

        /// <summary>
        /// What a "set" modifier pins a spell's number to -- 2905, 2906 -- or null when no live
        /// row pins it. The latest row wins: a pin is not a sum.
        /// </summary>
        public int? FijadoDelHechizo(int hechizo, SpellAspect que, int ronda)
        {
            for (int i = _puestos.Count - 1; i >= 0; i--)
            {
                var e = _puestos[i];
                if (e.Sobre == que && e.HechizoAfectado == hechizo && e.Vivo(ronda)) return e.Cuanto;
            }
            return null;
        }

        /// <summary>Whether a live row of a switch modifier (289, 297, 299, 314, 798) holds for a spell.</summary>
        public bool TieneDelHechizo(int hechizo, SpellAspect que, int ronda)
            => FijadoDelHechizo(hechizo, que, ronda).HasValue;

        /// <summary>The last temporary appearance still active, or zero.</summary>
        public int AparienciaEn(int ronda)
        {
            for (int i = _puestos.Count - 1; i >= 0; i--)
            {
                var e = _puestos[i];
                if (e.Apariencia != 0 && e.Vivo(ronda)) return e.Apariencia;
            }
            return 0;
        }

        /// <summary>Takes away the ones that have already expired and returns which they were.</summary>
        /// <param name="leTocaCaer">
        /// Which of the expired rows fall NOW. Without it, all of them. The turn start passes
        /// the rule the captures show: a row falls at the start of its caster's turn, not at
        /// the first turn of its round. Measured over the class captures on the rounds a
        /// monster opens -- the only ones that tell the two apart -- thirteen rows put by the
        /// player fall at his own turn and one at the monster's; and fifty-seven rows put by a
        /// summon fall at the summon's own turn.
        /// </param>
        public List<Buff> Barrer(int ronda, Func<Buff, bool> leTocaCaer = null)
        {
            // What has EXPIRED is swept away, not what «is not alive».
            //
            // It is not the same since delayed buffs exist: one that has not yet
            // started is not alive either, and with the earlier condition the broom swept it the first time
            // it went by, that is the turn after casting it.
            //
            // That is exactly what was seen with the Flecha Castigadora: its two bonuses appeared with
            // their countdown -the 3 and the 2- and on the next turn they disappeared leaving the chain
            // empty, without ever getting applied. They were born and the broom took them before it was
            // their turn to start.
            bool cae(Buff e) => Caducado(e, ronda) && (leTocaCaer == null || leTocaCaer(e));
            var caidos = _puestos.FindAll(cae);
            _puestos.RemoveAll(cae);

            // A temporary state cannot outlive the buff that set it. It is kept if
            // there is still another live buff representing the same state.
            // A 952 falling puts nothing back and takes nothing away: the state it switched off
            // simply counts again.
            foreach (var caido in caidos)
            {
                if (caido.Estado == 0 || caido.EffectId == Jondo.Unity.World.Combat.EffectSupport.DisableState) continue;
                if (!SigueHabiendo(caido.Estado)) _estados.Remove(caido.Estado);
            }
            return caidos;
        }

        /// <summary>Whether a row still puts this state -- a 952 switching it off does not count.</summary>
        private bool SigueHabiendo(int estado)
        {
            return _puestos.Any(e => e.Estado == estado
                                  && e.EffectId != Jondo.Unity.World.Combat.EffectSupport.DisableState);
        }

        /// <summary>
        /// Removes and returns delayed one-shot heals whose start round has arrived. They must be
        /// taken before the regular expiry sweep because a zero-duration effect expires one round
        /// after it starts.
        /// </summary>
        public List<Buff> TakeDueHealing(int round)
        {
            var due = _puestos.FindAll(e => e.PendingHealPoints > 0 && round >= e.EmpiezaEnRonda);
            _puestos.RemoveAll(e => e.PendingHealPoints > 0 && round >= e.EmpiezaEnRonda);
            return due;
        }

        /// <summary>
        /// Removes and returns the pending rows whose round has come, in the order they were
        /// put. Like the delayed heals, before the expiry sweep: a pending row's expiry is its
        /// activation round, and the sweep would take it as merely expired.
        /// </summary>
        public List<Buff> TakeDuePending(int round)
        {
            var due = _puestos.FindAll(e => e.Pendiente && round >= e.EmpiezaEnRonda);
            _puestos.RemoveAll(e => e.Pendiente && round >= e.EmpiezaEnRonda);
            return due;
        }

        /// <summary>Whether a buff's time is up. One that has not started yet, is NOT.</summary>
        /// <remarks>A pending row never expires on its own: it goes when it goes off.</remarks>
        private static bool Caducado(Buff embrujo, int ronda)
            => !embrujo.Pendiente && embrujo.CaducaEnRonda >= 0 && ronda >= embrujo.CaducaEnRonda;

        public void Vaciar()
        {
            _puestos.Clear();
            _estados.Clear();
            Actitudes.Clear();
            _gradosDeActitud.Clear();
        }
    }
}

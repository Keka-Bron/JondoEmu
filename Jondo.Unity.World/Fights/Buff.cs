using System;
using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.World.Fights
{
    /// <summary>
    /// A qué afecta un embrujo que no toca una característica suelta.
    ///
    /// Los tres salen del catálogo de efectos del cliente y comparten forma: el hechizo afectado
    /// viaja en el <c>diceNum</c> y lo que se le suma, en el <c>value</c>.
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
    }

    /// <summary>
    /// Un embrujo: lo que un hechizo deja puesto sobre alguien, y hasta cuándo.
    ///
    /// No hay ninguna lista de hechizos escrita a mano detrás de esto. Cada embrujo sale de una
    /// entrada del <c>EffectsJson</c> del hechizo, y lo que significa el número de efecto lo dice
    /// la tabla <c>Effects</c> del cliente. Aquí sólo se guarda lo ya resuelto.
    /// </summary>
    public sealed class Buff
    {
        /// <summary>El número correlativo con el que viaja al cliente, empezando por uno.</summary>
        public int Numero { get; set; }

        public int EffectId { get; set; }
        public int EffectUid { get; set; }

        /// <summary>La característica que toca, o cero si lo suyo va por otro lado.</summary>
        public int Caracteristica { get; set; }

        /// <summary>Cuánto, ya con su signo.</summary>
        public int Cuanto { get; set; }

        /// <summary>Si afecta a un hechizo concreto, cuál y de qué manera.</summary>
        public SpellAspect Sobre { get; set; }
        public int HechizoAfectado { get; set; }

        /// <summary>El estado que pone o quita, si es de los que hacen eso.</summary>
        public int Estado { get; set; }

        public int HechizoOrigen { get; set; }
        public int NivelOrigen { get; set; }
        public long Quien { get; set; }
        public string Disparador { get; set; } = "I";

        /// <summary>La ronda en la que se cae. Menos uno es "hasta que acabe el combate".</summary>
        public int CaducaEnRonda { get; set; }

        /// <summary>
        /// La ronda en la que EMPIEZA a valer. Para casi todos es la del lanzamiento; para los
        /// retardados, tantas rondas después como diga el retardo del efecto.
        ///
        /// Sin esto no había manera de expresar «esto empieza dentro de dos turnos», que es lo que
        /// hace la Flecha Castigadora, y el embrujo se aplicaba entero en el acto.
        /// </summary>
        public int EmpiezaEnRonda { get; set; }

        /// <summary>
        /// Si se suma al que ya hubiera igual en vez de sustituirlo. Lo llevan los que un hechizo
        /// va poniendo cada vez que pasa algo —cada paso, cada golpe recibido—, que se acumulan.
        /// </summary>
        public bool Apila { get; set; }

        /// <summary>
        /// An instantaneous heal waiting for <see cref="EmpiezaEnRonda"/>. The amount is resolved
        /// when the spell is cast, like every other delayed buff, but missing-life capping is done
        /// only when the heal becomes due.
        /// </summary>
        public int PendingHealPoints { get; set; }

        public bool Vivo(int ronda)
            => ronda >= EmpiezaEnRonda && (CaducaEnRonda < 0 || ronda < CaducaEnRonda);
    }

    /// <summary>
    /// Los embrujos y los estados que uno lleva encima, y las actitudes que le dan sus objetos.
    ///
    /// Va aparte del <see cref="Fighter"/> para que se pueda mirar de un vistazo qué hay puesto y
    /// quién lo puso, que es justo lo que el cliente pinta en el panel de embrujos.
    /// </summary>
    public sealed class Buffs
    {
        private readonly List<Buff> _puestos = new List<Buff>();
        private readonly HashSet<int> _estados = new HashSet<int>();

        /// <summary>
        /// Los hechizos que los objetos regalan: las actitudes de los dofus y de los trofeos, que
        /// vienen del efecto 1175 de cada objeto. Se registran al empezar el combate y son las que
        /// se disparan al principio y al final de cada turno.
        /// </summary>
        public List<int> Actitudes { get; } = new List<int>();

        /// <summary>
        /// Los hechizos que uno lleva puestos y que TODAVÍA TIENEN ALGO QUE HACER más adelante.
        ///
        /// Un hechizo no se acaba al lanzarlo: sus efectos con disparador distinto de "I" quedan a
        /// la espera de que pase lo suyo. El Centinela del Ocra es el caso claro: al lanzarlo da
        /// diez de alcance y un veinte por ciento de daños a distancia, y luego, POR CADA PASO que
        /// se anda, se come uno de alcance y un dos por ciento. Ese "por cada paso" es el
        /// disparador CCMPARR, y para poder dispararlo hay que acordarse de que el hechizo sigue
        /// puesto.
        ///
        /// Es lo mismo que las actitudes de los objetos, pero con fecha de caducidad.
        /// </summary>
        public List<ActiveSpell> ActiveSpells { get; } = new List<ActiveSpell>();

        /// <summary>Un hechizo que sigue puesto y en qué grado, hasta que se caiga.</summary>
        public sealed class ActiveSpell
        {
            public int Hechizo { get; set; }
            public int Grado { get; set; }
            public int CaducaEnRonda { get; set; }
            public bool Vivo(int ronda) => CaducaEnRonda < 0 || ronda < CaducaEnRonda;
        }

        /// <summary>Deja apuntado que este hechizo sigue puesto, o alarga el que ya estaba.</summary>
        public void Enganchar(int hechizo, int grado, int caducaEnRonda)
        {
            var ya = _enganchesPorHechizo(hechizo);
            if (ya != null)
            {
                ya.Grado = grado;
                ya.CaducaEnRonda = caducaEnRonda;
                return;
            }
            ActiveSpells.Add(new ActiveSpell { Hechizo = hechizo, Grado = grado, CaducaEnRonda = caducaEnRonda });
        }

        private ActiveSpell _enganchesPorHechizo(int hechizo)
            => ActiveSpells.FirstOrDefault(e => e.Hechizo == hechizo);

        /// <summary>Quita los enganches cumplidos.</summary>
        public void BarrerEnganches(int ronda) => ActiveSpells.RemoveAll(e => !e.Vivo(ronda));

        public IReadOnlyList<Buff> Puestos => _puestos;
        public IReadOnlyCollection<int> Estados => _estados;

        public bool TieneEstado(int estado) => _estados.Contains(estado);

        public void PonerEstado(int estado) { if (estado != 0) _estados.Add(estado); }
        public void QuitarEstado(int estado) => _estados.Remove(estado);

        /// <summary>
        /// Añade un embrujo con el número que le toque. El número NO es de cada luchador: es
        /// correlativo del combate entero, y el que se usa luego para quitarlo con el jya. En la
        /// captura los del jugador van del 19 al 25 y los del monstruo siguen del 26 al 32, misma
        /// serie.
        /// </summary>
        public Buff Poner(Buff embrujo, Func<int> siguienteNumero)
        {
            // Un mismo efecto del mismo hechizo no se apila: se refresca. Es lo que hace Flecha
            // Helada al lanzarse dos veces seguidas, que renueva sus tres turnos en vez de sumar
            // otros ocho de daños básicos.
            //
            // Con una excepción: los que APILAN, que son los que un hechizo va poniendo cada vez
            // que pasa algo. El Centinela se come uno de alcance por cada paso, y a los tres pasos
            // hay que llevar tres menos, no uno. En la captura se ve claro: el servidor manda un
            // embrujo NUEVO por cada paso, apuntando al primero.
            if (embrujo.Apila)
            {
                embrujo.Numero = siguienteNumero();
                _puestos.Add(embrujo);
                return embrujo;
            }

            // La ENTRADA DEL CATÁLOGO forma parte de la llave, y faltaba.
            //
            // Un hechizo puede traer dos efectos iguales en todo menos en su número de entrada:
            // la Flecha Castigadora lleva dos veces el efecto 293 —«+24 de daños básicos» y «+32»—
            // que comparten efecto, hechizo de origen, hechizo afectado y destinatario, y sólo se
            // distinguen por el effectUid (424781 y 424782). Sin él en la llave, el segundo pisaba
            // al primero y de los dos acumulados sólo quedaba uno.
            var yaEstaba = _puestos.FirstOrDefault(e => e.EffectId == embrujo.EffectId
                                                     && e.EffectUid == embrujo.EffectUid
                                                     && e.HechizoOrigen == embrujo.HechizoOrigen
                                                     && e.HechizoAfectado == embrujo.HechizoAfectado
                                                     && e.Quien == embrujo.Quien);
            if (yaEstaba != null)
            {
                yaEstaba.Cuanto = embrujo.Cuanto;
                yaEstaba.CaducaEnRonda = embrujo.CaducaEnRonda;
                if (embrujo.PendingHealPoints > 0)
                {
                    yaEstaba.PendingHealPoints = embrujo.PendingHealPoints;
                    yaEstaba.EmpiezaEnRonda = embrujo.EmpiezaEnRonda;
                }
                return yaEstaba;
            }

            embrujo.Numero = siguienteNumero();
            _puestos.Add(embrujo);
            return embrujo;
        }

        /// <summary>Lo que suman los embrujos a una característica.</summary>
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
        /// Lo que MULTIPLICAN los embrujos de un número de efecto, en tanto por ciento.
        ///
        /// Hay una familia que no suma sino que multiplica: el 1163 es "daños sufridos x#1%" y el
        /// 1159 "curas recibidas x#1%". No tienen característica en el catálogo —el cliente los
        /// resuelve por su número—, así que no valen los mismos caminos que el resto.
        ///
        /// Devuelve cien cuando no hay ninguno, o sea "por uno". Varios se encadenan: dos del
        /// ciento diez dan un ciento veintiuno.
        /// </summary>
        public int Multiplicador(int efecto, int ronda)
        {
            double total = 1.0;
            bool alguno = false;
            foreach (var e in _puestos)
            {
                if (e.EffectId != efecto || !e.Vivo(ronda)) continue;
                if (e.Cuanto == 0) continue;
                total *= e.Cuanto / 100.0;
                alguno = true;
            }
            return alguno ? (int)Math.Round(total * 100) : 100;
        }

        /// <summary>Lo que suman los embrujos a un hechizo concreto: daño base o alcance.</summary>
        public int DelHechizo(int hechizo, SpellAspect que, int ronda)
        {
            int total = 0;
            foreach (var e in _puestos)
            {
                if (e.Sobre == que && e.HechizoAfectado == hechizo && e.Vivo(ronda)) total += e.Cuanto;
            }
            return total;
        }

        /// <summary>Se lleva los que ya han caducado y devuelve cuáles eran.</summary>
        public List<Buff> Barrer(int ronda)
        {
            // Se barre lo que ha CADUCADO, no lo que «no esta vivo».
            //
            // No es lo mismo desde que existen los embrujos retardados: uno que todavia no ha
            // empezado tampoco esta vivo, y con la condicion de antes lo barria la primera vez que
            // pasaba la escoba, o sea el turno siguiente a lanzarlo.
            //
            // Eso es justo lo que se veia con la Flecha Castigadora: sus dos bonos aparecian con
            // su cuenta atras -el 3 y el 2- y al turno siguiente desaparecian dejando la cadena
            // vacia, sin llegar a aplicarse nunca. Nacian y se los llevaba la escoba antes de que
            // les tocara empezar.
            var caidos = _puestos.FindAll(e => Caducado(e, ronda));
            _puestos.RemoveAll(e => Caducado(e, ronda));
            return caidos;
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

        /// <summary>Si a un embrujo se le ha pasado la hora. Uno que aun no ha empezado, NO.</summary>
        private static bool Caducado(Buff embrujo, int ronda)
            => embrujo.CaducaEnRonda >= 0 && ronda >= embrujo.CaducaEnRonda;

        public void Vaciar()
        {
            _puestos.Clear();
            _estados.Clear();
            Actitudes.Clear();
        }
    }
}

namespace Jondo.Unity.World.Fights
{
    /// <summary>
    /// Lo que cambia de un tipo de combate a otro, en un sitio y con nombre.
    /// </summary>
    /// <remarks>
    /// <b>Por qué esto y no dos motores.</b> Andar, empujar, el orden de turnos, los embrujos, el
    /// daño, las resistencias y los invocados son el noventa por ciento del motor y son idénticos
    /// peleando contra monstruos o contra una persona. Partirlo daría dos copias que se separan, y
    /// un arreglo de daño habría que hacerlo dos veces o se quedaría a medias. Es el mismo
    /// argumento por el que el motor de efectos es uno y no uno por raza.
    ///
    /// Lo que sí es distinto son estas siete respuestas, y estaban disueltas en dieciséis <c>if</c>
    /// repartidos por cinco métodos —<c>if (!fight.IsDuel)</c>, <c>fight.IsKoliseo ? … : …</c>—.
    /// Así, el motor deja de preguntar QUÉ CLASE DE COMBATE ERES y pregunta QUÉ HAGO, y añadir algo
    /// del koliseo toca una clase en vez de cinco métodos.
    ///
    /// <code>
    ///                       ContraMonstruos   Desafío   Koliseo
    ///   HayRetos                    sí          no        no
    ///   RelojDeColocación        45,0 s          —      59,2 s
    ///   TipoDelKam                   4           0         7
    ///   KaaConCuentaAtrás           sí          no        sí
    ///   ReparteBotín                sí          no        no
    ///   PagaElKoliseo               no          no        sí
    ///   BorraElGrupoAlGanar         sí          no        no
    ///   AvanzaDeSala                sí          no        no
    /// </code>
    ///
    /// Los números no son elegidos: el 4, el 0 y el 7 son el f2 del kam en las capturas, y el 592
    /// es el f5 del kaa del koliseo.
    /// </remarks>
    public abstract class FightRules
    {
        /// <summary>Si se ofrecen retos, que dan un extra sobre el botín de los monstruos.</summary>
        public abstract bool HayRetos { get; }

        /// <summary>Lo que dura la colocación, en décimas de segundo. Cero: no hay reloj.</summary>
        public abstract int RelojDeColocacion { get; }

        /// <summary>El tipo que va en el f2 del kam.</summary>
        public abstract int TipoDelKam { get; }

        /// <summary>Si enfrente hay monstruos de verdad, que es lo que el kam lista.</summary>
        public abstract bool EnfrenteHayMonstruos { get; }

        /// <summary>Si se gana experiencia, kamas y objetos.</summary>
        public abstract bool ReparteBotin { get; }

        /// <summary>Si el que gana cobra lo del koliseo: kolichas, vitorichas, kamas y experiencia.</summary>
        /// <remarks>
        /// Va aparte de <see cref="ReparteBotin"/> porque no es el mismo reparto ni sale del mismo
        /// sitio. Aquel son las tablas de botín de los monstruos y su experiencia; esto es lo que
        /// paga el koliseo por ganar, y enfrente no hay monstruos de los que sacar nada.
        /// </remarks>
        public abstract bool PagaElKoliseo { get; }

        /// <summary>Si al ganar desaparece del mapa el grupo con el que se peleaba.</summary>
        public abstract bool BorraElGrupoAlGanar { get; }

        /// <summary>Si ganar puede mover a la sala siguiente de una mazmorra.</summary>
        public abstract bool AvanzaDeSala { get; }

        /// <summary>Si el kaa lleva cuenta atrás. Se deduce del reloj: no es otra decisión.</summary>
        public bool KaaConCuentaAtras => RelojDeColocacion > 0;

        /// <summary>Cómo se llama esto en el registro.</summary>
        public abstract string Nombre { get; }

        // ─── Las tres ───────────────────────────────────────────────────────

        /// <summary>Pelear contra monstruos, que es de donde salió todo el motor.</summary>
        public static readonly FightRules ContraMonstruos = new Monstruos();

        /// <summary>Un desafío entre dos jugadores.</summary>
        public static readonly FightRules Desafio = new Reto();

        /// <summary>El koliseo: PvP, pero con reloj de colocación como un combate normal.</summary>
        public static readonly FightRules Koliseo = new Arena();

        private sealed class Monstruos : FightRules
        {
            public override bool HayRetos => true;

            /// <summary>Cuarenta y cinco segundos. El cliente enseña la misma cuenta atrás.</summary>
            public override int RelojDeColocacion => 450;

            public override int TipoDelKam => 4;
            public override bool EnfrenteHayMonstruos => true;
            public override bool ReparteBotin => true;
            public override bool PagaElKoliseo => false;
            public override bool BorraElGrupoAlGanar => true;
            public override bool AvanzaDeSala => true;
            public override string Nombre => "contra monstruos";
        }

        private sealed class Reto : FightRules
        {
            /// <summary>No hay botín que multiplicar, y la captura del desafío no trae ni un reto.</summary>
            public override bool HayRetos => false;

            /// <summary>
            /// Ninguno: el combate empieza cuando los dos pulsan listo.
            /// </summary>
            /// <remarks>
            /// No es que el reloj se esconda: el servidor real no manda ninguno. Su kaa son seis
            /// bytes sin el f5 del tiempo.
            /// </remarks>
            public override int RelojDeColocacion => 0;

            /// <summary>Sin tipo. Medido: su kam llega «f3=retado f5=id f6=retador».</summary>
            public override int TipoDelKam => 0;

            public override bool EnfrenteHayMonstruos => false;
            public override bool ReparteBotin => false;
            public override bool PagaElKoliseo => false;
            public override bool BorraElGrupoAlGanar => false;
            public override bool AvanzaDeSala => false;
            public override string Nombre => "desafío";
        }

        private sealed class Arena : FightRules
        {
            public override bool HayRetos => false;

            /// <summary>El 592 del kaa de «koliseo completo con invitacion-koli 2vs2».</summary>
            public override int RelojDeColocacion => 592;

            /// <summary>El 7 de su kam, «100728ee0a».</summary>
            public override int TipoDelKam => 7;

            public override bool EnfrenteHayMonstruos => false;

            /// <summary>Las tablas de los monstruos no, que enfrente no hay monstruos.</summary>
            public override bool ReparteBotin => false;

            /// <summary>Y las kolichas sí, que es lo que paga el koliseo. Ver KoliseoRewards.</summary>
            public override bool PagaElKoliseo => true;

            public override bool BorraElGrupoAlGanar => false;
            public override bool AvanzaDeSala => false;
            public override string Nombre => "koliseo";
        }
    }
}

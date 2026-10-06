namespace Jondo.Unity.World.Fights
{
    /// <summary>
    /// What changes from one fight type to another, in one place and with a name.
    /// </summary>
    /// <remarks>
    /// <b>Why this and not two engines.</b> Walking, pushing, the turn order, the buffs, the
    /// damage, the resistances and the summons are ninety per cent of the engine and are identical
    /// fighting against monsters or against a person. Splitting it would give two copies that drift apart, and
    /// a damage fix would have to be made twice or would be left half done. It is the same
    /// argument by which the effect engine is one and not one per breed.
    ///
    /// What is different are these seven answers, and they were dissolved in sixteen <c>if</c>s
    /// spread over five methods —<c>if (!fight.IsDuel)</c>, <c>fight.IsKoliseo ? … : …</c>—.
    /// This way, the engine stops asking WHAT KIND OF FIGHT ARE YOU and asks WHAT DO I DO, and adding something
    /// for the koliseo touches one class instead of five methods.
    ///
    /// <code>
    ///                       ContraMonstruos   Desafío   Koliseo   Entrenamiento
    ///   HayRetos                   yes          no        no          no
    ///   RelojDeColocación        45.0 s          —      59.2 s      45.0 s
    ///   TipoDelKam                   4           0         7           4
    ///   KaaConCuentaAtrás          yes          no        yes         yes
    ///   ReparteBotín               yes          no        no          no
    ///   PagaElKoliseo               no          no        yes         no
    ///   BorraElGrupoAlGanar        yes          no        no          no
    ///   AvanzaDeSala               yes          no        no          no
    ///   DefeatCosts                yes          no        no          no
    /// </code>
    ///
    /// The numbers are not chosen: 4, 0 and 7 are the kam's f2 in the captures, and 592
    /// is the koliseo kaa's f5.
    /// </remarks>
    public abstract class FightRules
    {
        /// <summary>Whether challenges are offered, which give an extra on top of the monsters' loot.</summary>
        public abstract bool HayRetos { get; }

        /// <summary>How long placement lasts, in tenths of a second. Zero: there is no clock.</summary>
        public abstract int RelojDeColocacion { get; }

        /// <summary>The type that goes in the kam's f2.</summary>
        public abstract int TipoDelKam { get; }

        /// <summary>Whether there are real monsters opposite, which is what the kam lists.</summary>
        public abstract bool EnfrenteHayMonstruos { get; }

        /// <summary>Whether experience, kamas and items are earned.</summary>
        public abstract bool ReparteBotin { get; }

        /// <summary>Whether the winner is paid the koliseo's: kolichas, vitorichas, kamas and experience.</summary>
        /// <remarks>
        /// It goes apart from <see cref="ReparteBotin"/> because it is not the same distribution nor does it come from the same
        /// place. That one is the monsters' loot tables and their experience; this is what
        /// the koliseo pays for winning, and opposite there are no monsters to take anything from.
        /// </remarks>
        public abstract bool PagaElKoliseo { get; }

        /// <summary>Whether on winning the group being fought disappears from the map.</summary>
        public abstract bool BorraElGrupoAlGanar { get; }

        /// <summary>Whether winning can move to a dungeon's next room.</summary>
        public abstract bool AvanzaDeSala { get; }

        /// <summary>
        /// Whether losing costs the loser: the energy of <see cref="DefeatPenalty"/>, half his
        /// life, and the way back to his save point. Against monsters it does, in the four
        /// defeats of the captures; in a challenge and in the Koliseo it does not -- the client's
        /// help (Translations 1156704) excepts them by name, and the challenge capture's loser
        /// keeps his 8,250 energy --; and the training dummies cost nothing either: the class
        /// captures lose to Puch Ingball with the energy and the life untouched.
        /// </summary>
        public abstract bool DefeatCosts { get; }

        /// <summary>Whether the kaa carries a countdown. It is deduced from the clock: it is not another decision.</summary>
        public bool KaaConCuentaAtras => RelojDeColocacion > 0;

        /// <summary>What this is called in the log.</summary>
        public abstract string Nombre { get; }

        // ─── The three ──────────────────────────────────────────────────────

        /// <summary>Fighting monsters, which is where the whole engine came from.</summary>
        public static readonly FightRules ContraMonstruos = new Monstruos();

        /// <summary>Un desafío entre dos jugadores.</summary>
        public static readonly FightRules Desafio = new Reto();

        /// <summary>The koliseo: PvP, but with a placement clock like a normal fight.</summary>
        public static readonly FightRules Koliseo = new Arena();

        /// <summary>The kanojedo: hitting a puch, which is a fight against monsters with nothing at stake.</summary>
        public static readonly FightRules Entrenamiento = new Kanojedo();

        private sealed class Monstruos : FightRules
        {
            public override bool HayRetos => true;

            /// <summary>Forty-five seconds. The client shows the same countdown.</summary>
            public override int RelojDeColocacion => 450;

            public override int TipoDelKam => 4;
            public override bool EnfrenteHayMonstruos => true;
            public override bool ReparteBotin => true;
            public override bool PagaElKoliseo => false;
            public override bool BorraElGrupoAlGanar => true;
            public override bool AvanzaDeSala => true;
            public override bool DefeatCosts => true;
            public override string Nombre => "contra monstruos";
        }

        private sealed class Reto : FightRules
        {
            /// <summary>There is no loot to multiply, and the challenge capture does not bring a single challenge.</summary>
            public override bool HayRetos => false;

            /// <summary>
            /// None: the fight starts when both press ready.
            /// </summary>
            /// <remarks>
            /// It is not that the clock is hidden: the real server sends none. Its kaa is six
            /// bytes without the time's f5.
            /// </remarks>
            public override int RelojDeColocacion => 0;

            /// <summary>No type. Measured: its kam arrives «f3=challenged f5=id f6=challenger».</summary>
            public override int TipoDelKam => 0;

            public override bool EnfrenteHayMonstruos => false;
            public override bool ReparteBotin => false;
            public override bool PagaElKoliseo => false;
            public override bool BorraElGrupoAlGanar => false;
            public override bool AvanzaDeSala => false;
            public override bool DefeatCosts => false;
            public override string Nombre => "desafío";
        }

        /// <summary>
        /// The kanojedo's puchs. Measured in the Hipermago capture on the Amakna kanojedo:
        /// the kam is type 4 and the kaa carries its countdown —445 tenths, the usual one
        /// except for the heartbeat— just like against monsters, but in sixty seconds of fighting not
        /// a single challenge opcode comes out, and the final jyg carries EMPTY rewards. And the puch
        /// stays where it was: it is a training bag, not a creature that gets killed.
        /// </summary>
        private sealed class Kanojedo : FightRules
        {
            public override bool HayRetos => false;
            public override int RelojDeColocacion => 450;
            public override int TipoDelKam => 4;
            public override bool EnfrenteHayMonstruos => true;
            public override bool ReparteBotin => false;
            public override bool PagaElKoliseo => false;
            public override bool BorraElGrupoAlGanar => false;
            public override bool AvanzaDeSala => false;
            public override bool DefeatCosts => false;
            public override string Nombre => "entrenamiento";
        }

        private sealed class Arena : FightRules
        {
            public override bool HayRetos => false;

            /// <summary>The 592 of the kaa of «koliseo completo con invitacion-koli 2vs2».</summary>
            public override int RelojDeColocacion => 592;

            /// <summary>The 7 of its kam, «100728ee0a».</summary>
            public override int TipoDelKam => 7;

            public override bool EnfrenteHayMonstruos => false;

            /// <summary>Not the monsters' tables, since there are no monsters opposite.</summary>
            public override bool ReparteBotin => false;

            /// <summary>And the kolichas yes, which is what the koliseo pays. See KoliseoRewards.</summary>
            public override bool PagaElKoliseo => true;

            public override bool BorraElGrupoAlGanar => false;
            public override bool AvanzaDeSala => false;
            public override bool DefeatCosts => false;
            public override string Nombre => "koliseo";
        }
    }
}

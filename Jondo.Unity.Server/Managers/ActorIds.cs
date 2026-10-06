using System.Threading;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Who is who on a map: handing out contextual actor identifiers.
    ///
    /// The contextual id is the number the client uses to refer to each thing on the map. It travels in
    /// the actor's f3, both in the jss -- the full list sent on entering -- and in the jpv -- the one sent
    /// when loading the map --, and it comes back as it is when the player clicks: talking to an NPC
    /// carries his, attacking a monster group carries its own.
    ///
    /// Before, each place made it up on its own and it came out different depending on where one
    /// looked: the jss gave a monster group its MobId -- a -1000000 and going down -- and the jpv gave it
    /// whatever number it got after the map's NPCs. Measured on the Amakna NPC map, which has 52: the
    /// jss sent -1011567 and the jpv -20052, for the same group. And since the client sends back the
    /// last one that reached it, attacking answered «there is no group here» or, worse, attacked
    /// another one.
    ///
    /// The bands, which are not to be touched:
    ///
    ///   players        positive, their CharacterId
    ///   NPCs           from -20000 to -999999, starting at -20000 on EACH map
    ///   monsters       -1000000 and going down, unique across the whole world
    ///   summons        -1, -2, -3... inside their fight, which is another space (see FightInstance)
    ///
    /// NPCs repeat from one map to another on purpose: it is what the real server does -- -20000,
    /// -20001... in order and starting again on the next map -- and the places that look for an NPC
    /// always know which map it is on. Monsters, on the other hand, are numbered only once for the
    /// whole world, because a group can die on one map and be reborn on another.
    /// </summary>
    public static class ActorIds
    {
        /// <summary>The first one given to an NPC on each map.</summary>
        public const long PrimerNpc = -20000;

        /// <summary>How far the NPC band goes. Enough for 980,000 on the same map.</summary>
        public const long UltimoNpc = -999999;

        /// <summary>Where monster groups start, going down.</summary>
        public const long PrimerMonstruo = -1000000;

        /// <summary>
        /// Where the handing out of monsters is up to. It starts one above the first so that the first one
        /// handed out is exactly <see cref="PrimerMonstruo"/>.
        /// </summary>
        private static long _monstruo = PrimerMonstruo + 1;

        /// <summary>
        /// The next free id for a monster group.
        ///
        /// It goes with Interlocked because the group generator of an empty map runs on the thread of the
        /// player arriving: two entering two maps with no written groups at the same time both did the same
        /// <c>_id--</c> on the same field and could take the same number.
        /// </summary>
        public static long NuevoMonstruo() => Interlocked.Decrement(ref _monstruo);

        /// <summary>
        /// Moves the cursor below a number already handed out.
        ///
        /// The groups written in the database carry their MobId set since the seeding, so at start-up one
        /// has to keep clear of them: otherwise, the first group generated on the fly would take a number a
        /// group on the next map already has.
        /// </summary>
        public static void ReservarMonstruosHasta(long yaRepartido)
        {
            while (true)
            {
                long visto = Interlocked.Read(ref _monstruo);
                if (yaRepartido >= visto) return;
                if (Interlocked.CompareExchange(ref _monstruo, yaRepartido, visto) == visto) return;
            }
        }

        /// <summary>The id that goes to the NPC who is number <paramref name="posicion"/> on the map.</summary>
        public static long NpcDelMapa(int posicion) => PrimerNpc - posicion;

        public static bool EsJugador(long id) => id > 0;

        public static bool EsNpc(long id) => id <= PrimerNpc && id >= UltimoNpc;

        public static bool EsMonstruo(long id) => id <= PrimerMonstruo;

        /// <summary>
        /// Leaves the handing out as freshly started. It belongs to the test bench: nobody on the server
        /// must call it, because it would hand out again numbers that already have an owner.
        /// </summary>
        public static void ReiniciarParaPruebas() => Interlocked.Exchange(ref _monstruo, PrimerMonstruo + 1);
    }
}

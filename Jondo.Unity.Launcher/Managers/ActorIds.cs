using System.Threading;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Quién es quién en un mapa: el reparto de identificadores contextuales de actor.
    ///
    /// El id contextual es el número con el que el cliente se refiere a cada cosa que hay en el
    /// mapa. Viaja en el f3 del actor, tanto en el jss —la lista completa que se manda al entrar—
    /// como en el jpv —la que se manda al cargar el mapa—, y vuelve tal cual cuando el jugador
    /// clica: hablar con un NPC lleva el suyo, atacar a un grupo de monstruos lleva el suyo.
    ///
    /// Antes cada sitio se lo inventaba por su cuenta y salía distinto según por dónde se mirara:
    /// el jss daba a un grupo de monstruos su MobId —un -1000000 y bajando— y el jpv le daba el
    /// número que le tocara detrás de los NPCs del mapa. Medido en el mapa de los NPCs de Amakna,
    /// que tiene 52: el jss mandaba el -1011567 y el jpv el -20052, para el mismo grupo. Y como el
    /// cliente devuelve el que le llegó el último, atacar contestaba «aquí no hay ningún grupo»
    /// o, peor, atacaba a otro.
    ///
    /// Las bandas, que no se tocan:
    ///
    ///   jugadores      positivos, su CharacterId
    ///   NPCs           de -20000 a -999999, empezando en -20000 en CADA mapa
    ///   monstruos      -1000000 y bajando, únicos en todo el mundo
    ///   invocaciones   -1, -2, -3... dentro de su combate, que es otro espacio (ver FightInstance)
    ///
    /// Los NPCs se repiten de un mapa a otro a propósito: es lo que hace el servidor real —-20000,
    /// -20001... en orden y vuelta a empezar en el mapa siguiente— y los sitios que buscan un NPC
    /// siempre saben en qué mapa está. Los monstruos, en cambio, se numeran una sola vez para todo
    /// el mundo, porque un grupo puede morir en un mapa y renacer en otro.
    /// </summary>
    public static class ActorIds
    {
        /// <summary>El primero que se le da a un NPC en cada mapa.</summary>
        public const long PrimerNpc = -20000;

        /// <summary>Hasta dónde llega la banda de los NPCs. Da para 980.000 en un mismo mapa.</summary>
        public const long UltimoNpc = -999999;

        /// <summary>Desde dónde empiezan los grupos de monstruos, y hacia abajo.</summary>
        public const long PrimerMonstruo = -1000000;

        /// <summary>
        /// Por dónde va el reparto de monstruos. Empieza uno por encima del primero para que la
        /// primera entrega sea exactamente <see cref="PrimerMonstruo"/>.
        /// </summary>
        private static long _monstruo = PrimerMonstruo + 1;

        /// <summary>
        /// El siguiente id libre para un grupo de monstruos.
        ///
        /// Va con Interlocked porque el generador de grupos de un mapa vacío corre en el hilo del
        /// jugador que llega: dos entrando a la vez a dos mapas sin grupos escritos hacían los dos
        /// el mismo <c>_id--</c> sobre el mismo campo y podían llevarse el mismo número.
        /// </summary>
        public static long NuevoMonstruo() => Interlocked.Decrement(ref _monstruo);

        /// <summary>
        /// Baja el cursor por debajo de un número que ya está repartido.
        ///
        /// Los grupos que vienen escritos en la base traen su MobId puesto desde la siembra, así
        /// que al arrancar hay que apartarse de ellos: si no, el primer grupo que se generara al
        /// vuelo se llevaría un número que ya tiene un grupo del mapa de al lado.
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

        /// <summary>El id que le toca al NPC que hace el número <paramref name="posicion"/> del mapa.</summary>
        public static long NpcDelMapa(int posicion) => PrimerNpc - posicion;

        public static bool EsJugador(long id) => id > 0;

        public static bool EsNpc(long id) => id <= PrimerNpc && id >= UltimoNpc;

        public static bool EsMonstruo(long id) => id <= PrimerMonstruo;

        /// <summary>
        /// Deja el reparto como recién arrancado. Es del banco de pruebas: en el servidor nadie
        /// debe llamarlo, porque volvería a dar números que ya tienen dueño.
        /// </summary>
        public static void ReiniciarParaPruebas() => Interlocked.Exchange(ref _monstruo, PrimerMonstruo + 1);
    }
}

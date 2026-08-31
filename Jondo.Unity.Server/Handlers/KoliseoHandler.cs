using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// El koliseo: por ahora, qué modalidades hay y cuáles están abiertas.
    /// </summary>
    /// <remarks>
    /// Medido en «koliseo completo con invitacion-koli 2vs2». El cliente pide la tabla con un
    /// <c>lux</c> vacío y el servidor contesta con un <c>ltd</c> de cuatro entradas:
    ///
    /// <code>
    ///   f1{      f2{f1=1 f4=1} f3=1 }    1 contra 1, abierta
    ///   f1{f1=1  f2{f1=1 f4=2} f3=1 }    2 contra 2, abierta
    ///   f1{f1=2  f2{f1=1 f4=3} f3=1 }    3 contra 3, abierta
    ///   f1{f1=3  f2{     f4=3}      }    otra de tres, sin el f3: cerrada
    /// </code>
    ///
    /// El <c>f4</c> es cuántos van por equipo y el <c>f3</c> es el interruptor. Las tres primeras
    /// se replican tal cual, que es lo que pedía abrir las tres modalidades; la cuarta se manda
    /// igual de cerrada que en la captura, porque no se sabe qué es y encenderla sería inventar.
    ///
    /// <b>Apuntarse.</b> Ordenando las dos mitades de la conexión por marca de tiempo, que es lo
    /// que hacía falta para leer esto bien, el intercambio entero es:
    ///
    /// <code>
    ///   109,6 s  C-&gt;S  luy { f2 = índice de modalidad }     «1001», y la captura es un 2 contra 2
    ///   109,7 s  S-&gt;C  lth { f2 = el mismo índice }         38 ms después
    ///        ... siete segundos de espera ...
    ///   116,9 s  S-&gt;C  ilw                                  el grupo, con el nombre del compañero
    ///   116,9 s  S-&gt;C  lst { host, ip, billete }            A OTRO SERVIDOR
    ///   446,0 s  C-&gt;S  lte                                  de vuelta, ya acabado el combate
    ///   446,1 s  S-&gt;C  lty, lsr, lsx                        en 80 ms
    /// </code>
    ///
    /// <b>Lo que esto cambia.</b> El koliseo de verdad NO pelea en el servidor de mundo: el
    /// <c>lst</c> manda al cliente a «dofus2-ko-tynril.ankama-games.com» con un billete de 32
    /// bytes, el cliente abre una segunda conexión y el combate entero —kam, kaa, los cuatro jxg,
    /// el reparto— viaja por ahí. Jondo es un solo servidor y monta el combate en el mismo sitio.
    /// Es una diferencia de arquitectura y está dicha, no disimulada.
    ///
    /// <b>Lo que sigue sin estar hecho.</b> El <c>ilw</c> del grupo formado, la invitación entre
    /// compañeros (<c>ijz</c>, <c>ilo</c>, <c>ing</c>, <c>iki</c>, <c>ijx</c>), las clasificaciones
    /// (<c>iqt</c> e <c>irc</c>, dos listas de más de tres mil bytes) y el reparto de kolichas. Y
    /// el emparejamiento de aquí es por orden de llegada, no por puntuación: ver
    /// <see cref="KoliseoQueue"/>.
    /// </remarks>
    public static class KoliseoHandler
    {
        /// <summary>Una modalidad: cuántos por equipo y si está abierta.</summary>
        public readonly record struct Mode(int Index, int TeamSize, bool Open, bool Inner);

        /// <summary>
        /// Las cuatro de la captura, con las tres de verdad abiertas.
        /// </summary>
        /// <remarks>
        /// La cuarta lleva <c>Inner = false</c> porque su <c>f2</c> no trae el <c>f1</c> que
        /// llevan las otras tres. Es una diferencia de un byte y se respeta: replicar lo que se
        /// midió cuesta lo mismo que aproximarlo.
        /// </remarks>
        public static readonly IReadOnlyList<Mode> Modes = new[]
        {
            new Mode(0, 1, true, true),
            new Mode(1, 2, true, true),
            new Mode(2, 3, true, true),
            new Mode(3, 3, false, false),
        };

        /// <summary>El cliente pide la tabla (lux). Se le contesta con el ltd.</summary>
        /// <remarks>
        /// Va por la raíz 3 y con el id de la petición, no por la 1. Estaba mal: se mandaba con
        /// Push, que envuelve en la raíz 1 —«esto lo dice el servidor por su cuenta»— y el cliente
        /// no tenía con qué emparejarlo. En la captura las cinco parejas cuadran una a una:
        ///
        /// <code>
        ///   C-&gt;S  12 19 {…lux…} 10 0e        el 14 va en el f2 de la raíz
        ///   S-&gt;C  1a 45 {…ltd…} 10 0e        y vuelve el mismo 14
        /// </code>
        ///
        /// Y siguen: 15, 16, 17 y 18. Es un contador del cliente, no el -1 de siempre.
        /// </remarks>
        public static async Task SendModesAsync(NetworkStream stream, byte[] payload)
        {
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Ltd, BuildModes(Modes),
                                          ConnectionProtocol.RequestId(payload)));

            Console.WriteLine($"[Koliseo] Tabla de modalidades: " +
                              $"{CountOpen()} abierta(s) de {Modes.Count}.");
        }

        public static int CountOpen()
        {
            int abiertas = 0;
            foreach (var modo in Modes) if (modo.Open) abiertas++;
            return abiertas;
        }

        /// <summary>El cliente se apunta a una modalidad (luy).</summary>
        /// <remarks>
        /// El campo 2 es el índice del ltd: la captura manda «1001» y es de un 2 contra 2, que es
        /// justo la entrada de índice 1. Una modalidad cerrada no admite a nadie, que es lo que
        /// significa que esté cerrada.
        /// </remarks>
        public static async Task EnrolAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? luy = ConnectionProtocol.ReadPayload(payload, Op.Luy);
            if (luy == null) return;

            int indice = -1;
            foreach (var field in ProtoMessage.Parse(luy).Fields)
            {
                if (field.FieldNumber == 2 && field.WireType == 0) indice = (int)field.VarIntValue;
            }

            var modo = FindMode(indice);
            if (modo == null || !modo.Value.Open)
            {
                Console.WriteLine($"[Koliseo] Se apuntan a la modalidad {indice}, que no está abierta.");
                return;
            }

            long yo = GameState.CharacterId;
            if (!KoliseoQueue.Enrol(yo, indice))
            {
                Console.WriteLine($"[Koliseo] {yo} ya estaba en una cola.");
                return;
            }

            // La respuesta medida es el lth con el MISMO indice, a 38 ms, y por la raíz 3 con el
            // id de la petición —el 19 del luy vuelve como 19—. El lsx que había aquí era cosa
            // mía: en la captura no contesta al luy ni una sola vez.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Lth, BuildEnrolled(indice),
                                          ConnectionProtocol.RequestId(payload)));

            Console.WriteLine($"[Koliseo] {yo} en la cola de {modo.Value.TeamSize} contra " +
                              $"{modo.Value.TeamSize}: {KoliseoQueue.CountIn(indice)} esperando.");

            await TryMatchAsync(indice, modo.Value.TeamSize);
        }

        /// <summary>Se apunta un GRUPO entero (lsm).</summary>
        /// <remarks>
        /// El mismo botón que el <see cref="EnrolAsync"/>, pero con gente detrás. Con un grupo
        /// formado el cliente deja de mandar el luy y manda el lsm, y el índice se le mueve del
        /// campo 2 al 1: medido sobre nuestro propio cliente, «0801» al pulsar en un 2 contra 2,
        /// que es la entrada 1 del ltd — el mismo índice que lleva el luy en la captura.
        ///
        /// Se apunta a TODO EL GRUPO y no sólo a quien pulsa, que es lo que quiere decir apuntarse
        /// en grupo; los que ya estuvieran en una cola se quedan donde estaban. El grupo es el
        /// normal, el de <see cref="Parties"/>: el equipo de koliseo lo monta el koliseo después
        /// del emparejamiento, y en la captura el ilw del equipo aparece justo ahí, no antes.
        ///
        /// SIN MEDIR: qué contesta el servidor de verdad a un lsm. No hay captura del camino en
        /// grupo. Se le manda el lth, que es el acuse que saca a la ventana de su estado de espera
        /// y lo que contesta al luy a los 38 ms.
        /// </remarks>
        public static async Task EnrolPartyAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? lsm = ConnectionProtocol.ReadPayload(payload, Op.Lsm);
            if (lsm == null) return;

            int indice = -1;
            foreach (var field in ProtoMessage.Parse(lsm).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 0) indice = (int)field.VarIntValue;
            }

            var modo = FindMode(indice);
            if (modo == null || !modo.Value.Open)
            {
                Console.WriteLine($"[Koliseo] Se apunta un grupo a la modalidad {indice}, que no está abierta.");
                return;
            }

            long yo = GameState.CharacterId;
            var grupo = Parties.Of(yo);
            var quienes = grupo != null ? Parties.MembersOf(grupo) : new List<long> { yo };

            int nuevos = 0;
            foreach (long miembro in quienes)
            {
                if (KoliseoQueue.Enrol(miembro, indice)) nuevos++;
            }

            // El acuse va SIEMPRE, aunque no se haya apuntado nadie nuevo: sin él la ventana se
            // queda como si no hubiera pasado nada, que es exactamente el fallo que trae aquí.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Lth, BuildEnrolled(indice),
                                          ConnectionProtocol.RequestId(payload)));

            Console.WriteLine($"[Koliseo] Grupo de {quienes.Count} en la cola de " +
                              $"{modo.Value.TeamSize} contra {modo.Value.TeamSize} " +
                              $"({nuevos} nuevo(s)): {KoliseoQueue.CountIn(indice)} esperando.");

            await TryMatchAsync(indice, modo.Value.TeamSize);
        }

        /// <summary>El cliente vuelve del koliseo (lte).</summary>
        /// <remarks>
        /// No es salirse de la cola, aunque lo pareciera: en la captura el luy y el lte van a
        /// cinco minutos y medio uno del otro, con el combate entero en medio. Lo que se contesta
        /// son tres tramas en 80 ms —lty, lsr y lsx—; aquí sólo va la última, que es la única de
        /// las tres cuyos cuatro bytes se pueden repetir sin fingir que se entienden. El lty son
        /// 151 bytes sin descifrar y mandar 151 bytes inventados es peor que no mandarlos.
        ///
        /// Por si acaso se le quita también el sitio en la cola: volver del koliseo y seguir
        /// apuntado no tendría sentido, y si no estaba, no cuesta nada.
        /// </remarks>
        public static async Task ReturnAsync(NetworkStream stream)
        {
            KoliseoQueue.Leave(GameState.CharacterId);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lsx, BuildReturned()));

            Console.WriteLine($"[Koliseo] {GameState.CharacterId} vuelve del koliseo.");
        }

        /// <summary>
        /// Si ya hay gente para los dos equipos, monta el combate.
        /// </summary>
        /// <remarks>
        /// Se comprueba que sigan todos conectados ANTES de sacarlos de la cola del todo: entre
        /// apuntarse y llenar la partida cabe una desconexión, y montar un koliseo con un hueco
        /// vacío es peor que esperar al siguiente.
        /// </remarks>
        private static async Task TryMatchAsync(int mode, int teamSize)
        {
            var pareja = KoliseoQueue.TryMatch(mode, teamSize);
            if (pareja == null) return;

            var azul = new List<GameSession>();
            var rojo = new List<GameSession>();

            foreach (long id in pareja.Value.Blue)
            {
                var sesion = SessionRegistry.FindByCharacter(id);
                if (sesion != null && sesion.IsInWorld) azul.Add(sesion);
            }
            foreach (long id in pareja.Value.Red)
            {
                var sesion = SessionRegistry.FindByCharacter(id);
                if (sesion != null && sesion.IsInWorld) rojo.Add(sesion);
            }

            if (azul.Count != teamSize || rojo.Count != teamSize)
            {
                // Alguno se fue por el camino. Los que quedan vuelven a la cola en vez de perder
                // el sitio por culpa de otro.
                foreach (var sesion in azul) KoliseoQueue.Enrol(sesion.State.CharacterId, mode);
                foreach (var sesion in rojo) KoliseoQueue.Enrol(sesion.State.CharacterId, mode);
                Console.WriteLine($"[Koliseo] Faltó alguien al formar la partida; los demás " +
                                  $"vuelven a la cola.");
                return;
            }

            Console.WriteLine($"[Koliseo] Partida de {teamSize} contra {teamSize} formada.");
            await FightHandler.InitiatePvpAsync(azul, rojo, azul[0].MapId, koliseo: true);
        }

        private static Mode? FindMode(int index)
        {
            foreach (var modo in Modes) if (modo.Index == index) return modo;
            return null;
        }

        /// <summary>El lth: el índice de vuelta, tal cual llegó.</summary>
        public static byte[] BuildEnrolled(int modeIndex)
            => Pb.New().VarIfNotZero(2, modeIndex).Build();

        /// <summary>El lsx de la vuelta: «18032001» de la captura.</summary>
        public static byte[] BuildReturned() => Pb.New().Var(3, 3).Var(4, 1).Build();

        /// <summary>El ltd, byte por byte como la captura.</summary>
        public static byte[] BuildModes(IReadOnlyList<Mode> modes)
        {
            var ltd = Pb.New();

            foreach (var modo in modes)
            {
                var dentro = Pb.New();
                if (modo.Inner) dentro.Var(1, 1);
                dentro.Var(4, modo.TeamSize);

                var entrada = Pb.New();
                // El índice cero no viaja: es el valor por omisión de protobuf y la captura lo
                // deja fuera en la primera entrada y sólo en ella.
                entrada.VarIfNotZero(1, modo.Index);
                entrada.Msg(2, dentro);
                if (modo.Open) entrada.Var(3, 1);

                ltd.Msg(1, entrada);
            }

            return ltd.Build();
        }
    }
}

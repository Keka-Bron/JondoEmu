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

            int indice = IndiceDeModalidad(lsm, 1);

            var modo = FindMode(indice);
            if (modo == null || !modo.Value.Open)
            {
                Console.WriteLine($"[Koliseo] Se apunta un grupo a la modalidad {indice}, que no está abierta.");
                return;
            }

            long yo = GameState.CharacterId;

            // El castigo por dejar vencer un cartel. El servidor real contesta con el lqn 642 y
            // los minutos que faltan, y no te apunta.
            int faltan = KoliseoOffers.MinutesLeft(yo);
            if (faltan > 0)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lqn, BuildStillBanned(faltan)));
                Console.WriteLine($"[Koliseo] {yo} no puede apuntarse todavia: {faltan} minuto(s).");
                return;
            }

            var grupo = Parties.Of(yo);
            var quienes = grupo != null ? Parties.MembersOf(grupo) : new List<long> { yo };

            int nuevos = 0;
            foreach (long miembro in quienes)
            {
                if (KoliseoQueue.Enrol(miembro, indice)) nuevos++;
            }

            // Va SIEMPRE, aunque no se haya apuntado nadie nuevo: sin esto la ventana se queda
            // como si no hubiera pasado nada, que es exactamente el fallo que trae aqui.
            // EL ESTADO DE LA COLA, que es lo que pinta el «buscando». No es un acuse a la
            // peticion: es un empujon del servidor con como esta el jugador ahora mismo.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lsx, BuildQueueState(indice, true)));

            Console.WriteLine($"[Koliseo] Grupo de {quienes.Count} en la cola de " +
                              $"{modo.Value.TeamSize} contra {modo.Value.TeamSize} " +
                              $"({nuevos} nuevo(s)): {KoliseoQueue.CountIn(indice)} esperando.");

            await TryMatchAsync(indice, modo.Value.TeamSize);
        }

        /// <summary>El jugador acepta o rechaza la partida (luy).</summary>
        /// <remarks>
        /// El luy es <c>{ map&lt;string,string&gt;, bool }</c> leido del propio cliente: el campo 2 es
        /// un BOOLEANO, no un indice de modalidad. Aceptar llega como «1001» y el servidor real
        /// contesta un lth identico por la raiz 3 con el id de la peticion, a 38 ms.
        ///
        /// SIN MEDIR el rechazo: en la captura se dejo vencer el plazo. Un bool de proto3 en falso
        /// no viaja, asi que un «no» tendria que llegar con la carga vacia, y asi se trata. El
        /// desafio pvp hace exactamente lo mismo -- aceptar «08ec031001», rechazar «08e903» --.
        /// </remarks>
        public static async Task AnswerOfferAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? luy = ConnectionProtocol.ReadPayload(payload, Op.Luy);
            if (luy == null) return;

            long yo = GameState.CharacterId;
            var oferta = KoliseoOffers.Of(yo);
            if (oferta == null)
            {
                Console.WriteLine($"[Koliseo] {yo} contesta a una partida que ya no existe.");
                return;
            }

            bool acepta = false;
            foreach (var field in ProtoMessage.Parse(luy).Fields)
            {
                if (field.FieldNumber == 2 && field.WireType == 0) acepta = field.VarIntValue != 0;
            }

            // El acuse va siempre, se diga que si o que no: es la respuesta a SU peticion.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Lth, BuildAccepted(acepta),
                                          ConnectionProtocol.RequestId(payload)));

            if (!acepta)
            {
                Console.WriteLine($"[Koliseo] {yo} rechaza la partida.");
                await DeshacerAsync(oferta, new List<long> { yo });
                return;
            }

            Console.WriteLine($"[Koliseo] {yo} acepta la partida.");
            if (!KoliseoOffers.Accept(oferta, yo)) return;

            KoliseoOffers.Forget(oferta);
            await EmpezarAsync(oferta);
        }

        /// <summary>Espera el plazo y, si no han dicho que si todos, la deshace.</summary>
        private static async Task VencerAsync(KoliseoOffers.Offer oferta)
        {
            await Task.Delay(TimeSpan.FromSeconds(KoliseoOffers.Segundos + 1));

            // Si alguien la acepto entera por los pelos, Close devuelve falso y aqui no se toca.
            if (!KoliseoOffers.Close(oferta)) return;

            Console.WriteLine($"[Koliseo] Vence el plazo de la partida {oferta.Id}.");
            await DeshacerAsync(oferta, KoliseoOffers.WhoDidNotAnswer(oferta), yaCerrada: true);
        }

        /// <summary>
        /// Deshace una partida: castiga a quien no dijo que si y devuelve a los demas a la cola.
        /// </summary>
        /// <remarks>
        /// Medido al vencer el plazo: lqn con el aviso y la marca de tiempo, ltk vacio, y el lsx
        /// diciendo que ya no se busca. El lty de la clasificacion tambien viaja ahi y NO se manda:
        /// son 144 bytes con un bloque de coma flotante dentro que no se ha descifrado, y mandar
        /// bytes inventados es peor que no mandarlos.
        /// </remarks>
        private static async Task DeshacerAsync(KoliseoOffers.Offer oferta, List<long> culpables,
                                                bool yaCerrada = false)
        {
            if (!yaCerrada && !KoliseoOffers.Close(oferta)) return;

            var castigados = new HashSet<long>(culpables);
            var hasta = DateTime.UtcNow.AddMinutes(KoliseoOffers.Castigo);

            foreach (long quien in oferta.Everybody)
            {
                var sesion = SessionRegistry.FindByCharacter(quien);

                if (castigados.Contains(quien))
                {
                    KoliseoOffers.Ban(quien, hasta);
                    if (sesion != null)
                    {
                        await Escribir(sesion, ConnectionProtocol.Push(Op.Lqn,
                            BuildSanction(new DateTimeOffset(hasta).ToUnixTimeSeconds())));
                    }
                }
                else
                {
                    // El que si dijo que si no pierde el sitio por culpa de otro.
                    KoliseoQueue.Enrol(quien, oferta.Mode);
                }

                if (sesion == null) continue;
                await Escribir(sesion, ConnectionProtocol.Push(Op.Ltk));
                await Escribir(sesion, ConnectionProtocol.Push(Op.Lsx, BuildLeftQueue(oferta.Mode)));
            }

            Console.WriteLine($"[Koliseo] Partida deshecha: {castigados.Count} castigado(s) " +
                              $"{KoliseoOffers.Castigo} minuto(s).");

            // Los que se quedaron pueden emparejarse con otros que estuvieran esperando.
            var modo = FindMode(oferta.Mode);
            if (modo != null) await TryMatchAsync(oferta.Mode, modo.Value.TeamSize);
        }

        /// <summary>Todos han dicho que si: se monta el combate.</summary>
        private static async Task EmpezarAsync(KoliseoOffers.Offer oferta)
        {
            var azul = new List<GameSession>();
            var rojo = new List<GameSession>();

            foreach (long id in oferta.Blue)
            {
                var sesion = SessionRegistry.FindByCharacter(id);
                if (sesion != null && sesion.IsInWorld) azul.Add(sesion);
            }
            foreach (long id in oferta.Red)
            {
                var sesion = SessionRegistry.FindByCharacter(id);
                if (sesion != null && sesion.IsInWorld) rojo.Add(sesion);
            }

            if (azul.Count != oferta.TeamSize || rojo.Count != oferta.TeamSize)
            {
                Console.WriteLine("[Koliseo] Alguien se fue entre aceptar y empezar; se deshace.");
                await DeshacerAsync(oferta, new List<long>(), yaCerrada: true);
                return;
            }

            Console.WriteLine($"[Koliseo] Todos aceptan: partida de {oferta.TeamSize} contra " +
                              $"{oferta.TeamSize}.");
            await FightHandler.InitiatePvpAsync(azul, rojo, azul[0].MapId, koliseo: true);
        }

        /// <summary>Escribe a una sesion sin que un socket caido se lleve por delante a los demas.</summary>
        private static async Task Escribir(GameSession sesion, byte[] frame)
        {
            try
            {
                await sesion.SendAsync(frame);
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[Koliseo] No se ha podido escribir a {sesion.Id}: {ex.Message}");
            }
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
                ConnectionProtocol.Push(Op.Lsx,
                    BuildLeftQueue(KoliseoOffers.LastMode(GameState.CharacterId))));

            Console.WriteLine($"[Koliseo] {GameState.CharacterId} vuelve del koliseo.");
        }

        /// <summary>
        /// El índice de modalidad que trae una petición de apuntarse.
        /// </summary>
        /// <remarks>
        /// CERO CUANDO NO VIENE, y ahí estaba el fallo. El campo no viaja cuando vale cero —es el
        /// valor por omisión de protobuf, y lo dice nuestro propio Op.cs sobre este mismo ltd: «el
        /// índice cero no viaja»— así que un uno contra uno llega con la carga vacía. Empezando en
        /// menos uno, esa carga vacía se leía como «modalidad -1», caía en «no está abierta», y el
        /// cliente se quedaba esperando un acuse que no llegaba sin un solo aviso por ninguna
        /// parte. El dos contra dos funcionaba porque su índice es el uno y sí viaja.
        ///
        /// El número de campo cambia según por dónde entre —el luy lo trae en el 2 y el lsm en el
        /// 1— pero la numeración es la misma en los dos, y es el orden de las entradas del ltd:
        /// 0 uno contra uno, 1 dos contra dos, 2 tres contra tres.
        /// </remarks>
        internal static int IndiceDeModalidad(byte[] carga, int campo)
        {
            foreach (var field in ProtoMessage.Parse(carga).Fields)
            {
                if (field.FieldNumber == campo && field.WireType == 0) return (int)field.VarIntValue;
            }
            return 0;
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

            // Y AQUI NO EMPIEZA EL COMBATE, empieza el cartel. El servidor real manda un lsh
            // con el plazo y espera; medido en dos capturas, y el plazo son 59 segundos.
            var oferta = KoliseoOffers.Open(mode, teamSize, pareja.Value.Blue, pareja.Value.Red);

            byte[] aviso = ConnectionProtocol.Push(Op.Lsh, BuildOffer(KoliseoOffers.Segundos));
            foreach (var sesion in azul) await Escribir(sesion, aviso);
            foreach (var sesion in rojo) await Escribir(sesion, aviso);

            Console.WriteLine($"[Koliseo] Partida de {teamSize} contra {teamSize} encontrada: " +
                              $"{KoliseoOffers.Segundos} s para aceptarla.");

            _ = VencerAsync(oferta);
        }

        private static Mode? FindMode(int index)
        {
            foreach (var modo in Modes) if (modo.Index == index) return modo;
            return null;
        }

        /// <summary>
        /// El lsx: en qué cola está el jugador, que es lo que pinta el «buscando».
        /// </summary>
        /// <remarks>
        /// Esto estuvo mal leído desde el principio y merece quedar escrito. Se le contestaba un
        /// lth con el índice dentro, y el lth no es eso: el esquema del propio cliente dice
        /// <c>lth { bool gdak = 1; bool gdal = 2; }</c>, dos booleanos, y es la respuesta a un luy
        /// —<c>{ map&lt;string,string&gt;, bool }</c>—, que no es apuntarse a nada. La ventana
        /// recibía una cosa que no entendía y se quedaba igual que estaba, sin un solo error.
        ///
        /// El que lleva el estado es el lsx, y el esquema lo deja claro:
        ///
        /// <code>
        ///   enum lsg { 0, 1, 2, 3 }                          las cuatro modalidades
        ///   message lsm { lsg gcxp = 1; }                    apuntarse: la modalidad y ya
        ///   message lsx { bool gcyt = 1; … lsg gcyw = 4; }   ¿buscando?, y en cuál
        /// </code>
        ///
        /// Y la captura lo confirma byte a byte: el lsx que el servidor empuja a los 27 segundos
        /// de entrar, sin que el cliente pida nada, es «08012001» — f1 cierto, f4 uno. O sea
        /// «estás buscando, en la modalidad 1», que es el dos contra dos. Ese jugador ya estaba
        /// apuntado de antes, y por eso en la captura no sale el apuntarse por ningún lado: pasó
        /// antes de empezar a grabar. Buscar el lsm en las 37 carpetas de capturas no lo encuentra
        /// ni una vez.
        ///
        /// La modalidad cero no viaja, como en todo lo demás de aquí.
        /// </remarks>
        public static byte[] BuildQueueState(int modeIndex, bool searching)
            => Pb.New().VarIfNotZero(1, searching ? 1 : 0).VarIfNotZero(4, modeIndex).Build();

        /// <summary>El lsh: el cartel de partida encontrada, con el plazo en segundos.</summary>
        public static byte[] BuildOffer(int seconds) => Pb.New().VarIfNotZero(2, seconds).Build();

        /// <summary>El lth: el acuse de la respuesta. El campo 2 es un booleano.</summary>
        public static byte[] BuildAccepted(bool accepted)
            => Pb.New().VarIfNotZero(2, accepted ? 1 : 0).Build();

        /// <summary>
        /// El lsx de salir de la cola: «18032002» de la captura del 3 contra 3.
        /// </summary>
        /// <remarks>
        /// Llevaba la modalidad clavada a uno, que es la de la otra captura. Es el f4, igual que en
        /// el lsx de estar buscando, y la del 3 contra 3 lo enseña con un dos.
        /// </remarks>
        public static byte[] BuildLeftQueue(int modeIndex)
            => Pb.New().Var(3, 3).VarIfNotZero(4, modeIndex).Build();

        /// <summary>
        /// El lqn del castigo: «prohibido participar», con la marca de tiempo en que se levanta.
        /// </summary>
        /// <remarks>
        /// «080110f703220a31373838323136393936» de la captura: f1 = 1, f2 = 503 —la plantilla del
        /// cliente— y el f4 la marca de tiempo en segundos, COMO CADENA. Es la misma forma de
        /// mensaje informativo que ya se usa en todo el emulador.
        /// </remarks>
        public static byte[] BuildSanction(long epochSeconds)
            => Pb.New().Var(1, 1).Var(2, 503)
                       .Str(4, epochSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .Build();

        /// <summary>El lqn de «todavia no puedes», con los minutos que faltan.</summary>
        /// <remarks>«0801108205220134»: f1 = 1, f2 = 642, f4 = «4».</remarks>
        public static byte[] BuildStillBanned(int minutes)
            => Pb.New().Var(1, 1).Var(2, 642)
                       .Str(4, minutes.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .Build();

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

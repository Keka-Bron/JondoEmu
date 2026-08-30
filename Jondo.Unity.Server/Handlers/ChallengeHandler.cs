using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;
using Jondo.Unity.World.Fights;
using static Jondo.Protocol.NetworkMessage;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Los retos del combate, fase de preparación: ofrecerlos y dejar que el jugador elija.
    ///
    /// ─── El guión, medido sobre 305 capturas ────────────────────────────────────────────────
    ///
    /// Con la línea de tiempo de los DOS sentidos junta, que es lo que costó: las capturas se
    /// leen por conexión, y sin volver a mezclar los dos lados por hora no se ve quién contesta
    /// a quién y todo parece llegar suelto.
    ///
    ///   kxa   S→C  cuántos hay que elegir. Llega DOS VECES, con el mismo número: una al entrar
    ///              en la preparación y otra detrás de las casillas
    ///   kwo   C→S  ajuste del panel          →  kwn  S→C  con el mismo valor
    ///   kwr   C→S  abrir el selector (vacío) →  kwx  S→C  LA LISTA, siempre dos candidatos
    ///   kwv   C→S  marcar uno
    ///   kwi   C→S  pasar el ratón. Sin respuesta
    ///   kwj   C→S  validar                   →  kww  S→C  el reto queda FIJADO
    ///   kaq   C→S  listo                     →  kah, y ahí van los kww que falten
    ///   kai   S→C  se acabó la colocación
    ///   kwu   S→C  la lista definitiva, pegada al jyy
    ///
    /// ─── Dos trampas que costaron entender la traza ─────────────────────────────────────────
    ///
    /// La primera: el PRIMER <c>kwv</c> no es un clic. Llega solo, entre dos y treinta
    /// milisegundos detrás de la lista, y siempre con el id del primer candidato: es el cliente
    /// marcando uno por su cuenta. Nueve de nueve veces. Si se tomara por una elección del
    /// jugador, el reto quedaría fijado sin que nadie lo hubiera tocado.
    ///
    /// La segunda: los dos candidatos son ALTERNATIVAS, no una pareja compatible. En las capturas
    /// se ofrecieron juntos dos retos que la propia tabla del cliente marca como incompatibles.
    /// La incompatibilidad manda entre los ya FIJADOS, no entre los que están sobre la mesa.
    ///
    /// ─── Lo que aquí no está ────────────────────────────────────────────────────────────────
    ///
    /// Comprobar durante el combate si el reto se cumple, y aplicar el porcentaje al ganar. Esto
    /// es sólo la preparación: los retos se eligen, se fijan y viajan, pero todavía no vigilan
    /// nada. El mensaje del resultado es el <c>kwl</c> y está medido —{ f1: cuál, f2: cumplido },
    /// y sin el f2 está fallado—, pero nadie lo emite aún.
    /// </summary>
    public static class ChallengeHandler
    {
        /// <summary>Cuántos se eligen en un combate normal. En mazmorra son dos.</summary>
        private const int NormalCount = 1;

        private static readonly Random _dado = new Random();

        /// <summary>
        /// El nivel del grupo, que es lo que decide si un reto se puede ofrecer: la suma de los
        /// niveles de los monstruos. Con eso se explica por qué contra un poutch no sale ninguno,
        /// que es lo que pasa en las cuatro capturas de poutch: ni un kxa, ni un kwx.
        /// </summary>
        private static int GroupLevel(FightInstance fight)
        {
            int total = 0;
            foreach (var bicho in fight.Team1) total += bicho.Level;
            return total;
        }

        /// <summary>¿Hay retos que ofrecer en este combate?</summary>
        public static bool Any(FightInstance fight)
            => Challenges.Pair(GroupLevel(fight), NoneFixed, _dado).Count > 0;

        private static readonly int[] NoneFixed = Array.Empty<int>();

        /// <summary>
        /// Cuántos hay que elegir (kxa). El servidor real lo manda DOS veces con el mismo número,
        /// así que esto se llama dos veces desde la preparación.
        /// </summary>
        public static async Task SendCountAsync(NetworkStream stream, FightInstance fight,
                                                bool primeraVez = false)
        {
            if (!Any(fight)) return;

            fight.ChallengesToPick = NormalCount;
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kxa,
                Network.FightProtocol.BuildChallengeCount(fight.ChallengesToPick)));

            // Y pegado al PRIMER kxa, un kwk vacío. Sale seis veces en las capturas, siempre sin
            // carga y siempre en este mismo hueco, entre el primer kxa y el primer jxg. No se
            // sabe qué dice —va vacío, no hay nada que leer—, pero es lo único que el servidor
            // real manda ahí y que aquí faltaba.
            if (primeraVez) await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kwk));
        }

        /// <summary>
        /// Mandar la pareja de candidatos (kwx).
        ///
        /// La pareja se SORTEA UNA VEZ y se guarda. Antes se volvía a sortear en cada llamada, así
        /// que si el jugador abría el selector con dos retos ya en pantalla, se los cambiaba por
        /// otros dos delante de sus narices. El servidor real conserva el que no se ha elegido.
        /// </summary>
        public static async Task OpenAsync(NetworkStream stream, FightInstance fight)
        {
            if (!fight.ChallengesPending) return;

            // Si ya hay una pareja sobre la mesa, se vuelve a mandar la misma.
            var lista = new List<byte[]>();
            var nombres = new List<Challenges.Challenge>();

            if (fight.ChallengesOffered.Count > 0)
            {
                foreach (int id in fight.ChallengesOffered)
                {
                    var reto = Challenges.Get(id);
                    if (reto == null) continue;
                    nombres.Add(reto);
                    lista.Add(Network.FightProtocol.BuildChallenge(reto.Id, reto.Percent));
                }
            }
            else
            {
                var pareja = Challenges.Pair(GroupLevel(fight), FixedIds(fight), _dado);
                if (pareja.Count == 0) return;

                fight.ChallengeMarked = 0;
                foreach (var reto in pareja)
                {
                    fight.ChallengesOffered.Add(reto.Id);
                    nombres.Add(reto);
                    lista.Add(Network.FightProtocol.BuildChallenge(reto.Id, reto.Percent));
                }
            }

            if (lista.Count == 0) return;

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kwx,
                Network.FightProtocol.BuildChallengeList(lista)));

            Console.WriteLine($"[Retos] Se ofrecen {Names(nombres)} en el combate #{fight.FightId}.");
        }

        /// <summary>
        /// El jugador marca un candidato (kwv). No se contesta nada: en las capturas el servidor
        /// se queda callado. Sólo se apunta cuál, para saber qué fijar cuando valide.
        /// </summary>
        public static void Mark(FightInstance fight, byte[] payload)
        {
            byte[]? kwv = ConnectionProtocol.ReadPayload(payload, Op.Kwv);
            if (kwv == null) return;

            int id = (int)VarField(kwv, 1);
            if (id != 0 && fight.ChallengesOffered.Contains(id)) fight.ChallengeMarked = id;
        }

        /// <summary>
        /// El jugador valida (kwj): el reto queda fijado y se le contesta con el kww. Si todavía
        /// quedan retos por elegir, detrás va otra lista con la pareja siguiente, que es
        /// exactamente lo que hace el servidor real en la mazmorra.
        /// </summary>
        public static async Task ValidateAsync(NetworkStream stream, FightInstance fight, byte[] payload)
        {
            byte[]? kwj = ConnectionProtocol.ReadPayload(payload, Op.Kwj);
            if (kwj == null) return;

            int id = (int)VarField(kwj, 1);
            if (id == 0) id = fight.ChallengeMarked;
            if (id == 0 || !fight.ChallengesOffered.Contains(id)) return;

            await FixAsync(stream, fight, id);

            if (fight.ChallengesPending) await OpenAsync(stream, fight);
        }

        /// <summary>
        /// El ajuste del panel (kwo): se devuelve tal cual en un kwn, y DETRÁS va la lista.
        ///
        /// El orden importa y está medido: en las doce veces que el servidor real manda la lista
        /// de candidatos, las doce van DESPUÉS del kwo del cliente —incluidas las dos que llegan
        /// sin que nadie las pida, en la captura de entrada automática—. El emulador la mandaba
        /// al final de la preparación, antes de que el cliente hubiera dicho nada de su panel.
        /// </summary>
        public static async Task SettingsAsync(NetworkStream stream, FightInstance? fight, byte[] payload)
        {
            byte[]? kwo = ConnectionProtocol.ReadPayload(payload, Op.Kwo);
            if (kwo == null) return;

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kwn,
                Network.FightProtocol.BuildChallengeSettings(VarField(kwo, 1))));

            if (fight != null) await OpenAsync(stream, fight);
        }

        /// <summary>
        /// El jugador ha pulsado listo y quedan retos sin elegir: el servidor los rellena él
        /// solo. Está medido en la anomalía, donde el jugador eligió uno de los dos y el servidor
        /// mandó el que faltaba sin haberlo ofrecido nunca.
        ///
        /// Va ANTES del kai, que es el corte entre la colocación y el combate.
        /// </summary>
        public static async Task FillAsync(NetworkStream stream, FightInstance fight)
        {
            // Lo que estuviera marcado sin validar cuenta: el jugador lo eligió, sólo que no llegó
            // a pulsar el botón antes de declararse listo.
            if (fight.ChallengeMarked != 0 && fight.ChallengesPending)
            {
                await FixAsync(stream, fight, fight.ChallengeMarked);
            }

            while (fight.ChallengesPending)
            {
                var pareja = Challenges.Pair(GroupLevel(fight), FixedIds(fight), _dado);
                if (pareja.Count == 0) break;
                await FixAsync(stream, fight, pareja[0].Id);
            }

            await ImposeAsync(stream, fight);
        }

        /// <summary>
        /// Y detrás, los que PONE el contenido, que son otra cosa.
        ///
        /// En una mazmorra o una anomalía no salen sólo los dos retos normales: van además de uno
        /// a tres retos propios de ese sitio, los que llevan logro detrás. No se proponen ni se
        /// eligen —el jugador no los ve en el selector— y llegan con el extra a CERO.
        ///
        /// Está medido en la anomalía: detrás de los dos normales llegaron tres kww más, 772,
        /// 773 y 774, que no se habían ofrecido nunca, los tres sin porcentaje, y los tres
        /// exigiendo el monstruo 5781, que era el de esa anomalía.
        ///
        /// Como llevan logro, se hacen una vez: al personaje que ya los tenga cumplidos no se le
        /// vuelven a poner. Hoy esa lista está siempre vacía porque todavía nadie comprueba si un
        /// reto se cumple, así que a efectos prácticos salen siempre; el día que se implante la
        /// comprobación, esto ya está en su sitio.
        /// </summary>
        private static async Task ImposeAsync(NetworkStream stream, FightInstance fight)
        {
            // Sólo en la sala del jefe. Estos son los retos con logro detrás, el premio de haber
            // hecho la mazmorra entera, y salían en cada sala porque lo único que se miraba era
            // si el combate tenía monstruos con reto. Salir en la cuarta de cinco además engaña:
            // el jugador los lee como «esta es la última» y deja de avanzar.
            //
            // Un combate fuera de mazmorra no entra aquí -- IsBossRoom contesta que no cuando el
            // mapa no es sala de ninguna --, que es lo que ya pasaba antes por otro camino.
            if (!DungeonHandler.IsBossRoom(SessionContext.State.MapId)) return;

            var bichos = new List<int>();
            foreach (var uno in fight.Team1)
            {
                if (uno.IsMonster && uno.MonsterId != 0) bichos.Add(uno.MonsterId);
            }
            if (bichos.Count == 0) return;

            var cumplidos = DatabaseManager.LoadChallengesDone(GameState.CharacterId);
            var puestos = Challenges.Imposed(bichos, cumplidos);
            if (puestos.Count == 0) return;

            foreach (var reto in puestos)
            {
                fight.ChallengesFixed.Add((reto.Id, 0));
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kww,
                    Network.FightProtocol.BuildChallengeChosen(
                        Network.FightProtocol.BuildChallenge(reto.Id, 0))));
            }

            Console.WriteLine($"[Retos] El sitio impone {puestos.Count} reto(s) más en el " +
                              $"combate #{fight.FightId}.");
        }

        /// <summary>La lista definitiva (kwu). Va entre el kai y el jyy.</summary>
        public static async Task SendFinalListAsync(NetworkStream stream, FightInstance fight)
        {
            if (fight.ChallengesFixed.Count == 0) return;

            var lista = new List<byte[]>();
            foreach (var (id, percent) in fight.ChallengesFixed)
            {
                lista.Add(Network.FightProtocol.BuildChallenge(id, percent));
            }

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kwu,
                Network.FightProtocol.BuildChallengeFinalList(lista)));

            Console.WriteLine($"[Retos] El combate #{fight.FightId} se pelea con " +
                              $"{Fixed(fight)}.");
        }

        // ─── Piezas ─────────────────────────────────────────────────────────────

        private static async Task FixAsync(NetworkStream stream, FightInstance fight, int id)
        {
            var reto = Challenges.Get(id);
            if (reto == null) return;

            fight.ChallengesFixed.Add((id, reto.Percent));
            fight.ChallengesOffered.Clear();
            fight.ChallengeMarked = 0;

            byte[] ldd = Network.FightProtocol.BuildChallenge(id, reto.Percent);
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kww,
                Network.FightProtocol.BuildChallengeChosen(ldd)));
        }

        private static int[] FixedIds(FightInstance fight)
        {
            var ids = new int[fight.ChallengesFixed.Count];
            for (int i = 0; i < ids.Length; i++) ids[i] = fight.ChallengesFixed[i].Id;
            return ids;
        }

        private static string Names(IReadOnlyList<Challenges.Challenge> retos)
        {
            var trozos = new List<string>();
            foreach (var reto in retos) trozos.Add($"«{reto.Name}» al {reto.Percent} %");
            return string.Join(" o ", trozos);
        }

        private static string Fixed(FightInstance fight)
        {
            var trozos = new List<string>();
            foreach (var (id, percent) in fight.ChallengesFixed)
            {
                trozos.Add($"«{Challenges.Get(id)?.Name ?? id.ToString()}» al {percent} %");
            }
            return string.Join(" y ", trozos);
        }

        private static long VarField(byte[] payload, int number)
        {
            foreach (var field in ProtoMessage.Parse(payload).Fields)
            {
                if (field.FieldNumber == number && field.WireType == 0) return field.VarIntValue;
            }
            return 0;
        }
    }
}

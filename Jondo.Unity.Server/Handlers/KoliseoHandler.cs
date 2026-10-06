using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The koliseo: for now, which modes there are and which are open.
    /// </summary>
    /// <remarks>
    /// Measured in «koliseo completo con invitacion-koli 2vs2». The client asks for the table with an
    /// empty <c>lux</c> and the server answers with an <c>ltd</c> of four entries:
    ///
    /// <code>
    ///   f1{      f2{f1=1 f4=1} f3=1 }    1 against 1, open
    ///   f1{f1=1  f2{f1=1 f4=2} f3=1 }    2 against 2, open
    ///   f1{f1=2  f2{f1=1 f4=3} f3=1 }    3 against 3, open
    ///   f1{f1=3  f2{     f4=3}      }    another of three, without f3: closed
    /// </code>
    ///
    /// <c>f4</c> is how many go per team and <c>f3</c> is the switch. The first three are replicated
    /// as they are, which is what opening the three modes required; the fourth is sent as closed as
    /// in the capture, because it is not known what it is and switching it on would be making it up.
    ///
    /// <b>Enrolling.</b> Ordering the two halves of the connection by timestamp, which is what was
    /// needed to read this right, the whole exchange is:
    ///
    /// <code>
    ///   109.6 s  C-&gt;S  luy { f2 = mode index }              «1001», and the capture is a 2 against 2
    ///   109.7 s  S-&gt;C  lth { f2 = the same index }          38 ms later
    ///        ... seven seconds of waiting ...
    ///   116.9 s  S-&gt;C  ilw                                  the party, with the teammate's name
    ///   116.9 s  S-&gt;C  lst { host, ip, ticket }             TO ANOTHER SERVER
    ///   446.0 s  C-&gt;S  lte                                  back, the fight already over
    ///   446.1 s  S-&gt;C  lty, lsr, lsx                        within 80 ms
    /// </code>
    ///
    /// <b>What this changes.</b> The real koliseo does NOT fight on the world server: the
    /// <c>lst</c> sends the client to «dofus2-ko-tynril.ankama-games.com» with a 32-byte ticket, the
    /// client opens a second connection and the whole fight -- kam, kaa, the four jxg, the payout --
    /// travels through there. Jondo is a single server and sets up the fight in the same place. It
    /// is a difference of architecture and it is stated, not hidden.
    ///
    /// <b>What is still not done.</b> The <c>ilw</c> of the formed party, the invitation between
    /// teammates (<c>ijz</c>, <c>ilo</c>, <c>ing</c>, <c>iki</c>, <c>ijx</c>), the rankings
    /// (<c>iqt</c> and <c>irc</c>, two lists of more than three thousand bytes) and the payout of
    /// kolichas. And matchmaking here is by order of arrival, not by rating: see
    /// <see cref="KoliseoQueue"/>.
    /// </remarks>
    public static class KoliseoHandler
    {
        /// <summary>A mode: how many per team and whether it is open.</summary>
        public readonly record struct Mode(int Index, int TeamSize, bool Open, bool Inner);

        /// <summary>
        /// The capture's four, with the three real ones open.
        /// </summary>
        /// <remarks>
        /// The fourth carries <c>Inner = false</c> because its <c>f2</c> does not carry the <c>f1</c> the
        /// other three carry. It is a one-byte difference and it is respected: replicating what was
        /// measured costs the same as approximating it.
        /// </remarks>
        public static readonly IReadOnlyList<Mode> Modes = new[]
        {
            new Mode(0, 1, true, true),
            new Mode(1, 2, true, true),
            new Mode(2, 3, true, true),
            new Mode(JondoBotMode, 1, true, false),
        };

        /// <summary>
        /// The fourth card of the Koliseo window: 1v1 against a JondoBot (<see cref="KoliseoBots"/>).
        /// </summary>
        /// <remarks>
        /// The client's window has a fourth card besides 1v1, 2v2 and 3v3, its "event" one
        /// (ctr_pvpEventLeagueInfo, UpdateEventMode): the entry of the ltd whose settings are
        /// not the default ones (no f1 in its lsz). It shows when that mode is open and its
        /// texts are the client's own. Measured closed, as a 3v3, in the capture; here it is open,
        /// a 1v1, and every enrolment in it is a fight against a JondoBot at once -- the 1v1's own
        /// "searching", its match-found popup, its accept, its sanction for letting it run out.
        /// It pays as a Koliseo and leaves the ladder alone.
        /// </remarks>
        public const int JondoBotMode = 3;

        /// <summary>The client asks for the table (lux). It is answered with the ltd.</summary>
        /// <remarks>
        /// It goes through root 3 and with the request's id, not through root 1. It was wrong: it was sent
        /// with Push, which wraps in root 1 -- «the server says this on its own» -- and the client had
        /// nothing to pair it with. In the capture the five pairs match one by one:
        ///
        /// <code>
        ///   C-&gt;S  12 19 {…lux…} 10 0e        the 14 goes in the root's f2
        ///   S-&gt;C  1a 45 {…ltd…} 10 0e        and the same 14 comes back
        /// </code>
        ///
        /// And they go on: 15, 16, 17 and 18. It is a counter of the client, not the usual -1.
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

        /// <summary>A whole PARTY enrols (lsm).</summary>
        /// <remarks>
        /// The same button as <see cref="EnrolAsync"/>, but with people behind it. With a party formed
        /// the client stops sending the luy and sends the lsm, and the index moves from field 2 to 1:
        /// measured on our own client, «0801» when pressing a 2 against 2, which is entry 1 of the ltd --
        /// the same index the luy carries in the capture.
        ///
        /// THE WHOLE PARTY is enrolled and not only whoever presses, which is what enrolling as a party
        /// means; those already in a queue stay where they were. The party is the normal one, that of
        /// <see cref="Parties"/>: the koliseo team is set up by the koliseo after matchmaking, and in the
        /// capture the team's ilw appears right there, not before.
        ///
        /// NOT MEASURED: what the real server answers to an lsm. There is no capture of the party road.
        /// It is sent the lth, which is the acknowledgement that puts the window in its waiting state and
        /// what answers the luy within 38 ms.
        /// </remarks>
        public static async Task EnrolPartyAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? lsm = ConnectionProtocol.ReadPayload(payload, Op.Lsm);
            if (lsm == null) return;
            // A Koliseo fight is a trip to an arena: none for a prisoner.
            if (await Managers.Jail.KeepsInAsync(stream)) return;

            int indice = IndiceDeModalidad(lsm, 1);

            var modo = FindMode(indice);
            if (modo == null || !modo.Value.Open)
            {
                Console.WriteLine($"[Koliseo] Se apunta un grupo a la modalidad {indice}, que no está abierta.");
                return;
            }

            long yo = GameState.CharacterId;

            // The penalty for letting a poster expire. The real server answers with lqn 642 and
            // the minutes left, and does not enrol you.
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

            // The JondoBot card: each of them against a JondoBot of his own, now.
            if (indice == JondoBotMode)
            {
                foreach (long miembro in quienes)
                {
                    var suya = SessionRegistry.FindByCharacter(miembro);
                    if (suya == null || !suya.IsInWorld || KoliseoOffers.Of(miembro) != null) continue;
                    if (suya.State.IsInFight || KoliseoQueue.Waits(miembro)) continue;
                    await StartJondoBotAsync(suya);
                }
                return;
            }

            // A party that fits one side waits, and fights, as one unit; one that does not (three
            // enrolling for 1v1) goes in one by one.
            int nuevos = 0;
            if (quienes.Count <= modo.Value.TeamSize) nuevos = KoliseoQueue.EnrolUnit(quienes, indice);
            else foreach (long miembro in quienes) if (KoliseoQueue.Enrol(miembro, indice)) nuevos++;

            // It ALWAYS goes, even if nobody new enrolled: without this the window stays as if
            // nothing had happened, which is exactly the bug that brings us here.
            // THE QUEUE STATE, which is what draws the «searching». It is not an acknowledgement
            // of the request: it is a push from the server with how the player stands right now.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lsx, BuildQueueState(indice, true)));

            Console.WriteLine($"[Koliseo] Grupo de {quienes.Count} en la cola de " +
                              $"{modo.Value.TeamSize} contra {modo.Value.TeamSize} " +
                              $"({nuevos} nuevo(s)): {KoliseoQueue.CountIn(indice)} esperando.");

            await TryMatchAsync(indice, modo.Value.TeamSize);
        }

        /// <summary>The player accepts or refuses the match (luy).</summary>
        /// <remarks>
        /// The luy is <c>{ map&lt;string,string&gt;, bool }</c> read off the client itself: field 2 is a
        /// BOOLEAN, not a mode index. Accepting arrives as «1001» and the real server answers with an
        /// identical lth through root 3 with the request's id, within 38 ms.
        ///
        /// The refusal is NOT MEASURED: in the capture the deadline was left to run out. A proto3 bool
        /// set to false does not travel, so a «no» would have to arrive with an empty payload, and that
        /// is how it is treated. The pvp challenge does exactly the same -- accept «08ec031001», refuse
        /// «08e903» --.
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

            // The acknowledgement always goes, yes or no: it is the answer to HIS request.
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

        /// <summary>Waits for the deadline and, if not everybody has said yes, undoes it.</summary>
        private static async Task VencerAsync(KoliseoOffers.Offer oferta)
        {
            await Task.Delay(TimeSpan.FromSeconds(KoliseoOffers.Segundos + 1));

            // If somebody accepted it in full by a hair, Close returns false and it is not touched here.
            if (!KoliseoOffers.Close(oferta)) return;

            Console.WriteLine($"[Koliseo] Vence el plazo de la partida {oferta.Id}.");
            await DeshacerAsync(oferta, KoliseoOffers.WhoDidNotAnswer(oferta), yaCerrada: true);
        }

        /// <summary>
        /// Undoes a match: punishes whoever did not say yes and sends the others back to the queue.
        /// </summary>
        /// <remarks>
        /// Measured when the deadline runs out: lqn with the notice and the timestamp, an empty ltk, and
        /// the lsx saying the search is over. The rating's lty also travels there and is NOT sent: it is
        /// 144 bytes with a floating-point block inside that has not been deciphered, and sending made-up
        /// bytes is worse than not sending them.
        /// </remarks>
        private static async Task DeshacerAsync(KoliseoOffers.Offer oferta, List<long> culpables,
                                                bool yaCerrada = false)
        {
            if (!yaCerrada && !KoliseoOffers.Close(oferta)) return;

            var castigados = new HashSet<long>(culpables);
            var hasta = DateTime.UtcNow.AddMinutes(KoliseoOffers.Castigo);

            foreach (long quien in oferta.Everybody)
            {
                if (KoliseoBots.IsBot(quien))
                {
                    KoliseoBots.Forget(quien);
                    continue;
                }
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
                else if (oferta.Mode != JondoBotMode)
                {
                    // Whoever did say yes does not lose his place because of somebody else.
                    KoliseoQueue.Enrol(quien, oferta.Mode);
                }

                if (sesion == null) continue;
                await Escribir(sesion, ConnectionProtocol.Push(Op.Ltk));
                await Escribir(sesion, ConnectionProtocol.Push(Op.Lsx, BuildLeftQueue(oferta.Mode)));
            }

            Console.WriteLine($"[Koliseo] Partida deshecha: {castigados.Count} castigado(s) " +
                              $"{KoliseoOffers.Castigo} minuto(s).");

            // Those who stayed can be matched with others who were waiting.
            var modo = FindMode(oferta.Mode);
            if (modo != null && oferta.Mode != JondoBotMode) await TryMatchAsync(oferta.Mode, modo.Value.TeamSize);
        }

        /// <summary>Everybody has said yes: the fight is set up.</summary>
        private static async Task EmpezarAsync(KoliseoOffers.Offer oferta)
        {
            var azul = new List<GameSession>();
            var rojo = new List<GameSession>();
            var azulBots = new List<Fighter>();
            var rojoBots = new List<Fighter>();

            foreach (long id in oferta.Blue) Juntar(id, azul, azulBots);
            foreach (long id in oferta.Red) Juntar(id, rojo, rojoBots);

            if (azul.Count + azulBots.Count != oferta.TeamSize || rojo.Count + rojoBots.Count != oferta.TeamSize)
            {
                Console.WriteLine("[Koliseo] Alguien se fue entre aceptar y empezar; se deshace.");
                await DeshacerAsync(oferta, new List<long>(), yaCerrada: true);
                return;
            }

            Console.WriteLine($"[Koliseo] Todos aceptan: partida de {oferta.TeamSize} contra " +
                              $"{oferta.TeamSize}.");
            long mapa = azul.Count > 0 ? azul[0].MapId : rojo[0].MapId;
            await FightHandler.InitiatePvpAsync(azul, rojo, mapa, koliseo: true, koliseoMode: oferta.Mode,
                                                blueBots: azulBots, redBots: rojoBots);
        }

        /// <summary>One of an offer's fighters: his session, or the JondoBot built for the fight.</summary>
        private static void Juntar(long id, List<GameSession> sesiones, List<Fighter> bots)
        {
            if (KoliseoBots.IsBot(id))
            {
                var spec = KoliseoBots.Of(id);
                if (spec != null) bots.Add(KoliseoBots.BuildFighter(spec));
                KoliseoBots.Forget(id);
                return;
            }
            var sesion = SessionRegistry.FindByCharacter(id);
            if (sesion != null && sesion.IsInWorld) sesiones.Add(sesion);
        }

        /// <summary>Writes to a session without a dropped socket taking the others down with it.</summary>
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

        /// <summary>The client comes back from the koliseo (lte).</summary>
        /// <remarks>
        /// It is not leaving the queue, although it might look like it: in the capture the luy and the
        /// lte are five and a half minutes apart, with the whole fight in between. What is answered is
        /// three frames within 80 ms -- lty, lsr and lsx --; only the last goes here, which is the only one
        /// of the three whose four bytes can be repeated without pretending to understand them. The lty is
        /// 151 undeciphered bytes and sending 151 made-up bytes is worse than not sending them.
        ///
        /// Just in case, his place in the queue is taken away too: coming back from the koliseo and still
        /// being enrolled would make no sense, and if he was not, it costs nothing.
        /// </remarks>
        public static async Task ReturnAsync(NetworkStream stream, byte[]? payload = null)
        {
            KoliseoQueue.Leave(GameState.CharacterId);

            // The league as the fight left it (lty, pushed), then the empty lsr that answers the
            // lte (root 3, with its request id), as the capture has them.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lty, BuildRanks(GameState.CharacterId, GameState.CharacterLevel)));
            if (payload != null)
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Answer(Op.Lsr, null, ConnectionProtocol.RequestId(payload)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lsx,
                    BuildLeftQueue(KoliseoOffers.LastMode(GameState.CharacterId))));

            Console.WriteLine($"[Koliseo] {GameState.CharacterId} vuelve del koliseo.");
        }

        /// <summary>
        /// The window's "leave the queue" (lsi): out of the queue with the unit he enrolled with --
        /// a party leaves together -- and each of them told with the lsx of leaving, which puts
        /// the window back to "search a fight" (see <see cref="Op.Lsi"/>).
        /// </summary>
        /// <remarks>
        /// On the JondoBot card the search is the offer itself, drawn at once: leaving withdraws it,
        /// with no sanction, since nothing was refused. A normal mode's offer is answered from its
        /// popup, not from here.
        /// </remarks>
        public static async Task LeaveQueueAsync(NetworkStream stream)
        {
            long yo = GameState.CharacterId;

            var oferta = KoliseoOffers.Of(yo);
            if (oferta != null && oferta.Mode == JondoBotMode)
            {
                Console.WriteLine($"[Koliseo] {yo} deja la tarjeta de JondoBots antes de aceptar.");
                await DeshacerAsync(oferta, new List<long>());
                return;
            }

            var (mode, members) = KoliseoQueue.LeaveWithUnit(yo);
            if (mode < 0)
            {
                // Not waiting anywhere: the window is set straight all the same, so that it does
                // not stay "searching" for a search the server does not have.
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lsx, BuildLeftQueue(KoliseoOffers.LastMode(yo))));
                Console.WriteLine($"[Koliseo] {yo} deja una cola en la que no estaba.");
                return;
            }

            byte[] left = ConnectionProtocol.Push(Op.Lsx, BuildLeftQueue(mode));
            foreach (long id in members)
            {
                var sesion = SessionRegistry.FindByCharacter(id);
                if (sesion != null) await Escribir(sesion, left);
            }
            Console.WriteLine($"[Koliseo] {yo} deja la cola del modo {mode}" +
                              (members.Count > 1 ? $" con su grupo ({members.Count})." : ".") +
                              $" Quedan {KoliseoQueue.CountIn(mode)} esperando.");
        }

        /// <summary>
        /// The mode index an enrolment request carries.
        /// </summary>
        /// <remarks>
        /// ZERO WHEN IT DOES NOT COME, and that was the bug. The field does not travel when it is zero --
        /// it is protobuf's default value, and our own Op.cs says so about this same ltd: «index zero
        /// does not travel» -- so a one against one arrives with an empty payload. Starting at minus one,
        /// that empty payload was read as «mode -1», fell into «it is not open», and the client was left
        /// waiting for an acknowledgement that never came without a single notice anywhere. Two against
        /// two worked because its index is one and it does travel.
        ///
        /// The field number changes depending on where it comes in -- the luy carries it in 2 and the lsm
        /// in 1 -- but the numbering is the same in both, and it is the order of the ltd's entries:
        /// 0 one against one, 1 two against two, 2 three against three.
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
        /// If there are already people for both teams, sets up the fight.
        /// </summary>
        /// <remarks>
        /// It is checked that they are all still connected BEFORE taking them out of the queue for good:
        /// a disconnection fits between enrolling and filling the match, and setting up a koliseo with an
        /// empty slot is worse than waiting for the next one.
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
                // Somebody left along the way. Those remaining go back to the queue instead of losing
                // their place because of somebody else.
                foreach (var sesion in azul) KoliseoQueue.Enrol(sesion.State.CharacterId, mode);
                foreach (var sesion in rojo) KoliseoQueue.Enrol(sesion.State.CharacterId, mode);
                Console.WriteLine($"[Koliseo] Faltó alguien al formar la partida; los demás " +
                                  $"vuelven a la cola.");
                return;
            }

            // And THE FIGHT DOES NOT START HERE, the poster does. The real server sends an lsh
            // with the deadline and waits; measured in two captures, and the deadline is 59 seconds.
            var oferta = KoliseoOffers.Open(mode, teamSize, pareja.Value.Blue, pareja.Value.Red);

            byte[] aviso = ConnectionProtocol.Push(Op.Lsh, BuildOffer(KoliseoOffers.Segundos));
            foreach (var sesion in azul) await Escribir(sesion, aviso);
            foreach (var sesion in rojo) await Escribir(sesion, aviso);

            Console.WriteLine($"[Koliseo] Partida de {teamSize} contra {teamSize} encontrada: " +
                              $"{KoliseoOffers.Segundos} s para aceptarla.");

            _ = VencerAsync(oferta);
        }

        /// <summary>
        /// Every few seconds: the queues looked at again, since the rating window widens with the
        /// wait and a match impossible when someone enrolled may be possible now.
        /// </summary>
        public static async Task TickAsync()
        {
            foreach (var modo in Modes)
            {
                if (!modo.Open || modo.Index == JondoBotMode || KoliseoQueue.CountIn(modo.Index) < modo.TeamSize * 2) continue;
                int before;
                do
                {
                    before = KoliseoQueue.CountIn(modo.Index);
                    await TryMatchAsync(modo.Index, modo.TeamSize);
                } while (KoliseoQueue.CountIn(modo.Index) < before && KoliseoQueue.CountIn(modo.Index) >= modo.TeamSize * 2);
            }
        }

        // ─── The ladder ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The lty: the season's start and a character's standing in every mode, what the Koliseo
        /// window draws its leagues and victories from.
        /// </summary>
        /// <remarks>
        /// Measured in the world entry of every capture and after the 2v2 of "koliseo completo";
        /// the field meanings read off the client (its frame fff builds one oa per ltw, which the
        /// window's UpdateUILeague and UpdateUIVictories draw):
        ///
        /// <code>
        ///   lty { f2: season start, "2025-03-14T20:46:31.598Z"
        ///         f3 (repeated) ltw { f1: mode, f3: league (-1 none), f4: placement fights left,
        ///                             f5: season wins, f6: season fights, f7: day wins,
        ///                             f8 { f1: best league of the season (-1 none) },
        ///                             f10: day fights } }
        /// </code>
        ///
        /// The recorder of "koliseo completo" LOST the 2v2 (the kolichas of its jyg went to the
        /// other side), and the lty that follows says f6 = 1, f10 = 1 and no f5 or f7: so f6 and
        /// f10 are fights and f5 and f7 wins. The f4 of that capture's f8, 6691, is not read by
        /// any of the window's code seen, and is not sent.
        /// </remarks>
        public static byte[] BuildRanks(long characterId, int level)
        {
            var season = KoliseoLadder.Current();
            var lty = Pb.New().Str(2, season.StartUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                                                               System.Globalization.CultureInfo.InvariantCulture));
            foreach (var s in KoliseoLadder.AllOf(characterId, level))
            {
                lty.Msg(3, Pb.New()
                    .VarIfNotZero(1, s.Mode)
                    .Var(3, s.League)
                    .VarIfNotZero(4, s.PlacementLeft)
                    .VarIfNotZero(5, s.SeasonWins)
                    .VarIfNotZero(6, s.SeasonFights)
                    .VarIfNotZero(7, s.DayWins)
                    .Msg(8, Pb.New().Var(1, s.BestLeague))
                    .VarIfNotZero(10, s.DayFights));
            }
            return lty.Build();
        }

        private static string IsoDate(DateTime utc)
            => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                                                                      System.Globalization.CultureInfo.InvariantCulture);

        // ─── The JondoBots ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Enrolled on the JondoBot card: a JondoBot drawn for him and the 1v1's match-found popup,
        /// the JondoBot's yes already given. What follows is the Koliseo's own: his accept starts
        /// the fight (<see cref="EmpezarAsync"/>), his no or the clock undoes it.
        /// </summary>
        private static async Task StartJondoBotAsync(GameSession human)
        {
            var bot = KoliseoBots.Create(against: human.State.CharacterId);
            var oferta = KoliseoOffers.Open(JondoBotMode, 1, new List<long> { human.State.CharacterId },
                                            new List<long> { bot.Id });
            KoliseoOffers.Accept(oferta, bot.Id);

            await Escribir(human, ConnectionProtocol.Push(Op.Lsx, BuildQueueState(JondoBotMode, true)));
            await Escribir(human, ConnectionProtocol.Push(Op.Lsh, BuildOffer(KoliseoOffers.Segundos)));
            Console.WriteLine($"[Koliseo] {human.State.CharacterId} against {bot.Name} (level {KoliseoBots.Level}): " +
                              $"{KoliseoOffers.Segundos} s to accept.");
            _ = VencerAsync(oferta);
        }

        private static Mode? FindMode(int index)
        {
            foreach (var modo in Modes) if (modo.Index == index) return modo;
            return null;
        }

        /// <summary>
        /// The lsx: which queue the player is in, which is what draws the «searching».
        /// </summary>
        /// <remarks>
        /// This was misread from the start and deserves to stay written. It used to be answered with an
        /// lth with the index inside, and the lth is not that: the client's own schema says
        /// <c>lth { bool gdak = 1; bool gdal = 2; }</c>, two booleans, and it is the answer to a luy --
        /// <c>{ map&lt;string,string&gt;, bool }</c> --, which is not enrolling in anything. The window
        /// received something it did not understand and stayed as it was, without a single error.
        ///
        /// The one carrying the state is the lsx, and the schema makes it clear:
        ///
        /// <code>
        ///   enum lsg { 0, 1, 2, 3 }                          the four modes
        ///   message lsm { lsg gcxp = 1; }                    enrolling: the mode and that is all
        ///   message lsx { bool gcyt = 1; … lsg gcyw = 4; }   searching?, and in which
        /// </code>
        ///
        /// And the capture confirms it byte for byte: the lsx the server pushes 27 seconds after entering,
        /// without the client asking for anything, is «08012001» -- f1 true, f4 one. That is «you are
        /// searching, in mode 1», which is the two against two. That player was already enrolled from
        /// before, and that is why the enrolment does not appear anywhere in the capture: it happened
        /// before recording started. Searching for the lsm in the 37 capture folders does not find it
        /// even once.
        ///
        /// Mode zero does not travel, as with everything else here.
        /// </remarks>
        public static byte[] BuildQueueState(int modeIndex, bool searching)
            => Pb.New().VarIfNotZero(1, searching ? 1 : 0).VarIfNotZero(4, modeIndex).Build();

        /// <summary>The lsh: the match-found poster, with the deadline in seconds.</summary>
        public static byte[] BuildOffer(int seconds) => Pb.New().VarIfNotZero(2, seconds).Build();

        /// <summary>The lth: the answer's acknowledgement. Field 2 is a boolean.</summary>
        public static byte[] BuildAccepted(bool accepted)
            => Pb.New().VarIfNotZero(2, accepted ? 1 : 0).Build();

        /// <summary>
        /// The lsx for leaving the queue: «18032002» from the 3 against 3 capture.
        /// </summary>
        /// <remarks>
        /// It carried the mode nailed at one, which is the other capture's. It is f4, the same as in the
        /// searching lsx, and the 3 against 3 one shows it with a two.
        /// </remarks>
        public static byte[] BuildLeftQueue(int modeIndex)
            => Pb.New().Var(3, 3).VarIfNotZero(4, modeIndex).Build();

        /// <summary>
        /// The penalty's lqn: «banned from taking part», with the timestamp at which it is lifted.
        /// </summary>
        /// <remarks>
        /// «080110f703220a31373838323136393936» from the capture: f1 = 1, f2 = 503 -- the client's
        /// template -- and f4 the timestamp in seconds, AS A STRING. It is the same informative message
        /// shape already used throughout the emulator.
        /// </remarks>
        public static byte[] BuildSanction(long epochSeconds)
            => Pb.New().Var(1, 1).Var(2, 503)
                       .Str(4, epochSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .Build();

        /// <summary>The «you cannot yet» lqn, with the minutes left.</summary>
        /// <remarks>«0801108205220134»: f1 = 1, f2 = 642, f4 = «4».</remarks>
        public static byte[] BuildStillBanned(int minutes)
            => Pb.New().Var(1, 1).Var(2, 642)
                       .Str(4, minutes.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .Build();

        /// <summary>The ltd, byte for byte as the capture.</summary>
        public static byte[] BuildModes(IReadOnlyList<Mode> modes)
        {
            var ltd = Pb.New();

            foreach (var modo in modes)
            {
                // lsz { f1: default mode, f2: start, f3: end, f4: team size }, as the client's
                // ArenaStateModeWrapper reads it. The event one runs for the season.
                var dentro = Pb.New();
                if (modo.Inner) dentro.Var(1, 1);
                if (!modo.Inner && modo.Open)
                {
                    var season = KoliseoLadder.Current();
                    dentro.Str(2, IsoDate(season.StartUtc));
                    dentro.Str(3, IsoDate(season.StartUtc + KoliseoLadder.SeasonLength));
                }
                dentro.Var(4, modo.TeamSize);

                var entrada = Pb.New();
                // Index zero does not travel: it is protobuf's default value and the capture leaves it
                // out in the first entry and only in it.
                entrada.VarIfNotZero(1, modo.Index);
                entrada.Msg(2, dentro);
                if (modo.Open) entrada.Var(3, 1);

                ltd.Msg(1, entrada);
            }

            return ltd.Build();
        }
    }
}

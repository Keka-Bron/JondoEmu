using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Managers;
using Jondo.Unity.Launcher.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>
    /// Los grupos: invitar, aceptar, rechazar, salirse y ceder el mando.
    ///
    /// ─── El baile, medido en las seis capturas ──────────────────────────────────────────────
    ///
    /// Con los dos puntos de vista, que es lo que hacía falta: en unas capturas graba quien
    /// invita y en otras el invitado, y los mensajes no son los mismos.
    ///
    ///   invitar    C→S ime { nombre }        →  S→C ing (el grupo, contigo solo) + imf
    ///   te invitan                              S→C ijz { yo, quién, plazas, grupo, nombre }
    ///   detalles   C→S imd { grupo }         →  S→C ilb   (NO implementado: el botón
    ///                                                      «Detalles» de la ventanita no
    ///                                                      contesta nada todavía)
    ///   aceptar    C→S ijx { grupo }         →  S→C ing (el grupo entero)
    ///                                           y al que invitó: ink + lqn 1663
    ///   rechazar   C→S iki { grupo }         →  a ti ilo; al que invitó iko + imy
    ///   salir      C→S inh { grupo }         →  S→C ils { grupo }
    ///   ceder      C→S ima { quién, grupo }  →  S→C imk (vacío) + ilx { quién, grupo }
    ///
    /// Dos cosas que despistan y conviene tener presentes. Se invita por NOMBRE y se acepta por
    /// ID DE GRUPO: el ime lleva «Uber-Black» en texto y el ijx lleva 71272. Y el grupo se crea
    /// AL INVITAR, antes de que el otro conteste, por eso el ing con un solo miembro llega
    /// enseguida; si el otro dice que no, se deshace solo.
    ///
    /// El cambio de jefe NO reenvía el grupo: manda un ilx de once bytes. Se comprobó comparando
    /// la ficha del mismo grupo antes y después, y lo único que cambia es su campo 4.
    ///
    /// ─── Por qué no se formaba el grupo ─────────────────────────────────────────────────────
    ///
    /// La primera versión mandaba una hoja de miembro con el nombre, el nivel y la raza y nada
    /// más. La invitación salía y el aceptar viajaba, pero en pantalla no aparecía el grupo, y
    /// además el que invitaba ya no podía invitar a nadie más: el servidor daba el grupo por
    /// hecho y el cliente no. Faltaba el ASPECTO, que es con lo que dibuja el retrato de cada
    /// miembro. Ahora la hoja va entera; ver <see cref="MemberSheet"/>.
    /// </summary>
    public static class PartyHandler
    {
        /// <summary>«<b>{0}</b> sigue tu desplazamiento», al entrar alguien.</summary>
        private const int FollowsYouMessage = 1663;

        // ─── Invitar ────────────────────────────────────────────────────────────

        public static async Task InviteAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? ime = ConnectionProtocol.ReadPayload(payload, Op.Ime);
            if (ime == null) return;

            string target = NameIn(ime);
            if (target.Length == 0) return;

            long meId = SessionContext.State.CharacterId;
            string meName = SessionContext.State.CharacterName;

            if (string.Equals(target, meName, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[Grupo] {meName} intenta invitarse a sí mismo.");
                return;
            }

            var guest = SessionRegistry.FindByName(target);
            if (guest == null)
            {
                Console.WriteLine($"[Grupo] {meName} invita a «{target}», que no está conectado.");
                return;
            }

            if (Parties.IsInParty(guest.State.CharacterId))
            {
                Console.WriteLine($"[Grupo] {guest.State.CharacterName} ya está en un grupo.");
                return;
            }

            // El grupo se crea al invitar, no al aceptar: es lo que hace el servidor real.
            var party = Parties.Of(meId);
            bool nuevo = party == null;
            party ??= Parties.Create(meId);

            if (!Parties.Invite(party, guest.State.CharacterId, meId))
            {
                Console.WriteLine($"[Grupo] No se ha podido invitar a {guest.State.CharacterName}.");
                if (nuevo) Parties.Dissolve(party);
                return;
            }

            if (nuevo)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Ing, BuildParty(party)));
            }

            // Y el invitado, en la lista de quien invita, con el letrero de que está pendiente.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Imf, BuildPending(guest.State.CharacterId, meId, party.Id)));

            await guest.SendAsync(ConnectionProtocol.Push(Op.Ijz,
                ConnectionProtocol.BuildPartyInvitation(guest.State.CharacterId, meId, meName,
                                                        party.Id, Parties.MaxMembers)));

            Console.WriteLine($"[Grupo] {meName} invita a {guest.State.CharacterName} " +
                              $"al grupo {party.Id}.");
        }

        // ─── Aceptar ────────────────────────────────────────────────────────────

        public static async Task AcceptAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? ijx = ConnectionProtocol.ReadPayload(payload, Op.Ijx);
            if (ijx == null) return;

            int partyId = (int)VarField(ijx, 1);
            var party = Parties.Get(partyId);
            long meId = SessionContext.State.CharacterId;
            if (party == null || !Parties.Accept(party, meId))
            {
                Console.WriteLine($"[Grupo] Aceptación sin invitación: grupo {partyId}.");
                return;
            }

            // A quien acepta, el grupo entero.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ing, BuildParty(party)));

            // Y a los demás, sólo el que entra: el grupo entero no se reenvía.
            string meName = SessionContext.State.CharacterName;
            byte[] entra = ConnectionProtocol.Push(Op.Ink,
                Pb.New().Var(1, party.Id).Msg(2, BuildMember(meId)).Build());

            foreach (long otro in Parties.MembersOf(party))
            {
                if (otro == meId) continue;
                var sesion = SessionRegistry.FindByCharacter(otro);
                if (sesion == null) continue;

                await sesion.SendAsync(entra);
                await sesion.SendAsync(ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildSystemMessage(FollowsYouMessage, meName)));
            }

            Console.WriteLine($"[Grupo] {meName} entra en el grupo {party.Id} " +
                              $"({Parties.MembersOf(party).Count} miembros).");
        }

        // ─── Rechazar ───────────────────────────────────────────────────────────

        public static async Task RefuseAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? iki = ConnectionProtocol.ReadPayload(payload, Op.Iki);
            if (iki == null) return;

            int partyId = (int)VarField(iki, 2);
            var party = Parties.Get(partyId);
            long meId = SessionContext.State.CharacterId;
            if (party == null) return;

            long hostId = Parties.Refuse(party, meId);
            if (hostId == 0) return;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ilo,
                    ConnectionProtocol.BuildInvitationClosed(partyId, hostId)));

            // Al que invitó: quítale de la lista. Y si el grupo se queda con uno, se deshace —los
            // dos mensajes llegan pegados en el servidor real, en el mismo segmento.
            var host = SessionRegistry.FindByCharacter(hostId);
            if (host != null)
            {
                await host.SendAsync(ConnectionProtocol.Push(Op.Iko,
                    ConnectionProtocol.BuildInvitationWithdrawn(meId, partyId)));

                if (Parties.MembersOf(party).Count <= 1)
                {
                    await host.SendAsync(ConnectionProtocol.Push(Op.Imy,
                        ConnectionProtocol.BuildPartyDissolved(partyId)));
                    Parties.Dissolve(party);
                }
            }

            Console.WriteLine($"[Grupo] {SessionContext.State.CharacterName} rechaza el grupo {partyId}.");
        }

        // ─── Salirse ────────────────────────────────────────────────────────────

        public static async Task LeaveAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? inh = ConnectionProtocol.ReadPayload(payload, Op.Inh);
            if (inh == null) return;

            int partyId = (int)VarField(inh, 2);
            var party = Parties.Get(partyId);
            long meId = SessionContext.State.CharacterId;
            if (party == null) return;

            var (quedan, deshecho, nuevoJefe) = Parties.Leave(party, meId);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ils, ConnectionProtocol.BuildPartyLeft(partyId)));

            foreach (long otro in quedan)
            {
                var sesion = SessionRegistry.FindByCharacter(otro);
                if (sesion == null) continue;

                if (deshecho)
                {
                    await sesion.SendAsync(ConnectionProtocol.Push(Op.Imy,
                        ConnectionProtocol.BuildPartyDissolved(partyId)));
                    continue;
                }

                // Si el que se iba mandaba, el mando pasa al siguiente que entró: un grupo sin
                // jefe no lo entiende el cliente.
                if (nuevoJefe != 0)
                {
                    await sesion.SendAsync(ConnectionProtocol.Push(Op.Ilx,
                        ConnectionProtocol.BuildPartyLeader(nuevoJefe, partyId)));
                }
                await sesion.SendAsync(ConnectionProtocol.Push(Op.Ing, BuildParty(party)));
            }

            Console.WriteLine($"[Grupo] {SessionContext.State.CharacterName} deja el grupo " +
                              $"{partyId}{(deshecho ? " y se deshace" : "")}.");
        }

        // ─── Ceder el mando ─────────────────────────────────────────────────────

        public static async Task PromoteAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? ima = ConnectionProtocol.ReadPayload(payload, Op.Ima);
            if (ima == null) return;

            long nuevo = VarField(ima, 1);
            int partyId = (int)VarField(ima, 3);
            var party = Parties.Get(partyId);
            if (party == null || party.LeaderId != SessionContext.State.CharacterId) return;
            if (!Parties.Promote(party, nuevo)) return;

            // El imk va vacío del todo: ni siquiera lleva carga.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Imk));

            foreach (long quien in Parties.MembersOf(party))
            {
                var sesion = SessionRegistry.FindByCharacter(quien);
                if (sesion == null) continue;
                await sesion.SendAsync(ConnectionProtocol.Push(Op.Ilx,
                    ConnectionProtocol.BuildPartyLeader(nuevo, partyId)));
            }

            Console.WriteLine($"[Grupo] El grupo {partyId} pasa a mandarlo {nuevo}.");
        }

        /// <summary>Alguien se ha desconectado: sale del grupo sin decir nada.</summary>
        public static async Task DisconnectedAsync(long characterId)
        {
            var party = Parties.Of(characterId);
            if (party == null) return;

            var (quedan, deshecho, nuevoJefe) = Parties.Leave(party, characterId);
            foreach (long otro in quedan)
            {
                var sesion = SessionRegistry.FindByCharacter(otro);
                if (sesion == null) continue;
                if (deshecho)
                {
                    await sesion.SendAsync(ConnectionProtocol.Push(Op.Imy,
                        ConnectionProtocol.BuildPartyDissolved(party.Id)));
                    continue;
                }
                if (nuevoJefe != 0)
                {
                    await sesion.SendAsync(ConnectionProtocol.Push(Op.Ilx,
                        ConnectionProtocol.BuildPartyLeader(nuevoJefe, party.Id)));
                }
                await sesion.SendAsync(ConnectionProtocol.Push(Op.Ing, BuildParty(party)));
            }
        }

        // ─── Piezas ─────────────────────────────────────────────────────────────

        /// <summary>
        /// El grupo entero (ing): { f1 (repetido): miembro, f4: el jefe, f5: 1, f6: 1,
        /// f7: el grupo, f10: plazas }.
        ///
        /// El orden en que van los miembros da igual: el cliente los pinta de arriba abajo
        /// ordenados por iniciativa, no por como lleguen.
        /// </summary>
        private static byte[] BuildParty(Managers.Parties.Party party)
        {
            var ing = Pb.New();
            foreach (long quien in Parties.MembersOf(party)) ing.Msg(1, BuildMember(quien));

            return ing
                .Var(4, party.LeaderId)
                .Var(5, 1)
                .Var(6, 1)
                .Var(7, party.Id)
                .Var(10, Parties.MaxMembers)
                .Build();
        }

        /// <summary>Un miembro: { f1: su hoja, f2: su id }. Igual dentro del ing que del ink.</summary>
        private static Pb BuildMember(long characterId)
            => Pb.New().Bytes(1, MemberSheet(characterId)).Var(2, characterId);

        /// <summary>
        /// La hoja de un miembro. Es la MISMA que la de la lista de personajes —nombre, nivel,
        /// sexo, aspecto y raza— más lo que el grupo añade:
        ///
        ///   f2: nombre   f3: nivel
        ///   f4 { f2 { f1: lo del grupo, f3: sexo }, f6: el aspecto, f7: la raza }
        ///
        /// y lo del grupo, que se mete en el mismo hueco donde iba el sexo:
        ///
        ///   f2 { f1: 1 }
        ///   f4 { f1: mapa, f2: x, f4: subzona, f5: y }
        ///   f7 { f1: 5, f3: prospección, f4: vida, f6: vida máxima }
        ///   f8: iniciativa
        ///
        /// La posición está COMPROBADA contra la base: los cuatro mapas que salen en las capturas
        /// —130286592, 217056262, 212600322 y 88212757— dan en MapPositions exactamente las x, las
        /// y y las subzonas que llevan los mensajes, hasta la última cifra. Las coordenadas
        /// negativas viajan en complemento a dos de 64 bits, no en zigzag.
        ///
        /// ─── Esto es lo que faltaba ─────────────────────────────────────────────────────────
        ///
        /// La hoja que se mandaba antes llevaba nombre, nivel y raza y nada más. Sin el aspecto el
        /// cliente no tiene con qué dibujar el retrato del miembro, y el grupo no llegaba a
        /// formarse: la invitación salía, el aceptar viajaba, el servidor daba el grupo por hecho
        /// —y por eso ya no dejaba invitar a nadie más— pero en pantalla no aparecía nada.
        /// </summary>
        private static byte[] MemberSheet(long characterId)
        {
            var character = DatabaseManager.GetCharacterById(characterId);
            if (character == null) return Array.Empty<byte>();

            var session = SessionRegistry.FindByCharacter(characterId);

            // El bloque del sexo es el mismo hueco donde el grupo mete lo suyo: el sexo en su f3
            // y lo del grupo en su f1.
            var enElGrupo = Pb.New();
            if (session != null) enElGrupo.Bytes(1, PartyInfo(session));
            enElGrupo.VarIfNotZero(3, character.Sex);

            var traits = Pb.New()
                .Msg(2, enElGrupo)
                .Bytes(6, BreedLookTable.BuildLook(
                    character.Breed, character.Sex, character.HeadId, null, character.Id))
                .VarIfNotZero(7, character.Breed);

            return Pb.New()
                .Str(2, character.Name)
                .VarIfNotZero(3, session?.State.CharacterLevel ?? character.Level)
                .Msg(4, traits)
                .Build();
        }

        /// <summary>
        /// El invitado que todavía no ha contestado, para la lista de quien invita (imf):
        ///
        ///   f2: el grupo
        ///   f3 { f1: su aspecto, f2: su nombre, f3: su id, f5: quien invita, f6 { f1: 1 }, f8: su raza }
        ///
        /// El aspecto que va aquí es el MISMO que luego lleva su hoja al entrar: en la captura del
        /// que invita, los bytes del imf y los del ink son idénticos.
        /// </summary>
        private static byte[] BuildPending(long guestId, long hostId, int partyId)
        {
            var character = DatabaseManager.GetCharacterById(guestId);
            if (character == null) return Pb.New().Var(2, partyId).Build();

            return Pb.New()
                .Var(2, partyId)
                .Msg(3, Pb.New()
                    .Bytes(1, BreedLookTable.BuildLook(
                        character.Breed, character.Sex, character.HeadId, null, character.Id))
                    .Str(2, character.Name)
                    .Var(3, guestId)
                    .Var(5, hostId)
                    .Msg(6, Pb.New().Var(1, 1))
                    .VarIfNotZero(8, character.Breed))
                .Build();
        }

        /// <summary>Prospección de partida, antes de la suerte y del equipo.</summary>
        private const int BaseProspecting = 100;

        /// <summary>
        /// El f1 del bloque de vida, que vale 5 en las cuatro fichas capturadas —dos personajes
        /// distintos, tres capturas— y no cambia con el nivel ni con la raza. No sabemos qué es,
        /// así que va el número que manda el juego: dejarlo fuera no es lo mismo que mandarlo.
        /// </summary>
        private const int UnknownLifeF1 = 5;

        /// <summary>
        /// Lo que el grupo añade a la hoja: dónde está, cuánta vida tiene y con qué iniciativa.
        ///
        /// La vida, la prospección y la iniciativa son las DEL MIEMBRO, no las de quien pregunta,
        /// así que se calculan dentro de su sesión. Vida y vida máxima van iguales porque fuera de
        /// combate el emulador no lleva la cuenta de la que le falta a nadie; en las cuatro fichas
        /// capturadas también salen iguales.
        ///
        /// Hay un quinto campo, el f5, que vale 2, 3 o 4 y no cambia para un mismo personaje entre
        /// capturas. No se ha podido averiguar qué es —no es la raza, ni el nivel, ni el mapa— así
        /// que no se manda: mejor el cero de proto3 que un número inventado.
        /// </summary>
        private static byte[] PartyInfo(GameSession session)
        {
            var state = session.State;
            var map = MapManager.GetMapInfo(state.MapId);

            int life, initiative, prospecting;
            using (SessionContext.Push(session))
            {
                life = StatsHandler.GetPlayerMaxHp();
                initiative = StatsHandler.GetPlayerInitiative();
                Managers.Equipment.Bonuses().TryGetValue(
                    ConnectionProtocol.Stat.Prospecting, out long delEquipo);
                prospecting = BaseProspecting + state.StatChance / 10 + (int)delEquipo;
            }

            var info = Pb.New().Msg(2, Pb.New().Var(1, 1));

            if (map != null)
            {
                info.Msg(4, Pb.New()
                    .Var(1, state.MapId)
                    .VarIfNotZero(2, map.PosX)
                    .VarIfNotZero(4, map.SubAreaId)
                    .VarIfNotZero(5, map.PosY));
            }

            return info
                .Msg(7, Pb.New()
                    .Var(1, UnknownLifeF1)
                    .VarIfNotZero(3, prospecting)
                    .VarIfNotZero(4, life)
                    .VarIfNotZero(6, life))
                .VarIfNotZero(8, initiative)
                .Build();
        }

        /// <summary>El nombre que lleva un ime: va en f1.f4.f1, tres capas dentro.</summary>
        private static string NameIn(byte[] ime)
        {
            foreach (var uno in ProtoMessage.Parse(ime).Fields)
            {
                if (uno.FieldNumber != 1 || uno.WireType != 2) continue;
                foreach (var cuatro in ProtoMessage.Parse(uno.BytesValue).Fields)
                {
                    if (cuatro.FieldNumber != 4 || cuatro.WireType != 2) continue;
                    foreach (var nombre in ProtoMessage.Parse(cuatro.BytesValue).Fields)
                    {
                        if (nombre.FieldNumber != 1 || nombre.WireType != 2) continue;
                        try
                        {
                            return System.Text.Encoding.UTF8.GetString(nombre.BytesValue);
                        }
                        catch (Exception) { return ""; }
                    }
                }
            }
            return "";
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

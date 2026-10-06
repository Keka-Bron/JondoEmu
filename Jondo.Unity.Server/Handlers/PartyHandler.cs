using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Parties: inviting, accepting, refusing, leaving and handing over the lead.
    ///
    /// ─── The dance, measured in the six captures ────────────────────────────────────────────
    ///
    /// With both points of view, which is what was needed: in some captures the inviter records and in
    /// others the invited player, and the messages are not the same.
    ///
    ///   invite     C→S ime { name }          →  S→C ing (the party, you alone) + imf
    ///   invited                                 S→C ijz { me, who, slots, party, name }
    ///   details    C→S imd { party }         →  S→C ilb   (NOT implemented: the little window's
    ///                                                      «Detalles» button does not answer
    ///                                                      anything yet)
    ///   accept     C→S ijx { party }         →  S→C ing (the whole party)
    ///                                           and to the inviter: ink
    ///   follow     C→S imh / imo             →  see <see cref="PartyFollowHandler"/>
    ///   refuse     C→S iki { party }         →  to you ilo; to the inviter iko + imy
    ///   leave      C→S inh { party }         →  S→C ils { party }
    ///   hand over  C→S ima { who, party }    →  S→C imk (empty) + ilx { who, party }
    ///   kick       C→S ili { party, who }    →  to the kicked one ils; see <see cref="KickAsync"/>
    ///
    /// The <c>ili</c> is the only one that does not come from the captures but from the running
    /// client: in the 34 folders there is not a single time anybody kicks anybody.
    ///
    /// Two misleading things worth keeping in mind. One invites by NAME and accepts by PARTY ID: the
    /// ime carries «Uber-Black» in text and the ijx carries 71272. And the party is created ON INVITING,
    /// before the other answers, which is why the ing with a single member arrives right away; if the
    /// other says no, it undoes itself.
    ///
    /// The change of leader does NOT resend the party: it sends an eleven-byte ilx. It was checked by
    /// comparing the sheet of the same party before and after, and the only thing that changes is its
    /// field 4.
    ///
    /// ─── Why the party did not form ─────────────────────────────────────────────────────────
    ///
    /// The first version sent a member sheet with the name, the level and the breed and nothing else.
    /// The invitation went out and the accept travelled, but the party did not appear on screen, and
    /// on top of that the inviter could no longer invite anybody else: the server considered the party
    /// done and the client did not. The LOOK was missing, which is what it draws each member's portrait
    /// with. Now the sheet goes whole; see <see cref="MemberSheet"/>.
    /// </summary>
    public static class PartyHandler
    {
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

            // The party is created on inviting, not on accepting: it is what the real server does.
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

            // And the invited player, in the inviter's list, with the label saying he is pending.
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

            // To whoever accepts, the whole party.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ing, BuildParty(party)));

            // And to the others, only the one joining: the whole party is not resent.
            //
            // The 1663 («... sigue tu desplazamiento») that used to go with it belongs to the imh
            // the new member's client may send next, not to joining: two other captures add a
            // member with the same ink and no 1663. See PartyFollowProtocol.FollowsYouMessage.
            string meName = SessionContext.State.CharacterName;
            byte[] entra = ConnectionProtocol.Push(Op.Ink,
                Pb.New().Var(1, party.Id).Msg(2, BuildMember(meId)).Build());

            foreach (long otro in Parties.MembersOf(party))
            {
                if (otro == meId) continue;
                var sesion = SessionRegistry.FindByCharacter(otro);
                if (sesion == null) continue;

                await sesion.SendAsync(entra);
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

            // To the inviter: take him off the list. And if the party is left with one, it is undone --
            // both messages arrive together on the real server, in the same segment.
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

            var salida = Parties.Leave(party, meId);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ils, ConnectionProtocol.BuildPartyLeft(partyId)));

            await AnnounceGoneAsync(party, partyId, salida);

            Console.WriteLine($"[Grupo] {SessionContext.State.CharacterName} deja el grupo " +
                              $"{partyId}{(salida.Dissolved ? " y se deshace" : "")}.");
        }

        // ─── Expulsar ───────────────────────────────────────────────────────────

        /// <summary>
        /// Kicking somebody out of the party. The client asks for it with
        ///
        ///   ili { f1: the party, f2: whom }
        ///
        /// which is the only thing of all this measured from the real client: there is no capture where
        /// anybody is kicked, not even among the 34 folders.
        ///
        /// That is why whoever stays inside is NOT sent a message of his own saying «so-and-so has been
        /// kicked». It exists -- the client has its handler for the <c>inc</c> --, but its shape is not
        /// measured and the .proto gets the numbering wrong often enough not to be trusted. The whole
        /// party is sent, which is measured and tells the truth.
        ///
        /// With two people, which is the normal case, the question does not even arise: the party is left
        /// with one and is undone, and the <c>imy</c> for undoing it is measured.
        /// </summary>
        public static async Task KickAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? ili = ConnectionProtocol.ReadPayload(payload, Op.Ili);
            if (ili == null) return;

            int partyId = (int)VarField(ili, 1);
            long quien = VarField(ili, 2);
            var party = Parties.Get(partyId);
            if (party == null || quien == 0) return;

            long meId = SessionContext.State.CharacterId;
            string meName = SessionContext.State.CharacterName;

            // Only the leader kicks, and for leaving oneself there is the inh.
            if (party.LeaderId != meId)
            {
                Console.WriteLine($"[Grupo] {meName} intenta echar del grupo {partyId} sin mandarlo.");
                return;
            }
            if (quien == meId) return;

            // If he had not answered the invitation yet, he is not kicked: it is withdrawn.
            long host = Parties.Refuse(party, quien);
            if (host != 0)
            {
                await WithdrawAsync(stream, party, partyId, quien, host);
                Console.WriteLine($"[Grupo] {meName} retira la invitación de {quien} al grupo {partyId}.");
                return;
            }

            if (!Parties.MembersOf(party).Contains(quien)) return;

            var salida = Parties.Leave(party, quien);

            var echado = SessionRegistry.FindByCharacter(quien);
            if (echado != null)
            {
                await echado.SendAsync(ConnectionProtocol.Push(Op.Ils,
                    ConnectionProtocol.BuildPartyLeft(partyId)));
            }

            await AnnounceGoneAsync(party, partyId, salida);

            Console.WriteLine($"[Grupo] {meName} echa a " +
                              $"{echado?.State.CharacterName ?? quien.ToString()} del grupo " +
                              $"{partyId}{(salida.Dissolved ? ", que se deshace" : "")}.");
        }

        /// <summary>
        /// Withdrawing an invitation that had not been answered yet. It is the same the real server sends
        /// when the invited player says no, but the other way round: here the inviter cuts it off.
        /// </summary>
        private static async Task WithdrawAsync(NetworkStream stream, Managers.Parties.Party party,
                                                int partyId, long guestId, long hostId)
        {
            var guest = SessionRegistry.FindByCharacter(guestId);
            if (guest != null)
            {
                await guest.SendAsync(ConnectionProtocol.Push(Op.Ilo,
                    ConnectionProtocol.BuildInvitationClosed(partyId, hostId)));
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iko,
                    ConnectionProtocol.BuildInvitationWithdrawn(guestId, partyId)));

            if (Parties.MembersOf(party).Count <= 1)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Imy, ConnectionProtocol.BuildPartyDissolved(partyId)));
                Parties.Dissolve(party);
            }
        }

        /// <summary>
        /// To those who stay: either the party has been undone, or there is a new leader and a new list.
        ///
        /// The three roads by which somebody stops being in it -- leaving, being kicked and disconnecting --
        /// use it, because what the rest see is the same in all three.
        /// </summary>
        private static async Task AnnounceGoneAsync(
            Managers.Parties.Party party, int partyId,
            (IReadOnlyList<long> Remaining, bool Dissolved, long NewLeader) salida)
        {
            foreach (long otro in salida.Remaining)
            {
                var sesion = SessionRegistry.FindByCharacter(otro);
                if (sesion == null) continue;

                if (salida.Dissolved)
                {
                    await sesion.SendAsync(ConnectionProtocol.Push(Op.Imy,
                        ConnectionProtocol.BuildPartyDissolved(partyId)));
                    continue;
                }

                // If the one leaving was in charge, the lead passes to the next one who joined: the
                // client does not understand a party without a leader.
                if (salida.NewLeader != 0)
                {
                    await sesion.SendAsync(ConnectionProtocol.Push(Op.Ilx,
                        ConnectionProtocol.BuildPartyLeader(salida.NewLeader, partyId)));
                }
                await sesion.SendAsync(ConnectionProtocol.Push(Op.Ing, BuildParty(party)));
            }

            // Whoever followed the one who left stops: the leader they followed is gone.
            if (!salida.Dissolved && salida.NewLeader != 0)
                await PartyFollowHandler.LeaderChangedAsync(party);
        }

        // ─── Handing over the lead ─────────────────────────────────────────────

        public static async Task PromoteAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? ima = ConnectionProtocol.ReadPayload(payload, Op.Ima);
            if (ima == null) return;

            long nuevo = VarField(ima, 1);
            int partyId = (int)VarField(ima, 3);
            var party = Parties.Get(partyId);
            if (party == null || party.LeaderId != SessionContext.State.CharacterId) return;
            if (!Parties.Promote(party, nuevo)) return;

            // The imk goes completely empty: it does not even carry a payload.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Imk));

            foreach (long quien in Parties.MembersOf(party))
            {
                var sesion = SessionRegistry.FindByCharacter(quien);
                if (sesion == null) continue;
                await sesion.SendAsync(ConnectionProtocol.Push(Op.Ilx,
                    ConnectionProtocol.BuildPartyLeader(nuevo, partyId)));
            }

            // And following ends with the old leader: the imk above is the one the zaap sends to
            // the followers, so they get it too. Inferred; see PartyFollowHandler.
            await PartyFollowHandler.LeaderChangedAsync(party);

            Console.WriteLine($"[Grupo] El grupo {partyId} pasa a mandarlo {nuevo}.");
        }

        /// <summary>Somebody has disconnected: he leaves the party without a word.</summary>
        public static async Task DisconnectedAsync(long characterId)
        {
            var party = Parties.Of(characterId);
            if (party == null) return;

            int partyId = party.Id;
            await AnnounceGoneAsync(party, partyId, Parties.Leave(party, characterId));
        }

        // ─── Piezas ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The whole party (ing): { f1 (repeated): member, f4: the leader, f5: 1, f6: 1,
        /// f7: the party, f10: slots }.
        ///
        /// The order of the members makes no difference: the client draws them top to bottom sorted by
        /// initiative, not by how they arrive.
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

        /// <summary>A member: { f1: his sheet, f2: his id }. The same inside the ing as in the ink.</summary>
        private static Pb BuildMember(long characterId)
            => Pb.New().Bytes(1, MemberSheet(characterId)).Var(2, characterId);

        /// <summary>
        /// A member's sheet. It is THE SAME as the character list's -- name, level, sex, look and breed --
        /// plus what the party adds:
        ///
        ///   f2: name   f3: level
        ///   f4 { f2 { f1: the party's part, f3: sex }, f6: the look, f7: the breed }
        ///
        /// and the party's part, which goes into the same slot the sex went in:
        ///
        ///   f2 { f1: 1 }
        ///   f4 { f1: map, f2: x, f4: subarea, f5: y }
        ///   f7 { f1: 5, f3: prospecting, f4: life, f6: maximum life }
        ///   f8: initiative
        ///
        /// The position is CHECKED against the database: the four maps in the captures -- 130286592,
        /// 217056262, 212600322 and 88212757 -- give in MapPositions exactly the x, the y and the subareas
        /// the messages carry, down to the last digit. Negative coordinates travel in 64-bit two's
        /// complement, not in zigzag.
        ///
        /// ─── This is what was missing ───────────────────────────────────────────────────────
        ///
        /// The sheet sent before carried name, level and breed and nothing else. Without the look the client
        /// has nothing to draw the member's portrait with, and the party never got to form: the invitation
        /// went out, the accept travelled, the server considered the party done -- and that is why it no
        /// longer let anybody else be invited -- but nothing appeared on screen.
        /// </summary>
        private static byte[] MemberSheet(long characterId)
        {
            var character = DatabaseManager.GetCharacterById(characterId);
            if (character == null) return Array.Empty<byte>();

            var session = SessionRegistry.FindByCharacter(characterId);

            // The sex block is the same slot where the party puts its part: the sex in its f3
            // and the party's part in its f1.
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
        /// The invited player who has not answered yet, for the inviter's list (imf):
        ///
        ///   f2: the party
        ///   f3 { f1: his look, f2: his name, f3: his id, f5: who invites, f6 { f1: 1 }, f8: his breed }
        ///
        /// The look that goes here is THE SAME his sheet carries later on joining: in the inviter's capture,
        /// the bytes of the imf and those of the ink are identical.
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

        /// <summary>Starting prospecting, before chance and equipment.</summary>
        private const int BaseProspecting = 100;

        /// <summary>
        /// The life block's f1, which is 5 in the four captured sheets -- two different characters, three
        /// captures -- and does not change with level or breed. We do not know what it is, so the number the
        /// game sends goes: leaving it out is not the same as sending it.
        /// </summary>
        private const int UnknownLifeF1 = 5;

        /// <summary>
        /// What the party adds to the sheet: where he is, how much life he has and with what initiative.
        ///
        /// Life, prospecting and initiative are THE MEMBER's, not the asker's, so they are worked out inside
        /// his session. Life and maximum life go equal because outside a fight the emulator does not keep
        /// track of anybody's missing life; in the four captured sheets they come out equal too.
        ///
        /// There is a fifth field, f5, which is 2, 3 or 4 and does not change for the same character between
        /// captures. It has not been possible to find out what it is -- it is not the breed, nor the level,
        /// nor the map -- so it is not sent: better proto3's zero than a made-up number.
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
                prospecting = BaseProspecting + state.TotalChance / 10 + (int)delEquipo;
            }

            var info = Pb.New().Msg(2, Pb.New().Var(1, 1));

            // The same block the ikv carries when the party follows its leader.
            if (map != null) info.Msg(4, PartyFollowProtocol.MapPosition(state.MapId, map));

            return info
                .Msg(7, Pb.New()
                    .Var(1, UnknownLifeF1)
                    .VarIfNotZero(3, prospecting)
                    .VarIfNotZero(4, life)
                    .VarIfNotZero(6, life))
                .VarIfNotZero(8, initiative)
                .Build();
        }

        /// <summary>The name an ime carries: it goes in f1.f4.f1, three layers inside.</summary>
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

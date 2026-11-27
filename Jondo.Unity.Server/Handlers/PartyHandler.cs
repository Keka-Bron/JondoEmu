using Jondo.Unity.ServerMods;
using Jondo.Unity.Server.Mods;
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
    ///   details    C→S imd { party }         →  S→C ilb   see <see cref="DetailsAsync"/>
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
    /// ime carries «Follower-1» in text and the ijx carries 71272. And the party is created ON INVITING,
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
        // ─── Inviting ───────────────────────────────────────────────────────────

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
                Console.WriteLine($"[Party] {meName} tries to invite themselves.");
                return;
            }

            var guest = SessionRegistry.FindByName(target);
            // A companion has no session of his own among the connected: the invitation
            // goes to whoever plays him, and his companions follow him into the party as they do. Among
            // several characters standing together, the one played was the only one that could be
            // invited (Keka-Bron/JondoEmu#217).
            if (guest == null && ServerModHooks.Get<IPartyMembershipPolicy>()?.PartyInviteRecipient(target) is > 0 and var recipient)
            {
                guest = SessionRegistry.FindByCharacter(recipient);
                if (guest != null)
                    Console.WriteLine($"[Party] {meName} invites {target}, a companion: the invitation goes to {guest.State.CharacterName}, who plays him.");
            }
            if (guest == null)
            {
                Console.WriteLine($"[Party] {meName} invites «{target}», who is not connected.");
                return;
            }
            // An invitation routed back to its sender targets their own companion.
            if (guest.State.CharacterId == meId)
            {
                Console.WriteLine($"[Party] {meName} invites their own companion {target}.");
                return;
            }

            // A party of his companions alone does not keep him from another: it is let go for the
            // invitation, and his companions follow him into whichever party he ends in.
            if (Parties.IsInParty(guest.State.CharacterId) && !IsCompanionOnlyParty(guest.State.CharacterId))
            {
                Console.WriteLine($"[Party] {guest.State.CharacterName} is already in a party.");
                return;
            }

            if (!ChatStatusHandler.TakesInvitations(guest))
            {
                Console.WriteLine($"[Party] {guest.State.CharacterName} is in solo mode.");
                return;
            }

            // The party is created on inviting, not on accepting: it is what the real server does.
            var party = Parties.Of(meId);
            bool nuevo = party == null;
            party ??= Parties.Create(meId);

            if (Parties.IsInParty(guest.State.CharacterId)) await DisbandCompanionPartyAsync(guest);
            if (!Parties.Invite(party, guest.State.CharacterId, meId))
            {
                Console.WriteLine($"[Party] Could not invite {guest.State.CharacterName}.");
                if (nuevo) Parties.Dissolve(party);
                await RejoinCompanionPartyAsync(guest);
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

            Console.WriteLine($"[Party] {meName} invites {guest.State.CharacterName} " +
                              $"to party {party.Id}.");
        }

        // ─── Accepting ──────────────────────────────────────────────────────────

        public static async Task AcceptAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? ijx = ConnectionProtocol.ReadPayload(payload, Op.Ijx);
            if (ijx == null) return;

            int partyId = (int)VarField(ijx, 1);
            var party = Parties.Get(partyId);
            long meId = SessionContext.State.CharacterId;
            if (party == null || !Parties.Accept(party, meId))
            {
                Console.WriteLine($"[Party] Acceptance without an invitation: party {partyId}.");
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

            Console.WriteLine($"[Party] {meName} joins party {party.Id} " +
                              $"({Parties.MembersOf(party).Count} members).");

            // And his companions in behind him, as long as there is room.
            await RejoinCompanionPartyAsync(SessionContext.Current);
        }

        // ─── Rejecting ──────────────────────────────────────────────────────────

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

            Console.WriteLine($"[Party] {SessionContext.State.CharacterName} turns down party {partyId}.");

            // His companions' party, let go for the invitation, back.
            await RejoinCompanionPartyAsync(SessionContext.Current);
        }

        // ─── Withdrawing an invitation ──────────────────────────────────────────

        /// <summary>
        /// A pending invitation withdrawn from the invitee's menu, «Cancelar la invitación»:
        /// <c>inq { 1 party, 2 invitee }</c>, by whoever invited him or the leader.
        /// </summary>
        /// <remarks>
        /// Read in the client, which no capture records. Its party frame (ffl) answers two messages
        /// with exactly this: <c>ilo { 1 party, 2 who withdrew }</c> takes the «partyInvit_» popup
        /// away and writes «ui.party.invitationCancelledForGuest» -- the same ilo the capture sends
        /// whoever declines --, and <c>ind { 1 party, 2 invitee, 3 who withdrew }</c> writes
        /// «ui.party.invitationCancelled» with both names and takes the invitee off the party's
        /// list. A party left with nobody but its leader and no invitation open breaks up, as on a
        /// refusal.
        ///
        /// The invitee first, then who withdrew, as the client reads them (Keka-Bron/JondoEmu#216):
        /// sent the other way round, it took the inviter off the list, left the invitee pending
        /// with his hourglass, and wrote «Testas a annulé l'invitation de Lilia» for Lilia's own.
        /// </remarks>
        public static async Task CancelInvitationAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? inq = ConnectionProtocol.ReadPayload(payload, "inq");
            if (inq == null) return;

            int partyId = (int)VarField(inq, 1);
            long guestId = VarField(inq, 2);
            var party = Parties.Get(partyId);
            long meId = SessionContext.State.CharacterId;
            if (party == null || !Parties.MembersOf(party).Contains(meId)) return;

            long hostId;
            lock (party.Gate)
            {
                if (!party.Pending.TryGetValue(guestId, out hostId)) return;
            }
            if (hostId != meId && party.LeaderId != meId) return;
            Parties.Refuse(party, guestId);

            var guest = SessionRegistry.FindByCharacter(guestId);
            if (guest != null)
            {
                await guest.SendAsync(ConnectionProtocol.Push(Op.Ilo, ConnectionProtocol.BuildInvitationClosed(partyId, meId)));
                await RejoinCompanionPartyAsync(guest);
            }

            byte[] cancelled = ConnectionProtocol.Push("ind", Pb.New().Var(1, partyId).Var(2, guestId).Var(3, meId).Build());
            foreach (long member in Parties.MembersOf(party))
            {
                var session = SessionRegistry.FindByCharacter(member);
                if (session != null) await session.SendAsync(cancelled);
            }

            bool alone;
            lock (party.Gate) alone = party.Pending.Count == 0;
            if (alone && Parties.MembersOf(party).Count <= 1)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Imy, ConnectionProtocol.BuildPartyDissolved(partyId)));
                Parties.Dissolve(party);
            }
            Console.WriteLine($"[Party] {SessionContext.State.CharacterName} withdraws the invitation of {guestId} to party {partyId}.");
        }

        // ─── Loyalty ────────────────────────────────────────────────────────────

        /// <summary>
        /// The party window's lock, «no aceptar más invitaciones»: <c>ila { 2 party, 4 on }</c>, read
        /// off the client's party actions (ffd). The answer is <c>ilj { 1 on, 2 party }</c>, which
        /// its party frame keeps and hands the window (PartyDisplayWindowHookHandler
        /// .OnPartyLoyaltyStatus), the old game's PartyLoyaltyStatus: the member's own pledge to stay.
        /// Kept with the party; nobody in a party can be invited to another here anyway.
        /// </summary>
        public static async Task PledgeLoyaltyAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? ila = ConnectionProtocol.ReadPayload(payload, "ila");
            if (ila == null) return;

            int partyId = (int)VarField(ila, 2);
            bool loyal = VarField(ila, 4) != 0;
            var party = Parties.Get(partyId);
            long meId = SessionContext.State.CharacterId;
            if (party == null || !Parties.MembersOf(party).Contains(meId)) return;

            lock (party.Gate)
            {
                if (loyal) party.Loyal.Add(meId);
                else party.Loyal.Remove(meId);
            }
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("ilj", Pb.New().VarIfNotZero(1, loyal ? 1 : 0).Var(2, partyId).Build()));
        }

        // ─── The invitation's details ───────────────────────────────────────────

        /// <summary>
        /// The invitation's «Detalles» button: imd { f2: party } → ilb, measured in «recibir
        /// invitacion de grupo y aceptar» (frames 54-55, the ilb a push, not an answer):
        ///
        ///   f1: 1 (the party's kind, the ing's f5)    f3: each member, as in the ing
        ///   f4: who invites    f5: the party    f6: each invited player, as in the imf
        ///   f7: the leader     f9: the inviter's name
        ///
        /// For its members and whoever has an invitation open to it; nobody else is told.
        /// </summary>
        public static async Task DetailsAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? imd = ConnectionProtocol.ReadPayload(payload, Op.Imd);
            if (imd == null) return;
            var party = Parties.Get((int)VarField(imd, 2));
            long meId = SessionContext.State.CharacterId;
            if (party == null) return;

            long host;
            List<(long Guest, long Host)> guests;
            lock (party.Gate)
            {
                if (!party.Members.Contains(meId) && !party.Pending.ContainsKey(meId)) return;
                host = party.Pending.TryGetValue(meId, out long invitedBy) ? invitedBy : party.LeaderId;
                guests = party.Pending.Select(p => (p.Key, p.Value)).ToList();
            }

            var ilb = Pb.New().Var(1, 1);
            foreach (long member in Parties.MembersOf(party)) ilb.Msg(3, BuildMember(member));
            ilb.Var(4, host).Var(5, party.Id);
            foreach (var (guest, invitedBy) in guests)
                if (BuildGuest(guest, invitedBy) is { } sheet) ilb.Msg(6, sheet);
            ilb.Var(7, party.LeaderId)
               .StrIfNotEmpty(9, DatabaseManager.GetCharacterById(host)?.Name ?? "");

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Ilb, ilb.Build()));
        }

        // ─── Leaving ────────────────────────────────────────────────────────────

        public static async Task LeaveAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? inh = ConnectionProtocol.ReadPayload(payload, Op.Inh);
            if (inh == null) return;

            int partyId = (int)VarField(inh, 2);
            var party = Parties.Get(partyId);
            long meId = SessionContext.State.CharacterId;
            if (party == null) return;

            // Leaving a full group of the dungeon search by one's own will costs the search an hour.
            bool full = MatchmakingHandler.IsFull(party);
            await LeaveCompanionPartyAsync(party, meId);
            var salida = Parties.Leave(party, meId);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ils, ConnectionProtocol.BuildPartyLeft(partyId)));

            await AnnounceGoneAsync(party, partyId, salida);
            await MatchmakingHandler.LeftAsync(party, meId, salida.Dissolved, penalised: full);

            Console.WriteLine($"[Party] {SessionContext.State.CharacterName} leaves party " +
                              $"{partyId}{(salida.Dissolved ? " and it breaks up" : "")}.");

            // His companions go with him: a party of their own around him.
            await RejoinCompanionPartyAsync(SessionContext.Current);
        }

        // ─── Expelling ──────────────────────────────────────────────────────────

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

            // In a group the dungeon search made, putting somebody out is the group's vote: this
            // ili is a yes to it -- the one that starts it, or a ballot.
            if (Matchmaking.GroupOf(partyId) != null)
            {
                await MatchmakingHandler.KickBallotAsync(stream, party, quien);
                return;
            }

            long meId = SessionContext.State.CharacterId;
            string meName = SessionContext.State.CharacterName;

            // Only the leader kicks, and for leaving oneself there is the inh.
            if (party.LeaderId != meId)
            {
                Console.WriteLine($"[Party] {meName} tries to kick from party {partyId} without leading it.");
                return;
            }
            if (quien == meId) return;

            // If he had not answered the invitation yet, he is not kicked: it is withdrawn.
            long host = Parties.Refuse(party, quien);
            if (host != 0)
            {
                await WithdrawAsync(stream, party, partyId, quien, host);
                Console.WriteLine($"[Party] {meName} withdraws {quien}'s invitation to party {partyId}.");
                return;
            }

            if (ServerModHooks.Get<IPartyMembershipPolicy>()?.ReleaseKickedFollower(meId, quien) == true) return;

            if (!await PutOutAsync(party, quien)) return;
            Console.WriteLine($"[Party] {meName} kicks {quien} out of party {partyId}.");
        }

        /// <summary>
        /// Somebody out of the party, as the leader's kick or the dungeon search's vote does it: the
        /// ils to him and the party, or its end, to the others. False when he was not in it.
        /// </summary>
        internal static async Task<bool> PutOutAsync(Managers.Parties.Party party, long who)
        {
            if (!Parties.MembersOf(party).Contains(who)) return false;
            int partyId = party.Id;
            await LeaveCompanionPartyAsync(party, who);
            var salida = Parties.Leave(party, who);

            var echado = SessionRegistry.FindByCharacter(who);
            if (echado != null)
            {
                await echado.SendAsync(ConnectionProtocol.Push(Op.Ils,
                    ConnectionProtocol.BuildPartyLeft(partyId)));
            }

            await AnnounceGoneAsync(party, partyId, salida);
            await MatchmakingHandler.LeftAsync(party, who, salida.Dissolved, penalised: false);
            if (salida.Dissolved) Console.WriteLine($"[Party] Party {partyId} breaks up.");
            if (echado != null) await RejoinCompanionPartyAsync(echado);
            return true;
        }

        /// <summary>The whole party broken up at once (imy to everybody): the dungeon search's group that gave up.</summary>
        internal static async Task BreakUpAsync(Managers.Parties.Party party)
        {
            var members = Parties.MembersOf(party).ToList();
            byte[] dissolved = ConnectionProtocol.Push(Op.Imy, ConnectionProtocol.BuildPartyDissolved(party.Id));
            Parties.Dissolve(party);
            foreach (long member in members)
            {
                var session = SessionRegistry.FindByCharacter(member);
                if (session != null) await session.SendAsync(dissolved);
            }
            Console.WriteLine($"[Party] Party {party.Id} breaks up.");
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
                await RejoinCompanionPartyAsync(guest);
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
            // A companion has no client to lead from.
            if (ServerModHooks.Get<IPartyMembershipPolicy>()?.CanLeadParty(nuevo) == false) return;
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

            Console.WriteLine($"[Party] Party {partyId} is now led by {nuevo}.");
        }

        /// <summary>Somebody has disconnected: he leaves the party without a word.</summary>
        public static async Task DisconnectedAsync(long characterId)
        {
            var party = Parties.Of(characterId);
            if (party == null) return;

            int partyId = party.Id;
            var salida = Parties.Leave(party, characterId);
            await AnnounceGoneAsync(party, partyId, salida);
            await MatchmakingHandler.LeftAsync(party, characterId, salida.Dissolved, penalised: false);
        }

        // ─── Companions ─────────────────────────────────────────────────────────────

        /// <summary>
        /// A companion connected: into his master's party, the game's own window drawing
        /// him with the others -- the party the master is in, or a new one he leads --, when it has
        /// room. Nobody sends a companion anything: he has no client; the others get him as a member who
        /// joins (ink), or the master the whole new party (ing).
        /// </summary>
        internal static async Task AddMemberAsync(GameSession master, long memberId, int? partyId)
        {
            long masterId = master.State.CharacterId;
            bool fresh = partyId == null;
            var party = partyId is int id ? Parties.Get(id) : Parties.Create(masterId);
            if (party == null) return;
            if (!Parties.Invite(party, memberId, masterId) || !Parties.Accept(party, memberId))
            {
                if (fresh) Parties.Dissolve(party);
                else Parties.Refuse(party, memberId);
                return;
            }

            if (fresh)
            {
                await master.SendAsync(ConnectionProtocol.Push(Op.Ing, BuildParty(party)));
                return;
            }
            byte[] joins = ConnectionProtocol.Push(Op.Ink, Pb.New().Var(1, party.Id).Msg(2, BuildMember(memberId)).Build());
            foreach (long member in Parties.MembersOf(party))
            {
                if (member == memberId) continue;
                var session = SessionRegistry.FindByCharacter(member);
                if (session != null) await session.SendAsync(joins);
            }
        }

        /// <summary>A companion let go: out of the party he was in, the others told as of any member put out.</summary>
        internal static async Task RemoveMemberAsync(long memberId)
        {
            var party = Parties.Of(memberId);
            if (party == null) return;
            if (Parties.IsInParty(memberId)) await PutOutAsync(party, memberId);
            else Parties.Refuse(party, memberId);
        }

        private static Task LeaveCompanionPartyAsync(Managers.Parties.Party party, long masterId)
            => ServerModHooks.Get<IPartyMembershipPolicy>()?.LeavePartyAsync(party.Id, masterId) ?? Task.CompletedTask;

        private static Task RejoinCompanionPartyAsync(GameSession? master)
            => master == null ? Task.CompletedTask : ServerModHooks.Get<IPartyMembershipPolicy>()?.JoinPartyAsync(master) ?? Task.CompletedTask;

        /// <summary>Whether the party a character is in is his and his companions' alone.</summary>
        private static bool IsCompanionOnlyParty(long characterId)
        {
            var party = Parties.Of(characterId);
            if (party == null) return false;
            var members = Parties.MembersOf(party);
            return ServerModHooks.Get<IPartyMembershipPolicy>()?.OwnsParty(characterId, members) ?? members.All(member => member == characterId);
        }

        /// <summary>
        /// A master's party of his companions alone let go, for an invitation to another: he is told it
        /// broke up (imy); his companions come back with his answer (<see cref="RejoinCompanionPartyAsync"/>).
        /// </summary>
        private static async Task DisbandCompanionPartyAsync(GameSession master)
        {
            var party = Parties.Of(master.State.CharacterId);
            if (party == null || !IsCompanionOnlyParty(master.State.CharacterId)) return;
            Parties.Dissolve(party);
            await master.SendAsync(ConnectionProtocol.Push(Op.Imy, ConnectionProtocol.BuildPartyDissolved(party.Id)));
        }

        internal static async Task PromoteAndNotifyAsync(GameSession session, int partyId)
        {
            long id = session.CharacterId;
            var party = Parties.Get(partyId);
            if (party == null || !Parties.Promote(party, id)) return;

            byte[] leader = ConnectionProtocol.Push(Op.Ilx, ConnectionProtocol.BuildPartyLeader(id, party.Id));
            foreach (long member in Parties.MembersOf(party))
            {
                if (member == id) continue;
                var other = SessionRegistry.FindByCharacter(member);
                if (other != null) await other.SendAsync(leader);
            }
            await PartyFollowHandler.LeaderChangedAsync(party);
        }

        internal static Task SendPartyAsync(GameSession session, int partyId)
            => Parties.Get(partyId) is { } party
                ? session.SendAsync(ConnectionProtocol.Push(Op.Ing, BuildParty(party))) : Task.CompletedTask;

        // ─── Pieces ─────────────────────────────────────────────────────────────

        /// <summary>
        /// The whole party (ing): { f1 (repeated): member, f4: the leader, f5: 1, f6: 1,
        /// f7: the party, f10: slots }.
        ///
        /// The order of the members makes no difference: the client draws them top to bottom sorted by
        /// initiative, not by how they arrive.
        /// </summary>
        /// <summary>
        /// Players put together in one party without inviting each other -- the automatic dungeon
        /// search's group: into the party one of them already leads, or a new one around the first.
        /// Everybody gets the whole party (ing), as the search's capture has it. The party.
        /// </summary>
        internal static async Task<Managers.Parties.Party> FormAsync(IReadOnlyList<long> members)
        {
            var party = members.Select(Parties.Of).FirstOrDefault(p => p != null && members.Contains(p.LeaderId))
                        ?? Parties.Create(members[0]);
            foreach (long member in members)
            {
                if (Parties.IsInParty(member) && Parties.Of(member) == party) continue;
                if (Parties.Invite(party, member, party.LeaderId)) Parties.Accept(party, member);
            }

            byte[] whole = ConnectionProtocol.Push(Op.Ing, BuildParty(party));
            foreach (long member in Parties.MembersOf(party))
            {
                var session = SessionRegistry.FindByCharacter(member);
                if (session != null) await session.SendAsync(whole);
            }
            Console.WriteLine($"[Party] The dungeon search makes party {party.Id} of {Parties.MembersOf(party).Count}.");
            return party;
        }

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

            var session = SessionRegistry.FindByCharacter(characterId) ?? CompanionSessions.SessionOf(characterId);

            // The sex block is the same slot where the party puts its part: the sex in its f3
            // and the party's part in its f1.
            var enElGrupo = Pb.New();
            if (session != null) enElGrupo.Bytes(1, PartyInfo(session));
            enElGrupo.VarIfNotZero(3, character.Sex);

            var traits = Pb.New()
                .Msg(2, enElGrupo)
                .Bytes(6, CharacterLookBuilder.BuildLook(character))
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
            var guest = BuildGuest(guestId, hostId);
            return guest == null ? Pb.New().Var(2, partyId).Build() : Pb.New().Var(2, partyId).Msg(3, guest).Build();
        }

        /// <summary>An invited player, the same in the imf's f3 and the ilb's f6.</summary>
        private static Pb? BuildGuest(long guestId, long hostId)
        {
            var character = DatabaseManager.GetCharacterById(guestId);
            if (character == null) return null;
            return Pb.New()
                .Bytes(1, CharacterLookBuilder.BuildLook(character))
                .Str(2, character.Name)
                .Var(3, guestId)
                .Var(5, hostId)
                .Msg(6, ChatStatusHandler.Online(guestId))
                .VarIfNotZero(8, character.Breed);
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
                var stats = SessionContext.Current.Combat;
                life = stats.GetPlayerMaxHp();
                initiative = stats.GetPlayerInitiative();
                Managers.Equipment.Bonuses().TryGetValue(
                    Stat.Prospecting, out long delEquipo);
                prospecting = BaseProspecting + state.TotalChance / 10 + (int)delEquipo;
            }

            var info = Pb.New().Msg(2, ChatStatusHandler.Of(state));

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

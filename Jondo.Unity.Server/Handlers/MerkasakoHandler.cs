using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Entering the haven bag, changing the decor and placing the furniture.
    ///
    /// From the captures:
    ///
    ///   client   jbn { f2: whose }        the button and the H key
    ///   client   jbl { f1: theme }        changing the decor
    ///   server   jru { f2: the map } + the whole map + jbu (furniture) + jaz (permissions)
    ///
    ///   client   jbv          server jbm        opening placement mode
    ///   client   jbg { f2 (rep): {f1: cell, f2: piece, f3: rotation} }   accept
    ///   client   jbk / jav / jaw   server jba                            closing the mode
    ///
    /// The jbg arrives in pieces -- in the capture there are three in a row -- and carries the WHOLE
    /// room, not the differences. That is why the pieces are put together and written at once on
    /// closing: saving each piece separately, the first would delete what the other two carry.
    /// </summary>
    public static class MerkasakoHandler
    {
        /// <summary>«No puedes entrar en tu merkasako antes del nivel 10»: info message 1/468.</summary>
        public const int MinimumLevel = 10;

        private const int TooLowType = 1, TooLow = 468;

        /// <summary>Whose bag a character stands in: the one he visits, or his own.</summary>
        public static long BagOwnerOf(SessionState state)
            => state.HavenBagOwnerId != 0 ? state.HavenBagOwnerId : state.CharacterId;

        /// <summary>
        /// Why a character may not come into another's bag, or null when he may: the owner opened it
        /// to his friends and they are mutual friends, or to his guild and they share one
        /// (HavenBagStore.SharedOf, the bag's menu «Compartir»). The friends and guild lists show
        /// the «Visitar el merkasako de este personaje» button on those terms.
        /// </summary>
        public static string? WhyNotVisit(long visitor, long visitorAccount, long owner)
        {
            var host = DatabaseManager.GetCharacterById(owner);
            if (host == null) return "no such character";
            int shared = HavenBagStore.SharedOf(owner);
            if ((shared & HavenBagStore.SharedWithFriends) != 0 && Contacts.AreMutual(visitorAccount, host.AccountId))
                return null;
            var guild = GuildStore.GuildOf(visitor);
            if ((shared & HavenBagStore.SharedWithGuild) != 0 && guild != null && GuildStore.GuildOf(owner)?.Id == guild.Id)
                return null;
            return "the bag is not open to him";
        }
        /// <summary>
        /// The button and the H key, which is a different message from changing the decor:
        ///
        ///   client   jbn { f2: whose haven bag it is }
        ///
        /// It carries a character because somebody else's can be visited. Here there is only one, so one
        /// goes into one's own, with the decor left on the last time.
        ///
        /// And IT IS A TOGGLE: the same message goes in and out, and the server decides by where the
        /// player is. Measured in «Movimiento/ir al merkasako y volver.pcapng»: the player's requests #1
        /// and #8 are the same jbn with the body 1081f092cbdd08 byte for byte; to the first it answers
        /// with map 162795538 -- subarea 851, the bags' one -- and to the second with 217056262, a normal
        /// world map. There is no third opcode: a sweep of the captures finds jbn in five files and jbl in
        /// one, and the empty jbl is decor 0, not the exit.
        ///
        /// Before, this called GoToThemeAsync without looking at anything, so the second H put the player
        /// back in the same room he wanted to leave.
        /// </summary>
        public static async Task EnterFromOutsideAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? jbn = ConnectionProtocol.ReadPayload(payload, Op.Jbn);
            if (jbn == null) return;
            if (await Managers.Jail.KeepsInAsync(stream)) return;
            if (await Managers.HeadHunts.HoldsAsync(stream, Managers.HeadHunts.Hold.Merkasako)) return;

            long owner = 0;
            foreach (var field in ProtoMessage.Parse(jbn).Fields)
                if (field.FieldNumber == 2 && field.WireType == 0) owner = (long)field.VarIntValue;
            await EnterBagAsync(stream, owner, invited: false);
        }

        /// <summary>
        /// Into a bag, one's own (owner 0 or oneself) or another's -- open to him, or he was invited
        /// -- or out of the one he stands in. See <see cref="EnterFromOutsideAsync"/>.
        /// </summary>
        private static async Task EnterBagAsync(NetworkStream stream, long owner, bool invited)
        {
            var state = SessionContext.State;
            bool visiting = owner != 0 && owner != state.CharacterId;

            if (Merkasako.IsHavenBag(state.MapId))
            {
                // The button again, or the bag one is in: out. Another's from a bag: straight there,
                // the way back the first one's.
                if (!visiting || owner == BagOwnerOf(state))
                {
                    await LeaveAsync(stream);
                    return;
                }
            }
            else
            {
                if (await ConquestHandler.HoldsInAsync(stream, ConquestHandler.NoHavenBagInAWar)) return;
                if (state.CharacterLevel < MinimumLevel)
                {
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(TooLowType, TooLow)));
                    return;
                }
            }

            if (visiting && !invited)
            {
                string? why = WhyNotVisit(state.CharacterId, SessionContext.Current.AccountId, owner);
                if (why != null)
                {
                    Console.WriteLine($"[Merkasako] {state.CharacterName} may not visit {owner}'s bag: {why}.");
                    return;
                }
            }

            if (!Merkasako.IsHavenBag(state.MapId))
            {
                state.HavenBagEntryMapId = state.MapId;
                state.HavenBagEntryCell = state.CellId;
                HavenBagStore.SetEntry(state.CharacterId, state.MapId, state.CellId);
            }
            else
            {
                // From one bag to another: the first one's people see him go before he changes bag.
                await SessionRegistry.RemoveFromMapAsync(state.MapId, state.CharacterId, SessionContext.Current.Id);
            }

            state.HavenBagOwnerId = visiting ? owner : 0;
            await GoToRoomAsync(stream, HavenBagStore.CurrentRoom(BagOwnerOf(state)), entering: true);
        }

        // ─── Invitations and kicks ───────────────────────────────────────────────

        /// <summary>
        /// The seconds an invitation waits for its answer: jbh f2, the popup's countdown (fyo.bjcv).
        /// INFERRED: no capture invites.
        /// </summary>
        public const int InvitationSeconds = 30;

        /// <summary>An invitation waiting for its answer: who invites, until when.</summary>
        private sealed record Invitation(long Host, DateTime Until);

        private static readonly ConcurrentDictionary<long, Invitation> _invitations = new();

        /// <summary>The info messages of the invitations (InfoMessagesDataRoot).</summary>
        private const int Warning = 1, AlreadyInvited = 477, CannotCome = 473, TooBusy = 554, GuestTooLow = 555,
                          Kicked = 627, KickedOut = 628;

        /// <summary>A character as the invitation's lines name him (lpg { f1 lpe { f2 name, f3 level }, f2 id }).</summary>
        public static Pb Person(long id, string name, int level)
            => Pb.New().Msg(1, Pb.New().Str(2, name ?? "").Var(3, level)).Var(2, id);

        /// <summary>
        /// «Invitar a mi merkasako» (jbb { f1 the guest }; PlayerMenuMaker.OnHavenbagInvite): the guest
        /// gets «{0} te invita a entrar en su merkasako.» (jbh { f1 the host, f2 the seconds to
        /// answer }), unless he has an invitation waiting (the host is told, 1/477), is fighting
        /// (1/554) or is under level 10 (1/555), each told as the client's own sentence.
        /// </summary>
        public static async Task InviteAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? jbb = ConnectionProtocol.ReadPayload(payload, Op.Jbb);
            var host = SessionContext.State;
            if (jbb == null) return;
            long target = 0;
            foreach (var field in ProtoMessage.Parse(jbb).Fields)
                if (field.FieldNumber == 1 && field.WireType == 0) target = (long)field.VarIntValue;
            var guest = target == host.CharacterId ? null : SessionRegistry.FindByCharacter(target);
            if (guest == null) return;

            var now = DateTime.UtcNow;
            if (_invitations.TryGetValue(target, out var waiting) && waiting.Until > now)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(Warning, AlreadyInvited, guest.State.CharacterName, target.ToString())));
                return;
            }
            if (guest.State.IsInFight || guest.State.CharacterLevel < MinimumLevel)
            {
                await guest.SendAsync(ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(Warning,
                    guest.State.IsInFight ? TooBusy : GuestTooLow, host.CharacterName)));
                return;
            }

            _invitations[target] = new Invitation(host.CharacterId, now.AddSeconds(InvitationSeconds));
            await guest.SendAsync(ConnectionProtocol.Push(Op.Jbh, Pb.New()
                .Msg(1, Person(host.CharacterId, host.CharacterName, host.CharacterLevel))
                .Var(2, InvitationSeconds).Build()));
            Console.WriteLine($"[Merkasako] {host.CharacterName} invites {guest.State.CharacterName} into his bag.");
        }

        /// <summary>
        /// The guest's answer (jas { f2 accepts }): the host learns it (jat { f1 the guest, f2 accepted },
        /// «{0} ha aceptado la invitación al merkasako.» or «se ha negado»), and an accepting guest goes
        /// into the host's bag -- unless he cannot from where he is (the host is told, 1/473).
        /// </summary>
        public static async Task AnswerInvitationAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? jas = ConnectionProtocol.ReadPayload(payload, Op.Jas);
            var guest = SessionContext.State;
            if (jas == null || !_invitations.TryRemove(guest.CharacterId, out var invitation) || invitation.Until < DateTime.UtcNow)
                return;
            bool accepted = false;
            foreach (var field in ProtoMessage.Parse(jas).Fields)
                if (field.FieldNumber == 2 && field.WireType == 0) accepted = field.VarIntValue != 0;
            // A hunted guest declines: a Merkasako is no place the hunter can follow him into.
            if (accepted && await Managers.HeadHunts.HoldsAsync(stream, Managers.HeadHunts.Hold.Merkasako)) accepted = false;

            var host = SessionRegistry.FindByCharacter(invitation.Host);
            if (host != null)
                await host.SendAsync(ConnectionProtocol.Push(Op.Jat, Pb.New()
                    .Msg(1, Person(guest.CharacterId, guest.CharacterName, guest.CharacterLevel))
                    .VarIfNotZero(2, accepted ? 1 : 0).Build()));
            if (!accepted) return;

            if (guest.IsInFight)
            {
                if (host != null)
                    await host.SendAsync(ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(Warning, CannotCome,
                        guest.CharacterName, guest.CharacterId.ToString())));
                return;
            }
            await EnterBagAsync(stream, invitation.Host, invited: true);
        }

        /// <summary>
        /// «Expulsar de mi merkasako» (jay { f2 the visitor }; PlayerMenuMaker.OnHavenbagKick): a
        /// visitor of one's bag goes back where he came from, told «{{player}} te ha expulsado de su
        /// merkasako.» (0/627), and the host «... ha sido expulsado de tu merkasako.» (0/628).
        /// </summary>
        public static async Task KickAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? jay = ConnectionProtocol.ReadPayload(payload, Op.Jay);
            var host = SessionContext.State;
            if (jay == null) return;
            long target = 0;
            foreach (var field in ProtoMessage.Parse(jay).Fields)
                if (field.FieldNumber == 2 && field.WireType == 0) target = (long)field.VarIntValue;
            var guest = SessionRegistry.FindByCharacter(target);
            if (guest?.Stream == null || guest.State.HavenBagOwnerId != host.CharacterId || !Merkasako.IsHavenBag(guest.State.MapId))
                return;

            using (SessionContext.Push(guest)) await LeaveAsync(guest.Stream);
            await guest.SendAsync(ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildSystemMessage(Kicked, host.CharacterName, host.CharacterId.ToString())));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildSystemMessage(KickedOut, guest.State.CharacterName, target.ToString())));
            Console.WriteLine($"[Merkasako] {host.CharacterName} puts {guest.State.CharacterName} out of his bag.");
        }

        /// <summary>For tests: the invitations forgotten.</summary>
        internal static void ForgetInvitations() => _invitations.Clear();

        /// <summary>
        /// «Compartir» in the bag's menu (jau { f1 friends + 2 × guild }; HavenbagManagerUI.ChangeShare):
        /// kept, and told back (jaz { f1 }, a push), as in «hacer accesible merkasako a amigos y gente
        /// del gremio»: 2 for the guild, then 3 for both. Only in one's own bag.
        /// </summary>
        public static async Task ShareAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? jau = ConnectionProtocol.ReadPayload(payload, Op.Jau);
            var state = SessionContext.State;
            if (jau == null || state.HavenBagOwnerId != 0) return;

            int shared = 0;
            foreach (var field in ProtoMessage.Parse(jau).Fields)
                if (field.FieldNumber == 1 && field.WireType == 0) shared = (int)field.VarIntValue;

            HavenBagStore.SetShared(state.CharacterId, shared);
            shared = HavenBagStore.SharedOf(state.CharacterId);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Jaz, Pb.New().VarIfNotZero(1, shared).Build()));
            Console.WriteLine($"[Merkasako] {state.CharacterName}'s bag is open to {shared} (1 friends, 2 guild).");
        }

        /// <summary>
        /// «Salir del refugio», the map panel's option inside the haven bag (jaq, empty:
        /// MapInfoUI.ExitHavenBag): the same way out as the button's second press.
        /// </summary>
        public static async Task ExitAsync(NetworkStream stream)
        {
            if (Merkasako.IsHavenBag(SessionContext.State.MapId)) await LeaveAsync(stream);
        }

        /// <summary>
        /// Back to the world, the way he came in.
        /// </summary>
        /// <remarks>
        /// The same frames as leaving a house and in the same order, which is what the capture has: after
        /// the exit jru neither jbf, nor jbu, nor jaz appear -- the three that do go on ENTERING --, only
        /// lqu, lva, lva, iom and the jss.
        ///
        /// Where he came from is kept in the database too (<see cref="HavenBagStore.EntryOf"/>): one
        /// who logged in inside was taken to the starting point, Incarnam, since the session that
        /// knew it was gone. If it is not known at all he is still sent there rather than locked in.
        ///
        /// And the roleplay context goes and comes back (kml, kmp), as on leaving a fight. The client
        /// keeps the bag's menu while its haven bag state stands, and only the roleplay entities'
        /// reset clears it (RoleplayEntitiesService.Reset); without it the menu stayed on in Incarnam.
        /// </remarks>
        private static async Task LeaveAsync(NetworkStream stream)
        {
            var state = Jondo.Unity.Server.Network.SessionContext.State;
            long dentro = state.MapId;

            long salidaMapa = state.HavenBagEntryMapId;
            int salidaCasilla = state.HavenBagEntryCell;
            if (salidaMapa == 0) (salidaMapa, salidaCasilla) = HavenBagStore.EntryOf(state.CharacterId);

            if (salidaMapa == 0 || Merkasako.IsHavenBag(salidaMapa))
            {
                salidaMapa = DatabaseManager.StartingMap;
                salidaCasilla = DatabaseManager.StartingCell;
                Console.WriteLine("[Merkasako] Where it came in from is not known: it is taken out to the starting point.");
            }

            state.MapId = salidaMapa;
            state.CellId = MapManager.GetNearestWalkableCell(salidaMapa, salidaCasilla);
            state.HavenBagEntryMapId = 0;
            state.HavenBagEntryCell = 0;
            DatabaseManager.SaveCurrentCharacter();

            // Told while he still belongs to the bag he leaves: its people see him go.
            await SessionRegistry.AnunciarMudanzaAsync(SessionContext.Current, dentro);
            state.HavenBagOwnerId = 0;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildActorLeft(state.CharacterId));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kml));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kmp));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildLoadMap(salidaMapa));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildMapClock());
            await EntryEnvironmentMessages.SendDiscoveredAsync(SessionContext.Current, salidaMapa);

            Console.WriteLine($"[Merkasako] Out of {dentro} to map {salidaMapa}, " +
                              $"cell {state.CellId}.");
        }

        /// <summary>
        /// Going to another room of one's bag (jbl { f1 room }): the room's theme is loaded, with its
        /// furniture. «Cambiar de salas» sends 1, 2, 3 and an empty one for room 0.
        /// </summary>
        /// <remarks>
        /// It was read as a theme: room 1 opened theme 1 whatever the room had.
        /// </remarks>
        public static async Task ChangeRoomAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? jbl = ConnectionProtocol.ReadPayload(payload, Op.Jbl);
            if (jbl == null || !Merkasako.IsHavenBag(SessionContext.State.MapId) || SessionContext.State.HavenBagOwnerId != 0) return;

            int room = 0;
            foreach (var field in ProtoMessage.Parse(jbl).Fields)
                if (field.FieldNumber == 1 && field.WireType == 0) room = (int)field.VarIntValue;
            if (room < 0 || room >= Merkasako.Rooms) return;

            HavenBagStore.SetCurrentRoom(SessionContext.State.CharacterId, room);
            await GoToRoomAsync(stream, room, entering: false);
        }

        /// <summary>
        /// The room's theme changed (jbt { f1 theme }), once its popup is accepted: the room takes
        /// it, is loaded again in it, and the rooms' list (jbf) follows. Every theme is everybody's
        /// here (<see cref="Merkasako.Themes"/>).
        /// </summary>
        public static async Task ChangeThemeAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? jbt = ConnectionProtocol.ReadPayload(payload, Op.Jbt);
            if (jbt == null || !Merkasako.IsHavenBag(SessionContext.State.MapId) || SessionContext.State.HavenBagOwnerId != 0) return;

            int theme = 0;
            foreach (var field in ProtoMessage.Parse(jbt).Fields)
                if (field.FieldNumber == 1 && field.WireType == 0) theme = (int)field.VarIntValue;
            if (!Merkasako.IsTheme(theme)) return;

            long who = SessionContext.State.CharacterId;
            int room = HavenBagStore.CurrentRoom(who);
            HavenBagStore.SetRoomTheme(who, room, theme);
            await GoToRoomAsync(stream, room, entering: false);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Jbf, ConnectionProtocol.BuildHavenBagRooms(HavenBagStore.RoomsOf(who))));
        }

        /// <summary>
        /// Into a room of one's bag: its theme's map, by the zaap. Entering, the rooms' list (jbf)
        /// goes behind the map's own frames, as in «ir al merkasako y volver» (frame 11).
        /// </summary>
        private static async Task GoToRoomAsync(NetworkStream stream, int room, bool entering)
        {
            long who = BagOwnerOf(SessionContext.State);
            bool own = who == SessionContext.State.CharacterId;
            int theme = HavenBagStore.ThemeOfRoom(who, room);
            long target = Merkasako.MapOfTheme(theme);
            if (target == 0)
            {
                Console.WriteLine("[Merkasako] There is no theme at all in the client's data.");
                return;
            }

            if (MapManager.GetMapInfo(target) == null)
            {
                Console.WriteLine($"[Merkasako] Map {target} is not in the world data.");
                return;
            }

            if (own) HavenBagStore.SaveTheme(who, Merkasako.ThemeOfMap(target));

            long left = SessionContext.State.MapId;
            SessionContext.State.MapId = target;

            // Next to the zaap, which is where the game leaves one on entering.
            var zaap = Merkasako.ZaapOf(target);
            SessionContext.State.CellId = MapManager.GetNearestWalkableCell(target, zaap.Cell);
            DatabaseManager.SaveCurrentCharacter();

            // Whoever stays on the map he left stops seeing him there. This was missing: going
            // into the haven bag left him standing on the street, drawn, for everybody on it.
            // And whoever is in this bag -- its owner and his visitors, nobody else's -- sees him
            // come (SessionRegistry.OnMap keeps each bag apart).
            await SessionRegistry.AnunciarMudanzaAsync(SessionContext.Current, left);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildActorLeft(SessionContext.State.CharacterId));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildLoadMap(target));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildMapClock());
            await EntryEnvironmentMessages.SendDiscoveredAsync(SessionContext.Current, target);
            if (entering)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Jbf, ConnectionProtocol.BuildHavenBagRooms(HavenBagStore.RoomsOf(who))));
            }

            Console.WriteLine($"[Merkasako] Room {room}, theme {Merkasako.ThemeOfMap(target)} -> map {target}, " +
                              $"cell {SessionContext.State.CellId} (zaap on {zaap.Cell}).");
        }

        /// <summary>
        /// Whose bag the map is, the jss's f8 (jqd), for the one standing in it: here always his
        /// own, since nobody visits another's. The HavenbagManagerUI shows its menu -- rooms,
        /// themes, furniture -- only in one's own bag (m_inMyOwnHavenbag), which it learns here.
        /// </summary>
        public static void AddToMap(Pb jss, long mapId, DatabaseManager.DbCharacter owner)
        {
            if (owner == null || !Merkasako.IsHavenBag(mapId)) return;
            long visited = SessionContext.Actual?.State.HavenBagOwnerId ?? 0;
            if (visited != 0 && visited != owner.Id) owner = DatabaseManager.GetCharacterById(visited) ?? owner;
            jss.Msg(8, ConnectionProtocol.HavenBagInfo(HavenBagStore.CurrentRoom(owner.Id), Merkasako.ThemeOfMap(mapId),
                                                       Merkasako.Rooms, owner.Id, owner.Name, owner.Level));
        }

        // ─── Furniture placement mode ───────────────────────────────────────────

        /// <summary>The client opens the management menu. An empty jbm is answered and it lets him place.</summary>
        public static async Task OpenEditorAsync(NetworkStream stream)
        {
            if (SessionContext.State.HavenBagOwnerId != 0) return;   // another's bag is not his to furnish
            SessionContext.State.IsHavenBagEditing = true;
            SessionContext.State.PendingHavenBagFurniture.Clear();

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Jbm));

            Console.WriteLine("[Merkasako] Furniture placing mode open.");
        }

        /// <summary>
        /// Accepting the room starts with a jbk: a new upload of the whole room begins, and what
        /// was gathered before is forgotten. Nothing is answered to it.
        /// </summary>
        public static void UploadStarts()
        {
            if (SessionContext.State.HavenBagOwnerId != 0) return;
            SessionContext.State.IsHavenBagEditing = true;
            SessionContext.State.PendingHavenBagFurniture.Clear();
        }

        /// <summary>
        /// A piece of the room (jbg): added to the upload, and the upload written at once, in place
        /// of the room. On accepting, the client sends jbk, the pieces, jav and jaw -- the capture's
        /// order -- but the pieces may also come behind the jav (01:16:03 of the 16/10 test). It
        /// was written on the first close, before any piece had come: the room was stored empty and
        /// the pieces that followed were dropped.
        /// </summary>
        public static void CollectFurniture(byte[] payload)
        {
            byte[]? jbg = ConnectionProtocol.ReadPayload(payload, Op.Jbg);
            if (jbg == null || !Merkasako.IsHavenBag(SessionContext.State.MapId) || SessionContext.State.HavenBagOwnerId != 0) return;

            foreach (var field in ProtoMessage.Parse(jbg).Fields)
            {
                if (field.FieldNumber != 2 || field.WireType != 2) continue;

                int cell = 0, orientation = 0;
                long typeId = 0;
                foreach (var inner in ProtoMessage.Parse(field.BytesValue).Fields)
                {
                    if (inner.WireType != 0) continue;
                    if (inner.FieldNumber == 1) cell = (int)inner.VarIntValue;
                    else if (inner.FieldNumber == 2) typeId = (long)inner.VarIntValue;
                    else if (inner.FieldNumber == 3) orientation = (int)inner.VarIntValue;
                }

                // A piece of furniture that is not in the client's catalogue is not stored: the client
                // would not know how to draw it and the room would be left with an invisible blocking gap.
                if (typeId == 0 || !Merkasako.IsFurniture(typeId)) continue;

                SessionContext.State.PendingHavenBagFurniture.Add(new HavenBagStore.Furniture
                {
                    Cell = cell,
                    TypeId = typeId,
                    Orientation = orientation,
                });
            }

            long who = SessionContext.State.CharacterId;
            int theme = Merkasako.ThemeOfMap(SessionContext.State.MapId);
            HavenBagStore.SaveFurniture(who, theme, SessionContext.State.PendingHavenBagFurniture);
            Console.WriteLine($"[Merkasako] Theme {theme}: {SessionContext.State.PendingHavenBagFurniture.Count} piece(s) of furniture saved.");
        }

        /// <summary>
        /// The client closes the menu (jav, jaw): the room is sent back to him as it is stored.
        /// It was written here, but its pieces are written as they come (<see cref="CollectFurniture"/>).
        /// </summary>
        public static async Task CloseEditorAsync(NetworkStream stream)
        {
            SessionContext.State.IsHavenBagEditing = false;

            await SendFurnitureAsync(stream);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Jba));
        }

        /// <summary>
        /// The furniture and the permissions, which the client expects after the map: the bag's owner's
        /// pieces for the room's theme, and whom he opened it to (jaz { f1 }).
        /// </summary>
        public static async Task SendFurnitureAsync(NetworkStream stream)
        {
            long owner = BagOwnerOf(SessionContext.State);
            var pieces = HavenBagStore.FurnitureOf(owner, Merkasako.ThemeOfMap(SessionContext.State.MapId));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Jbu, ConnectionProtocol.BuildHavenBagFurniture(pieces)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Jaz, Pb.New().VarIfNotZero(1, HavenBagStore.SharedOf(owner)).Build()));
        }
    }
}

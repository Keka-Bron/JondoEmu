using System;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The friends window's in-game side: mutual friends, contacts, blocked and ignored players.
    /// (The Ankama friends, the window's first section, live on the chat service: see
    /// <see cref="Network.ChatServer"/>.)
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client asks for the three lists on entering the world and whenever the window opens --
    /// <c>knm</c>, <c>kno</c>, <c>kny</c> -- and takes them whole each time (social frame
    /// <c>fhk</c>, which stores them in the social service and raises the window's refresh):
    /// </para>
    /// <code>
    ///   knm -> kqg { f1 kqm[] }       mutual friends:  kqm { 1 account, 2 league -1, 4 hours offline,
    ///                                                        5 kqk when playing, 6 state, 7 lfi }
    ///   kno -> koj { f2 kpr[] }       contacts:        kpr { 2 account, 4 lfi, 5 state 3 }
    ///   kny -> koi { f2 kph[] }       blocked:         kph { 1 lfi, 4 account }
    /// </code>
    /// <para>
    /// lfi is the account's { nickname, tag }. The real server's lists (the friends capture) have
    /// exactly these shapes: a contact's state is always 3, unknown -- only mutual friends see each
    /// other playing --, and an offline mutual friend carries the hours since he last played. kqk,
    /// the playing character, is read by the client as { 1 status, 5 level, 9 class, 11 id,
    /// 13 sex, 14 name } (the ic wrapper's constructor).
    /// </para>
    /// <para>
    /// The window's «add» popup sends, by section (AddFriendUi.SendInvite, category 3/4, 5, 6):
    /// </para>
    /// <code>
    ///   koq { 2 lgf }  add a contact   -> koh { kpr }            or kqc { 2 reason }
    ///   kok { 1 lgf }  block           -> knv { 2 knt { 1 kph }} or knv { 1 kns { 3 reason }}
    ///   kpa { 1 lgf }  ignore          -> kpy { 1 kpw { 1 kph }} or kpy { 2 kpv { 1 reason }}
    /// </code>
    /// <para>
    /// lgf names the player: f4 { f1 a character's name }, or f2/f3 { f1 nickname, f3 tag } for an
    /// account. The three answers with their success shapes are the guild capture's, where the
    /// player tried all three on the same name. The reasons are the client's list-failure enum,
    /// whose texts are ui.social.friend.addFailure*: 1 list full, 2 not found, 3 oneself,
    /// 4 already in the list, 5 in the conflicting list.
    /// </para>
    /// <para>
    /// And the removals, each with an account id (the context menu and the window's buttons):
    /// </para>
    /// <code>
    ///   kog { 2 account }  remove a contact -> kpi { 1 true, 2 lfi }
    ///   kqd { 1 account }  unblock          -> knk { 3 kni { 1 lfi }}
    ///   kpl { 1 account }  unignore         -> kox { 1 kov { 1 lfi }}
    /// </code>
    /// <para>
    /// The client takes each by the name in its lfi, out of its lists. None of them was captured; the
    /// shapes are the client's handlers'.
    /// </para>
    /// </remarks>
    public static class FriendsHandler
    {
        /// <summary>The client's list-failure reasons (knb), by their ui.social.friend.addFailure* texts.</summary>
        public const int OverQuota = 1, NotFound = 2, Egocentric = 3, AlreadyInList = 4, Conflicting = 5;

        /// <summary>A contact's state as the real server sends it: unknown, he is not a mutual friend.</summary>
        private const int UnknownState = 3;

        private const int Offline = 0, Roleplay = 1, InFight = 2;

        // ─── the lists ───────────────────────────────────────────────────────────────────────

        public static Task SendMutualAsync(NetworkStream stream)
            => Write(stream, "kqg", BuildMutual(SessionContext.Current.AccountId));

        public static Task SendContactsAsync(NetworkStream stream)
            => Write(stream, "koj", BuildContacts(SessionContext.Current.AccountId));

        public static Task SendBlockedAsync(NetworkStream stream)
            => Write(stream, "koi", BuildBlocked(SessionContext.Current.AccountId));

        /// <summary>kqg: the mutual friends, with the character of those playing.</summary>
        public static byte[] BuildMutual(long accountId)
        {
            var kqg = Pb.New();
            foreach (long other in Contacts.ContactsOf(accountId))
            {
                if (!Contacts.IsContact(other, accountId)) continue;
                kqg.Msg(1, MutualEntry(other));
            }
            return kqg.Build();
        }

        /// <summary>koj: the contacts that have not added this account back.</summary>
        public static byte[] BuildContacts(long accountId)
        {
            var koj = Pb.New();
            foreach (long other in Contacts.ContactsOf(accountId))
            {
                if (Contacts.IsContact(other, accountId)) continue;
                koj.Msg(2, ContactEntry(other));
            }
            return koj.Build();
        }

        /// <summary>koi: the blocked players, in its second list.</summary>
        public static byte[] BuildBlocked(long accountId)
        {
            var koi = Pb.New();
            foreach (long other in Contacts.BlockedOf(accountId))
                koi.Msg(2, ListedEntry(other));
            return koi.Build();
        }

        // ─── adding ──────────────────────────────────────────────────────────────────────────

        /// <summary>koq: add a contact.</summary>
        public static async Task AddContactAsync(NetworkStream stream, byte[] payload)
        {
            long me = SessionContext.Current.AccountId;
            long other = TargetOf(ConnectionProtocol.ReadPayload(payload, "koq"), 2);
            int refusal = Refusal(me, other, Contacts.IsContact(me, other),
                                  Contacts.IsBlocked(me, other) || Contacts.IsBlocked(other, me));
            if (refusal != 0)
            {
                await Write(stream, "kqc", Pb.New().Var(2, refusal).Build());
                Console.WriteLine($"[Friends] Account {me} cannot add {other} as a contact: reason {refusal}.");
                return;
            }

            Contacts.Add(me, other, Contacts.Contact);
            if (Contacts.IsContact(other, me))
            {
                // Both have added each other: the other moves from the contacts to the mutual
                // friends, on both sides.
                await RefreshFriendshipAsync(stream, me);
                await RefreshFriendshipOfAsync(other);
            }
            else
            {
                await Write(stream, "koh", Pb.New().Msg(1, ContactEntry(other)).Build());
            }
            Console.WriteLine($"[Friends] Account {me} added {other} as a contact.");
        }

        /// <summary>kok: block a player.</summary>
        public static async Task BlockAsync(NetworkStream stream, byte[] payload)
        {
            long me = SessionContext.Current.AccountId;
            long other = TargetOf(ConnectionProtocol.ReadPayload(payload, "kok"), 1);
            int refusal = Refusal(me, other, Contacts.IsBlocked(me, other), Contacts.IsContact(me, other));
            if (refusal != 0)
            {
                await Write(stream, "knv", Pb.New().Msg(1, Pb.New().Var(3, refusal)).Build());
                Console.WriteLine($"[Friends] Account {me} cannot block {other}: reason {refusal}.");
                return;
            }

            Contacts.Add(me, other, Contacts.Blocked);
            await Write(stream, "knv", Pb.New().Msg(2, Pb.New().Msg(1, ListedEntry(other))).Build());
            Console.WriteLine($"[Friends] Account {me} blocked {other}.");
        }

        /// <summary>kpa: ignore a player for the session.</summary>
        public static async Task IgnoreAsync(NetworkStream stream, byte[] payload)
        {
            long me = SessionContext.Current.AccountId;
            long other = TargetOf(ConnectionProtocol.ReadPayload(payload, "kpa"), 1);
            int refusal = Refusal(me, other, Contacts.IsIgnored(me, other), conflicting: false);
            if (refusal != 0)
            {
                await Write(stream, "kpy", Pb.New().Msg(2, Pb.New().Var(1, refusal)).Build());
                Console.WriteLine($"[Friends] Account {me} cannot ignore {other}: reason {refusal}.");
                return;
            }

            Contacts.Ignore(me, other);
            await Write(stream, "kpy", Pb.New().Msg(1, Pb.New().Msg(1, ListedEntry(other))).Build());
            Console.WriteLine($"[Friends] Account {me} ignores {other} this session.");
        }

        // ─── removing ────────────────────────────────────────────────────────────────────────

        /// <summary>kog: remove a contact (or a mutual friend, who stays with his own contact).</summary>
        public static async Task RemoveContactAsync(NetworkStream stream, byte[] payload)
        {
            long me = SessionContext.Current.AccountId;
            long other = AccountField(ConnectionProtocol.ReadPayload(payload, "kog"), 2);
            bool wereMutual = Contacts.AreMutual(me, other);
            if (!Contacts.Remove(me, other, Contacts.Contact)) return;

            await Write(stream, "kpi", Pb.New().Var(1, 1).Msg(2, Tag(other)).Build());
            if (wereMutual) await RefreshFriendshipOfAsync(other);
            Console.WriteLine($"[Friends] Account {me} removed {other} from the contacts.");
        }

        /// <summary>kqd: unblock a player.</summary>
        public static async Task UnblockAsync(NetworkStream stream, byte[] payload)
        {
            long me = SessionContext.Current.AccountId;
            long other = AccountField(ConnectionProtocol.ReadPayload(payload, "kqd"), 1);
            if (!Contacts.Remove(me, other, Contacts.Blocked)) return;

            await Write(stream, "knk", Pb.New().Msg(3, Pb.New().Msg(1, Tag(other))).Build());
            Console.WriteLine($"[Friends] Account {me} unblocked {other}.");
        }

        /// <summary>kpl: stop ignoring a player.</summary>
        public static async Task UnignoreAsync(NetworkStream stream, byte[] payload)
        {
            long me = SessionContext.Current.AccountId;
            long other = AccountField(ConnectionProtocol.ReadPayload(payload, "kpl"), 1);
            if (!Contacts.Unignore(me, other)) return;

            await Write(stream, "kox", Pb.New().Msg(1, Pb.New().Msg(1, Tag(other))).Build());
            Console.WriteLine($"[Friends] Account {me} stopped ignoring {other}.");
        }

        // ─── presence ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Someone came into the world or left it: his mutual friends who are playing get their
        /// list again, which shows him playing or not.
        /// </summary>
        public static async Task AnnouncePresenceAsync(long accountId)
        {
            if (accountId <= 0) return;
            foreach (long other in Contacts.ContactsOf(accountId))
            {
                if (!Contacts.IsContact(other, accountId)) continue;
                var session = Playing(other);
                if (session == null) continue;
                try
                {
                    await session.SendAsync(ConnectionProtocol.Push("kqg", BuildMutual(other)));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Friends] Could not tell {other} about {accountId}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// A socket of the account closed. When it was the last one, the session's ignored list goes
        /// with it; either way his mutual friends see whether he is still playing.
        /// </summary>
        public static async Task LeftAsync(long accountId)
        {
            if (accountId <= 0) return;
            if (!SessionRegistry.HasConnected(accountId)) Contacts.ForgetSession(accountId);
            try
            {
                await AnnouncePresenceAsync(accountId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Friends] Could not tell the friends of {accountId} he left: {ex.Message}");
            }
        }

        // ─── the entries ─────────────────────────────────────────────────────────────────────

        private static Pb MutualEntry(long other)
        {
            var kqm = Pb.New().Var(1, other).Var(2, -1);
            var session = Playing(other);
            if (session == null)
            {
                kqm.VarIfNotZero(4, HoursOffline(other));
            }
            else
            {
                var state = session.State;
                kqm.Msg(5, Pb.New()
                        .Msg(1, ChatStatusHandler.Of(state))
                        .Var(5, Math.Max(1, state.CharacterLevel))
                        .VarIfNotZero(9, state.Breed)
                        .Var(11, state.CharacterId)
                        .VarIfNotZero(13, state.Sex)
                        .Str(14, state.CharacterName)
                        .VarIfNotZero(12, SharesHavenBagWithFriends(state.CharacterId) ? 1 : 0))
                   .Var(6, state.IsInFight ? InFight : Roleplay);
            }
            return kqm.Msg(7, Tag(other));
        }

        /// <summary>
        /// Whether the friend's haven bag is open to his friends: kqk f12, which shows the line's
        /// «Visitar el merkasako de este personaje» (FriendsUI.SetFriendsInfo, ic +200). In the real
        /// server's lists it goes with whatever map the friend is on.
        /// </summary>
        private static bool SharesHavenBagWithFriends(long characterId)
            => (HavenBagStore.SharedOf(characterId) & HavenBagStore.SharedWithFriends) != 0;

        private static Pb ContactEntry(long other)
            => Pb.New().Var(2, other).Msg(4, Tag(other)).Var(5, UnknownState);

        private static Pb ListedEntry(long other)
            => Pb.New().Msg(1, Tag(other)).Var(4, other);

        /// <summary>lfi: the account's nickname and tag, as the authentication gives them.</summary>
        private static Pb Tag(long accountId)
        {
            var (name, tag) = HouseHandler.AccountTagOf(accountId);
            return Pb.New().Str(1, name).Str(2, tag);
        }

        /// <summary>The hours since any of the account's characters last played; zero if unknown.</summary>
        private static long HoursOffline(long accountId)
        {
            DateTimeOffset? last = null;
            foreach (var character in DatabaseManager.GetCharactersByAccountId(accountId))
            {
                if (!DateTimeOffset.TryParse(character.LastConnection, out var when)) continue;
                if (last == null || when > last) last = when;
            }
            if (last == null) return 0;
            return Math.Max(0, (long)(DateTimeOffset.Now - last.Value).TotalHours);
        }

        private static GameSession? Playing(long accountId)
            => SessionRegistry.InWorld().FirstOrDefault(s => s.AccountId == accountId);

        /// <summary>
        /// How many of the account's mutual friends are playing: the figure of «Tienes {0}
        /// contacto(s) recíproco(s) conectado(s)», told on entering the world.
        /// </summary>
        public static int MutualPlaying(long accountId)
            => accountId <= 0 ? 0
               : Contacts.ContactsOf(accountId).Count(other => Contacts.IsContact(other, accountId) && Playing(other) != null);

        // ─── reading the requests ────────────────────────────────────────────────────────────

        /// <summary>
        /// Why another account cannot go in a list, or 0 if it can: not found, oneself, already
        /// there, or in a list that excludes this one.
        /// </summary>
        private static int Refusal(long me, long other, bool already, bool conflicting)
        {
            if (other <= 0) return NotFound;
            if (other == me) return Egocentric;
            if (already) return AlreadyInList;
            if (conflicting) return Conflicting;
            return 0;
        }

        /// <summary>The account an lgf names, in the request's field <paramref name="field"/>.</summary>
        internal static long TargetOf(byte[]? request, int field)
        {
            if (request == null) return 0;
            foreach (var f in ProtoMessage.Parse(request).Fields)
            {
                if (f.FieldNumber == field && f.WireType == 2) return AccountOf(f.BytesValue);
            }
            return 0;
        }

        /// <summary>
        /// lgf: f4 { f1 character name }, or f2/f3 { f1 nickname, f2 or f3 tag } for an account. A
        /// character is looked for among those playing first, then in the base; an account by its
        /// nickname, and its tag must be the one it shows.
        /// </summary>
        internal static long AccountOf(byte[] lgf)
        {
            foreach (var f in ProtoMessage.Parse(lgf).Fields)
            {
                if (f.WireType != 2) continue;
                if (f.FieldNumber == 4)
                {
                    string name = Text(ProtoMessage.Parse(f.BytesValue), 1);
                    if (name.Length == 0) return 0;
                    var playing = SessionRegistry.FindByName(name);
                    return playing != null ? playing.AccountId : DatabaseManager.AccountIdOfCharacterName(name);
                }
                if (f.FieldNumber == 2 || f.FieldNumber == 3)
                {
                    var lgd = ProtoMessage.Parse(f.BytesValue);
                    string nickname = Text(lgd, 1);
                    string tag = Text(lgd, 3);
                    if (tag.Length == 0)
                    {
                        long number = lgd.Fields.FirstOrDefault(x => x.FieldNumber == 2 && x.WireType == 0)?.VarIntValue ?? 0;
                        if (number > 0) tag = number.ToString("D4");
                    }
                    long account = DatabaseManager.AccountIdOfNickname(nickname);
                    if (account <= 0) return 0;
                    return tag.Length == 0 || tag == GameServerProxy.BuildAccountTag(account) ? account : 0;
                }
            }
            return 0;
        }

        private static long AccountField(byte[]? request, int field)
        {
            if (request == null) return 0;
            return ProtoMessage.Parse(request).Fields
                .FirstOrDefault(f => f.FieldNumber == field && f.WireType == 0)?.VarIntValue ?? 0;
        }

        private static string Text(ProtoMessage message, int field)
        {
            var f = message.Fields.FirstOrDefault(x => x.FieldNumber == field && x.WireType == 2);
            if (f == null) return "";
            try { return Encoding.UTF8.GetString(f.BytesValue).Trim(); }
            catch (Exception) { return ""; }
        }

        // ─── sending ─────────────────────────────────────────────────────────────────────────

        /// <summary>Both lists that a friendship moves between, to this account's client.</summary>
        private static async Task RefreshFriendshipAsync(NetworkStream stream, long accountId)
        {
            await Write(stream, "kqg", BuildMutual(accountId));
            await Write(stream, "koj", BuildContacts(accountId));
        }

        /// <summary>The same for another account, if he is playing.</summary>
        private static async Task RefreshFriendshipOfAsync(long accountId)
        {
            var session = Playing(accountId);
            if (session == null) return;
            try
            {
                await session.SendAsync(ConnectionProtocol.Push("kqg", BuildMutual(accountId)));
                await session.SendAsync(ConnectionProtocol.Push("koj", BuildContacts(accountId)));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Friends] Could not refresh the lists of {accountId}: {ex.Message}");
            }
        }

        private static Task Write(NetworkStream stream, string opcode, byte[] message)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(opcode, message));
    }
}

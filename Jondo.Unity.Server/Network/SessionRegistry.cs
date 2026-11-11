using Jondo.Unity.Launcher;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// Session tickets shared between the connection server and the game server.
    ///
    /// The client makes two separate connections: on the first one it authenticates and picks a
    /// server, and gets a ticket back; on the second one it presents that ticket to say who it is,
    /// which server it selected and which language it uses. Without this registry there is no way
    /// to know which account the second connection belongs to, and the character list would end up
    /// being the same one for everybody.
    ///
    /// The ticket is single-use and expires, so that it cannot work as a permanent key.
    /// </summary>
    public static class SessionRegistry
    {
        /// <summary>Grace period for the client to close one connection and open the next one.</summary>
        private static readonly TimeSpan Expiration = TimeSpan.FromMinutes(5);

        public sealed class Ticket
        {
            public string Value { get; init; } = "";
            public long AccountId { get; init; }
            public int ServerId { get; init; }
            public string Language { get; init; } = "es";
            public DateTime Created { get; init; }
        }

        private static readonly ConcurrentDictionary<string, Ticket> _tickets
            = new ConcurrentDictionary<string, Ticket>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<Guid, GameSession> _sessions
            = new ConcurrentDictionary<Guid, GameSession>();

        private static readonly object SessionGate = new object();

        public static bool Register(GameSession session)
        {
            lock (SessionGate)
            {
                // The SERVER's capacity, which has nothing to do with how many clients a
                // person opens. Here it had the multi-account launcher's cap -eight- and that is why the whole server
                // rejected the ninth connection, whatever computer it came from.
                if (_sessions.Count >= Contract.ClientesEnTotal) return false;
                return _sessions.TryAdd(session.Id, session);
            }
        }

        /// <summary>He leaves the server: what he left pending is removed too.</summary>
        /// <remarks>
        /// The challenge offered and the place in the koliseo queue survive the socket because they live
        /// in static tables. Without this cleanup, whoever closes the client while waiting for a match leaves
        /// a ghost slot the matchmaking counts as a player, and the challenge he offered
        /// blocks the other forever.
        /// </remarks>
        public static bool Unregister(GameSession session)
        {
            long yo = session.State.CharacterId;
            if (yo != 0)
            {
                Managers.Duels.ForgetThoseOf(yo);
                Managers.KoliseoQueue.Leave(yo);
            }
            return _sessions.TryRemove(session.Id, out _);
        }

        /// <summary>
        /// Whether this account has a game session connected right now.
        /// </summary>
        /// <remarks>
        /// The launch sweep uses it so as not to release the account of someone who is
        /// playing. It is the good sign: it looks at what is connected NOW, not whether it ever
        /// was. Careful with giving it another use -this says "there is a socket", not "he is in the world".
        /// </remarks>
        public static bool HasConnected(long accountId)
        {
            if (accountId <= 0) return false;

            foreach (var pair in _sessions)
            {
                if (pair.Value.AccountId == accountId) return true;
            }

            return false;
        }

        /// <summary>
        /// Closes this account's other game sessions and waits for them to be gone. Returns how many
        /// there were.
        /// </summary>
        /// <remarks>
        /// One account plays from one connection. Nothing kept a second one out: only the launcher
        /// counted launches, and a client that came back before its old socket was noticed dead --
        /// or one not started from the launcher -- had the same character in the world twice, two
        /// states in memory that each saved over the other, and an item sold from one still in the
        /// bag of the other. The newer one is the one that stays, as on Ankama's servers: refusing it
        /// would lock a player out until a half-open socket timed out. And it waits for the old one's
        /// way out -- which saves the character -- before the new one reads anything.
        /// </remarks>
        public static async Task<int> CloseOthersOfAsync(GameSession session, TimeSpan wait)
        {
            if (session.AccountId <= 0) return 0;
            var others = _sessions.Values.Where(s => s.Id != session.Id && s.AccountId == session.AccountId).ToList();
            foreach (var other in others)
            {
                try { other.Stream?.Dispose(); } catch { }
            }

            var until = DateTime.UtcNow + wait;
            while (others.Any(o => _sessions.ContainsKey(o.Id)) && DateTime.UtcNow < until)
                await Task.Delay(50);
            return others.Count;
        }

        /// <summary>
        /// Every connected game session, as a snapshot. The one registry of who is connected: the
        /// game node used to keep a second one (SesionesVivas) that the window, the restart and the
        /// monsters' sweep read, and a failed broadcast took a session out of this one only, so the
        /// two told different stories.
        /// </summary>
        public static IReadOnlyList<GameSession> All() => _sessions.Values.ToArray();

        /// <summary>How many game sessions are connected.</summary>
        public static int Count => _sessions.Count;

        public static bool TryGet(Guid sessionId, out GameSession? session)
            => _sessions.TryGetValue(sessionId, out session);

        /// <summary>Every session with a character in the world, whatever the map.</summary>
        public static IReadOnlyList<GameSession> InWorld()
            => _sessions.Values.Where(s => s.IsInWorld && s.CharacterId != 0).ToList();

        /// <summary>
        /// One packet for every character in the world, whatever the map: what the world map
        /// shows to all, such as a prism placed or gone. Returns how many got it.
        /// </summary>
        public static async Task<int> BroadcastAsync(byte[] packet)
        {
            int told = 0;
            foreach (var target in InWorld())
            {
                try
                {
                    await target.SendAsync(packet);
                    told++;
                }
                catch (Exception ex)
                {
                    // Not taken out of the registry: a failed send is not a closed connection, and
                    // taking it out left a player still playing unreachable -- no whispers, no guild
                    // notices -- until he reconnected. His connection's own way out unregisters him.
                    Program.LogDebug($"[Sessions] Send to {target.Id} failed: {ex.Message}");
                }
            }
            return told;
        }

        public static GameSession? FindByCharacter(long characterId)
            => _sessions.Values.FirstOrDefault(s => s.CharacterId == characterId);

        /// <summary>
        /// The connected character with that name. It is needed for whispers: the client
        /// sends the NAME, not the identifier.
        ///
        /// It is looked up first by the exact name, case-insensitive. If it does not appear, it
        /// is looked up again without the decoration: the names in this base are of the kind
        /// [#KEKA-BRON#], and whoever types by hand puts "keka-bron" or "kekabron". Comparing only
        /// letters and digits keeps a whisper from getting lost over a bracket.
        /// </summary>
        public static GameSession? FindByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            var exacto = _sessions.Values.FirstOrDefault(
                s => string.Equals(s.State.CharacterName, name, StringComparison.OrdinalIgnoreCase));
            if (exacto != null) return exacto;

            string buscado = Desnudo(name);
            if (buscado.Length == 0) return null;
            return _sessions.Values.FirstOrDefault(
                s => Desnudo(s.State.CharacterName) == buscado);
        }

        /// <summary>Only the letters and digits, lowercase: [#KEKA-BRON#] -> kekabron.</summary>
        private static string Desnudo(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var limpio = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c)) limpio.Append(char.ToLowerInvariant(c));
            }
            return limpio.ToString();
        }

        /// <summary>Returns a stable snapshot; callers never enumerate a mutable registry.</summary>
        /// <remarks>
        /// A haven bag's map is every bag of its theme -- one map per theme for everybody -- and
        /// each bag is its owner's alone: asked from a session, it answers with that session's own
        /// bag, himself. Nobody visits another's here. Without it two players in two bags of one
        /// theme saw each other, the second to come in seeing the first.
        /// </remarks>
        public static IReadOnlyList<GameSession> OnMap(long mapId)
        {
            var here = _sessions.Values.Where(s => s.IsInWorld && s.MapId == mapId);
            // A haven bag is its owner's: who stands in it is who came into that one bag.
            if (Managers.Merkasako.IsHavenBag(mapId) && SessionContext.Actual is { } viewer)
            {
                long bag = Handlers.MerkasakoHandler.BagOwnerOf(viewer.State);
                here = here.Where(s => Handlers.MerkasakoHandler.BagOwnerOf(s.State) == bag);
            }

            // And a house's interior is that house's: several doors lead to one interior map, and
            // each house is its own copy of it -- who came in through another door is not there.
            if (Managers.Houses.IsInterior(mapId) && SessionContext.Actual is { } guest)
            {
                var house = Handlers.HouseHandler.HouseOf(guest.State);
                here = here.Where(s => Handlers.HouseHandler.HouseOf(s.State) == house);
            }
            return here.ToArray();
        }

        /// <summary>
        /// Whether somebody standing on the map should hear what was sent to it.
        /// </summary>
        /// <remarks>
        /// Its own method because it is the whole of the rule and it is easy to write backwards.
        /// A null sender means "the map should hear this", which is right for anything the world
        /// itself says. A number means "only the people in that fight", where 0 is the crowd
        /// walking around outside -- and 0 has to work the same as any other value, or a fighter
        /// would be shouting at the passers-by.
        /// </remarks>
        public static bool Hears(long targetFightId, long? senderFightId)
            => !senderFightId.HasValue || targetFightId == senderFightId.Value;

        /// <summary>Sends one packet to every connected character on a map.</summary>
        /// <remarks>
        /// <paramref name="fightId"/> is how two fights standing on one map are told apart, and it
        /// matters because they genuinely do stand on one map: ResolveArenaMapId gives every
        /// roleplay map a single arena, so both groups end up with the same MapId. Passing the
        /// sender's fight -- 0 when they are not fighting -- keeps map chat inside the fight it was
        /// said in. Left out, nothing is filtered, which is right for anything the whole map should
        /// hear and wrong for anything said by a person.
        /// </remarks>
        public static async Task<int> BroadcastToMapAsync(long mapId, byte[] packet,
                                                           Guid? exceptSessionId = null,
                                                           long? fightId = null,
                                                           long speakerAccountId = 0)
        {
            // Whoever blocked or ignores the speaker does not hear him.
            var targets = OnMap(mapId)
                .Where(s => !exceptSessionId.HasValue || s.Id != exceptSessionId.Value)
                .Where(s => Hears(s.State.FightId, fightId))
                .Where(s => speakerAccountId == 0 || !Managers.Contacts.Shuts(s.AccountId, speakerAccountId))
                .ToArray();

            return await SendToAsync(targets, packet);
        }

        /// <summary>Fans out already encoded bytes without acquiring recipients' gameplay locks.</summary>
        internal static async Task<int> SendToAsync(IReadOnlyList<GameSession> targets, byte[] packet)
        {
            var results = await Task.WhenAll(targets.Select(async target =>
            {
                try
                {
                    await target.SendAsync(packet);
                    return true;
                }
                catch (Exception ex)
                {
                    // Not taken out of the registry: a failed send is not a closed connection, and
                    // taking it out left a player still playing unreachable -- no whispers, no guild
                    // notices -- until he reconnected. His connection's own way out unregisters him.
                    Program.LogDebug($"[Sessions] Send to {target.Id} failed: {ex.Message}");
                    return false;
                }
            }));
            return results.Count(delivered => delivered);
        }

        /// <summary>
        /// A character's move, told to both maps: to the one he leaves, that he has gone (jsd);
        /// to the one he reaches, that he has arrived (jsn).
        ///
        /// ONE single piece was needed because the ways a character changes map
        /// are four —the edge, the zaap, the .teleport and the map command— and each one told in its own
        /// way: the edge one sent the jsd to the old map and nothing to the new one; the zaap one did not
        /// send either, it only removed the character from his own screen. Hence what
        /// was seen while playing: whoever arrived by the zaap saw those already there —he brings his whole actor
        /// list with him— but they did not see him until reloading the map. And the other
        /// way round too: whoever left by the zaap stayed as a ghost on the other's screen.
        ///
        /// The arrival notice is the same jsn already sent on entering the world, so the
        /// client does not distinguish between «has just connected» and «has just arrived»: it draws the actor.
        ///
        /// It is called AFTER moving the session's state to the new map, which is what decides
        /// whom each thing reaches: the one moving is no longer on the old map and is on the new one,
        /// and in both cases he himself is excluded, since he already learns about his own movements
        /// another way.
        /// </summary>
        public static async Task<(int seVa, int llega)> AnunciarMudanzaAsync(GameSession quien,
                                                                            long mapaQueDeja,
                                                                            int? porDonde = null)
        {
            if (quien == null || !quien.IsInWorld || quien.CharacterId <= 0) return (0, 0);

            int seVa = 0;
            if (mapaQueDeja > 0 && mapaQueDeja != quien.MapId)
            {
                seVa = await RemoveFromMapAsync(mapaQueDeja, quien.CharacterId, quien.Id, porDonde);

                // And whoever follows him, if he leads a party, learns where he landed, right
                // behind the jsd and kmu of the old map: frames 101-103 of "Grupos/con grupo
                // seguir desplazamiento del lider...". See PartyFollowHandler.
                await Handlers.PartyFollowHandler.LeaderMovedAsync(quien);
            }

            var ficha = DatabaseManager.GetCharacterById(quien.CharacterId);
            if (ficha == null) return (seVa, 0);

            int llega = await BroadcastToMapAsync(quien.MapId,
                ConnectionProtocol.Push(Op.Jsn, ConnectionProtocol.BuildActorRefreshed(
                    ficha, quien.State.CellId, quien.State.Orientation, quien.AccountId)),
                quien.Id);

            if (seVa > 0 || llega > 0)
            {
                Program.LogDebug($"[Move] {ficha.Name}: {mapaQueDeja} -> {quien.MapId}. " +
                                 $"Told {seVa} on the map left and {llega} on the one found.");
            }
            return (seVa, llega);
        }

        /// <summary>
        /// A character gone from a map, told to everyone still on it: the kmu that takes him off
        /// their screens, with the jsd before it for the ones in his party, as the captures have
        /// it (see <see cref="ConnectionProtocol.BuildActorRemoved"/>).
        /// </summary>
        public static async Task<int> RemoveFromMapAsync(long mapId, long characterId, Guid exceptSessionId,
                                                         int? porDonde = null)
        {
            var party = Managers.Parties.Of(characterId);

            var targets = OnMap(mapId).Where(s => s.Id != exceptSessionId).ToArray();
            var results = await Task.WhenAll(targets.Select(async target =>
            {
                try
                {
                    bool mate = party != null && Managers.Parties.Of(target.CharacterId) == party;
                    foreach (byte[] notice in LeaveNotices(characterId, mate, porDonde))
                        await target.SendAsync(notice);
                    return true;
                }
                catch (Exception ex)
                {
                    // Not taken out of the registry: a failed send is not a closed connection, and
                    // taking it out left a player still playing unreachable -- no whispers, no guild
                    // notices -- until he reconnected. His connection's own way out unregisters him.
                    Program.LogDebug($"[Sessions] Send to {target.Id} failed: {ex.Message}");
                    return false;
                }
            }));
            return results.Count(delivered => delivered);
        }

        /// <summary>
        /// What one person still on the map gets when a character leaves it, in order.
        /// </summary>
        /// <remarks>
        /// The jsd only goes when the character walked out, because only then is there a way out
        /// to tell. When the leader takes the zaap in "Grupos/con grupo seguir desplazamiento del
        /// lider...", the member standing next to him gets an empty imk and the kmu, and no jsd
        /// (frames 245-246). A jsd without its f3 is not "no direction" either: it is direction 0,
        /// east, which proto3 leaves off the wire -- frame 5 is the leader walking right from
        /// [1,-32] to [2,-32] -- so sending one for a jump told the party he had walked off east.
        /// </remarks>
        internal static IReadOnlyList<byte[]> LeaveNotices(long characterId, bool partyMate, int? porDonde)
        {
            byte[] removed = ConnectionProtocol.BuildActorRemoved(characterId);
            if (!partyMate || !porDonde.HasValue) return new[] { removed };
            return new[] { ConnectionProtocol.BuildActorLeft(characterId, porDonde), removed };
        }

        /// <summary>Creates a new ticket for a specific account, server and client language.</summary>
        public static Ticket Issue(long accountId, int serverId, string language = "es")
        {
            Purge();

            string normalized = NormalizeLanguage(language);

            var ticket = new Ticket
            {
                Value = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                AccountId = accountId,
                ServerId = serverId,
                Language = normalized,
                Created = DateTime.UtcNow
            };
            _tickets[ticket.Value] = ticket;
            return ticket;
        }

        /// <summary>
        /// Redeems a ticket. Returns null if it does not exist or if it has expired.
        /// It is consumed on use: a ticket is not good for two connections.
        /// </summary>
        public static Ticket? Redeem(string value)
        {
            Purge();
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!_tickets.TryRemove(value.Trim(), out var ticket)) return null;
            if (DateTime.UtcNow - ticket.Created > Expiration) return null;
            return ticket;
        }

        /// <summary>
        /// The language of the server's own texts, which are in three: es, en and fr.
        /// </summary>
        /// <remarks>
        /// The game can be started in any language the client brings (de, pt...), and a German
        /// player is better answered in English than in Spanish. Only a real language code goes
        /// to English; anything else -the "0" a client sends in its authentication request, or
        /// nothing- stays the Spanish it always was.
        /// </remarks>
        internal static string NormalizeLanguage(string? language)
        {
            string code = (language ?? "").Trim().ToLowerInvariant();
            if (code is "es" or "en" or "fr") return code;
            return code.Length is 2 or 3 && code.All(c => c is >= 'a' and <= 'z') ? "en" : "es";
        }

        /// <summary>Regression guard: the second socket keeps the language chosen on the first.</summary>
        internal static void AssertLanguageFollowsTicket()
        {
            var issued = Issue(1, 1, "fr");
            var redeemed = Redeem(issued.Value);
            if (redeemed == null || redeemed.Language != "fr")
                throw new InvalidOperationException("The session ticket lost the client language.");
        }

        private static void Purge()
        {
            DateTime now = DateTime.UtcNow;
            foreach (var pair in _tickets)
            {
                if (now - pair.Value.Created > Expiration)
                {
                    _tickets.TryRemove(pair.Key, out _);
                }
            }
        }
    }
}

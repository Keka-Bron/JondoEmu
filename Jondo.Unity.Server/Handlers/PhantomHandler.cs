using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// A soul's way (Managers.Phantoms): turned into one, told so again when he comes back into the
    /// world, and brought back by a phoenix's «Resucitar».
    /// </summary>
    /// <remarks>
    /// The client keeps the life state (kup, ees.wup) and its popups; the look is the server's to
    /// draw, sent as a kmb to him and to his map, as a mutation's is.
    /// </remarks>
    public static class PhantomHandler
    {
        /// <summary>The session each soul was last told he is one, so a login tells him again.</summary>
        private static readonly ConcurrentDictionary<long, Guid> _told = new();

        /// <summary>
        /// The character turned into a soul with his phoenix on <paramref name="phoenixMap"/>. Called
        /// before the map change that leaves him where he died, so that map's actors already draw him
        /// as one; <see cref="TellAsync"/> follows it.
        /// </summary>
        public static void Become(long characterId, long phoenixMap) => Phantoms.Become(characterId, phoenixMap);

        /// <summary>The soul told: the life state 2 with his phoenix's map (kup), and his look.</summary>
        public static async Task TellAsync(GameSession session)
        {
            long me = session.CharacterId;
            long phoenix = Phantoms.PhoenixOf(me);
            if (phoenix == 0) return;
            _told[me] = session.Id;
            await session.SendAsync(new CharacterLifeStatusEvent(CharacterLifeStatus.Phantom, phoenix));
            await LookChangedAsync(session);
            Console.WriteLine($"[Phantoms] {session.State.CharacterName} is a soul, his phoenix on map {phoenix}.");
        }

        /// <summary>A map loaded: a soul back into the world with a new session is told again.</summary>
        public static async Task OnMapLoadedAsync(GameSession session)
        {
            long me = session.CharacterId;
            if (!Phantoms.Is(me)) return;
            if (_told.TryGetValue(me, out var told) && told == session.Id) return;
            await TellAsync(session);
        }

        /// <summary>
        /// A phoenix used: false when the element is none. The living get nothing from it; a soul
        /// is alive again (kup 0, his own look), and what a quest asks of that phoenix follows.
        /// </summary>
        public static async Task<bool> TryPhoenixAsync(NetworkStream stream, long mapId, int elementId)
        {
            var element = Interactives.ByElementId(mapId, elementId);
            if (element.Gfx != Phantoms.PhoenixGfx) return false;

            long me = SessionContext.State.CharacterId;
            if (!Phantoms.Rise(me))
            {
                Console.WriteLine($"[Phantoms] {SessionContext.State.CharacterName} uses the phoenix of map {mapId} alive: nothing to do.");
                return true;
            }
            _told.TryRemove(me, out _);
            await SessionContext.Current.SendAsync(new CharacterLifeStatusEvent(CharacterLifeStatus.AliveAndKicking, 0));
            await LookChangedAsync(SessionContext.Current);
            Console.WriteLine($"[Phantoms] {SessionContext.State.CharacterName} rises at the phoenix of map {mapId}.");

            await Managers.Quests.OnInteractiveUsedAsync(SessionContext.Current, mapId, elementId);
            return true;
        }

        /// <summary>Whether the character is a soul, who cannot fight.</summary>
        public static bool CannotFight(long characterId) => Phantoms.Is(characterId);

        /// <summary>The character's look as it is now (kmb), to him and to whoever is on his map.</summary>
        private static async Task LookChangedAsync(GameSession session)
        {
            var character = DatabaseManager.GetCharacterById(session.CharacterId);
            if (character == null) return;
            var look = new ContextRefreshEntityLookEvent(CharacterLookBuilder.SemanticMapLook(character), session.CharacterId);
            using var context = SessionContext.Push(session);
            await session.SendAsync(look);
            await SessionRegistry.BroadcastToMapAsync(session.MapId, look, session.Id);
        }
    }
}

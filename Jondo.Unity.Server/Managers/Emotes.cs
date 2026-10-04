using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Emotes;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Emotes and smileys: what a character can play, playing it for the whole map, and learning
    /// new ones.
    /// </summary>
    /// <remarks>
    /// Measured on the four captures of the <c>Emotes</c> folder. The client asks with <c>khl</c>
    /// and the server answers the whole map with <c>khh</c> — or does not answer at all: every
    /// refusal in the captures is silence, whether the player is on a mount (208, "Reunificación
    /// de los seis dofus", allowOnMount 0) or asks again too soon after the last one.
    ///
    /// Sitting is emote 1 and goes the same way, "AnimEmoteSit". The captures send nothing else
    /// for it — no regeneration message follows it — so nothing else is sent here.
    /// </remarks>
    public static class Emotes
    {
        private static volatile EmoteCatalogue? _book;
        private static readonly object _loadLock = new object();

        public static EmoteCatalogue? Book => _book;

        /// <summary>Reads the emote table. Once, at startup.</summary>
        public static void Load()
        {
            if (_book != null) return;
            lock (_loadLock)
            {
                if (_book != null) return;
                var book = new EmoteCatalogue(Console.WriteLine);
                _book = book;
                if (book.Ready) Console.WriteLine($"[Actitudes] {book.Count} actitudes en el catálogo.");
            }
        }

        /// <summary>Puts this character's emotes on: the starting four and whatever was learned.</summary>
        public static void LoadFrom(long characterId)
        {
            var owned = new HashSet<int>(EmoteRules.Starting);
            owned.UnionWith(DatabaseManager.LoadEmotes(characterId));

            var state = SessionContext.State;
            state.Emotes = owned;
            state.LastEmoteUtc = DateTime.MinValue;
            state.LastSmileyUtc = DateTime.MinValue;
        }

        /// <summary>
        /// The character's emotes (khn), in place of the list the replayed block used to carry —
        /// the recorded account's 47.
        /// </summary>
        public static Task SendListAsync(NetworkStream stream)
        {
            var list = new List<int>(SessionContext.State.Emotes);
            list.Sort();
            return Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Khn, EmoteProtocol.BuildList(list)));
        }

        /// <summary>
        /// The player asks to play an emote (khl). Played for everybody on the map, or refused in
        /// silence, as the real server does.
        /// </summary>
        public static async Task PlayAsync(NetworkStream stream, int emoteId)
        {
            var state = SessionContext.State;
            if (state.IsInFight) return;

            var emote = _book?.Of(emoteId);
            bool mounted = Mounts.Ridden() != null;
            var now = DateTime.UtcNow;

            var refusal = EmoteRules.Check(emote, state.Emotes.Contains(emoteId), mounted, now, state.LastEmoteUtc);
            if (refusal != EmoteRefusal.None)
            {
                Program.LogDebug($"[Actitudes] {state.CharacterName} no puede hacer la {emoteId}: {refusal}.");
                return;
            }

            state.LastEmoteUtc = now;
            byte[] frame = ConnectionProtocol.Push(Op.Khh, EmoteProtocol.BuildPlayed(
                state.CharacterId, emoteId, SessionContext.Current.AccountId, emote!.Anim));

            // To the whole map, the player included: the captures send the khh back to whoever
            // asked, and that is what makes their own character move.
            await SessionRegistry.BroadcastToMapAsync(state.MapId, frame, fightId: 0);
        }

        /// <summary>
        /// A smiley over the head (hov), for everybody on the map. The same silence as the emotes
        /// when asked again too soon: the capture answers one hov in two when they come in pairs.
        /// </summary>
        public static async Task SmileyAsync(NetworkStream stream, int smileyId)
        {
            var state = SessionContext.State;
            if (smileyId <= 0) return;

            var now = DateTime.UtcNow;
            if (state.LastSmileyUtc != DateTime.MinValue
                && (now - state.LastSmileyUtc).TotalMilliseconds < EmoteRules.MinimumGapMs)
            {
                return;
            }

            state.LastSmileyUtc = now;
            byte[] frame = ConnectionProtocol.Push(Op.Hoc, EmoteProtocol.BuildSmiley(
                state.CharacterId, SessionContext.Current.AccountId, smileyId));
            await SessionRegistry.BroadcastToMapAsync(state.MapId, frame, fightId: state.IsInFight ? state.FightId : 0);
        }

        /// <summary>The mood smiley (hor), set or cleared, confirmed with hns as the capture does.</summary>
        public static Task MoodAsync(NetworkStream stream, int smileyId)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Hns, EmoteProtocol.BuildMood(Math.Max(0, smileyId))));

        /// <summary>
        /// A new emote for this character: kept, and announced with khi. Returns false when it was
        /// already known or does not exist.
        /// </summary>
        public static async Task<bool> LearnAsync(NetworkStream stream, int emoteId)
        {
            var state = SessionContext.State;
            if (_book?.Of(emoteId) == null) return false;
            if (!state.Emotes.Add(emoteId)) return false;

            DatabaseManager.SaveEmote(state.CharacterId, emoteId);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Khi, EmoteProtocol.BuildLearned(emoteId)));

            Console.WriteLine($"[Actitudes] {state.CharacterName} aprende la actitud {emoteId}.");
            return true;
        }
    }
}

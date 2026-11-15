using System;
using System.Collections.Concurrent;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The NPCs a character has upset into silence for a while, by a reply that sulks
    /// (<see cref="Jondo.Unity.World.Content.DialogueChoice.Sulks"/>).
    /// </summary>
    /// <remarks>
    /// Gorco at Meriana's cabin is the one who does: a wrong step at his cauldron («Toâ bête comme
    /// trool, feu pas encore assez chô ! Moâ bouder, na !») and «Si vous vous trompez dans les choix
    /// de réponse, il faudra attendre 10 minutes environ pour lui reparler» (Dofus pour les noobs,
    /// «Il faut battre le lait quand il est chaud»).
    ///
    /// Kept in memory, by character and NPC: a sulk of minutes does not need to outlive the
    /// server, and losing one on a restart only shortens the wait.
    /// </remarks>
    public static class NpcSulks
    {
        private static readonly ConcurrentDictionary<(long Character, int Npc), DateTime> _until = new();

        /// <summary>For the tests: what "now" is.</summary>
        internal static Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

        /// <summary>The NPC refuses to talk to the character for that many minutes from now.</summary>
        public static void Start(long characterId, int npcId, int minutes)
        {
            if (characterId == 0 || npcId == 0 || minutes <= 0) return;
            _until[(characterId, npcId)] = Clock().AddMinutes(minutes);
        }

        /// <summary>Whether the NPC still refuses to talk to the character.</summary>
        public static bool Sulking(long characterId, int npcId)
        {
            if (!_until.TryGetValue((characterId, npcId), out var until)) return false;
            if (Clock() < until) return true;
            _until.TryRemove((characterId, npcId), out _);
            return false;
        }

        /// <summary>For the tests and the harness: no NPC sulks any more.</summary>
        internal static void Forget() => _until.Clear();
    }
}

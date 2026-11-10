using Jondo.Unity.Launcher;
using System;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The conversations written by hand, which is the only thing about an NPC the client has never brought.
    /// </summary>
    /// <remarks>
    /// The client hands out all the sentences an NPC can say and all the replies that can
    /// be given to it, and nowhere does it say which goes with which —measured over the 6,467—. That
    /// pairing has always been Ankama's server's, so here it can only come from
    /// <c>content/npcs/dialogues.json</c>, written by a person with the editor.
    ///
    /// With nothing written nothing changes: the same as before is still done, which is to drop all the
    /// template's replies under the first sentence. It is what Snori Nairb does with his
    /// thirty-nine.
    /// </remarks>
    public static class NpcDialogues
    {
        /// <summary>
        /// Volatile because it is swapped wholesale in <see cref="Load"/> while other threads
        /// are reading it: the assignment itself is atomic, but without this a reader could see
        /// the new reference before the store behind it is finished being built.
        /// </summary>
        private static volatile ContentStore<NpcDialogueKey, NpcDialogue> _dialogues
            = new ContentStore<NpcDialogueKey, NpcDialogue>();

        private static volatile bool _loaded;
        private static readonly object _lock = new object();

        /// <summary>
        /// Reads the authored dialogues, once per run. Kept as a separate call so the server pays
        /// for it at boot, with its log line, and not on the first NPC somebody talks to.
        /// </summary>
        public static void Load() => Ensure();

        /// <summary>The authored dialogues read again with the server running (the harness's «reload»).</summary>
        public static void Reload()
        {
            lock (_lock)
            {
                Read();
                _loaded = true;
            }
        }

        private static void Ensure()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try
                {
                    Read();
                }
                finally
                {
                    _loaded = true;   // in a finally so a missing file counts as tried
                }
            }
        }

        private static void Read()
        {
            try
            {
                _dialogues = NpcDialogueContent.Load(
                    Paths.ContentFile(NpcDialogueContent.AuthoredFile), Console.WriteLine);

                Console.WriteLine(_dialogues.Count == 0
                    ? "[NPCs] There is no written dialog at all: each NPC offers all its replies at once."
                    : $"[NPCs] {_dialogues.Count} dialog(s) written by hand.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NPCs] The dialogs could not be read: {ex.Message}");
            }
        }

        /// <summary>
        /// This NPC's conversation here: the one written for this map, the one written for all, or
        /// none.
        /// </summary>
        public static NpcDialogue? For(int npcId, long mapId)
        {
            Ensure();
            return NpcDialogueContent.For(_dialogues, npcId, mapId);
        }

        /// <summary>
        /// Whether one of this NPC's conversations, on any map, has a reply that does the
        /// objective: Otomai's apprentice closes «¡Cuidado con la bestia!» in Jykel's cave, and
        /// talking to him by the pit must not do it on a hello.
        /// </summary>
        public static bool AnyHasReplyFor(int npcId, int objectiveId)
        {
            Ensure();
            foreach (var row in _dialogues.Rows)
            {
                if (row.Key.NpcId == npcId && row.Value.Value.HasReplyFor(objectiveId)) return true;
            }
            return false;
        }
    }
}

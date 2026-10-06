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

        /// <summary>How many conversations are written.</summary>
        public static int Count { get { Ensure(); return _dialogues.Count; } }

        /// <summary>
        /// Reads the authored dialogues, once per run. Kept as a separate call so the server pays
        /// for it at boot, with its log line, and not on the first NPC somebody talks to.
        /// </summary>
        public static void Load() => Ensure();

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
                    ? "[NPCs] No hay ningún diálogo escrito: cada NPC ofrece todas sus respuestas a la vez."
                    : $"[NPCs] {_dialogues.Count} diálogo(s) escritos a mano.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NPCs] Los diálogos no se han podido leer: {ex.Message}");
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
        /// The conversation used when the player talks: an authored tree, or the accept/refuse
        /// fallback when this NPC can hand a quest over and nothing is written yet.
        /// </summary>
        public static NpcDialogue? ForTalk(int npcId, long mapId)
        {
            var written = For(npcId, mapId);
            if (written != null) return written;

            var template = Npcs.TemplateOf(npcId);
            if (template == null) return null;

            return FallbackQuestDialogue.Build(npcId, mapId, template);
        }
    }
}

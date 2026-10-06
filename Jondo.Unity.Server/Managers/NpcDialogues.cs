using Jondo.Unity.Launcher;
using System;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Las conversaciones escritas a mano, que es lo único de un NPC que el cliente nunca ha traído.
    /// </summary>
    /// <remarks>
    /// El cliente reparte todas las frases que un NPC puede decir y todas las respuestas que se le
    /// pueden dar, y en ningún sitio dice cuál va con cuál —medido sobre los 6.467—. Ese
    /// emparejamiento siempre ha sido del servidor de Ankama, así que aquí sólo puede venir de
    /// <c>content/npcs/dialogues.json</c>, escrito por una persona con el editor.
    ///
    /// Sin nada escrito no cambia nada: se sigue haciendo lo de antes, que es soltar todas las
    /// respuestas de la plantilla debajo de la primera frase. Es lo que hace Snori Nairb con sus
    /// treinta y nueve.
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

        /// <summary>Cuántas conversaciones hay escritas.</summary>
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
        /// La conversación de este NPC aquí: la escrita para este mapa, la escrita para todos, o
        /// ninguna.
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

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
        private static ContentStore<NpcDialogueKey, NpcDialogue> _dialogues
            = new ContentStore<NpcDialogueKey, NpcDialogue>();

        /// <summary>Cuántas conversaciones hay escritas.</summary>
        public static int Count => _dialogues.Count;

        public static void Load()
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
            => NpcDialogueContent.For(_dialogues, npcId, mapId);
    }
}

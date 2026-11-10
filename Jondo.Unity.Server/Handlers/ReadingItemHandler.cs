using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The books and documents of the bag: effect 620, «Consultar #3», whose first number is the
    /// document to open.
    /// </summary>
    /// <remarks>
    /// Byte for byte «abrir libro el escudo de feca y leerlo»: the book (12360, document 217) used,
    /// the server answers kkt { f2: 217 } and nothing else -- the book stays --; the close button
    /// sends kla and gets an empty kld. The same kkt a notice on the map opens
    /// (<see cref="InteractiveActionHandler"/>). The books that also start a quest (effect 30) are
    /// left to whoever does that.
    /// </remarks>
    public static class ReadingItemHandler
    {
        public const int ReadEffect = 620;

        /// <summary>
        /// «Valida un objetivo»: the recipe of Incarnam's cake (16517) carries it with 9805, «Lee la
        /// receta de la torta de Incarnam», beside its 620 with document 437.
        /// </summary>
        public const int ObjectiveEffect = 30;

        /// <summary>Effects that say something of the item and do nothing on use.</summary>
        private static readonly HashSet<int> Markers = new() { 981, 983, 2818 };

        /// <summary>The document a book opens, or zero when the item is no book or does more than that.</summary>
        internal static int DocumentOf(IReadOnlyList<DatabaseManager.ItemEffectData> effects)
        {
            var read = effects.FirstOrDefault(e => e.EffectId == ReadEffect);
            if (read == null || !effects.All(e => e.EffectId == ReadEffect || e.EffectId == ObjectiveEffect
                                                  || Markers.Contains(e.EffectId))) return 0;
            return read.DiceNum;
        }

        /// <summary>A book used: its document opens. False when the item is no such thing.</summary>
        public static async Task<bool> TryReadAsync(NetworkStream stream, Equipment.Item item)
        {
            var effects = DatabaseManager.GetItemEffectsData(
                DatabaseManager.GetItemTemplatePossibleEffects(item.Template));
            int document = DocumentOf(effects);
            if (document == 0) return false;
            if (!await ItemUseHandler.CriterionHoldsAsync(stream, item)) return true;

            await OpenAsync(stream, document);

            // And the objective it validates, when the quest is in hand: reading the recipe.
            foreach (var effect in effects.Where(e => e.EffectId == ObjectiveEffect && e.Value != 0))
                await Quests.CompleteByReplyAsync(stream, 0, effect.Value);
            return true;
        }

        /// <summary>A document opened (kkt { f2 }), its close button awaited.</summary>
        internal static async Task OpenAsync(NetworkStream stream, int document)
        {
            SessionContext.State.DocumentOpen = true;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kkt, Pb.New().Var(2, document).Build()));
        }

        /// <summary>
        /// The close button (kla) while a document is open: the empty kld. False when no document is
        /// open, so the kla goes on to whatever else is.
        /// </summary>
        public static async Task<bool> CloseAsync(NetworkStream stream)
        {
            if (!SessionContext.State.DocumentOpen) return false;
            SessionContext.State.DocumentOpen = false;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kld));
            return true;
        }
    }
}

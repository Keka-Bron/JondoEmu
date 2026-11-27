using Jondo.Unity.ServerMods;
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
        public static async Task<bool> TryReadAsync(GameSession session, Equipment.Item item)
        {
            using var context = SessionContext.Push(session);
            var stream = session.Stream;
            var effects = DatabaseManager.GetItemEffectsData(
                DatabaseManager.GetItemTemplatePossibleEffects(item.Template));
            int document = DocumentOf(effects);
            if (document == 0) return false;
            if (!await ItemUseHandler.CriterionHoldsAsync(stream, item)) return true;

            await OpenAsync(stream, document, item.Template);

            // And the objective it validates, when the quest is in hand: reading the recipe.
            foreach (var effect in effects.Where(e => e.EffectId == ObjectiveEffect && e.Value != 0))
            {
                if (await Quests.CompleteByReplyAsync(session, 0, effect.Value))
                    await ShareReadingAsync(item.Template, effect.Value);
            }
            // Or the one a binding ties to reading this book (content/quests/objectives.json).
            await Quests.OnItemReadAsync(session, item.Template);
            return true;
        }

        /// <summary>
        /// The companions beside him read it with him, each his own copy: the objective done for whoever
        /// shares quests and has the book in his bag too. The cake of the recipe asks for its reading
        /// to be crafted («Qa=1637&amp;Qo&gt;9805»), and a companion who was given the recipe with the
        /// quest but had not opened it himself was told nothing came out of the ingredients.
        /// </summary>
        private static async Task ShareReadingAsync(int template, int objectiveId)
        {
            var book = Quests.Book;
            var objective = book?.Objective(objectiveId);
            var step = objective == null ? null : book!.Step(objective.StepId);
            if (step == null) return;
            await QuestSharing.ShareQuestActionAsync(async companion =>
            {
                if (Equipment.HowMany(template) > 0 && Quests.SharedObjective(step.QuestId, step.Id, objectiveId, out _))
                    await Quests.CompleteByReplyAsync(companion, step.QuestId, objectiveId);
            },
            quests: new[] { step.QuestId },
            explain: companion => Equipment.HowMany(template) > 0 ? null
                : new LagCause(QuestLagReason.MissingItems, template, 1));
        }

        /// <summary>A document opened (kkt { f2 }), its close button awaited.</summary>
        internal static async Task OpenAsync(NetworkStream stream, int document, int itemTemplate = 0)
        {
            SessionContext.State.DocumentOpen = true;
            SessionContext.State.DocumentItemTemplate = itemTemplate;
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
            SessionContext.State.DocumentItemTemplate = 0;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kld));
            return true;
        }
    }
}

using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Using an item from the bag: a double click.
    ///
    ///   client  iuu { f3: uid }
    ///
    /// It was answered by nothing, whatever the item. What is handled here is the box, the item
    /// whose effect 222 says "¿Qué hay ahí dentro?" (<see cref="LootBoxes"/>): one box is spent
    /// and what it gives goes into the bag, each with its chat line. The answer has the shape of
    /// the captures' item uses ("utilizar pocima de recuerdo"): the spent stack's new total
    /// (ivj) -- or its leaving (ium) when it was the last --, then the bag's weight (iun); and
    /// each item that arrives, as the gathering and the shops hand them over, with "Has
    /// conseguido {0} '$item{1}'" (info 21).
    ///
    /// The travel items (<see cref="TravelItemHandler"/>), the ones that give back life or energy
    /// (<see cref="RecoveryItemHandler"/>), the characteristic scrolls (<see cref="ScrollItemHandler"/>),
    /// the books (<see cref="ReadingItemHandler"/>), the attitudes (<see cref="AttitudeItemHandler"/>)
    /// the experience scrolls (<see cref="ExperienceItemHandler"/>), the candies, dragobones and
    /// elixirs (<see cref="BonusItemHandler"/>) and the spell scrolls (<see cref="SpellItemHandler"/>)
    /// are handled too. Other uses -- the pets' croquettes, a few with mixed effects -- are still not
    /// handled, and are logged.
    /// </summary>
    public static class ItemUseHandler
    {
        public static async Task UseAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? iuu = ConnectionProtocol.ReadPayload(payload, Op.Iuu);
            if (iuu == null) return;

            long uid = 0;
            foreach (var field in ProtoMessage.Parse(iuu).Fields)
                if (field.FieldNumber == 3 && field.WireType == 0) uid = field.VarIntValue;
            if (uid == 0) return;

            var item = Equipment.ByUid(uid);
            if (item == null || item.Quantity <= 0) return;

            // The alliance's potions and the guild's: changing its name or its emblem opens the editor.
            if (await AllianceHandler.TryUsePotionAsync(stream, item)) return;
            if (await GuildHandler.TryUsePotionAsync(stream, item)) return;

            // A mimisymbic: its window opens, to turn a real item into an appearance one.
            if (await MimicryHandler.TryOpenAsync(stream, item)) return;

            // A tax collector potion: one of the alliance's tax collectors where the user stands.
            if (await TaxCollectorHandler.TryPlaceAsync(stream, item)) return;

            // An alliance prism: the subarea held by the alliance, the prism where the user stands.
            if (await PrismHandler.TryPlaceAsync(stream, item)) return;
            if (await ConquestHandler.UseRolePotionAsync(stream, item)) return;

            // A travel item: the recall potion, a city's, one's home, an ankla's choice.
            if (await TravelItemHandler.TryUseAsync(stream, item)) return;

            // Bread, meat, fish, a potion or a beer: life or energy back.
            if (await RecoveryItemHandler.TryUseAsync(stream, item)) return;

            // A scroll, or a bread that raises a characteristic for good, within its bound.
            if (await ScrollItemHandler.TryUseAsync(stream, item)) return;

            // A book: its document opens, and the book stays.
            if (await ReadingItemHandler.TryReadAsync(stream, item)) return;

            // A quest item whose use is an objective (effect 30): the signalling fairy of «Aquí
            // huele a gas» makes its noise in the mine (13995) and is gone.
            var itemEffects = DatabaseManager.GetItemEffectsData(
                DatabaseManager.GetItemTemplatePossibleEffects(item.Template));
            var objectiveEffects = itemEffects
                .Where(e => e.EffectId == ReadingItemHandler.ObjectiveEffect && e.Value != 0).ToList();
            if (objectiveEffects.Count > 0)
            {
                // Its own condition first: Nistracolamus' holy waters of «Eterno descanso» call
                // their spirit out only on its map («Qo=14181&Pm=190842368»).
                if (!await CriterionHoldsAsync(stream, item)) return;
                bool any = false;
                foreach (var effect in objectiveEffects)
                    any |= await Quests.CompleteByReplyAsync(stream, 0, effect.Value);
                if (any)
                {
                    // And what it turns into, «Gana x#2 #3»: the next spirit's holy water, the sacred
                    // urn filled (209, the item in its third number), the sample of «Muestra de
                    // mejunje de test» (193, in its first). One that names the item itself keeps it:
                    // the torn page of «Guewa y Paz» is looked at (5599) and still to be shown.
                    var gains = itemEffects
                        .Where(e => e.EffectId is BonusItemHandler.GainItem or BonusItemHandler.GainItems)
                        .Select(e => (Template: e.EffectId == BonusItemHandler.GainItem ? e.DiceNum : e.Value,
                                      Count: Math.Max(1, e.DiceSide)))
                        .Where(g => g.Template > 0).ToList();
                    bool kept = gains.RemoveAll(g => g.Template == item.Template) > 0;
                    if (!kept) await Equipment.TakeAsync(stream, item.Template, 1);
                    foreach (var (template, count) in gains)
                    {
                        if (!await WorkshopHandler.GiveAsync(stream, template, count)) continue;
                        await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                            ConnectionProtocol.BuildSystemMessage(InfoMessages.ItemGained,
                                count.ToString(), template.ToString())));
                    }
                }
                return;
            }

            // A name or sex change potion: the change waits for the next connection.
            if (await RemodelHandler.TryUseAsync(stream, item)) return;

            // A face, a body or a pose for the appearance window: unlocked for the account.
            if (await AppearanceUnlockItemHandler.TryUseAsync(stream, item)) return;

            // An attitude scroll or potion: the attitude learnt.
            if (await AttitudeItemHandler.TryUseAsync(stream, item)) return;

            // An experience scroll, the character's or a job's.
            if (await ExperienceItemHandler.TryUseAsync(stream, item)) return;

            // A candy, a dragobone, an elixir: an alteration, or a boost food in its slot.
            if (await BonusItemHandler.TryUseAsync(stream, item)) return;

            // A spell scroll: a special spell learnt.
            if (await SpellItemHandler.TryUseAsync(stream, item)) return;

            // Effect 30 validates a quest's objective: the treasure chests' «Encuentra y abre el
            // cofre del día» and 315 other items. A box does it on being opened; an item that only
            // does that does it and stays.
            var objectives = QuestObjectivesOf(item.Template);
            if (LootBoxes.BoxOf(item.Template) is not { } box)
            {
                if (objectives.Count > 0 && await TickQuestObjectivesAsync(stream, objectives)) return;
                Console.WriteLine($"[Items] Using item {item.Template} (uid {uid}) is not handled yet.");
                return;
            }

            long character = SessionContext.State.CharacterId;
            if (!await SpendOneAsync(stream, uid)) return;

            var given = LootBoxes.Open(box.Group, box.Draws, Random.Shared);
            foreach (var (gid, quantity) in given)
            {
                if (!await WorkshopHandler.GiveAsync(stream, gid, quantity)) continue;
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildSystemMessage(InfoMessages.ItemGained, quantity.ToString(), gid.ToString())));
            }

            // The recobotín boxes give a job's experience besides: 614 beside their 222, the farmer's
            // or the alchemist's, 190 to 500 points.
            foreach (var effect in DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(item.Template)))
            {
                if (effect.EffectId != ExperienceItemHandler.JobExperienceEffect || effect.DiceSide <= 0 || effect.Value <= 0) continue;
                if (!JobManager.TryGet(effect.DiceSide, out _)) continue;
                await WorkshopHandler.GiveJobExperienceAsync(stream, effect.DiceSide, effect.Value, bonus: false);
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildSystemMessage(InfoMessages.JobExperienceGained, effect.Value.ToString(),
                                                          effect.DiceSide.ToString())));
            }

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Iun,
                ConnectionProtocol.BuildPods(0, 1000 + 5L * SessionContext.State.TotalStrength)));

            await TickQuestObjectivesAsync(stream, objectives);

            Console.WriteLine($"[Items] {character} opens a {item.Template} (group {box.Group.Id}): " +
                              string.Join(", ", given.ConvertAll(g => $"{g.Quantity} x {g.ItemId}")) + ".");
        }

        /// <summary>«Valida el objetivo de misión»: the effect an item validates an objective with, the objective its value.</summary>
        public const int QuestObjectiveEffect = 30;

        /// <summary>The objectives an item's effect 30 validates.</summary>
        internal static List<int> QuestObjectivesOf(int template)
        {
            var found = new List<int>();
            foreach (var effect in DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(template)))
                if (effect.EffectId == QuestObjectiveEffect && effect.Value > 0) found.Add((int)effect.Value);
            return found;
        }

        /// <summary>
        /// Each objective ticked for the quest in hand that stands at its step. False when none was.
        /// </summary>
        internal static async Task<bool> TickQuestObjectivesAsync(NetworkStream stream, IEnumerable<int> objectives)
        {
            var log = Quests.Log;
            var book = Quests.Book;
            if (log == null || book == null) return false;
            bool ticked = false;
            foreach (int id in objectives)
            {
                var objective = book.Objective(id);
                var step = objective == null ? null : book.Step(objective.StepId);
                if (step == null || !log.Active(step.QuestId) || log.Run(step.QuestId)?.StepId != step.Id) continue;
                await Quests.TickAsync(stream, step.QuestId, id);
                ticked = true;
            }
            return ticked;
        }

        /// <summary>
        /// One of a stack spent, as the captures' item uses tell it: the stack's new total (ivj), or
        /// its leaving (ium) when it was the last, and then the bag's weight (iun). Returns false if
        /// there was nothing to spend.
        /// </summary>
        /// <remarks>
        /// The weight is not decoration. Without it -- the prism and the tax collector spent their
        /// item with the ium alone -- the client took the item out of its inventory and the open bag
        /// kept drawing it until it was closed and opened again. Destroying an item, which never
        /// had that trouble, sends the same two.
        /// </remarks>
        internal static async Task<bool> SpendOneAsync(NetworkStream stream, long uid, bool weigh = true)
        {
            var item = Equipment.ByUid(uid);
            if (item == null || item.Quantity <= 0) return false;
            int left = item.Quantity - 1;
            if (!DatabaseManager.DestroyCharacterItem(SessionContext.State.CharacterId, uid, 1)) return false;
            Equipment.Remove(uid, 1);
            await Achievements.CountAsync(stream, "EI", item.Template);   // «Dulces de Nawidad»: each one used
            await WriteAsync(stream, left > 0
                ? ConnectionProtocol.Push(Op.Ivj, ConnectionProtocol.BuildItemQuantity(uid, left))
                : ConnectionProtocol.Push(Op.Ium, ConnectionProtocol.BuildItemGone(uid)));
            if (weigh) await WeighAsync(stream);
            return true;
        }

        /// <summary>
        /// Whether the user meets the item's criterion; when not, he is told why -- «Logro necesario»
        /// for an achievement, «Tus características no te lo permiten.» for the characteristics, as
        /// the real server answers an ankla and a scroll, «No cumples los requisitos.» otherwise --
        /// and nothing is spent (<see cref="ItemCriteria.RefusalOf"/>).
        /// </summary>
        internal static async Task<bool> CriterionHoldsAsync(NetworkStream stream, Equipment.Item item)
        {
            // In the client's words, which the criterion reader speaks: «Qo=X» is objective X in hand
            // -- Nistracolamus' holy water «Qo=14181&Pm=190842368», the crunchy carrot of «Guewa y
            // Paz» «Qa=920&Qo=5604» --, «Qo!X» not in hand.
            string criterion = DatabaseManager.GetItemTemplateCriterion(item.Template);
            if (ItemCriteria.Holds(criterion)) return true;

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, ItemCriteria.RefusalOf(criterion))));
            Console.WriteLine($"[Items] {SessionContext.State.CharacterName} cannot use item {item.Template}: «{criterion}».");
            return false;
        }

        /// <summary>
        /// The bag's weight (iun), for whoever spent with <c>weigh: false</c> to put something in
        /// between, as the Trool fair's potion does: ium, its kub, then the iun.
        /// </summary>
        internal static Task WeighAsync(NetworkStream stream)
            => WriteAsync(stream, ConnectionProtocol.Push(Op.Iun,
                ConnectionProtocol.BuildPods(0, 1000 + 5L * SessionContext.State.TotalStrength)));

        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);
    }
}

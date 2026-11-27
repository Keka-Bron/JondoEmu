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
        public static async Task UseAsync(GameSession session, byte[] payload)
        {
            using var context = SessionContext.Push(session);
            byte[]? iuu = ConnectionProtocol.ReadPayload(payload, Op.Iuu);
            if (iuu == null) return;

            long uid = 0;
            foreach (var field in ProtoMessage.Parse(iuu).Fields)
                if (field.FieldNumber == 3 && field.WireType == 0) uid = field.VarIntValue;
            if (uid == 0) return;

            await UseOneAsync(session, uid);
        }

        /// <summary>
        /// «Utiliser plusieurs», the item's menu: ivs { f1 uid, f2 how many }, measured on the 44
        /// purses of 10000 kamas opened at once, which nothing answered. Each one is used as a
        /// double click uses it, until the count is reached or the stack is gone; one use that
        /// spends nothing -- a book read, an item refused -- stops it, so it is not done again.
        /// </summary>
        public static async Task UseSeveralAsync(GameSession session, byte[] payload)
        {
            using var context = SessionContext.Push(session);
            byte[]? ivs = ConnectionProtocol.ReadPayload(payload, Op.Ivs);
            if (ivs == null) return;

            long uid = 0, count = 0;
            foreach (var field in ProtoMessage.Parse(ivs).Fields)
            {
                if (field.WireType != 0) continue;
                if (field.FieldNumber == 1) uid = field.VarIntValue;
                else if (field.FieldNumber == 2) count = field.VarIntValue;
            }
            if (uid == 0 || count <= 0) return;

            int used = 0;
            for (; used < count; used++)
            {
                int before = Equipment.ByUid(uid)?.Quantity ?? 0;
                if (before <= 0) break;
                await UseOneAsync(session, uid);
                if ((Equipment.ByUid(uid)?.Quantity ?? 0) >= before) break;
            }
            Console.WriteLine($"[Items] {SessionContext.State.CharacterName} uses {used} of {count} asked of uid {uid}.");
        }

        private static async Task UseOneAsync(GameSession session, long uid)
        {
            var stream = session.Stream;
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
            if (await ReadingItemHandler.TryReadAsync(session, item)) return;

            // A compass: a flag on the world map where its map reference is, and the compass stays.
            if (await CompassItemHandler.TryUseAsync(stream, item)) return;

            // An item that starts a quest (effect 34, the quest in its value): the vibrating feather
            // of «Un gros cube, un p'ti cube» (555) is double-clicked to take the quest, and stays in
            // the bag until it is handed over.
            if (await TryStartQuestAsync(session, item)) return;

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
                    any |= await Quests.CompleteByReplyAsync(session, 0, effect.Value);
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
                        // A follower walks behind the character instead of going to the bag: the
                        // Pikosonda of «Prestigio y contaminación» turns into the Pikosonda activada
                        // (type 32), «Vous obtenez alors la Pikosonde en personnage suiveur».
                        bool handed = Managers.QuestFollowers.IsFollower(template)
                            ? await Managers.QuestFollowers.GiveAsync(stream, template, count)
                            : await WorkshopHandler.GiveAsync(stream, template, count);
                        if (!handed) continue;
                        await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                            ConnectionProtocol.BuildSystemMessage(InfoMessages.ItemGained,
                                count.ToString(), template.ToString())));
                    }

                    // And where it takes the character (826 or 1155, a map reference of the client's
                    // data): the fermented bamboo milk of «El despertar de Pandala» (23043, reference
                    // 379) knocks one out into the spirit's room, «vous vous réveillez dans une pièce
                    // où vous pouvez voir un esprit».
                    var journey = itemEffects.FirstOrDefault(e => e.EffectId is TravelItemHandler.MapReferenceEffect
                                                                      or TravelItemHandler.OtherMapReferenceEffect);
                    if (journey != null && Managers.TravelData.TryMapReference(journey.Value, out long toMap, out int toCell))
                        await TeleportHandler.ToMapAsync(stream, toMap, toCell);

                    // And the fight it opens (905: the monster in its second number, its grade in the
                    // first): the «Muestra de mejunje» of «El remedio milagroso» (15140) brings out
                    // Deyek (3639), «Un combat se lance alors contre Déjé».
                    var fightEffect = itemEffects.FirstOrDefault(e => e.EffectId == FightEffect && e.DiceSide > 0);
                    if (fightEffect != null && journey == null
                        && MobSpawnManager.ComposeOffMap(new[] { (fightEffect.DiceSide, Math.Max(1, fightEffect.DiceNum)) }) is { } foes)
                        await FightHandler.InitiateFightFromMobCollision(stream, foes, SessionContext.State.MapId);
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
            if (await ExperienceItemHandler.TryUseAsync(session, item)) return;

            // A candy, a dragobone, an elixir: an alteration, or a boost food in its slot.
            if (await BonusItemHandler.TryUseAsync(stream, item)) return;

            // A spell scroll: a special spell learnt.
            if (await SpellItemHandler.TryUseAsync(stream, item)) return;

            // A purse of kamas (effect 194, ┬½+#1 kamas┬╗): the Bourses de 1000, 5000 and 10000 Kamas.
            // Nothing handled it, and using one did nothing at all. A box with kamas besides (the
            // chest of the bandits of Cania: their three scales and 1510 kamas) opens as a box below.
            var box = LootBoxes.BoxOf(item.Template);
            var purse = itemEffects.FirstOrDefault(e => e.EffectId == KamasEffect);
            long KamasOf(DatabaseManager.ItemEffectData p) => p.DiceSide > p.DiceNum ? Random.Shared.Next(p.DiceNum, p.DiceSide + 1)
                                              : p.DiceNum > 0 ? p.DiceNum : p.Value;
            if (purse != null && box == null)
            {
                long kamas = KamasOf(purse);
                if (kamas <= 0 || !await CriterionHoldsAsync(stream, item)) return;
                if (!await SpendOneAsync(session, uid)) return;
                await Managers.CharacterRewards.GiveKamasAsync(stream, kamas);
                Console.WriteLine($"[Items] Purse {item.Template} (uid {uid}) opened: {kamas} kamas.");
                return;
            }

            // Effect 30 validates a quest's objective: the treasure chests' «Encuentra y abre el
            // cofre del día» and 315 other items. A box does it on being opened; an item that only
            // does that does it and stays.
            var objectives = QuestObjectivesOf(item.Template);
            if (box is not { } opened)
            {
                if (objectives.Count > 0 && await TickQuestObjectivesAsync(session, objectives)) return;
                Console.WriteLine($"[Items] Using item {item.Template} (uid {uid}) is not handled yet.");
                return;
            }

            long character = SessionContext.State.CharacterId;
            if (!await SpendOneAsync(session, uid)) return;

            var given = LootBoxes.Open(opened.Group, opened.Draws, Random.Shared);
            foreach (var (gid, quantity) in given)
            {
                if (!await WorkshopHandler.GiveAsync(stream, gid, quantity)) continue;
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildSystemMessage(InfoMessages.ItemGained, quantity.ToString(), gid.ToString())));
            }

            // And its kamas, when it has some.
            if (purse != null && KamasOf(purse) is > 0 and var boxKamas)
                await Managers.CharacterRewards.GiveKamasAsync(stream, boxKamas);

            // The recobotín boxes give a job's experience besides: 614 beside their 222, the farmer's
            // or the alchemist's, 190 to 500 points.
            foreach (var effect in DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(item.Template)))
            {
                if (effect.EffectId != ExperienceItemHandler.JobExperienceEffect || effect.DiceSide <= 0 || effect.Value <= 0) continue;
                if (!JobManager.TryGet(effect.DiceSide, out _)) continue;
                await WorkshopHandler.GiveJobExperienceAsync(session, effect.DiceSide, effect.Value, bonus: false);
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildSystemMessage(InfoMessages.JobExperienceGained, effect.Value.ToString(),
                                                          effect.DiceSide.ToString())));
            }

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Iun,
                ConnectionProtocol.BuildPods(ConnectionProtocol.Carried(Jondo.Unity.Server.Network.SessionContext.State), ConnectionProtocol.Capacity(Jondo.Unity.Server.Network.SessionContext.State))));

            await TickQuestObjectivesAsync(session, objectives);

            Console.WriteLine($"[Items] {character} opens a {item.Template} (group {opened.Group.Id}): " +
                              string.Join(", ", given.ConvertAll(g => $"{g.Quantity} x {g.ItemId}")) + ".");
        }

        /// <summary>«Valida el objetivo de misión»: the effect an item validates an objective with, the objective its value.</summary>
        public const int QuestObjectiveEffect = 30;

        /// <summary>Effect 194, ┬½+#1{{~1~2 a }}#2 kamas┬╗: a purse's kamas.</summary>
        public const int KamasEffect = 194;

        /// <summary>Effect 905: a fight against the monster of its second number, at the grade of its first.</summary>
        public const int FightEffect = 905;

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
        internal static async Task<bool> TickQuestObjectivesAsync(GameSession session, IEnumerable<int> objectives)
        {
            using var context = SessionContext.Push(session);
            var log = Quests.Log;
            var book = Quests.Book;
            if (log == null || book == null) return false;
            bool ticked = false;
            foreach (int id in objectives)
            {
                var objective = book.Objective(id);
                var step = objective == null ? null : book.Step(objective.StepId);
                if (step == null || !log.Active(step.QuestId) || log.Run(step.QuestId)?.StepId != step.Id) continue;
                await Quests.TickAsync(session, step.QuestId, id);
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
        internal static async Task<bool> SpendOneAsync(GameSession session, long uid, bool weigh = true)
        {
            using var context = SessionContext.Push(session);
            var item = Equipment.ByUid(uid);
            if (item == null || item.Quantity <= 0) return false;
            int left = item.Quantity - 1;
            if (!DatabaseManager.DestroyCharacterItem(session.CharacterId, uid, 1)) return false;
            Equipment.Remove(uid, 1);
            await ReportSpendAsync(session, item.Template, uid, left, 1, weigh);
            return true;
        }

        internal static async Task ReportSpendAsync(GameSession session, int template, long uid, int left, int quantity, bool weigh)
        {
            for (int used = 0; used < quantity; used++)
                await Achievements.CountAsync(session, "EI", template, defer: session.Stream == null);
            await session.SendAsync(left > 0
                ? ConnectionProtocol.Push(Op.Ivj, ConnectionProtocol.BuildItemQuantity(uid, left))
                : ConnectionProtocol.Push(Op.Ium, ConnectionProtocol.BuildItemGone(uid)));
            if (weigh) await session.SendAsync(ConnectionProtocol.Push(Op.Iun,
                ConnectionProtocol.BuildPods(ConnectionProtocol.Carried(session.State), ConnectionProtocol.Capacity(session.State))));
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

        /// <summary>The client's «start a quest» effect: its value is the quest.</summary>
        internal const int StartQuestEffect = 34;

        /// <summary>The quest an item starts, or zero when it starts none.</summary>
        internal static int QuestStartedBy(IReadOnlyList<DatabaseManager.ItemEffectData> effects)
            => effects.FirstOrDefault(e => e.EffectId == StartQuestEffect && e.Value > 0)?.Value ?? 0;

        /// <summary>
        /// Whether a quest-starting item's criterion holds, its own quest's «Qc» left to the start
        /// (<see cref="Quests.StartAsync"/> asks whether the quest can be taken, with what this
        /// server can judge of it); every other term as the resolver answers it.
        /// </summary>
        internal static bool StartCriterionHolds(string criterion, int questId, Jondo.Unity.World.Content.Criterion.Resolver resolver)
        {
            if (string.IsNullOrWhiteSpace(criterion)) return true;
            return Jondo.Unity.World.Content.Criterion.Met(criterion, condition =>
                condition.Code == "Qc" && condition.Value == questId
                    ? (condition.Operator == '=' ? Jondo.Unity.World.Content.Answer.True : Jondo.Unity.World.Content.Answer.False)
                    : resolver(condition));
        }

        /// <summary>
        /// An item that starts a quest, used: the quest starts under the same conditions as from an
        /// NPC, and the item stays (the feather of 555 and the gum of 600 are handed over later).
        /// One already in hand gets info 626, as the document's button does. False when the item
        /// starts no quest.
        /// </summary>
        private static async Task<bool> TryStartQuestAsync(GameSession session, Equipment.Item item)
        {
            var stream = session.Stream!;
            int questId = QuestStartedBy(DatabaseManager.GetItemEffectsData(
                DatabaseManager.GetItemTemplatePossibleEffects(item.Template)));
            if (questId == 0) return false;

            if (Quests.Log?.Active(questId) == true)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, QuestHandler.AlreadyStarted)));
                return true;
            }

            // Its criterion: 20 of the 22 carry «Qc» for the very quest they start (the feather's
            // «Qc=555&Qa!555»), «can be started now», which the start itself judges just below --
            // and which the item criterion reader took for «finished», refusing every one of them.
            string criterion = DatabaseManager.GetItemTemplateCriterion(item.Template);
            if (!StartCriterionHolds(criterion, questId, PlayerCriteria.ResolverFor(SessionContext.State.MapId)))
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InfoMessages.RequirementsNotMet)));
                Console.WriteLine($"[Items] {SessionContext.State.CharacterName} cannot start quest {questId} with item {item.Template}: «{criterion}».");
                return true;
            }

            if (!await Quests.StartSharedAsync(session, questId, () =>
                Equipment.HowMany(item.Template) > 0
                && StartCriterionHolds(criterion, questId, PlayerCriteria.ResolverFor(SessionContext.State.MapId))))
                Console.WriteLine($"[Items] {SessionContext.State.CharacterName} cannot start quest {questId} with item {item.Template}.");
            return true;
        }

        /// <summary>
        /// The bag's weight (iun), for whoever spent with <c>weigh: false</c> to put something in
        /// between, as the Trool fair's potion does: ium, its kub, then the iun.
        /// </summary>
        internal static Task WeighAsync(NetworkStream stream)
            => WriteAsync(stream, ConnectionProtocol.Push(Op.Iun,
                ConnectionProtocol.BuildPods(ConnectionProtocol.Carried(Jondo.Unity.Server.Network.SessionContext.State), ConnectionProtocol.Capacity(Jondo.Unity.Server.Network.SessionContext.State))));

        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);
    }
}

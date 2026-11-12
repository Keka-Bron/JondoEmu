using System;
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
    /// The items that leave something on the character for a while: the candies, the dragobones and
    /// pills, the elixirs, the Trool fair's potion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two families. The ones that set an alteration carry 2892 with it as its first number, and 2893
    /// to take one away: the Trool potion sets 624 and 625, and the real server answers its use with an
    /// lzs for each, the ium, the kub and the iun (<see cref="AlterationHandler"/>). The others give an
    /// item, 193 or 209 «Gana x#2 #3»: a candy gives its boost food, which goes to the boost food's
    /// slot (<see cref="BonusSlots"/>), a bag of candies gives candies, which go to the bag.
    /// </para>
    /// <para>
    /// The candy rule, the server owner's: their effects are free of any bound, but two never add up.
    /// A candy's effect takes the place of whatever another candy left, a boost food or a «Golosina»
    /// alteration. Eating the same one again is not allowed by its own criterion -- «PO!1723», not
    /// holding the boost food it gives; «HA!586», not having the alteration --, which is what makes
    /// sense of the replacing: without it, those criteria would ask for nothing a full slot did not.
    /// </para>
    /// <para>
    /// A mutation given goes through <see cref="MutationHandler"/>; a curse, a roleplay buff or a
    /// follower that changes the look draws the character again (<see cref="SlotLookHandler"/>). A
    /// boost food that lasts minutes instead of fights -- the perlequines' «Sabor de perlequín», 60 --
    /// is placed with the moment it ends and goes on the clock (AlterationHandler.TickAsync). Not
    /// handled, and logged: anything mixed with other effects.
    /// </para>
    /// </remarks>
    public static class BonusItemHandler
    {
        public const int GainAlteration = 2892;
        public const int LoseAlteration = 2893;

        /// <summary>«Gana x#2 #3» with the item in its first number.</summary>
        public const int GainItem = 193;

        /// <summary>«Gana x#2 #3» with the count in the second and the item in the third.</summary>
        public const int GainItems = 209;

        /// <summary>Effects that say something of the item and do nothing on use.</summary>
        private static readonly HashSet<int> Markers = new() { 981, 983, 2818 };

        /// <summary>
        /// Effects with no text that only go with a quest item's use, beside what it gives: 350 names
        /// NPCs (the precious ore shard of «La luz al final de las alcantarillas» names Temerius and
        /// the «Haz de luz», 6626), 630 a number of the client's own (1437 for the shard). Nothing for
        /// the server to keep.
        /// </summary>
        private static readonly HashSet<int> Shown = new() { 350, 630 };

        /// <summary>
        /// The item used up (192), with the item in its first number. Naming the item itself, it is
        /// what spending it already does: the shard gone as the next one comes.
        /// </summary>
        private const int UsedUp = 192;

        /// <summary>
        /// «Subir/Bajar de una montura»: the potions that change the look, the Trool fair's «Pócima
        /// astromágica de bworky» (13351) among them, carry it beside what they give. A rider gets off first.
        /// </summary>
        private const int Dismounts = 949;

        /// <summary>Whether an item sets or takes alterations, and nothing else.</summary>
        internal static bool GivesAlterations(IReadOnlyList<DatabaseManager.ItemEffectData> effects)
            => effects.Any(e => e.EffectId is GainAlteration or LoseAlteration)
               && effects.All(e => e.EffectId is GainAlteration or LoseAlteration || Markers.Contains(e.EffectId));

        /// <summary>
        /// The items an item gives and how many of each, or null when it does anything else besides.
        /// <paramref name="own"/> is the item's own template, for a 192 that uses it up.
        /// </summary>
        internal static List<(int Template, int Quantity)>? GivenItems(IReadOnlyList<DatabaseManager.ItemEffectData> effects,
                                                                     int own = 0)
        {
            if (!effects.Any(e => e.EffectId is GainItem or GainItems)) return null;
            var given = new List<(int, int)>();
            foreach (var effect in effects)
            {
                if (Markers.Contains(effect.EffectId) || Shown.Contains(effect.EffectId)) continue;
                if (effect.EffectId == UsedUp && own != 0 && effect.DiceNum == own) continue;
                if (effect.EffectId == Dismounts) continue;
                if (effect.EffectId is not (GainItem or GainItems)) return null;
                int template = effect.EffectId == GainItem ? effect.DiceNum : effect.Value;
                if (template > 0) given.Add((template, Math.Max(1, effect.DiceSide)));
            }
            return given;
        }

        /// <summary>Such an item used: false when the item is no such thing, or one this server does not place.</summary>
        public static async Task<bool> TryUseAsync(NetworkStream stream, Equipment.Item item)
        {
            var effects = DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(item.Template));
            if (GivesAlterations(effects)) return await SetAlterationsAsync(stream, item, effects);

            var given = GivenItems(effects, item.Template);
            if (given == null || given.Count == 0) return false;
            foreach (var (template, _) in given)
            {
                string? why = WhyNotPlaced(template);
                if (why == null) continue;
                Console.WriteLine($"[Items] Using item {item.Template} is not handled: item {template} {why}.");
                return false;
            }
            return await GiveItemsAsync(stream, item, given);
        }

        /// <summary>
        /// Why an item given cannot go where it belongs, or null when it can: a bonus slot this server
        /// does not place.
        /// </summary>
        private static string? WhyNotPlaced(int template)
        {
            int type = DatabaseManager.GetItemTemplateType(template);
            if (!BonusSlots.IsSlotType(type)) return null;
            if (BonusSlots.SlotsFor(type).Count == 0) return $"goes to a bonus slot that changes the look (type {type})";
            if (!DatabaseManager.TryGetItemTemplateEffects(template, out _)) return "is not in the data";
            return null;
        }

        /// <summary>The alterations set and taken: lzs for each, then the item spent, the kub and the weight.</summary>
        private static async Task<bool> SetAlterationsAsync(NetworkStream stream, Equipment.Item item,
                                                            IReadOnlyList<DatabaseManager.ItemEffectData> effects)
        {
            var me = SessionContext.State;
            if (me.IsInFight || me.FightId != 0) return true;
            if (!await ItemUseHandler.CriterionHoldsAsync(stream, item)) return true;

            var gains = effects.Where(e => e.EffectId == GainAlteration).Select(e => e.DiceNum)
                               .Where(id => AlterationCatalogue.Of(id) != null).ToList();
            var losses = effects.Where(e => e.EffectId == LoseAlteration).Select(e => e.DiceNum).ToList();

            if (gains.Any(AlterationCatalogue.IsCandy)) await AlterationHandler.DropCandyEffectsAsync(stream);
            foreach (int alteration in losses) await AlterationHandler.LoseAsync(stream, alteration);
            foreach (int alteration in gains) await AlterationHandler.GainAsync(stream, alteration);

            if (!await ItemUseHandler.SpendOneAsync(stream, item.Uid, weigh: false)) return true;
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));
            await ItemUseHandler.WeighAsync(stream);

            Console.WriteLine($"[Items] {me.CharacterName} uses {item.Template}: alteration(s) " +
                              string.Join(", ", gains) + (losses.Count > 0 ? $", lost {string.Join(", ", losses)}" : "") + ".");
            return true;
        }

        /// <summary>
        /// The items given: a boost food or a blessing to its slot, anything else to the bag with its
        /// «Has conseguido» line; the item spent first, the kub when a slot was filled, the weight.
        /// </summary>
        private static async Task<bool> GiveItemsAsync(NetworkStream stream, Equipment.Item item,
                                                       IReadOnlyList<(int Template, int Quantity)> given)
        {
            var me = SessionContext.State;
            if (me.IsInFight || me.FightId != 0) return true;
            if (!await ItemUseHandler.CriterionHoldsAsync(stream, item)) return true;

            var typed = given.Select(g => (g.Template, g.Quantity, Type: DatabaseManager.GetItemTemplateType(g.Template)))
                             .ToList();
            if (!await ItemUseHandler.SpendOneAsync(stream, item.Uid, weigh: false)) return true;
            if (typed.Any(g => g.Type == BonusSlots.BoostFoodType)) await AlterationHandler.DropCandyEffectsAsync(stream);
            if (DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(item.Template))
                               .Any(e => e.EffectId == Dismounts))
                await GetOffAsync(stream);

            bool placed = false, redraw = false;
            foreach (var (template, quantity, type) in typed)
            {
                if (type == BonusSlots.MutationType)
                {
                    await MutationHandler.GiveAsync(stream, template);
                    continue;
                }
                var slots = BonusSlots.SlotsFor(type);
                if (slots.Count > 0)
                {
                    var put = await PlaceAsync(stream, template, slots);
                    if (put != null) placed = true;
                    if (put != null && SlotLooks.ChangesTheLook(put.Effects)) redraw = true;
                    continue;
                }
                if (!await WorkshopHandler.GiveAsync(stream, template, quantity)) continue;
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildSystemMessage(InfoMessages.ItemGained, quantity.ToString(), template.ToString())));
            }

            if (placed) await WriteAsync(stream, ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));
            await ItemUseHandler.WeighAsync(stream);
            if (redraw) await SlotLookHandler.RedrawAsync(stream);

            Console.WriteLine($"[Items] {me.CharacterName} uses {item.Template}: " +
                              string.Join(", ", typed.Select(g => $"{g.Quantity} x {g.Template}")) + ".");
            return true;
        }

        /// <summary>
        /// An item put in its bonus slot, with its fights as left, total, total: the first free one of
        /// its slots, or the one whose item has the fewest fights left. Told with an iua into the slot,
        /// as the orbalantir's mutation arrives.
        /// </summary>
        internal static async Task<Equipment.Item?> PlaceAsync(NetworkStream stream, int template, IReadOnlyList<int> slots)
        {
            long character = SessionContext.State.CharacterId;
            if (!DatabaseManager.TryGetItemTemplateEffects(template, out string json)) return null;

            int slot = slots.FirstOrDefault(s => !Equipment.All.Any(i => i.Position == s), -1);
            if (slot < 0)
            {
                var weakest = Equipment.All.Where(i => slots.Contains(i.Position))
                                           .OrderBy(i => FightsLeftOf(i)).First();
                slot = weakest.Position;
                await AlterationHandler.RemoveSlotItemAsync(stream, character, weakest);
            }

            var created = Equipment.Create(template, 1, BonusSlots.Fresh(Equipment.ParseEffects(json)));
            if (created == null) return null;
            DatabaseManager.SaveItemPosition(created.Uid, slot, character);
            Equipment.Move(created.Uid, slot);
            var legacy = GameState.GetInventoryItem(created.Uid);
            if (legacy != null) legacy.Position = slot;
            Equipment.RememberWorn(created.Uid, slot, created.Effects);
            SlotLooks.Current();

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Iua, AlterationProtocol.BuildSlotItemPlaced(created)));
            return created;
        }

        private static long FightsLeftOf(Equipment.Item item)
            => item.Effects.Where(e => e.Effect == AlterationCatalogue.FightsLeft).Select(e => e.Value).DefaultIfEmpty(0).Max();

        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);

        /// <summary>The mount ridden, back to the bag: what «Subir/Bajar de una montura» asks of a rider.</summary>
        private static async Task GetOffAsync(NetworkStream stream)
        {
            var ridden = Equipment.All.FirstOrDefault(i => i.Position == Mounts.Slot && Mounts.Of(i.Template) != null);
            if (ridden == null) return;
            await EquipmentHandler.MoveItemAsync(stream, ridden.Uid, Equipment.Bag);
        }
    }
}

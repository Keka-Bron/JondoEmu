using System;
using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The inventory's bonus slots, past the worn ones: what a candy, a blessing or a mutation puts
    /// there, and how it counts down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// They are the slots the item types say: 20 the mutation (type 27), 21 the boost food (28, «Alimento
    /// boost», what a candy gives), 22 and 23 the blessings (29), 24 and 25 the curses (30), 26 the roleplay
    /// buff (31) and 27 the follower (32). Measured: the orbalantir's mutations arrive in 20 (iua) and the
    /// tournament character carries a blessing in 22, +1 AP, +1 MP and +1 range for 666 fights.
    /// </para>
    /// <para>
    /// The fights they last are effect 811 with three numbers -- left, total, total --: the blessing
    /// enters the world at 665 of 666 and goes down by one at the end of every fight (iuj), and the
    /// mutation at 1 of 1 is taken away at the end of its fight (ium).
    /// </para>
    /// <para>
    /// The boost food and the blessings are placed as they are: they do nothing but add
    /// characteristics. The mutation goes through MutationHandler, which tells the client its spells,
    /// look and sheet. The curses (the «Pergamino del desánimo», the «Botella de Zampaburg»...), the
    /// roleplay buffs (the camouflages, the disguise potions) and the followers (the balloons, the
    /// golden larva) are placed too, and the character is drawn again when they change his look
    /// (SlotLooks, SlotLookHandler).
    /// </para>
    /// </remarks>
    public static class BonusSlots
    {
        public const int First = 20;
        public const int Last = 27;

        /// <summary>The boost food's slot, the candies'.</summary>
        public const int BoostFood = 21;

        public const int MutationType = 27;
        public const int BoostFoodType = 28;
        public const int BlessingType = 29;
        public const int CurseType = 30;
        public const int RoleplayBuffType = 31;
        public const int FollowerType = 32;

        /// <summary>The item types that go to a bonus slot, placed or not.</summary>
        private static readonly HashSet<int> SlotTypes = new() { 27, 28, 29, 30, 31, 32 };

        public static bool IsBonusSlot(int position) => position >= First && position <= Last;

        /// <summary>Whether an item of this type belongs in a bonus slot.</summary>
        public static bool IsSlotType(int itemType) => SlotTypes.Contains(itemType);

        /// <summary>The slots this server places an item type in; empty for the ones it does not.</summary>
        public static IReadOnlyList<int> SlotsFor(int itemType) => itemType switch
        {
            MutationType => new[] { Mutations.Slot },
            BoostFoodType => new[] { BoostFood },
            BlessingType => new[] { 22, 23 },
            CurseType => new[] { 24, 25 },
            RoleplayBuffType => new[] { SlotLooks.RoleplayBuffer },
            FollowerType => new[] { SlotLooks.Follower },
            _ => Array.Empty<int>(),
        };

        /// <summary>
        /// An item's effects as it is placed: its fights and victories as left, total and total, the way
        /// the captured blessing carries them, and its minutes -- «Expiración: #1», the perlequines' 60 --
        /// as the moment it ends, in seconds, the date the effect shows. The template has the count alone.
        /// </summary>
        public static List<Equipment.ItemEffect> Fresh(IEnumerable<Equipment.ItemEffect> effects, DateTime? nowUtc = null)
            => effects.Select(e => IsCount(e.Effect)
                    ? Counted(e.Effect, Alterations.CountOf(e), Alterations.CountOf(e))
                    : e.Effect == AlterationCatalogue.Expiry
                        ? new Equipment.ItemEffect(e.Effect,
                              new DateTimeOffset(nowUtc ?? DateTime.UtcNow).ToUnixTimeSeconds() + Alterations.CountOf(e) * 60L, 0, 0)
                        : e)
                .ToList();

        /// <summary>When a placed item ends, in seconds, or zero when it lasts no time.</summary>
        public static long EndsAt(IEnumerable<Equipment.ItemEffect> effects)
            => effects.Where(e => e.Effect == AlterationCatalogue.Expiry && e.Value >= EffectFields.DatedSince)
                      .Select(e => e.Value).DefaultIfEmpty(0).Min();

        /// <summary>
        /// One fight over for an item: its fights one fewer, and its victories if it was won. Null when
        /// nothing in it counts; Spent when it has reached zero and goes.
        /// </summary>
        /// <remarks>
        /// A follower (type 32) counts only the fights lost: nearly every one carries «1 combate
        /// restante», yet escorts fight beside the character and walk on after a win, and are gone
        /// only when the fight is lost (Poly in «Le palais de Sassine», Dofus pour les noobs).
        /// </remarks>
        public static (List<Equipment.ItemEffect> Effects, bool Spent)? CountDown(IReadOnlyList<Equipment.ItemEffect> effects,
                                                                                bool won, int type = 0)
        {
            if (won && type == FollowerType) return null;
            bool counted = false, spent = false;
            var after = new List<Equipment.ItemEffect>();
            foreach (var effect in effects)
            {
                bool counts = effect.Effect == AlterationCatalogue.FightsLeft
                              || (won && effect.Effect == AlterationCatalogue.VictoriesLeft);
                if (!counts || effect.Value <= 0)
                {
                    after.Add(effect);
                    continue;
                }
                counted = true;
                long left = effect.Value - 1;
                if (left <= 0) spent = true;
                long total = Math.Max(effect.DiceNum, effect.Value);
                after.Add(Counted(effect.Effect, left, total));
            }
            return counted ? (after, spent) : null;
        }

        private static bool IsCount(int effect)
            => effect is AlterationCatalogue.FightsLeft or AlterationCatalogue.VictoriesLeft;

        /// <summary>A count as the item carries it: f6 { f1 left, f2 total, f3 total }.</summary>
        private static Equipment.ItemEffect Counted(int effect, long left, long total)
            => new(effect, left, total, total);
    }
}

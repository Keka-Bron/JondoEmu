using System;
using System.Collections.Generic;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// What the alterations window shows, byte for byte against the captures: the alterations (lzs
    /// set, lzo lost, lzp all of them) and the items in the bonus slots (iua placed, iuj changed),
    /// which the same window lists beside them -- «data provided should be an ItemWrapper or an
    /// Alteration», says the client.
    /// </summary>
    /// <remarks>
    /// An alteration's entry (lzv):
    /// <code>
    ///   f1 since (ms)   f2 the alteration   f3 each effect   f4 its kind   f5 until (ms) or the count left
    /// </code>
    /// The effects are the item effects' entries (lng), with what is live in them: 811 and 817 as
    /// f6 { f1 left, f2 total, f3 total } -- the Trool potion's 624 fresh «320608011001180158ab06», at
    /// the end of the race «32041001180158ab06» --, 2833 as the date it ends, f2 { f1 day, f2 year,
    /// f3 month, f4 minute, f5 hour } in Paris time with Dofus's years (2026 is 656), and the ones
    /// that count minutes (2867 «Experiencia x#3% (#1 minuto)») as f6 { f1 the percentage, f2 the
    /// minutes left }: the oracle set at 01:13:12 that ends at 03:13:00 goes out with 119. The rest
    /// carry their one number in f4: 174's 50 initiative, 148's creature 2850.
    /// <para>
    /// The order is not the data's. Three of the four captured come in the order opposite to the
    /// client's list -- 625, the oracle's 859, the gladiators' 572 -- and the Trool's 624 in the same
    /// one; the reversed order is kept, which is right three times out of four and only changes the
    /// order of the lines in the tooltip.
    /// </para>
    /// </remarks>
    public static class AlterationProtocol
    {
        /// <summary>Dofus's calendar runs 1370 years behind: the captures' September 2026 is 656.</summary>
        public const int DofusYearOffset = AlterationMessages.DofusYearOffset;

        /// <summary>An alteration set (lzs): f1 its entry.</summary>
        public static byte[] BuildSet(Alterations.Active active, DateTime nowUtc)
            => Pb.New().Msg(1, Entry(active, nowUtc)).Build();

        /// <summary>An alteration lost (lzo): f1 its entry, as it stood.</summary>
        public static byte[] BuildLost(Alterations.Active active, DateTime nowUtc)
            => Pb.New().Msg(1, Entry(active, nowUtc)).Build();

        /// <summary>Every alteration (lzp): f1 each entry. Empty when there are none, as the captures send it.</summary>
        public static byte[] BuildList(IEnumerable<Alterations.Active> list, DateTime nowUtc)
        {
            var lzp = Pb.New();
            foreach (var active in list) lzp.Msg(1, Entry(active, nowUtc));
            return lzp.Build();
        }

        /// <summary>One alteration's entry (lzv).</summary>
        internal static Pb Entry(Alterations.Active active, DateTime nowUtc)
        {
            var alteration = AlterationMessages.FromCatalogue.Entry(active, nowUtc);
            var entry = Pb.New().Var(1, alteration.CreationTime).Var(2, alteration.AlterationId);
            foreach (var effect in alteration.Effects) entry.Msg(3, Effect(effect));
            int kind = alteration.ExpirationType switch
            {
                AlterationExpirationType.Infinite => 1,
                AlterationExpirationType.Date => 2,
                AlterationExpirationType.FightCount => 3,
                AlterationExpirationType.FightsWonCount => 4,
                _ => throw new ArgumentOutOfRangeException(nameof(active)),
            };
            entry.Var(4, kind);
            switch (alteration.ExpirationType)
            {
                case AlterationExpirationType.Date:
                    entry.Var(5, alteration.ExpirationValue);
                    break;
                case AlterationExpirationType.FightCount:
                case AlterationExpirationType.FightsWonCount:
                    entry.VarIfNotZero(5, alteration.ExpirationValue);
                    break;
            }
            return entry;
        }

        /// <summary>Legacy gain/loss serialization of the shared semantic effect.</summary>
        private static Pb Effect(ObjectEffect effect)
        {
            var line = Pb.New();
            switch (effect.EffectCase)
            {
                case ObjectEffectCaseNone:
                    break;
                case ObjectEffectCaseValueInt integer:
                    line.Var(4, integer.Value);
                    break;
                case ObjectEffectCaseValueDate date:
                    line.Msg(2, Date(date.Value));
                    break;
                case ObjectEffectCaseValueDice dice:
                    var values = Pb.New().VarIfNotZero(1, dice.Value.Const);
                    if (effect.Action is AlterationCatalogue.FightsLeft or AlterationCatalogue.VictoriesLeft)
                        values.Var(2, dice.Value.Num).Var(3, dice.Value.Side);
                    else
                        values.VarIfNotZero(2, dice.Value.Num).VarIfNotZero(3, dice.Value.Side);
                    line.Msg(6, values);
                    break;
                default:
                    throw new ArgumentException("Unsupported alteration effect value.", nameof(effect));
            }
            return line.Var(11, effect.Action);
        }

        /// <summary>
        /// A date as the effects carry it: f1 day, f2 Dofus's year, f3 month, f4 minute, f5 hour, in
        /// the game's own time, which is Paris's.
        /// </summary>
        internal static Pb Date(long unixMs)
            => Date(AlterationMessages.Date(unixMs));

        private static Pb Date(ObjectEffectDateValue when)
            => Pb.New()
                .VarIfNotZero(1, when.Day)
                .Var(2, when.Year)
                .Var(3, when.Month)
                .VarIfNotZero(4, when.Minute)
                .VarIfNotZero(5, when.Hour);

        /// <summary>
        /// An item placed in a bonus slot (iua): f3 { f1 the slot, f5 the item }, as the orbalantir's
        /// mutation arrives in slot 20.
        /// </summary>
        public static byte[] BuildSlotItemPlaced(Equipment.Item item)
            => Pb.New().Msg(3, SlotItem(item)).Build();

        /// <summary>
        /// An item that has changed (iuj): f1 { f1 the slot, f5 the item }. The real server sends one at
        /// the end of every fight for the blessing in slot 22, its fights left one fewer each time.
        /// </summary>
        public static byte[] BuildSlotItemChanged(Equipment.Item item)
            => Pb.New().Msg(1, SlotItem(item)).Build();

        private static Pb SlotItem(Equipment.Item item)
            => Pb.New().VarIfNotZero(1, item.Position)
                       .Msg(5, ConnectionProtocol.ItemBody(item.Template, item.Effects, item.Quantity, item.Uid));
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What a quest step shared with heroes takes, gathered from the whole team's bags: each one who
    /// follows the step is handed what he lacks out of the others' spare, so that one lemonade
    /// bought five times is not five trades (Keka-Bron/JondoEmu#206).
    /// </summary>
    /// <remarks>
    /// <para>
    /// All or nobody. When the team has not enough for every one who follows, nothing moves and
    /// nobody does the step, the master neither: «the master goes on, a hero stays behind» is what
    /// #206 is about. The master is told how many are missing; turning a hero's quest sharing off
    /// lets him go on without him.
    /// </para>
    /// <para>
    /// The team is the master and the heroes who would do the step with him: sharing quests, on his
    /// map, out of a fight, and with the objective theirs to do (<c>follows</c>). Only what is
    /// handed over and gone is pooled -- an item asked to be shown is still each one's own -- and
    /// only what lies in a bag and is not bound to its character (981): the account's own items go
    /// between the account's characters as its bank would let them.
    /// </para>
    /// </remarks>
    internal static class HeroQuestItems
    {
        /// <summary>
        /// Hands each one of the team what he lacks of <paramref name="asked"/> -- each one's
        /// share -- from the others' spare, before the master does the step. False, with the master
        /// told and nothing moved, when the team has not enough for all; true when there is no team.
        /// </summary>
        internal static async Task<bool> PoolAsync(GameSession master, IEnumerable<(int Item, int Count)> asked,
                                                   Func<GameSession, bool> follows)
        {
            var wants = asked.Where(a => a.Item != 0)
                             .GroupBy(a => a.Item)
                             .Select(g => (Item: g.Key, Count: g.Sum(a => Math.Max(1, a.Count))))
                             .ToList();
            if (wants.Count == 0 || !Heroes.On || master.CharacterId == 0
                || Heroes.AsHero(master.CharacterId) != null || master.State.IsInFight) return true;

            var team = new List<GameSession> { master };
            foreach (var hero in Heroes.Of(master.CharacterId))
            {
                if (!Heroes.SharesQuests(hero.CharacterId) || hero.MasterId != master.CharacterId) continue;
                if (hero.Session.MapId != master.MapId || hero.Session.State.IsInFight) continue;
                bool joins;
                using (SessionContext.Push(hero.Session)) joins = follows(hero.Session);
                if (joins) team.Add(hero.Session);
            }
            if (team.Count == 1) return true;

            int missing = 0;
            foreach (var (item, count) in wants)
            {
                int lacking = team.Sum(s => Math.Max(0, count - Has(s, item)));
                int spare = team.Sum(s => Spare(s, item, count));
                if (lacking > spare)
                {
                    missing += lacking - spare;
                    Console.WriteLine($"[Heroes] {master.CharacterId}'s team of {team.Count} lacks " +
                                      $"{lacking - spare}x{item} for a quest step; nobody does it.");
                }
            }
            if (missing > 0)
            {
                // Silent when the master lacks his own share as well: that is the step not done for
                // want of the items, as without heroes, and a hello says nothing about it.
                if (wants.All(w => Has(master, w.Item) >= w.Count)) await TellShortAsync(master, missing);
                return false;
            }

            foreach (var (item, count) in wants)
            {
                foreach (var needy in team)
                {
                    int lacking = count - Has(needy, item);
                    foreach (var giver in team)
                    {
                        if (lacking <= 0) break;
                        if (giver == needy) continue;
                        lacking -= await MoveAsync(giver, needy, item, Math.Min(lacking, Spare(giver, item, count)));
                    }
                    if (lacking > 0)
                    {
                        Console.WriteLine($"[Heroes] {needy.CharacterId} still lacks {lacking}x{item} after the team's bags.");
                        return false;
                    }
                }
            }
            return true;
        }

        private static int Has(GameSession session, int item)
            => CommissionHandler.As(session, () => Equipment.HowMany(item));

        /// <summary>What one can give of an item and still keep his own share: his bag's unbound stacks.</summary>
        private static int Spare(GameSession session, int item, int keeps)
            => CommissionHandler.As(session, () =>
            {
                int over = Equipment.HowMany(item) - keeps;
                return over <= 0 ? 0 : Math.Min(over, Movable(item).Sum(i => i.Quantity));
            });

        private static List<Equipment.Item> Movable(int item)
            => Equipment.All.Where(i => i.Template == item && i.Position == Equipment.Bag
                                        && !ItemBinding.CharacterOnly(i.Effects)).ToList();

        /// <summary>Up to <paramref name="count"/> of an item from one bag into the other, as a trade moves them.</summary>
        private static async Task<int> MoveAsync(GameSession giver, GameSession receiver, int item, int count)
        {
            int moved = 0;
            if (count <= 0) return 0;
            var stacks = CommissionHandler.As(giver, () => Movable(item));
            foreach (var stack in stacks)
            {
                int quantity = Math.Min(count - moved, stack.Quantity);
                if (quantity <= 0) break;
                var given = CommissionHandler.As(giver, () => TradeHandler.Give(stack.Uid, quantity, ownAccount: true));
                if (given == null) continue;
                await giver.SendAsync(ConnectionProtocol.Push(given.Value.Opcode, given.Value.Body));
                await giver.SendAsync(ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                    InfoMessages.ItemLost, quantity.ToString(CultureInfo.InvariantCulture),
                    item.ToString(CultureInfo.InvariantCulture))));

                var received = CommissionHandler.As(receiver, () => TradeHandler.Receive(given.Value.Gid, quantity, given.Value.Effects));
                if (received == null)
                {
                    Console.WriteLine($"[Heroes] {quantity}x{item} out of {giver.CharacterId}'s bag did not reach {receiver.CharacterId}'s.");
                    break;
                }
                await receiver.SendAsync(ConnectionProtocol.Push(received.Value.Opcode, received.Value.Body));
                moved += quantity;
            }
            if (moved > 0)
                Console.WriteLine($"[Heroes] {moved}x{item} from {giver.CharacterId} to {receiver.CharacterId} for a quest step.");
            return moved;
        }

        /// <summary>The master's warning that nobody went on, in his language, red as the one of a hero behind.</summary>
        private static async Task TellShortAsync(GameSession master, int missing)
        {
            if (!master.IsInWorld) return;
            string text = CommandTexts.Get(master.State.Language, "hero.quest.teamshort", missing);
            try
            {
                await master.SendAsync(ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildNotice($"<color=#FF0000><b>{text}</b></color>")));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Heroes] {master.CharacterId} could not be told his team lacks items: {ex.Message}");
            }
        }
    }
}

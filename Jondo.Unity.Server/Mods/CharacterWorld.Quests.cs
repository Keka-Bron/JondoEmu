using System.Globalization;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.ServerMods;

namespace Jondo.Unity.Server.Mods;

internal sealed partial class CharacterWorld
{
    public bool NpcPresent(int npcId) => Npcs.OnMap(SessionContext.State.MapId).Any(spawn => spawn.NpcId == npcId);
    public int QuestProgress(ICharacterSession session, int questId) => Quests.ProgressOf((GameSession)session, questId);
    public int QuestStep(ICharacterSession session, int questId) => ((GameSession)session).State.Quests?.Run(questId)?.StepId ?? 0;
    public string LanguageOf(ICharacterSession session) => ((GameSession)session).State.Language;

    public Task DeliverQuestAchievementsAsync(ICharacterSession master, ICharacterSession follower)
        => ((GameSession)follower).State.AchievementsPending.Count > 0
            ? Achievements.ForCompanionAsync((GameSession)master, (GameSession)follower) : Task.CompletedTask;

    public Task SendQuestNoticeAsync(ICharacterSession recipient, string text)
        => ((GameSession)recipient).SendAsync(ConnectionProtocol.Push(Op.Lqn,
            ConnectionProtocol.BuildNotice($"<color=#FF0000><b>{text}</b></color>")));

    public int QuestItemCount(ICharacterSession session, int item)
        => CommissionHandler.As((GameSession)session, () => Equipment.HowMany(item));

    /// <summary>What one can give of an item and still keep his own share: his bag's unbound stacks.</summary>
    public int SpareQuestItems(ICharacterSession session, int item, int keeps)
        => CommissionHandler.As((GameSession)session, () =>
        {
            int over = Equipment.HowMany(item) - keeps;
            return over <= 0 ? 0 : Math.Min(over, Movable(item).Sum(i => i.Quantity));
        });

    private static List<Equipment.Item> Movable(int item)
        => Equipment.All.Where(i => i.Template == item && i.Position == Equipment.Bag
                                    && !ItemBinding.CharacterOnly(i.Effects)).ToList();

    /// <summary>Up to <paramref name="count"/> of an item from one bag into the other, as a trade moves them.</summary>
    public async Task<int> MoveQuestItemsAsync(ICharacterSession source, ICharacterSession destination, int item, int count)
    {
        var giver = (GameSession)source;
        var receiver = (GameSession)destination;
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
                Console.WriteLine($"[Character world] {quantity}x{item} out of {giver.CharacterId}'s bag did not reach {receiver.CharacterId}'s.");
                break;
            }
            await receiver.SendAsync(ConnectionProtocol.Push(received.Value.Opcode, received.Value.Body));
            moved += quantity;
        }
        if (moved > 0)
            Console.WriteLine($"[Character world] {moved}x{item} from {giver.CharacterId} to {receiver.CharacterId} for a quest step.");
        return moved;
    }

}

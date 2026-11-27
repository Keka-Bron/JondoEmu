using System.Collections.Concurrent;
using Jondo.Unity.ServerMods;

namespace Heroes.Server;

internal sealed partial class HeroGroups
{
    public bool CanShareNpc(int npcId) => questWorld.NpcPresent(npcId);

    public bool CanShareQuestReply(int npcId, QuestReplyEffects effects, Func<bool> isAvailable)
        => CanShareNpc(npcId) && effects == QuestReplyEffects.None && isAvailable();

    // The quests each hero is behind in, keyed by hero and quest.
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<int, QuestLag>> _questLags = new();

    /// <summary>
    /// The quests a hero is still behind his master in. Those he has caught up with since --
    /// by doing the step himself, or his master having gone back -- are let go as they are read.
    /// </summary>
    public IReadOnlyList<QuestLag> LagsOf(ICharacterSession master, long heroId)
    {
        if (!_questLags.TryGetValue(heroId, out var lags) || lags.IsEmpty) return Array.Empty<QuestLag>();
        var hero = Find(heroId);
        if (hero == null || hero.ControllerId != master.CharacterId) return Array.Empty<QuestLag>();
        foreach (int quest in lags.Keys)
        {
            if (questWorld.QuestProgress(hero.Session, quest) >= questWorld.QuestProgress(master, quest))
                lags.TryRemove(quest, out _);
        }
        return lags.Values.OrderBy(l => l.QuestId).ToList();
    }

    public async Task ShareQuestActionAsync(ICharacterSession master, Func<ICharacterSession, Task> apply,
        Func<ICompanion, bool>? participates = null, IReadOnlyCollection<int>? quests = null,
        Func<ICharacterSession, LagCause?>? explain = null)
    {
        long masterId = master.CharacterId;
        if (masterId == 0 || Find(masterId) != null) return;

        foreach (var hero in Of(masterId))
        {
            if (!SharesQuests(hero.CharacterId) || Find(hero.CharacterId)?.Session != hero.Session || hero.ControllerId != masterId)
                continue;
            bool eligible = participates?.Invoke(hero)
                ?? (hero.Session.Position.MapId == master.Position.MapId && !hero.Session.IsInFight && !master.IsInFight);

            LagCause? cause = null;
            if (!eligible)
            {
                cause = new LagCause(participates != null ? QuestLagReason.NotInTheFight
                    : hero.Session.Position.MapId != master.Position.MapId ? QuestLagReason.Elsewhere
                    : QuestLagReason.InFight);
            }
            else
            {
                try
                {
                    await apply(hero.Session);
                    await questWorld.DeliverQuestAchievementsAsync(master, hero.Session);
                }
                catch (Exception ex)
                {
                    log($"Quest sharing failed for {hero.CharacterId}: {ex.Message}");
                }
            }

            if (quests == null || quests.Count == 0) continue;
            try { await NoteLagsAsync(master, hero, quests, cause, explain); }
            catch (Exception ex)
            {
                log($"Could not tell whether {hero.CharacterId} followed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Each quest the hero is now behind his master in, kept with its reason; the master is told
    /// once a quest, when the hero falls behind in it, not again at each step after.
    /// </summary>
    private async Task NoteLagsAsync(ICharacterSession master, ICompanion hero, IReadOnlyCollection<int> quests,
        LagCause? cause, Func<ICharacterSession, LagCause?>? explain)
    {
        var lags = _questLags.GetOrAdd(hero.CharacterId, _ => new ConcurrentDictionary<int, QuestLag>());
        foreach (int quest in quests.Distinct())
        {
            int heroAt = questWorld.QuestProgress(hero.Session, quest);
            int masterAt = questWorld.QuestProgress(master, quest);
            if (heroAt >= masterAt)
            {
                lags.TryRemove(quest, out _);
                continue;
            }
            if (lags.ContainsKey(quest)) continue;

            LagCause? explained = null;
            if (cause == null && explain != null)
                explained = explain(hero.Session);
            LagCause why = cause ?? explained
                ?? new LagCause(heroAt < 0 ? QuestLagReason.NotStarted : QuestLagReason.Behind);
            int step = questWorld.QuestStep(master, quest);
            lags[quest] = new QuestLag(quest, step, why.Reason, why.ItemId, why.ItemCount);
            await TellMasterAsync(master, hero, why);
            log($"{hero.CharacterId} stayed behind {master.CharacterId} in quest {quest}: {why.Reason}" +
                              (why.ItemId != 0 ? $" ({why.ItemCount}x{why.ItemId})" : ""));
        }
    }

    /// <summary>The master's warning, in his language: who stayed behind and why.</summary>
    private async Task TellMasterAsync(ICharacterSession master, ICompanion hero, LagCause why)
    {
        if (!master.IsInWorld) return;
        string language = questWorld.LanguageOf(master);
        string name = hero.Session.CharacterName is { Length: > 0 } n ? n : hero.CharacterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string reason = why.Reason == QuestLagReason.MissingItems
            ? HeroQuestTexts.Get(language, "hero.quest.why.missingitems", why.ItemCount)
            : HeroQuestTexts.Get(language, "hero.quest.why." + why.Reason.ToString().ToLowerInvariant());
        string text = HeroQuestTexts.Get(language, "hero.quest.behind", name, reason);
        try
        {
            await questWorld.SendQuestNoticeAsync(master, text);
        }
        catch (Exception ex)
        {
            log($"{master.CharacterId} could not be told about {hero.CharacterId}: {ex.Message}");
        }
    }

    public async Task<bool> PoolQuestItemsAsync(ICharacterSession master, IEnumerable<(int Item, int Count)> asked,
        Func<ICharacterSession, bool> follows)
    {
        var wants = asked.Where(a => a.Item != 0)
                         .GroupBy(a => a.Item)
                         .Select(g => (Item: g.Key, Count: g.Sum(a => Math.Max(1, a.Count))))
                         .ToList();
        if (wants.Count == 0 || master.CharacterId == 0
            || Find(master.CharacterId) != null || master.IsInFight) return true;

        var team = new List<ICharacterSession> { master };
        foreach (var hero in Of(master.CharacterId))
        {
            if (!SharesQuests(hero.CharacterId) || hero.ControllerId != master.CharacterId) continue;
            if (hero.Session.Position.MapId != master.Position.MapId || hero.Session.IsInFight) continue;
            if (follows(hero.Session)) team.Add(hero.Session);
        }
        if (team.Count == 1) return true;

        int missing = 0;
        foreach (var (item, count) in wants)
        {
            int lacking = team.Sum(s => Math.Max(0, count - questWorld.QuestItemCount(s, item)));
            int spare = team.Sum(s => questWorld.SpareQuestItems(s, item, count));
            if (lacking > spare)
            {
                missing += lacking - spare;
                log($"{master.CharacterId}'s team of {team.Count} lacks " +
                                  $"{lacking - spare}x{item} for a quest step; nobody does it.");
            }
        }
        if (missing > 0)
        {
            // Silent when the master lacks his own share as well: that is the step not done for
            // want of the items, as without heroes, and a hello says nothing about it.
            if (wants.All(w => questWorld.QuestItemCount(master, w.Item) >= w.Count)) await TellShortAsync(master, missing);
            return false;
        }

        foreach (var (item, count) in wants)
        {
            foreach (var needy in team)
            {
                int lacking = count - questWorld.QuestItemCount(needy, item);
                foreach (var giver in team)
                {
                    if (lacking <= 0) break;
                    if (giver == needy) continue;
                    lacking -= await questWorld.MoveQuestItemsAsync(giver, needy, item, Math.Min(lacking, questWorld.SpareQuestItems(giver, item, count)));
                }
                if (lacking > 0)
                {
                    log($"{needy.CharacterId} still lacks {lacking}x{item} after the team's bags.");
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>The master's warning that nobody went on, in his language, red as the one of a hero behind.</summary>
    private async Task TellShortAsync(ICharacterSession master, int missing)
    {
        if (!master.IsInWorld) return;
        string text = HeroQuestTexts.Get(questWorld.LanguageOf(master), "hero.quest.teamshort", missing);
        try
        {
            await questWorld.SendQuestNoticeAsync(master, text);
        }
        catch (Exception ex)
        {
            log($"{master.CharacterId} could not be told his team lacks items: {ex.Message}");
        }
    }
}

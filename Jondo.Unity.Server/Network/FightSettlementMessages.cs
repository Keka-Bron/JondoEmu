using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Network;

internal static class FightSettlementMessages
{
    internal static FightResultListEntry Row(FightProtocol.FightResult result)
    {
        FighterListEntryCase who = new FighterListEntryCaseNone();
        if (result.Companion) who = new FighterListEntryCaseMutant(new(result.Level));
        else if (result.Level > 0)
        {
            var xp = new FightExperienceData(result.XpGained, ExperienceTable.NextLevelFloor(result.Level), 1,
                true, result.Xp, ExperienceTable.LevelFloor(result.Level), result.XpGained > 0);
            List<FightResultAdditionalData> additional = [new(new FightResultAdditionalDataCaseExperienceData(xp))];
            if (result.Pvp is { } pvp) additional.Add(new(new FightResultAdditionalDataCasePvpData(new(pvp.Ceiling, 0))));
            who = new FighterListEntryCaseCharacter(new(additional.ToArray(), result.Level));
        }
        return new(Loot(result.Spoils), new(result.Fighter, true, who), result.Winner ? FightOutcome.ResultVictory : FightOutcome.ResultLost);
    }

    internal static FightLoot Loot(FightProtocol.Spoils? spoils)
    {
        var items = spoils?.Items.Where(i => !Equipment.IsInvisible(i.Gid))
            .Select(i => new FightLootObject(0, i.Quantity, i.Gid)).ToArray() ?? [];
        return new(spoils?.Kamas ?? 0, items.Length == 0 ? [] : [new(items, [], 0)]);
    }

    internal static FightStatisticsEvent Statistics(long id, Team team, FightStatistics s, int enemies)
    {
        FightPointStatistics Points(int spent) => new(0, 0, 0, s.PerTurn(spent), 0, 0, 0, 0);
        var player = new FightStatisticsFighterInformation(team, id, new FightStatisticsFighterKindCasePlayer(new()));
        var mine = new FighterFightStatistics(player, new(enemies, enemies, 0), Points(s.ActionPointsSpent),
            new(s.PerTurn(s.HealsReceived), s.HealsReceived, 0), new(0, 0, s.PerTurn(s.DamageTaken), 0, 0, 0, 0, s.DamageTaken),
            new(s.ShieldsGiven, 0, s.PerTurn(s.ShieldsGiven)), Points(s.MovementPointsSpent), new(0, 0, 0),
            new(0, s.PerTurn(s.HealsGiven), 0, s.HealsGiven), new(s.TriggerDamage, s.SummonDamage,
                s.OwnDamage / (float)Math.Max(1, s.ActionPointsOnDamage), s.TotalDamage, s.PushDamage,
                s.PerTurn(s.TotalDamage), s.GlyphDamage, 0, s.DirectDamage, 0));
        return new(new Dictionary<long, FighterFightStatistics> { [id] = mine },
            new(enemies, 0, s.DamageTaken, s.HealsGiven, s.TotalDamage, s.HealsReceived, s.ShieldsGiven));
    }
}

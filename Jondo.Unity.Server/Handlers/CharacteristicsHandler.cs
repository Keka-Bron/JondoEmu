using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers;

/// <summary>Session-owned spending/reset service. Upgrade fields are target total capital paid,
/// not increments or characteristic gains; repeating a distribution is idempotent.</summary>
internal sealed class CharacteristicsHandler(Action<GameSession> save, CharacteristicsMessages messages)
{
    internal static CharacteristicsHandler Live { get; } = new(session =>
    {
        using var context = SessionContext.Push(session);
        DatabaseManager.SaveCurrentCharacter();
    }, CharacteristicsMessages.Live);

    private static bool CanChange(GameSession session)
        => session.IsAuthenticated && session.HasCharacter && session.IsInWorld
            && (!session.State.IsInFight || FightHandler.FightOf(session.CharacterId)?.State == Jondo.Unity.World.Fights.FightState.Placement);

    internal async Task<bool> SpendAsync(GameSession session, CharacterCharacteristicUpgradeRequest request)
    {
        if (!CanChange(session)) return false;
        var state = session.State;
        var targets = new (string Name, int Capital)[]
        {
            ("strength", request.Strength), ("intelligence", request.Intelligence),
            ("chance", request.Chance), ("agility", request.Agility),
            ("vitality", request.Vitality), ("wisdom", request.Wisdom),
        };
        int capital = CharacteristicRules.TotalCapitalForLevel(state.CharacterLevel);
        long asked = targets.Sum(target => (long)target.Capital);
        var values = new int[targets.Length];
        bool valid = targets.All(target => target.Capital >= 0) && asked <= capital;
        for (int i = 0; valid && i < targets.Length; i++)
            valid = BreedStatCost.TryBuy(state.Breed, targets[i].Name, targets[i].Capital, out values[i]);

        if (valid)
        {
            state.StatStrength = values[0]; state.StatIntelligence = values[1];
            state.StatChance = values[2]; state.StatAgility = values[3];
            state.StatVitality = values[4]; state.StatWisdom = values[5];
            state.CharacterRemainingPoints = capital - checked((int)asked);
            save(session);
        }
        // Refused distributions return an unchanged sheet, without charging a partial purchase.
        await AnswerAsync(session);
        if (valid && session.Stream is { } stream)
        {
            using var context = SessionContext.Push(session);
            await FightHandler.RefreshPlacementSheetAsync(stream);
        }
        return true;
    }

    internal Task<bool> ResetAsync(GameSession session)
        => SpendAsync(session, new(0, 0, 0, 0, 0, 0));

    private async Task AnswerAsync(GameSession session)
    {
        var stats = session.Combat;
        long capacity = checked(CharacteristicRules.BasePods + 5L *
            (session.State.TotalStrength + (long)stats.GetEquipBonus(Stat.Strength)) + stats.GetEquipBonus(Stat.Pods));
        await session.SendAsync(new InventoryWeightEvent(0, checked((int)capacity)));
        await messages.SendAsync(session);
    }
}

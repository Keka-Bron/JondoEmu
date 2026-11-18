using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface ICharacteristicsMessagesBackend
{
    long LevelFloor(int level);
    long NextLevelFloor(int level);
    IReadOnlyDictionary<int, long> EquipmentBonuses(GameSession session);
    IReadOnlyDictionary<int, long> AlterationBonuses(long characterId);
    MonsterGradeStats? MutationSheet(GameSession session);
    Alignments.Alignment Alignment(long characterId);
    int AlignmentQuestNumber(GameSession session);
    int Energy(long characterId);
    int MissingLife(long characterId, DateTime atUtc);
}

/// <summary>Detached owner-state sheets shared by entry and gameplay refreshes.</summary>
internal sealed class CharacteristicsMessages(ICharacteristicsMessagesBackend backend,
    CharacteristicCatalogue catalogue, TimeProvider clock)
{
    internal static CharacteristicsMessages Live { get; } = new(new LiveBackend(), CharacteristicCatalogue.Default, TimeProvider.System);

    // Preserve the existing fresh-character weapon critical-hit policy; no weapon calculation is inferred.
    private const int CriticalHitWeapon = 5;

    internal Task SendAsync(GameSession session) => session.SendAsync(Snapshot(session));

    internal CharacterCharacteristicsEvent Snapshot(GameSession session)
    {
        using var context = SessionContext.Push(session);
        var state = session.State;
        int level = state.CharacterLevel;
        long characterId = state.CharacterId, experience = state.Experience, kamas = state.Kamas;
        var primary = new Dictionary<int, long>
        {
            [Stat.Strength] = state.StatStrength, [Stat.Vitality] = state.StatVitality, [Stat.Wisdom] = state.StatWisdom,
            [Stat.Chance] = state.StatChance, [Stat.Agility] = state.StatAgility, [Stat.Intelligence] = state.StatIntelligence,
        };
        var scrolled = new Dictionary<int, long>
        {
            [Stat.Strength] = state.ScrolledStrength, [Stat.Vitality] = state.ScrolledVitality, [Stat.Wisdom] = state.ScrolledWisdom,
            [Stat.Chance] = state.ScrolledChance, [Stat.Agility] = state.ScrolledAgility, [Stat.Intelligence] = state.ScrolledIntelligence,
        };
        var equipment = new Dictionary<int, long>(backend.EquipmentBonuses(session));
        var alterations = new Dictionary<int, long>(backend.AlterationBonuses(characterId));
        // The AP and MP reduction a game master gave himself (".stats retpa|retpm"), shown as the
        // temporary part it is: the fight adds it in FightHandler.RellenarLaFicha.
        if (state.StaffApReduction != 0)
            alterations[Stat.WithdrawActionPoints] = alterations.GetValueOrDefault(Stat.WithdrawActionPoints) + state.StaffApReduction;
        if (state.StaffMpReduction != 0)
            alterations[Stat.WithdrawMovementPoints] = alterations.GetValueOrDefault(Stat.WithdrawMovementPoints) + state.StaffMpReduction;
        var monster = backend.MutationSheet(session);
        if (monster != null)
        {
            equipment.Clear();
            alterations.Clear();
            foreach (int id in scrolled.Keys.ToArray()) scrolled[id] = 0;
            primary[Stat.Strength] = monster.Strength;
            primary[Stat.Vitality] = 0;
            primary[Stat.Wisdom] = monster.Wisdom;
            primary[Stat.Chance] = monster.Chance;
            primary[Stat.Agility] = monster.Agility;
            primary[Stat.Intelligence] = monster.Intelligence;
            primary[Stat.LifePoints] = monster.LifePoints;
            primary[Stat.ActionPoints] = monster.ActionPoints;
            primary[Stat.MovementPoints] = monster.MovementPoints;
        }

        long Total(int id) => checked(primary.GetValueOrDefault(id) + scrolled.GetValueOrDefault(id)
            + equipment.GetValueOrDefault(id) + alterations.GetValueOrDefault(id));
        // Calculation produces contributions, without knowing their protobuf container.
        var values = catalogue.Ids.Select(id =>
        {
            long value = primary.TryGetValue(id, out long allocated) ? allocated : ValueOf(id, level, session);
            if (monster == null && !primary.ContainsKey(id))
                value = checked(value + CharacteristicRules.GivenByCharacteristics(id, Total));
            long gear = equipment.GetValueOrDefault(id), temporary = alterations.GetValueOrDefault(id);
            int cap = id switch
            {
                Stat.ActionPoints => ServerSettings.Current.MaxActionPoints,
                Stat.MovementPoints => ServerSettings.Current.MaxMovementPoints,
                _ => 0,
            };
            long total = checked(value + gear + temporary);
            long context = monster == null && cap > 0 ? Math.Min(total, cap) - total : 0;
            return new Contributions(id, value, scrolled.GetValueOrDefault(id), gear, temporary, context);
        }).ToArray();

        var alignment = backend.Alignment(characterId);
        int grade = Math.Max(1, alignment.HonourGrade);
        var side = alignment.Side switch
        {
            0 => Alignment.Unknown, 1 => Alignment.Without, Alignments.Neutral => Alignment.Neutral,
            Alignments.Bonta => Alignment.Angel, Alignments.Brakmar => Alignment.Evil,
            _ => throw new ArgumentOutOfRangeException(nameof(alignment.Side), alignment.Side, "Unsupported alignment side."),
        };
        var information = new AlignmentInformation(backend.AlignmentQuestNumber(session), side,
            new(alignment.Honour, alignment.PvpOn ? AttackableStatus.PvpEnabledAttackable : AttackableStatus.NonAttackable,
                Honour.CeilingOf(grade), Honour.FloorOf(grade)), alignment.ShownGrade,
            Math.Max(1, CharacteristicRules.StatLevel(level)));
        return new(new(backend.NextLevelFloor(level), [], 0, CriticalHitWeapon, [], backend.LevelFloor(level),
            experience, information, kamas, values.Select(value => Characteristic(value, catalogue.KindOf(value.Id))).ToArray()));
    }

    internal long ValueOf(int id, int level, GameSession session)
    {
        if (id == Stat.LifePoints) return CharacteristicRules.BaseLifeForLevel(level);
        if (id == Stat.ActionPoints) return CharacteristicRules.InnateAp(level);
        if (id == Stat.EnergyPoints) return backend.Energy(session.CharacterId);
        if (id == Stat.HitPointLoss)
        {
            DateTime since = session.State.RegenerationStartedUtc;
            if (since == default) since = clock.GetUtcNow().UtcDateTime;
            return -(long)backend.MissingLife(session.CharacterId, since);
        }
        if (id == Stat.RemainingPoints) return session.State.CharacterRemainingPoints;
        // The base before strength's five a point (GivenByCharacteristics): the thousand, five a
        // character level and five a job level (ConnectionProtocol.Capacity).
        if (id == Stat.Pods) return ConnectionProtocol.PodsBeforeStrength(session.State);
        return catalogue.BaseValueOf(id);
    }

    private sealed record Contributions(int Id, long Base, long Additional, long Equipment, long Temporary, long Context);

    private static CharacterCharacteristic Characteristic(Contributions value, CharacteristicKind kind)
        => new(value.Id, kind switch
        {
            CharacteristicKind.Value => new CharacterCharacteristicCaseValue(new(value.Base)),
            // Historical AP/MP policy folds alterations into the equipment column.
            CharacteristicKind.Usable => new CharacterCharacteristicCaseUsable(new(checked((int)value.Base), checked((int)value.Context),
                checked((int)(value.Equipment + value.Temporary)), 0, 0, 0, 0)),
            _ => new CharacterCharacteristicCaseDetailed(new(checked((int)value.Base), checked((int)value.Additional),
                checked((int)value.Temporary), 0, checked((int)value.Equipment), 0)),
        });

    private sealed class LiveBackend : ICharacteristicsMessagesBackend
    {
        public long LevelFloor(int level) => ExperienceTable.LevelFloor(level);
        public long NextLevelFloor(int level) => ExperienceTable.NextLevelFloor(level);
        public IReadOnlyDictionary<int, long> EquipmentBonuses(GameSession session)
            => Equipment.BonusesOf(session.State.EquipmentItems.Values.Select(item => (item.Position, item)));
        public IReadOnlyDictionary<int, long> AlterationBonuses(long characterId) => Alterations.Bonuses(characterId);
        public MonsterGradeStats? MutationSheet(GameSession session)
        {
            var item = session.State.EquipmentItems.Values.FirstOrDefault(item => item.Position == Mutations.Slot);
            return item != null && Mutations.Of(item.Effects) is { } mutation ? Mutations.SheetOf(mutation) : null;
        }
        public Alignments.Alignment Alignment(long characterId) => Alignments.Of(characterId);
        public int AlignmentQuestNumber(GameSession session)
        {
            using var context = SessionContext.Push(session);
            return Alignments.Level();
        }
        public int Energy(long characterId) => Managers.Energy.Of(characterId);
        public int MissingLife(long characterId, DateTime atUtc) => RestingLife.MissingAt(characterId, atUtc);
    }
}

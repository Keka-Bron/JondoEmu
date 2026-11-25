using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers;

public static partial class FightHandler
{
    private static FightFighterShowEvent PlacementAppearance(FightInstance fight, Fighter fighter)
        => new(BoardFighter(fight, fighter, ongoing: false));

    private static ActorPositionInformation BoardFighter(FightInstance fight, Fighter fighter, bool ongoing, bool tellLife = false)
    {
        bool isDouble = ongoing && fighter.DoubleOf != 0;
        bool ai = fighter.IsMonster && !isDouble;
        var disposition = new EntityDisposition(fighter.CellId, MapActorMessages.Facing(FacingOf(fight, fighter)), null, 0);
        var spawn = new SpawnInformation(TeamIdentity(ongoing ? fighter.TeamId : fight.EquipoDe(fighter.Id)), fighter.IsAlive,
            new(disposition, null, fighter.Id));
        var (look, identity) = BoardIdentity(fighter, isDouble, isDouble ? fight.Buscar(fighter.DoubleOf) : null);
        if (ongoing) look = BoardLookWithAppearance(look, fighter.Buffs.AparienciaEn(fight.RoundNumber));
        long summoner = ongoing ? SummonerOf(fighter) : 0;
        var scrolls = !isDouble ? ScrollsOf(fighter) : null;
        // Participant synchronization sets the life baseline; a spectator snapshot does not.
        var sheet = ongoing ? FullSheetOf(fighter, conTraza: tellLife, told: tellLife) : PlacementSheetOf(fighter);
        var stats = sheet.Select(row => BoardStat(row.Characteristic, row.Base, row.Gear,
            scrolls?.GetValueOrDefault(row.Characteristic) ?? 0, ai)).ToArray();
        var actor = new FightFighterInformation(ai ? 0 : fighter.Id,
            new(summoner, FightInvisibilityState.Visible, summoner != 0, stats), spawn, identity,
            ongoing && ai ? fighter.Wave : 0);
        return new(disposition, new(look, new ActorInformationCaseFightActor(actor)), fighter.Id);
    }

    private static CharacterCharacteristic BoardStat(int id, long baseValue, long gear, long additional, bool monster)
    {
        if (monster) return new(id, new CharacterCharacteristicCaseValue(new(
            id == FightProtocol.TemporaryLifeMalus ? baseValue : baseValue + gear)));
        int value = checked((int)baseValue), bonus = checked((int)gear);
        return new(id, id switch
        {
            1 or 23 => new CharacterCharacteristicCaseUsable(new(value, 0, bonus, 0, 0, 0, 0)),
            FightProtocol.TemporaryLifeMalus => new CharacterCharacteristicCaseDetailed(new(value, 0, 0, 0, 0, bonus)),
            _ => new CharacterCharacteristicCaseDetailed(new(value, checked((int)additional), 0, 0, bonus, 0)),
        });
    }

    private static (EntityLook, FightFighterInformationCase) BoardIdentity(Fighter fighter, bool isDouble, Fighter? original)
    {
        if (fighter.IsMonster && !isDouble)
        {
            bool summon = fighter.EsInvocado && fighter.DoubleOf == 0;
            EntityLook look;
            if (string.IsNullOrEmpty(fighter.Look) || (summon && fighter.LookBoneId > 0))
            {
                int scale = summon ? Summons.De(fighter.MonsterId, fighter.GradeIndex)?.LookScale ?? 0 : 0;
                look = new([], 3, fighter.LookBoneId, scale > 0 ? [scale] : [], [], []);
            }
            else look = MonsterMessages.Look(fighter.Look, ServerSettings.Current.Hardcore && fighter.TaxCollectorFirstName == 0);
            if (fighter.IsCompanion) return (look, new FightFighterInformationCaseEntityFighter(new(fighter.DrivenBy,
                DatabaseManager.GetCharacterById(fighter.DrivenBy)?.Name ?? "", fighter.CompanionId, fighter.Level)));
            FightAIFighterInformationCase ai = fighter.TaxCollectorFirstName > 0
                ? new FightAIFighterInformationCaseTaxCollector(new(fighter.TaxCollectorFirstName, fighter.TaxCollectorLastName, fighter.MonsterId))
                : new FightAIFighterInformationCaseMonster(new(fighter.EsInvocado ? fighter.GradeIndex : fighter.GradeIndex + 1, fighter.MonsterId, fighter.Level));
            return (look, new FightFighterInformationCaseAiFighter(new(ai)));
        }

        var model = original ?? fighter;
        var character = model.IsBot ? null : DatabaseManager.GetCharacterById(isDouble ? fighter.DoubleOf : fighter.Id);
        var appearance = model.IsBot && KoliseoBots.PlacementLook(model) is { } botLook ? botLook
            : !string.IsNullOrEmpty(model.IncarnationLook) ? MonsterMessages.Look(model.IncarnationLook, false)
            : character != null ? CharacterLookBuilder.SemanticLook(character) : new EntityLook([], 0, fighter.LookBoneId, [], [], []);
        var info = new FightNamedFighterCharacterInformation(character?.Breed ?? model.Breed,
            (character?.Sex ?? model.Sex) == 1 ? Gender.Female : Gender.Male, fighter.Level,
            new(100, Alignment.Angel, null, 0, 200)); // Historical placement alignment snapshot.
        return (appearance, new FightFighterInformationCaseNamedFighter(new(-1, fighter.Name ?? "",
            new(CharacterStatusStatus.Available, ""), new FightNamedFighterInformationCharacterCaseCharacter(info))));
    }

    private static EntityLook BoardLookWithAppearance(EntityLook normal, int appearance)
    {
        if (appearance == 0) return normal;
        string full = Cosmetics.AppearanceLook(appearance);
        if (!string.IsNullOrEmpty(full)) return MonsterMessages.Look(full, false);
        int bones = Cosmetics.AppearanceBones(appearance);
        return bones > 0 ? normal with { Bones = bones } : normal;
    }
}

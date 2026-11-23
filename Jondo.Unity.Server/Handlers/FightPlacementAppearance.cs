using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers;

public static partial class FightHandler
{
    private static FightFighterShowEvent PlacementAppearance(FightInstance fight, Fighter fighter)
    {
        var disposition = new EntityDisposition(fighter.CellId, MapActorMessages.Facing(FacingOf(fight, fighter)), null, 0);
        var spawn = new SpawnInformation(TeamIdentity(fight.EquipoDe(fighter.Id)), fighter.IsAlive,
            new(disposition, null, fighter.Id));
        var (look, identity) = PlacementIdentity(fighter);
        var scrolls = ScrollsOf(fighter);
        var stats = PlacementSheetOf(fighter).Select(row => PlacementStat(row.Characteristic, row.Base, row.Gear,
            scrolls?.GetValueOrDefault(row.Characteristic) ?? 0, fighter.IsMonster)).ToArray();
        var actor = new FightFighterInformation(fighter.IsMonster ? 0 : fighter.Id,
            new(0, FightInvisibilityState.Visible, false, stats), spawn, identity, 0);
        return new(new(disposition, new(look, new ActorInformationCaseFightActor(actor)), fighter.Id));
    }

    private static CharacterCharacteristic PlacementStat(int id, long baseValue, long gear, long additional, bool monster)
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

    private static (EntityLook, FightFighterInformationCase) PlacementIdentity(Fighter fighter)
    {
        if (fighter.IsMonster)
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

        var character = fighter.IsBot ? null : DatabaseManager.GetCharacterById(fighter.Id);
        var appearance = fighter.IsBot && KoliseoBots.PlacementLook(fighter) is { } botLook ? botLook
            : !string.IsNullOrEmpty(fighter.IncarnationLook) ? MonsterMessages.Look(fighter.IncarnationLook, false)
            : character != null ? CharacterLookBuilder.SemanticLook(character) : new EntityLook([], 0, fighter.LookBoneId, [], [], []);
        var info = new FightNamedFighterCharacterInformation(character?.Breed ?? fighter.Breed,
            (character?.Sex ?? fighter.Sex) == 1 ? Gender.Female : Gender.Male, fighter.Level,
            new(100, Alignment.Angel, null, 0, 200)); // Historical placement alignment snapshot.
        return (appearance, new FightFighterInformationCaseNamedFighter(new(-1, fighter.Name ?? "",
            new(CharacterStatusStatus.Available, ""), new FightNamedFighterInformationCharacterCaseCharacter(info))));
    }
}

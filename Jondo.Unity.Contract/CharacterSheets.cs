namespace Jondo.Unity.ServerMods;

public readonly record struct CharacterSummary(long Id, string Name, int Level, int Breed, int Sex, string Look, string PortraitLook);

public readonly record struct CharacterVitals(int Life, int MaxLife, int Ap, int Mp, int Initiative, int Prospecting, int Wisdom);

public sealed record CharacterSheet(long Id, string? Name, int Breed, int Level, long Xp, long XpFloor,
    long XpNext, long Kamas, int Points, int Life, int MaxLife, int Ap, int Mp, int Initiative, int Prospecting,
    int Wisdom, int Strength, int Intelligence, int Chance, int Agility, int Vitality, int Range, int Power,
    int Critical, int Summons, int[] Resist, CharacterSheetSpell[] Spells, CharacterSheetItem[] Items);

public readonly record struct CharacterSheetSpell(int Id, int Grade);
public readonly record struct CharacterSheetItem(int Gid, int Quantity, int Position);

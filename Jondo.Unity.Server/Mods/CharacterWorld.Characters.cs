using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Server.Network.Connection;
using Jondo.Unity.ServerMods;

namespace Jondo.Unity.Server.Mods;

internal sealed partial class CharacterWorld
{
    public IReadOnlyList<CharacterSummary> CharactersOf(ICharacterSession master)
        => DatabaseManager.GetCharactersByAccountId(master.AccountId, ((GameSession)master).ServerId)
            .Select(c => new CharacterSummary(c.Id, c.Name ?? "", c.Level, c.Breed, c.Sex, LookOf(c), CharacterLookBuilder.Drawable(c))).ToArray();

    public int CharacterSlotsLeft(ICharacterSession master)
        => Math.Max(0, ConnectionService.MaxCharactersPerServer -
            CharacterCreationHandler.SlotsTaken(DatabaseManager.GetServers(),
                DatabaseManager.GetCharactersByAccountId(master.AccountId), ((GameSession)master).ServerId));

    public (long Id, string Error) CreateCharacter(ICharacterSession master, string name, int breed, int sex,
        int head, IReadOnlyList<long> colors)
        => CharacterCreationHandler.Make(master.AccountId, ((GameSession)master).ServerId, name, breed, sex, head, colors.ToList());

    public string SuggestName() => CharacterCreationHandler.SuggestName();
    public bool CharacterNameTaken(string name) => DatabaseManager.CharacterNameTaken(name);
    public bool AccountOwnsCharacter(long accountId, long characterId)
        => DatabaseManager.CharacterBelongsToAccount(characterId, accountId);

    public string PreviewLook(int breed, int sex, int head)
        => Convert.ToBase64String(CharacterLookBuilder.BuildLook(
            new DbCharacter { Breed = breed, Sex = sex, HeadId = head }, onFoot: true));

    private static string LookOf(DbCharacter character)
    {
        try { return Convert.ToBase64String(CharacterLookBuilder.BuildLook(character, onFoot: true)); }
        catch { return ""; }
    }

    public string? PortraitLook(long id) => DatabaseManager.GetCharacterById(id) is { } character
        ? CharacterLookBuilder.Drawable(character) : null;

    public void WarmPortraits(IEnumerable<string> looks) => CharacterPortraits.Warm(looks);
    public byte[]? RenderPortrait(string look) => CharacterPortraits.Of(look);

    public int CharacterLevel(ICharacterSession session) => ((GameSession)session).State.CharacterLevel;


    /// <summary>A connected character's points and what a group's tooltip shows of him.</summary>
    public CharacterVitals CharacterVitals(ICharacterSession character)
    {
        var session = (GameSession)character;
        var (life, maxLife) = LifeOf(session);
        try
        {
            var sheet = Handlers.FightHandler.SheetOf(session);
            using (SessionContext.Push(session))
            {
                int wisdom = session.State.TotalWisdom + session.Combat.GetEquipBonus(Stat.Wisdom);
                Equipment.Bonuses().TryGetValue(Stat.Prospecting, out long fromGear);
                return new(life, maxLife, sheet.MaxAP, sheet.MaxMP, sheet.Initiative,
                    100 + session.State.TotalChance / 10 + (int)fromGear, wisdom);
            }
        }
        catch
        {
            return new(life, maxLife, 0, 0, 0, 0, 0);
        }
    }

    /// <summary>A connected character's life: his fighter's in a fight, his resting life outside one.</summary>
    private static (int Life, int Max) LifeOf(GameSession session)
    {
        long id = session.State.CharacterId;
        if (Handlers.FightHandler.FightOf(id)?.Buscar(id) is { } fighter) return (Math.Max(0, fighter.CurrentHP), fighter.MaxHP);
        try
        {
            using (SessionContext.Push(session))
            {
                int max = session.Combat.GetPlayerMaxHp();
                return (RestingLife.LifeAt(id, max, DateTime.UtcNow), max);
            }
        }
        catch
        {
            return (0, 0);
        }
    }

    public CharacterSheet ReadCharacterSheet(ICharacterSession character)
    {
        var session = (GameSession)character;
        long id = session.CharacterId;
        var state = session.State;
        var sheet = Handlers.FightHandler.SheetOf(session);
        int wisdom, prospecting;
        List<SpellTable.KnownSpell> spells;
        using (SessionContext.Push(session))
        {
            wisdom = state.TotalWisdom + session.Combat.GetEquipBonus(Stat.Wisdom);
            Equipment.Bonuses().TryGetValue(Stat.Prospecting, out long fromGear);
            prospecting = 100 + state.TotalChance / 10 + (int)fromGear;
            spells = SpellTable.KnownFor(state.Breed, state.CharacterLevel, SpellChoices.Chosen,
                                         LearnedSpells.For(id, session.AccountId));
        }
        var (life, maxLife) = LifeOf(session);
        int level = state.CharacterLevel;
        return new(id, state.CharacterName, state.Breed, level, state.Experience, ExperienceTable.LevelFloor(level),
            ExperienceTable.LevelFloor(level + 1), state.Kamas, state.CharacterRemainingPoints, life, maxLife,
            sheet.MaxAP, sheet.MaxMP, sheet.Initiative, prospecting, wisdom, sheet.Strength, sheet.Intelligence,
            sheet.Chance, sheet.Agility, sheet.Vitality, sheet.Range, sheet.Power, sheet.CriticalBonus, sheet.Otra(26),
            [sheet.NeutralResPct, sheet.EarthResPct, sheet.FireResPct, sheet.WaterResPct, sheet.AirResPct],
            spells.Select(spell => new CharacterSheetSpell(spell.SpellId, spell.Grade)).ToArray(),
            state.GetInventoryCopy().Select(item => new CharacterSheetItem(item.ItemId, item.Quantity, item.Position)).ToArray());
    }

}

internal static class CharacterPortraits
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, byte[]?> _drawn = new();
    private static Jondo.Unity.Sprites.NpcSprites? _drawer;

    private static readonly HashSet<string> _warming = new();

    /// <summary>The looks drawn in the background, each once, so that asking for them later is instant.</summary>
    public static void Warm(IEnumerable<string> looks)
    {
        var todo = new List<string>();
        lock (_lock)
        {
            foreach (string look in looks)
                if (!string.IsNullOrEmpty(look) && !_drawn.ContainsKey(look) && _warming.Add(look)) todo.Add(look);
        }
        if (todo.Count == 0) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            foreach (string look in todo) Of(look);
        });
    }

    /// <summary>The PNG of a look, or null when it cannot be drawn here (no client, no display).</summary>
    public static byte[]? Of(string look)
    {
        if (string.IsNullOrEmpty(look)) return null;
        lock (_lock)
        {
            if (_drawn.TryGetValue(look, out var known)) return known;
            byte[]? png = null;
            try
            {
                // Four times the card's height: the drawer does not smooth, the client's shrinking does.
                _drawer ??= new Jondo.Unity.Sprites.NpcSprites { Height = 384 };
                var bitmap = _drawer.Of(look);
                if (bitmap != null)
                {
                    using var stream = new MemoryStream();
                    bitmap.Save(stream);
                    png = stream.ToArray();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Character portraits] The portrait could not be drawn: {ex.Message}");
            }
            _drawn[look] = png;
            return png;
        }
    }
}

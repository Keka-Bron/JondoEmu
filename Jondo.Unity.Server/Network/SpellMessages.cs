using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface ISpellMessagesBackend
{
    bool IsCatalogueLoaded { get; }
    bool IsAdministrator(long accountId);
    IReadOnlyCollection<int> Learned(long characterId);
    IReadOnlyCollection<int> AllSpecialSpells();
    IReadOnlyList<SpellTable.KnownSpell> Known(int breed, int level, IReadOnlyDictionary<int, int> chosen,
        IReadOnlyCollection<int> learned);
    IReadOnlyList<(int Spell, int Grade)> AdministratorSpells();
    IReadOnlyList<(int Spell, int Grade)> MutationSpells(GameSession session);
}

/// <summary>Detached spell-book snapshots shared by map entry, level-ups, scrolls and mutations.</summary>
internal sealed class SpellMessages(ISpellMessagesBackend backend)
{
    internal static SpellMessages Live { get; } = new(new LiveBackend());

    internal SpellsEvent Snapshot(GameSession session)
    {
        long accountId = session.AccountId, characterId = session.CharacterId;
        int breed = session.State.Breed, level = session.State.CharacterLevel;
        var chosen = new Dictionary<int, int>(session.State.ChosenSpells);
        bool administrator = backend.IsAdministrator(accountId);
        var learned = (administrator ? backend.AllSpecialSpells() : backend.Learned(characterId)).ToArray();

        // SpellLevel is the grade, not the character level or the SpellLevels row ID.
        // Spell ID zero is the real melee entry, and must remain first.
        var human = new List<SpellItem> { new(1, 0, SpellType.Unknown2) };
        foreach (var spell in backend.Known(breed, level, chosen, learned))
            human.Add(new(spell.Grade, spell.SpellId, spell.Special ? SpellType.Unknown2 : SpellType.Unknown1));
        if (administrator)
            foreach (var (spell, grade) in backend.AdministratorSpells())
                human.Add(new(grade, spell, SpellType.Unknown1));

        var mutant = backend.MutationSpells(session)
            .Select(spell => new SpellItem(spell.Grade, spell.Spell, SpellType.Unknown6)).ToArray();

        // The former writer's unmapped root bool is omitted by explicit policy.
        // SpellItem's unmapped bool keeps its former default false. See the populated-field inventory.
        return new(human.ToArray(), mutant);
    }

    internal async Task RefreshAsync(GameSession session, Func<Task> sendBar)
    {
        // A catalogue outage must not replace a working spell book during a live refresh.
        if (!backend.IsCatalogueLoaded) return;
        using var context = SessionContext.Push(session);
        await session.SendAsync(Snapshot(session));
        await sendBar();
    }

    private sealed class LiveBackend : ISpellMessagesBackend
    {
        public bool IsCatalogueLoaded => SpellTable.IsLoaded;
        public bool IsAdministrator(long accountId) => AdminSpells.Para(accountId);
        public IReadOnlyCollection<int> Learned(long characterId) => LearnedSpells.Of(characterId);
        public IReadOnlyCollection<int> AllSpecialSpells() => LearnedSpells.Everything;
        public IReadOnlyList<SpellTable.KnownSpell> Known(int breed, int level, IReadOnlyDictionary<int, int> chosen,
            IReadOnlyCollection<int> learned) => SpellTable.KnownFor(breed, level, chosen, learned);
        public IReadOnlyList<(int Spell, int Grade)> AdministratorSpells() => AdminSpells.Granted;
        public IReadOnlyList<(int Spell, int Grade)> MutationSpells(GameSession session)
        {
            var item = session.State.EquipmentItems.Values.FirstOrDefault(item => item.Position == Mutations.Slot);
            return item != null && Mutations.Of(item.Effects) is { } mutation ? Mutations.SpellsOf(mutation) : [];
        }
    }
}

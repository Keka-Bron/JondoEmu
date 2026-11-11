using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

internal interface IShortcutBarBackend
{
    IReadOnlyList<int>? MutationSpells(GameSession session);
    IReadOnlyList<(int Slot, int Spell)> SpellLayout(GameSession session);
    void RememberBar(GameSession session, IReadOnlyList<(int Slot, int Spell)> slots);
    IReadOnlyList<Shortcut> GeneralBar(long characterId);
}

/// <summary>Full semantic shortcut snapshots, shared by entry, level-ups and preset application.</summary>
internal sealed class ShortcutBarMessages(IShortcutBarBackend backend)
{
    internal static ShortcutBarMessages Live { get; } = new(new LiveBackend());

    internal ShortcutBarContentEvent SpellBar(GameSession session)
    {
        using var context = SessionContext.Push(session);
        if (backend.MutationSpells(session) is { } mutation)
            return new(mutation.Select((spell, slot) => Spell(slot, spell)).ToArray(), ShortcutBar.Spell);

        var layout = backend.SpellLayout(session).ToArray();
        backend.RememberBar(session, layout.Where(entry => entry.Spell != FightProtocol.HechizoCuerpoACuerpo).ToArray());
        return new(layout.Select(entry => Spell(entry.Slot, entry.Spell)).ToArray(), ShortcutBar.Spell);
    }

    internal ShortcutBarContentEvent GeneralBar(GameSession session)
        => new(backend.GeneralBar(session.CharacterId).ToArray(), ShortcutBar.General);

    internal (ShortcutBarContentEvent Spell, ShortcutBarContentEvent General) Snapshot(GameSession session)
        => (SpellBar(session), GeneralBar(session));

    internal Task SendSpellAsync(GameSession session) => session.SendAsync(SpellBar(session));

    private static Shortcut Spell(int slot, int spell)
        => new(slot, new ShortcutCaseShortcutSpell(new(spell)));

    private sealed class LiveBackend : IShortcutBarBackend
    {
        public IReadOnlyList<int>? MutationSpells(GameSession session)
            => Mutations.Current() is { } mutation ? Mutations.SpellsOf(mutation).Select(entry => entry.Spell).ToArray() : null;

        public IReadOnlyList<(int Slot, int Spell)> SpellLayout(GameSession session)
            => FightSpellLayout.Current(session.State.Breed, session.State.CharacterLevel, session.AccountId).Bar;

        public void RememberBar(GameSession session, IReadOnlyList<(int Slot, int Spell)> slots)
            => SpellChoices.RememberBar(slots.Select(entry => (entry.Slot, entry.Spell)));

        public IReadOnlyList<Shortcut> GeneralBar(long characterId)
            => ItemShortcuts.ReadForSnapshot(characterId).Select(entry =>
            {
                var shortcut = ShortcutStorageCodec.Decode(entry.Value);
                if (shortcut.SlotId != entry.Key)
                    throw new GameConversionException($"Stored shortcut slot {shortcut.SlotId} differs from row slot {entry.Key}.");
                return shortcut;
            }).ToArray();
    }
}

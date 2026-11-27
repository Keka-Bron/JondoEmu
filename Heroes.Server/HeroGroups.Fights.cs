using Jondo.Unity.ServerMods;

namespace Heroes.Server;

internal sealed partial class HeroGroups : IFightParticipantPolicy
{
    public IReadOnlyList<ControlledFightParticipant> SelectOpeningParticipants(long openerId, int places)
        => Of(openerId).Where(hero => !hero.Session.IsInFight).Take(places)
            .Select(hero => new ControlledFightParticipant(hero.Session, hero.ControllerId)).ToArray();

    public ControlledFightParticipant? FindParticipant(long characterId)
        => Find(characterId) is { } hero ? new(hero.Session, hero.ControllerId) : null;

    public bool ShouldPassTurn(long characterId) => Find(characterId)?.AutoPass == true;

    public IReadOnlyList<(int Slot, int Spell)> ArrangeControlledBar(int weaponSpell, bool withWeapon,
        IReadOnlyList<(int Slot, int Spell)> bar, IReadOnlyList<(int Spell, int Grade)> spells)
    {
        // The controller's client only shows the first page of another character's bar.
        var packed = new List<(int Slot, int Spell)>();
        if (withWeapon) packed.Add((0, weaponSpell));
        foreach (var (_, spell) in bar.Where(slot => slot.Spell != weaponSpell).OrderBy(slot => slot.Slot))
            packed.Add((packed.Count, spell));
        foreach (var (spell, _) in spells)
            if (!packed.Any(slot => slot.Spell == spell)) packed.Add((packed.Count, spell));
        return packed;
    }

    public IReadOnlyList<(long Giver, long Recipient)> SelectLootRecipients(IReadOnlyList<long> winners)
    {
        var groups = winners.GroupBy(id => Find(id)?.ControllerId ?? id);
        var recipients = new List<(long Giver, long Recipient)>();
        foreach (var group in groups)
        {
            long recipient = LootTo(group.Key);
            if (recipient == 0 || !winners.Contains(recipient)) continue;
            foreach (long winner in group)
                if (winner != recipient) recipients.Add((winner, recipient));
        }
        return recipients;
    }

    public Task RestoreFollowerPresenceAsync(ICharacterSession controller) => MasterMovedAsync(controller, 0);
}

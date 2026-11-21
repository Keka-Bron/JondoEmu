using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The single source of truth for the player's combat spell list and shortcuts. Both the
    /// placement message (jvn) and the live combat bar (jyy) must carry the same variant choices.
    /// </summary>
    public static class FightSpellLayout
    {
        public const int SlotCount = 40;

        public sealed class Layout
        {
            public List<(int Spell, int Grade)> Spells { get; } = new();
            public List<(int Slot, int Spell)> Bar { get; } = new();

            /// <summary>Whether the bar carries the weapon's entry: not for a monster one fights as.</summary>
            public bool WithWeapon { get; init; } = true;
        }

        /// <summary>
        /// The in-fight bar, with the administration spells if the account is one.
        /// </summary>
        /// <remarks>
        /// It goes apart from the list outside the fight: they are two different messages -- the hms of entering
        /// the world and the jyy of the fight's start -- and adding the spell only to the first leaves it visible
        /// on the walking panel and missing exactly where it is needed.
        /// </remarks>
        public static Layout Current(int breed, int level, long accountId)
        {
            // A bar never written is filled whole; one the player emptied stays empty. Asked before
            // PlaceNewlyUnlocked, which writes the bar's level.
            bool neverWritten = SpellChoices.Bar.Count == 0 && SpellChoices.BarLevel == 0;

            // What a level-up unlocked goes on the saved bar before it is drawn.
            SpellChoices.PlaceNewlyUnlocked(breed, level);

            var knownSpells = new List<SpellTable.KnownSpell>(
                SpellTable.KnownFor(breed, level, SpellChoices.Chosen,
                                    LearnedSpells.For(Network.SessionContext.State.CharacterId, accountId)));

            if (AdminSpells.Para(accountId))
            {
                foreach (var (spell, grade) in AdminSpells.Granted)
                    knownSpells.Add(new SpellTable.KnownSpell(spell, spell, grade));
            }

            return Build(knownSpells, SpellChoices.Bar, neverWritten);
        }

        /// <summary>
        /// The bar of the fight: <see cref="Current"/>, and the spell the net of the combat
        /// consumables' slot gives for it (RideCapture), on the first free slot.
        /// </summary>
        public static Layout ForFight(int breed, int level, long accountId)
        {
            // A piece of Belladona's chess plays its move and the blessing, nothing else (GuildRaidEfedra).
            if (GuildRaidEfedra.SpellsFor(Network.SessionContext.State.CharacterId) is { } chess)
                return Build(chess.Select(s => new SpellTable.KnownSpell(s.Spell, s.Spell, s.Grade)).ToList(), null);

            // A character fighting as a monster casts its spells and what the fight gave him, one
            // per slot, with no class spell and no weapon (FightIncarnation).
            long me = Network.SessionContext.State.CharacterId;
            var incarnated = Handlers.FightHandler.FightOf(me)?.Buscar(me);
            if (incarnated != null && incarnated.IncarnatedMonster != 0)
            {
                var asMonster = new Layout { WithWeapon = false };
                foreach (var (spell, grade) in Handlers.FightHandler.IncarnationCasts(incarnated))
                {
                    if (asMonster.Spells.Any(s => s.Spell == spell)) continue;
                    asMonster.Bar.Add((asMonster.Spells.Count, spell));
                    asMonster.Spells.Add((spell, grade));
                }
                return asMonster;
            }

            var layout = Current(breed, level, accountId);
            int net = RideCapture.SpellOfEquippedNet();
            if (net != 0 && !layout.Spells.Any(s => s.Spell == net)) AddOnFreeSlot(layout, net, 1);

            // And the worn legendary pet's power (LegendaryPets).
            var (power, powerGrade) = LegendaryPets.PowerOfWornPet();
            if (power != 0 && !layout.Spells.Any(s => s.Spell == power)) AddOnFreeSlot(layout, power, powerGrade);

            // And what the fight gave him (3405): the Princesa's Encantamiento Floral.
            var fighter = Handlers.FightHandler.FightOf(Network.SessionContext.State.CharacterId)?.Buscar(Network.SessionContext.State.CharacterId);
            foreach (var (spell, grade) in fighter?.GrantedSpells ?? new List<(int, int)>())
                if (!layout.Spells.Any(s => s.Spell == spell)) AddOnFreeSlot(layout, spell, grade);
            return layout;
        }

        private static void AddOnFreeSlot(Layout layout, int spell, int grade)
        {
            layout.Spells.Add((spell, grade));
            var taken = layout.Bar.Select(b => b.Slot).ToHashSet();
            int slot = Enumerable.Range(1, SlotCount - 1).FirstOrDefault(s => !taken.Contains(s));
            if (slot > 0) layout.Bar.Add((slot, spell));
        }

        /// <summary>
        /// Keeps valid saved shortcuts, drops spells lost after a level or variant change, and
        /// reserves one slot for close combat (spell 0).
        /// </summary>
        /// <remarks>
        /// Auto-filling every known spell into free slots only runs when the player has no saved
        /// bar yet. Once they have touched it (including removing a spell), putting those spells
        /// back would undo the remove on the next <c>itg</c> / fight bar rebuild.
        /// </remarks>
        /// <param name="seed">
        /// Whether to fill the free slots with the known spells. By default, when nothing is saved;
        /// <see cref="Current"/> says it only for a bar never written, so that one the player
        /// emptied -- the bar's «vaciar» (iuz), or slot by slot -- is not filled again.
        /// </param>
        public static Layout Build(IEnumerable<SpellTable.KnownSpell> known,
                                   IReadOnlyDictionary<int, int> savedBar, bool? seed = null)
        {
            var layout = new Layout();
            var available = new HashSet<int> { Network.FightProtocol.Weapon };
            foreach (var spell in known)
            {
                layout.Spells.Add((spell.SpellId, spell.Grade));
                available.Add(spell.SpellId);
            }

            var occupiedSlots = new HashSet<int>();
            var placedSpells = new HashSet<int>();
            foreach (var saved in savedBar)
            {
                if (saved.Key < 0 || saved.Key >= SlotCount || !available.Contains(saved.Value))
                    continue;

                layout.Bar.Add((saved.Key, saved.Value));
                occupiedSlots.Add(saved.Key);
                placedSpells.Add(saved.Value);
            }

            // First session only: seed free slots with known spells and place weapon attack.
            if (seed ?? (savedBar == null || savedBar.Count == 0))
            {
                if (!placedSpells.Contains(Network.FightProtocol.Weapon))
                {
                    int weaponSlot = 0;
                    while (occupiedSlots.Contains(weaponSlot)) weaponSlot++;
                    layout.Bar.Add((weaponSlot, Network.FightProtocol.Weapon));
                    occupiedSlots.Add(weaponSlot);
                    placedSpells.Add(Network.FightProtocol.Weapon);
                }

                int next = 1;
                foreach (var spell in layout.Spells)
                {
                    if (placedSpells.Contains(spell.Spell)) continue;
                    while (next < SlotCount && occupiedSlots.Contains(next)) next++;
                    if (next >= SlotCount) break;

                    layout.Bar.Add((next, spell.Spell));
                    occupiedSlots.Add(next);
                    placedSpells.Add(spell.Spell);
                    next++;
                }
            }

            layout.Bar.Sort((left, right) => left.Slot.CompareTo(right.Slot));
            return layout;
        }
    }
}

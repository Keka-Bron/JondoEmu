using System.Collections.Generic;

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
        }

        public static Layout Current(int breed, int level)
            => Current(breed, level, 0);

        /// <summary>
        /// La barra de dentro del combate, con los hechizos de administración si la cuenta lo es.
        /// </summary>
        /// <remarks>
        /// Va aparte de la lista de fuera del combate: son dos mensajes distintos —el hms de la
        /// entrada al mundo y el jyy del arranque de la pelea— y añadir el hechizo sólo al primero
        /// lo deja visible en el panel de paseo y ausente justo donde hace falta.
        /// </remarks>
        public static Layout Current(int breed, int level, long accountId)
        {
            var conocidos = new List<SpellTable.KnownSpell>(
                SpellTable.KnownFor(breed, level, SpellChoices.Chosen));

            if (AdminSpells.Para(accountId))
            {
                conocidos.Add(new SpellTable.KnownSpell(
                    AdminSpells.DoomDeMasas, AdminSpells.DoomDeMasas, AdminSpells.GradoDeDoom));
            }

            return Build(conocidos, SpellChoices.Bar);
        }

        /// <summary>
        /// Keeps valid saved shortcuts, drops spells lost after a level or variant change, fills
        /// newly opened spells into free slots, and reserves one slot for close combat (spell 0).
        /// </summary>
        public static Layout Build(IEnumerable<SpellTable.KnownSpell> known,
                                   IReadOnlyDictionary<int, int> savedBar)
        {
            var layout = new Layout();
            var available = new HashSet<int>();
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

            // Empezando por el UNO: el hueco cero es donde el cliente dibuja el arma, y en 37 de
            // las 51 barras de las capturas va vacío por eso mismo.
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

            // El cuerpo a cuerpo, en el primer hueco libre y SIEMPRE. Está en las 13 barras de
            // jugador de las capturas, incluida la del personaje del tutorial, que no lleva ni un
            // objeto: la casilla es la del puño y no depende de tener arma.
            //
            // Sin tope, que es como estaba antes de esta clase. Cortar en SlotCount lo dejaba
            // fuera cuando los cuarenta huecos estaban ocupados, y entonces el jugador entra al
            // combate sin poder pegar un puñetazo. La barra capturada llega hasta el 48, asi que
            // hay sitio de sobra por encima de los cuarenta que se rellenan solos.
            int weaponSlot = 0;
            while (occupiedSlots.Contains(weaponSlot)) weaponSlot++;
            layout.Bar.Add((weaponSlot, Network.FightProtocol.HechizoCuerpoACuerpo));

            layout.Bar.Sort((left, right) => left.Slot.CompareTo(right.Slot));
            return layout;
        }
    }
}

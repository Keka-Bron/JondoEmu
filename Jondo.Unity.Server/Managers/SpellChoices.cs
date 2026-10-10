using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What the player has chosen of his spells: which half of each pair he carries, and in which slot
    /// of the bar he put each one.
    ///
    /// It is the only thing about spells that does not come from the client's data. The pairs and the levels
    /// each grade asks for are the client's and are read from there (<see cref="SpellTable"/>); this is the
    /// player's, and that is why it lives in world.db and survives closing the game.
    /// </summary>
    public static class SpellChoices
    {
        /// <summary>pair -> chosen spell.</summary>
        private static Dictionary<int, int> ChosenStore => SessionContext.State.ChosenSpells;

        /// <summary>bar slot -> spell.</summary>
        private static Dictionary<int, int> BarStore => SessionContext.State.SpellBar;

        public static IReadOnlyDictionary<int, int> Chosen => ChosenStore;
        public static IReadOnlyDictionary<int, int> Bar => BarStore;

        /// <summary>The level whose spells the bar has been given; zero when it was never written.</summary>
        public static int BarLevel => SessionContext.State.SpellBarLevel;

        public static void LoadFrom(long characterId)
        {
            SessionContext.State.SpellChoicesCharacterId = characterId;
            SessionContext.State.SpellBarLevel = 0;
            ChosenStore.Clear();
            BarStore.Clear();

            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                var picks = connection.CreateCommand();
                picks.CommandText = "SELECT PairId, SpellId FROM CharacterSpellChoices WHERE CharacterId = $id;";
                picks.Parameters.AddWithValue("$id", characterId);
                using (var reader = picks.ExecuteReader())
                {
                    while (reader.Read()) ChosenStore[reader.GetInt32(0)] = reader.GetInt32(1);
                }

                var bar = connection.CreateCommand();
                bar.CommandText = "SELECT Slot, SpellId FROM CharacterSpellBar WHERE CharacterId = $id;";
                bar.Parameters.AddWithValue("$id", characterId);
                using (var reader = bar.ExecuteReader())
                {
                    while (reader.Read()) BarStore[reader.GetInt32(0)] = reader.GetInt32(1);
                }

                var level = connection.CreateCommand();
                level.CommandText = "SELECT Level FROM CharacterSpellBarLevel WHERE CharacterId = $id;";
                level.Parameters.AddWithValue("$id", characterId);
                if (level.ExecuteScalar() is long barLevel) SessionContext.State.SpellBarLevel = (int)barLevel;

                Console.WriteLine($"[SpellChoices] {ChosenStore.Count} chosen variants and " +
                                  $"{BarStore.Count} bar slots saved.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SpellChoices] Could not read the choices: {ex.Message}");
            }
        }

        /// <summary>
        /// Stores that of this pair the character carries this spell. It is checked that the spell
        /// really is one of the two: an id that is not would leave the character without that slot.
        /// </summary>
        public static bool Choose(int spellId)
        {
            var pair = SpellTable.PairOf(spellId);
            if (pair == null) return false;

            ChosenStore[pair.Id] = spellId;
            Write("INSERT INTO CharacterSpellChoices (CharacterId, PairId, SpellId) VALUES ($c, $p, $s) " +
                  "ON CONFLICT(CharacterId, PairId) DO UPDATE SET SpellId = $s;",
                  ("$p", pair.Id), ("$s", spellId));
            return true;
        }

        /// <summary>The bar slots that have this spell in them.</summary>
        public static List<int> SlotsHolding(int spellId)
        {
            var slots = new List<int>();
            foreach (var pair in BarStore)
            {
                if (pair.Value == spellId) slots.Add(pair.Key);
            }
            slots.Sort();
            return slots;
        }

        /// <summary>Remembers which bar slot a spell ended up in. Zero clears the slot.</summary>
        public static void PutInBar(int slot, int spellId)
        {
            if (spellId == 0)
            {
                BarStore.Remove(slot);
                Write("DELETE FROM CharacterSpellBar WHERE CharacterId = $c AND Slot = $t;",
                      ("$t", slot));
                return;
            }

            BarStore[slot] = spellId;
            Write("INSERT INTO CharacterSpellBar (CharacterId, Slot, SpellId) VALUES ($c, $t, $s) " +
                  "ON CONFLICT(CharacterId, Slot) DO UPDATE SET SpellId = $s;",
                  ("$t", slot), ("$s", spellId));
        }

        /// <summary>What spell sits in a bar slot, or zero when the slot is empty.</summary>
        public static int SpellInSlot(int slot)
            => BarStore.TryGetValue(slot, out int spell) ? spell : 0;

        /// <summary>Swaps the contents of two bar slots. Either side may be empty.</summary>
        public static void SwapBarSlots(int left, int right)
        {
            if (left == right) return;

            int spellLeft = SpellInSlot(left);
            int spellRight = SpellInSlot(right);
            PutInBar(left, spellRight);
            PutInBar(right, spellLeft);
        }

        /// <summary>
        /// Puts on the bar, each in the first free slot, the spells the character unlocks for the
        /// first time: those of the pairs his level opens above the level the bar was last given.
        /// </summary>
        /// <remarks>
        /// The bar is saved whole in the first session, and from then on what the player made of
        /// it rules: a spell he took off is not put back when the bar is drawn again. So a spell
        /// learned at a level-up has to be placed when it is learned, or it never reaches the bar
        /// -- a level 200 Selatrop was left with eight. A bar never written is filled whole by
        /// <see cref="FightSpellLayout.Build"/> and only gets its level here. A bar written before
        /// the level was kept gets, once, every spell it lacks: until then the bar put them all
        /// back each time it was drawn, so none had been taken off for good.
        /// </remarks>
        public static void PlaceNewlyUnlocked(int breed, int level)
        {
            // A level taken back down -- .level 1000 and then .level 200 -- takes the bar's level
            // with it, or the spells the next level-ups open would never be placed. The spells of
            // the levels left keep their slots: the bar is not touched.
            if (level < BarLevel && level > 0)
            {
                SaveBarLevel(level);
                return;
            }
            if (level <= BarLevel) return;

            if (BarStore.Count > 0)
            {
                var alreadyOpen = new HashSet<int>();
                if (BarLevel > 0)
                    foreach (var spell in SpellTable.KnownFor(breed, BarLevel, ChosenStore)) alreadyOpen.Add(spell.PairId);

                var onTheBar = new HashSet<int>(BarStore.Values);
                foreach (var spell in SpellTable.KnownFor(breed, level, ChosenStore))
                {
                    if (alreadyOpen.Contains(spell.PairId) || onTheBar.Contains(spell.SpellId)) continue;
                    int slot = FirstFreeSlot();
                    if (slot < 0) break;
                    PutInBar(slot, spell.SpellId);
                    onTheBar.Add(spell.SpellId);
                }
            }

            SaveBarLevel(level);
        }

        /// <summary>The level the bar has been given its spells up to, in the session and the base.</summary>
        private static void SaveBarLevel(int level)
        {
            SessionContext.State.SpellBarLevel = level;
            Write("INSERT INTO CharacterSpellBarLevel (CharacterId, Level) VALUES ($c, $l) " +
                  "ON CONFLICT(CharacterId) DO UPDATE SET Level = $l;",
                  ("$l", level));
        }

        /// <summary>The first slot of the bar with nothing in it, past slot zero, which is the weapon's.</summary>
        private static int FirstFreeSlot()
        {
            for (int slot = 1; slot < FightSpellLayout.SlotCount; slot++)
                if (!BarStore.ContainsKey(slot)) return slot;
            return -1;
        }

        /// <summary>
        /// Records the bar just sent to the client, so that the next session
        /// finds it the same. It is only written the first time: if the player has already touched it, what
        /// rules is his.
        /// </summary>
        public static void RememberBar(IEnumerable<(int Slot, int SpellId)> slots)
        {
            if (BarStore.Count > 0) return;
            foreach (var (slot, spellId) in slots) PutInBar(slot, spellId);
        }

        private static void Write(string sql, params (string Name, object Value)[] parameters)
        {
            long characterId = SessionContext.State.SpellChoicesCharacterId;
            if (characterId == 0) return;
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("$c", characterId);
                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SpellChoices] Could not save: {ex.Message}");
            }
        }
    }
}

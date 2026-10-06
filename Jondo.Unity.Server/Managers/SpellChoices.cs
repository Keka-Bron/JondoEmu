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
        /// <summary>pareja -> hechizo elegido.</summary>
        private static Dictionary<int, int> ChosenStore => SessionContext.State.ChosenSpells;

        /// <summary>bar slot -> spell.</summary>
        private static Dictionary<int, int> BarStore => SessionContext.State.SpellBar;

        public static IReadOnlyDictionary<int, int> Chosen => ChosenStore;
        public static IReadOnlyDictionary<int, int> Bar => BarStore;

        public static void LoadFrom(long characterId)
        {
            SessionContext.State.SpellChoicesCharacterId = characterId;
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

                Console.WriteLine($"[SpellChoices] {ChosenStore.Count} variantes elegidas y " +
                                  $"{BarStore.Count} huecos de barra guardados.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SpellChoices] No se pudieron leer las elecciones: {ex.Message}");
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

        /// <summary>Remembers which bar slot a spell ended up in.</summary>
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
                Console.WriteLine($"[SpellChoices] No se pudo guardar: {ex.Message}");
            }
        }
    }
}
